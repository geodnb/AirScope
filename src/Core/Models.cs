namespace AirScope.Core;

/// <summary>One access point (BSSID) as seen in a scan.</summary>
public sealed class BssInfo
{
    public ulong Key { get; init; }                 // BSSID as 48-bit int
    public string Bssid { get; init; } = "";
    public string Ssid { get; init; } = "";
    public bool Hidden { get; init; }
    public int Rssi { get; init; }
    public uint LinkQuality { get; init; }
    public int FreqMHz { get; init; }               // primary channel centre
    public int Channel { get; init; }
    public WifiBand Band { get; init; }
    public int WidthMHz { get; init; } = 20;
    public string WidthLabel { get; init; } = "20";
    public double CenterFreqMHz { get; init; }       // centre of the whole occupied width
    public int Generation { get; init; }             // 4,5,6,7 or 0 = legacy
    public string Standard { get; init; } = "";
    public string Security { get; init; } = "";
    public string Cipher { get; init; } = "";
    public string Pmf { get; init; } = "";
    public bool Wps { get; init; }
    public int Streams { get; init; }
    public int? StationCount { get; init; }
    public double? ChannelUtilisation { get; init; } // 0..1
    public int? BssColor { get; init; }
    public bool MultiLink { get; init; }
    public string? Country { get; init; }
    public ushort BeaconIntervalTu { get; init; }
    public TimeSpan? ApUptime { get; init; }
    public double? MaxPhyMbps { get; init; }
    public string Vendor { get; init; } = "";
    public bool LocallyAdministered { get; init; }
    public bool Dfs { get; init; }
    public DateTime SeenUtc { get; init; }
    /// <summary>AP's TSF timer from the beacon/probe response; unchanged means Windows hasn't heard it again.</summary>
    public ulong Tsf { get; init; }

    public double LowFreq => CenterFreqMHz - WidthMHz / 2.0;
    public double HighFreq => CenterFreqMHz + WidthMHz / 2.0;
    /// <summary>Name for display (masked in screenshot mode). Logs and comparisons use <see cref="Ssid"/>.</summary>
    public string DisplaySsid => Privacy.Name(Ssid, Hidden);
    /// <summary>Stable key for colour-coding, so colours don't change when screenshot mode is toggled.</summary>
    public string ColorKey => Hidden ? "‹hidden›" : Ssid;
    public string DisplayBssid => Privacy.Mac(Bssid);
    public string GenLabel => Generation switch { 7 => "Wi-Fi 7", 6 => "Wi-Fi 6", 5 => "Wi-Fi 5", 4 => "Wi-Fi 4", _ => "Legacy" };
}

/// <summary>Live state of the connection the PC is currently using.</summary>
public sealed class ConnectionInfo
{
    public bool Connected { get; init; }
    public string State { get; init; } = "";
    public string Profile { get; init; } = "";
    public string Ssid { get; init; } = "";
    public string Bssid { get; init; } = "";
    public ulong BssidKey { get; init; }
    public int PhyType { get; init; }
    public uint SignalQuality { get; init; }
    public double RxMbps { get; init; }
    public double TxMbps { get; init; }
    public string Auth { get; init; } = "";
    public string Cipher { get; init; } = "";
    public int Channel { get; init; }
    public string PhyName => PhyNames.Name(PhyType);
}

/// <summary>One fast-poll sample of the current link.</summary>
/// <param name="RetryPct">Retries as % of transmitted frames over the last second (raw, jumpy).</param>
/// <param name="RetryAvgPct">Retries over the last ~10 s, leaving out seconds that overlapped a scan.</param>
/// <param name="DuringScan">Taken while one of AirScope's own scans had the radio off-channel.</param>
/// <param name="TxFrames">Frames sent in the counter interval <paramref name="RetryPct"/> covers. Few frames = a noisy percentage.</param>
/// <param name="TxFps">Those frames per second.</param>
/// <param name="TxFpsAvg">Scan-free frames per second behind <paramref name="RetryAvgPct"/>.</param>
/// <param name="TxRetries">Of <paramref name="TxFrames"/>, how many needed a retry.</param>
/// <param name="RetrySeq">Which counter read produced <paramref name="RetryPct"/> (several samples share one), so each is counted once.</param>
public readonly record struct LinkSample(DateTime Utc, int Rssi, double RxMbps, double TxMbps, double? RetryPct,
    double? RetryAvgPct = null, bool DuringScan = false, bool CountersOk = false,
    int TxFrames = 0, double TxFps = 0, double TxFpsAvg = 0, long RetrySeq = 0, int TxRetries = 0);

