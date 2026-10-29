using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace AirScope.Core;

/// <summary>One echo to the router. <see cref="Ms"/> is null when it was lost (no reply within the timeout).</summary>
public readonly record struct PingSample(DateTime Utc, double? Ms);

/// <summary>
/// Optional: pings the Wi-Fi adapter's default gateway twice a second, so you can see what scans and signal dips
/// actually do to latency. Only targets a gateway inside the Wi-Fi adapter's own subnet, and sends with TTL 1 so
/// no router can forward the packet beyond the first hop.
/// </summary>
public sealed class RouterPinger : IDisposable
{
    private const int PeriodMs = 500, SlowPeriodMs = 5000, TimeoutMs = 1000;
    private readonly Func<bool> _connected;
    private readonly object _lock = new();
    private readonly PingSample[] _buf = new PingSample[30 * 60 * 2 + 32];   // 30 min at 2 Hz
    private int _start, _count;
    private CancellationTokenSource? _cts;
    private volatile Status _status = new(null, false, false, null, false);

    /// <summary>What the pinger is doing, for the UI.</summary>
    /// <param name="NoReplies">At least 10 pings sent and none answered: the router ignores pings; we've slowed right down.</param>
    /// <param name="SharedSubnet">Another active adapter (e.g. Ethernet) is on the same subnet, so Windows may send pings that way.</param>
    /// <param name="AnyReply">The current gateway has answered at least once (until then losses aren't judged).</param>
    public sealed record Status(IPAddress? Target, bool NoReplies, bool SharedSubnet, string? Error, bool AnyReply);

    public Status Current => _status;

    /// <summary>Raised on a background thread for each completed ping (lost ones ~1 s after they were sent).</summary>
    public event Action<PingSample>? Sampled;

    public RouterPinger(Func<bool> connected) => _connected = connected;

    public void Start(Guid iface)
    {
        Stop();
        Clear();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Task.Run(() => Loop(iface, ct));
    }

    /// <summary>Doesn't block: the old loop stops at its next check and can no longer add samples.</summary>
    public void Stop()
    {
        if (_cts == null) return;
        _cts.Cancel();
        _cts.Dispose();
        _cts = null;
        _status = new(null, false, false, null, false);
    }

    public void Clear() { lock (_lock) { _start = 0; _count = 0; } }

