using AirScope.Interop;

namespace AirScope.Core;

/// <summary>Per-BSSID tracking across scans.</summary>
public sealed class BssTrack
{
    public BssInfo Latest { get; set; } = null!;
    public DateTime FirstSeenUtc { get; init; }
    public List<(DateTime utc, int rssi)> History { get; } = new();
}

/// <summary>
/// Runs two independent background loops:
///  • a fast link poll (RSSI / rates / retries of the current connection), default 4 Hz
///  • a scan loop for neighbouring networks. When <see cref="ActiveScanning"/> is on it asks the driver to
///    scan and waits for the OS scan-complete notification; when it's off it never takes the radio
///    off-channel and just re-reads the list Windows' own background scans keep up to date.
/// Neither loop touches the UI; consumers subscribe to the events and marshal themselves.
/// Each Start() gets its own private loop state, so a slow-to-exit old loop can never
/// corrupt a new one, and loops stop raising events as soon as they're cancelled.
/// </summary>
public sealed class WifiMonitor : IDisposable
{
    private readonly WlanClient _wlan;
    private readonly AppSettings _settings;
    private CancellationTokenSource? _cts;
    private Task? _pollTask, _scanTask;
    private readonly SemaphoreSlim _scanSignal = new(0, 1);     // a scan completed or failed (not mere list refreshes)
    private readonly SemaphoreSlim _scanKick = new(0, 1);
    private readonly object _bssLock = new();
    private readonly Dictionary<ulong, BssTrack> _bss = new();
    private static readonly TimeSpan HistoryKeep = TimeSpan.FromMinutes(30);
    private const int RemoveAfterSeconds = 180;

    private volatile object _ifaceBox = Guid.Empty;        // boxed so cross-thread reads are atomic
    private volatile ConnectionInfo _connection = new();
    private volatile bool _activeWanted;
    private int _forceScan;                                 // 1 = "Scan now" pressed
    private int _cacheDirty;                                // 1 = Windows refreshed its scan list since we last read it
    private long _liveSinceTicks;                           // graph "Clear" cut-off (UTC ticks); 0 = none
    private long _retrySeq;                                 // counter-read sequence; poll loop only
    private long _lastOwnScanTick = long.MinValue / 2;      // Environment.TickCount64 (monotonic); scan loop only
    private static readonly TimeSpan MinOwnScanGap = TimeSpan.FromSeconds(3);

    public SampleBuffer Samples { get; } = new(30 * 60 * 10 + 64); // 30 min at 10 Hz worst case
    /// <summary>When scans (ours and Windows') had the radio off-channel.</summary>
    public ScanTimeline Scans { get; } = new();

    /// <summary>
    /// Whether the scan loop may trigger scans itself. Off = passive: no off-channel time caused by AirScope,
    /// neighbour data comes from Windows' cached list. Safe to set from any thread.
    /// </summary>
    public bool ActiveScanning { get => _activeWanted; set => _activeWanted = value; }
    public Guid InterfaceId => (Guid)_ifaceBox;
    public ConnectionInfo Connection => _connection;
    /// <summary>When the adapter last finished a scan (ours or Windows'), i.e. how fresh the neighbour list is.</summary>
    public DateTime? LastScanUtc => Scans.LastEndUtc;

    public event Action<LinkSample, ConnectionInfo>? SampleTaken;
    /// <summary>(snapshot, WLAN error code). An error with an empty list means "keep showing the previous data".</summary>
    public event Action<IReadOnlyList<BssInfo>, uint>? ScanCompleted;
    public event Action<EventEntry>? EventRaised;
    public event Action<string>? StatusChanged;

    public WifiMonitor(WlanClient wlan, AppSettings settings)
    {
        _wlan = wlan;
        _settings = settings;
        _wlan.Notification += OnNotification;
    }

