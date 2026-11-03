using System.Globalization;
using System.IO;
using System.Windows;

namespace ThermalPilot.App;

public partial class App : Application
{
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 无界面自检：逐页加载 + 布局，结果落盘后退出（供 CI / 研发冒烟用）
        if (e.Args.Contains("--smoke", StringComparer.OrdinalIgnoreCase))
        {
            string outPath = GetArgValue(e.Args, "--out")
                             ?? Path.Combine(Path.GetTempPath(), "thermalpilot-smoke.txt");
            int failures = SmokeTest.Run(outPath);
            Shutdown(failures == 0 ? 0 : 1);
            return;
        }

        if (e.Args.Contains("--msr-funcs", StringComparer.OrdinalIgnoreCase))
        {
            string report = GetArgValue(e.Args, "--out")
                            ?? Path.Combine(Path.GetTempPath(), "thermalpilot-msr-funcs.txt");
            Shutdown(MsrProbe.RunFunctionProbe(report));
            return;
        }

        if (e.Args.Contains("--write-test", StringComparer.OrdinalIgnoreCase))
        {
            string report = GetArgValue(e.Args, "--out")
                            ?? Path.Combine(Path.GetTempPath(), "thermalpilot-write-test.txt");
            Shutdown(MsrProbe.RunWriteTest(report));
            return;
        }

        if (e.Args.Contains("--msr-probe", StringComparer.OrdinalIgnoreCase))
        {
            string report = GetArgValue(e.Args, "--out")
                            ?? Path.Combine(Path.GetTempPath(), "thermalpilot-msr-probe.txt");
            Shutdown(MsrProbe.Run(report));
            return;
        }

        // 睿频倍率表自检：出厂档位 / 锁位 / 写入→回读→恢复（结束不改变硬件状态）
        if (e.Args.Contains("--turbo-probe", StringComparer.OrdinalIgnoreCase))
        {
            string report = GetArgValue(e.Args, "--out")
                            ?? Path.Combine(Path.GetTempPath(), "thermalpilot-turbo-probe.txt");
            Shutdown(MsrProbe.RunTurboProbe(report));
            return;
        }

        // 只读自检：RAPL 能量计数器能否给出真实包功耗
        if (e.Args.Contains("--rapl-probe", StringComparer.OrdinalIgnoreCase))
        {
            string report = GetArgValue(e.Args, "--out")
                            ?? Path.Combine(Path.GetTempPath(), "thermalpilot-rapl-probe.txt");
            Shutdown(RaplProbe.Run(report));
            return;
        }

        // 标定流程自检（真实满载 1–2 分钟，研发/QA 用）
        if (e.Args.Contains("--calibrate-test", StringComparer.OrdinalIgnoreCase))
        {
            string report = GetArgValue(e.Args, "--out")
                            ?? Path.Combine(Path.GetTempPath(), "thermalpilot-calibration.txt");
            Shutdown(CalibrationProbe.Run(report));
            return;
        }

        // 多档 PL1 阶梯压测智能标定：真实写入 0x610 → 逐档满载稳态 → 拟合 T(P) → 反解推荐 PL1
        // 用法：--calibrate-sweep --out report.txt --csv samples.csv [--seconds 25] [--ceiling 85]
        if (e.Args.Contains("--calibrate-sweep", StringComparer.OrdinalIgnoreCase))
        {
            string report = GetArgValue(e.Args, "--out")
                            ?? Path.Combine(Path.GetTempPath(), "thermalpilot-sweep.txt");
            string csv = GetArgValue(e.Args, "--csv")
                         ?? Path.ChangeExtension(report, ".csv");
            int seconds = int.TryParse(GetArgValue(e.Args, "--seconds"), out int secs)
                          && secs is >= 10 and <= 300 ? secs : 25;
            double ceiling = double.TryParse(GetArgValue(e.Args, "--ceiling"),
                                 NumberStyles.Float, CultureInfo.InvariantCulture, out double c)
                             ? c : double.NaN;
            Shutdown(CalibrationSweepProbe.Run(new CalibrationSweepProbe.Options(report, csv, seconds, ceiling)));
            return;
        }

        // A/B 压力测试：验证 0x610 写入链路 + 满载温度/功耗/频率曲线
        // 用法：--stress-test --out report.txt --csv samples.csv [--seconds 45] [--low-pl1 25]
        if (e.Args.Contains("--stress-test", StringComparer.OrdinalIgnoreCase))
        {
            string report = GetArgValue(e.Args, "--out")
                            ?? Path.Combine(Path.GetTempPath(), "thermalpilot-stress.txt");
            string csv = GetArgValue(e.Args, "--csv")
                         ?? Path.ChangeExtension(report, ".csv");
            int seconds = int.TryParse(GetArgValue(e.Args, "--seconds"), out int secs)
                          && secs is >= 10 and <= 600 ? secs : 45;
            double lowPl1 = double.TryParse(GetArgValue(e.Args, "--low-pl1"),
                                NumberStyles.Float, CultureInfo.InvariantCulture, out double lp)
                            && lp >= 5 && lp <= 200 ? lp : 25;
            Shutdown(StressProbe.Run(new StressProbe.Options(report, csv, seconds, lowPl1)));
            return;
        }

        // 睿频倍率 A/B 满载实测：出厂倍率表 vs 拉平到出厂单核上限，验证是否真的提升核频
        // 用法：--turbo-stress --out report.txt --csv samples.csv [--seconds 30]
        if (e.Args.Contains("--turbo-stress", StringComparer.OrdinalIgnoreCase))
        {
            string report = GetArgValue(e.Args, "--out")
                            ?? Path.Combine(Path.GetTempPath(), "thermalpilot-turbo-stress.txt");
            string csv = GetArgValue(e.Args, "--csv")
                         ?? Path.ChangeExtension(report, ".csv");
            int seconds = int.TryParse(GetArgValue(e.Args, "--seconds"), out int secs)
                          && secs is >= 10 and <= 300 ? secs : 30;
            Shutdown(TurboStressProbe.Run(new TurboStressProbe.Options(report, csv, seconds)));
            return;
        }

        if (e.Args.Contains("--shots", StringComparer.OrdinalIgnoreCase))
        {
            string dir = GetArgValue(e.Args, "--shots")
                         ?? Path.Combine(Path.GetTempPath(), "thermalpilot-shots");
            Shutdown(Shots.Run(dir) == 0 ? 0 : 1);
            return;
        }

        // 单实例：避免托盘常驻时被重复启动（CLI 自检模式已在此之前返回）
        _singleInstance = new Mutex(initiallyOwned: true, @"Global\ThermalPilot.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("ThermalPilot 已在运行。\n请从任务栏托盘图标打开。", "ThermalPilot",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                "发生未预期错误：\n" + args.Exception.Message + "\n\n程序会继续运行，可在「诊断与日志」查看详情。",
                "ThermalPilot", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };

        new MainWindow().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _singleInstance?.ReleaseMutex(); } catch { /* 未持有则忽略 */ }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private static string? GetArgValue(string[] args, string name)
    {
        int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
