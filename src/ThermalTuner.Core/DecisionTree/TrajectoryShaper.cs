namespace ThermalTuner.Core.DecisionTree;

/// <summary>
/// 轨迹整形参数（Trajectory Shaping）。
/// 整形不改变「要不要动」的判断，只约束「什么时候允许动、一次动多少」。
/// 与决策树的死区、WriteScheduler 的迟滞/冷却属于不同层次，互不替代。
/// </summary>
public sealed record TrajectorySettings
{
    /// <summary>温度带宽度（°C）。以 5°C 为一段，避免逐度抖动被反复跨越。</summary>
    public double BandC { get; init; } = 5.0;

    /// <summary>带宽内的滞回（°C）：升带要多走 +Hyst，降带要多退 −Hyst，防止边界来回翻。</summary>
    public double BandHysteresisC { get; init; } = 1.0;

    /// <summary>一阶低通系数（0~1]：越大越跟手，越小越平滑。1 Hz 采样下 0.35 ≈ 2 s 时间常数。</summary>
    public double EmaAlpha { get; init; } = 0.35;

    /// <summary>中值窗口长度（奇数，≥3）：用于剔除单拍突刺，先中值再低通。</summary>
    public int MedianWindow { get; init; } = 3;

    /// <summary>单拍温度偏离窗口中值多少度即判为突刺（仅用于标记与解释，不阻断滤波）。</summary>
    public double SpikeRejectC { get; init; } = 3.0;

    /// <summary>
    /// 门控放行时的单次最大升幅（W）。
    /// 升档以温度带跨越为门：每跨一个带最多放行一次、最多升这么多。
    /// 设计依据：调度器升档冷却默认 20s + 熔断 4 次/分，逐拍斜坡在物理上不可能落地；
    /// 带跨越（≥5 °C 温度变化）天然把写入间隔压到 ≥20 s，与冷却/熔断兼容。
    /// </summary>
    public double MaxRiseWPerGate { get; init; } = 6.0;

    public static TrajectorySettings Default => new();

    /// <summary>更平滑：牺牲响应换轨迹平顺（默认推荐给静音/平衡）。</summary>
    public static TrajectorySettings Smooth => new()
    {
        BandC = 5.0,
        BandHysteresisC = 1.0,
        EmaAlpha = 0.25,
        MedianWindow = 3,
        SpikeRejectC = 3.0,
        MaxRiseWPerGate = 4.0
    };

    /// <summary>更跟手：仅工程/短时压测场景显式选择（门控几乎不拦截）。</summary>
    public static TrajectorySettings Agile => new()
    {
        BandC = 2.0,
        BandHysteresisC = 0.5,
        EmaAlpha = 0.6,
        MedianWindow = 3,
        SpikeRejectC = 6.0,
        MaxRiseWPerGate = 15.0
    };

    /// <summary>参数自检；返回 null 表示合法。</summary>
    public string? Validate()
    {
        if (!(BandC > 0)) return $"BandC 必须 > 0（实际 {BandC}）";
        if (BandHysteresisC < 0) return $"BandHysteresisC 不得为负（实际 {BandHysteresisC}）";
        if (BandHysteresisC >= BandC) return $"BandHysteresisC 必须小于 BandC（{BandHysteresisC} >= {BandC}）";
        if (!(EmaAlpha > 0) || EmaAlpha > 1) return $"EmaAlpha 必须落在 (0,1]（实际 {EmaAlpha}）";
        if (MedianWindow < 3 || MedianWindow % 2 == 0) return $"MedianWindow 必须为 ≥3 的奇数（实际 {MedianWindow}）";
        if (SpikeRejectC <= 0) return $"SpikeRejectC 必须 > 0（实际 {SpikeRejectC}）";
        if (!(MaxRiseWPerGate > 0)) return $"MaxRiseWPerGate 必须 > 0（实际 {MaxRiseWPerGate}）";
        return null;
    }
}

/// <summary>整形一拍的结果：可直接交给 WriteScheduler 评估。</summary>
public readonly record struct ShapedTarget(
    double Pl1W,
    double FilteredTempC,
    int TempBandIndex,
    bool SpikeRejected,
    bool Bypassed,
    bool GateHeld,
    string Reason);

