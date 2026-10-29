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

    /// <summary>Mask any MAC addresses and known network names inside free text (event messages).</summary>
    public static string Text(string text)
    {
        if (!_on || string.IsNullOrEmpty(text)) return text;
        text = MacPattern.Replace(text, m => Mac(m.Value));
        string own = _ownSsid;
        List<string> known;
        lock (Lock) known = Names.Keys.ToList();
        if (own.Length > 0 && !known.Contains(own)) known.Add(own);
        // Very short names (e.g. "a") would mangle unrelated words, so only replace names of 3+ characters.
        foreach (var ssid in known.Where(s => s.Length >= 3).OrderByDescending(s => s.Length))
            text = text.Replace(ssid, Name(ssid));
        return text;
    }
}
