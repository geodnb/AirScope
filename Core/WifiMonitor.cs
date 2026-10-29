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
///  • a scan loop for neighbouring networks, paced by the OS scan-complete notification
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
    private readonly SemaphoreSlim _scanSignal = new(0, 1);
    private readonly SemaphoreSlim _scanKick = new(0, 1);
    private readonly object _bssLock = new();
    private readonly Dictionary<ulong, BssTrack> _bss = new();
    private static readonly TimeSpan HistoryKeep = TimeSpan.FromMinutes(30);
    private const int RemoveAfterSeconds = 180;

    private volatile object _ifaceBox = Guid.Empty;        // boxed so cross-thread reads are atomic
    private volatile ConnectionInfo _connection = new();
    private long _lastScanTicks;                            // 0 = never

    public SampleBuffer Samples { get; } = new(30 * 60 * 10 + 64); // 30 min at 10 Hz worst case
    public Guid InterfaceId => (Guid)_ifaceBox;
    public ConnectionInfo Connection => _connection;
    public DateTime? LastScanUtc
    {
        get { long t = Interlocked.Read(ref _lastScanTicks); return t == 0 ? null : new DateTime(t, DateTimeKind.Utc); }
    }

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
        lock (_bssLock) _bss.Clear();
        _connection = new ConnectionInfo();
        Interlocked.Exchange(ref _lastScanTicks, 0);
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

    /// <summary>Trigger a scan right now instead of waiting for the next interval.</summary>
    public void ScanNow() => Signal(_scanKick);

    private static void Signal(SemaphoreSlim s)
    {
        try { if (s.CurrentCount == 0) s.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }

    private void OnNotification(uint source, uint code, Guid iface)
    {
        if (iface != InterfaceId) return;
        if (source == WlanNative.WLAN_NOTIFICATION_SOURCE_ACM &&
            (code == WlanNative.ACM_SCAN_COMPLETE || code == WlanNative.ACM_SCAN_FAIL || code == WlanNative.ACM_SCAN_LIST_REFRESH))
            Signal(_scanSignal);
    }

    // ------------------------------------------------------------------ fast link poll

    private sealed class PollState
    {
        public ConnectionInfo? PrevConn;
        public (ulong tx, ulong retry)? PrevCounters;
        public double? RetryPct;
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
            st.PrevCounters = null; st.RetryPct = null;
            if (!ct.IsCancellationRequested) SampleTaken?.Invoke(new LinkSample(now, -100, 0, 0, null), conn);
            return;
        }

        int rssi = _wlan.GetRssi(iface) ?? RssiFromQuality(conn.SignalQuality);

        if ((now - st.LastCounterUtc).TotalMilliseconds >= 1000)
        {
            st.LastCounterUtc = now;
            var ctrs = _wlan.GetPhyCounters(iface);
            if (ctrs is { } k)
            {
                if (st.PrevCounters is { } p && k.tx >= p.tx && k.retry >= p.retry)
                {
                    ulong dTx = k.tx - p.tx, dRe = k.retry - p.retry;
                    if (dTx >= 20) st.RetryPct = Math.Min(100, 100.0 * dRe / dTx);
                }
                st.PrevCounters = (k.tx, k.retry);
            }
            else st.RetryPct = null;
        }

        if (ct.IsCancellationRequested) return;
        var s = new LinkSample(now, rssi, conn.RxMbps, conn.TxMbps, st.RetryPct);
        Samples.Add(s);
        DetectSignalDrop(st, now);
        SampleTaken?.Invoke(s, conn);
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
            if (c.Connected) Raise(EventKind.Connect, $"Connected to {c.Ssid} · {c.Bssid} · ch {c.Channel}");
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
                Raise(EventKind.Disconnect, $"Disconnected from {st.LostConn.Ssid} ({c.State})");
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
                    Raise(EventKind.Roam, $"Roamed {lost.Bssid} (ch {lost.Channel}) → {c.Bssid} (ch {c.Channel})");
                else
                    Raise(EventKind.Warning, $"Link to {c.Ssid} dropped briefly ({(now - st.LostAtUtc).TotalSeconds:0.0}s) and recovered");
            }
            else
            {
                string gap = st.DisconnectReported ? $" after {FormatGap(now - st.LostAtUtc)} offline" : "";
                Raise(EventKind.Connect, $"Connected to {c.Ssid} · {c.Bssid} · ch {c.Channel}{gap}");
            }
            st.PendingChannel = null; st.RateLowCount = 0; st.LastChannel = c.Channel;
            return;
        }

        if (p.BssidKey != c.BssidKey)
        {
            Raise(EventKind.Roam, $"Roamed {p.Bssid} (ch {p.Channel}) → {c.Bssid} (ch {c.Channel})");
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

    private void DetectSignalDrop(PollState st, DateTime now)
    {
        if ((now - st.LastRssiDropUtc).TotalSeconds < 20) return;
        Samples.CopySince(now.AddSeconds(-12), st.Scratch);
        int need = Math.Max(8, (int)(12000.0 / Math.Max(100, _settings.PollMs) * 0.6));
        if (st.Scratch.Count < need) return;
        double rs = 0, bs = 0; int rn = 0, bn = 0;
        var cut = now.AddSeconds(-2);
        foreach (var x in st.Scratch) { if (x.Utc >= cut) { rs += x.Rssi; rn++; } else { bs += x.Rssi; bn++; } }
        if (rn < 2 || bn < need / 2) return;
        double d = bs / bn - rs / rn;
        if (d >= 10)
        {
            st.LastRssiDropUtc = now;
            Raise(EventKind.Warning, $"Signal dropped {d:0} dB (now {rs / rn:0} dBm)");
        }
    }

    private void Raise(EventKind k, string text) => EventRaised?.Invoke(new EventEntry { Kind = k, Text = text });

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

                // Drain a stale signal so we wait for *this* scan's completion.
                while (_scanSignal.CurrentCount > 0) await _scanSignal.WaitAsync(ct);

                uint rc = _wlan.RequestScan(iface);
                if (!ct.IsCancellationRequested)
                    StatusChanged?.Invoke(rc == 0 ? "Scanning…" : $"Scan request refused ({rc}) — using cached results");

                // Scans normally finish in 2–4 s. Fall back to polling the cache if no notification arrives.
                await _scanSignal.WaitAsync(TimeSpan.FromSeconds(6), ct);
                FetchAndPublish(iface, ct);

                // Wait the remainder of the interval, unless someone asks for a scan now.
                var wait = TimeSpan.FromSeconds(Math.Max(1, _settings.ScanSeconds - 2));
                await _scanKick.WaitAsync(wait, ct);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) when (_wlan.IsDisposed || ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                // Never let one bad scan stop scanning for the rest of the session.
                StatusChanged?.Invoke("Scan error: " + ex.Message);
                try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { return; }
            }
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
        Interlocked.Exchange(ref _lastScanTicks, now.Ticks);
        List<BssInfo> snapshot;
        lock (_bssLock)
        {
            foreach (var b in list)
            {
                if (!_bss.TryGetValue(b.Key, out var t))
                    _bss[b.Key] = t = new BssTrack { Latest = b, FirstSeenUtc = now };
                t.Latest = b;
                t.History.Add((now, b.Rssi));
                int drop = 0;
                while (drop < t.History.Count && now - t.History[drop].utc > HistoryKeep) drop++;
                if (drop > 0) t.History.RemoveRange(0, drop);
            }
            foreach (var k in _bss.Where(kv => (now - kv.Value.Latest.SeenUtc).TotalSeconds > RemoveAfterSeconds).Select(kv => kv.Key).ToList())
                _bss.Remove(k);
            snapshot = _bss.Values.Select(v => v.Latest).ToList();
        }
        StatusChanged?.Invoke($"{snapshot.Count} access points");
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
