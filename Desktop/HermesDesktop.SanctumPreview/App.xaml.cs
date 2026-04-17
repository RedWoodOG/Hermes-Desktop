using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace SanctumPreview;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        this.UnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandled;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception ex)
        {
            LogAndShow("OnLaunched", ex);
            throw;
        }
    }

    private void OnUnhandled(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        LogAndShow("UI Unhandled", e.Exception);
    }

    private void OnDomainUnhandled(object? sender, System.UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            LogAndShow("Domain Unhandled", ex);
    }

    private static void LogAndShow(string origin, Exception ex)
    {
        try
        {
            var logPath = Path.Combine(Path.GetTempPath(), "SanctumPreview.log");
            File.AppendAllText(logPath,
                $"[{DateTimeOffset.Now:O}] {origin}\n{ex}\n{new string('-', 80)}\n");
            MessageBoxW(nint.Zero, $"{origin}\n\n{ex.GetType().Name}: {ex.Message}\n\nLog: {logPath}", "Sanctum Preview", 0x10);
        }
        catch { }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);
}
