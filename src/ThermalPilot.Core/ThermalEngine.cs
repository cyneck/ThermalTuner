using System.Diagnostics;

namespace ThermalPilot.Core;

/// <summary>
/// 温控决策引擎（ThrottleStop 式闭环，输出的是"风扇 PWM + CPU 频率上限"）：
///   采样 → 安全判定 → 策略求值（曲线模式 / 目标温度 PI 模式）→ 回差与斜率限速 → 下发 → 回读状态。
/// 三条铁律：① 退出/异常必须还原；② 温度失联必须还原；③ 超温直接满转 + 强制降频。
/// </summary>
public sealed class ThermalEngine : IDisposable
{
    private readonly HardwareService _hw = new();
    private readonly PowerManager _power = new();
    private readonly UndervoltService _uv = new();
    private readonly CpuControlService _cpu = new();
    private readonly AutoCpuController _auto = new();
    private readonly SampleLogger _logger = new();
    private readonly object _gate = new();
    private readonly EngineState _state = new();

    private System.Threading.Timer? _timer;
    private ThermalConfig _cfg;
    private DateTime _lastTick = DateTime.UtcNow;
    private DateTime _startedAt = DateTime.UtcNow;
    private double _fanOut = -1;
    /// <summary>上一次真正写风扇的时刻（用于 EC 写保护节流）。</summary>
    private DateTime _lastFanWrite = DateTime.MinValue;
    private int _procOut = -1;
    private int _boostOut = -1;
    private double _fanTempAnchor = double.NaN;
    private double _integral;              // PI 积分项（目标温度模式）
    private int _tickCount;
    private DateTime? _badTempSince;
    private bool _running;
    private bool _paused;
    private bool _disposed;

    // —— CPU 精细调控 / 智能自动 ——
    private DateTime _lastAuto = DateTime.MinValue;
    private double _lastAutoPl1 = double.NaN;
    /// <summary>温度历史（用于算 dT/dt）。温度量化到 1℃，必须跨十几秒求斜率，否则全是噪声。</summary>
    private readonly List<(DateTime at, double temp)> _tempHist = new();
    /// <summary>原始（未滤波）温度连续越线拍数 —— 超温保护的第二道确认，见 Tick 里的注释。</summary>
    private int _rawHotStreak;

    // —— 标定接管 ——
    // 标定期间不再「暂停引擎」：暂停之后风扇交回 BIOS，满载 77℃ 时风扇可能只有 22%，
    // 结果「温度」和「风扇」两列都对不上，数据不能用，机器还在高温空转。
    // 现在改成：风扇与 CPU 功率上限由标定计划决定，超温保护始终优先，其余决策暂停。
    private double _calibFanPct = -1;   // <0 = 不接管
    private int _calibCapPct = -1;      // <=0 或 >=100 = 不限制

    /// <summary>当前是否处于标定接管状态。</summary>
    public bool Calibrating => _calibFanPct >= 0 || _calibCapPct > 0;

    /// <summary>
    /// 标定阶段切换：风扇固定占空比 + CPU 功率上限百分比（相对本机设计功耗）。
    /// fanPct &lt; 0 表示该阶段不接管风扇（交回自动曲线）。
    /// </summary>
    public void SetCalibrationStage(double fanPct, int capPct)
    {
        lock (_gate)
        {
            _calibFanPct = fanPct >= 0 ? Math.Clamp(fanPct, 0, 100) : -1;
            _calibCapPct = capPct is > 0 and < 100 ? capPct : -1;
            // 阶段切换时清掉 PI 积分与回差锚点，避免上一阶段的尾巴带到下一阶段
            _integral = 0;
            _fanTempAnchor = double.NaN;
            _lastAutoPl1 = double.NaN;
            _state.ControlModeHint = "标定接管（风扇/功率由标定计划给定）";
        }
    }

    /// <summary>标定结束：恢复正常决策。</summary>
    public void EndCalibration()
    {
        lock (_gate)
        {
            _calibFanPct = -1;
            _calibCapPct = -1;
            _integral = 0;
            _fanTempAnchor = double.NaN;
            _lastAutoPl1 = double.NaN;
            _lastAuto = DateTime.MinValue;
            _state.ControlModeHint = ModeHint();
            _state.CpuHint = "";
        }
    }
    private int _lastAutoEpp = -1;
    private string _lastManualSig = "";
    private readonly PerfMetrics _perf;
    private DateTime _lastPerf = DateTime.MinValue;

    // —— 实测策略模型（查表式前馈 + 超温收紧）——
    private PolicyModel? _policy;
    private PolicyModel.LadderOption? _policyOpt;
    private double _policyTargetUsed = double.NaN;
    private DateTime _policyResolvedAt = DateTime.MinValue;
    private string _policyLoadedPath = "";

    public event Action<EngineState>? StateUpdated;

    public ThermalEngine(ThermalConfig cfg)
    {
        _cfg = cfg;
        _perf = new PerfMetrics(_cpu.Msr);
    }