/// <summary>
/// 轨迹整形层：决策树输出 → 整形 → WriteScheduler。
///
/// 链路顺序固定为「中值去突刺 → 一阶低通 → 温度带滞回 → 升档带门控」。
/// 设计约束（不得违反）：
/// 1. <b>降档与安全直通</b>：降档不受门控；<c>thermalBypass</c>（高温保护 / 硬闸）时跳过一切整形，
///    目标功率原样返回。绝不因为「要平滑」而延迟降压。
/// 2. <b>升档以温度带为门</b>：同带内不放行升档；跨带后每次放行最多升 MaxRiseWPerGate。
///    这样请求对调度器是分段常数（迟滞天然满足），写入频率被带跨越率天然限制（≥20 s/次）。
/// 3. <b>先滤波后分带</b>：单拍尖峰不能触发档位跳变。
/// 4. <b>无硬件依赖</b>：纯状态机，全部可离线单元测试。
/// </summary>
public sealed class TrajectoryShaper
{
    public TrajectorySettings Settings { get; }

    public TrajectoryShaper(TrajectorySettings? settings = null)
    {
        Settings = settings ?? TrajectorySettings.Default;
        if (Settings.Validate() is { } error)
            throw new ArgumentException("轨迹整形参数非法：" + error, nameof(settings));
        _window = new double[Settings.MedianWindow];
    }

    private readonly double[] _window;
    private int _windowCount;
    private int _windowNext;

    private double _filtered = double.NaN;
    private int _bandIndex = int.MinValue;
    private int _lastRiseBand = int.MinValue;

    /// <summary>滤波后的温度（°C）；首拍前为 NaN。</summary>
    public double FilteredTempC => _filtered;

    /// <summary>当前温度带索引（5 °C 一段时，62 °C → 12）。</summary>
    public int TempBandIndex => _bandIndex;

    /// <summary>模式切换 / 档位切换 / 用户手动重置后调用，清空滤波与门控历史。</summary>
    public void Reset()
    {
        Array.Clear(_window);
        _windowCount = 0;
        _windowNext = 0;
        _filtered = double.NaN;
        _bandIndex = int.MinValue;
        _lastRiseBand = int.MinValue;
    }

