using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media;

namespace ThermalTuner.App.Controls;

/// <summary>
/// 轻量趋势图（自绘，无第三方依赖）。
/// 支持两条序列（主序列填充 + 次序列虚线）与一条阈值线，用于温度 / 功耗同屏对比。
/// 研发注意：如需缩放、悬浮读数、峰值标记（AC-17），在此扩展 MouseMove 与 OnRender 即可。
/// </summary>
public sealed class SparkChart : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(ObservableCollection<double>), typeof(SparkChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnValuesChanged));

    public static readonly DependencyProperty SecondaryValuesProperty = DependencyProperty.Register(
        nameof(SecondaryValues), typeof(ObservableCollection<double>), typeof(SparkChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnValuesChanged));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(SparkChart),
        new FrameworkPropertyMetadata(Brushes.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SecondaryStrokeProperty = DependencyProperty.Register(
        nameof(SecondaryStroke), typeof(Brush), typeof(SparkChart),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(SparkChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThresholdProperty = DependencyProperty.Register(
        nameof(Threshold), typeof(double), typeof(SparkChart),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThresholdBrushProperty = DependencyProperty.Register(
        nameof(ThresholdBrush), typeof(Brush), typeof(SparkChart),
        new FrameworkPropertyMetadata(Brushes.IndianRed, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AutoScaleProperty = DependencyProperty.Register(
        nameof(AutoScale), typeof(bool), typeof(SparkChart),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(SparkChart),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(SparkChart),
        new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public ObservableCollection<double>? Values
    {
        get => (ObservableCollection<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public ObservableCollection<double>? SecondaryValues
    {
        get => (ObservableCollection<double>?)GetValue(SecondaryValuesProperty);
        set => SetValue(SecondaryValuesProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public Brush SecondaryStroke
    {
        get => (Brush)GetValue(SecondaryStrokeProperty);
        set => SetValue(SecondaryStrokeProperty, value);
    }

    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public double Threshold
    {
        get => (double)GetValue(ThresholdProperty);
        set => SetValue(ThresholdProperty, value);
    }

    public Brush ThresholdBrush
    {
        get => (Brush)GetValue(ThresholdBrushProperty);
        set => SetValue(ThresholdBrushProperty, value);
    }

    public bool AutoScale
    {
        get => (bool)GetValue(AutoScaleProperty);
        set => SetValue(AutoScaleProperty, value);
    }

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    private static void OnValuesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (SparkChart)d;
        if (e.OldValue is ObservableCollection<double> oldCol)
            oldCol.CollectionChanged -= chart.OnCollectionChanged;
        if (e.NewValue is ObservableCollection<double> newCol)
            newCol.CollectionChanged += chart.OnCollectionChanged;
        chart.InvalidateVisual();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

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

        var primary = Values;
        var secondary = SecondaryValues;
        if ((primary is null || primary.Count < 2) && (secondary is null || secondary.Count < 2))
        {
            var hint = new FormattedText("等待采样数据…", System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 12,
                new SolidColorBrush(Color.FromRgb(0x8C, 0x9A, 0x95)), VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(hint, new Point((w - hint.Width) / 2, (h - hint.Height) / 2));
            return;
        }

        double min = double.MaxValue, max = double.MinValue;
        Collect(primary, ref min, ref max);
        Collect(secondary, ref min, ref max);
        if (!AutoScale)
        {
            min = Minimum;
            max = Maximum;
        }
        else
        {
            double pad = Math.Max(1.0, (max - min) * 0.12);
            min -= pad;
            max += pad;
        }
        if (max - min < 1e-6) { max = min + 1; }

        double MapY(double v) => h - (v - min) / (max - min) * h;

        if (!double.IsNaN(Threshold) && Threshold > min && Threshold < max)
        {
            var pen = new Pen(ThresholdBrush, 1) { DashStyle = new DashStyle(new double[] { 4, 4 }, 0) };
            double ty = Math.Round(MapY(Threshold)) + 0.5;
            dc.DrawLine(pen, new Point(0, ty), new Point(w, ty));
        }

        DrawSeries(dc, primary, w, h, MapY, Stroke, Fill, 2.0, null);
        DrawSeries(dc, secondary, w, h, MapY, SecondaryStroke, null, 1.4,
            new DashStyle(new double[] { 3, 3 }, 0));
    }

    private static void Collect(ObservableCollection<double>? series, ref double min, ref double max)
    {
        if (series is null) return;
        foreach (var v in series)
        {
            if (double.IsNaN(v)) continue;
            if (v < min) min = v;
            if (v > max) max = v;
        }
    }

    private static void DrawSeries(DrawingContext dc, ObservableCollection<double>? series, double w, double h,
        Func<double, double> mapY, Brush stroke, Brush? fill, double thickness, DashStyle? dash)
    {
        if (series is null || series.Count < 2) return;

        int n = series.Count;
        var pts = new List<Point>(n);
        for (int i = 0; i < n; i++)
        {
            double v = series[i];
            if (double.IsNaN(v)) continue;
            double x = n == 1 ? w / 2 : i * w / (n - 1.0);
            pts.Add(new Point(x, mapY(v)));
        }
        if (pts.Count < 2) return;

        if (fill is not null)
        {
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(pts[0].X, h), true, true);
                ctx.LineTo(pts[0], true, false);
                for (int i = 1; i < pts.Count; i++) ctx.LineTo(pts[i], true, false);
                ctx.LineTo(new Point(pts[^1].X, h), true, false);
            }
            geo.Freeze();
            dc.DrawGeometry(fill, null, geo);
        }

        var pen = new Pen(stroke, thickness) { LineJoin = PenLineJoin.Round };
        if (dash is not null) pen.DashStyle = dash;

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(pts[0], false, false);
            for (int i = 1; i < pts.Count; i++) ctx.LineTo(pts[i], true, false);
        }
        line.Freeze();
        dc.DrawGeometry(null, pen, line);

        // 末端点
        var last = pts[^1];
        dc.DrawEllipse(stroke, null, last, 2.5, 2.5);
    }
}
