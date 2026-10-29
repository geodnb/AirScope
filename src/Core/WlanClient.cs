using System.Runtime.InteropServices;
using System.Text;
using AirScope.Interop;
using static AirScope.Interop.WlanNative;

namespace AirScope.Core;

public sealed class WlanException : Exception
{
    public uint Code { get; }
    public WlanException(string op, uint code) : base($"{op} failed (error {code})") => Code = code;
}

/// <summary>
/// Safe wrapper around a WLAN client handle. All native memory returned by the
/// API is copied out and freed inside each call, so nothing native escapes.
/// </summary>
public sealed class WlanClient : IDisposable
{
    private IntPtr _handle;
    private readonly WlanNotificationCallback _callback;   // kept alive for the lifetime of the handle
    private volatile bool _disposed;

    /// <summary>(source, code, interface) – raised on a WLAN service thread; keep handlers tiny.</summary>
    public event Action<uint, uint, Guid>? Notification;

    public WlanClient()
    {
        uint rc = WlanOpenHandle(WLAN_CLIENT_VERSION_2, IntPtr.Zero, out _, out _handle);
        if (rc != ERROR_SUCCESS) throw new WlanException("WlanOpenHandle", rc);
        _callback = OnNotification;
        rc = WlanRegisterNotification(_handle, WLAN_NOTIFICATION_SOURCE_ACM | WLAN_NOTIFICATION_SOURCE_MSM,
            true, _callback, IntPtr.Zero, IntPtr.Zero, out _);
        // Notifications are a nice-to-have; the app still works by polling if this fails.
        NotificationsAvailable = rc == ERROR_SUCCESS;
    }

    private void OnNotification(IntPtr data, IntPtr context)
    {
        if (_disposed || data == IntPtr.Zero) return;
        try
        {
            var n = Marshal.PtrToStructure<WLAN_NOTIFICATION_DATA>(data);
            Notification?.Invoke(n.NotificationSource, n.NotificationCode, n.InterfaceGuid);
        }
        catch { /* never let an exception cross back into native code */ }
    }

    private int _inFlight;
    public bool IsDisposed => _disposed;
    /// <summary>False if Windows refused the scan-complete notification registration.</summary>
    public bool NotificationsAvailable { get; }

    /// <summary>
    /// Every API call runs inside Enter/Exit. Dispose waits for in-flight calls to finish before
    /// closing the handle, so a background loop can never use a closed (or reused) handle.
    /// </summary>
    private IntPtr Enter()
    {
        Interlocked.Increment(ref _inFlight);
        if (_disposed) { Interlocked.Decrement(ref _inFlight); throw new ObjectDisposedException(nameof(WlanClient)); }
        return _handle;
    }

    private void Exit() => Interlocked.Decrement(ref _inFlight);

    private List<WlanInterfaceInfo> GetInterfacesCore(IntPtr h)
    {
        var list = new List<WlanInterfaceInfo>();
        uint rc = WlanEnumInterfaces(h, IntPtr.Zero, out var p);
        if (rc != ERROR_SUCCESS) throw new WlanException("WlanEnumInterfaces", rc);
        try
        {
            int n = Marshal.ReadInt32(p, 0);
            if (n < 0 || n > 64) n = 0;
            for (int i = 0; i < n; i++)
            {
                IntPtr e = p + 8 + i * WLAN_INTERFACE_INFO_SIZE;
                var guid = Marshal.PtrToStructure<Guid>(e);
                string desc = Marshal.PtrToStringUni(e + 16, 256).TrimEnd('\0');
                int nul = desc.IndexOf('\0'); if (nul >= 0) desc = desc[..nul];
                int state = Marshal.ReadInt32(e, 16 + 512);
                list.Add(new WlanInterfaceInfo { Id = guid, Description = desc, State = state });
            }
        }
        finally { WlanFreeMemory(p); }
        return list;
    }

