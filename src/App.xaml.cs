using System.Windows;
using System.Windows.Threading;

namespace AirScope;

public partial class App : Application
{
    private bool _shown;
    private bool _started;

    public static string ErrorLogPath => Path.Combine(Core.AppSettings.Folder, "error.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => { if (a.ExceptionObject is Exception ex) Log(ex); };
        try
        {
            var w = new MainWindow();
            MainWindow = w;
            w.Show();
            _started = true;
        }
        catch (Exception ex)
        {
            Fatal(ex);
        }
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        Log(e.Exception);
        // If the main window never got on screen, don't leave an invisible process running.
        if (!_started || MainWindow is not { IsVisible: true } && MainWindow?.WindowState != WindowState.Minimized)
        {
            Fatal(e.Exception);
            return;
        }
        if (_shown) return;
        _shown = true;
        MessageBox.Show("Something went wrong in the UI:\n\n" + Describe(e.Exception) +
                        $"\n\nAirScope will keep running. Details were saved to:\n{ErrorLogPath}",
            "AirScope", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void Fatal(Exception ex)
    {
        Log(ex);
        MessageBox.Show("AirScope couldn't start:\n\n" + Describe(ex) + $"\n\nDetails were saved to:\n{ErrorLogPath}",
            "AirScope", MessageBoxButton.OK, MessageBoxImage.Error);
        Shutdown(1);
    }

    /// <summary>Show the innermost cause – "type initializer threw" on its own is useless.</summary>
    private static string Describe(Exception ex)
    {
        var root = ex.GetBaseException();
        return root == ex ? ex.Message : $"{ex.Message}\n→ {root.GetType().Name}: {root.Message}";
    }

    private static void Log(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Core.AppSettings.Folder);
            File.AppendAllText(ErrorLogPath, $"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} · AirScope {typeof(App).Assembly.GetName().Version} · {Environment.OSVersion}\n{ex}\n\n");
        }
        catch { }
    }
}
