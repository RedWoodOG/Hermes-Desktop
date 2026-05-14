using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using HermesDesktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;
using Microsoft.Windows.ApplicationModel.Resources;

namespace HermesDesktop.Views;

public sealed partial class DiagnosticsPage : Page
{
    private static readonly ResourceLoader Loader = new();

    public DiagnosticsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshSummary();
        RefreshTail();
    }

    private void RefreshSummary()
    {
        string ver = FormatAppVersion();
        string logExists = File.Exists(HermesEnvironment.DesktopStartupLogPath)
            ? Loader.GetString("DiagnosticsSummaryYes")
            : Loader.GetString("DiagnosticsSummaryNo");
        string? sizeLine = null;
        if (File.Exists(HermesEnvironment.DesktopStartupLogPath))
        {
            long len = new FileInfo(HermesEnvironment.DesktopStartupLogPath).Length;
            sizeLine = string.Format(
                CultureInfo.InvariantCulture,
                Loader.GetString("DiagnosticsSummaryLogSizeLine"),
                len.ToString(CultureInfo.InvariantCulture));
        }

        var lines = new List<string>
        {
            string.Format(CultureInfo.InvariantCulture, Loader.GetString("DiagnosticsSummaryVersionLine"), ver),
            string.Format(CultureInfo.InvariantCulture, Loader.GetString("DiagnosticsSummaryOsLine"), RuntimeInformation.OSDescription),
            string.Format(CultureInfo.InvariantCulture, Loader.GetString("DiagnosticsSummaryFrameworkLine"), RuntimeInformation.FrameworkDescription),
            string.Format(CultureInfo.InvariantCulture, Loader.GetString("DiagnosticsSummaryHermesHomeLine"), HermesEnvironment.DisplayHermesHomePath),
            string.Format(CultureInfo.InvariantCulture, Loader.GetString("DiagnosticsSummaryStartupLogLine"), HermesEnvironment.DisplayDesktopStartupLogPath),
            string.Format(CultureInfo.InvariantCulture, Loader.GetString("DiagnosticsSummaryLogExistsLine"), logExists),
        };
        if (sizeLine is not null)
            lines.Add(sizeLine);

        lines.Add(string.Format(CultureInfo.InvariantCulture, Loader.GetString("DiagnosticsSummaryPrivacyLine"), HermesEnvironment.PrivacyModeEnabled));
        SummaryBody.Text = string.Join(Environment.NewLine, lines);
    }

    private static string FormatAppVersion() =>
        $"v{typeof(global::HermesDesktop.App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? typeof(global::HermesDesktop.App).Assembly.GetName().Version?.ToString() ?? "?"}";

    private void RefreshTail()
    {
        TailText.Text = DiagnosticsReportBuilder.GetRedactedStartupLogTailForDisplay();
    }

    private void ReloadTail_Click(object sender, RoutedEventArgs e)
    {
        RefreshSummary();
        RefreshTail();
        ShowInfoBar(InfoBarSeverity.Informational, Loader.GetString("DiagnosticsTailRefreshedTitle"), Loader.GetString("DiagnosticsTailRefreshedMessage"));
    }

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string report = DiagnosticsReportBuilder.BuildReport(includeStartupLogTail: true);
            var package = new DataPackage();
            package.SetText(report);
            Clipboard.SetContent(package);
            ShowInfoBar(InfoBarSeverity.Success, Loader.GetString("DiagnosticsCopySuccessTitle"), Loader.GetString("DiagnosticsCopySuccessMessage"));
        }
        catch (Exception ex)
        {
            ShowInfoBar(InfoBarSeverity.Error, Loader.GetString("DiagnosticsCopyErrorTitle"), ex.Message);
        }
    }

    private async void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(HermesEnvironment.DesktopCsLogsDirectory);
            StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(HermesEnvironment.DesktopCsLogsDirectory);
            await Launcher.LaunchFolderAsync(folder);
        }
        catch (Exception ex)
        {
            ShowInfoBar(InfoBarSeverity.Error, Loader.GetString("DiagnosticsOpenFolderErrorTitle"), ex.Message);
        }
    }

    private void ShowInfoBar(InfoBarSeverity severity, string title, string message)
    {
        StatusInfoBar.Severity = severity;
        StatusInfoBar.Title = title;
        StatusInfoBar.Message = message;
        StatusInfoBar.IsOpen = true;
    }
}
