using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AirScope.Core;
using AirScope.UI;

namespace AirScope;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings = AppSettings.Load();
    private WlanClient? _wlan;
    private WifiMonitor? _mon;
    private RouterPinger? _pinger;

    private readonly ObservableCollection<BssRow> _rows = new();
    private readonly Dictionary<ulong, BssRow> _rowMap = new();
    private ListCollectionView _view = null!;
    private readonly ObservableCollection<EventEntry> _events = new();
    private readonly ObservableCollection<SpotTestResult> _spots = new();

    private IReadOnlyList<BssInfo> _scan = Array.Empty<BssInfo>();
    private HashSet<ulong> _bestPerSsid = new();
    private HashSet<ulong> _selected = new();
    private ConnectionInfo _conn = new();
    private DateTime? _connectedSinceUtc;
    private string _status = "";

    private CsvLogger? _logger;
    private SpotTestRunner? _spot;
    private MiniWindow? _mini;
    private readonly DispatcherTimer _uiTimer;
    private int _livePending;
    private bool _loading = true;
    private bool _closed;
    private int _gen;                         // bumped on adapter switch; stale queued callbacks are dropped
    private volatile ConnectionInfo _latestConn = new();
    private EventHandler? _miniClosedHandler;

    private readonly Dictionary<string, TextBlock> _link = new();
    private readonly Dictionary<string, TextBlock> _det = new();
    private readonly List<LinkSample> _statWork = new();
    private readonly List<LinkSample> _cleanWork = new();
    private readonly List<ScanMark> _marksWork = new();
    private readonly List<PingSample> _pingWork = new();
    private double? _retryPeak;
    /// <summary>Scan-free frames in the 10 s retry window below which the link counts as lightly used.</summary>
    private const int LightTrafficFrames = 300;
    /// <summary>A single second needs this many frames to count towards the retry peak.</summary>
    private const int PeakMinFrames = 100;
    private readonly List<LinkSample> _peakWork = new(), _peakClean = new();
    private string _scanState = "";
    private DateTime _lastActiveUtc = DateTime.UtcNow;
    private DateTime _pingEvalUpTo = DateTime.MinValue, _lastPingEventUtc = DateTime.MinValue;
    private readonly List<PingSample> _pending = new();   // unexplained spikes awaiting an event

    public MainWindow()
    {
        InitializeComponent();

        _view = new ListCollectionView(_rows) { Filter = FilterRow };
        _view.SortDescriptions.Add(new SortDescription(nameof(BssRow.Rssi), ListSortDirection.Descending));
        _view.IsLiveSorting = true;
        _view.LiveSortingProperties.Add(nameof(BssRow.Rssi));
        _view.IsLiveFiltering = true;
        _view.LiveFilteringProperties.Add(nameof(BssRow.IsStale));
        _view.LiveFilteringProperties.Add(nameof(BssRow.IsBestForSsid));
        NetGrid.ItemsSource = _view;
        EventList.ItemsSource = _events;
        foreach (var s in SpotTestStore.Load()) _spots.Add(s);
        SpotGrid.ItemsSource = _spots;
        RecomputeSpotDeltas();

        BuildKeyValueRows(LinkGrid, _link, "Network", "BSSID", "Vendor", "Band · channel", "Width", "Standard",
            "Security", "Protected mgmt", "Rx link rate", "Tx link rate", "AP max PHY", "Retries", "Windows signal",
            "AP clients", "Channel busy", "AP uptime", "BSS colour", "Country", "Connected for");
        BuildKeyValueRows(DetGrid, _det, "BSSID", "Vendor", "Band", "Channel", "Width", "Centre", "Standard", "MIMO",
            "AP max PHY", "Security", "Cipher", "PMF", "WPS", "Clients", "Channel busy", "BSS colour", "Multi-link",
            "Country", "Beacon", "AP uptime", "First seen", "Last seen");

        BuildWindowChips();
        PollBox.ItemsSource = new[] { "100 ms  (10 Hz)", "250 ms  (4 Hz)", "500 ms  (2 Hz)", "1 s" };
        PollBox.SelectedIndex = _settings.PollMs switch { <= 100 => 0, <= 250 => 1, <= 500 => 2, _ => 3 };
        ScanBox.ItemsSource = new[] { "3 s", "5 s", "10 s", "20 s", "30 s", "60 s" };
        ScanBox.SelectedIndex = _settings.ScanSeconds switch { <= 3 => 0, <= 5 => 1, <= 10 => 2, <= 20 => 3, <= 30 => 4, _ => 5 };
        ScanModeBox.ItemsSource = new[] { "Auto — only while viewing networks", "Always — every interval", "Off — use Windows' own scans" };
        ScanModeBox.SelectedIndex = (int)_settings.ScanMode;
        PingCheck.IsChecked = _settings.PingRouter;
        ScreenshotCheck.IsChecked = Privacy.On = _settings.HideIdentifiers;
        PingCard.Visibility = _settings.PingRouter ? Visibility.Visible : Visibility.Collapsed;
        SpotDuration.ItemsSource = new[] { "10 seconds", "30 seconds", "60 seconds", "2 minutes" };
        SpotDuration.SelectedIndex = 1;
        MiniTopCheck.IsChecked = _settings.MiniAlwaysOnTop;
        RatesCheck.IsChecked = _settings.ShowRates;
        ExcludeOwnCheck.IsChecked = _settings.ExcludeOwnInAdvisor;
        Live.ShowRates = _settings.ShowRates;
        Live.WindowSeconds = _settings.GraphWindowSeconds;
        AboutText.Text = $"AirScope {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)} · read-only Wi-Fi monitor · " +
                         "no admin rights, makes no changes to Windows or your adapter.";

        G24.NetworkClicked += SelectFromGraph;
        G5.NetworkClicked += SelectFromGraph;
        G6.NetworkClicked += SelectFromGraph;

        _uiTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _uiTimer.Tick += (_, _) => SlowTick();

        SourceInitialized += (_, _) => DarkTitleBar(this);
        Loaded += (_, _) => { _loading = false; Task.Run(OuiLookup.Warm); InitWlan(); _uiTimer.Start(); };
        Closing += OnClosing;
        StateChanged += (_, _) => UpdateScanPolicy();
        Activated += (_, _) => UpdateScanPolicy();
    }

    // ================================================================== startup

    private void InitWlan()
    {
        try
        {
            _wlan = new WlanClient();
        }
        catch (DllNotFoundException)
        {
            ShowNoAdapter("The Windows Wi-Fi API (wlanapi.dll) isn't available on this PC.");
            return;
        }
        catch (WlanException ex)
        {
            ShowNoAdapter(ex.Code == 1062
                ? "The WLAN AutoConfig service isn't running. Start it from Services (WlanSvc) and reopen AirScope."
                : $"Couldn't open the Wi-Fi API: {ex.Message}");
            return;
        }

        _mon = new WifiMonitor(_wlan, _settings);
        _mon.SampleTaken += OnSample;
        _mon.ScanCompleted += (list, err) =>
        {
            int g = Volatile.Read(ref _gen);
            Dispatcher.BeginInvoke(() => { if (g == _gen && !_closed) OnScan(list, err); });
        };
        _mon.EventRaised += ev =>
        {
            int g = Volatile.Read(ref _gen);
            Dispatcher.BeginInvoke(() => { if (g == _gen && !_closed) AddEvent(ev); });
        };
        _mon.StatusChanged += s => _status = s;
        Live.Scans = _mon.Scans;
        var mon = _mon;
        _pinger = new RouterPinger(() => mon.Connection.Connected);
        _pinger.Sampled += p => { try { _logger?.WritePing(p); } catch { } };

        List<WlanInterfaceInfo> ifs;
        try { ifs = _wlan.GetInterfaces(); }
        catch (WlanException ex) { ShowNoAdapter("Couldn't list Wi-Fi adapters: " + ex.Message); return; }

        if (ifs.Count == 0) { ShowNoAdapter("No Wi-Fi adapter found. Check it's enabled in Device Manager and Airplane mode is off."); return; }
        _loading = true;
        AdapterBox.ItemsSource = ifs;
        AdapterBox.SelectedItem = ifs.FirstOrDefault(i => i.Id == _settings.InterfaceId) ?? ifs[0];
        _loading = false;
        StartMonitor();
    }

    private void ShowNoAdapter(string msg)
    {
        NoAdapterText.Text = msg;
        NoAdapterBanner.Visibility = Visibility.Visible;
        ScanNowBtn.IsEnabled = false;
        PingCheck.IsEnabled = false;
    }

    private void StartMonitor()
    {
        if (_mon == null || AdapterBox.SelectedItem is not WlanInterfaceInfo iface) return;
        _settings.InterfaceId = iface.Id;
        _rows.Clear(); _rowMap.Clear(); _selected.Clear(); _scan = Array.Empty<BssInfo>();
        _connectedSinceUtc = null;
        _conn = new ConnectionInfo();
        Live.Buffer = _mon.Samples;
        _mon.Stop();                          // old loops finish (and queue their last callbacks) first…
        Interlocked.Increment(ref _gen);      // …which are then ignored as belonging to the old adapter
        _events.Clear();
        _pinger?.Stop();
        ResetPingEvents();
        _mon.Start(iface.Id);
        if (_settings.PingRouter) { _pinger?.Start(iface.Id); Live.Pings = _pinger; }
        UpdateScanPolicy();
    }

    // ================================================================== scan policy

    /// <summary>
    /// Decide whether AirScope may take the radio off-channel to scan. Scans cause brief RSSI dips, retry bursts
    /// and latency spikes, so in Auto mode we only scan while a neighbour view is actually being looked at.
    /// </summary>
    private void UpdateScanPolicy()
    {
        if (_mon == null) return;
        var now = DateTime.UtcNow;
        if (IsActive) _lastActiveUtc = now;
        bool onScreen = WindowState != WindowState.Minimized && IsVisible && _mini == null;
        bool neighbourTab = Tabs.SelectedIndex is >= 1 and <= 4;
        bool recentlyUsed = (now - _lastActiveUtc).TotalSeconds < 60;
        bool active = _spot == null && _settings.ScanMode switch
        {
            ScanMode.Always => true,
            ScanMode.Passive => false,
            _ => onScreen && neighbourTab && recentlyUsed
        };
        _mon.ActiveScanning = active;
        _scanState = _spot != null ? "scans paused for spot test" : _settings.ScanMode switch
        {
            ScanMode.Always => $"scanning every {_settings.ScanSeconds} s",
            ScanMode.Passive => "scanning off (using Windows' scans)",
            _ when active => $"auto: scanning every {_settings.ScanSeconds} s",
            _ when _mini != null => "auto: scans paused in Mini mode",
            _ when !onScreen => "auto: scans paused while minimised",
            _ when !neighbourTab => "auto: scans paused on this tab",
            _ => "auto: scans paused (window in background)"
        };
    }

    /// <summary>
    /// The time "how long since we saw it" is measured against. When nothing has scanned for a while (Auto on the Live
    /// tab, or Off), an AP heard in the most recent scan isn't out of range just because that scan was a while ago.
    /// </summary>
    private DateTime FreshRef(DateTime now) =>
        _mon?.LastScanUtc is DateTime t && t.AddSeconds(10) < now ? t.AddSeconds(10) : now;

    private void ScanModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ScanModeBox.SelectedIndex < 0) return;
        _settings.ScanMode = (ScanMode)ScanModeBox.SelectedIndex;
        UpdateScanPolicy();
    }

    private void AdapterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        StartMonitor();
    }

    // ================================================================== live samples

    private void OnSample(LinkSample s, ConnectionInfo c)
    {
        // Poll thread. Log/measure every sample; coalesce UI updates so a busy UI never backs up the poll.
        try { _logger?.Write(s, c); _spot?.Add(s, c); }
        catch (Exception) { /* logging/measuring must never disturb the poll */ }
        _latestConn = c;
        if (Interlocked.Exchange(ref _livePending, 1) == 1) return;
        int g = Volatile.Read(ref _gen);
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            Interlocked.Exchange(ref _livePending, 0);
            if (g == _gen && !_closed) UpdateLive(_latestConn);
        });
    }

    private DateTime _lastStatsUtc;

    private void UpdateLive(ConnectionInfo c)
    {
        if (_mon == null || _closed) return;
        bool changedAp = c.BssidKey != _conn.BssidKey || c.Connected != _conn.Connected;
        if (c.Connected && !_conn.Connected) _connectedSinceUtc = DateTime.UtcNow;
        if (!c.Connected) _connectedSinceUtc = null;
        _conn = c;

        var last = _mon.Samples.Last;
        if (!c.Connected || last == null)
        {
            BigRssi.Text = "—";
            BigRssi.Foreground = (Brush)FindResource("Muted");
            QualityText.Text = c.State;
            QualityBar.Value = 0;
            ConnSsid.Text = "Not connected";
            ConnSub.Text = "";
            foreach (var tb in _link.Values) tb.Text = "—";
            _mini?.Update(null, c, null, null);
            return;
        }

        var s = last.Value;
        var col = Palette.ForRssi(s.Rssi);
        BigRssi.Text = s.Rssi.ToString(CultureInfo.InvariantCulture);
        BigRssi.Foreground = Palette.B(col);
        QualityText.Text = ChannelMath.Describe(s.Rssi);
        QualityText.Foreground = Palette.B(col);
        QualityBar.Value = ChannelMath.QualityFromRssi(s.Rssi);

        var ap = _mon.GetBss(c.BssidKey);
        Privacy.OwnSsid = c.Ssid;
        ConnSsid.Text = string.IsNullOrEmpty(c.Ssid) ? "(hidden network)" : Privacy.Name(c.Ssid);
        ConnSub.Text = ap != null
            ? $"{ChannelMath.BandName(ap.Band)} · ch {c.Channel} · {ap.WidthLabel} MHz · {ap.GenLabel}"
            : $"ch {c.Channel} · {c.PhyName}";

        _link["Network"].Text = string.IsNullOrEmpty(c.Profile) || c.Profile == c.Ssid || Privacy.On ? Privacy.Name(c.Ssid) : $"{c.Ssid}  (profile {c.Profile})";
        _link["BSSID"].Text = Privacy.Mac(c.Bssid);
        _link["Vendor"].Text = ap?.Vendor is { Length: > 0 } v ? v : OuiLookup.Vendor(c.BssidKey) is { Length: > 0 } v2 ? v2 : "—";
        _link["Band · channel"].Text = ap != null ? $"{ChannelMath.BandName(ap.Band)} · {c.Channel}{(ap.Dfs ? "  (DFS)" : "")}" : $"{c.Channel}";
        _link["Width"].Text = ap != null ? $"{ap.WidthLabel} MHz" : "—";
        _link["Standard"].Text = c.PhyName;
        _link["Security"].Text = $"{c.Auth} · {c.Cipher}";
        _link["Protected mgmt"].Text = ap?.Pmf ?? "—";
        _link["Rx link rate"].Text = $"{c.RxMbps:0} Mbps";
        _link["Tx link rate"].Text = $"{c.TxMbps:0} Mbps";
        _link["AP max PHY"].Text = ap?.MaxPhyMbps is double m ? $"{m:0} Mbps  ({(ap.Streams > 0 ? ap.Streams : 1)} stream{(ap.Streams > 1 ? "s" : "")})" : "—";
        // Below ~30 frames a second (an idle PC), a couple of retries look like a big percentage, so say so.
        bool light = s.TxFramesAvg < LightTrafficFrames;
        _link["Retries"].Text = s.RetryAvgPct is double ra
            ? (light ? $"≈{ra:0}% · light traffic" : $"{ra:0.0}% avg" + (_retryPeak is double pk && pk >= ra + 3 ? $" · peak {pk:0}%" : ""))
            : !s.CountersOk ? "not reported by driver"
            : s.RetryPct is double && _mon.Scans.Overlaps(DateTime.UtcNow.AddSeconds(-11), DateTime.UtcNow) ? "scans too frequent for a clean figure"
            : s.RetryPct is double r2 && s.TxFrames >= PeakMinFrames ? $"{r2:0.0}% of frames"
            : "idle — too little traffic";
        _link["Windows signal"].Text = $"{c.SignalQuality}%";
        _link["AP clients"].Text = ap?.StationCount?.ToString(CultureInfo.InvariantCulture) ?? "not advertised";
        _link["Channel busy"].Text = ap?.ChannelUtilisation is double u ? $"{u * 100:0}%" : "not advertised";
        _link["AP uptime"].Text = ap?.ApUptime is TimeSpan up ? FormatSpan(up) : "—";
        _link["BSS colour"].Text = ap?.BssColor?.ToString(CultureInfo.InvariantCulture) ?? "—";
        _link["Country"].Text = ap?.Country ?? "—";
        _link["Connected for"].Text = _connectedSinceUtc is DateTime since ? FormatSpan(DateTime.UtcNow - since) : "—";

        var now = DateTime.UtcNow;
        if (changedAp || (now - _lastStatsUtc).TotalMilliseconds >= 500)
        {
            _lastStatsUtc = now;
            _mon.Samples.CopySince(now.AddSeconds(-Live.WindowSeconds), _statWork);
            // Readings taken while the radio was off-channel scanning say nothing about the link; leave them out.
            _mon.Scans.CopySince(now.AddSeconds(-Live.WindowSeconds), now, _marksWork);
            ScanTimeline.ExcludeCovered(_statWork, x => x.Utc, _marksWork, _cleanWork);
            var use = _cleanWork.Count >= 10 || _cleanWork.Count * 2 >= _statWork.Count ? _cleanWork : _statWork;
            bool excluded = use == _cleanWork && _cleanWork.Count < _statWork.Count;
            var st = Stats.Of(use.Select(x => (double)x.Rssi).ToList());
            if (st.Count > 0)
            {
                StatMin.Text = $"{st.Min:0}"; StatAvg.Text = $"{st.Avg:0.0}"; StatMax.Text = $"{st.Max:0}"; StatJit.Text = $"±{st.StdDev:0.0}";
                StatNote.Text = (st.Count < 10 ? "Collecting…" :
                    st.StdDev < 1.5 ? "Very steady signal." :
                    st.StdDev < 3 ? "Normal variation." :
                    st.StdDev < 5 ? "Fluctuating — movement, people, or interference nearby." :
                    "Unstable — check antenna connections, interference, or adapter power saving.")
                    + (excluded ? " Moments when the radio was scanning are left out." : "");
            }
            // Peak over a fixed minute, scan moments excluded (otherwise it's nearly always a scan burst).
            _retryPeak = null;
            _mon.Samples.CopySince(now.AddSeconds(-60), _peakWork);
            _mon.Scans.CopySince(now.AddSeconds(-62), now, _marksWork);
            ScanTimeline.ExcludeCovered(_peakWork, x => x.Utc, _marksWork, _peakClean);
            foreach (var x in _peakClean)
                if (x.RetryPct is double rp && x.TxFrames >= PeakMinFrames && (_retryPeak is not double cur || rp > cur)) _retryPeak = rp;
            _mini?.Update(s, c, _statWork, MiniPingText(now));
        }
    }

    private static string FormatSpan(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h" :
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" :
        t.TotalMinutes >= 1 ? $"{t.Minutes}m {t.Seconds}s" : $"{t.Seconds}s";

    // ================================================================== scans

    private void OnScan(IReadOnlyList<BssInfo> list, uint err)
    {
        if (_mon == null) return;
        PrivacyBanner.Visibility = err == Interop.WlanNative.ERROR_ACCESS_DENIED ? Visibility.Visible : Visibility.Collapsed;
        if (err != 0) return;   // keep the last good data on screen

        _scan = list;
        _logger?.Write(list);
        var now = DateTime.UtcNow;
        var present = new HashSet<ulong>();
        foreach (var b in list)
        {
            present.Add(b.Key);
            if (!_rowMap.TryGetValue(b.Key, out var row))
            {
                row = new BssRow(b) { FirstSeenUtc = _mon.FirstSeen(b.Key) ?? now };
                _rowMap[b.Key] = row;
                _rows.Add(row);
            }
            row.Update(b, _conn.Connected && b.Key == _conn.BssidKey, FreshRef(now));
        }
        foreach (var gone in _rowMap.Keys.Where(k => !present.Contains(k)).ToList())
        {
            _rows.Remove(_rowMap[gone]);
            _rowMap.Remove(gone);
            _selected.Remove(gone);
        }

        _bestPerSsid = list.Where(b => !b.Hidden).GroupBy(b => b.Ssid)
            .Select(g => g.OrderByDescending(b => b.Rssi).First().Key)
            .Concat(list.Where(b => b.Hidden).Select(b => b.Key)).ToHashSet();
        foreach (var r in _rows) r.SetBestForSsid(_bestPerSsid.Contains(r.Key));   // live filtering picks this up

        RefreshChannelGraphs();
        RefreshVisibleTab();
        RefreshDetails();
        UpdateCount();
    }

    private void RefreshChannelGraphs()
    {
        G24.SetData(_scan, _conn.Connected ? _conn.BssidKey : 0, _selected);
        G5.SetData(_scan, _conn.Connected ? _conn.BssidKey : 0, _selected);
        G6.SetData(_scan, _conn.Connected ? _conn.BssidKey : 0, _selected);
        Head24.Text = $"2.4 GHz · {G24.Count} access points";
        Head5.Text = $"5 GHz · {G5.Count} access points";
        Head6.Text = $"6 GHz · {G6.Count} access points";
    }

    private void RefreshVisibleTab()
    {
        if (Tabs.SelectedIndex == 3) RefreshHistory();
        else if (Tabs.SelectedIndex == 4) RefreshAdvisor();
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource != Tabs || _loading) return;
        RefreshVisibleTab();
        UpdateScanPolicy();
    }

    // ================================================================== networks grid

    private bool FilterRow(object o)
    {
        if (o is not BssRow r) return false;
        var b = r.Info;
        if (b.Band == WifiBand.Band24 && F24.IsChecked != true) return false;
        if (b.Band == WifiBand.Band5 && F5.IsChecked != true) return false;
        if (b.Band == WifiBand.Band6 && F6.IsChecked != true) return false;
        if (ShowStaleCheck.IsChecked != true && r.IsStale) return false;
        if (GroupCheck.IsChecked == true && !r.IsBestForSsid) return false;
        var q = FilterBox.Text.Trim();
        if (q.Length > 0)
        {
            var c = StringComparison.OrdinalIgnoreCase;
            if (!(b.DisplaySsid.Contains(q, c) || b.Bssid.Contains(q, c) || b.Vendor.Contains(q, c) ||
                  b.Security.Contains(q, c) || b.GenLabel.Contains(q, c) || b.Channel.ToString(CultureInfo.InvariantCulture) == q))
                return false;
        }
        return true;
    }

    private void Filter_Changed(object sender, TextChangedEventArgs e)
    {
        FilterHint.Visibility = FilterBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _view.Refresh();
        UpdateCount();
    }

    private void Filter_Click(object sender, RoutedEventArgs e) { _view.Refresh(); UpdateCount(); }

    private void UpdateCount() => NetCount.Text = $"{_view.Count} of {_rows.Count} access points";

    private void Grid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        // Keep live re-sorting on whatever column the user picked.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _view.LiveSortingProperties.Clear();
            foreach (var sd in _view.SortDescriptions) _view.LiveSortingProperties.Add(sd.PropertyName);
        });
    }

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selected = NetGrid.SelectedItems.OfType<BssRow>().Select(r => r.Key).ToHashSet();
        RefreshChannelGraphs();
        RefreshDetails();
        if (Tabs.SelectedIndex == 3) RefreshHistory();
    }

    private void SelectFromGraph(BssInfo b)
    {
        if (!_rowMap.TryGetValue(b.Key, out var row)) return;
        if (!_view.Contains(row))
        {
            F24.IsChecked = F5.IsChecked = F6.IsChecked = true;
            GroupCheck.IsChecked = false; ShowStaleCheck.IsChecked = true; FilterBox.Text = "";
            _view.Refresh();
        }
        NetGrid.SelectedItem = row;
        NetGrid.ScrollIntoView(row);
    }

    private void RefreshDetails()
    {
        var row = NetGrid.SelectedItem as BssRow;
        if (row == null || _mon == null)
        {
            DetTitle.Text = "Select a network";
            DetSub.Text = ""; DetSparkLabel.Text = "";
            DetSpark.SetPoints(Array.Empty<double>());
            foreach (var tb in _det.Values) tb.Text = "";
            return;
        }
        var b = row.Info;
        DetTitle.Text = b.DisplaySsid;
        DetTitle.Foreground = Palette.B(Palette.ForName(b.ColorKey));
        DetSub.Text = $"{b.Rssi} dBm · {ChannelMath.Describe(b.Rssi)}" + (row.IsConnected ? " · connected" : "");
        var hist = _mon.GetHistory(b.Key);
        DetSpark.Stroke = Palette.ForName(b.ColorKey);
        DetSpark.SetPoints(hist.Select(h => (double)h.rssi).TakeLast(120).ToList());
        DetSparkLabel.Text = hist.Count > 1 ? $"Last {FormatSpan(hist[^1].utc - hist[Math.Max(0, hist.Count - 120)].utc)} · min {hist.TakeLast(120).Min(h => h.rssi)} / max {hist.TakeLast(120).Max(h => h.rssi)} dBm" : "";

        _det["BSSID"].Text = b.DisplayBssid;
        _det["Vendor"].Text = b.Vendor.Length > 0 ? b.Vendor : "unknown";
        _det["Band"].Text = ChannelMath.BandName(b.Band);
        _det["Channel"].Text = $"{b.Channel}  ({b.FreqMHz} MHz){(b.Dfs ? " · DFS" : "")}";
        _det["Width"].Text = $"{b.WidthLabel} MHz";
        _det["Centre"].Text = $"{b.CenterFreqMHz:0} MHz  ({b.LowFreq:0}–{b.HighFreq:0})";
        _det["Standard"].Text = $"{b.Standard} · {b.GenLabel}";
        _det["MIMO"].Text = b.Streams > 0 ? $"{b.Streams} spatial stream{(b.Streams > 1 ? "s" : "")}" : "—";
        _det["AP max PHY"].Text = b.MaxPhyMbps is double m ? $"{m:0} Mbps" : "—";
        _det["Security"].Text = b.Security;
        _det["Cipher"].Text = b.Cipher;
        _det["PMF"].Text = b.Pmf;
        _det["WPS"].Text = b.Wps ? "Enabled (consider turning off)" : "Off";
        _det["Clients"].Text = b.StationCount?.ToString(CultureInfo.InvariantCulture) ?? "not advertised";
        _det["Channel busy"].Text = b.ChannelUtilisation is double u ? $"{u * 100:0}%" : "not advertised";
        _det["BSS colour"].Text = b.BssColor?.ToString(CultureInfo.InvariantCulture) ?? "—";
        _det["Multi-link"].Text = b.MultiLink ? "Yes (Wi-Fi 7 MLO)" : "No";
        _det["Country"].Text = b.Country ?? "—";
        _det["Beacon"].Text = b.BeaconIntervalTu > 0 ? $"{b.BeaconIntervalTu * 1.024:0} ms" : "—";
        _det["AP uptime"].Text = b.ApUptime is TimeSpan t ? FormatSpan(t) : "—";
        _det["First seen"].Text = row.FirstSeenUtc.ToLocalTime().ToString("HH:mm:ss");
        _det["Last seen"].Text = row.LastSeen;
    }

    // ================================================================== channels / history / advisor

    private void ChBand_Click(object sender, RoutedEventArgs e)
    {
        bool all = ChAll.IsChecked == true;
        Card24.Visibility = all || Ch24.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        Card5.Visibility = all || Ch5.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        Card6.Visibility = all || Ch6.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        for (int i = 0; i < 3; i++)
        {
            var card = (FrameworkElement)ChannelGrid.Children[i];
            ChannelGrid.RowDefinitions[i].Height = card.Visibility == Visibility.Visible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        }
    }

    private void History_Click(object sender, RoutedEventArgs e) => RefreshHistory();

    private void RefreshHistory()
    {
        if (_mon == null) return;
        Hist.WindowMinutes = H5.IsChecked == true ? 5 : H30.IsChecked == true ? 30 : 10;
        var active = _scan.Where(b => (FreshRef(DateTime.UtcNow) - b.SeenUtc).TotalSeconds < 60).ToList();
        List<BssInfo> pick;
        bool byIndex = false;
        if (HMine.IsChecked == true && _conn.Connected)
        {
            pick = _scan.Where(b => b.Ssid == _conn.Ssid && !b.Hidden).OrderByDescending(b => b.Rssi).ToList();
            byIndex = true;
        }
        else if (HSel.IsChecked == true && _selected.Count > 0)
            pick = _scan.Where(b => _selected.Contains(b.Key)).OrderByDescending(b => b.Rssi).ToList();
        else
            pick = active.OrderByDescending(b => b.Rssi).Take(10).ToList();

        int i = 0;
        var dupNames = pick.GroupBy(b => b.DisplaySsid).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        var series = pick.Select(b =>
        {
            string name = dupNames.Contains(b.DisplaySsid)
                ? $"{b.DisplaySsid} · {ChannelMath.BandName(b.Band)} ch{b.Channel} · {(Privacy.On ? b.DisplayBssid : "…" + b.Bssid[^5..])}"
                : $"{b.DisplaySsid} · ch{b.Channel}";
            var col = byIndex || dupNames.Contains(b.DisplaySsid) ? Palette.ForIndex(i) : Palette.ForName(b.ColorKey);
            i++;
            return new HistorySeries(name, col, _mon.GetHistory(b.Key), b.Key == _conn.BssidKey && _conn.Connected);
        }).ToList();
        Hist.SetSeries(series);
    }

    private void ExcludeOwn_Click(object sender, RoutedEventArgs e)
    {
        _settings.ExcludeOwnInAdvisor = ExcludeOwnCheck.IsChecked == true;
        RefreshAdvisor();
    }

    private void RefreshAdvisor()
    {
        if (_mon == null) return;
        AdvisorPanel.Children.Clear();
        var mine = _conn.Connected ? _mon.GetBss(_conn.BssidKey) : null;
        var active = _scan.Where(b => (FreshRef(DateTime.UtcNow) - b.SeenUtc).TotalSeconds < 45).ToList();
        var pool = _settings.ExcludeOwnInAdvisor ? ChannelAdvisor.ExcludeOwn(active, mine, _conn.Connected ? _conn.Ssid : null).ToList() : active;
        if (!_settings.ExcludeOwnInAdvisor && mine != null) pool = pool.Where(b => b.Key != mine.Key).ToList();

        if (active.Count == 0)
        {
            AdvisorPanel.Children.Add(new TextBlock { Text = "Waiting for scan results…", Foreground = (Brush)FindResource("Muted") });
            return;
        }

        foreach (var band in new[] { WifiBand.Band24, WifiBand.Band5, WifiBand.Band6 })
        {
            int inBand = active.Count(b => b.Band == band);
            bool myBand = mine?.Band == band;
            if (inBand == 0 && !myBand && band == WifiBand.Band6) continue;
            var opts = ChannelAdvisor.Evaluate(band, pool, mine);
            AdvisorPanel.Children.Add(BuildAdvisorCard(band, opts, mine, pool.Count(b => b.Band == band)));
        }
    }

    private Border BuildAdvisorCard(WifiBand band, List<ChannelOption> opts, BssInfo? mine, int neighbours)
    {
        var card = new Border { Style = (Style)FindResource("Card"), Margin = new Thickness(0, 0, 0, 12) };
        var sp = new StackPanel();
        card.Child = sp;
        sp.Children.Add(new TextBlock { Text = $"{ChannelMath.BandName(band).ToUpperInvariant()} · {neighbours} NEIGHBOURING ACCESS POINTS", Style = (Style)FindResource("H") });

        // Summary sentence
        string summary;
        var cur = opts.FirstOrDefault(o => o.IsCurrent);
        if (mine != null && mine.Band == band)
        {
            var bestSame = opts.Where(o => o.Width == (band == WifiBand.Band24 ? 20 : mine.WidthMHz) && o.IsBest).FirstOrDefault();
            if (cur != null && bestSame != null && bestSame != cur && bestSame.Score - cur.Score >= 15)
                summary = $"You're on {cur.Label} (score {cur.Score:0}). {bestSame.Label} looks cleaner (score {bestSame.Score:0}) — worth trying.";
            else if (cur != null)
                summary = $"You're on {cur.Label} (score {cur.Score:0}). That's already one of the better choices here.";
            else
                summary = $"You're on channel {mine.Channel} at {mine.WidthLabel} MHz — not a standard block, so it isn't scored.";
        }
        else
        {
            var b = opts.Where(o => o.IsBest).OrderByDescending(o => o.Score).FirstOrDefault();
            summary = b != null ? $"Cleanest option: {b.Label} (score {b.Score:0})." : "";
        }
        if (band == WifiBand.Band24) summary += "  On 2.4 GHz stick to 20 MHz on 1, 6 or 11 — 40 MHz there hurts everyone.";
        sp.Children.Add(new TextBlock { Text = summary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });

        var grid = new Grid();
        foreach (var w in new[] { 170.0, 150, 44, 92, 92, 100, 0 })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w) });
        int r = 0;
        void Header(params string[] h)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int c = 0; c < h.Length; c++)
            {
                var t = new TextBlock { Text = h[c], Foreground = (Brush)FindResource("Muted"), FontSize = 11, Margin = new Thickness(0, 0, 8, 6) };
                Grid.SetRow(t, r); Grid.SetColumn(t, c); grid.Children.Add(t);
            }
            r++;
        }
        Header("Channel block", "Score", "", "Neighbours", "Strongest", "Interference", "Notes");
        int lastWidth = -1;
        foreach (var o in opts)
        {
            if (lastWidth != -1 && o.Width != lastWidth)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
                r++;
            }
            lastWidth = o.Width;
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
            var label = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            label.Children.Add(new TextBlock { Text = o.Label, FontWeight = o.IsCurrent ? FontWeights.SemiBold : FontWeights.Normal });
            if (o.IsCurrent) label.Children.Add(Badge("YOU", Palette.Text));
            if (o.IsBest) label.Children.Add(Badge("BEST", Palette.Good));
            Add(label, 0);
            Add(new ScoreBar { Value = o.Score, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) }, 1);
            Add(Txt($"{o.Score:0}", true), 2);
            Add(Txt(o.Neighbours.ToString(CultureInfo.InvariantCulture)), 3);
            Add(Txt(o.StrongestText), 4);
            Add(Txt(o.InterferenceText), 5);
            Add(new TextBlock { Text = o.Notes, Foreground = (Brush)FindResource("Muted"), FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }, 6);
            r++;
        }
        sp.Children.Add(grid);
        return card;

        void Add(UIElement el, int col) { Grid.SetRow(el, r); Grid.SetColumn(el, col); grid.Children.Add(el); }
        TextBlock Txt(string s, bool mono = false) => new()
        {
            Text = s, VerticalAlignment = VerticalAlignment.Center,
            FontFamily = mono ? (FontFamily)FindResource("MonoFont") : (FontFamily)FindResource("UiFont")
        };
    }

    private static Border Badge(string text, Color c) => new()
    {
        Background = Palette.B(c, 0.15), BorderBrush = Palette.B(c, 0.6), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 0, 5, 1), Margin = new Thickness(8, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 9.5, FontWeight = FontWeights.Bold, Foreground = Palette.B(c) }
    };

    // ================================================================== events, status

    private void AddEvent(EventEntry ev)
    {
        _events.Insert(0, ev);
        while (_events.Count > 500) _events.RemoveAt(_events.Count - 1);
    }

    private void ClearEvents_Click(object sender, RoutedEventArgs e) => _events.Clear();

    private void SlowTick()
    {
        if (_closed) return;
        var now = DateTime.UtcNow;
        var fresh = FreshRef(now);
        foreach (var r in _rows) r.Tick(fresh);
        if (NetGrid.SelectedItem is BssRow sel) _det["Last seen"].Text = sel.LastSeen;

        UpdateScanPolicy();
        string scanAge = _mon?.LastScanUtc is DateTime t ? $"last scan {FormatSpan(now - t)} ago" : "no scan yet";
        StatusLeft.Text = _status;
        StatusRight.Text = $"{scanAge} · live {_settings.PollMs} ms" + (_scanState.Length > 0 ? " · " + _scanState : "")
            + (Privacy.On ? " · screenshot mode" : "");
        UpdatePing(now);

        if (_logger != null)
            RecText.Text = $"Recording {FormatSpan(DateTime.Now - _logger.StartedLocal)} · {_logger.Rows:N0} rows";

        if (_spot != null)
        {
            SpotProgress.Width = 220 * _spot.Progress;
            SpotStatus.Text = $"Measuring… {Math.Max(0, _spot.Seconds - (now - _spot.StartUtc).TotalSeconds):0}s left";
            if (_spot.Done) FinishSpot();
        }
    }

    // ================================================================== header actions

    private void ScanNow_Click(object sender, RoutedEventArgs e) => _mon?.ScanNow();

    private void Record_Click(object sender, RoutedEventArgs e)
    {
        if (_logger == null)
        {
            try
            {
                var lg = new CsvLogger();
                lg.Faulted += msg => Dispatcher.BeginInvoke(() =>
                {
                    if (_logger != lg) return;
                    _logger = null; lg.Dispose();
                    RecordBtn.IsChecked = false; RecText.Text = "Record";
                    AddEvent(new EventEntry { Kind = EventKind.Warning, Text = "Recording stopped — couldn't write log: " + msg });
                });
                _logger = lg;
                RecText.Text = "Recording…";
                AddEvent(new EventEntry { Kind = EventKind.Info, Text = $"Recording to {_logger.Folder}" });
            }
            catch (Exception ex)
            {
                RecordBtn.IsChecked = false;
                MessageBox.Show(this, "Couldn't start recording:\n" + ex.Message, "AirScope", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        else
        {
            var l = _logger; _logger = null;
            l.Dispose();
            RecText.Text = "Record";
            AddEvent(new EventEntry { Kind = EventKind.Info, Text = $"Saved {Path.GetFileName(l.LinkPath)} and {Path.GetFileName(l.ScanPath)}" +
                (File.Exists(l.PingPath) ? $" and {Path.GetFileName(l.PingPath)}" : "") });
        }
    }

    private void Mini_Click(object sender, RoutedEventArgs e)
    {
        if (_mini == null)
        {
            _mini = new MiniWindow(_settings);
            _mini.TopmostChanged += on => MiniTopCheck.IsChecked = on;
            _miniClosedHandler = (_, _) =>
            {
                _mini = null;
                if (_closed) return;
                UpdateScanPolicy();
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();
            };
            _mini.Closed += _miniClosedHandler;
            _mini.Show();
            WindowState = WindowState.Minimized;
            UpdateScanPolicy();
        }
        else _mini.Activate();
    }

    private void PollBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _settings.PollMs = PollBox.SelectedIndex switch { 0 => 100, 1 => 250, 2 => 500, _ => 1000 };
    }

    private void ScanBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _settings.ScanSeconds = ScanBox.SelectedIndex switch { 0 => 3, 1 => 5, 2 => 10, 3 => 20, 4 => 30, _ => 60 };
        UpdateScanPolicy();
    }

    private void MiniTop_Click(object sender, RoutedEventArgs e)
    {
        _settings.MiniAlwaysOnTop = MiniTopCheck.IsChecked == true;
        if (_mini != null) _mini.Topmost = _settings.MiniAlwaysOnTop;
    }

    private void SettingsPopup_Closed(object? sender, EventArgs e) { SettingsBtn.IsChecked = false; _settings.Save(); }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(CsvLogger.LogsFolder);
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { CsvLogger.LogsFolder }, UseShellExecute = false });
        }
        catch { }
    }

    private void OpenLocation_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:privacy-location") { UseShellExecute = true }); } catch { }
    }

    private void RatesCheck_Click(object sender, RoutedEventArgs e)
    {
        Live.ShowRates = _settings.ShowRates = RatesCheck.IsChecked == true;
        Live.InvalidateVisual();
    }

    private void PingCheck_Click(object sender, RoutedEventArgs e)
    {
        _settings.PingRouter = PingCheck.IsChecked == true;
        PingCard.Visibility = _settings.PingRouter ? Visibility.Visible : Visibility.Collapsed;
        ResetPingEvents();
        if (_settings.PingRouter && _pinger != null && _mon != null && _mon.InterfaceId != Guid.Empty)
        {
            _pinger.Start(_mon.InterfaceId);
            Live.Pings = _pinger;
        }
        else
        {
            Live.Pings = null;
            _pinger?.Stop();
        }
        Live.MarkDirty();
        Live.InvalidateVisual();
    }

    private void ClearGraph_Click(object sender, RoutedEventArgs e)
    {
        _mon?.ResetLive();
        _pinger?.Clear();
        ResetPingEvents();
        _retryPeak = null;
        _lastStatsUtc = DateTime.MinValue;
        StatMin.Text = StatAvg.Text = StatMax.Text = StatJit.Text = "—";
        StatNote.Text = "Collecting…";
        PingNow.Text = PingAvg.Text = PingP95.Text = PingLoss.Text = "—";
        PingSpark.SetPoints(Array.Empty<double>());
        Live.MarkDirty();
        Live.InvalidateVisual();
    }

    private void ScreenshotCheck_Click(object sender, RoutedEventArgs e)
    {
        Privacy.On = _settings.HideIdentifiers = ScreenshotCheck.IsChecked == true;
        // Re-render everything that shows a name or address.
        foreach (var r in _rows) r.RefreshAll();
        EventList.Items.Refresh();
        SpotGrid.Items.Refresh();
        RefreshChannelGraphs();
        RefreshDetails();
        RefreshVisibleTab();
        UpdateLive(_latestConn);
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        Live.TogglePause();
        PauseBtn.Content = Live.Paused ? "▶" : "❚❚";
    }

    private void BuildWindowChips()
    {
        foreach (var (label, sec) in new[] { ("30s", 30), ("2m", 120), ("5m", 300), ("15m", 900), ("30m", 1800) })
        {
            var rb = new RadioButton
            {
                Content = label, GroupName = "win", Style = (Style)FindResource("ChipRadio"),
                IsChecked = sec == _settings.GraphWindowSeconds
            };
            rb.Click += (_, _) =>
            {
                Live.WindowSeconds = _settings.GraphWindowSeconds = sec;
                StatsHeader.Text = $"SIGNAL OVER LAST {label.ToUpperInvariant()}";
                _lastStatsUtc = DateTime.MinValue;
                Live.InvalidateVisual();
            };
            if (rb.IsChecked == true) StatsHeader.Text = $"SIGNAL OVER LAST {label.ToUpperInvariant()}";
            WindowChips.Children.Add(rb);
        }
    }

    // ================================================================== spot tests

    private void SpotLabel_Changed(object sender, TextChangedEventArgs e) =>
        SpotHint.Visibility = SpotLabel.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void SpotRun_Click(object sender, RoutedEventArgs e)
    {
        if (_spot != null)
        {
            _spot = null;
            SpotRun.Content = "Start test";
            SpotStatus.Text = "Cancelled";
            SpotProgress.Width = 0;
            UpdateScanPolicy();
            ScanNowBtn.IsEnabled = _mon != null;
            return;
        }
        if (!_conn.Connected) { SpotStatus.Text = "Connect to a network first."; return; }
        int secs = SpotDuration.SelectedIndex switch { 0 => 10, 1 => 30, 2 => 60, _ => 120 };
        _spot = new SpotTestRunner(SpotLabel.Text, secs);
        SpotRun.Content = "Cancel";
        SpotStatus.Text = "Measuring…";
        UpdateScanPolicy();   // no scans during a test, so they can't skew it
        ScanNowBtn.IsEnabled = false;
    }

    private void FinishSpot()
    {
        var runner = _spot;
        _spot = null;
        SpotRun.Content = "Start test";
        SpotProgress.Width = 0;
        UpdateScanPolicy();
        ScanNowBtn.IsEnabled = _mon != null;
        if (runner != null && _mon != null) _mon.Scans.CopySince(runner.StartUtc.AddSeconds(-10), DateTime.UtcNow, _marksWork);
        else _marksWork.Clear();
        var res = runner?.Finish(_mon?.GetBss(_conn.BssidKey), _marksWork);
        if (res == null) { SpotStatus.Text = "No samples collected (were you disconnected?)"; return; }
        if (runner != null && _pinger != null && _settings.PingRouter)
        {
            _pinger.CopySince(runner.StartUtc, _pingWork);
            _pingWork.RemoveAll(p => p.Utc > runner.StartUtc.AddSeconds(runner.Seconds));
            // Only if ping ran for (nearly) the whole test – judged by time, since lost pings take longer than replies.
            bool covered = _pingWork.Count >= 3 &&
                (_pingWork[0].Utc - runner.StartUtc).TotalSeconds <= 3 &&
                (runner.StartUtc.AddSeconds(runner.Seconds) - _pingWork[^1].Utc).TotalSeconds <= 3 &&
                _pingWork.Zip(_pingWork.Skip(1)).All(x => (x.Second.Utc - x.First.Utc).TotalSeconds <= 3);
            if (covered)
            {
                var ps = PingStats.Of(_pingWork);
                res.PingAvg = ps.Avg; res.PingP95 = ps.P95; res.PingLossPct = ps.LossPct;
            }
        }
        if (!_spots.Any(s => s.IsBaseline)) res.IsBaseline = true;
        _spots.Insert(0, res);
        RecomputeSpotDeltas();
        SpotTestStore.Save(_spots);
        SpotStatus.Text = $"Done: {res.RssiAvg:0.0} dBm avg, ±{res.RssiStdDev:0.0}";
        SpotLabel.Text = "";
    }

    private void RecomputeSpotDeltas()
    {
        var b = _spots.FirstOrDefault(s => s.IsBaseline) ?? _spots.LastOrDefault();
        foreach (var s in _spots)
        {
            if (b == null || s == b) { s.DeltaRssi = s == b ? "base" : ""; s.DeltaTx = s == b ? "base" : ""; continue; }
            double d = s.RssiAvg - b.RssiAvg;
            s.DeltaRssi = $"{(d >= 0 ? "+" : "")}{d:0.0}";
            s.DeltaTx = b.TxAvg > 0 ? $"{(s.TxAvg >= b.TxAvg ? "+" : "")}{(s.TxAvg / b.TxAvg - 1) * 100:0}%" : "";
        }
        SpotGrid.Items.Refresh();
    }

    private void SpotBaseline_Click(object sender, RoutedEventArgs e)
    {
        if (SpotGrid.SelectedItem is not SpotTestResult sel) return;
        foreach (var s in _spots) s.IsBaseline = s == sel;
        RecomputeSpotDeltas();
        SpotTestStore.Save(_spots);
    }

    private void SpotDelete_Click(object sender, RoutedEventArgs e)
    {
        var sel = SpotGrid.SelectedItems.OfType<SpotTestResult>().ToList();
        if (sel.Count == 0) return;
        if (MessageBox.Show(this, $"Delete {sel.Count} test result(s)?", "AirScope", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        foreach (var s in sel) _spots.Remove(s);
        RecomputeSpotDeltas();
        SpotTestStore.Save(_spots);
    }

    private void SpotExport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = $"airscope-spot-tests-{DateTime.Now:yyyyMMdd}.csv", Filter = "CSV|*.csv" };
        if (dlg.ShowDialog(this) != true) return;
        var sb = new StringBuilder("label,taken,ssid,bssid,band,channel,seconds,samples,rssi_avg,rssi_min,rssi_max,rssi_stddev,tx_avg_mbps,rx_avg_mbps,retry_pct,roams,ping_avg_ms,ping_p95_ms,ping_loss_pct,baseline\n");
        foreach (var s in _spots)
            sb.AppendLine(string.Join(",", CsvLogger.Esc(s.Label), s.TakenLocal.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                CsvLogger.Esc(s.Ssid), CsvLogger.Esc(s.Bssid), CsvLogger.Esc(s.Band), s.Channel, s.Seconds, s.Samples,
                F(s.RssiAvg), F(s.RssiMin), F(s.RssiMax), F(s.RssiStdDev), F(s.TxAvg), F(s.RxAvg),
                s.RetryAvg is double r ? F(r) : "", s.Roams,
                s.PingAvg is double pa ? F(pa) : "", s.PingP95 is double pp ? F(pp) : "", s.PingLossPct is double pl ? F(pl) : "",
                s.IsBaseline ? "yes" : ""));
        try { File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true)); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "AirScope", MessageBoxButton.OK, MessageBoxImage.Warning); }

        static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    }

    // ================================================================== router ping

    private string? MiniPingText(DateTime now)
    {
        if (!_settings.PingRouter || _pinger?.Last is not PingSample p || (now - p.Utc).TotalSeconds > 3) return null;
        return p.Ms is double ms ? $"{PingStats.Ms(ms)} ms" : "ping lost";
    }

    private void ResetPingEvents()
    {
        _pingEvalUpTo = DateTime.UtcNow;
        _pending.Clear();
    }

    private static bool IsSpike(PingSample p, double threshold) => p.Ms is not double ms || ms >= threshold;

    private readonly List<PingSample> _spikeWork = new(), _spikeClean = new();

    /// <summary>Once a second: refresh the ping card, and report latency spikes that weren't caused by a scan.</summary>
    private void UpdatePing(DateTime now)
    {
        if (!_settings.PingRouter) return;
        if (_pinger == null || _mon == null) { PingNote.Text = "The Windows Wi-Fi API isn't available, so there's no adapter to ping through."; return; }

        var status = _pinger.Current;
        _pinger.CopySince(now.AddSeconds(-Live.WindowSeconds), _pingWork);
        var st = PingStats.Of(_pingWork);
        // One spike threshold everywhere (card, graph strip, events): 50 ms, or 3× the recent median on slower links.
        var okMs = _pingWork.Where(p => p.Ms.HasValue && p.Utc >= now.AddSeconds(-60)).Select(p => p.Ms!.Value).OrderBy(x => x).ToList();
        double median = okMs.Count > 0 ? okMs[okMs.Count / 2] : 0;
        double threshold = Math.Max(50, median * 3);
        Live.PingSpikeMs = threshold;

        var last = _pinger.Last;
        PingNow.Text = last is PingSample l && (now - l.Utc).TotalSeconds < 3 ? (l.Ms is double m ? PingStats.Ms(m) : "lost") : "—";
        PingAvg.Text = PingStats.Ms(st.Avg);
        PingP95.Text = PingStats.Ms(st.P95);
        PingLoss.Text = st.Sent == 0 ? "—" : $"{st.LossPct:0.#}%";
        PingHeader.ToolTip = status.Target is { } ip ? $"Pinging {ip} (this adapter's default gateway)" : null;
        var recent = _pingWork.Where(p => p.Utc >= now.AddSeconds(-60)).Select(p => p.Ms ?? Math.Max(threshold, 100)).ToList();
        PingSpark.Max = Math.Max(20, recent.Count > 0 ? recent.Max() : 20);
        PingSpark.SetPoints(recent);

        // Which spikes happened during scans? One sweep over both time-ordered lists.
        _spikeWork.Clear();
        foreach (var p in _pingWork) if (IsSpike(p, threshold)) _spikeWork.Add(p);
        _mon.Scans.CopySince(now.AddSeconds(-Live.WindowSeconds - 10), now, _marksWork);
        ScanTimeline.ExcludeCovered(_spikeWork, p => p.Utc, _marksWork, _spikeClean);
        int spikes = _spikeWork.Count, notScan = _spikeClean.Count, inScan = spikes - notScan;

        string sharedNote = status.SharedSubnet ? " Another adapter (e.g. Ethernet) is on the same network, so Windows may be sending these pings over it rather than Wi-Fi." : "";
        PingNote.Text = !_conn.Connected ? "Not connected." :
            status.Error != null ? "Ping error: " + status.Error :
            status.Target == null ? "No router address on this Wi-Fi adapter's network yet." :
            status.NoReplies ? "Your router doesn't answer pings, so latency can't be measured this way. (Pinging slowly in case that changes.)" :
            st.Sent < 5 ? "Collecting…" :
            (spikes == 0 ? $"No pings over {threshold:0} ms or lost in this window." :
             notScan == 0 ? $"{spikes} slow/lost ping{(spikes > 1 ? "s" : "")}, all during scans." :
             $"{spikes} slow (≥{threshold:0} ms) or lost ping{(spikes > 1 ? "s" : "")}: {inScan} during scans, {notScan} not.") + sharedNote;

        // Events only for spikes a scan doesn't explain. Judged a few seconds late, because Windows only reports
        // its own scans when they finish. A single lost ping on its own isn't worth an event.
        var evalTo = now - _mon.Scans.ExternalLead - TimeSpan.FromSeconds(1);
        if (evalTo > _pingEvalUpTo)
        {
            // Until the router has answered once, losses mean "doesn't answer pings", not a bad link.
            if (status.AnyReply && !status.NoReplies)
                foreach (var p in _spikeClean)
                    if (p.Utc > _pingEvalUpTo && p.Utc <= evalTo) _pending.Add(p);
            _pingEvalUpTo = evalTo;
        }
        if (!status.AnyReply || status.NoReplies) _pending.Clear();
        _pending.RemoveAll(p => p.Utc < now.AddSeconds(-90));   // only spikes close together add up
        int pSlow = _pending.Count(p => p.Ms.HasValue), pLost = _pending.Count - pSlow;
        if ((pSlow > 0 || pLost >= 2) && (now - _lastPingEventUtc).TotalSeconds >= 60)
        {
            double worst = _pending.Where(p => p.Ms.HasValue).Select(p => p.Ms!.Value).DefaultIfEmpty(0).Max();
            var parts = new List<string>();
            if (pSlow > 0) parts.Add(pSlow == 1 ? $"one at {worst:0} ms" : $"{pSlow} slow, worst {worst:0} ms");
            if (pLost > 0) parts.Add($"{pLost} lost");
            AddEvent(new EventEntry { Kind = EventKind.Warning, Text = $"Router ping spikes with no scan running: {string.Join(", ", parts)}" });
            _lastPingEventUtc = now;
            _pending.Clear();
        }
    }

    // ================================================================== helpers / shutdown

    private void BuildKeyValueRows(Grid g, Dictionary<string, TextBlock> map, params string[] keys)
    {
        for (int i = 0; i < keys.Length; i++)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var k = new TextBlock { Text = keys[i], Style = (Style)FindResource("Key") };
            var v = new TextBlock { Text = "—", Style = (Style)FindResource("Val") };
            v.SetBinding(ToolTipProperty, new Binding(nameof(TextBlock.Text)) { Source = v });
            Grid.SetRow(k, i); Grid.SetRow(v, i);
            Grid.SetColumn(v, 1);
            g.Children.Add(k); g.Children.Add(v);
            map[keys[i]] = v;
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _closed = true;
        _uiTimer.Stop();
        if (_mini != null)
        {
            if (_miniClosedHandler != null) _mini.Closed -= _miniClosedHandler;
            _mini.Close();
        }
        _settings.Save();
        _pinger?.Dispose();
        _mon?.Dispose();
        var lg = _logger; _logger = null; lg?.Dispose();
        _wlan?.Dispose();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    internal static void DarkTitleBar(Window w)
    {
        try
        {
            var h = new WindowInteropHelper(w).Handle;
            int on = 1;
            if (DwmSetWindowAttribute(h, 20, ref on, sizeof(int)) != 0)   // DWMWA_USE_IMMERSIVE_DARK_MODE
                DwmSetWindowAttribute(h, 19, ref on, sizeof(int));        // pre-20H1 value
        }
        catch { }
    }
}
