namespace AirScope.Core;

/// <summary>Facts pulled from a beacon/probe-response Information Element blob.</summary>
public sealed class IeInfo
{
    public bool HasHt, HasVht, HasHe, HasEht, MultiLink, Wps;
    public int HtSecondaryOffset;            // 0 none, 1 above, 3 below
    public bool HtAnyWidth;
    public int VhtWidth = -1, VhtCcfs0, VhtCcfs1;
    public int He6Width = -1, He6Ccfs0, He6Ccfs1, He6Primary;
    public int HeVhtWidth = -1, HeVhtCcfs0, HeVhtCcfs1;
    public int EhtWidth = -1, EhtCcfs0, EhtCcfs1;
    public int Streams;
    public int? StationCount;
    public double? ChannelUtilisation;
    public int? BssColor;
    public string? Country;
    public byte[]? SsidBytes;
    // Security
    public bool HasRsn, HasWpa1, OweTransition;
    public readonly List<int> Akms = new();
    public readonly List<int> Ciphers = new();
    public bool MfpRequired, MfpCapable;
}

public static class IeParser
{
    public static IeInfo Parse(ReadOnlySpan<byte> ies)
    {
        var info = new IeInfo();
        int i = 0;
        // Every read below is bounds-checked against the element length, so a
        // malformed or truncated beacon can only cause the field to be skipped.
        while (i + 2 <= ies.Length)
        {
            int id = ies[i];
            int len = ies[i + 1];
            int start = i + 2;
            if (start + len > ies.Length) break;
            var e = ies.Slice(start, len);
            try
            {
                switch (id)
                {
                    case 0: info.SsidBytes = e.ToArray(); break;
                    case 7: if (len >= 2) info.Country = AsciiCountry(e); break;
                    case 11: // BSS Load
                        if (len >= 3)
                        {
                            info.StationCount = e[0] | (e[1] << 8);
                            info.ChannelUtilisation = e[2] / 255.0;
                        }
                        break;
                    case 45: // HT Capabilities
                        info.HasHt = true;
                        if (len >= 7) info.Streams = Math.Max(info.Streams, HtStreams(e.Slice(3, 4)));
                        break;
                    case 61: // HT Operation
                        if (len >= 2)
                        {
                            info.HtSecondaryOffset = e[1] & 0x03;
                            info.HtAnyWidth = (e[1] & 0x04) != 0;
                        }
                        break;
                    case 191: // VHT Capabilities
                        info.HasVht = true;
                        if (len >= 6) info.Streams = Math.Max(info.Streams, McsMapStreams((ushort)(e[4] | (e[5] << 8))));
                        break;
                    case 192: // VHT Operation
                        if (len >= 3) { info.VhtWidth = e[0]; info.VhtCcfs0 = e[1]; info.VhtCcfs1 = e[2]; }
                        break;
                    case 48: ParseRsn(e, info); break;
                    case 221: ParseVendor(e, info); break;
                    case 255: if (len >= 1) ParseExtension(e[0], e.Slice(1), info); break;
                }
            }
            catch (IndexOutOfRangeException) { /* malformed element – ignore it */ }
            catch (ArgumentOutOfRangeException) { }
            i = start + len;
        }
        return info;
    }

    private static void ParseExtension(int extId, ReadOnlySpan<byte> e, IeInfo info)
    {
        switch (extId)
        {
            case 35: // HE Capabilities: MAC(6) PHY(11) then Rx HE-MCS map (<=80 MHz)
                info.HasHe = true;
                if (e.Length >= 19) info.Streams = Math.Max(info.Streams, McsMapStreams((ushort)(e[17] | (e[18] << 8))));
                break;
            case 36: // HE Operation
            {
                if (e.Length < 6) break;
                uint p = (uint)(e[0] | (e[1] << 8) | (e[2] << 16));
                int colorByte = e[3];
                if ((colorByte & 0x80) == 0) info.BssColor = colorByte & 0x3F;
                int off = 6;
                bool vhtPresent = (p & (1u << 14)) != 0;
                bool cohosted = (p & (1u << 15)) != 0;
                bool sixGhzPresent = (p & (1u << 17)) != 0;
                if (vhtPresent)
                {
                    if (e.Length >= off + 3) { info.HeVhtWidth = e[off]; info.HeVhtCcfs0 = e[off + 1]; info.HeVhtCcfs1 = e[off + 2]; }
                    off += 3;
                }
                if (cohosted) off += 1;
                if (sixGhzPresent && e.Length >= off + 4)
                {
                    info.He6Primary = e[off];
                    info.He6Width = e[off + 1] & 0x03;
                    info.He6Ccfs0 = e[off + 2];
                    info.He6Ccfs1 = e[off + 3];
                }
                break;
            }
            case 106: // EHT Operation: params(1) basic MCS(4) [info: control, ccfs0, ccfs1]
                if (e.Length >= 1 && (e[0] & 0x01) != 0 && e.Length >= 8)
                {
                    info.EhtWidth = e[5] & 0x07;
                    info.EhtCcfs0 = e[6];
                    info.EhtCcfs1 = e[7];
                }
                break;
            case 107: info.MultiLink = true; break; // Basic Multi-Link element (MLO)
            case 108: info.HasEht = true; break;    // EHT Capabilities
        }
    }

