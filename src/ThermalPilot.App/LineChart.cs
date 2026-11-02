using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;

namespace ThermalPilot.App;

/// <summary>一条曲线数据（定长环形缓冲，NaN 表示空位）。</summary>
public sealed class ChartSeries
{
    public string Name { get; init; } = "";
    public Color Color { get; init; } = Colors.White;
    public double[] Values { get; init; } = Array.Empty<double>();
    public bool Dashed { get; init; }
}

/// <summary>轻量折线图（自绘，无第三方依赖），横向网格 + 图例 + 多曲线。</summary>
public sealed class LineChart : FrameworkElement
{
    private static readonly Brush GridBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x22, 0x2C, 0x39)));
    private static readonly Brush LabelBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x7D, 0x8A, 0x9A)));

    private ChartSeries[] _series = Array.Empty<ChartSeries>();
    private double _min;
    private double _max = 100;
    private string _unit = "°C";

    public void SetData(double min, double max, string unit, params ChartSeries[] series)
    {
        _min = min;
        _max = max <= min ? min + 1 : max;
        _unit = unit;
        _series = series ?? Array.Empty<ChartSeries>();
        InvalidateVisual();
    }

    private static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 40 || h < 40) return;

        const double left = 38, right = 10, top = 26, bottom = 16;
        var plot = new Rect(left, top, Math.Max(10, w - left - right), Math.Max(10, h - top - bottom));

        var gridPen = new Pen(GridBrush, 1);
        gridPen.Freeze();

        for (int i = 0; i <= 4; i++)
        {
            double y = plot.Top + plot.Height * i / 4.0;
            dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            double val = _max - (_max - _min) * i / 4.0;
            var ft = Text(val.ToString("F0"), LabelBrush, 10);
            dc.DrawText(ft, new Point(plot.Left - ft.Width - 6, y - ft.Height / 2));
        }

        var ftUnit = Text(_unit, LabelBrush, 10);
        dc.DrawText(ftUnit, new Point(6, plot.Top - 16));

        double x0 = 6;
        foreach (var s in _series)
        {
            var pen = new Pen(new SolidColorBrush(s.Color), 1.7)
            {
                LineJoin = PenLineJoin.Round,
                DashStyle = s.Dashed ? new DashStyle(new double[] { 4, 3 }, 0) : DashStyles.Solid
            };
            pen.Freeze();

            var vals = s.Values;
            if (vals.Length < 2) continue;

            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                bool started = false;
                for (int i = 0; i < vals.Length; i++)
                {
                    double v = vals[i];
                    if (double.IsNaN(v))
                    {
                        started = false;
                        continue;
                    }
                    double px = plot.Left + plot.Width * i / (vals.Length - 1.0);
                    double t = Math.Clamp((v - _min) / (_max - _min), 0, 1);
                    double py = plot.Bottom - t * plot.Height;
                    var p = new Point(px, py);
                    if (!started)
                    {
                        ctx.BeginFigure(p, false, false);
                        started = true;
                    }
                    else ctx.LineTo(p, true, false);
                }
            }
            geo.Freeze();
            dc.DrawGeometry(null, pen, geo);

            // 图例
            var dot = new SolidColorBrush(s.Color);
            dot.Freeze();
            dc.DrawEllipse(dot, null, new Point(x0 + 4, 10), 4, 4);
            var ftName = Text(s.Name, LabelBrush, 10.5);
            dc.DrawText(ftName, new Point(x0 + 12, 10 - ftName.Height / 2));
            x0 += 12 + ftName.Width + 14;
        }
    }

    private FormattedText Text(string text, Brush brush, double size) =>
        new(text, CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
}
