namespace AirScope.Core;

/// <summary>A period when the radio was (or was probably) off-channel scanning.</summary>
/// <param name="Own">True for scans AirScope asked for (exact start/end). False for scans Windows or another app ran,
/// which we only learn about when they finish, so their start is an estimate.</param>
public readonly record struct ScanMark(DateTime StartUtc, DateTime EndUtc, bool Own);

/// <summary>
/// Thread-safe record of recent scans. While the adapter scans it leaves your channel for a few hundred
/// milliseconds at a time, so RSSI readings glitch, queued frames get retried and latency spikes.
/// Knowing when that happened lets the UI shade those moments and keep them out of the stats, instead of
/// presenting them as problems with the Wi-Fi link.
/// Not every scan is visible: roaming scans the driver/firmware runs on its own raise no notification.
/// </summary>
public sealed class ScanTimeline
{
    /// <summary>After a scan finishes, rate adaptation and the RSSI average take a moment to settle.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan MinLead = TimeSpan.FromSeconds(3), MaxLead = TimeSpan.FromSeconds(8);
    /// <summary>An own scan with no completion after this long is treated as finished.</summary>
    private static readonly TimeSpan OwnMax = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Keep = TimeSpan.FromMinutes(31);
    private static readonly TimeSpan Dedupe = TimeSpan.FromSeconds(2);

    private readonly object _lock = new();
    private readonly List<ScanMark> _marks = new();   // ordered by EndUtc
    private readonly Queue<double> _ownDurations = new();
    private DateTime? _ownStart;
    private DateTime _lastEndUtc = DateTime.MinValue;
    private TimeSpan _lead = MinLead;

    /// <summary>
    /// How long before its completion notification a Windows/other-app scan probably started. Learned from the
    /// 90th-percentile duration of AirScope's own scans on this adapter (3–8 s).
    /// </summary>
    public TimeSpan ExternalLead { get { lock (_lock) return _lead; } }

    public DateTime? LastEndUtc
    {
        get { lock (_lock) return _lastEndUtc == DateTime.MinValue ? null : _lastEndUtc; }
    }

    public void Clear()
    {
        lock (_lock) { _marks.Clear(); _ownStart = null; _lastEndUtc = DateTime.MinValue; }
    }

    /// <summary>Call immediately before asking the driver to scan. Returns a token for <see cref="OwnCancelled"/>.</summary>
    public DateTime OwnStarted(DateTime utc)
    {
        lock (_lock)
        {
            ExpireLocked(utc);
            _ownStart = utc;
            return utc;
        }
    }

    /// <summary>The scan request was refused, so the radio never left the channel.</summary>
    public void OwnCancelled(DateTime token)
    {
        lock (_lock) if (_ownStart == token) _ownStart = null;
    }

    /// <summary>Close our scan if no completion arrived (no-op if it already closed).</summary>
    public void OwnTimedOut(DateTime token, DateTime utc)
    {
        lock (_lock)
        {
            if (_ownStart is not DateTime s || s != token) return;   // already closed, or a newer scan's
            AddLocked(new ScanMark(s, utc, true), learn: false);
            _ownStart = null;
        }
    }

    /// <summary>Windows reported a scan finished (ours or anyone else's). Called on the WLAN notification thread.</summary>
    public void Completed(DateTime utc)
    {
        lock (_lock)
        {
            ExpireLocked(utc);
            if (_ownStart is DateTime s)
            {
                AddLocked(new ScanMark(s, utc, true), learn: true);
                _ownStart = null;
            }
            else if (utc - _lastEndUtc > Dedupe)
            {
                // Someone else's scan (Windows background scan, the Wi-Fi flyout, another app).
                AddLocked(new ScanMark(utc - _lead, utc, false), learn: false);
            }
        }
    }

    /// <summary>A lost completion must not leave a scan "open" for minutes.</summary>
    private void ExpireLocked(DateTime now)
    {
        if (_ownStart is DateTime s && now - s > OwnMax)
        {
            AddLocked(new ScanMark(s, s + OwnMax, true), learn: false);
            _ownStart = null;
        }
    }

