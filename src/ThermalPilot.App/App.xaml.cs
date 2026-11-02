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
