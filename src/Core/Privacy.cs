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
    /// Fallback for free text (e.g. spot-test labels, non-network event text): masks MAC addresses and any network
    /// name AirScope has seen, in a single pass and only as whole words, so a stand-in is never masked again and
    /// "ch 100" stays "ch 100" unless a network is literally called "100". Events about networks don't rely on
    /// this – they keep names separate.
    /// </summary>
    public static string Text(string text)
    {
        if (!_on || string.IsNullOrEmpty(text)) return text;
        return Combined().Replace(text, m => m.Groups["mac"].Success ? Mac(m.Value) : Name(m.Value));
    }

    /// <summary>Remember a network name so free text mentioning it can be masked later. Safe from any thread.</summary>
    public static void Register(string ssid)
    {
        if (string.IsNullOrEmpty(ssid)) return;
        lock (Lock) if (Seen.Add(ssid)) _rx = null;
    }

    private static readonly HashSet<string> Seen = new();
    private static Regex? _rx;
    private static string _rxOwn = "";

    /// <summary>Built only when the set of names (or the own network) changes.</summary>
    private static Regex Combined()
    {
        lock (Lock)
        {
            string own = _ownSsid;
            if (own.Length > 0 && Seen.Add(own)) _rx = null;
            if (_rx != null && own == _rxOwn) return _rx;
            var names = Seen.Where(n => n.Length > 0).OrderByDescending(n => n.Length).Select(Regex.Escape).ToList();
            string mac = @"(?<mac>\b[0-9A-Fa-f]{2}(?::[0-9A-Fa-f]{2}){5}\b)";
            string pattern = names.Count == 0 ? mac : mac + @"|(?<![\w])(?:" + string.Join("|", names) + @")(?![\w])";
            _rxOwn = own;
            return _rx = new Regex(pattern, RegexOptions.CultureInvariant);
        }
    }
}