    private async Task Loop(Guid iface, CancellationToken ct)
    {
        using var ping = new Ping();
        var payload = new byte[32];
        var opts = new PingOptions(1, true);    // TTL 1: the gateway answers, nothing beyond it ever sees the packet
        long lastResolve = long.MinValue / 2;
        IPAddress? target = null;
        bool shared = false;
        int sent = 0, replies = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                bool noReplies = sent >= 10 && replies == 0;
                await Task.Delay(noReplies ? SlowPeriodMs : PeriodMs, ct);
                if (!_connected()) { target = null; sent = replies = 0; Publish(ct, null, false, false, null, false); continue; }   // a disconnect isn't "loss"

                long nowTick = Environment.TickCount64;
                if (nowTick - lastResolve >= 10_000)   // re-check every 10 s (VPN up, DHCP renumbered…), also when none found
                {
                    lastResolve = nowTick;
                    var (gw, sh) = ResolveGateway(iface);
                    if (!Equals(gw, target)) { sent = replies = 0; }
                    target = gw; shared = sh;
                }
                Publish(ct, target, sent >= 10 && replies == 0, shared, null, replies > 0);
                if (target == null) continue;

                var sentUtc = DateTime.UtcNow;
                double? ms = null;
                try
                {
                    var reply = await ping.SendPingAsync(target, TimeSpan.FromMilliseconds(TimeoutMs), payload, opts, ct);
                    if (reply.Status == IPStatus.Success) ms = reply.RoundtripTime;
                }
                catch (PingException) { }
                if (ct.IsCancellationRequested || !_connected()) continue;   // link went down mid-ping
                sent++; if (ms != null) replies++;
                if (ms != null && replies == 1) Publish(ct, target, false, shared, null, true);
                if (sent > 1_000_000) { sent /= 2; replies /= 2; }
                var sample = new PingSample(sentUtc, ms);
                if (Add(sample, ct)) Sampled?.Invoke(sample);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                // Never die silently: say what went wrong and keep trying.
                Publish(ct, target, false, shared, ex.Message, replies > 0);
                try { await Task.Delay(SlowPeriodMs, ct); } catch (OperationCanceledException) { return; }
            }
        }
    }

    private void Publish(CancellationToken ct, IPAddress? target, bool noReplies, bool shared, string? error, bool anyReply)
    {
        if (!ct.IsCancellationRequested) _status = new Status(target, noReplies, shared, error, anyReply);
    }

    /// <summary>
    /// IPv4 default gateway of this Wi-Fi adapter, only if it lies inside one of the adapter's own subnets (so it's
    /// on the local link). The WLAN interface GUID is the NIC id. Also reports whether another up adapter shares
    /// that subnet, in which case Windows may route the ping over that adapter instead.
    /// </summary>
    private static (IPAddress? gw, bool shared) ResolveGateway(Guid iface)
    {
        try
        {
            string id = iface.ToString("B");
            var all = NetworkInterface.GetAllNetworkInterfaces();
            var ni = all.FirstOrDefault(n => string.Equals(n.Id, id, StringComparison.OrdinalIgnoreCase));
            if (ni == null) return (null, false);
            var props = ni.GetIPProperties();
            var subnets = props.UnicastAddresses
                .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork && u.PrefixLength is > 0 and <= 32)
                .Select(u => (net: Mask(u.Address, u.PrefixLength), len: u.PrefixLength)).ToList();
            foreach (var g in props.GatewayAddresses.Select(x => x.Address))
            {
                if (g.AddressFamily != AddressFamily.InterNetwork || g.Equals(IPAddress.Any)) continue;
                var hit = subnets.FirstOrDefault(sn => Mask(g, sn.len) == sn.net);
                if (hit.len == 0) continue;   // gateway not on this adapter's subnet – don't ping it
                bool shared = all.Any(o => o.Id != ni.Id && o.OperationalStatus == OperationalStatus.Up &&
                    o.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                    o.GetIPProperties().UnicastAddresses.Any(u => u.Address.AddressFamily == AddressFamily.InterNetwork &&
                        u.PrefixLength == hit.len && Mask(u.Address, u.PrefixLength) == hit.net));
                return (g, shared);
            }
        }
        catch (NetworkInformationException) { }
        return (null, false);
    }

    private static uint Mask(IPAddress a, int prefix)
    {
        var b = a.GetAddressBytes();
        uint v = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
        return prefix >= 32 ? v : v & ~(uint.MaxValue >> prefix);
    }

    private bool Add(PingSample s, CancellationToken ct)
    {
        lock (_lock)
        {
            if (ct.IsCancellationRequested) return false;   // a stopped loop can't write into the next run's data
            if (_count < _buf.Length) _buf[(_start + _count++) % _buf.Length] = s;
            else { _buf[_start] = s; _start = (_start + 1) % _buf.Length; }
            return true;
        }
    }

    public void CopySince(DateTime sinceUtc, List<PingSample> into)
    {
        into.Clear();
        lock (_lock)
        {
            for (int i = 0; i < _count; i++)
            {
                var s = _buf[(_start + i) % _buf.Length];
                if (s.Utc >= sinceUtc) into.Add(s);
            }
        }
    }

    public PingSample? Last
    {
        get { lock (_lock) return _count == 0 ? null : _buf[(_start + _count - 1) % _buf.Length]; }
    }

    public void Dispose() => Stop();
}

public readonly record struct PingStats(int Sent, int Lost, double? Avg, double? P95, double? Max)
{
    public double LossPct => Sent == 0 ? 0 : 100.0 * Lost / Sent;

    /// <summary>Avg/P95/Max are over successful pings; loss is separate.</summary>
    public static PingStats Of(IReadOnlyList<PingSample> v)
    {
        var ok = v.Where(p => p.Ms.HasValue).Select(p => p.Ms!.Value).OrderBy(x => x).ToList();
        if (ok.Count == 0) return new PingStats(v.Count, v.Count, null, null, null);
        double p95 = ok[Math.Min(ok.Count - 1, (int)Math.Ceiling(ok.Count * 0.95) - 1)];
        return new PingStats(v.Count, v.Count - ok.Count, ok.Average(), p95, ok[^1]);
    }

    public static string Ms(double? v) => v is not double d ? "—" : d < 1 ? "<1" : d.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
}
