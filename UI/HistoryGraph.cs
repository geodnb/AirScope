using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace AirScope.UI;

public sealed record HistorySeries(string Name, Color Color, IReadOnlyList<(DateTime utc, int rssi)> Points, bool Bold);

/// <summary>RSSI of several networks over time (from the scan loop).</summary>
public sealed class HistoryGraph : FrameworkElement
{
    private IReadOnlyList<HistorySeries> _series = Array.Empty<HistorySeries>();
    public double WindowMinutes { get; set; } = 10;
    private Point? _mouse;

    public HistoryGraph() { ClipToBounds = true; }

    public void SetSeries(IReadOnlyList<HistorySeries> s) { _series = s; InvalidateVisual(); }

    protected override void OnMouseMove(MouseEventArgs e) { _mouse = e.GetPosition(this); InvalidateVisual(); }
    protected override void OnMouseLeave(MouseEventArgs e) { _mouse = null; InvalidateVisual(); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 80 || h < 60) return;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        double legendW = Math.Min(220, w * 0.28);
        var plot = new Rect(44, 10, Math.Max(1, w - 44 - legendW - 16), Math.Max(1, h - 10 - 24));
        var now = DateTime.UtcNow;
        var start = now.AddMinutes(-WindowMinutes);
        double X(DateTime t) => plot.Left + (t - start).TotalSeconds / (WindowMinutes * 60) * plot.Width;
        double Y(double d) => plot.Bottom - (d + 100) / 75.0 * plot.Height;   // −100…−25

        var grid = Palette.P(Palette.Grid, 1);
        for (int d = -90; d <= -30; d += 10)
        {
            double y = Y(d);
            dc.DrawLine(grid, new Point(plot.Left, y), new Point(plot.Right, y));
            var ft = Palette.Text_(d.ToString(), 10, Palette.Muted, dpi);
            dc.DrawText(ft, new Point(plot.Left - ft.Width - 6, y - ft.Height / 2));
        }
        double stepMin = WindowMinutes <= 5 ? 1 : WindowMinutes <= 15 ? 2 : 5;
        for (double m = 0; m <= WindowMinutes + 0.01; m += stepMin)
        {
            double x = plot.Right - m / WindowMinutes * plot.Width;
            dc.DrawLine(grid, new Point(x, plot.Top), new Point(x, plot.Bottom));
            var ft = Palette.Text_(m == 0 ? "now" : $"-{m:0}m", 10, Palette.Muted, dpi);
            dc.DrawText(ft, new Point(Math.Clamp(x - ft.Width / 2, plot.Left, plot.Right - ft.Width), plot.Bottom + 4));
        }
        dc.DrawRectangle(null, Palette.P(Palette.Line, 1), plot);

        if (_series.Count == 0)
        {
            var ft = Palette.Text_("Select networks on the Networks tab, or wait for a few scans", 13, Palette.Muted, dpi);
            dc.DrawText(ft, new Point(plot.Left + (plot.Width - ft.Width) / 2, plot.Top + plot.Height / 2 - 10));
            return;
        }

        dc.PushClip(new RectangleGeometry(plot));
        foreach (var s in _series)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                bool open = false; DateTime prev = default;
                foreach (var (t, r) in s.Points)
                {
                    if (t < start.AddSeconds(-30)) continue;
                    var p = new Point(X(t), Y(r));
                    if (!open || (t - prev).TotalSeconds > 60) { c.BeginFigure(p, false, false); open = true; }
                    else c.LineTo(p, true, true);
                    prev = t;
                }
            }
            g.Freeze();
            dc.DrawGeometry(null, Palette.P(s.Color, s.Bold ? 2.6 : 1.6, 0.95), g);
            if (s.Points.Count > 0)
            {
                var (lt, lr) = s.Points[^1];
                dc.DrawEllipse(Palette.B(s.Color), null, new Point(X(lt), Y(lr)), 3, 3);
            }
        }
        dc.Pop();

        // Legend with latest value
        double ly = plot.Top;
        foreach (var s in _series.Take(18))
        {
            dc.DrawRoundedRectangle(Palette.B(s.Color), null, new Rect(plot.Right + 16, ly + 4, 10, 10), 2, 2);
            string latest = s.Points.Count > 0 ? $"{s.Points[^1].rssi}" : "–";
            var ft = Palette.Text_($"{latest,4}  {s.Name}", 11, s.Bold ? Palette.Text : Palette.Muted, dpi, s.Bold);
            ft.MaxTextWidth = Math.Max(10, legendW - 16);
            ft.MaxLineCount = 1;
            ft.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(ft, new Point(plot.Right + 32, ly + 1));
            ly += 19;
        }

        if (_mouse is Point mp && plot.Contains(mp))
        {
            var mt = start.AddSeconds((mp.X - plot.Left) / plot.Width * WindowMinutes * 60);
            dc.DrawLine(Palette.P(Palette.Muted, 1, 0.6, DashStyles.Dot), new Point(mp.X, plot.Top), new Point(mp.X, plot.Bottom));
            var lines = new List<string> { mt.ToLocalTime().ToString("HH:mm:ss") };
            foreach (var s in _series.Take(10))
            {
                (DateTime t, int r)? best = null; double bd = double.MaxValue;
                foreach (var p in s.Points) { double d = Math.Abs((p.utc - mt).TotalSeconds); if (d < bd) { bd = d; best = p; } }
                if (best is { } b && bd < 30) lines.Add($"{b.r,4} dBm  {s.Name}");
            }
            if (lines.Count > 1) LiveGraph.DrawTip(dc, string.Join("\n", lines), mp, plot, dpi);
        }
    }
}
