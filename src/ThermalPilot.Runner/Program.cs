using ThermalPilot.Core;
using ThermalPilot.Core.Environment;
using ThermalPilot.Core.Taxonomy;
using ThermalPilot.Core.Tests;

// ============================================================================
// ThermalPilot Runner —— 环境自适应启动入口
//
// 流程：
//   1. HardwareProbe  探测当前机器 CPU 平台画像（零特权、跨平台）
//   2. HardwareLimitResolver  推导该平台的物理硬约束 (TjMax / PL 范围)
//   3. 运行决策树全量单元测试（验证算法与平台无关）
//   4. 用推导出的真实约束跑一段模拟遥测控制回路，输出因果凭证
// ============================================================================

Console.WriteLine("========================================================");
Console.WriteLine("  ThermalPilot Runner - 环境自适应 CPU 热/功耗控制");
Console.WriteLine("========================================================");
Console.WriteLine();

// ---------- 1. 环境探测 ----------
var env = HardwareProbe.Probe();

Console.WriteLine("[1] 环境探测结果 (HardwareProbe)");
Console.WriteLine($"    CPU 品牌   : {env.BrandString}");
Console.WriteLine($"    厂商       : {env.Vendor}");
Console.WriteLine($"    代数编码   : {env.Generation}  (7=KabyLake, 12=AlderLake, 100=MeteorLake, 200=ArrowLake)");
Console.WriteLine($"    产品线后缀 : {(string.IsNullOrEmpty(env.Suffix) ? "(无)" : env.Suffix)}");
Console.WriteLine($"    形态分类   : {env.FormFactor}");
Console.WriteLine($"    混合架构   : {env.IsHybridArchitecture}");
Console.WriteLine($"    核心数     : {env.PhysicalCores} 物理 / {env.LogicalCores} 逻辑");
Console.WriteLine($"    操作系统   : {env.OsPlatform}");
Console.WriteLine($"    虚拟化     : {env.IsVirtualized}");
if (env.Notes.Count > 0)
{
    foreach (var note in env.Notes)
        Console.WriteLine($"    备注       : {note}");
}
Console.WriteLine();

// ---------- 2. 约束推导 ----------
var limits = HardwareLimitResolver.Resolve(env);
var baseline = HardwareLimits.DefaultIntelMobile;

Console.WriteLine("[2] 推导出的物理硬约束 (HardwareLimitResolver)");
Console.WriteLine($"    TjMax           : {limits.TjMax:F0}°C   (安全天花板 {limits.MaxSafeCeiling:F0}°C)");
Console.WriteLine($"    PL1 范围        : [{limits.MinPL1:F0}W, {limits.MaxPL1:F0}W]");
Console.WriteLine($"    PL2 范围        : [{limits.MinPL2:F0}W, {limits.MaxPL2:F0}W]");
Console.WriteLine($"    vs 7代基线     : PL1 上限 {baseline.MaxPL1:F0}W→{limits.MaxPL1:F0}W, " +
                  $"PL2 上限 {baseline.MaxPL2:F0}W→{limits.MaxPL2:F0}W");
Console.WriteLine();

// ---------- 3. 单元测试 ----------
Console.WriteLine("[3] 决策树单元测试 (DecisionTreeTests)");
try
{
    new DecisionTreeTests().RunAllTests();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"    测试失败: {ex.Message}");
    return 1;
}
Console.WriteLine();

// ---------- 4. 模拟控制回路 ----------
Console.WriteLine("[4] 模拟遥测控制回路（使用本机推导约束）");
Console.WriteLine("    每拍输出: 规则 | PL1 | 方向 | 因果凭证");
Console.WriteLine("    --------------------------------------------------------");

var controller = new AutoCpuController(limits);
var profile = UserProfile.Balanced(limits);
double ceiling = profile.TargetTemperatureCeiling;

// 模拟场景：空闲 → 中载 → 逼近天花板 → 超温负反馈 → 安全熔断 → 轻载回落
var scenarios = new (double Temp, double Power, double Ambient, string Profile, double Load, bool Prochot)[]
{
    (42.0,  8.0, 25.0, "balanced",    12.0, false), // 轻载
    (58.0, 28.0, 25.0, "balanced",    65.0, false), // 中载升温
    (72.0, 40.0, 26.0, "performance", 90.0, false), // 高负载
    (86.0, 48.0, 27.0, "performance", 95.0, false), // 逼近天花板
    (95.0, 52.0, 28.0, "performance", 98.0, false), // 超温: 触发 CeilingExceeded 负反馈
    (98.0, 55.0, 29.0, "performance", 99.0, true),  // PROCHOT: 触发 SafetyWatchdog 熔断
    (78.0, 20.0, 26.0, "silent",      45.0, false), // 降温回落
    (55.0,  6.0, 25.0, "silent",      10.0, false), // 回到轻载
};

for (int i = 0; i < scenarios.Length; i++)
{
    var s = scenarios[i];
    var dec = controller.Update(
        tempC: s.Temp,
        powerW: s.Power,
        ceilingC: AutoCpuController.TargetCForProfile(s.Profile),
        ambientC: s.Ambient,
        profile: s.Profile,
        perf: new PerfSample { CoreClocksGhz = 3.2, MaxTurboGhz = 5.0, Prochot = s.Prochot },
        loadPct: s.Load,
        dTdt: 0.3);

    string arrow = dec.Direction switch
    {
        > 0 => "↑",
        < 0 => "↓",
        _ => "→"
    };

    Console.WriteLine($"    T{i + 1} {s.Temp,5:F1}°C {s.Load,3:F0}% | {dec.Pl1W,5:F1}W {arrow} | {dec.Limit,-14} | {dec.Reason}");
}

Console.WriteLine();
Console.WriteLine("========================================================");
Console.WriteLine($"  完成。控制策略全程基于本机实测约束运行，");
Console.WriteLine($"  决策树节点与平台解耦，换任意 CPU 仅需重新探测。");
Console.WriteLine("========================================================");
return 0;
