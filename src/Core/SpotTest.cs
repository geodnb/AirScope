using System.Text.Json;

namespace AirScope.Core;

/// <summary>A timed measurement of the current link, for A/B comparisons (antennas, router position, channels…).</summary>
public sealed class SpotTestResult
{
    public string Label { get; set; } = "";
    public DateTime TakenLocal { get; set; }
    public int Seconds { get; set; }
    public string Ssid { get; set; } = "";
    public string Bssid { get; set; } = "";
    public int Channel { get; set; }
    public string Band { get; set; } = "";
    public int Samples { get; set; }
    public double RssiAvg { get; set; }
    public double RssiMin { get; set; }
    public double RssiMax { get; set; }
    public double RssiStdDev { get; set; }
    public double RxAvg { get; set; }
    public double TxAvg { get; set; }
    public double? RetryAvg { get; set; }
    public int Roams { get; set; }
    public double? PingAvg { get; set; }
    public double? PingP95 { get; set; }
    public double? PingLossPct { get; set; }

    // Filled in by the UI relative to the chosen baseline
    [System.Text.Json.Serialization.JsonIgnore] public string DeltaRssi { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore] public string DeltaTx { get; set; } = "";
    public bool IsBaseline { get; set; }

    [System.Text.Json.Serialization.JsonIgnore] public string BaselineMark => IsBaseline ? "★" : "";
    [System.Text.Json.Serialization.JsonIgnore] public string TxText => $"{TxAvg:0}";
    [System.Text.Json.Serialization.JsonIgnore] public string RxText => $"{RxAvg:0}";
    [System.Text.Json.Serialization.JsonIgnore] public string LengthText => $"{Seconds}s";

    public string TakenText => TakenLocal.ToString("dd MMM HH:mm");
    public string RssiText => $"{RssiAvg:0.0}";
    public string RangeText => $"{RssiMin:0} … {RssiMax:0}";
    public string StabilityText => $"±{RssiStdDev:0.0}";
    public string RetryText => RetryAvg is double r ? $"{r:0.0}%" : "—";
    [System.Text.Json.Serialization.JsonIgnore] public string PingText => PingAvg is double a ? $"{PingStats.Ms(a)} / {PingStats.Ms(PingP95)}" : "—";
    [System.Text.Json.Serialization.JsonIgnore] public string LossText => PingLossPct is double l ? $"{l:0.#}%" : "—";
}

public sealed class SpotTestRunner
{
    private readonly List<LinkSample> _samples = new();
    private int _roams;
    private ConnectionInfo? _last;
    public DateTime StartUtc { get; } = DateTime.UtcNow;
    public int Seconds { get; }
    public string Label { get; }
    public double Progress => Math.Clamp((DateTime.UtcNow - StartUtc).TotalSeconds / Seconds, 0, 1);
    public bool Done => Progress >= 1;

    public SpotTestRunner(string label, int seconds) { Label = label; Seconds = seconds; }

    private readonly object _lock = new();

    /// <summary>Called from the monitor's poll thread.</summary>
    public void Add(LinkSample s, ConnectionInfo c)
    {
        lock (_lock)
        {
            if (!c.Connected || Done) return;
            if (_last != null && _last.BssidKey != c.BssidKey) _roams++;
            _last = c;
            _samples.Add(s);
        }
    }

    /// <param name="scans">Scan periods during the test; samples inside them are left out (Windows can still scan in the background).</param>
    public SpotTestResult? Finish(BssInfo? apInfo, IReadOnlyList<ScanMark> scans)
    {
        lock (_lock) return FinishLocked(apInfo, scans);
    }

    private SpotTestResult? FinishLocked(BssInfo? apInfo, IReadOnlyList<ScanMark> scans)
    {
        if (_samples.Count == 0 || _last == null) return null;
        var clean = new List<LinkSample>();
        ScanTimeline.ExcludeCovered(_samples, x => x.Utc, scans, clean);
        var use = clean.Count * 2 >= _samples.Count ? clean : _samples;
        var st = Stats.Of(use.Select(x => (double)x.Rssi).ToList());
        var retries = use.Where(x => x.RetryPct.HasValue).Select(x => x.RetryPct!.Value).ToList();
        return new SpotTestResult
        {
            Label = string.IsNullOrWhiteSpace(Label) ? $"Test {DateTime.Now:HH:mm}" : Label.Trim(),
            TakenLocal = StartUtc.ToLocalTime(),
            Seconds = Seconds,
            Ssid = _last.Ssid,
            Bssid = _last.Bssid,
            Channel = _last.Channel,
            Band = apInfo != null ? ChannelMath.BandName(apInfo.Band) : "",
            Samples = use.Count,
            RssiAvg = st.Avg, RssiMin = st.Min, RssiMax = st.Max, RssiStdDev = st.StdDev,
            RxAvg = use.Average(x => x.RxMbps),
            TxAvg = use.Average(x => x.TxMbps),
            RetryAvg = retries.Count > 0 ? retries.Average() : null,
            Roams = _roams
        };
    }
}

public static class SpotTestStore
{
    private static string FilePath => Path.Combine(AppSettings.Folder, "spot-tests.json");

    public static List<SpotTestResult> Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<List<SpotTestResult>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch
        {
            // Unreadable – keep a copy rather than letting the next save overwrite the user's history.
            try { File.Copy(FilePath, FilePath + $".unreadable-{DateTime.Now:yyyyMMdd-HHmmss}", false); } catch { }
        }
        return new();
    }

    public static void Save(IEnumerable<SpotTestResult> items)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(items.ToList(), new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath, true);
        }
        catch { }
    }
}