    private static void ParseRsn(ReadOnlySpan<byte> e, IeInfo info)
    {
        if (e.Length < 2) return;
        info.HasRsn = true;
        int o = 2;                                   // version
        if (e.Length < o + 4) return;
        o += 4;                                      // group cipher
        if (e.Length < o + 2) return;
        int pc = e[o] | (e[o + 1] << 8); o += 2;
        if (e.Length < o + 4L * pc) return;           // truncated list – don't misread what follows
        for (int k = 0; k < pc; k++, o += 4)
            if (IsIeee(e, o)) info.Ciphers.Add(e[o + 3]);
        if (e.Length < o + 2) return;
        int ac = e[o] | (e[o + 1] << 8); o += 2;
        if (e.Length < o + 4L * ac) return;
        for (int k = 0; k < ac; k++, o += 4)
            if (IsIeee(e, o)) info.Akms.Add(e[o + 3]);
        if (e.Length >= o + 2)
        {
            int caps = e[o] | (e[o + 1] << 8);
            info.MfpRequired = (caps & 0x40) != 0;
            info.MfpCapable = (caps & 0x80) != 0;
        }
    }

    private static bool IsIeee(ReadOnlySpan<byte> e, int o) => e[o] == 0x00 && e[o + 1] == 0x0F && e[o + 2] == 0xAC;

    private static void ParseVendor(ReadOnlySpan<byte> e, IeInfo info)
    {
        if (e.Length < 4) return;
        if (e[0] == 0x00 && e[1] == 0x50 && e[2] == 0xF2)
        {
            if (e[3] == 1) info.HasWpa1 = true;
            else if (e[3] == 4) info.Wps = true;
        }
        else if (e[0] == 0x50 && e[1] == 0x6F && e[2] == 0x9A && e[3] == 0x1C)
            info.OweTransition = true;                // Wi-Fi Alliance OWE transition mode
    }

    private static string? AsciiCountry(ReadOnlySpan<byte> e)
    {
        if (e[0] < 'A' || e[0] > 'Z' || e[1] < 'A' || e[1] > 'Z') return null;
        return new string(new[] { (char)e[0], (char)e[1] });
    }

    private static int HtStreams(ReadOnlySpan<byte> rxMask)
    {
        int n = 0;
        for (int k = 0; k < 4; k++) if (rxMask[k] != 0) n = k + 1;
        return n;
    }

    /// <summary>VHT/HE MCS maps: 2 bits per spatial stream, 3 = unsupported.</summary>
    private static int McsMapStreams(ushort map)
    {
        int n = 0;
        for (int ss = 0; ss < 8; ss++)
            if (((map >> (ss * 2)) & 0x3) != 0x3) n = ss + 1;
        return n;
    }

    // ---------------------------------------------------------------------

    /// <summary>Work out occupied width and centre frequency from the parsed operation elements.</summary>
    public static (int width, string label, double center) ResolveWidth(IeInfo ie, WifiBand band, int primaryMhz)
    {
        int width = 20; string label = "20"; double center = primaryMhz;

        if (ie.HtAnyWidth && band != WifiBand.Band6)
        {
            if (ie.HtSecondaryOffset == 1) { width = 40; label = "40"; center = primaryMhz + 10; }
            else if (ie.HtSecondaryOffset == 3) { width = 40; label = "40"; center = primaryMhz - 10; }
        }

        if (band == WifiBand.Band5)
        {
            int w = ie.VhtWidth, c0 = ie.VhtCcfs0, c1 = ie.VhtCcfs1;
            if (w < 0 && ie.HeVhtWidth >= 0) { w = ie.HeVhtWidth; c0 = ie.HeVhtCcfs0; c1 = ie.HeVhtCcfs1; }
            if (w == 1 && c0 > 0)
            {
                if (c1 == 0) { width = 80; label = "80"; center = 5000 + 5 * c0; }
                else if (Math.Abs(c1 - c0) == 8) { width = 160; label = "160"; center = 5000 + 5 * c1; }
                else { width = 80; label = "80+80"; center = 5000 + 5 * c0; }
            }
            else if (w == 2 && c0 > 0) { width = 160; label = "160"; center = 5000 + 5 * c0; }
            else if (w == 3 && c0 > 0) { width = 80; label = "80+80"; center = 5000 + 5 * c0; }
        }
        else if (band == WifiBand.Band6 && ie.He6Width >= 0)
        {
            int c0 = ie.He6Ccfs0, c1 = ie.He6Ccfs1;
            switch (ie.He6Width)
            {
                case 0: width = 20; label = "20"; center = primaryMhz; break;
                case 1: width = 40; label = "40"; center = 5950 + 5 * c0; break;
                case 2: width = 80; label = "80"; center = 5950 + 5 * c0; break;
                case 3:
                    if (c1 != 0 && Math.Abs(c1 - c0) == 8) { width = 160; label = "160"; center = 5950 + 5 * c1; }
                    else if (c1 != 0) { width = 80; label = "80+80"; center = 5950 + 5 * c0; }
                    else { width = 160; label = "160"; center = 5950 + 5 * c0; }
                    break;
            }
        }

        // EHT (Wi-Fi 7) can widen further – 320 MHz on 6 GHz.
        if (ie.EhtWidth >= 0)
        {
            double basef = band == WifiBand.Band6 ? 5950 : band == WifiBand.Band5 ? 5000 : 2407;
            int ew = ie.EhtWidth switch { 0 => 20, 1 => 40, 2 => 80, 3 => 160, 4 => 320, _ => 0 };
            bool allowed = band switch { WifiBand.Band24 => ew <= 40, WifiBand.Band5 => ew <= 160, _ => true };
            if (ew > width && allowed)
            {
                int c = ew >= 160 && ie.EhtCcfs1 != 0 ? ie.EhtCcfs1 : ie.EhtCcfs0;
                if (c > 0) { width = ew; label = ew.ToString(); center = basef + 5 * c; }
            }
        }

        // Sanity guard: if the maths lands the primary outside its own span, fall back.
        if (primaryMhz < center - width / 2.0 || primaryMhz > center + width / 2.0)
        { width = 20; label = "20"; center = primaryMhz; }

        return (width, label, center);
    }

