using System.Globalization;
using System.Text;

namespace AirScope.Core;

/// <summary>Records link samples, scan results and (if on) router pings to CSV files under Documents\AirScope\Logs.</summary>
public sealed class CsvLogger : IDisposable
{
    private readonly StreamWriter _link, _scan;
    private StreamWriter? _ping;                  // created on the first ping, so no empty file when ping is off
    private readonly object _lock = new();
    private DateTime _lastFlush = DateTime.UtcNow;
    public string Folder { get; }
    public string LinkPath { get; }
    public string ScanPath { get; }
    public string PingPath { get; }
    public DateTime StartedLocal { get; } = DateTime.Now;
    public long Rows { get; private set; }
    private bool _disposed, _faulted;

    /// <summary>Raised once (on the writing thread) if the disk fails; the logger then stops writing.</summary>
    public event Action<string>? Faulted;

    public static string LogsFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AirScope", "Logs");

    public CsvLogger()
    {
        Folder = LogsFolder;
        Directory.CreateDirectory(Folder);
        string stamp = StartedLocal.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        LinkPath = Path.Combine(Folder, $"link_{stamp}.csv");
        ScanPath = Path.Combine(Folder, $"scan_{stamp}.csv");
        PingPath = Path.Combine(Folder, $"ping_{stamp}.csv");
        _link = new StreamWriter(LinkPath, false, new UTF8Encoding(true));
        _scan = new StreamWriter(ScanPath, false, new UTF8Encoding(true));
        // own_scan = 1 while one of AirScope's own scans had the radio off-channel (Windows' scans aren't known in real time).
        _link.WriteLine("time_local,ssid,bssid,channel,rssi_dbm,rx_mbps,tx_mbps,retry_pct,retry_avg_pct,own_scan");
        _scan.WriteLine("time_local,ssid,bssid,vendor,band,channel,width_mhz,rssi_dbm,standard,security,stations,utilisation_pct");
    }

    public void Write(LinkSample s, ConnectionInfo c)
    {
        if (!c.Connected) return;
        lock (_lock)
        {
            if (_disposed || _faulted) return;
            try
            {
            _link.WriteLine(string.Join(",",
                s.Utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                Esc(c.Ssid), c.Bssid, c.Channel, s.Rssi,
                s.RxMbps.ToString("0.#", CultureInfo.InvariantCulture),
                s.TxMbps.ToString("0.#", CultureInfo.InvariantCulture),
                s.RetryPct?.ToString("0.0", CultureInfo.InvariantCulture) ?? "",
                s.RetryAvgPct?.ToString("0.0", CultureInfo.InvariantCulture) ?? "",
                s.DuringScan ? "1" : "0"));
            Rows++;
            MaybeFlush();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Fault(ex); }
        }
    }

    public void Write(IReadOnlyList<BssInfo> scan)
    {
        lock (_lock)
        {
            if (_disposed || _faulted) return;
            try
            {
            string t = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            foreach (var b in scan)
            {
                _scan.WriteLine(string.Join(",", t, Esc(b.DisplaySsid), b.Bssid, Esc(b.Vendor),
                    ChannelMath.BandName(b.Band), b.Channel, b.WidthMHz, b.Rssi, Esc(b.Standard), Esc(b.Security),
                    b.StationCount?.ToString(CultureInfo.InvariantCulture) ?? "",
                    b.ChannelUtilisation is double u ? (u * 100).ToString("0", CultureInfo.InvariantCulture) : ""));
                Rows++;
            }
            MaybeFlush();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Fault(ex); }
        }
    }

    /// <summary>One row per ping (time it was sent), so lost pings are recorded too.</summary>
    public void WritePing(PingSample p)
    {
        lock (_lock)
        {
            if (_disposed || _faulted) return;
            try
            {
                if (_ping == null)
                {
                    _ping = new StreamWriter(PingPath, false, new UTF8Encoding(true));
                    _ping.WriteLine("time_local,ping_ms");
                }
                _ping.WriteLine(p.Utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "," +
                    (p.Ms is double ms ? ms.ToString("0", CultureInfo.InvariantCulture) : "lost"));
                Rows++;
                MaybeFlush();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Fault(ex); }
        }
    }

    private void Fault(Exception ex)
    {
        _faulted = true;
        try { Faulted?.Invoke(ex.Message); } catch { }
    }

    private void MaybeFlush()
    {
        if ((DateTime.UtcNow - _lastFlush).TotalSeconds < 2) return;
        _link.Flush(); _scan.Flush(); _ping?.Flush();
        _lastFlush = DateTime.UtcNow;
    }

    /// <summary>CSV-escape, and neutralise leading =,+,-,@ so SSIDs can't become spreadsheet formulas.</summary>
    public static string Esc(string? v)
    {
        v ??= "";
        if (v.Length > 0 && "=+-@\t\r".Contains(v[0])) v = "'" + v;
        return v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            try { _link.Flush(); _scan.Flush(); _ping?.Flush(); } catch { }
            try { _link.Dispose(); } catch { }
            try { _scan.Dispose(); } catch { }
            try { _ping?.Dispose(); } catch { }
        }
    }
}
