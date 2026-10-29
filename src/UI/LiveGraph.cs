using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AirScope.Core;

namespace AirScope.UI;

/// <summary>
/// Real-time RSSI (and optional link-rate) graph. The x-axis is wall-clock time and the view
/// is re-rendered every frame (capped at ~60 fps), so it scrolls smoothly between samples
/// instead of jumping once per poll – the main cause of the "laggy" feel in other tools.
/// Long windows are decimated into time-aligned buckets (stable, no shimmer) and draw a
/// faint min/max envelope so brief drops are still visible.
/// Scan periods are shaded so off-channel glitches aren't mistaken for signal problems, and when router
/// ping is on a thin strip along the bottom shows each ping's latency.
/// </summary>
public sealed class LiveGraph : FrameworkElement
{
    public SampleBuffer? Buffer { get; set; }
    public ScanTimeline? Scans { get; set; }
    public RouterPinger? Pings { get; set; }
    /// <summary>Latency at which a ping counts as a spike (red); set by the window so card, strip and events agree.</summary>
    public double PingSpikeMs { get; set; } = 50;
    public double WindowSeconds { get; set; } = 120;
    public bool ShowRates { get; set; } = true;
    public bool Paused { get; private set; }

    private readonly List<LinkSample> _work = new();
    private readonly List<ScanMark> _marks = new();
    private readonly List<PingSample> _pings = new();
    private sbyte[] _pingCols = Array.Empty<sbyte>();
    private DateTime _frozenNow;
    private TimeSpan _lastRender;
    private DateTime _lastDrawnNow;
    private Point? _mouse;
    private bool _dirty = true;
    private double _rateMax = 600;
    private bool _hooked;
    private Window? _host;

    private static readonly Pen GridPen = Palette.P(Palette.Grid, 1);
    private static readonly Pen AxisPen = Palette.P(Palette.Line, 1);
    private static readonly Pen RssiPen = Palette.P(Palette.Accent, 2);
    private static readonly Pen TxPen = Palette.P(Palette.Accent2, 1.4, 0.9);
    private static readonly Pen RxPen = Palette.P(Palette.Rx, 1.2, 0.75, DashStyles.Dash);
    private static readonly Pen CrossPen = Palette.P(Palette.Muted, 1, 0.6, DashStyles.Dot);
    private static readonly Brush AreaBrush = MakeArea();
    private static readonly Brush EnvelopeBrush = Palette.B(Palette.Accent, 0.16);
    private static readonly Brush HeadHalo = Palette.B(Palette.Accent, 0.25);
    private static readonly Brush HeadDot = Palette.B(Palette.Accent);
    private static readonly Brush CrossDot = Palette.B(Palette.Text);
    private static readonly Brush OwnScanBrush = Palette.B(Palette.Accent2, 0.10);
    private static readonly Brush ExtScanBrush = Palette.B(Palette.Fair, 0.07);
    private static readonly Pen ExtScanPen = Palette.P(Palette.Fair, 1, 0.35, DashStyles.Dot);
    private static readonly Brush PingGood = Palette.B(Palette.Good, 0.55);
    private static readonly Brush PingFair = Palette.B(Palette.Fair, 0.85);
    private static readonly Brush PingBad = Palette.B(Palette.Bad);
    private const double PingStrip = 5;
    private static readonly Pen SwatchPen = Palette.P(Palette.Line, 1);
    private static readonly Brush TipBg = Palette.B(Palette.Panel2, 0.96);
    private static readonly Pen TipPen = Palette.P(Palette.Line, 1);
    private static readonly (double lo, double hi, Brush b)[] Bands =
    {
        (-55, 0, Palette.B(Palette.Good, 0.045)),
        (-67, -55, Palette.B(Palette.C("#A3E635"), 0.045)),
        (-75, -67, Palette.B(Palette.Fair, 0.045)),
        (-82, -75, Palette.B(Palette.Weak, 0.045)),
        (-200, -82, Palette.B(Palette.Bad, 0.045)),
    };

