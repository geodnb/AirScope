namespace AirScope.Core;

public sealed class ChannelOption
{
    public WifiBand Band { get; init; }
    public string Label { get; init; } = "";        // "36–48 (80 MHz)"
    public int Width { get; init; }
    public double LoMHz { get; init; }
    public double HiMHz { get; init; }
    public int Neighbours { get; init; }
    public int? Strongest { get; init; }
    public double InterferenceDbm { get; init; }     // summed, overlap-weighted neighbour power
    public double Score { get; init; }               // 0 (awful) – 100 (clean)
    public bool IsCurrent { get; set; }
    public bool IsBest { get; set; }
    public string Notes { get; init; } = "";
    public string StrongestText => Strongest is int s ? $"{s} dBm" : "—";
    public string InterferenceText => Neighbours == 0 ? "none" : $"{InterferenceDbm:0} dBm";
}

/// <summary>
/// Scores candidate channels by how much neighbour energy overlaps them.
/// Each neighbour contributes its power (mW, from RSSI) × the fraction of its width that
/// overlaps the candidate, inflated by its advertised channel utilisation when known.
/// </summary>
public static class ChannelAdvisor
{
    private record Candidate(string Label, int First, int Last, int Width, string Notes = "");

    private static readonly Candidate[] C24 =
    {
        new("1", 1, 1, 20), new("6", 6, 6, 20), new("11", 11, 11, 20),
        new("13", 13, 13, 20, "UK/EU only; some older devices can't see it"),
    };

    private static readonly Candidate[] C5 =
    {
        new("36–40", 36, 40, 40, "Band A"), new("44–48", 44, 48, 40, "Band A"),
        new("52–56", 52, 56, 40, "DFS"), new("60–64", 60, 64, 40, "DFS"),
        new("100–104", 100, 104, 40, "DFS"), new("108–112", 108, 112, 40, "DFS"),
        new("116–120", 116, 120, 40, "DFS · 120 is weather radar"), new("124–128", 124, 128, 40, "DFS · weather radar"),
        new("132–136", 132, 136, 40, "DFS"),
        new("149–153", 149, 153, 40, "UK Band C · indoor"), new("157–161", 157, 161, 40, "UK Band C · indoor"),
        new("36–48", 36, 48, 80, "Band A · no radar checks · indoor"),
        new("52–64", 52, 64, 80, "DFS · 1 min radar check on start"),
        new("100–112", 100, 112, 80, "DFS · higher power allowed in UK/EU"),
        new("116–128", 116, 128, 80, "DFS · 120–128 are weather-radar channels (10 min check)"),
        new("149–161", 149, 161, 80, "UK Band C · indoor low power"),
        new("36–64", 36, 64, 160, "Half DFS"),
        new("100–128", 100, 128, 160, "DFS · includes weather-radar channels"),
        // 132–144 (80 MHz) is omitted: channel 144 sits above 5725 MHz, which UK/EU rules don't allow.
    };

    private static readonly Candidate[] C6 =
    {
        new("1–13", 1, 13, 80), new("17–29", 17, 29, 80), new("33–45", 33, 45, 80),
        new("49–61", 49, 61, 80), new("65–77", 65, 77, 80), new("81–93", 81, 93, 80),
        new("1–29", 1, 29, 160), new("33–61", 33, 61, 160), new("65–93", 65, 93, 160),
        new("1–61", 1, 61, 320, "Wi-Fi 7 only"),
        new("33–93", 33, 93, 320, "Wi-Fi 7 only · fits UK lower 6 GHz"),
    };

    public static List<ChannelOption> Evaluate(WifiBand band, IEnumerable<BssInfo> neighbours, BssInfo? mine)
    {
        var cands = band switch { WifiBand.Band24 => C24, WifiBand.Band5 => C5, WifiBand.Band6 => C6, _ => Array.Empty<Candidate>() };
        var others = neighbours.Where(b => b.Band == band).ToList();
        var result = new List<ChannelOption>();

        foreach (var c in cands)
        {
            double lo = ChannelMath.FreqFromChannel(band, c.First) - 10;
            double hi = ChannelMath.FreqFromChannel(band, c.Last) + 10;
            // 2.4 GHz transmissions spill ~11 MHz either side of centre in practice.
            if (band == WifiBand.Band24) { lo -= 1; hi += 1; }

            double mw = 0; int count = 0; int? strongest = null;
            foreach (var b in others)
            {
                double bLo = b.LowFreq, bHi = b.HighFreq;
                if (band == WifiBand.Band24) { bLo -= 1; bHi += 1; }
                double ov = ChannelMath.Overlap(lo, hi, bLo, bHi);
                if (ov <= 0) continue;
                double frac = ov / Math.Max(1, bHi - bLo);
                double busy = 1 + 2 * (b.ChannelUtilisation ?? 0.15);
                mw += ChannelMath.DbmToMw(b.Rssi) * frac * busy;
                count++;
                if (strongest == null || b.Rssi > strongest) strongest = b.Rssi;
            }
            double idbm = mw > 0 ? 10 * Math.Log10(mw) : -120;
            // -95 dBm or quieter → 100; -45 dBm or louder → 0.
            double score = Math.Clamp((-45 - idbm) * 2, 0, 100);

            bool current = mine != null && mine.Band == band && mine.WidthMHz == c.Width &&
                           Math.Abs(mine.CenterFreqMHz - (lo + hi) / 2) < 6;
            if (mine != null && band == WifiBand.Band24 && mine.Band == band) current = mine.Channel == c.First;

            result.Add(new ChannelOption
            {
                Band = band,
                Label = band == WifiBand.Band24 ? $"Ch {c.Label}" : $"{c.Label} ({c.Width} MHz)",
                Width = c.Width,
                LoMHz = lo, HiMHz = hi,
                Neighbours = count,
                Strongest = strongest,
                InterferenceDbm = idbm,
                Score = Math.Round(score),
                IsCurrent = current,
                Notes = c.Notes
            });
        }

        // Best per width class.
        foreach (var g in result.GroupBy(r => r.Width))
        {
            // On a tie prefer where the user already is – no point recommending a change for nothing.
            var best = g.OrderByDescending(r => r.Score).ThenByDescending(r => r.IsCurrent).ThenBy(r => r.Neighbours).FirstOrDefault();
            if (best != null) best.IsBest = true;
        }
        return result.OrderBy(r => r.Width).ThenByDescending(r => r.Score).ToList();
    }

    /// <summary>
    /// Filter out access points that belong to the user's own router (same SSID or a BSSID that
    /// differs only in the last byte / local bit – routers typically use consecutive MACs per radio).
    /// </summary>
    public static IEnumerable<BssInfo> ExcludeOwn(IEnumerable<BssInfo> all, BssInfo? mine, string? mySsid)
    {
        if (mine == null && string.IsNullOrEmpty(mySsid)) return all;
        ulong myPrefix = mine != null ? (mine.Key & ~(0x02UL << 40)) >> 8 : ulong.MaxValue;
        return all.Where(b =>
            !(mine != null && ((b.Key & ~(0x02UL << 40)) >> 8) == myPrefix) &&
            !(!string.IsNullOrEmpty(mySsid) && b.Ssid == mySsid));
    }
}
