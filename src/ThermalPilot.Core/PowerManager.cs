using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ThermalPilot.Core;

/// <summary>
/// 通过 powercfg 控制 CPU 频率上限（等效 ThrottleStop 的 "Set Multiplier / 降频"，
/// 但走的是 Windows 官方电源管理接口，不需要内核驱动、不写 MSR，安全可回滚）。
///   PROCTHROTTLEMAX  = 最大处理器状态(%)，直接限制最高倍频；
///   PERFBOOSTMODE    = Turbo Boost 策略，关掉即彻底禁用超频。
/// </summary>
public sealed class PowerManager
{
    // 电源设置子组 / 项 GUID（Windows 内置固定值）
    private static readonly Guid SubProcessor = new("54533251-82be-4824-96c1-47b60b740d00");
    private static readonly Guid ProcthrottleMax = new("bc5038f7-23e0-4960-96da-33abaf5935ec");
    private static readonly Guid ProcthrottleMin = new("893dee8e-2bef-41e0-89c6-b55d0929964c");
    private static readonly Guid PerfBoostMode = new("be337238-0d82-4146-a960-4f3749d470c7");

    private static readonly Regex GuidRx = new("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.Compiled);
    private static readonly Regex HexRx = new("0x([0-9a-fA-F]{1,8})", RegexOptions.Compiled);

    public Guid ActiveScheme { get; private set; } = Guid.Empty;
    public int OriginalProcMax { get; private set; } = 100;
    public int OriginalBoostMode { get; private set; } = 2;
    public bool Available { get; private set; }

    /// <summary>读取当前电源方案与原始值，供退出时精确还原。</summary>
    public bool Initialize()
    {
        try
        {
            string output = Shell.Run("powercfg", "/getactivescheme");
            var m = GuidRx.Match(output);
            if (!m.Success)
            {
                Log.Warn("无法解析当前电源方案，降频功能停用。");
                Available = false;
                return false;
            }

            ActiveScheme = Guid.Parse(m.Value);
            OriginalProcMax = QuerySetting(ProcthrottleMax, 100);
            OriginalBoostMode = QuerySetting(PerfBoostMode, 2);

            // 防御：读到 0（表示"未配置/读取失败"）时绝不能当成 0% 去还原，否则会把 CPU 锁在最低频
            if (OriginalProcMax < 5 || OriginalProcMax > 100) OriginalProcMax = 100;
            if (OriginalBoostMode < 0 || OriginalBoostMode > 4) OriginalBoostMode = 2;
            Available = true;
            SaveOriginalSnapshot();
            Log.Info($"电源方案：{ActiveScheme}；原始最大处理器状态={OriginalProcMax}%，Turbo策略={OriginalBoostMode}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("powercfg 初始化失败", ex);
            Available = false;
            return false;
        }
    }

    /// <summary>设置最大处理器状态（5-100）。仅在有变化时执行，避免频繁拉起进程。</summary>
    public bool SetProcessorMax(int percent, bool alsoDc = true)
    {
        if (!Available) return false;
        percent = Math.Clamp(percent, 5, 100);
        bool ok = SetValue(ProcthrottleMax, percent, true);
        if (alsoDc) SetValue(ProcthrottleMax, percent, false);
        return ok;
    }

    /// <summary>Turbo Boost 策略：0=禁用 1=启用 2=激进 3=高效启用 4=高效激进。</summary>
    public bool SetBoostMode(int mode)
    {
        if (!Available) return false;
        mode = Math.Clamp(mode, 0, 4);
        bool ok = SetValue(PerfBoostMode, mode, true);
        SetValue(PerfBoostMode, mode, false);
        return ok;
    }

    public bool SetMinProcessorState(int percent)
    {
        if (!Available) return false;
        percent = Math.Clamp(percent, 0, 100);
        bool ok = SetValue(ProcthrottleMin, percent, true);
        SetValue(ProcthrottleMin, percent, false);
        return ok;
    }

    /// <summary>还原到启动时捕获的原始值。</summary>
    public void RestoreOriginal()
    {
        if (!Available) return;
        try
        {
            SetProcessorMax(OriginalProcMax);
            SetBoostMode(OriginalBoostMode);
            Log.Info($"已还原电源设置：最大处理器状态={OriginalProcMax}%，Turbo策略={OriginalBoostMode}");
        }
        catch (Exception ex)
        {
            Log.Error("还原电源设置失败", ex);
        }
    }

    private bool SetValue(Guid setting, int value, bool ac)
    {
        try
        {
            string sub = ac ? "/setacvalueindex" : "/setdcvalueindex";
            Shell.Run("powercfg",
                $"{sub} {ActiveScheme} {SubProcessor} {setting} {value}");
            // 必须重新激活方案才生效
            Shell.Run("powercfg", $"/setactive {ActiveScheme}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"powercfg 写入 {setting} 失败", ex);
            return false;
        }
    }

    /// <summary>把原始值快照落盘，供卸载程序（--restore）精确还原。</summary>
    private void SaveOriginalSnapshot()
    {
        try
        {
            Directory.CreateDirectory(Paths.DataDirectory);
            string file = Path.Combine(Paths.DataDirectory, "original-power.json");
            File.WriteAllText(file, JsonSerializer.Serialize(new
            {
                scheme = ActiveScheme.ToString(),
                procMax = OriginalProcMax,
                boost = OriginalBoostMode,
                savedAt = DateTime.Now.ToString("s")
            }));
        }
        catch (Exception ex)
        {
            Log.Warn("保存电源原始值快照失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 读取某个电源设置的"当前值"。
    ///
    /// 注意：powercfg /query 的输出里会出现多个 16 进制数（最小值 / 最大值 / 增量 / 当前值），
    /// 早期实现取"第一个 0x…"，结果拿到的是 **最小值**（甚至 0），退出还原时会把
    /// 最大处理器状态写成 5%，等于把 CPU 锁死在最低频。这里改为精确匹配当前值行。
    /// </summary>
    private int QuerySetting(Guid setting, int fallback)
    {
        try
        {
            string output = Shell.Run("powercfg", $"/query SCHEME_CURRENT {SubProcessor} {setting}");

            foreach (var pattern in new[]
                     {
                         @"(?:当前交流电源设置索引|Current AC Power Setting Index)\s*[:：]?\s*0x([0-9a-fA-F]+)",
                         @"(?:当前直流电源设置索引|Current DC Power Setting Index)\s*[:：]?\s*0x([0-9a-fA-F]+)"
                     })
            {
                var m = Regex.Match(output, pattern);
                if (m.Success &&
                    int.TryParse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v) &&
                    v >= 0 && v <= 100)
                    return v;
            }
        }
        catch { }
        return fallback;
    }
}