    private static Brush MakeArea()
    {
        var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        g.GradientStops.Add(new GradientStop(Color.FromArgb(90, Palette.Accent.R, Palette.Accent.G, Palette.Accent.B), 0));
        g.GradientStops.Add(new GradientStop(Color.FromArgb(0, Palette.Accent.R, Palette.Accent.G, Palette.Accent.B), 1));
        g.Freeze();
        return g;
    }

    public LiveGraph()
    {
        ClipToBounds = true;
        Loaded += (_, _) =>
        {
            if (!_hooked) { CompositionTarget.Rendering += OnFrame; _hooked = true; }
            _host = Window.GetWindow(this);
        };
        Unloaded += (_, _) =>
        {
            if (_hooked) { CompositionTarget.Rendering -= OnFrame; _hooked = false; }
        };
        IsVisibleChanged += (_, _) => { _dirty = true; InvalidateVisual(); };
        SizeChanged += (_, _) => _dirty = true;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (!IsVisible || Paused || _host?.WindowState == WindowState.Minimized) return;
        var t = ((RenderingEventArgs)e).RenderingTime;
        // Full frame rate only when someone's looking: behind a game, ~12 fps is plenty and cheaper.
        double minGapMs = _host?.IsActive == false ? 80 : 15;
        if ((t - _lastRender).TotalMilliseconds < minGapMs) return;
        // Skip frames where the plot hasn't scrolled by at least half a pixel and nothing else changed.
        double secPerHalfPx = WindowSeconds / Math.Max(50, ActualWidth - 60) / 2;
        if (!_dirty && (DateTime.UtcNow - _lastDrawnNow).TotalSeconds < secPerHalfPx) return;
        _lastRender = t;
        InvalidateVisual();
    }

    public void MarkDirty() => _dirty = true;

    public void TogglePause()
    {
        Paused = !Paused;
        _frozenNow = DateTime.UtcNow;
        _dirty = true;
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e) { _mouse = e.GetPosition(this); _dirty = true; if (Paused) InvalidateVisual(); }
    protected override void OnMouseLeave(MouseEventArgs e) { _mouse = null; _dirty = true; if (Paused) InvalidateVisual(); }

    private readonly record struct Bucket(DateTime T, double Avg, double Min, double Max, double Tx, double Rx);