    private void AddLocked(ScanMark m, bool learn)
    {
        _marks.Add(m);
        if (m.EndUtc > _lastEndUtc) _lastEndUtc = m.EndUtc;
        int drop = 0;
        while (drop < _marks.Count && m.EndUtc - _marks[drop].EndUtc > Keep) drop++;
        if (drop > 0) _marks.RemoveRange(0, drop);

        if (learn)
        {
            double d = (m.EndUtc - m.StartUtc).TotalSeconds;
            if (d > 0 && d < OwnMax.TotalSeconds)
            {
                _ownDurations.Enqueue(d);
                while (_ownDurations.Count > 20) _ownDurations.Dequeue();
                var sorted = _ownDurations.OrderBy(x => x).ToList();
                double p90 = sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(sorted.Count * 0.9) - 1)];
                _lead = TimeSpan.FromSeconds(Math.Clamp(p90, MinLead.TotalSeconds, MaxLead.TotalSeconds));
            }
        }
    }

    /// <summary>True while one of our own scans is running (or just finished and is still settling).</summary>
    public bool OwnActive(DateTime nowUtc)
    {
        lock (_lock)
        {
            ExpireLocked(nowUtc);
            if (_ownStart != null) return true;
            for (int i = _marks.Count - 1; i >= 0; i--)
            {
                var m = _marks[i];
                if (m.EndUtc + Settle < nowUtc) break;
                if (m.Own) return true;
            }
            return false;
        }
    }

    /// <summary>Does any scan (plus its settle time) overlap [from, to]?</summary>
    public bool Overlaps(DateTime from, DateTime to)
    {
        lock (_lock)
        {
            ExpireLocked(DateTime.UtcNow);
            if (_ownStart is DateTime s && s <= to) return true;
            for (int i = _marks.Count - 1; i >= 0; i--)
            {
                var m = _marks[i];
                if (m.EndUtc + Settle < from) break;   // ordered by end, so nothing earlier can overlap
                if (m.StartUtc <= to) return true;
            }
            return false;
        }
    }

    /// <summary>Copy marks that end after <paramref name="sinceUtc"/>; an in-progress own scan is included, ending now.</summary>
    public void CopySince(DateTime sinceUtc, DateTime nowUtc, List<ScanMark> into)
    {
        into.Clear();
        lock (_lock)
        {
            ExpireLocked(nowUtc);
            int i = _marks.Count;
            while (i > 0 && _marks[i - 1].EndUtc + Settle >= sinceUtc) i--;   // walk back only as far as needed
            for (; i < _marks.Count; i++) into.Add(_marks[i]);
            if (_ownStart is DateTime s) into.Add(new ScanMark(s, nowUtc, true));
        }
    }

    /// <summary>
    /// Keep only items whose time is outside every mark. Items must be in time order; runs in O(n + m).
    /// </summary>
    public static void ExcludeCovered<T>(IReadOnlyList<T> items, Func<T, DateTime> time, IReadOnlyList<ScanMark> marks, List<T> into)
    {
        into.Clear();
        // Marks are ordered by end; the sweep needs them by start. They're almost always already in that order.
        bool sorted = true;
        for (int i = 1; i < marks.Count && sorted; i++) sorted = marks[i - 1].StartUtc <= marks[i].StartUtc;
        var byStart = sorted ? marks : marks.OrderBy(m => m.StartUtc).ToList();
        int k = 0;
        DateTime coveredUntil = DateTime.MinValue;
        foreach (var it in items)
        {
            var t = time(it);
            while (k < byStart.Count && byStart[k].StartUtc <= t)
            {
                var end = byStart[k].EndUtc + Settle;
                if (end > coveredUntil) coveredUntil = end;
                k++;
            }
            if (t > coveredUntil) into.Add(it);
        }
    }
}
