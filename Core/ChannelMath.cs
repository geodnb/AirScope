namespace AirScope.Core;

public enum WifiBand { Unknown = 0, Band24 = 1, Band5 = 2, Band6 = 3 }

public static class ChannelMath
{
    public static WifiBand BandFromFreq(int mhz) => mhz switch
    {
        >= 2400 and <= 2500 => WifiBand.Band24,
        >= 5150 and < 5925 => WifiBand.Band5,
        >= 5925 and <= 7125 => WifiBand.Band6,
        _ => WifiBand.Unknown
    };

    public static int ChannelFromFreq(int mhz)
    {
        if (mhz == 2484) return 14;
        return BandFromFreq(mhz) switch
        {
            WifiBand.Band24 => (mhz - 2407) / 5,
            WifiBand.Band5 => (mhz - 5000) / 5,
            WifiBand.Band6 => mhz == 5935 ? 2 : (mhz - 5950) / 5,
            _ => 0
        };
    }

    public static double FreqFromChannel(WifiBand band, int ch) => band switch
    {
        WifiBand.Band24 => ch == 14 ? 2484 : 2407 + 5 * ch,
        WifiBand.Band5 => 5000 + 5 * ch,
        WifiBand.Band6 => ch == 2 ? 5935 : 5950 + 5 * ch,
        _ => 0
    };

    /// <summary>5 GHz channels that need radar detection (DFS) in the UK/EU.</summary>
    public static bool IsDfs(WifiBand band, int ch) => band == WifiBand.Band5 && ch >= 52 && ch <= 144;

    /// <summary>UK 6 GHz is licence-exempt for indoor low-power only in 5925–6425 MHz (channels 1–93).</summary>
    public static bool IsUk6GhzLowerBand(int ch) => ch >= 1 && ch <= 93;

    public static string BandName(WifiBand b) => b switch
    {
        WifiBand.Band24 => "2.4 GHz",
        WifiBand.Band5 => "5 GHz",
        WifiBand.Band6 => "6 GHz",
        _ => "?"
    };

    /// <summary>Frequency span (MHz) to draw on a channel graph.</summary>
    public static (double min, double max) BandSpan(WifiBand b) => b switch
    {
        WifiBand.Band24 => (2397, 2497),
        WifiBand.Band5 => (5160, 5905),
        WifiBand.Band6 => (5925, 7135),
        _ => (0, 1)
    };

    /// <summary>Channel numbers shown as tick labels.</summary>
    public static IEnumerable<int> TickChannels(WifiBand b) => b switch
    {
        WifiBand.Band24 => Enumerable.Range(1, 13).Append(14),
        WifiBand.Band5 => new[] { 36, 40, 44, 48, 52, 56, 60, 64, 100, 104, 108, 112, 116, 120, 124, 128, 132, 136, 140, 144, 149, 153, 157, 161, 165, 169, 173, 177 },
        WifiBand.Band6 => Enumerable.Range(0, 59).Select(i => 1 + i * 4),
        _ => Array.Empty<int>()
    };

    /// <summary>Overlap (MHz) between two frequency ranges.</summary>
    public static double Overlap(double aLo, double aHi, double bLo, double bHi) =>
        Math.Max(0, Math.Min(aHi, bHi) - Math.Max(aLo, bLo));

    /// <summary>dBm → linear milliwatts.</summary>
    public static double DbmToMw(double dbm) => Math.Pow(10, dbm / 10.0);

    /// <summary>Signal quality 0–100 from RSSI, same curve Windows uses (-100 → 0, -50 → 100).</summary>
    public static int QualityFromRssi(int rssi) => Math.Clamp(2 * (rssi + 100), 0, 100);

    public static string Describe(int rssi) => rssi switch
    {
        >= -55 => "Excellent",
        >= -65 => "Good",
        >= -72 => "Fair",
        >= -80 => "Weak",
        _ => "Unusable"
    };
}