/// <summary>When neighbour scans are allowed to take the radio off-channel.</summary>
public enum ScanMode
{
    /// <summary>Scan only while a neighbour view (Networks, Channels, History, Advisor) is on screen.</summary>
    Auto = 0,
    /// <summary>Scan every interval, whatever is on screen.</summary>
    Always = 1,
    /// <summary>Never trigger scans; show whatever Windows' own background scans found.</summary>
    Passive = 2
}

public enum EventKind { Info, Roam, Disconnect, Connect, Warning, Good }

public sealed class EventEntry
{
    public DateTime Local { get; init; } = DateTime.Now;
    public EventKind Kind { get; init; }
    /// <summary>The full message with real names and addresses (for anything that isn't the screen).</summary>
    public string Text { get; init; } = "";
    /// <summary>
    /// Optional template with {s0}, {s1}… for network names and {b0}, {b1}… for hardware addresses, so the
    /// screen can mask exactly those parts in screenshot mode instead of guessing them from finished text.
    /// </summary>
    public string? Template { get; init; }
    public string[] Ssids { get; init; } = Array.Empty<string>();
    public string[] Bssids { get; init; } = Array.Empty<string>();

    /// <summary>Text as displayed: network names and addresses masked in screenshot mode.</summary>
    public string Shown => Template == null ? Privacy.Text(Text) : Fill(Template, Ssids, Bssids, Privacy.Name, Privacy.Mac);

    /// <summary>Build an event whose names and addresses can be masked on screen.</summary>
    public static EventEntry Make(EventKind kind, string template, string[] ssids, string[] bssids) => new()
    {
        Kind = kind, Template = template, Ssids = ssids, Bssids = bssids,
        Text = Fill(template, ssids, bssids, s => s, b => b)
    };

    private static string Fill(string t, string[] ssids, string[] bssids, Func<string, bool, string> name, Func<string, string> mac)
    {
        for (int i = 0; i < ssids.Length; i++) t = t.Replace("{s" + i + "}", name(ssids[i], false));
        for (int i = 0; i < bssids.Length; i++) t = t.Replace("{b" + i + "}", mac(bssids[i]));
        return t;
    }

    private static string Fill(string t, string[] ssids, string[] bssids, Func<string, string> name, Func<string, string> mac) =>
        Fill(t, ssids, bssids, (s, _) => name(s), mac);
    public string TimeText => Local.ToString("HH:mm:ss");
    public string Icon => Kind switch
    {
        EventKind.Roam => "⇄",
        EventKind.Disconnect => "✕",
        EventKind.Connect => "✓",
        EventKind.Warning => "!",
        EventKind.Good => "▲",
        _ => "•"
    };
}

public sealed class WlanInterfaceInfo
{
    public Guid Id { get; init; }
    public string Description { get; init; } = "";
    public int State { get; init; }
    public override string ToString() => Description;
}

public static class PhyNames
{
    // DOT11_PHY_TYPE
    public static string Name(int phy) => phy switch
    {
        1 => "FHSS", 2 => "DSSS", 3 => "IR",
        4 => "802.11a", 5 => "802.11b", 6 => "802.11g",
        7 => "802.11n (Wi-Fi 4)", 8 => "802.11ac (Wi-Fi 5)", 9 => "802.11ad",
        10 => "802.11ax (Wi-Fi 6/6E)", 11 => "802.11be (Wi-Fi 7)",
        _ => "Unknown"
    };

    public static string Auth(int a) => a switch
    {
        1 => "Open", 2 => "Shared key", 3 => "WPA-Enterprise", 4 => "WPA-Personal", 5 => "WPA-None",
        6 => "WPA2-Enterprise", 7 => "WPA2-Personal", 8 => "WPA3-Enterprise 192", 9 => "WPA3-Personal",
        10 => "OWE", 11 => "WPA3-Enterprise",
        _ => a == 0 ? "" : $"Auth {a}"
    };

    public static string Cipher(int c) => c switch
    {
        0 => "None", 1 => "WEP-40", 2 => "TKIP", 4 => "CCMP (AES)", 5 => "WEP-104",
        6 => "BIP", 8 => "GCMP-128", 9 => "GCMP-256", 10 => "CCMP-256",
        0x100 => "WPA-Group", 0x101 => "WEP",
        _ => $"Cipher {c}"
    };

    public static string InterfaceState(int s) => s switch
    {
        0 => "Not ready", 1 => "Connected", 2 => "Ad-hoc formed", 3 => "Disconnecting",
        4 => "Disconnected", 5 => "Associating", 6 => "Discovering", 7 => "Authenticating",
        _ => "Unknown"
    };
}
