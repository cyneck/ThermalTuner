using System.Diagnostics;
using ThermalTuner.Core.Tests;

// 独立测试入口：dotnet run --project tests/ThermalTuner.Core.Tests
// 零外部测试框架依赖：每个套件自带断言，Check 负责新增用例的计数与失败清单。

var suites = new (string Name, Action Run)[]
{
    ("MsrEncoding",        () => new MsrEncodingTests().RunAllTests()),
    ("TurboRatioLimit",    () => new TurboRatioLimitTests().RunAllTests()),
    ("ThermalCalibration", () => new ThermalCalibrationTests().RunAllTests()),
    ("ThermalResistance",  () => new ThermalResistanceTests().RunAllTests()),
    ("DecisionTree",       () => new DecisionTreeTests().RunAllTests()),
    ("PlatformProfile",    () => new PlatformProfileTests().RunAllTests()),
    ("StressScenario",     () => new StressScenarioTests().RunAllTests()),
    ("AdvancedParams",     () => new AdvancedParamsTests().RunAllTests()),
    ("ProfileEffect",      () => new ProfileEffectTests().RunAllTests()),
    ("FanPercent",         () => new FanPercentTests().RunAllTests()),
    ("FanStagnation",      () => new FanStagnationTests().RunAllTests()),
    ("WriteScheduler",     () => new WriteSchedulerTests().RunAllTests()),
    ("TrajectoryShaper",   () => new TrajectoryShaperTests().RunAllTests()),
    ("ThermalEvent",       () => new ThermalEventTests().RunAllTests())
};

int failedSuites = 0;
var total = Stopwatch.StartNew();

Console.WriteLine("==== ThermalTuner Core 功能测试 ====");
foreach (var (name, run) in suites)
{
    var sw = Stopwatch.StartNew();
    try
    {
        run();
        Console.WriteLine($"PASS  {name,-20} {sw.ElapsedMilliseconds,5} ms");
    }
    catch (Exception ex)
    {
        failedSuites++;
        Console.WriteLine($"FAIL  {name,-20} {sw.ElapsedMilliseconds,5} ms  {ex.Message}");
    }
}
total.Stop();

Console.WriteLine();
Console.WriteLine($"套件 : {suites.Length - failedSuites}/{suites.Length} 通过（失败 {failedSuites}）");
Console.WriteLine($"断言 : 通过 {Check.Passed} / 失败 {Check.Failures.Count}（新增用例）");
Console.WriteLine($"耗时 : {total.ElapsedMilliseconds} ms");

if (Check.Failures.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("失败断言：");
    foreach (string f in Check.Failures) Console.WriteLine("  ✗ " + f);
}

bool ok = failedSuites == 0 && Check.Failures.Count == 0;
Console.WriteLine(ok ? "RESULT: PASS" : "RESULT: FAIL");
return ok ? 0 : 1;