    /// <summary>
    /// 推进一拍。
    /// </summary>
    /// <param name="rawTempC">原始包温度（°C）；NaN 表示本拍无有效读数，滤波值保持不变。</param>
    /// <param name="targetPl1W">决策树的期望 PL1（W）。</param>
    /// <param name="currentPl1W">当前实际生效的 PL1（W）。</param>
    /// <param name="thermalBypass">高温保护 / 硬闸：为真时完全跳过整形。</param>
    /// <param name="riseUnrestricted">
    /// 热余量充足（温度远低于天花板）时放开升档门控：仍按 MaxRiseWPerGate 限幅形成斜坡，
    /// 但不再等温度跨带。否则轻载回升会被「同带内不放行」永久拦住，形成只降不升的棘轮。
    /// 高温工况必须传 false。
    /// </param>
    public ShapedTarget Step(
        double rawTempC,
        double targetPl1W,
        double currentPl1W,
        bool thermalBypass = false,
        bool riseUnrestricted = false)
    {
        bool spike = UpdateTemperature(rawTempC, out double filtered);
        int band = UpdateBand(filtered);

        if (thermalBypass)
        {
            double direct = Quantize(targetPl1W);
            return new ShapedTarget(direct, filtered, band, spike, true, false,
                $"[安全直通] 高温保护/硬闸：跳过整形，PL1 直接下发 {direct:F0} W");
        }

        if (double.IsNaN(targetPl1W) || targetPl1W <= 0)
        {
            double hold = double.IsNaN(currentPl1W)
                ? 0
                : Math.Round(currentPl1W, 0, MidpointRounding.AwayFromZero);
            return new ShapedTarget(hold, filtered, band, spike, false, false,
                "[无目标] 本拍未收到有效目标 PL1，保持当前值");
        }

        double delta = targetPl1W - currentPl1W;
        double shaped;
        bool gateHeld = false;
        string why;

        if (delta <= 0)
        {
            // 降档 / 持平：不受门控。安全方向永远直通（调度器对降档同样立即放行）。
            shaped = Quantize(targetPl1W);
            why = delta < 0
                ? $"[降档直通] 目标 {shaped:F0} W 低于当前 {currentPl1W:F0} W，不受温度带门控"
                : $"[已收敛] 目标与当前一致（{shaped:F0} W），无动作";
        }
        else
        {
            // 升档：热余量充足时只限幅不拦；否则同带内不放行，跨带（或会话首次升档）后放行一次。
            bool gateOpen = riseUnrestricted
                            || _lastRiseBand == int.MinValue
                            || band > _lastRiseBand;
            if (!gateOpen)
            {
                shaped = Quantize(currentPl1W);
                gateHeld = true;
                why = $"[带门控] 温度仍在第 {band} 带（{Settings.BandC:F0} °C/带 ±{Settings.BandHysteresisC:F0} °C 滞回），" +
                      $"升档暂缓，保持 {shaped:F0} W";
            }
            else
            {
                shaped = Quantize(Math.Min(targetPl1W, currentPl1W + Settings.MaxRiseWPerGate));
                if (!riseUnrestricted) _lastRiseBand = band;
                why = shaped < targetPl1W - 1e-9
                    ? (riseUnrestricted
                        ? $"[热余量充足] 温度远低于天花板，免带门控，本次最多升 {Settings.MaxRiseWPerGate:F0} W，下发 {shaped:F0} W"
                        : $"[带门控放行] 温度进入第 {band} 带，本次最多升 {Settings.MaxRiseWPerGate:F0} W，下发 {shaped:F0} W")
                    : (riseUnrestricted
                        ? $"[热余量充足] 免带门控，目标 {shaped:F0} W 在限幅内"
                        : $"[带门控放行] 温度进入第 {band} 带，目标 {shaped:F0} W 在限幅内");
            }
        }

        if (spike) why += "（本拍温度偏离窗口中值，已按突刺处理）";
        return new ShapedTarget(shaped, filtered, band, spike, false, gateHeld, why);
    }

    /// <summary>中值去突刺 + 一阶低通。返回 true 表示本拍原始值被判为突刺。</summary>
    private bool UpdateTemperature(double raw, out double filtered)
    {
        if (double.IsNaN(raw))
        {
            filtered = _filtered;
            return false;
        }

        _window[_windowNext] = raw;
        _windowNext = (_windowNext + 1) % _window.Length;
        if (_windowCount < _window.Length) _windowCount++;

        double median = Median();
        bool spike = _windowCount >= 3 && Math.Abs(raw - median) > Settings.SpikeRejectC;

        if (double.IsNaN(_filtered))
            _filtered = median;
        else
            _filtered += Settings.EmaAlpha * (median - _filtered);

        filtered = _filtered;
        return spike;
    }

    private int UpdateBand(double filtered)
    {
        if (double.IsNaN(filtered)) return _bandIndex == int.MinValue ? 0 : _bandIndex;

        int band = BandOf(filtered);
        if (_bandIndex == int.MinValue) { _bandIndex = band; return _bandIndex; }

        double h = Settings.BandHysteresisC;
        if (band > _bandIndex && filtered >= (_bandIndex + 1) * Settings.BandC + h) _bandIndex++;
        else if (band < _bandIndex && filtered <= _bandIndex * Settings.BandC - h) _bandIndex--;

        return _bandIndex;
    }

    private double Median()
    {
        var copy = new double[_windowCount];
        Array.Copy(_window, copy, _windowCount);
        Array.Sort(copy);
        int mid = copy.Length / 2;
        return copy.Length % 2 == 1 ? copy[mid] : (copy[mid - 1] + copy[mid]) / 2.0;
    }

    private int BandOf(double tempC) => (int)Math.Floor(tempC / Settings.BandC);

    /// <summary>1 W 台阶量化（向上取整），与 WriteScheduler 台阶一致。</summary>
    private static double Quantize(double watts) => Math.Ceiling(watts);
}
