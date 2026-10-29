using System.Windows;
using System.Windows.Input;
using AirScope.Core;
using AirScope.UI;

namespace AirScope;

/// <summary>Compact floating signal monitor – handy on a second screen or over a game.</summary>
public partial class MiniWindow : Window
{
    private readonly AppSettings _settings;
    public event Action<bool>? TopmostChanged;

    public MiniWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        Topmost = settings.MiniAlwaysOnTop;
        TopItem.IsChecked = Topmost;

        var wa = SystemParameters.WorkArea;
        Left = wa.Right - Width - 16; Top = wa.Bottom - Height - 16;
        if (settings.MiniLeft is double l && settings.MiniTop is double t)
        {
            // Only restore if the saved spot is still on a real monitor (handles unplugged / L-shaped setups).
            SourceInitialized += (_, _) =>
            {
                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
                bool OnScreen(double x, double y) =>
                    MonitorFromPoint(new POINT { X = (int)(x * dpi.DpiScaleX), Y = (int)(y * dpi.DpiScaleY) }, 0) != IntPtr.Zero;
                if (OnScreen(l + 10, t + 10) && OnScreen(l + Width - 10, t + Height - 10)) { Left = l; Top = t; }
            };
        }

        Closing += (_, _) => { _settings.MiniLeft = Left; _settings.MiniTop = Top; };
    }

    public void Update(LinkSample? s, ConnectionInfo c, IReadOnlyList<LinkSample>? window)
    {
        if (s is not LinkSample x || !c.Connected)
        {
            Rssi.Text = "—"; Quality.Text = "offline"; Tx.Text = Rx.Text = "";
            Info.Text = c.State;
            Spark.SetPoints(Array.Empty<double>());
            return;
        }
        var col = Palette.ForRssi(x.Rssi);
        Rssi.Text = x.Rssi.ToString();
        Rssi.Foreground = Palette.B(col);
        Quality.Text = ChannelMath.Describe(x.Rssi);
        Quality.Foreground = Palette.B(col);
        Tx.Text = $"↑ {x.TxMbps:0} Mbps";
        Rx.Text = $"↓ {x.RxMbps:0} Mbps";
        Info.Text = $"{c.Ssid} · ch {c.Channel}";
        if (window != null)
        {
            var cutoff = DateTime.UtcNow.AddSeconds(-60);
            Spark.Stroke = col;
            Spark.SetPoints(window.Where(w => w.Utc >= cutoff).Select(w => (double)w.Rssi).ToList());
        }
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.ClickCount == 1)
            try { DragMove(); } catch (InvalidOperationException) { }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);   // flags 0 = MONITOR_DEFAULTTONULL

    private void OnDoubleClick(object sender, MouseButtonEventArgs e) => Close();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Top_Click(object sender, RoutedEventArgs e)
    {
        Topmost = TopItem.IsChecked;
        _settings.MiniAlwaysOnTop = Topmost;
        TopmostChanged?.Invoke(Topmost);
    }
}
