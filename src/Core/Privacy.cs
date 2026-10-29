using System.Text.RegularExpressions;

namespace AirScope.Core;

/// <summary>
/// Screenshot mode: hides network names and hardware addresses on screen, so a screenshot can be shared without
/// revealing where you live (BSSIDs and SSIDs can be looked up in public Wi-Fi maps). Only affects what's
/// displayed – CSV logs and saved data always keep the real values.
/// Names get stable stand-ins ("My network", "Network 3", "AP 2") for the session, so the views stay readable.
/// </summary>
public static class Privacy
{
    private static volatile bool _on;
    private static readonly object Lock = new();
    private static readonly Dictionary<string, string> Names = new();
    private static readonly Dictionary<string, string> Macs = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Regex MacPattern = new(@"\b[0-9A-Fa-f]{2}(?::[0-9A-Fa-f]{2}){5}\b", RegexOptions.Compiled);
    private static volatile string _ownSsid = "";

    public static bool On { get => _on; set => _on = value; }

    /// <summary>The network we're connected to, shown as "My network".</summary>
    public static string OwnSsid { get => _ownSsid; set => _ownSsid = value ?? ""; }

    public static string Name(string ssid, bool hidden = false)
    {
        if (hidden || string.IsNullOrEmpty(ssid)) return "‹hidden›";
        Register(ssid);
        if (!_on) return ssid;
        if (ssid == _ownSsid) return "My network";
        lock (Lock)
        {
            if (!Names.TryGetValue(ssid, out var alias)) Names[ssid] = alias = $"Network {Names.Count + 1}";
            return alias;
        }
    }

    public static string Mac(string bssid)
    {
        if (!_on || string.IsNullOrEmpty(bssid)) return bssid;
        lock (Lock)
        {
            if (!Macs.TryGetValue(bssid, out var alias)) Macs[bssid] = alias = $"AP {Macs.Count + 1}";
            return alias;
        }
    }

    /// <summary>
    /// Fallback for free text (e.g. spot-test labels): masks MAC addresses and any network name AirScope has seen,
    /// in one pass and only as whole words, so stand-ins can't be re-masked and "ch 100" stays "ch 100" unless a
    /// network is literally called "100". Events don't rely on this – they keep names separate.
    /// </summary>
    public static string Text(string text)
    {
        if (!_on || string.IsNullOrEmpty(text)) return text;
        text = MacPattern.Replace(text, m => Mac(m.Value));
        var names = new List<string>();
        lock (Lock) names.AddRange(Seen);
        if (_ownSsid.Length > 0) names.Add(_ownSsid);
        names = names.Where(n => n.Length > 0).Distinct().OrderByDescending(n => n.Length).ToList();
        if (names.Count == 0) return text;
        var rx = new Regex(@"(?<![\w])(" + string.Join("|", names.Select(Regex.Escape)) + @")(?![\w])");
        return rx.Replace(text, m => Name(m.Value));
    }

    /// <summary>Remember a network name so free text mentioning it can be masked later. Safe from any thread.</summary>
    public static void Register(string ssid)
    {
        if (string.IsNullOrEmpty(ssid)) return;
        lock (Lock) Seen.Add(ssid);
    }

    private static readonly HashSet<string> Seen = new();
}
