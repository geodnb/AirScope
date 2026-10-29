using System.IO.Compression;
using System.Reflection;

namespace AirScope.Core;

/// <summary>Offline MAC vendor lookup from the embedded IEEE MA-L registry.</summary>
public static class OuiLookup
{
    private static readonly Lazy<Dictionary<uint, string>> Table = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    private static Dictionary<uint, string> Load()
    {
        var d = new Dictionary<uint, string>(42000);
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("AirScope.oui.txt.gz");
            if (s == null) return d;
            using var gz = new GZipStream(s, CompressionMode.Decompress);
            using var r = new StreamReader(gz);
            string? line;
            while ((line = r.ReadLine()) != null)
            {
                int tab = line.IndexOf('\t');
                if (tab != 6) continue;
                if (uint.TryParse(line.AsSpan(0, 6), System.Globalization.NumberStyles.HexNumber, null, out var k))
                    d[k] = line[(tab + 1)..];
            }
        }
        catch { /* vendor names are cosmetic */ }
        return d;
    }

    public static void Warm() => _ = Table.Value;

    public static bool IsLocallyAdministered(ulong mac) => ((mac >> 40) & 0x02) != 0;

    public static string Vendor(ulong mac)
    {
        if (IsLocallyAdministered(mac)) return "";
        uint oui = (uint)(mac >> 24) & 0xFFFFFF;
        return Table.Value.TryGetValue(oui, out var v) ? v : "";
    }
}
