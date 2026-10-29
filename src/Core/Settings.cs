using System.Text.Json;

namespace AirScope.Core;

public sealed class AppSettings
{
    public int PollMs { get; set; } = 250;
    public int ScanSeconds { get; set; } = 10;
    public int GraphWindowSeconds { get; set; } = 120;
    public Guid? InterfaceId { get; set; }
    public bool ShowRates { get; set; } = true;
    public bool MiniAlwaysOnTop { get; set; } = true;
    public bool ExcludeOwnInAdvisor { get; set; } = true;
    public double? MiniLeft { get; set; }
    public double? MiniTop { get; set; }
    public ScanMode ScanMode { get; set; } = ScanMode.Auto;
    /// <summary>Ping the Wi-Fi adapter's default gateway to show latency and loss. Off by default.</summary>
    public bool PingRouter { get; set; }
    /// <summary>Screenshot mode: mask network names and hardware addresses on screen.</summary>
    public bool HideIdentifiers { get; set; }

    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AirScope");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
                if (s != null) { s.Clamp(); return s; }
            }
        }
        catch { /* corrupt settings → defaults */ }
        return new AppSettings();
    }

    public void Clamp()
    {
        PollMs = Math.Clamp(PollMs, 100, 2000);
        ScanSeconds = Math.Clamp(ScanSeconds, 2, 120);
        GraphWindowSeconds = Math.Clamp(GraphWindowSeconds, 15, 1800);
        if (!Enum.IsDefined(ScanMode)) ScanMode = ScanMode.Auto;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath, true);
        }
        catch { /* best effort */ }
    }
}