    /// <summary>Fast path: RSSI of the current association straight from the driver.</summary>
    private int? GetRssiCore(IntPtr h, Guid iface)
    {
        uint rc = WlanQueryInterface(h, iface, OPCODE_RSSI, IntPtr.Zero, out uint size, out var p, out _);
        if (rc != ERROR_SUCCESS) return null;
        try { return size >= 4 ? Marshal.ReadInt32(p) : null; }
        finally { WlanFreeMemory(p); }
    }

    private int? GetChannelCore(IntPtr h, Guid iface)
    {
        uint rc = WlanQueryInterface(h, iface, OPCODE_CHANNEL_NUMBER, IntPtr.Zero, out uint size, out var p, out _);
        if (rc != ERROR_SUCCESS) return null;
        try { return size >= 4 ? Marshal.ReadInt32(p) : null; }
        finally { WlanFreeMemory(p); }
    }

    /// <summary>Summed PHY transmit/retry counters, if the driver exposes them.</summary>
    private (ulong tx, ulong retry, ulong failed)? GetPhyCountersCore(IntPtr h, Guid iface)
    {
        uint rc = WlanQueryInterface(h, iface, OPCODE_STATISTICS, IntPtr.Zero, out uint size, out var p, out _);
        if (rc != ERROR_SUCCESS) return null;
        try
        {
            const int physOffset = 216, firstPhy = 224, phySize = 144;
            if (size < firstPhy) return null;
            uint nPhys = (uint)Marshal.ReadInt32(p, physOffset);
            if (nPhys == 0 || nPhys > 16 || size < firstPhy + nPhys * phySize) return null;
            ulong tx = 0, retry = 0, failed = 0;
            for (int i = 0; i < nPhys; i++)
            {
                IntPtr ph = p + firstPhy + i * phySize;
                tx += (ulong)Marshal.ReadInt64(ph, 0);
                failed += (ulong)Marshal.ReadInt64(ph, 16);
                retry += (ulong)Marshal.ReadInt64(ph, 24);
            }
            return (tx, retry, failed);
        }
        finally { WlanFreeMemory(p); }
    }

    private ConnectionInfo GetConnectionCore(IntPtr h, Guid iface)
    {
        uint rc = WlanQueryInterface(h, iface, OPCODE_CURRENT_CONNECTION, IntPtr.Zero, out uint size, out var p, out _);
        if (rc != ERROR_SUCCESS)
            return new ConnectionInfo { Connected = false, State = rc == ERROR_INVALID_STATE ? "Disconnected" : $"Unavailable ({rc})" };
        try
        {
            if (size < WLAN_CONNECTION_ATTRIBUTES_SIZE) return new ConnectionInfo { Connected = false, State = "Unknown" };
            int state = Marshal.ReadInt32(p, 0);
            string profile = ReadWString(p + 8, 256);
            string ssid = ReadSsid(p + 520);
            byte[] mac = new byte[6];
            Marshal.Copy(p + 560, mac, 0, 6);
            ulong key = MacKey(mac);
            int phy = Marshal.ReadInt32(p, 568);
            uint quality = (uint)Marshal.ReadInt32(p, 576);
            uint rx = (uint)Marshal.ReadInt32(p, 580);
            uint tx = (uint)Marshal.ReadInt32(p, 584);
            int auth = Marshal.ReadInt32(p, 596);
            int cipher = Marshal.ReadInt32(p, 600);
            return new ConnectionInfo
            {
                Connected = state == 1,
                State = PhyNames.InterfaceState(state),
                Profile = profile,
                Ssid = ssid,
                Bssid = MacString(mac),
                BssidKey = key,
                PhyType = phy,
                SignalQuality = quality,
                RxMbps = rx / 1000.0,
                TxMbps = tx / 1000.0,
                Auth = PhyNames.Auth(auth),
                Cipher = PhyNames.Cipher(cipher)
            };
        }
        finally { WlanFreeMemory(p); }
    }

    /// <summary>Ask the driver to scan. Returns the WLAN error code (0 = accepted).</summary>
    public uint RequestScan(Guid iface)
    {
        var h = Enter();
        try { return WlanScan(h, iface, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero); }
        finally { Exit(); }
    }

    // ---- guarded public surface ------------------------------------------