    public HardwareService Hardware => _hw;
    public PowerManager Power => _power;
    public UndervoltService Undervolt => _uv;
    public CpuControlService Cpu => _cpu;
    public AutoCpuController AutoController => _auto;
    public PerfMetrics Perf => _perf;
    public bool IsRunning { get { lock (_gate) return _running; } }
    public bool IsPaused { get { lock (_gate) return _paused; } }

    public EngineState State
    {
        get { lock (_gate) return _state.Copy(); }
    }

    public ThermalConfig Config
    {
        get { lock (_gate) return _cfg.Clone(); }
    }

    public string SampleLogPath => _logger.FilePath;

    /// <summary>当前生效的策略模型（未启用时为 null），供 UI 显示与手动查验。</summary>
    public PolicyModel? Policy { get { lock (_gate) return _policy; } }

    /// <summary>策略模型是否真的在驱动输出。</summary>
    public bool PolicyActive
    {
        get { lock (_gate) return _policy != null && _cfg.UsePolicyModel && _policy.ControlLadder.Count > 0; }
    }

    /// <summary>启动引擎。返回启动过程中需要提示给用户的告警（可为空）。</summary>
    public List<string> Start()
    {
        var notes = new List<string>();
        lock (_gate)
        {
            if (_running) return notes;

            bool hwOk = _hw.Open();
            if (!hwOk) notes.Add("硬件监控未启动：" + _hw.OpenError);

            // 平台能力：Ring0 句柄这时才可用，所以必须在 _hw.Open() 之后探测。
            // 判定结果决定后面每一项 MSR 写入做不做 —— AMD / 无 HWP / 无睿频 / BIOS 锁死
            // 都在这里被识别，避免"写了一堆必然失败的寄存器还显示失败红字"。
            var caps = CpuCapabilities.Refresh(_cpu.Msr);
            // 平台功率要在能力探测之后：型号表查表用的是 CPUID 拿到的型号名。
            PlatformPower.Apply(_cfg, _cpu.Msr, force: true);
            var unsupported = caps.UnsupportedInUse(_cfg);
            // 先克隆再清洗：_cfg 可能与 UI 的 Committed 是同一个对象，
            // 就地改等于在参数中心背后偷偷改值（没有 Diff、没有待应用提示）。
            var cleaned = _cfg.Clone();
            if (caps.Sanitize(cleaned))
            {
                _cfg = cleaned;
                if (unsupported.Count > 0)
                    notes.Add("本机平台不支持这些参数，已忽略：" + string.Join("、", unsupported)
                              + "（原因见「高级调参 → CPU 能力」）");
            }
            if (caps.AllMsrUnsupported)
                notes.Add("本机无法使用 MSR 精细调控（" + caps.CpuLine + "），已自动改用 Windows 电源计划限频。");

            if (!_power.Initialize())
                notes.Add("电源方案读取失败，降频功能不可用（风扇控制仍可工作）。");

            if (_cfg.EnableUndervolt)
            {
                if (_uv.Apply(_cfg.UndervoltMv))
                    notes.Add($"降压已生效：{_cfg.UndervoltMv} mV");
                else
                    notes.Add("降压未生效：" + _uv.LastMessage);
            }

            // CPU 精细调控 / 智能自动
            _auto.Reset();
            _lastAuto = DateTime.MinValue;
            _lastAutoPl1 = double.NaN;
            _lastAutoEpp = -1;
            _lastManualSig = "";
            _perf.Reset();
            _lastPerf = DateTime.MinValue;
            _state.CpuHint = "";
            _state.AutoCpuActive = false;
            if (_cfg.AutoCpuMode || _cfg.EnableCpuTuning)
            {
                if (!_cpu.EnsureAvailable())
                    notes.Add("CPU 精细调控不可用：" + _cpu.LastError);
                else if (!_cfg.AutoCpuMode && _cfg.EnableCpuTuning)
                    ApplyManualCpuLocked(notes);
            }

            // EC 保护：最近 10 分钟若有 EC 超时，说明这条总线已经在挣扎，
            // 把写间隔抬到 ≥5 秒并明确告诉用户（很多人是通过"检测不到电池"才发现这事的）。
            int ecRecent = EcHealth.RecentTimeouts(out _, 10);
            if (ecRecent > 0)
            {
                _cfg.FanWriteMinIntervalMs = Math.Max(_cfg.FanWriteMinIntervalMs, 5000);
                notes.Add($"检测到最近 10 分钟有 {ecRecent} 次 EC 超时（ACPI 13），"
                          + $"风扇写 EC 间隔已提高到 {_cfg.FanWriteMinIntervalMs} ms；"
                          + "电池电量也由 EC 上报，建议关机拔电长按电源键 30 秒做一次 EC 复位。");
            }
            _hw.Nbfc.MinWriteIntervalMs = _cfg.FanWriteMinIntervalMs;

            if (_cfg.EnableFanControl && !_hw.FanControlSupported)
                notes.Add("未检测到可软件调速的风扇通道，风扇交还 BIOS，散热由降频联动承担（笔记本常见）。");

            if (!_hw.RpmSupported)
                notes.Add("未检测到风扇转速(RPM)通道，仪表盘只显示输出百分比。");

            if (_cfg.EnableLogging) OpenLogger();

            LoadPolicyLocked(notes);

            _running = true;
            _paused = false;
            _state.Status = "运行中";
            _state.ControlModeHint = ModeHint();
            _lastTick = DateTime.UtcNow;
            _startedAt = DateTime.UtcNow;
            _badTempSince = null;
            _fanOut = -1;
            _procOut = -1;
            _boostOut = -1;
            _integral = 0;
            _fanTempAnchor = double.NaN;
            _tickCount = 0;
            _rawHotStreak = 0;
            _hw.ResetTempFilters();

            int interval = Math.Clamp(_cfg.SampleIntervalMs, 500, 5000);
            _timer = new Timer(_ => Tick(), null, dueTime: 300, period: interval);
            Log.Info($"引擎已启动（采样 {interval} ms，模式：{_state.ControlModeHint}）。");
        }
        return notes;
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (!_running || _paused) return;
            _paused = true;
            _state.Paused = true;
            _state.Status = "已暂停（未还原设置）";
            Log.Info("引擎已暂停。");
        }
        RaiseState();
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (!_running || !_paused) return;
            _paused = false;
            _state.Paused = false;
            _state.Status = "运行中";
            Log.Info("引擎已恢复。");
        }
        RaiseState();
    }

    public void ApplyConfig(ThermalConfig cfg)
    {
        // 防御性清洗：本机没有的旋钮不要带进硬件路径（UI 已禁用，但配置可能来自旧文件/别的机器）。
        // 同样先克隆：入参常常就是 UI 那个 Committed 实例。
        cfg = cfg.Clone();
        CpuCapabilities.Current.Sanitize(cfg);
        _hw.Nbfc.MinWriteIntervalMs = cfg.FanWriteMinIntervalMs;
        // 设计功耗/GPU 功率可能被用户改过 —— 不重探（省掉 nvidia-smi 进程），但覆盖层要重算，
        // 否则 PL1 的允许区间还停在旧值上。
        PlatformPower.Apply(cfg, null);
        bool loggingChanged;
        bool policyChanged;
        bool autoChanged;
        lock (_gate)
        {
            loggingChanged = cfg.EnableLogging != _cfg.EnableLogging;
            policyChanged = cfg.UsePolicyModel != _cfg.UsePolicyModel ||
                            !string.Equals(cfg.PolicyModelPath, _cfg.PolicyModelPath, StringComparison.OrdinalIgnoreCase);
            autoChanged = cfg.AutoCpuMode != _cfg.AutoCpuMode ||
                          Math.Abs(cfg.AutoTargetC - _cfg.AutoTargetC) > 0.01 ||
                          Math.Abs(cfg.AmbientC - _cfg.AmbientC) > 0.01 ||
                          !string.Equals(cfg.AutoCpuProfile, _cfg.AutoCpuProfile, StringComparison.OrdinalIgnoreCase) ||
                          cfg.AutoCpuDryRun != _cfg.AutoCpuDryRun;
            _cfg = cfg;
            _state.ControlModeHint = ModeHint();
            if (policyChanged) LoadPolicyLocked(null);
            if (autoChanged)
            {
                _auto.Reset();
                _lastAuto = DateTime.MinValue;
                _lastAutoPl1 = double.NaN;
                _lastAutoEpp = -1;
                _lastManualSig = "";
                _perf.Reset();
                _lastPerf = DateTime.MinValue;
                _state.CpuHint = "";
            }
        }

        Log.Info($"配置已更新：模式 {_state.ControlModeHint}，风扇控制={cfg.EnableFanControl}，降频={cfg.EnableFrequencyControl}，CPU自动={cfg.AutoCpuMode}");

        if (loggingChanged)
        {
            if (cfg.EnableLogging) OpenLogger();
            else _logger.Close();
        }

        if (cfg.EnableUndervolt)
            _uv.Apply(cfg.UndervoltMv);
        else if (_uv.IsApplied)
            _uv.Restore();

        // CPU 旋钮：自动模式由 Tick 循环驱动；手动模式在此一次性下发；关闭则还原
        if (cfg.AutoCpuMode)
        {
            if (!_cpu.EnsureAvailable()) _state.CpuHint = "智能自动不可用：" + _cpu.LastError;
        }
        else if (cfg.EnableCpuTuning)
        {
            ApplyManualCpuLocked(null);
        }
        else if (_cpu.IsActive)
        {
            _cpu.Restore();
            _state.CpuHint = "";
        }
    }

    private string ModeHint()
    {
        string mode = _cfg.UsePolicyModel && _policy != null
            ? "实测策略（查表）"
            : (_cfg.TargetTempMode ? "目标温度 PI" : "曲线");
        return _cfg.AutoCpuMode ? mode + " + CPU 智能自动" : mode;
    }

    /// <summary>把 UI 设置的手动 CPU 旋钮（PL1/PL2/τ/EPP/睿频）下发到 MSR。</summary>
    private void ApplyManualCpuLocked(List<string>? notes)
    {
        if (!_cpu.EnsureAvailable())
        {
            _state.CpuHint = "CPU 精细调控不可用：" + _cpu.LastError;
            notes?.Add(_state.CpuHint);
            return;
        }

        string sig = $"{_cfg.CpuPl1W}|{_cfg.CpuPl2W}|{_cfg.CpuTauS}|{_cfg.CpuEpp}|{_cfg.CpuTurboRatio}";
        if (sig == _lastManualSig) return;
        _lastManualSig = sig;

        var msgs = new List<string>();
        if (_cfg.CpuPl1W > 0)
        {
            bool ok = _cpu.ApplyPowerLimits(
                _cfg.CpuPl1W,
                _cfg.CpuPl2W > 0 ? _cfg.CpuPl2W : null,
                _cfg.CpuTauS > 0 ? _cfg.CpuTauS : null);
            msgs.Add((ok ? "✔ " : "✘ ") + _cpu.LastMessage);
        }
        if (_cfg.CpuEpp >= 0)
        {
            bool ok = _cpu.ApplyEpp(_cfg.CpuEpp);
            msgs.Add((ok ? "✔ " : "✘ ") + _cpu.LastMessage);
        }
        if (_cfg.CpuTurboRatio > 0)
        {
            bool ok = _cpu.ApplyTurboRatio(_cfg.CpuTurboRatio);
            msgs.Add((ok ? "✔ " : "✘ ") + _cpu.LastMessage);
        }

        _state.CpuHint = msgs.Count == 0 ? "未设置任何 CPU 旋钮" : string.Join(" | ", msgs);
        foreach (var m in msgs) notes?.Add("CPU 调控：" + m);
    }

    /// <summary>重新加载策略模型（UI 换了文件后可主动调一次）。</summary>
    public List<string> ReloadPolicy()
    {
        var notes = new List<string>();
        lock (_gate) LoadPolicyLocked(notes);
        return notes;
    }

    private void LoadPolicyLocked(List<string>? notes)
    {
        _policy = null;
        _policyOpt = null;
        _policyLoadedPath = "";
        _policyTargetUsed = double.NaN;

        if (!_cfg.UsePolicyModel) return;

        string path = _cfg.PolicyModelPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            notes?.Add("未找到策略模型文件：" + (string.IsNullOrWhiteSpace(path) ? "(未选择)" : path));
            Log.Warn("策略模型未启用：文件不存在 " + path);
            return;
        }

        var m = PolicyModel.Load(path);
        if (m == null || m.ControlLadder.Count == 0)
        {
            notes?.Add("策略模型无效（无控制阶梯）：" + Path.GetFileName(path));
            return;
        }

        _policy = m;
        _policyLoadedPath = path;
        notes?.Add($"已载入实测策略：{Path.GetFileName(path)}（{m.Mode}，{m.ControlLadder.Count} 档，τ={m.Dynamic.TauS:F0}s）");
    }

    private PolicyModel.Strategy StrategyOrDefault()
    {
        return (_cfg.PolicyStrategy ?? "").ToLowerInvariant() switch
        {
            "quiet" or "静音" => PolicyModel.Strategy.Quiet,
            "power" or "性能" => PolicyModel.Strategy.Power,
            _ => PolicyModel.Strategy.Balanced
        };
    }

    /// <summary>
    /// 查表定档：以「目标温度」为输入取 cap/fan（前馈），再用当前温度做收紧/放宽的修正。
    /// 不每 tick 重算：温度漂移超过回差、或 20 秒、或策略变化才重新查表（省 powercfg 写入）。
    /// </summary>
    private void UpdatePolicyTarget(double temp)
    {
        if (!PolicyActive || _policy == null) { _policyOpt = null; _state.PolicyHint = ""; return; }

        double eff = Math.Clamp(_cfg.TargetTempC, 55, 100);
        // 先用阶梯覆盖目标温度；实测温度明显超出时收紧一档（-3℃），远低时放宽半档
        if (temp > eff + 4) eff -= 3;
        else if (temp > eff + 8) eff -= 6;
        else if (temp < eff - 8) eff += 2;

        bool stale = DateTime.UtcNow - _policyResolvedAt > TimeSpan.FromSeconds(20);
        bool drifted = double.IsNaN(_policyTargetUsed) || Math.Abs(eff - _policyTargetUsed) >= 1.0;
        if (!stale && !drifted) return;

        var opt = _policy.Resolve(eff, StrategyOrDefault());
        _policyResolvedAt = DateTime.UtcNow;
        _policyTargetUsed = eff;

        if (opt == null) { _policyOpt = null; _state.PolicyHint = "策略表无可用档（目标温度过低）"; return; }

        _policyOpt = opt;
        _state.PolicyHint = $"策略档 ≤{eff:F0}℃ → cap {opt.Cap}% / 风扇 " +
                            (opt.Fan.HasValue ? $"{opt.Fan.Value:F0}%" : "不干预") +
                            $"（预测 {opt.PredT:F1}℃，噪声 {opt.SplDeltaDb:+0.0;-0.0;0} dB）";
    }

    private void OpenLogger()
    {
        try
        {
            _logger.Open();
            Log.Info("采样日志已开启：" + _logger.FilePath);
        }
        catch (Exception ex)
        {
            Log.Error("打开采样日志失败", ex);
        }
    }

    private void Tick()
    {
        if (!Monitor.TryEnter(_gate)) return;
        try
        {
            if (_disposed || !_running) return;

            var snap = _hw.Read();
            _state.Snapshot = snap;
            _tickCount++;

            if (snap.CpuTempValid) _badTempSince = null;
            else _badTempSince ??= DateTime.UtcNow;

            if (_badTempSince is { } since && (DateTime.UtcNow - since).TotalSeconds > 15)
            {
                if (!_state.Faulted)
                {
                    _state.Faulted = true;
                    _state.Status = "温度读数丢失，已还原为系统默认";
                    Log.Warn("超过 15 秒未取得 CPU 温度，触发失效保护，还原全部设置。");
                    RestoreInternal();
                }
                return;
            }

            if (_cfg.EnableLogging && _logger.IsOpen && _tickCount % Math.Max(1, _cfg.LogEveryN) == 0)
                _logger.WriteRuntime(snap, _state.AppliedFanPct, _state.AppliedProcMax, _startedAt);

            if (_paused) return;

            // temp 已经是抗毛刺后的值（snap.Hotspot 走 CpuTempShown/GpuTempShown）。
            // 但中值窗口有 1~2 s 的固有延迟，且历史数据里出现过「47/68/47/85」交替毛刺
            // 会把窗口带偏，所以超温保护再加一道原始值确认：
            //   ① 原始值连续 2 拍越线 → 真超温（真升温不可能只维持一个采样点）；
            //   ② 原始值单拍就 ≥ 保护线+3℃（离 TJmax 只剩个位数）→ 不等确认，立即保护。
            double temp = snap.Hotspot;
            bool rawOverHot = snap.CpuTempValid && snap.CpuTemp >= _cfg.CriticalTempC;
            _rawHotStreak = rawOverHot ? _rawHotStreak + 1 : 0;
            bool critical = snap.CpuTempValid && temp >= _cfg.CriticalTempC &&
                            (_rawHotStreak >= 2 || snap.CpuTemp >= _cfg.CriticalTempC + 3);

            if (critical)
            {
                _policyOpt = null;
                if (PolicyActive) _state.PolicyHint = "超温保护中，已忽略策略档";
            }
            else UpdatePolicyTarget(temp);

            UpdateFan(snap, temp, critical);
            UpdateProcessor(temp, critical);
            UpdateCpu(snap, temp, critical);

            _state.Status = critical ? "超温保护中"
                          : Calibrating ? "标定中（接管风扇与功率）"
                          : "运行中";
        }
        catch (Exception ex)
        {
            Log.Error("控制循环异常", ex);
            _state.Status = "异常：" + ex.Message;
        }
        finally
        {
            Monitor.Exit(_gate);
            RaiseState();
        }
    }

    private void UpdateFan(SensorSnapshot snap, double temp, bool critical)
    {
        double target;
        if (critical)
        {
            target = 100;
            _fanTempAnchor = double.NaN;
            _integral = 0;
        }
        else if (_calibFanPct >= 0)
        {
            // 标定接管：固定占空比。优先于 EnableFanControl —— 标定本身就是「控制风扇」的实验，
            // 不接管就没有「风扇 → 温度」的数据，拟合出来的曲线是空的。
            target = _calibFanPct;
        }
        else if (!_cfg.EnableFanControl || !snap.CpuTempValid)
        {
            target = -1; // 不接管，交还 BIOS
        }
        else if (_policyOpt?.Fan is { } policyFan)
        {
            // 实测策略档（阶梯表）给出的风扇值，仍受上下限约束
            target = Math.Clamp(policyFan, _cfg.FanMinPct, _cfg.FanMaxPct);
        }
        else if (_cfg.TargetTempMode)
        {
            // 高级模式：PI 控制器把温度压到 TargetTempC
            double err = temp - _cfg.TargetTempC;
            var now = DateTime.UtcNow;
            double dt = Math.Clamp((now - _lastTick).TotalSeconds, 0.2, 5.0);
            _integral = Math.Clamp(_integral + err * dt, -300, 300);
            double raw = _cfg.FanMinPct + _cfg.Kp * err + _cfg.Ki * _integral;
            target = Math.Clamp(raw, 0, _cfg.FanMaxPct);
        }
        else
        {
            if (double.IsNaN(_fanTempAnchor)) _fanTempAnchor = temp;
            else if (Math.Abs(temp - _fanTempAnchor) >= _cfg.FanHysteresisC) _fanTempAnchor = temp;

            double raw = _cfg.FanCurve.Evaluate(_fanTempAnchor, _cfg.FanMinPct);
            if (raw <= 0.5 && _fanTempAnchor < _cfg.FanStopBelowC) target = 0;
            else target = Math.Max(raw, _cfg.FanMinPct);
            target = Math.Clamp(target, 0, _cfg.FanMaxPct);
        }

        _state.TargetFanPct = target < 0 ? 0 : Math.Round(target, 0);

        if (target < 0)
        {
            if (_fanOut >= 0)
            {
                _hw.RestoreFanDefault();
                _fanOut = -1;
                _state.AppliedFanPct = -1;
                _state.FanEffective = false;
                _state.FanNote = "未接管（已交还 BIOS/NBFC 自动）";
                _state.LastAction = "风扇已交还 BIOS";
            }
            return;
        }

        var now2 = DateTime.UtcNow;
        double dt2 = Math.Clamp((now2 - _lastTick).TotalSeconds, 0.2, 5.0);

        double next;
        if (_fanOut < 0) next = target;
        else if (target > _fanOut) next = Math.Min(target, _fanOut + _cfg.FanRampUpPctPerSec * dt2);
        else next = Math.Max(target, _fanOut - _cfg.FanRampDownPctPerSec * dt2);

        // —— EC 写保护 ——
        // 笔记本风扇走 NBFC 直写 EC（每次 set 一个新进程 + 一次 EC 事务）。旧版只要目标值
        // 变化 ≥1% 就写，1 秒采样下等于 1 Hz 地压 EC，实测把 ACPI EC 驱动打到超时
        // （事件日志 ACPI 13），而电池电量/插电状态也由同一个 EC 上报 —— 于是 Windows
        // 会短暂"检测不到电池"。这里两道闸：时间闸 + 死区闸。
        int minMs = Math.Clamp(_cfg.FanWriteMinIntervalMs, 1000, 10000);
        bool emergency = target >= 99.5 || temp >= _cfg.CriticalTempC;
        double deadband = emergency ? 1.0 : 2.0;

        if (!emergency && (now2 - _lastFanWrite).TotalMilliseconds < minMs)
            return;                                  // 还没到下一次写 EC 的时间

        if (_fanOut < 0 || Math.Abs(next - _fanOut) >= deadband)
        {
            int ok = _hw.SetFanPercent(next, force: emergency);

            // NBFC 后端还要再确认一层：返回 1 也可能是"被节流跳过"（时间闸/值未变），
            // 那种情况下值并没有真的写进 EC。只有 LastWriteApplied=true 才算落地。
            bool nbfcPath = _hw.Nbfc.Installed && !_hw.FanControlSupported;
            bool applied = ok > 0 && (!nbfcPath || _hw.Nbfc.LastWriteApplied);

            if (applied)
            {
                _fanOut = next;
                _lastFanWrite = now2;
                _state.AppliedFanPct = Math.Round(next, 0);
                _state.FanEffective = true;
                _state.FanNote = $"已接管 {next:F0}%";
                _state.LastAction = $"风扇 → {next:F0}%（{ok} 通道）";
            }
            else if (ok > 0)
            {
                // 被 EC 写保护节流跳过：不是失败，值还没写下去，等时间闸打开后再试。
                // 这里**不动 _fanOut**，下一拍才会重新评估并重试；也不要假装生效。
                string skip = _hw.Nbfc.LastSkipReason;
                _state.FanNote = $"待写入 {next:F0}%"
                               + (string.IsNullOrEmpty(skip) ? "" : $"（{skip}）");
            }
            else
            {
                // 写入没生效（NBFC 未接管 / 服务不可用 / 值被忽略）。
                // 三件事：① 不更新 AppliedFanPct —— 界面不能显示一个没生效的数；
                //        ② 把 _fanOut 复位成 -1，下一拍重新尝试（时间闸与退避仍然有效，不会刷 EC）；
                //        ③ 记下原因给界面，别让用户靠"听声音"才发现没接管。
                _fanOut = -1;
                _state.AppliedFanPct = -1;
                _state.FanEffective = false;
                string why = _hw.Nbfc.LastError;
                if (string.IsNullOrEmpty(why)) why = _hw.Nbfc.LastSkipReason;
                _state.FanNote = $"未生效（目标 {next:F0}%）："
                               + (string.IsNullOrEmpty(why) ? "写入未被接受" : why);
                _state.LastAction = _state.FanNote;
            }
        }
    }

    private void UpdateProcessor(double temp, bool critical)
    {
        if (!_cfg.EnableFrequencyControl || !_power.Available)
        {
            if (_procOut >= 0)
            {
                _power.RestoreOriginal();
                _procOut = -1;
                _boostOut = -1;
                _state.AppliedProcMax = -1;
                _state.BoostMode = -1;
            }
            return;
        }

        int target;
        bool fromPolicy = _policyOpt is { } opt;
        // 实测策略档：cap 直接来自阶梯表（已是"能达标的最低限频"）
        target = fromPolicy ? _policyOpt!.Cap : (int)Math.Round(_cfg.FreqCurve.Evaluate(temp, 100));

        if (critical) target = Math.Min(target, _cfg.CriticalProcMax);
        target = Math.Clamp(target, 5, 100);
        _state.TargetProcMax = target;

        int boost = target >= 100 ? _power.OriginalBoostMode : target >= 70 ? 1 : 0;

        bool procChanged = _procOut < 0 || Math.Abs(target - _procOut) >= 5;
        bool boostChanged = _boostOut < 0 || boost != _boostOut;

        if (procChanged)
        {
            _power.SetProcessorMax(target);
            _procOut = target;
            _state.AppliedProcMax = target;
            _state.LastAction = critical
                ? $"超温降频 → CPU 上限 {target}%"
                : fromPolicy ? $"策略限频 → CPU 上限 {target}%" : $"CPU 频率上限 → {target}%";
        }
        if (boostChanged)
        {
            _power.SetBoostMode(boost);
            _boostOut = boost;
            _state.BoostMode = boost;
        }
        _lastTick = DateTime.UtcNow;
    }

    /// <summary>
    /// CPU 精细调控：自动模式每 3 秒闭环求解一次 PL1/PL2/τ/EPP；手动模式在配置变更时下发。
    /// 超温时无条件把 PL1 压到最低（压过一切策略）。
    /// </summary>
    private void UpdateCpu(SensorSnapshot snap, double temp, bool critical)
    {
        if (!_cfg.AutoCpuMode && !_cfg.EnableCpuTuning && _calibCapPct <= 0) return;
        if (!snap.CpuTempValid && !critical) return;

        if (critical)
        {
            if (_cpu.EnsureAvailable() && !_cfg.AutoCpuDryRun)
            {
                // 连 PL2 一起压住，避免短时 boost 又把温度顶上去
                _cpu.ApplyPowerLimits(10, 12, null);
                _cpu.ApplyEpp(255);
            }
            if (_cfg.AutoCpuMode) _auto.ForcePl1(10);
            _state.AutoCpuActive = _cfg.AutoCpuMode;
            _state.CpuHint = "超温保护：PL1/PL2 已压到最低、EPP 偏节能";
            return;
        }

        // —— 标定接管：按标定计划的百分比折算 PL1 ——
        // 为什么不用 powercfg：本机实测 PROCTHROTTLEMAX 写进去读得回、CPU 不认（见 docs/MODEL.md 基线），
        // 于是「满载·上限 50%」实际跑的是满血 38W/3.24GHz，测出来的点全部作废。
        if (_calibCapPct > 0)
        {
            double pl1 = Math.Max(5, ParamConstraints.PlatformPl1MaxW * _calibCapPct / 100.0);
            double pl2 = Math.Min(ParamConstraints.PlatformPl2MaxW, pl1 * 1.25);
            if (_cpu.EnsureAvailable() && !_cfg.AutoCpuDryRun &&
                (double.IsNaN(_lastAutoPl1) || Math.Abs(pl1 - _lastAutoPl1) >= 0.5))
            {
                bool ok = _cpu.ApplyPowerLimits(pl1, pl2, null);
                if (ok) _lastAutoPl1 = pl1;
            }
            _state.AutoCpuActive = false;
            _state.CpuHint = $"标定接管：PL1 ≈ {pl1:F0}W（{_calibCapPct}% × 设计功耗 "
                             + $"{ParamConstraints.PlatformPl1MaxW:F0}W）";
            return;
        }

        if (_cfg.AutoCpuMode)
        {
            if (!_cpu.EnsureAvailable())
            {
                _state.AutoCpuActive = false;
                _state.CpuHint = "智能自动不可用：" + _cpu.LastError;
                return;
            }

            if (_lastAuto != DateTime.MinValue &&
                (DateTime.UtcNow - _lastAuto).TotalSeconds < 3) return;

            double power = snap.CpuPower > 1 ? snap.CpuPower : 0;
            double amb = Math.Clamp(_cfg.AmbientC, -10, 45);
            double ceiling = Math.Clamp(_cfg.AutoTargetC, 50, AutoCpuController.SafetyCeilingC);

            // 交付性能指标（有效频率/包功率/谁在限制变快）：约 3s 采一次，
            // 绑核读 APERF/MPERF 有轻微成本，没必要每 50ms 一次。
            PerfSample? ps = _perf.Last;
            if ((DateTime.UtcNow - _lastPerf).TotalSeconds >= 2.5)
            {
                double pl1Now = double.IsNaN(_lastAutoPl1) ? (_auto.CurrentPl1 is var v && !double.IsNaN(v) ? v : 0) : _lastAutoPl1;
                ps = _perf.Sample(pl1Now, temp, ceiling, snap.CpuLoad);
                _lastPerf = DateTime.UtcNow;
            }

            // dT/dt 用 ~12 秒窗口：温度是 1℃ 量化 + 3 秒一拍，逐拍差分会得到 ±0.3~1℃/s 的假噪声，
            // 把"准稳态"判据全部误杀（先前的实现就是这样导致 R 永远估不出来）。
            double dTdt = double.NaN;
            var nowR = DateTime.UtcNow;
            _tempHist.Add((nowR, temp));
            while (_tempHist.Count > 2 && (nowR - _tempHist[0].at).TotalSeconds > 12)
                _tempHist.RemoveAt(0);
            if (_tempHist.Count >= 2)
            {
                double span = (nowR - _tempHist[0].at).TotalSeconds;
                if (span >= 6) dTdt = (temp - _tempHist[0].temp) / span;
            }

            // maxW 用本机 CPU 设计功耗（PlatformPower：MSR 0x614 / NVML / 型号表 / 用户手填），
            // 不要写死 80：超过设计功率只会更热，不会更快。
            var d = _auto.Update(temp, power, ceiling, amb, _cfg.AutoCpuProfile,
                                 10, ParamConstraints.PlatformPl1MaxW, ps, snap.CpuLoad, dTdt);
            _lastAuto = DateTime.UtcNow;

            bool pl1Changed = double.IsNaN(_lastAutoPl1) || Math.Abs(d.Pl1W - _lastAutoPl1) >= 0.5;
            bool eppChanged = _lastAutoEpp != d.Epp;
            bool okAny = true;
            if (!_cfg.AutoCpuDryRun)
            {
                if (pl1Changed)
                {
                    bool ok = _cpu.ApplyPowerLimits(d.Pl1W, d.Pl2W, d.TauS);
                    okAny &= ok;
                    if (ok) _lastAutoPl1 = d.Pl1W;
                }
                if (eppChanged)
                {
                    bool ok = _cpu.ApplyEpp(d.Epp);
                    okAny &= ok;
                    if (ok) _lastAutoEpp = d.Epp;
                }
            }

            _state.AutoCpuActive = true;
            string rTxt = double.IsNaN(d.REff)
                ? "热阻 R 待估（" + (_auto.Resistance.LastReject is { Length: > 0 } rj ? rj : "需要一段满载") + "）"
                : $"R {d.REff:F2} K/W{(d.RConfident ? "" : "（参考值）")}";
            _state.CpuHint = $"自动 {ProfileLabel(_cfg.AutoCpuProfile)}：PL1 {d.Pl1W:F0}W · PL2 {d.Pl2W:F0}W · τ {d.TauS:F0}s · EPP {d.Epp}"
                             + $"　|　{rTxt}"
                             + $"　|　预测 {d.PredictedC:F0}℃（当前负载 {snap.CpuLoad:F0}% 下抬到 PL1 的增量）"
                             + "\n依据：" + d.Reason
                             + (_cfg.AutoCpuDryRun ? "（预览，未写入）"
                                : (okAny ? "" : "（写入失败：" + _cpu.LastMessage + "）"));
            _state.PerfText = BuildPerfText(ps);
            _state.BindLimitText = ps?.LimitText ?? "";
            return;
        }

        // 手动模式：已在 Start / ApplyConfig 下发，这里只维护提示 + 交付指标，
        // 让用户"改完立刻看得见"有效频率/限制原因的变化（手工微调的反馈闭环）。
        _state.CpuHint = _cpu.IsActive ? "手动旋钮已生效" : ("手动旋钮未生效：" + _cpu.LastMessage);
        PerfSample? pm = _perf.Last;
        if ((DateTime.UtcNow - _lastPerf).TotalSeconds >= 2.5)
        {
            double pl1Now = _cpu.ReadCurrent()?.Pl1W ?? 0;
            // 这里必须用「策略上限 AutoTargetC」，不是 PID 的 setpoint TargetTempC：
            // 旧代码拿 80℃ 当"墙"，于是 79℃ 就被判成"温度墙"，而策略其实允许到 85℃。
            pm = _perf.Sample(pl1Now, temp, Math.Clamp(_cfg.AutoTargetC, 50, AutoCpuController.SafetyCeilingC), snap.CpuLoad);
            _lastPerf = DateTime.UtcNow;
        }
        _state.PerfText = BuildPerfText(pm);
        _state.BindLimitText = pm?.LimitText ?? "";
    }

    private static string ProfileLabel(string profile) => (profile ?? "").ToLowerInvariant() switch
    {
        "silent" => "静音",
        "performance" => "性能",
        _ => "平衡"
    };

    /// <summary>把交付性能采样拼成一行给 UI 看：有效频率 · 包功率 · 谁在限制。</summary>
    private static string BuildPerfText(PerfSample? ps)
    {
        if (ps == null || !ps.Valid) return "交付指标：暂无（需要管理员权限读 MSR）";
        string ev = string.IsNullOrEmpty(ps.Evidence) ? "" : "（" + ps.Evidence + "）";
        return $"交付指标：{ps.Detail}  →  {ps.LimitText}{ev}";
    }

    /// <summary>停止并还原一切（必须在退出、崩溃、卸载前调用）。</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _running = false;
            _paused = false;
            _timer?.Dispose();
            _timer = null;
            RestoreInternal();
            _state.Status = "已停止";
        }
        RaiseState();
    }

    private void RestoreInternal()
    {
        try { _hw.RestoreFanDefault(); } catch { }
        try { _power.RestoreOriginal(); } catch { }
        try { _uv.Restore(); } catch { }
        try { _cpu.Restore(); } catch { }
        _auto.Reset();
        _lastAuto = DateTime.MinValue;
        _lastAutoPl1 = double.NaN;
        _lastAutoEpp = -1;
        _lastManualSig = "";
        _perf.Reset();
        _lastPerf = DateTime.MinValue;
        _fanOut = -1;
        _procOut = -1;
        _boostOut = -1;
        _integral = 0;
        _state.AppliedFanPct = -1;
        _state.AppliedProcMax = -1;
        _state.BoostMode = -1;
        _state.CpuHint = "";
        _state.AutoCpuActive = false;
    }

    private void RaiseState()
    {
        try { StateUpdated?.Invoke(State); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Stop(); } catch { }
        try { _logger.Dispose(); } catch { }
        try { _hw.Dispose(); } catch { }
    }
}