    protected override void OnRender(DrawingContext dc)
    {
        _dirty = false;
        double w = ActualWidth, h = ActualHeight;
        if (w < 50 || h < 40) return;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));   // hit-testable

        const double padL = 44, padT = 10, padB = 22;
        double padR = ShowRates ? 52 : 12;
        var plot = new Rect(padL, padT, Math.Max(1, w - padL - padR), Math.Max(1, h - padT - padB));

        var now = Paused ? _frozenNow : DateTime.UtcNow;
        _lastDrawnNow = DateTime.UtcNow;
        var start = now.AddSeconds(-WindowSeconds);
        Buffer?.CopySince(start.AddSeconds(-2), _work);
        if (Paused) _work.RemoveAll(s => s.Utc > now);   // a true freeze: ignore samples after the pause
        if (Scans != null) { Scans.CopySince(start, now, _marks); if (Paused) _marks.RemoveAll(m => m.StartUtc > now); }
        else _marks.Clear();
        if (Pings != null) { Pings.CopySince(start.AddSeconds(-1), _pings); if (Paused) _pings.RemoveAll(p => p.Utc > now); }
        else _pings.Clear();

        // y-range: −95…−25 dBm, widened if data falls outside.
        double yMin = -95, yMax = -25;
        foreach (var s in _work) { if (s.Rssi < yMin) yMin = Math.Floor(s.Rssi / 5.0) * 5; if (s.Rssi > yMax) yMax = Math.Ceiling(s.Rssi / 5.0) * 5; }
        double Y(double dbm) => plot.Bottom - (dbm - yMin) / (yMax - yMin) * plot.Height;
        double X(DateTime t) => plot.Left + (t - start).TotalSeconds / WindowSeconds * plot.Width;

        foreach (var (lo, hi, b) in Bands)
        {
            double top = Math.Max(plot.Top, Y(Math.Min(hi, yMax))), bot = Math.Min(plot.Bottom, Y(Math.Max(lo, yMin)));
            if (bot > top) dc.DrawRectangle(b, null, new Rect(plot.Left, top, plot.Width, bot - top));
        }

        for (double d = Math.Ceiling(yMin / 10) * 10; d <= yMax; d += 10)
        {
            double y = Y(d);
            dc.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var ft = Palette.Text_($"{d:0}", 10.5, Palette.Muted, dpi);
            dc.DrawText(ft, new Point(plot.Left - ft.Width - 6, y - ft.Height / 2));
        }

        double step = WindowSeconds <= 60 ? 10 : WindowSeconds <= 180 ? 30 : WindowSeconds <= 600 ? 60 : WindowSeconds <= 1200 ? 120 : 300;
        for (double sec = 0; sec <= WindowSeconds + 0.1; sec += step)
        {
            double x = plot.Right - sec / WindowSeconds * plot.Width;
            dc.DrawLine(GridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            string lab = sec == 0 ? "now" : sec < 60 ? $"-{sec:0}s" : $"-{sec / 60:0.#}m";
            var ft = Palette.Text_(lab, 10, Palette.Muted, dpi);
            dc.DrawText(ft, new Point(Math.Clamp(x - ft.Width / 2, plot.Left, plot.Right - ft.Width), plot.Bottom + 4));
        }
        dc.DrawRectangle(null, AxisPen, plot);

        // Scan periods: the radio was off-channel, so dips/retries/latency here are the scan, not the link.
        dc.PushClip(new RectangleGeometry(plot));
        foreach (var mk in _marks)
        {
            double x0 = X(mk.StartUtc), x1 = Math.Max(x0 + 2, X(mk.EndUtc + ScanTimeline.Settle));
            if (x1 < plot.Left || x0 > plot.Right) continue;
            if (mk.Own) dc.DrawRectangle(OwnScanBrush, null, new Rect(x0, plot.Top, x1 - x0, plot.Height));
            else
            {
                dc.DrawRectangle(ExtScanBrush, null, new Rect(x0, plot.Top, x1 - x0, plot.Height));
                double xe = X(mk.EndUtc);
                dc.DrawLine(ExtScanPen, new Point(xe, plot.Top), new Point(xe, plot.Bottom));
            }
        }
        dc.Pop();

        if (_work.Count < 2)
        {
            var ft = Palette.Text_("Waiting for samples…", 13, Palette.Muted, dpi);
            dc.DrawText(ft, new Point(plot.Left + (plot.Width - ft.Width) / 2, plot.Top + (plot.Height - ft.Height) / 2));
            return;
        }

        var pts = Decimate(_work, WindowSeconds / plot.Width, out bool decimated);

        if (ShowRates)
        {
            double mx = 100;
            foreach (var p in pts) mx = Math.Max(mx, Math.Max(p.Tx, p.Rx));
            double nice = NiceCeil(mx * 1.1);
            if (!Paused) _rateMax += (nice - _rateMax) * 0.15;   // ease axis changes
            for (int i = 0; i <= 4; i++)
            {
                double v = _rateMax * i / 4;
                double y = plot.Bottom - i / 4.0 * plot.Height;
                var ft = Palette.Text_(v >= 1000 ? $"{v / 1000:0.#}G" : $"{v:0}", 10, Palette.Accent2, dpi, alpha: 0.8);
                dc.DrawText(ft, new Point(plot.Right + 6, y - ft.Height / 2));
            }
        }

        dc.PushClip(new RectangleGeometry(plot));
        double maxGap = Math.Max(3, WindowSeconds / plot.Width * 4);   // break lines on real gaps (disconnects)

        if (decimated)
        {
            var env = new StreamGeometry();
            using (var g = env.Open()) Envelope(g, pts, X, Y, maxGap);
            env.Freeze();
            dc.DrawGeometry(EnvelopeBrush, null, env);
        }

        var area = new StreamGeometry();
        var line = new StreamGeometry();
        using (var ga = area.Open())
        using (var gl = line.Open())
        {
            bool open = false; Point prev = default; DateTime prevT = default;
            foreach (var b in pts)
            {
                var p = new Point(X(b.T), Y(b.Avg));
                if (open && (b.T - prevT).TotalSeconds > maxGap)
                {
                    ga.LineTo(new Point(prev.X, plot.Bottom), false, false);
                    open = false;
                }
                if (!open)
                {
                    ga.BeginFigure(new Point(p.X, plot.Bottom), true, true);
                    ga.LineTo(p, false, false);
                    gl.BeginFigure(p, false, false);
                    open = true;
                }
                else { ga.LineTo(p, false, false); gl.LineTo(p, true, true); }
                prev = p; prevT = b.T;
            }
            if (open) ga.LineTo(new Point(prev.X, plot.Bottom), false, false);
        }
        area.Freeze(); line.Freeze();
        dc.DrawGeometry(AreaBrush, null, area);

        if (ShowRates && _rateMax > 0)
        {
            double RY(double v) => plot.Bottom - v / _rateMax * plot.Height;
            DrawSeries(dc, pts, b => RY(b.Rx), X, RxPen, maxGap);
            DrawSeries(dc, pts, b => RY(b.Tx), X, TxPen, maxGap);
        }
        dc.DrawGeometry(null, RssiPen, line);
        DrawPingStrip(dc, plot, X);

        var last = _work[^1];
        if ((now - last.Utc).TotalSeconds < 3)
        {
            var hp = new Point(X(last.Utc), Y(last.Rssi));
            dc.DrawEllipse(HeadHalo, null, hp, 7, 7);
            dc.DrawEllipse(HeadDot, null, hp, 3.5, 3.5);
        }
        dc.Pop();

        // Hover crosshair + readout (raw samples, not decimated)
        if (_mouse is Point m && plot.Contains(m))
        {
            var mt = start.AddSeconds((m.X - plot.Left) / plot.Width * WindowSeconds);
            int bi = NearestIndex(_work, mt);
            if (bi >= 0 && Math.Abs((_work[bi].Utc - mt).TotalSeconds) < Math.Max(3, WindowSeconds / plot.Width * 3))
            {
                var b = _work[bi];
                double x = X(b.Utc);
                dc.DrawLine(CrossPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
                dc.DrawEllipse(CrossDot, null, new Point(x, Y(b.Rssi)), 3, 3);
                string txt = $"{b.Utc.ToLocalTime():HH:mm:ss.f}\n{b.Rssi} dBm   ↑{b.TxMbps:0}  ↓{b.RxMbps:0} Mbps" +
                             (b.RetryPct is double r ? $"\nretries {r:0.0}% of {b.TxFrames} frames" : "");
                if (NearestPing(b.Utc) is PingSample ps) txt += ps.Ms is double pm ? $"\nrouter ping {PingStats.Ms(pm)} ms" : "\nrouter ping lost";
                var scan = ScanAt(b.Utc);
                if (scan is ScanMark sm) txt += sm.Own ? "\n(during an AirScope scan)" : "\n(around a Windows/other-app scan)";
                DrawTip(dc, txt, new Point(x, Y(b.Rssi)), plot, dpi);
            }
        }

        double lx = plot.Left + 8, ly = plot.Top + 6;
        LegendItem(dc, ref lx, ly, RssiPen, "RSSI (dBm)", dpi);
        if (ShowRates)
        {
            LegendItem(dc, ref lx, ly, TxPen, "Tx rate", dpi);
            LegendItem(dc, ref lx, ly, RxPen, "Rx rate", dpi);
        }
        if (_marks.Any(m => m.Own)) SwatchItem(dc, ref lx, ly, OwnScanBrush, "AirScope scan", dpi);
        if (_marks.Any(m => !m.Own)) SwatchItem(dc, ref lx, ly, ExtScanBrush, "Windows scan", dpi);
        if (_pings.Count > 0) SwatchItem(dc, ref lx, ly, PingGood, "ping (bottom strip)", dpi);
        if (Paused)
        {
            var ft = Palette.Text_("PAUSED", 11, Palette.Fair, dpi, true);
            dc.DrawText(ft, new Point(plot.Right - ft.Width - 8, plot.Top + 6));
        }
    }

    /// <summary>
    /// Router pings as a strip along the bottom: green under 20 ms, amber up to the spike threshold, red above it
    /// or lost. One cell per pixel column showing the worst ping in it, so 30-minute windows stay cheap to draw.
    /// </summary>
    private void DrawPingStrip(DrawingContext dc, Rect plot, Func<DateTime, double> X)
    {
        if (_pings.Count == 0) return;
        int cols = Math.Max(1, (int)Math.Ceiling(plot.Width));
        if (_pingCols.Length != cols) _pingCols = new sbyte[cols];
        Array.Fill(_pingCols, (sbyte)-1);
        double pxPerPing = 0.5 / WindowSeconds * plot.Width;   // a ping covers ~0.5 s
        foreach (var p in _pings)
        {
            sbyte level = p.Ms is not double ms ? (sbyte)2 : ms >= PingSpikeMs ? (sbyte)2 : ms >= 20 ? (sbyte)1 : (sbyte)0;
            int c0 = (int)Math.Floor(X(p.Utc) - plot.Left), c1 = (int)Math.Floor(X(p.Utc) - plot.Left + Math.Max(1, pxPerPing));
            for (int c = Math.Max(0, c0); c <= Math.Min(cols - 1, c1); c++) if (level > _pingCols[c]) _pingCols[c] = level;
        }
        double y = plot.Bottom - PingStrip;
        int run = 0;
        for (int c = 1; c <= cols; c++)
        {
            if (c < cols && _pingCols[c] == _pingCols[run]) continue;
            sbyte lv = _pingCols[run];
            if (lv >= 0)
            {
                var b = lv == 0 ? PingGood : lv == 1 ? PingFair : PingBad;
                double h = lv == 2 ? PingStrip + 3 : PingStrip;
                dc.DrawRectangle(b, null, new Rect(plot.Left + run, plot.Bottom - h, c - run, h));
            }
            run = c;
        }
    }

    private ScanMark? ScanAt(DateTime t)
    {
        foreach (var m in _marks) if (t >= m.StartUtc && t <= m.EndUtc + ScanTimeline.Settle) return m;
        return null;
    }

    private PingSample? NearestPing(DateTime t)
    {
        PingSample? best = null; double bd = 0.8;
        foreach (var p in _pings) { double d = Math.Abs((p.Utc - t).TotalSeconds); if (d < bd) { bd = d; best = p; } }
        return best;
    }

    private static void SwatchItem(DrawingContext dc, ref double x, double y, Brush b, string label, double dpi)
    {
        dc.DrawRectangle(b, SwatchPen, new Rect(x, y + 2, 12, 10));
        var ft = Palette.Text_(label, 10.5, Palette.Muted, dpi);
        dc.DrawText(ft, new Point(x + 16, y));
        x += 16 + ft.Width + 14;
    }

    private static int NearestIndex(List<LinkSample> s, DateTime t)
    {
        if (s.Count == 0) return -1;
        int lo = 0, hi = s.Count - 1;
        while (lo < hi) { int mid = (lo + hi) / 2; if (s[mid].Utc < t) lo = mid + 1; else hi = mid; }
        if (lo > 0 && (t - s[lo - 1].Utc) < (s[lo].Utc - t)) lo--;
        return lo;
    }

    /// <summary>Bucket samples on an absolute time grid (~1 bucket per pixel) so buckets don't shift as the view scrolls.</summary>
    private static List<Bucket> Decimate(List<LinkSample> src, double secPerPx, out bool decimated)
    {
        var res = new List<Bucket>(Math.Min(src.Count, 4096));
        double span = (src[^1].Utc - src[0].Utc).TotalSeconds;
        double avgDt = span / Math.Max(1, src.Count - 1);
        decimated = avgDt < secPerPx * 0.75;
        if (!decimated)
        {
            foreach (var s in src) res.Add(new Bucket(s.Utc, s.Rssi, s.Rssi, s.Rssi, s.TxMbps, s.RxMbps));
            return res;
        }
        long bucketTicks = Math.Max(1, (long)(secPerPx * TimeSpan.TicksPerSecond));
        int i = 0;
        while (i < src.Count)
        {
            long id = src[i].Utc.Ticks / bucketTicks;
            double sum = 0, tx = 0, rx = 0, mn = double.MaxValue, mx = double.MinValue; int n = 0;
            while (i < src.Count && src[i].Utc.Ticks / bucketTicks == id)
            {
                var s = src[i++];
                sum += s.Rssi; tx += s.TxMbps; rx += s.RxMbps; n++;
                if (s.Rssi < mn) mn = s.Rssi; if (s.Rssi > mx) mx = s.Rssi;
            }
            res.Add(new Bucket(new DateTime(id * bucketTicks + bucketTicks / 2, DateTimeKind.Utc), sum / n, mn, mx, tx / n, rx / n));
        }
        return res;
    }

    private static void Envelope(StreamGeometryContext g, List<Bucket> pts, Func<DateTime, double> x, Func<double, double> y, double maxGap)
    {
        int segStart = 0;
        for (int i = 1; i <= pts.Count; i++)
        {
            if (i == pts.Count || (pts[i].T - pts[i - 1].T).TotalSeconds > maxGap)
            {
                if (i - segStart >= 2)
                {
                    g.BeginFigure(new Point(x(pts[segStart].T), y(pts[segStart].Max)), true, true);
                    for (int k = segStart + 1; k < i; k++) g.LineTo(new Point(x(pts[k].T), y(pts[k].Max)), false, false);
                    for (int k = i - 1; k >= segStart; k--) g.LineTo(new Point(x(pts[k].T), y(pts[k].Min)), false, false);
                }
                segStart = i;
            }
        }
    }

    private static void DrawSeries(DrawingContext dc, List<Bucket> src, Func<Bucket, double> y, Func<DateTime, double> x, Pen pen, double maxGap)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            bool open = false; DateTime prev = default;
            foreach (var s in src)
            {
                var p = new Point(x(s.T), y(s));
                if (!open || (s.T - prev).TotalSeconds > maxGap) { c.BeginFigure(p, false, false); open = true; }
                else c.LineTo(p, true, true);
                prev = s.T;
            }
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }

    private static void LegendItem(DrawingContext dc, ref double x, double y, Pen pen, string label, double dpi)
    {
        dc.DrawLine(pen, new Point(x, y + 7), new Point(x + 16, y + 7));
        var ft = Palette.Text_(label, 10.5, Palette.Muted, dpi);
        dc.DrawText(ft, new Point(x + 20, y));
        x += 20 + ft.Width + 14;
    }

    internal static void DrawTip(DrawingContext dc, string text, Point anchor, Rect bounds, double dpi)
    {
        var ft = Palette.Text_(text, 11.5, Palette.Text, dpi);
        var size = new Size(ft.Width + 16, ft.Height + 10);
        double x = anchor.X + 12, y = anchor.Y - size.Height - 10;
        if (x + size.Width > bounds.Right) x = anchor.X - size.Width - 12;
        x = Math.Max(bounds.Left, x);
        if (y < bounds.Top) y = anchor.Y + 12;
        if (y + size.Height > bounds.Bottom) y = bounds.Bottom - size.Height;
        y = Math.Max(bounds.Top, y);
        var r = new Rect(x, y, size.Width, size.Height);
        dc.DrawRoundedRectangle(TipBg, TipPen, r, 6, 6);
        dc.DrawText(ft, new Point(r.X + 8, r.Y + 5));
    }

    internal static double NiceCeil(double v)
    {
        if (v <= 0) return 1;
        double mag = Math.Pow(10, Math.Floor(Math.Log10(v)));
        double n = v / mag;
        double nice = n <= 1 ? 1 : n <= 2 ? 2 : n <= 2.5 ? 2.5 : n <= 5 ? 5 : 10;
        return nice * mag;
    }
}