    public static (string security, string cipher, string pmf) ResolveSecurity(IeInfo ie, ushort capability)
    {
        bool privacy = (capability & 0x0010) != 0;
        string cipher = string.Join(", ", ie.Ciphers.Distinct().Select(c => c switch
        {
            2 => "TKIP", 4 => "CCMP", 8 => "GCMP", 9 => "GCMP-256", 10 => "CCMP-256", _ => $"#{c}"
        }));
        string pmf = ie.MfpRequired ? "Required" : ie.MfpCapable ? "Capable" : (ie.HasRsn ? "Off" : "—");

        if (!ie.HasRsn && !ie.HasWpa1)
            return (privacy ? "WEP (insecure)" : ie.OweTransition ? "Open (OWE transition)" : "Open", privacy ? "WEP" : "None", "—");

        var a = ie.Akms;
        bool sae = a.Contains(8) || a.Contains(9) || a.Contains(24) || a.Contains(25);
        bool psk = a.Contains(2) || a.Contains(4) || a.Contains(6);
        bool ent = a.Contains(1) || a.Contains(3) || a.Contains(5) || a.Contains(22) || a.Contains(23);
        bool ent192 = a.Contains(11) || a.Contains(12) || a.Contains(13);
        bool owe = a.Contains(18);

        string sec;
        if (ent192) sec = "WPA3-Enterprise 192";
        else if (ent && ie.MfpRequired && (a.Contains(5) || a.Contains(23))) sec = "WPA3-Enterprise";
        else if (ent) sec = ie.HasWpa1 ? "WPA/WPA2-Enterprise" : "WPA2-Enterprise";
        else if (sae && psk) sec = "WPA2/WPA3-Personal";
        else if (sae) sec = "WPA3-Personal";
        else if (owe) sec = "Enhanced Open (OWE)";
        else if (psk) sec = ie.HasWpa1 ? "WPA/WPA2-Personal" : "WPA2-Personal";
        else if (ie.HasWpa1) sec = "WPA (legacy)";
        else sec = "RSN";

        if (string.IsNullOrEmpty(cipher)) cipher = ie.HasWpa1 ? "TKIP" : "—";
        return (sec, cipher, pmf);
    }

    public static int Generation(IeInfo ie, int phyType)
    {
        if (ie.HasEht || phyType == 11) return 7;
        if (ie.HasHe || phyType == 10) return 6;
        if (ie.HasVht || phyType == 8) return 5;
        if (ie.HasHt || phyType == 7) return 4;
        return 0;
    }

    public static string StandardName(int gen, WifiBand band) => gen switch
    {
        7 => "802.11be",
        6 => band == WifiBand.Band6 ? "802.11ax (6E)" : "802.11ax",
        5 => "802.11ac",
        4 => "802.11n",
        _ => band == WifiBand.Band24 ? "802.11b/g" : "802.11a"
    };

    /// <summary>Theoretical top PHY rate of the AP (short GI, top MCS). Real throughput is far lower.</summary>
    public static double? MaxPhyMbps(int gen, int width, int streams)
    {
        if (streams <= 0) streams = 1;
        double per = gen switch
        {
            7 => width switch { 20 => 172.1, 40 => 344.1, 80 => 720.6, 160 => 1441.2, 320 => 2882.4, _ => 0 },
            6 => width switch { 20 => 143.4, 40 => 286.8, 80 => 600.5, 160 => 1201.0, _ => 0 },
            5 => width switch { 20 => 86.7, 40 => 200.0, 80 => 433.3, 160 => 866.7, _ => 0 },
            4 => width switch { 20 => 72.2, 40 => 150.0, _ => 0 },
            _ => 54
        };
        if (gen == 0) return 54;
        return per <= 0 ? null : Math.Round(per * streams);
    }
}