    public List<WlanInterfaceInfo> GetInterfaces() { var h = Enter(); try { return GetInterfacesCore(h); } finally { Exit(); } }
    public int? GetRssi(Guid iface) { var h = Enter(); try { return GetRssiCore(h, iface); } finally { Exit(); } }
    public int? GetChannel(Guid iface) { var h = Enter(); try { return GetChannelCore(h, iface); } finally { Exit(); } }
    public (ulong tx, ulong retry, ulong failed)? GetPhyCounters(Guid iface) { var h = Enter(); try { return GetPhyCountersCore(h, iface); } finally { Exit(); } }
    public ConnectionInfo GetConnection(Guid iface) { var h = Enter(); try { return GetConnectionCore(h, iface); } finally { Exit(); } }
    public List<BssInfo> GetBssList(Guid iface, out uint error) { var h = Enter(); try { return GetBssListCore(h, iface, out error); } finally { Exit(); } }

    private List<BssInfo> GetBssListCore(IntPtr h, Guid iface, out uint error)
    {
        var result = new List<BssInfo>();
        error = WlanGetNetworkBssList(h, iface, IntPtr.Zero, DOT11_BSS_TYPE_ANY, false, IntPtr.Zero, out var p);
        if (error != ERROR_SUCCESS) return result;
        try
        {
            uint total = (uint)Marshal.ReadInt32(p, 0);
            int n = Marshal.ReadInt32(p, 4);
            long fit = total < 8 ? 0 : ((long)total - 8) / WLAN_BSS_ENTRY_SIZE;   // entries that really fit
            if (n < 0 || n > fit) n = (int)Math.Min(fit, 4096);
            var now = DateTime.UtcNow;
            for (int i = 0; i < n; i++)
            {
                int entryOff = 8 + i * WLAN_BSS_ENTRY_SIZE;
                var bss = ParseEntry(p, entryOff, total, now);
                if (bss != null) result.Add(bss);
            }
        }
        finally { WlanFreeMemory(p); }
        return result;
    }

    private static BssInfo? ParseEntry(IntPtr listBase, int entryOff, uint total, DateTime now)
    {
        IntPtr e = listBase + entryOff;
        string ssid = ReadSsid(e);
        byte[] mac = new byte[6];
        Marshal.Copy(e + 40, mac, 0, 6);
        ulong key = MacKey(mac);
        int phy = Marshal.ReadInt32(e, 52);
        int rssi = Marshal.ReadInt32(e, 56);
        uint lq = (uint)Marshal.ReadInt32(e, 60);
        ushort beacon = (ushort)Marshal.ReadInt16(e, 66);
        ulong tsf = (ulong)Marshal.ReadInt64(e, 72);
        ushort capInfo = (ushort)Marshal.ReadInt16(e, 88);
        uint freqKhz = (uint)Marshal.ReadInt32(e, 92);
        uint ieOff = (uint)Marshal.ReadInt32(e, 352);
        uint ieSize = (uint)Marshal.ReadInt32(e, 356);

        int mhz = (int)(freqKhz / 1000);
        var band = ChannelMath.BandFromFreq(mhz);
        if (band == WifiBand.Unknown) return null;
        int ch = ChannelMath.ChannelFromFreq(mhz);

        IeInfo ie = new();
        // IE blob must lie entirely inside the buffer the API handed us.
        if (ieSize > 0 && ieSize < 8192 && (ulong)entryOff + ieOff + ieSize <= total)
        {
            var buf = new byte[ieSize];
            Marshal.Copy(e + (int)ieOff, buf, 0, (int)ieSize);
            ie = IeParser.Parse(buf);
        }

        bool hidden = ssid.Length == 0 || ssid.All(c => c == '\0');
        if (hidden && ie.SsidBytes is { Length: > 0 } sb && sb.Any(b => b != 0))
        { ssid = DecodeSsid(sb); hidden = false; }

        var (width, label, center) = IeParser.ResolveWidth(ie, band, mhz);
        var (sec, cipher, pmf) = IeParser.ResolveSecurity(ie, capInfo);
        int gen = IeParser.Generation(ie, phy);

        bool la = OuiLookup.IsLocallyAdministered(key);
        string vendor = OuiLookup.Vendor(key);
        if (la)
        {
            // Routers often derive guest/mesh BSSIDs by setting the "local" bit on their real MAC.
            string guess = OuiLookup.Vendor(key & ~(0x02UL << 40));
            vendor = guess.Length > 0 ? guess + " (virtual)" : "Virtual / randomised";
        }

        TimeSpan? uptime = null;
        if (tsf > 0 && tsf < 3UL * 365 * 24 * 3600 * 1_000_000) uptime = TimeSpan.FromTicks((long)tsf * 10);

        return new BssInfo
        {
            Key = key,
            Bssid = MacString(mac),
            Ssid = hidden ? "" : ssid,
            Hidden = hidden,
            Rssi = rssi,
            LinkQuality = lq,
            FreqMHz = mhz,
            Channel = ch,
            Band = band,
            WidthMHz = width,
            WidthLabel = label,
            CenterFreqMHz = center,
            Generation = gen,
            Standard = IeParser.StandardName(gen, band),
            Security = sec,
            Cipher = cipher,
            Pmf = pmf,
            Wps = ie.Wps,
            Streams = ie.Streams,
            StationCount = ie.StationCount,
            ChannelUtilisation = ie.ChannelUtilisation,
            BssColor = ie.BssColor,
            MultiLink = ie.MultiLink,
            Country = ie.Country,
            BeaconIntervalTu = beacon,
            ApUptime = uptime,
            MaxPhyMbps = IeParser.MaxPhyMbps(gen, width, ie.Streams),
            Vendor = vendor,
            LocallyAdministered = la,
            Dfs = ChannelMath.IsDfs(band, ch),
            SeenUtc = now,
            Tsf = tsf
        };
    }

