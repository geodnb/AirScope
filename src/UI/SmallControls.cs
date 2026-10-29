using System.Windows;
using System.Windows.Media;
using AirScope.Core;

namespace AirScope.UI;

/// <summary>Four-bar signal meter for grid rows.</summary>
public sealed class SignalMeter : FrameworkElement
{
    public static readonly DependencyProperty RssiProperty = DependencyProperty.Register(
        nameof(Rssi), typeof(int), typeof(SignalMeter),
        new FrameworkPropertyMetadata(-100, FrameworkPropertyMetadataOptions.AffectsRender));

    public int Rssi { get => (int)GetValue(RssiProperty); set => SetValue(RssiProperty, value); }

    protected override Size MeasureOverride(Size s) => new(22, 14);

    protected override void OnRender(DrawingContext dc)
    {
        int bars = Rssi >= -55 ? 4 : Rssi >= -67 ? 3 : Rssi >= -75 ? 2 : Rssi >= -85 ? 1 : 0;
        var on = Palette.B(Palette.ForRssi(Rssi));
        var off = Palette.B(Palette.Line);
        for (int i = 0; i < 4; i++)
        {
            double bh = 4 + i * 3;
            dc.DrawRoundedRectangle(i < bars ? on : off, null, new Rect(i * 5.5, 14 - bh, 3.6, bh), 1, 1);
        }
    }
}

/// <summary>Tiny line chart for the mini window and the details panel.</summary>
public sealed class Sparkline : FrameworkElement
{
    private IReadOnlyList<double> _pts = Array.Empty<double>();
    public double Min { get; set; } = -95;
    public double Max { get; set; } = -25;
    public Color Stroke { get; set; } = Palette.Accent;

    public void SetPoints(IReadOnlyList<double> pts) { _pts = pts; InvalidateVisual(); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        if (_pts.Count < 2 || w < 4) return;
        double lo = Math.Min(Min, _pts.Min()), hi = Math.Max(Max, _pts.Max());
        var g = new StreamGeometry();
        var area = new StreamGeometry();
        using (var c = g.Open())
        using (var a = area.Open())
        {
            for (int i = 0; i < _pts.Count; i++)
            {
                var p = new Point(i / (double)(_pts.Count - 1) * w, h - (_pts[i] - lo) / (hi - lo) * h);
                if (i == 0) { c.BeginFigure(p, false, false); a.BeginFigure(new Point(0, h), true, true); a.LineTo(p, false, false); }
                else { c.LineTo(p, true, true); a.LineTo(p, false, false); }
            }
            a.LineTo(new Point(w, h), false, false);
        }
        g.Freeze(); area.Freeze();
        dc.DrawGeometry(Palette.B(Stroke, 0.15), null, area);
        dc.DrawGeometry(null, Palette.P(Stroke, 1.6), g);
    }
}

/// <summary>Horizontal score bar for the channel advisor.</summary>
public sealed class ScoreBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(ScoreBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    protected override Size MeasureOverride(Size s) => new(Math.Min(double.IsInfinity(s.Width) ? 140 : s.Width, 140), 8);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = Math.Min(8, ActualHeight);
        double y = (ActualHeight - h) / 2;
        dc.DrawRoundedRectangle(Palette.B(Palette.Line), null, new Rect(0, y, w, h), h / 2, h / 2);
        double v = Math.Clamp(Value, 0, 100) / 100;
        var col = v >= 0.75 ? Palette.Good : v >= 0.5 ? Palette.C("#A3E635") : v >= 0.3 ? Palette.Fair : v >= 0.15 ? Palette.Weak : Palette.Bad;
        if (v > 0) dc.DrawRoundedRectangle(Palette.B(col), null, new Rect(0, y, Math.Max(h, w * v), h), h / 2, h / 2);
    }
}

public sealed class InverseBoolConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type t, object p, System.Globalization.CultureInfo c) => value is bool b ? !b : true;
    public object ConvertBack(object value, Type t, object p, System.Globalization.CultureInfo c) => value is bool b ? !b : false;
}
