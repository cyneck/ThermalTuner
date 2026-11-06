using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using ThermalTuner.App.Services;
using ThermalTuner.Core.Stress;

namespace ThermalTuner.App;

public partial class App : Application
{
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // 带参数的启动都是命令行自检，不消耗「首次安装启动」标记
        EnvironmentSelfCheck.IsCommandLineMode = e.Args.Length > 0;

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

        // 通用 HWP 参数自检：0x774 全字段写入 → 回读 → 整字恢复出厂基线
        if (e.Args.Contains("--hwp-probe", StringComparer.OrdinalIgnoreCase))
        {
            string report = GetArgValue(e.Args, "--out")
                            ?? Path.Combine(Path.GetTempPath(), "thermalpilot-hwp-probe.txt");
            Shutdown(MsrProbe.RunHwpProbe(report));
            return;
        }

        // 个性化电压偏移自检：小幅降压 → 回读 → 强制回零（判断该 CPU 是否真的可写）
        if (e.Args.Contains("--voltage-probe", StringComparer.OrdinalIgnoreCase))
        {
            string report = GetArgValue(e.Args, "--out")
                            ?? Path.Combine(Path.GetTempPath(), "thermalpilot-voltage-probe.txt");
            Shutdown(MsrProbe.RunVoltageProbe(report));
            return;
        }

        // 环境自检：首次安装启动会自动跑只读自检；此命令用于管理员下补做写入能力实测并出报告
        if (e.Args.Contains("--env-check", StringComparer.OrdinalIgnoreCase))
        {
            string report = GetArgValue(e.Args, "--out")
                            ?? Path.Combine(Path.GetTempPath(), "thermalpilot-env-check.txt");
            // 先采一拍遥测，避免命令行模式把温度 / 功耗误判成「不可读」
            var session = new SessionState();
            session.SampleOnce();
            string telemetry =
                $"温度 {(session.TempReadable ? "可读" : "不可读")} · " +
                $"功耗 {(session.PowerMeasured ? "实测" : "估算 / 不可用")} · {session.RaplText}";
            var snap = EnvironmentSelfCheck.Run(true, telemetry,
                session.TempReadable, session.PowerMeasured);
            string text = EnvironmentSelfCheck.Report(snap);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
                File.WriteAllText(report, text, new UTF8Encoding(true));
            }
            catch { /* 写盘失败不影响退出码 */ }
            Console.WriteLine(text);
            Shutdown(0);
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

        // 场景化压测 / 功耗覆盖扫描：以 0x610 实读 PL1 为目标，跑完整负载场景阶梯
        // 用法：--scenario-sweep --out report.txt --csv samples.csv [--seconds 12] [--target-ratio 0.9]
        if (e.Args.Contains("--scenario-sweep", StringComparer.OrdinalIgnoreCase))
        {
            string report = GetArgValue(e.Args, "--out")
                            ?? Path.Combine(Path.GetTempPath(), "thermalpilot-scenarios.txt");
            string csv = GetArgValue(e.Args, "--csv")
                         ?? Path.ChangeExtension(report, ".csv");
            int seconds = int.TryParse(GetArgValue(e.Args, "--seconds"), out int s2)
                          && s2 is >= 3 and <= 120 ? s2 : 12;
            double ratio = double.TryParse(GetArgValue(e.Args, "--target-ratio"),
                               NumberStyles.Float, CultureInfo.InvariantCulture, out double tr)
                           && tr is >= 0.3 and <= 1.5 ? tr : 0.9;
            Shutdown(ScenarioSweepProbe.Run(new ScenarioSweepProbe.Options(report, csv, seconds, ratio)));
            return;
        }

        // A/B 压力测试：验证 0x610 写入链路 + 满载温度/功耗/频率曲线
        // 用法：--stress-test --out report.txt --csv samples.csv [--seconds 45]
        //       [--low-pl1 25] [--kernel SimdFma]
        if (e.Args.Contains("--stress-test", StringComparer.OrdinalIgnoreCase))
        {
            string report = GetArgValue(e.Args, "--out")
                            ?? Path.Combine(Path.GetTempPath(), "thermalpilot-stress.txt");
            string csv = GetArgValue(e.Args, "--csv")
                         ?? Path.ChangeExtension(report, ".csv");
            int seconds = int.TryParse(GetArgValue(e.Args, "--seconds"), out int secs)
                          && secs is >= 10 and <= 600 ? secs : 45;
            // 省略时传 NaN：降墙档由 0x610 实读 PL1 按比例推导，不预设任何机型瓦数
            double lowPl1 = double.TryParse(GetArgValue(e.Args, "--low-pl1"),
                                NumberStyles.Float, CultureInfo.InvariantCulture, out double lp)
                            && lp >= 5 && lp <= 200 ? lp : double.NaN;
            var kernel = StressScenarioCatalog.TryParseKernel(GetArgValue(e.Args, "--kernel"), out var k)
                         ? k : StressKernel.SimdFma;
            Shutdown(StressProbe.Run(new StressProbe.Options(report, csv, seconds, lowPl1, kernel)));
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
        _singleInstance = new Mutex(initiallyOwned: true, @"Global\ThermalTuner.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("ThermalTuner 已在运行。\n请从任务栏托盘图标打开。", "ThermalTuner",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                "发生未预期错误：\n" + args.Exception.Message + "\n\n程序会继续运行，可在「诊断与日志」查看详情。",
                "ThermalTuner", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };

        // 更名过渡：清理指向旧版 exe 的开机自启项（仅该情况才删，不误伤同名项）
        StartupRegistration.CleanupLegacy();

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
