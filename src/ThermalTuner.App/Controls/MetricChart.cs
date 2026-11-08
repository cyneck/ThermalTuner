using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using ThermalTuner.App.Services;

namespace ThermalTuner.App.Controls;

/// <summary>
/// 多序列统一时序图（自绘，无第三方依赖）。
///
/// 与 SparkChart 的区别：SparkChart 只有主/次两条线且共用同一数值轴，
/// 无法把 °C、W、% 画在一起而不失真。本控件为每条序列按自身量程独立归一化，
/// 共用同一时间轴（索引对齐），真实读数由图例给出。
/// 序列不可用时写 NaN，图上断线而不串接，避免伪造趋势。
/// </summary>
public sealed class MetricChart : FrameworkElement
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series), typeof(ObservableCollection<MetricSeries>), typeof(MetricChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSeriesChanged));

    public static readonly DependencyProperty ThresholdProperty = DependencyProperty.Register(
        nameof(Threshold), typeof(double), typeof(MetricChart),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>阈值线按哪条序列的量程换算（默认温度）。</summary>
    public static readonly DependencyProperty ThresholdSeriesKeyProperty = DependencyProperty.Register(
        nameof(ThresholdSeriesKey), typeof(string), typeof(MetricChart),
        new FrameworkPropertyMetadata("temp", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThresholdLabelProperty = DependencyProperty.Register(
        nameof(ThresholdLabel), typeof(string), typeof(MetricChart),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StartLabelProperty = DependencyProperty.Register(
        nameof(StartLabel), typeof(string), typeof(MetricChart),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public ObservableCollection<MetricSeries>? Series
    {
        get => (ObservableCollection<MetricSeries>?)GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public double Threshold
    {
        get => (double)GetValue(ThresholdProperty);
        set => SetValue(ThresholdProperty, value);
    }

    public string ThresholdSeriesKey
    {
        get => (string)GetValue(ThresholdSeriesKeyProperty);
        set => SetValue(ThresholdSeriesKeyProperty, value);
    }

    public string ThresholdLabel
    {
        get => (string)GetValue(ThresholdLabelProperty);
        set => SetValue(ThresholdLabelProperty, value);
    }

    public string StartLabel
    {
        get => (string)GetValue(StartLabelProperty);
        set => SetValue(StartLabelProperty, value);
    }

    private readonly List<MetricSeries> _attached = new();

    private static void OnSeriesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (MetricChart)d;
        if (e.OldValue is ObservableCollection<MetricSeries> oldCol)
            oldCol.CollectionChanged -= chart.OnSeriesCollectionChanged;
        chart.DetachAll();
        if (e.NewValue is ObservableCollection<MetricSeries> newCol)
            newCol.CollectionChanged += chart.OnSeriesCollectionChanged;
        chart.AttachAll();
        chart.InvalidateVisual();
    }

    private void OnSeriesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        DetachAll();
        AttachAll();
        InvalidateVisual();
    }

    private void OnValuesChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    private void OnSeriesPropertyChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    private void DetachAll()
    {
        foreach (var s in _attached)
        {
            s.Values.CollectionChanged -= OnValuesChanged;
            s.PropertyChanged -= OnSeriesPropertyChanged;
        }
        _attached.Clear();
    }

    private void AttachAll()
    {
        if (Series is null) return;
        foreach (var s in Series)
        {
            s.Values.CollectionChanged += OnValuesChanged;
            s.PropertyChanged += OnSeriesPropertyChanged;
            _attached.Add(s);
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 1 || h <= 1) return;

        var gridPen = new Pen(new SolidColorBrush(Color.FromRgb(0xEC, 0xEF, 0xED)), 1);
        for (int i = 0; i <= 4; i++)
        {
            double y = Math.Round(h * i / 4) + 0.5;
            dc.DrawLine(gridPen, new Point(0, y), new Point(w, y));
        }

        const double topPad = 8, bottomPad = 18;
        double plotH = Math.Max(1, h - topPad - bottomPad);
        var list = Series;

        if (list is null || list.Count == 0)
        {
            DrawHint(dc, w, h);
            return;
        }

        bool any = false;
        foreach (var s in list)
        {
            if (!s.Visible || Range(s) is not { } r) continue;
            any = true;
            Func<double, double> mapY = v => topPad + (r.Max - v) / (r.Max - r.Min) * plotH;
            DrawSeriesWithGaps(dc, s, w, mapY);
        }

        if (!any) DrawHint(dc, w, h);

        // 天花板阈值：只按参考序列（温度）的量程定位，避免多量纲下位置失真。
        // 自动量程下阈值常在当前温度范围之外，此时不画虚线（画在边缘会误导），
        // 但右上角恒显示阈值标注，读数始终可见。
        if (!double.IsNaN(Threshold))
        {
            var refSeries = list.FirstOrDefault(x => x.Key == ThresholdSeriesKey);
            (double Min, double Max)? r = refSeries is null ? null : Range(refSeries);
            if (r is { } range && Threshold > range.Min && Threshold < range.Max)
            {
                double y = Math.Round(topPad + (range.Max - Threshold) / (range.Max - range.Min) * plotH) + 0.5;
                var pen = new Pen(new SolidColorBrush(Color.FromRgb(0xB0, 0x45, 0x3C)), 1)
                {
                    DashStyle = new DashStyle(new double[] { 4, 4 }, 0)
                };
                dc.DrawLine(pen, new Point(0, y), new Point(w, y));
            }

            if (!string.IsNullOrWhiteSpace(ThresholdLabel))
            {
                var label = Text(ThresholdLabel, 10, 0xB0453C);
                dc.DrawText(label, new Point(Math.Max(0, w - label.Width - 4), 2));
            }
        }

        if (!string.IsNullOrWhiteSpace(StartLabel))
            dc.DrawText(Text(StartLabel, 10, 0x8C9A95), new Point(2, h - 14));
        dc.DrawText(Text("now", 10, 0x8C9A95), new Point(w - 24, h - 14));
    }

    private FormattedText Text(string value, double size, uint rgb) =>
        new(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei UI"), size,
            new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)),
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private void DrawHint(DrawingContext dc, double w, double h)
    {
        var hint = Text("等待采样数据…", 12, 0x8C9A95);
        dc.DrawText(hint, new Point((w - hint.Width) / 2, (h - hint.Height) / 2));
    }

    /// <summary>返回该序列的绘图量程；有效点少于 2 个返回 null。</summary>
    private static (double Min, double Max)? Range(MetricSeries s)
    {
        double min = double.MaxValue, max = double.MinValue;
        int n = 0;
        foreach (double v in s.Values)
        {
            if (double.IsNaN(v)) continue;
            if (v < min) min = v;
            if (v > max) max = v;
            n++;
        }
        if (n < 2) return null;

        if (!double.IsNaN(s.FixedMin) && !double.IsNaN(s.FixedMax))
            return (s.FixedMin, s.FixedMax);

        double pad = Math.Max(1.0, (max - min) * 0.12);
        min -= pad;
        max += pad;
        if (max - min < 1e-6) max = min + 1;
        return (min, max);
    }

    private static void DrawSeriesWithGaps(DrawingContext dc, MetricSeries s, double w,
        Func<double, double> mapY)
    {
        int n = s.Values.Count;
        if (n < 2) return;

        var segments = new List<List<Point>>();
        var cur = new List<Point>();
        for (int i = 0; i < n; i++)
        {
            double v = s.Values[i];
            if (double.IsNaN(v))
            {
                if (cur.Count > 0) segments.Add(cur);
                cur = new List<Point>();
                continue;
            }
            double x = n == 1 ? w / 2 : i * w / (n - 1.0);
            cur.Add(new Point(x, mapY(v)));
        }
        if (cur.Count > 0) segments.Add(cur);

        var pen = new Pen(s.Stroke, s.Thickness) { LineJoin = PenLineJoin.Round };
        if (s.Dashed) pen.DashStyle = new DashStyle(new double[] { 3, 3 }, 0);

        foreach (var seg in segments)
        {
            if (seg.Count < 2) continue;
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(seg[0], false, false);
                for (int i = 1; i < seg.Count; i++) ctx.LineTo(seg[i], true, false);
            }
            geo.Freeze();
            dc.DrawGeometry(null, pen, geo);
        }

        if (segments.Count > 0 && segments[^1].Count > 0)
            dc.DrawEllipse(s.Stroke, null, segments[^1][^1], 2.5, 2.5);
    }
}