    public void Start(Guid iface)
    {
        Stop();
        _ifaceBox = iface;
        Samples.Clear();
        Scans.Clear();
        Interlocked.Exchange(ref _liveSinceTicks, 0);
        Interlocked.Exchange(ref _forceScan, 0);            // a "Scan now" for the old adapter doesn't carry over
        lock (_bssLock) _bss.Clear();
        _connection = new ConnectionInfo();
        _cts = new CancellationTokenSource();
        var t = _cts.Token;
        _pollTask = Task.Run(() => PollLoop(iface, new PollState(), t));
        _scanTask = Task.Run(() => ScanLoop(iface, t));
    }

    public void Stop()
    {
        if (_cts == null) return;
        _cts.Cancel();
        var tasks = new[] { _pollTask, _scanTask }.Where(x => x != null).Cast<Task>().ToArray();
        // Loops exit at their next await; only a hung driver call could make this time out, and the
        // per-run state + WlanClient's in-flight guard keep that case safe anyway.
        try { Task.WaitAll(tasks, 5000); } catch (AggregateException) { }
        _cts.Dispose();
        _cts = null;
        _pollTask = _scanTask = null;
    }

    /// <summary>
    /// Start the live picture afresh (graph "Clear"). Nothing is deleted or reset – samples, scan marks, pings and
    /// the poll loop's figures carry on for signal-drop detection, spot tests and recording – the views just ignore
    /// anything before this moment.
    /// </summary>
    public void ResetLive() => Interlocked.Exchange(ref _liveSinceTicks, DateTime.UtcNow.Ticks);

    /// <summary>Views show data from here on (the last "Clear"), or everything if never cleared.</summary>
    public DateTime LiveSinceUtc
    {
        get { long t = Interlocked.Read(ref _liveSinceTicks); return t == 0 ? DateTime.MinValue : new DateTime(t, DateTimeKind.Utc); }
    }

    /// <summary>Trigger a scan right now (even in passive mode) instead of waiting for the next interval.</summary>
    public void ScanNow()
    {
        Interlocked.Exchange(ref _forceScan, 1);
        Signal(_scanKick);
    }

