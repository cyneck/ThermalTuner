using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using ThermalPilot.Core;

namespace ThermalPilot.App;

public partial class App : System.Windows.Application
{
    private static Mutex? _singleInstance;

    /// <summary>界面自检模式（不接管硬件，仅渲染 UI），用于排障与打包验收。</summary>
    public static bool SmokeTest { get; private set; }
    public static string? SmokeShotPath { get; private set; }
    public static string SmokeNav { get; private set; } = "dash";
    /// <summary>自检时可指定滚动位置（像素），用于截取长面板的下半部分。</summary>
    public static double SmokeScroll { get; private set; }
    /// <summary>
    /// 自检时伪造平台能力（amd / noturbo / nohwp / needadmin / unknown），
    /// 用来验证"非 Intel 平台高级参数是否真的被禁用"—— 真机上没法把 CPU 换掉。
    /// </summary>
    public static string SmokeCaps { get; private set; } = "";

    protected override void OnStartup(StartupEventArgs e)
    {
        // 卸载/修复前由安装包调用：把电源设置还原回安装前的值
        if (e.Args.Any(a => a.Equals("--restore", StringComparison.OrdinalIgnoreCase)))
        {
            RestoreForUninstall();
            Shutdown();
            return;
        }

        foreach (string a in e.Args)
        {
            if (a.Equals("--smoke", StringComparison.OrdinalIgnoreCase)) SmokeTest = true;
            else if (a.StartsWith("--shot=", StringComparison.OrdinalIgnoreCase)) SmokeShotPath = a["--shot=".Length..];
            else if (a.StartsWith("--smoke-nav=", StringComparison.OrdinalIgnoreCase)) SmokeNav = a["--smoke-nav=".Length..];
            else if (a.StartsWith("--smoke-scroll=", StringComparison.OrdinalIgnoreCase) &&
                     double.TryParse(a["--smoke-scroll=".Length..], out double sc)) SmokeScroll = sc;
            else if (a.StartsWith("--smoke-caps=", StringComparison.OrdinalIgnoreCase))
                SmokeCaps = a["--smoke-caps=".Length..].ToLowerInvariant();
        }

        if (SmokeTest)
        {
            // 自检模式不持有单实例锁，避免影响正常实例
            base.OnStartup(e);
            return;
        }

        _singleInstance = new Mutex(true, @"Global\ThermalPilot.SingleInstance", out bool created);
        if (!created)
        {
            System.Windows.MessageBox.Show("ThermalPilot 已经在运行，请查看任务栏托盘图标。",
                "ThermalPilot", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("未处理异常：" + (args.ExceptionObject as Exception)?.Message);

        Log.Cleanup();
        Log.Info("===== ThermalPilot 启动 =====");
        base.OnStartup(e);
    }

    private void OnDispatcherException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("UI 线程异常", e.Exception);
        System.Windows.MessageBox.Show(
            "发生未处理异常，已记录日志：\n" + e.Exception.Message +
            "\n\n程序将尝试还原系统默认设置后退出。",
            "ThermalPilot", MessageBoxButton.OK, MessageBoxImage.Error);

        try { RestoreForUninstall(); } catch { }
        e.Handled = true;
        Shutdown(1);
    }

    /// <summary>把电源方案还原为安装前的快照（引擎启动时写 original-power.json）。</summary>
    private static void RestoreForUninstall()
    {
        try
        {
            int procMax = 100, boost = 2;
            string file = Path.Combine(Paths.DataDirectory, "original-power.json");
            if (File.Exists(file))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.TryGetProperty("procMax", out var pm)) procMax = pm.GetInt32();
                if (doc.RootElement.TryGetProperty("boost", out var bm)) boost = bm.GetInt32();
            }

            var power = new PowerManager();
            if (power.Initialize())
            {
                power.SetProcessorMax(procMax);
                power.SetBoostMode(boost);
                Log.Info($"卸载还原：最大处理器状态={procMax}%，Turbo策略={boost}");
            }
        }
        catch (Exception ex)
        {
            Log.Error("卸载还原失败", ex);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("===== ThermalPilot 退出 =====");
        try { _singleInstance?.ReleaseMutex(); } catch { }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
