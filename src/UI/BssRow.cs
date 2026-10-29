using System.ComponentModel;
using System.Windows.Media;
using AirScope.Core;

namespace AirScope.UI;

/// <summary>Grid row wrapper – updated in place each scan so sorting and selection stay put.</summary>
public sealed class BssRow : INotifyPropertyChanged
{
    public BssInfo Info { get; private set; }
    public bool IsConnected { get; private set; }
    public bool IsStale { get; private set; }
    public bool IsBestForSsid { get; private set; } = true;

    public void SetBestForSsid(bool v)
    {
        if (v == IsBestForSsid) return;
        IsBestForSsid = v;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsBestForSsid)));
    }
    public DateTime FirstSeenUtc { get; init; }

    public BssRow(BssInfo i) { Info = i; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(BssInfo i, bool connected, DateTime nowUtc)
    {
        Info = i;
        IsConnected = connected;
        IsStale = (nowUtc - i.SeenUtc).TotalSeconds > 30;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public void Tick(DateTime nowUtc)
    {
        bool stale = (nowUtc - Info.SeenUtc).TotalSeconds > 30;
        if (stale != IsStale)
        {
            IsStale = stale;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsStale)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Opacity)));
        }
        else PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LastSeen)));
    }

    public ulong Key => Info.Key;
    public string Ssid => Info.DisplaySsid;
    public string Bssid => Info.Bssid;
    public string Vendor => Info.Vendor;
    public int Rssi => Info.Rssi;
    public int Channel => Info.Channel;
    public string Band => ChannelMath.BandName(Info.Band);
    public int BandOrder => (int)Info.Band;
    public int Width => Info.WidthMHz;
    public string WidthText => Info.WidthLabel;
    public string Gen => Info.GenLabel;
    public int GenOrder => Info.Generation;
    public string Security => Info.Security;
    public string Streams => Info.Streams > 0 ? $"{Info.Streams}×{Info.Streams}" : "—";
    public string Clients => Info.StationCount?.ToString() ?? "—";
    public int ClientsOrder => Info.StationCount ?? -1;
    public string Utilisation => Info.ChannelUtilisation is double u ? $"{u * 100:0}%" : "—";
    public double UtilisationOrder => Info.ChannelUtilisation ?? -1;
    public string MaxRate => Info.MaxPhyMbps is double m ? (m >= 1000 ? $"{m / 1000:0.0} G" : $"{m:0} M") : "—";
    public double MaxRateOrder => Info.MaxPhyMbps ?? 0;
    public string Dfs => Info.Dfs ? "DFS" : "";
    public string LastSeen
    {
        get
        {
            var s = (DateTime.UtcNow - Info.SeenUtc).TotalSeconds;
            return s < 8 ? "now" : s < 60 ? $"{s:0}s" : $"{s / 60:0}m";
        }
    }
    public double Opacity => IsStale ? 0.45 : 1;
    public Brush SsidBrush => Palette.B(Palette.ForName(Info.DisplaySsid));
    public string ConnectedMark => IsConnected ? "●" : "";
    public string Flags => string.Join(" ", new[]
    {
        Info.MultiLink ? "MLO" : null,
        Info.Wps ? "WPS" : null,
        Info.Pmf == "Required" ? "PMF" : null,
        Info.Hidden ? "Hidden" : null,
    }.Where(x => x != null));
}
