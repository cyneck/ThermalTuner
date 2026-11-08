using System.Collections.ObjectModel;
using System.IO;
using System.Diagnostics;
using System.Management;
using System.Windows;
using System.Windows.Controls;
using ThermalTuner.App.Services;

namespace ThermalTuner.App.Views;

public partial class DiagnosticsPage : UserControl
{
    /// <summary>数据目录里的一个文件：名称 / 用途 / 当前状态。用途固定，状态每次刷新重算。</summary>
    private sealed record DataFileRow(string Name, string Purpose, string State);

    private readonly ObservableCollection<DataFileRow> _dataFiles = new();

    public DiagnosticsPage()
    {
        InitializeComponent();
        DataFileList.ItemsSource = _dataFiles;
        Loaded += (_, _) =>
        {
            if (Session is { } s)
            {
                s.Alerts.CollectionChanged -= OnAlertsChanged;
                s.Alerts.CollectionChanged += OnAlertsChanged;
            }
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            if (Session is { } s) s.Alerts.CollectionChanged -= OnAlertsChanged;
        };
    }

    private SessionState? Session => DataContext as SessionState;

    private void OnAlertsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => UpdateAlertEmpty();

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    /// <summary>管理员重新自检（无害写探测）。原「高级参数」页的按钮迁到诊断页。</summary>
    private void EnvCheck_Click(object sender, RoutedEventArgs e)
    {
        if (Session is not { } s) return;
        string result = s.RunFullEnvironmentCheck();
        EnvCheckOutcome.Text = "环境自检：" + result;
        EnvCheckOutcome.Visibility = Visibility.Visible;
        Refresh();
    }

    private void Refresh()
    {
        if (Session is not { } s) return;

        VmText.Text = s.Environment.IsVirtualized ? "是（功耗寄存器可能不可写）" : "否";

        try
        {
            SensorText.Text = string.Join(Environment.NewLine, s.DumpSensors());
        }
        catch (Exception ex)
        {
            SensorText.Text = "读取传感器失败：" + ex.Message;
        }

        HvciText.Text = ReadHvci();
        BuildDataFiles();
        UpdateAlertEmpty();
    }

    private void UpdateAlertEmpty()
    {
        bool empty = Session?.Alerts.Count is null or 0;
        AlertEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 按用途列出数据目录里的文件。刻意不合并成「日志目录」一个条目：
    /// 排障时要分清「哪份是出厂基线、哪份是我保存的微调、哪行是写入审计」。
    /// </summary>
    private void BuildDataFiles()
    {
        _dataFiles.Clear();
        string dir = SettingsStore.DataDirectory;

        _dataFiles.Add(new DataFileRow("settings.json", "设置、调优模式、自动推荐值、手工微调值",
            FileState(Path.Combine(dir, "settings.json"))));

        _dataFiles.Add(new DataFileRow("capability.json", "环境自检快照：决定哪些参数可编辑（只读项依据）",
            FileState(Path.Combine(dir, "capability.json"))));

        _dataFiles.Add(new DataFileRow("startup-log.txt", "启动耗时记录：检测缓存是否命中、各阶段用时",
            LineState(Path.Combine(dir, "startup-log.txt"), "行")));

        _dataFiles.Add(new DataFileRow("backup/msr-*-startup.json", "L1 固件基线：首次运行快照，永不覆盖",
            BackupState(Path.Combine(dir, "backup"))));

        _dataFiles.Add(new DataFileRow("logs/msr-audit.log", "写入审计：每次写入 / 恢复的时间、寄存器、前后值、结果",
            LineState(Path.Combine(dir, "logs", "msr-audit.log"), "行")));

        _dataFiles.Add(new DataFileRow("thermal-events.csv", "真实触顶热事件埋点（只采数，不参与冷却策略）",
            EventState(Path.Combine(dir, "thermal-events.csv"))));
    }

    private static string FileState(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return "未生成";
            return $"{Size(fi.Length)} · {fi.LastWriteTime:MM-dd HH:mm}";
        }
        catch
        {
            return "无法读取";
        }
    }

    private static string BackupState(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return "未生成";
            var files = Directory.GetFiles(dir, "msr-*-startup.json");
            return files.Length == 0 ? "未生成" : $"{files.Length} 份快照";
        }
        catch
        {
            return "无法读取";
        }
    }

    private static string LineState(string path, string unit)
    {
        try
        {
            if (!File.Exists(path)) return "未生成";
            int lines = 0;
            foreach (var _ in File.ReadLines(path))
                if (++lines >= 9999) break;
            var fi = new FileInfo(path);
            return $"{lines} {unit} · {fi.LastWriteTime:MM-dd HH:mm}";
        }
        catch
        {
            return "无法读取";
        }
    }

    /// <summary>热事件表：首发写表头，所以事件数 = 行数 − 1；0 条是很常见的正常态。</summary>
    private static string EventState(string path)
    {
        try
        {
            if (!File.Exists(path)) return "暂无事件";
            int lines = 0;
            foreach (var _ in File.ReadLines(path))
                if (++lines >= 9999) break;
            int events = Math.Max(0, lines - 1);
            return events == 0 ? "暂无事件" : $"{events} 条事件";
        }
        catch
        {
            return "无法读取";
        }
    }

    private static string Size(long bytes) => bytes < 1024
        ? $"{bytes} B"
        : bytes < 1024 * 1024 ? $"{bytes / 1024.0:F1} KB" : $"{bytes / 1024.0 / 1024.0:F1} MB";

    private static string ReadHvci()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\DeviceGuard",
                "SELECT VirtualizationBasedSecurityStatus, CodeIntegrityPolicyEnforcementStatus FROM Win32_DeviceGuard");
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    object? vbs = mo["VirtualizationBasedSecurityStatus"];
                    object? ci = mo["CodeIntegrityPolicyEnforcementStatus"];
                    string vbsText = Convert.ToInt32(vbs) switch
                    {
                        2 => "VBS 已启用",
                        1 => "VBS 已启用（未运行）",
                        _ => "VBS 关闭"
                    };
                    string ciText = Convert.ToInt32(ci) switch
                    {
                        2 => "驱动强制签名",
                        1 => "审计模式",
                        _ => "签名策略关闭"
                    };
                    return $"{vbsText} · {ciText}";
                }
            }
        }
        catch
        {
            // 非管理员或平台不支持时静默降级
        }
        return "无法读取（需管理员或平台不支持）";
    }

    private void OpenDataDir_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.DataDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = SettingsStore.DataDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show("无法打开目录：" + ex.Message, "ThermalTuner",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
