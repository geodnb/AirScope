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

    public SpotTestResult? Finish(BssInfo? apInfo)
    {
        lock (_lock) return FinishLocked(apInfo);
    }

    private SpotTestResult? FinishLocked(BssInfo? apInfo)
    {
        if (_samples.Count == 0 || _last == null) return null;
        var st = Stats.Of(_samples.Select(x => (double)x.Rssi).ToList());
        var retries = _samples.Where(x => x.RetryPct.HasValue).Select(x => x.RetryPct!.Value).ToList();
        return new SpotTestResult
        {
            Label = string.IsNullOrWhiteSpace(Label) ? $"Test {DateTime.Now:HH:mm}" : Label.Trim(),
            TakenLocal = StartUtc.ToLocalTime(),
            Seconds = Seconds,
            Ssid = _last.Ssid,
            Bssid = _last.Bssid,
            Channel = _last.Channel,
            Band = apInfo != null ? ChannelMath.BandName(apInfo.Band) : "",
            Samples = _samples.Count,
            RssiAvg = st.Avg, RssiMin = st.Min, RssiMax = st.Max, RssiStdDev = st.StdDev,
            RxAvg = _samples.Average(x => x.RxMbps),
            TxAvg = _samples.Average(x => x.TxMbps),
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
        catch { }
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
