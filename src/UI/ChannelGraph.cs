using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AirScope.Core;

namespace AirScope.UI;

/// <summary>Spectrum-style view: each AP is drawn across the full width it occupies, height = signal.</summary>
public sealed class ChannelGraph : FrameworkElement
{
    public WifiBand Band { get; set; } = WifiBand.Band24;
    private IReadOnlyList<BssInfo> _nets = Array.Empty<BssInfo>();
    private HashSet<ulong> _highlight = new();
    private ulong _connected;
    private Point? _mouse;
    private Rect _plot;

    public event Action<BssInfo>? NetworkClicked;

    public ChannelGraph()
    {
        ClipToBounds = true;
        Cursor = Cursors.Arrow;
    }

    public void SetData(IReadOnlyList<BssInfo> all, ulong connectedKey, HashSet<ulong> highlight)
    {
        _nets = all.Where(b => b.Band == Band).OrderBy(b => b.Rssi).ToList();   // strongest drawn last
        _connected = connectedKey;
        _highlight = highlight;
        InvalidateVisual();
    }

    public int Count => _nets.Count;

    protected override void OnMouseMove(MouseEventArgs e) { _mouse = e.GetPosition(this); InvalidateVisual(); }
    protected override void OnMouseLeave(MouseEventArgs e) { _mouse = null; InvalidateVisual(); }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        var hit = HitTest(e.GetPosition(this)).FirstOrDefault();
        if (hit != null) NetworkClicked?.Invoke(hit);
    }

    private (double lo, double hi) _span;
    private double X(double mhz) => _plot.Left + (mhz - _span.lo) / (_span.hi - _span.lo) * _plot.Width;
    private double Y(double dbm) => _plot.Bottom - (dbm + 100) / 80.0 * _plot.Height;   // −100…−20

    private IEnumerable<BssInfo> HitTest(Point p)
    {
        if (!_plot.Contains(p)) yield break;
        foreach (var b in _nets.Reverse())
        {
            double x0 = X(b.LowFreq), x1 = X(b.HighFreq);
            if (p.X >= x0 && p.X <= x1 && p.Y >= Y(b.Rssi) - 2) yield return b;
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 80 || h < 60) return;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        _span = ChannelMath.BandSpan(Band);
        _plot = new Rect(40, 8, Math.Max(1, w - 52), Math.Max(1, h - 8 - 24));
        var gridPen = Palette.P(Palette.Grid, 1);

        // Regulatory shading
        if (Band == WifiBand.Band5)
        {
            Shade(dc, 5250, 5350, Palette.Fair, 0.05, "DFS", dpi);
            Shade(dc, 5470, 5725, Palette.Fair, 0.05, "DFS", dpi);
            Shade(dc, 5590, 5650, Palette.Weak, 0.07, "Weather radar", dpi);
            Shade(dc, 5350, 5470, Palette.Bg, 0.6, "", dpi);
        }
        else if (Band == WifiBand.Band6)
        {
            Shade(dc, 6425, 7125, Palette.Muted, 0.05, "Not authorised for Wi-Fi in the UK above 6425 MHz (Ofcom is consulting on 6425–6585)", dpi);
        }

        for (int d = -90; d <= -30; d += 10)
        {
            double y = Y(d);
            dc.DrawLine(gridPen, new Point(_plot.Left, y), new Point(_plot.Right, y));
            var ft = Palette.Text_(d.ToString(), 10, Palette.Muted, dpi);
            dc.DrawText(ft, new Point(_plot.Left - ft.Width - 6, y - ft.Height / 2));
        }

        double lastLabelRight = double.MinValue;
        foreach (var ch in ChannelMath.TickChannels(Band))
        {
            double x = X(ChannelMath.FreqFromChannel(Band, ch));
            if (x < _plot.Left - 1 || x > _plot.Right + 1) continue;
            dc.DrawLine(gridPen, new Point(x, _plot.Bottom), new Point(x, _plot.Bottom + 3));
            var ft = Palette.Text_(ch.ToString(), 10, Palette.Muted, dpi);
            if (x - ft.Width / 2 > lastLabelRight + 3)
            {
                dc.DrawText(ft, new Point(x - ft.Width / 2, _plot.Bottom + 5));
                lastLabelRight = x + ft.Width / 2;
            }
        }
        dc.DrawLine(Palette.P(Palette.Line, 1), new Point(_plot.Left, _plot.Bottom), new Point(_plot.Right, _plot.Bottom));

        if (_nets.Count == 0)
        {
            var ft = Palette.Text_($"No {ChannelMath.BandName(Band)} networks seen", 13, Palette.Muted, dpi);
            dc.DrawText(ft, new Point(_plot.Left + (_plot.Width - ft.Width) / 2, _plot.Top + _plot.Height / 2 - 10));
            return;
        }

        bool anyHighlight = _highlight.Count > 0;
        var hover = _mouse is Point mp ? HitTest(mp).FirstOrDefault() : null;

        dc.PushClip(new RectangleGeometry(new Rect(_plot.Left, 0, _plot.Width, _plot.Bottom)));
        var labelRects = new List<Rect>();
        foreach (var b in _nets)
        {
            bool mine = b.Key == _connected;
            bool hl = _highlight.Contains(b.Key) || hover == b;
            double dim = anyHighlight && !hl && !mine ? 0.25 : 1.0;
            var col = Palette.ForName(b.ColorKey);
            double x0 = X(b.LowFreq), x1 = X(b.HighFreq), yTop = Y(b.Rssi), yBot = _plot.Bottom;
            double edge = Math.Min((x1 - x0) * 0.18, 14);

            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(x0, yBot), true, true);
                c.BezierTo(new Point(x0 + edge * 0.6, yBot), new Point(x0 + edge * 0.4, yTop), new Point(x0 + edge, yTop), true, true);
                c.LineTo(new Point(x1 - edge, yTop), true, true);
                c.BezierTo(new Point(x1 - edge * 0.4, yTop), new Point(x1 - edge * 0.6, yBot), new Point(x1, yBot), true, true);
            }
            g.Freeze();
            dc.DrawGeometry(Palette.B(col, (hl ? 0.32 : 0.14) * dim),
                Palette.P(mine ? Palette.Text : col, mine ? 2.4 : hl ? 2 : 1.3, 0.95 * dim), g);

            // Label – skip if it would collide with a stronger network's label.
            string name = b.DisplaySsid + (mine ? "  ●" : "");
            var ft = Palette.Text_(name, 11, mine ? Palette.Text : col, dpi, mine || hl, dim);
            double lx = (x0 + x1) / 2 - ft.Width / 2, ly = yTop - ft.Height - 2;
            var r = new Rect(lx, ly, ft.Width, ft.Height);
            for (int tries = 0; tries < 3 && labelRects.Any(o => o.IntersectsWith(r)); tries++) r.Y -= ft.Height;
            if (!labelRects.Any(o => o.IntersectsWith(r)) && r.Top >= 0)
            {
                dc.DrawText(ft, r.TopLeft);
                labelRects.Add(r);
            }
        }
        dc.Pop();

        if (hover != null && _mouse is Point m)
        {
            var under = HitTest(m).Take(4).ToList();
            var txt = string.Join("\n\n", under.Select(b =>
                $"{b.DisplaySsid}  {b.Rssi} dBm\nch {b.Channel} · {b.WidthLabel} MHz · {b.GenLabel}\n{b.DisplayBssid}  {b.Vendor}"));
            LiveGraph.DrawTip(dc, txt, m, new Rect(0, 0, w, h), dpi);
        }
    }

    private void Shade(DrawingContext dc, double lo, double hi, Color c, double a, string label, double dpi)
    {
        double x0 = Math.Max(_plot.Left, X(lo)), x1 = Math.Min(_plot.Right, X(hi));
        if (x1 <= x0) return;
        dc.DrawRectangle(Palette.B(c, a), null, new Rect(x0, _plot.Top, x1 - x0, _plot.Height));
        if (label.Length > 0)
        {
            var ft = Palette.Text_(label, 9.5, Palette.Muted, dpi, alpha: 0.7);
            if (ft.Width < x1 - x0 - 4) dc.DrawText(ft, new Point(x0 + 4, _plot.Top + 2));
        }
    }
}