    // ---- helpers -------------------------------------------------------

    private static string ReadSsid(IntPtr p)
    {
        uint len = (uint)Marshal.ReadInt32(p, 0);
        if (len == 0 || len > 32) return "";
        var b = new byte[len];
        Marshal.Copy(p + 4, b, 0, (int)len);
        return DecodeSsid(b);
    }

    private static string DecodeSsid(byte[] b)
    {
        string s;
        try { s = new UTF8Encoding(false, true).GetString(b); }
        catch (DecoderFallbackException) { s = Encoding.Latin1.GetString(b); }
        var sb = new StringBuilder(s.Length);
        // Replace control characters and invisible format characters (bidi overrides, zero-width joiners…)
        // so a nearby network can't make its name render as a copy of someone else's.
        foreach (var c in s)
            sb.Append(char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format ? '·' : c);
        return sb.ToString();
    }

    private static string ReadWString(IntPtr p, int maxChars)
    {
        string s = Marshal.PtrToStringUni(p, maxChars);
        int nul = s.IndexOf('\0');
        return nul >= 0 ? s[..nul] : s;
    }

    public static ulong MacKey(byte[] m) =>
        ((ulong)m[0] << 40) | ((ulong)m[1] << 32) | ((ulong)m[2] << 24) | ((ulong)m[3] << 16) | ((ulong)m[4] << 8) | m[5];

    public static string MacString(byte[] m) => string.Join(":", m.Select(b => b.ToString("X2")));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Thread.MemoryBarrier();   // publish the flag before reading the in-flight count (store→load)
        // Let any call that already entered finish before the handle goes away.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (Volatile.Read(ref _inFlight) > 0 && sw.ElapsedMilliseconds < 3000) Thread.Sleep(5);
        if (Volatile.Read(ref _inFlight) > 0) return;   // a driver call is hung – leave the handle; the process is exiting
        var h = _handle;
        _handle = IntPtr.Zero;
        if (h != IntPtr.Zero)
        {
            WlanRegisterNotification(h, WLAN_NOTIFICATION_SOURCE_NONE, true, null, IntPtr.Zero, IntPtr.Zero, out _);
            WlanCloseHandle(h, IntPtr.Zero);
        }
        GC.KeepAlive(_callback);
    }
}
