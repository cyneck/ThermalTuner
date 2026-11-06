using ThermalTuner.Core.Taxonomy;

namespace ThermalTuner.Core.Tests;

/// <summary>
/// EC 风扇百分比换算回归测试。
/// 本机实测锚点：ec-probe read 149 → 原始值 80，配置 Min=255 / Max=58，
/// 换算结果必须等于 NBFC 服务报告的 88.83%。
/// </summary>
internal sealed class FanPercentTests
{
    public void RunAllTests()
    {
        Check.Suite("FanPercent");
        RealMachineAnchor();
        DirectionAndClamp();
        InvalidInput();
        ParseRawOutput();
    }

    private static void RealMachineAnchor()
    {
        double pct = FanPercentCodec.FromRaw(80, 255, 58);
        Check.Near(88.83, pct, 0.01, "本机 EC 原值 80 应换算为 88.83%（与 NBFC status 一致）");
        Check.Near(88.83, FanPercentCodec.FromRaw(
                FanPercentCodec.ParseFirstNumber("80 (0x50)"), 255, 58),
            0.01, "ec-probe 输出直解析 → 换算链路闭环");
    }

    private static void DirectionAndClamp()
    {
        Check.Near(0, FanPercentCodec.FromRaw(255, 255, 58), 0.001, "反向编码 Min 对应 0%");
        Check.Near(100, FanPercentCodec.FromRaw(58, 255, 58), 0.001, "反向编码 Max 对应 100%");
        Check.Near(50.196, FanPercentCodec.FromRaw(128, 0, 255), 0.01, "正向编码同样适用");
        Check.Near(0, FanPercentCodec.FromRaw(300, 255, 58), 0.001, "超出 Min 侧钳到 0%");
        Check.Near(100, FanPercentCodec.FromRaw(0, 255, 58), 0.001, "超出 Max 侧钳到 100%");
    }

    private static void InvalidInput()
    {
        Check.IsNaN(FanPercentCodec.FromRaw(double.NaN, 255, 58), "原始值 NaN 不得给出百分比");
        Check.IsNaN(FanPercentCodec.FromRaw(80, double.NaN, 58), "Min 缺失不得给出百分比");
        Check.IsNaN(FanPercentCodec.FromRaw(80, 58, 58), "Min=Max 时除零保护");
    }

    private static void ParseRawOutput()
    {
        Check.Near(80, FanPercentCodec.ParseFirstNumber("80 (0x50)"), 0.001, "十进制优先解析");
        Check.Near(80, FanPercentCodec.ParseFirstNumber("0x50"), 0.001, "十六进制解析");
        Check.Near(149, FanPercentCodec.ParseFirstNumber("read 149"), 0.001, "命令行回显里的数字");
        Check.IsNaN(FanPercentCodec.ParseFirstNumber("Could not load EC plugin"),
            "插件加载失败文本不得被误解析成数值");
        Check.IsNaN(FanPercentCodec.ParseFirstNumber(""), "空串返回 NaN");
        Check.IsNaN(FanPercentCodec.ParseFirstNumber(null), "null 返回 NaN");
    }
}
