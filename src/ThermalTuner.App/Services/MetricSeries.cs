using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Media;

namespace ThermalTuner.App.Services;

/// <summary>
/// 统一时序图的一条指标序列：自带量纲、量程、历史点与当前读数。
///
/// 设计边界：
/// · 各序列按自身量程独立归一化后画在同一张图上，图例显示带单位的真实读数，
///   因此 °C / W / % 混合显示时不会产生「共用一个数值轴」的误读。
/// · 采样不可用时写 double.NaN 而不是跳过，保证所有序列索引对齐（同一时间点）。
/// · 只承载只读采集值；决策树演算建议值（TargetFanPercent）不得注册为序列。
/// </summary>
public sealed class MetricSeries : INotifyPropertyChanged
{
    public string Key { get; }
    public string Label { get; }
    public string Unit { get; }
    public Brush Stroke { get; }
    public double Thickness { get; }
    public bool Dashed { get; }
    /// <summary>NaN 表示自动量程；百分比类指标固定 0~100。</summary>
    public double FixedMin { get; }
    public double FixedMax { get; }
    public string Format { get; }
    public string Tooltip { get; }
    public ObservableCollection<double> Values { get; } = new();

    private bool _visible = true;
    public bool Visible
    {
        get => _visible;
        set
        {
            if (_visible == value) return;
            _visible = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Visible)));
        }
    }

    /// <summary>
    /// 图例附加说明（如风扇的 EC 原始字节）。由采集侧逐拍更新，
    /// 变更时主动通知 CurrentText，避免图例停留在旧值。
    /// </summary>
    private string _extra = string.Empty;
    public string Extra
    {
        get => _extra;
        set
        {
            if (_extra == value) return;
            _extra = value ?? string.Empty;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentText)));
        }
    }

    /// <summary>图例读数：取最近一个有效值，无有效值显示 N/A。</summary>
    public string CurrentText
    {
        get
        {
            for (int i = Values.Count - 1; i >= 0; i--)
            {
                double v = Values[i];
                if (double.IsNaN(v)) continue;
                return $"{v.ToString(Format, CultureInfo.InvariantCulture)} {Unit}{Extra}";
            }
            return "N/A";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public MetricSeries(string key, string label, string unit, Brush stroke,
        double thickness = 2.0, bool dashed = false,
        double fixedMin = double.NaN, double fixedMax = double.NaN,
        string format = "F1", string tooltip = "")
    {
        Key = key;
        Label = label;
        Unit = unit;
        Stroke = stroke;
        Thickness = thickness;
        Dashed = dashed;
        FixedMin = fixedMin;
        FixedMax = fixedMax;
        Format = format;
        Tooltip = tooltip;
        Values.CollectionChanged += (_, _) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentText)));
    }

    /// <summary>追加一拍；超长裁剪保证与主历史缓冲同长。</summary>
    public void Push(double value, int cap)
    {
        Values.Add(value);
        while (Values.Count > cap) Values.RemoveAt(0);
    }
}