    private static void Signal(SemaphoreSlim s)
    {
        try { if (s.CurrentCount == 0) s.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }

    private void OnNotification(uint source, uint code, Guid iface)
    {
        if (iface != InterfaceId || source != WlanNative.WLAN_NOTIFICATION_SOURCE_ACM) return;
        if (code == WlanNative.ACM_SCAN_COMPLETE || code == WlanNative.ACM_SCAN_FAIL)
        {
            Scans.Completed(DateTime.UtcNow);
            Interlocked.Exchange(ref _cacheDirty, 1);
            Signal(_scanSignal);
        }
        else if (code == WlanNative.ACM_SCAN_LIST_REFRESH)
            Interlocked.Exchange(ref _cacheDirty, 1);   // partial results – re-read, but the scan isn't over
    }

    // ------------------------------------------------------------------ fast link poll

    private sealed class PollState
    {
        public ConnectionInfo? PrevConn;
        public (ulong tx, ulong retry)? PrevCounters;
        public double? RetryPct, RetryAvg;
        public int RetryFrames;
        public double RetryFps, RetryAvgFps;
        public int RetryCount;
        public readonly Queue<(DateTime start, DateTime end, ulong dTx, ulong dRe)> RetryWin = new();
        public readonly List<LinkSample> Clean = new();
        public readonly List<ScanMark> Marks = new();
        public DateTime LastConnQueryUtc, LastCounterUtc, LastRssiDropUtc, LastRateDropUtc;
        public ConnectionInfo? LostConn;          // last good connection while a drop is pending
        public DateTime LostAtUtc;
        public bool DisconnectReported;
        public int? PendingChannel;               // channel change awaiting confirmation
        public DateTime PendingChannelSinceUtc;
        public int PendingFrom;
        public int LastChannel;                   // last non-zero channel, so a 0 reading can't hide a move
        public int RateLowCount;
        public double RateRef;
        public readonly List<LinkSample> Scratch = new();
    }

    private async Task PollLoop(Guid iface, PollState st, CancellationToken ct)
    {
        int currentPeriod = _settings.PollMs;
        var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(currentPeriod));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (currentPeriod != _settings.PollMs)
                {
                    timer.Dispose();
                    currentPeriod = _settings.PollMs;
                    timer = new PeriodicTimer(TimeSpan.FromMilliseconds(currentPeriod));
                }
                try
                {
                    PollOnce(iface, st, ct);
                }
                catch (ObjectDisposedException) when (_wlan.IsDisposed || ct.IsCancellationRequested) { return; }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    // One bad sample (or a throwing subscriber) must never stop the poll.
                    StatusChanged?.Invoke("Poll error: " + ex.Message);
                }
            }
        }
        catch (OperationCanceledException) { }
        finally { timer.Dispose(); }
    }

    private void PollOnce(Guid iface, PollState st, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        // Connection attributes change slowly; refresh them at ≤4 Hz.
        if ((now - st.LastConnQueryUtc).TotalMilliseconds >= 240 || st.PrevConn == null)
        {
            var c = _wlan.GetConnection(iface);
            if (c.Connected) c = CopyWithChannel(c, _wlan.GetChannel(iface) ?? 0);
            if (ct.IsCancellationRequested) return;
            _connection = c;
            st.LastConnQueryUtc = now;
            DetectTransitions(st, c);
        }

        var conn = _connection;
        if (!conn.Connected)
        {
            st.PrevCounters = null; st.RetryPct = null; st.RetryAvg = null; st.RetryWin.Clear(); st.RetryFrames = 0; st.RetryFps = st.RetryAvgFps = 0;
            if (!ct.IsCancellationRequested) SampleTaken?.Invoke(new LinkSample(now, -100, 0, 0, null), conn);
            return;
        }

        int rssi = _wlan.GetRssi(iface) ?? RssiFromQuality(conn.SignalQuality);

        if ((now - st.LastCounterUtc).TotalMilliseconds >= 1000)
        {
            var from = st.LastCounterUtc;
            st.LastCounterUtc = now;
            var ctrs = _wlan.GetPhyCounters(iface);
            if (ctrs is { } k)
            {
                if (st.PrevCounters is { } p && k.tx >= p.tx && k.retry >= p.retry)
                {
                    ulong dTx = k.tx - p.tx, dRe = k.retry - p.retry;
                    double secs = Math.Max(0.5, (now - from).TotalSeconds);   // intervals can be ~2 s at slow poll rates
                    _retrySeq++;   // on the monitor, so it never repeats across adapter switches
                    st.RetryFrames = (int)Math.Min(int.MaxValue, dTx);
                    st.RetryCount = (int)Math.Min(int.MaxValue, Math.Min(dRe, dTx));
                    st.RetryFps = dTx / secs;
                    // Under 20 frames there's no meaningful percentage – report nothing rather than hold an old figure.
                    st.RetryPct = dTx >= 20 ? Math.Min(100, 100.0 * dRe / dTx) : null;
                    st.RetryWin.Enqueue((from, now, dTx, dRe));
                    while (st.RetryWin.Count > 10) st.RetryWin.Dequeue();   // ~10 s; by count so clock changes can't break it
                    (st.RetryAvg, st.RetryAvgFps) = RollingRetry(st.RetryWin);
                }
                else st.RetryWin.Clear();   // first read, or counters reset (reconnect / driver restart)
                st.PrevCounters = (k.tx, k.retry);
            }
            else { st.RetryPct = null; st.RetryAvg = null; st.RetryWin.Clear(); st.PrevCounters = null; st.RetryFrames = 0; st.RetryFps = st.RetryAvgFps = 0; }
        }

        if (ct.IsCancellationRequested) return;
        var s = new LinkSample(now, rssi, conn.RxMbps, conn.TxMbps, st.RetryPct, st.RetryAvg, Scans.OwnActive(now), st.PrevCounters != null,
            st.RetryPct != null ? st.RetryFrames : 0, st.RetryFps, st.RetryAvgFps, st.RetryPct != null ? _retrySeq : 0,
            st.RetryPct != null ? st.RetryCount : 0);
        Samples.Add(s);
        DetectSignalDrop(st, now);
        SampleTaken?.Invoke(s, conn);
    }

    /// <summary>
    /// Frame-weighted retry % over the window, leaving out seconds that overlapped a scan. Overlap is checked now,
    /// not when each second was recorded, so Windows' scans (reported only when they finish) drop out once known.
    /// Null when there isn't enough scan-free traffic to say.
    /// </summary>
    /// <returns>The percentage, and the scan-free frames per second it's based on (how busy the link was).</returns>
    private (double? pct, double fps) RollingRetry(IEnumerable<(DateTime start, DateTime end, ulong dTx, ulong dRe)> win)
    {
        ulong tx = 0, re = 0;
        double secs = 0;
        foreach (var w in win)
            if (!Scans.Overlaps(w.start, w.end)) { tx += w.dTx; re += w.dRe; secs += Math.Max(0, (w.end - w.start).TotalSeconds); }
        double fps = secs > 0 ? tx / secs : 0;
        return (tx >= 50 ? Math.Min(100, 100.0 * re / tx) : null, fps);
    }

    private static ConnectionInfo CopyWithChannel(ConnectionInfo c, int ch) => new()
    {
        Connected = c.Connected, State = c.State, Profile = c.Profile, Ssid = c.Ssid, Bssid = c.Bssid,
        BssidKey = c.BssidKey, PhyType = c.PhyType, SignalQuality = c.SignalQuality, RxMbps = c.RxMbps,
        TxMbps = c.TxMbps, Auth = c.Auth, Cipher = c.Cipher, Channel = ch
    };

    private static int RssiFromQuality(uint q) => (int)Math.Round(q / 2.0 - 100);

    private const double RoamGraceSeconds = 2.5;

    private void DetectTransitions(PollState st, ConnectionInfo c)
    {
        var now = DateTime.UtcNow;
        var p = st.PrevConn;
        st.PrevConn = c;
        if (p == null)
        {
            if (c.Connected) RaiseNamed(EventKind.Connect, $"Connected to {{s0}} · {{b0}} · ch {c.Channel}", new[] { c.Ssid }, new[] { c.Bssid });
            return;
        }

        // During a roam Windows briefly reports associating/authenticating. Hold a drop for a moment
        // and decide once we see where the link comes back.
        if (p.Connected && !c.Connected)
        {
            st.LostConn = p; st.LostAtUtc = now; st.DisconnectReported = false;
            return;
        }
        if (!c.Connected)
        {
            if (st.LostConn != null && !st.DisconnectReported && (now - st.LostAtUtc).TotalSeconds >= RoamGraceSeconds)
            {
                RaiseNamed(EventKind.Disconnect, $"Disconnected from {{s0}} ({c.State})", new[] { st.LostConn.Ssid }, Array.Empty<string>());
                st.DisconnectReported = true;
            }
            return;
        }
        if (!p.Connected)
        {
            var lost = st.LostConn;
            st.LostConn = null;
            if (lost != null && !st.DisconnectReported)
            {
                if (lost.BssidKey != c.BssidKey)
                    RaiseNamed(EventKind.Roam, $"Roamed {{b0}} (ch {lost.Channel}) → {{b1}} (ch {c.Channel})", Array.Empty<string>(), new[] { lost.Bssid, c.Bssid });
                else
                    RaiseNamed(EventKind.Warning, $"Link to {{s0}} dropped briefly ({(now - st.LostAtUtc).TotalSeconds:0.0}s) and recovered", new[] { c.Ssid }, Array.Empty<string>());
            }
            else
            {
                string gap = st.DisconnectReported ? $" after {FormatGap(now - st.LostAtUtc)} offline" : "";
                RaiseNamed(EventKind.Connect, $"Connected to {{s0}} · {{b0}} · ch {c.Channel}{gap}", new[] { c.Ssid }, new[] { c.Bssid });
            }
            st.PendingChannel = null; st.RateLowCount = 0; st.LastChannel = c.Channel;
            return;
        }

        if (p.BssidKey != c.BssidKey)
        {
            RaiseNamed(EventKind.Roam, $"Roamed {{b0}} (ch {p.Channel}) → {{b1}} (ch {c.Channel})", Array.Empty<string>(), new[] { p.Bssid, c.Bssid });
            st.PendingChannel = null; st.RateLowCount = 0; st.LastChannel = c.Channel;
            return;
        }
        if (st.LastChannel == 0) st.LastChannel = p.Channel;

        // Same AP, different channel. Some drivers briefly report an off-channel number while scanning,
        // and Wi-Fi 7 multi-link APs can legitimately hop between links – so require it to hold for 3 s.
        if (c.Channel == 0 || st.LastChannel == 0) { /* unknown reading – keep any pending change as is */ }
        else if (c.Channel != (st.PendingChannel is null ? st.LastChannel : st.PendingFrom))
        {
            if (st.PendingChannel != c.Channel)
            {
                st.PendingFrom = st.PendingChannel is null ? st.LastChannel : st.PendingFrom;
                st.PendingChannel = c.Channel;
                st.PendingChannelSinceUtc = now;
            }
            else if ((now - st.PendingChannelSinceUtc).TotalSeconds >= 3)
            {
                var ap = GetBss(c.BssidKey);
                int from = st.PendingFrom;
                st.PendingChannel = null;
                st.LastChannel = c.Channel;
                if (ap?.MultiLink != true)
                {
                    var band = ap?.Band ?? WifiBand.Unknown;
                    string why = band == WifiBand.Band5 && ChannelMath.IsDfs(band, from)
                        ? " — possibly a DFS radar event forcing the router off its channel" : "";
                    Raise(EventKind.Warning, $"Access point moved channel {from} → {c.Channel}{why}");
                }
            }
        }
        else st.PendingChannel = null;

        // Big transmit-rate collapse that persists (not normal rate adaptation), at most every 30 s.
        if (st.RateLowCount == 0 && p.TxMbps > 0 && c.TxMbps < p.TxMbps * 0.4 && p.TxMbps - c.TxMbps > 100) st.RateLowCount = 1;
        else if (st.RateLowCount > 0 && c.TxMbps < 0.5 * Math.Max(1, st.RateRef)) st.RateLowCount++;
        else st.RateLowCount = 0;
        if (st.RateLowCount == 1) st.RateRef = p.TxMbps;
        if (st.RateLowCount >= 12 && (now - st.LastRateDropUtc).TotalSeconds >= 30)
        {
            st.LastRateDropUtc = now;
            st.RateLowCount = 0;
            Raise(EventKind.Warning, $"Transmit link rate fell {st.RateRef:0} → {c.TxMbps:0} Mbps and stayed down");
        }
    }

    private static string FormatGap(TimeSpan t) => t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds}s" : $"{t.TotalSeconds:0}s";

    /// <summary>
    /// Evaluated a few seconds in arrears: Windows only tells us about its own background scans when they
    /// finish, so waiting lets us discard scan-induced dips instead of reporting them as signal drops.
    /// </summary>
    private void DetectSignalDrop(PollState st, DateTime now)
    {
        if ((now - st.LastRssiDropUtc).TotalSeconds < 20) return;
        var t0 = now - Scans.ExternalLead - TimeSpan.FromMilliseconds(500);
        Samples.CopySince(t0.AddSeconds(-12), st.Scratch);
        st.Scratch.RemoveAll(x => x.Utc > t0);
        Scans.CopySince(t0.AddSeconds(-12), now, st.Marks);
        ScanTimeline.ExcludeCovered(st.Scratch, x => x.Utc, st.Marks, st.Clean);
        int need = Math.Max(8, (int)(12000.0 / Math.Max(100, _settings.PollMs) * 0.6));
        if (st.Clean.Count < need) return;
        double rs = 0, bs = 0; int rn = 0, bn = 0;
        var cut = t0.AddSeconds(-2);
        foreach (var x in st.Clean) { if (x.Utc >= cut) { rs += x.Rssi; rn++; } else { bs += x.Rssi; bn++; } }
        if (rn < 2 || bn < need / 2) return;
        double d = bs / bn - rs / rn;
        if (d >= 10)
        {
            st.LastRssiDropUtc = now;
            Raise(EventKind.Warning, $"Signal dropped {d:0} dB (now {rs / rn:0} dBm)");
        }
    }

    private void Raise(EventKind k, string text) => EventRaised?.Invoke(new EventEntry { Kind = k, Text = text });

    /// <summary>Events that mention a network name or address keep them separate so the screen can mask them.</summary>
    private void RaiseNamed(EventKind k, string template, string[] ssids, string[] bssids)
    {
        if (_connection is { Connected: true } c) Privacy.OwnSsid = c.Ssid;   // so "My network" is right from the first event
        EventRaised?.Invoke(EventEntry.Make(k, template, ssids, bssids));
    }

    // ------------------------------------------------------------------ scan loop

    private async Task ScanLoop(Guid iface, CancellationToken ct)
    {
        bool first = true;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (first)
                {
                    // Show whatever the OS already has cached straight away.
                    first = false;
                    FetchAndPublish(iface, ct);
                }

                bool force = Interlocked.Exchange(ref _forceScan, 0) == 1;
                if ((force || _activeWanted) && await ActiveScanAsync(iface, force, ct))
                {
                    await RestAsync(iface, TimeSpan.FromSeconds(Math.Max(1, _settings.ScanSeconds - 2)), wakeWhenActive: false, ct);
                }
                else
                {
                    // Passive: AirScope never takes the radio off-channel. Windows still scans in the background
                    // now and then; we pick up its results when it says the list changed, or on the interval.
                    Interlocked.Exchange(ref _cacheDirty, 0);
                    FetchAndPublish(iface, ct);
                    await RestAsync(iface, TimeSpan.FromSeconds(Math.Max(5, _settings.ScanSeconds)), wakeWhenActive: true, ct);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) when (_wlan.IsDisposed || ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                // Never let one bad scan stop scanning for the rest of the session – but don't retry back-to-back.
                StatusChanged?.Invoke("Scan error: " + ex.Message);
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(2, _settings.ScanSeconds)), ct); } catch (OperationCanceledException) { return; }
            }
        }
    }

    /// <summary>Returns false if, after waiting out the minimum gap, scanning was no longer wanted (e.g. a spot test began).</summary>
    private async Task<bool> ActiveScanAsync(Guid iface, bool force, CancellationToken ct)
    {
        // Rapid tab switching shouldn't turn into back-to-back scans. Monotonic clock: a clock change can't stall this.
        long sinceMs = Environment.TickCount64 - _lastOwnScanTick;
        long gapMs = (long)MinOwnScanGap.TotalMilliseconds;
        if (!force && sinceMs >= 0 && sinceMs < gapMs)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(gapMs - sinceMs), ct);
            if (!_activeWanted) return false;
        }

        // Drain a stale signal so we wait for *this* scan's completion.
        while (_scanSignal.CurrentCount > 0) await _scanSignal.WaitAsync(ct);

        _lastOwnScanTick = Environment.TickCount64;
        var token = Scans.OwnStarted(DateTime.UtcNow);   // before the request, so a fast completion is attributed to us
        uint rc;
        try { rc = _wlan.RequestScan(iface); }
        catch { Scans.OwnCancelled(token); throw; }
        if (rc != 0) Scans.OwnCancelled(token);
        if (!ct.IsCancellationRequested)
            StatusChanged?.Invoke(rc == 0 ? "Scanning…" : $"Scan request refused ({rc}) — using cached results");

        // Scans normally finish in 2–4 s. Without notifications (registration failed) assume ~4 s rather than
        // shading 6 s of every interval as off-channel.
        if (rc == 0)
        {
            if (_wlan.NotificationsAvailable) await _scanSignal.WaitAsync(TimeSpan.FromSeconds(6), ct);
            else await Task.Delay(TimeSpan.FromSeconds(4), ct);
        }
        Scans.OwnTimedOut(token, DateTime.UtcNow);       // no-op if the completion already closed it
        Interlocked.Exchange(ref _cacheDirty, 0);
        FetchAndPublish(iface, ct);
        return true;
    }

    /// <summary>
    /// Wait out the interval in 1 s slices. Ends early for "Scan now", and (in passive mode) as soon as active
    /// scanning is switched on. Re-reads the cache whenever Windows reports it finished a scan of its own.
    /// </summary>
    private async Task RestAsync(Guid iface, TimeSpan span, bool wakeWhenActive, CancellationToken ct)
    {
        long until = Environment.TickCount64 + (long)span.TotalMilliseconds;   // monotonic
        while (true)
        {
            var left = TimeSpan.FromMilliseconds(until - Environment.TickCount64);
            if (left <= TimeSpan.Zero) return;
            if (await _scanKick.WaitAsync(left < TimeSpan.FromSeconds(1) ? left : TimeSpan.FromSeconds(1), ct)) return;
            if (wakeWhenActive && _activeWanted) return;
            if (Interlocked.Exchange(ref _cacheDirty, 0) == 1) FetchAndPublish(iface, ct);
        }
    }

    private void FetchAndPublish(Guid iface, CancellationToken ct)
    {
        var list = _wlan.GetBssList(iface, out uint err);
        if (ct.IsCancellationRequested) return;
        if (err != 0)
        {
            StatusChanged?.Invoke(err == WlanNative.ERROR_ACCESS_DENIED
                ? "Windows location privacy is blocking scan results"
                : $"Scan results unavailable (error {err})");
            ScanCompleted?.Invoke(Array.Empty<BssInfo>(), err);
            return;
        }

        var now = DateTime.UtcNow;
        List<BssInfo> snapshot;
        lock (_bssLock)
        {
            foreach (var b in list)
            {
                if (!b.Hidden) Privacy.Register(b.Ssid);
                if (!_bss.TryGetValue(b.Key, out var t))
                    _bss[b.Key] = t = new BssTrack { Latest = b, FirstSeenUtc = now };
                else if (b.Tsf != 0 && b.Tsf == t.Latest.Tsf)
                    continue;   // same beacon re-read from Windows' cache – not a new sighting, so don't refresh "seen" or history
                t.Latest = b;
                t.History.Add((now, b.Rssi));
                int drop = 0;
                while (drop < t.History.Count && now - t.History[drop].utc > HistoryKeep) drop++;
                if (drop > 0) t.History.RemoveRange(0, drop);
            }
            // Never drop the AP we're connected to: without scans Windows may not re-list it for a while,
            // and the Live tab's link details (width, clients, busy…) come from it.
            ulong mine = _connection.Connected ? _connection.BssidKey : 0;
            foreach (var k in _bss.Where(kv => kv.Key != mine && (now - kv.Value.Latest.SeenUtc).TotalSeconds > RemoveAfterSeconds).Select(kv => kv.Key).ToList())
                _bss.Remove(k);
            snapshot = _bss.Values.Select(v => v.Latest).ToList();
        }
        int n = snapshot.Count;
        StatusChanged?.Invoke($"{n} access point{(n == 1 ? "" : "s")}" + (_activeWanted ? "" : " · from Windows' own scans (open Networks to scan)"));
        ScanCompleted?.Invoke(snapshot, 0);
    }

    /// <summary>Copy of RSSI history for a BSSID (thread-safe).</summary>
    public List<(DateTime utc, int rssi)> GetHistory(ulong key)
    {
        lock (_bssLock)
            return _bss.TryGetValue(key, out var t) ? new List<(DateTime, int)>(t.History) : new();
    }

    public BssInfo? GetBss(ulong key)
    {
        lock (_bssLock) return _bss.TryGetValue(key, out var t) ? t.Latest : null;
    }

    public DateTime? FirstSeen(ulong key)
    {
        lock (_bssLock) return _bss.TryGetValue(key, out var t) ? t.FirstSeenUtc : null;
    }

    public void Dispose()
    {
        _wlan.Notification -= OnNotification;
        Stop();
        _scanSignal.Dispose();
        _scanKick.Dispose();
    }
}
