using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Hermes.Agent.Updates;
using HermesDesktop.Services;
using HermesDesktop.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.ApplicationModel.Resources;
using Windows.Graphics;
using Windows.System;

namespace HermesDesktop;

public sealed partial class MainWindow : Window
{
    private static readonly IReadOnlyDictionary<string, System.Type> PageMap = new Dictionary<string, System.Type>
    {
        ["dashboard"] = typeof(DashboardPage),
        ["chat"] = typeof(ChatPage),
        ["agent"] = typeof(AgentPage),
        ["skills"] = typeof(SkillsPage),
        ["memory"] = typeof(MemoryPage),
        ["buddy"] = typeof(BuddyPage),
        ["integrations"] = typeof(IntegrationsPage),
        ["mcp"] = typeof(McpPage),
        ["diagnostics"] = typeof(DiagnosticsPage),
        ["settings"] = typeof(SettingsPage),
        ["welcome"] = typeof(WelcomePage),
        ["setup"] = typeof(SetupPage),
    };

    private static readonly ResourceLoader ResourceLoader = new();

    private Uri? _pendingReleasePageUri;

    // Track presenter kind so AppWindow.Changed can detect the
    // overlapped→maximized transition that triggered the v2.4.0 stretch bug.
    private AppWindowPresenterKind _lastPresenterKind;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        Title = ResourceLoader.GetString("WindowTitle");
        AppTitleBar.Title = Title;
        AppWindow.Resize(new SizeInt32(1480, 960));
        AppWindow.SetIcon("Assets/AppIcon.ico");

        // Hook AppWindow.Changed to fix the drag-maximize layout regression.
        // When the user drags the title bar to the top edge of the screen,
        // Windows raises a presenter change to OverlappedPresenter+Maximized,
        // but the root XAML tree retains its previous size for one frame and
        // renders stretched/clipped until something forces a measure pass.
        // Force an InvalidateMeasure on the root element when presenter
        // kind changes — cheap (single layout pass) and idempotent.
        _lastPresenterKind = AppWindow.Presenter?.Kind ?? AppWindowPresenterKind.Default;
        AppWindow.Changed += OnAppWindowChanged;

        if (TryRouteFirstRun())
        {
            // First-run wizard owns navigation; chat will be selected after Finish/Skip.
        }
        else
        {
            ShellNavigation.SelectedItem = ChatNavItem;
            NavigateToTag("chat");
        }

        ScheduleDeferredUpdateCheck();
    }

    private bool TryRouteFirstRun()
    {
        try
        {
            var soulService = App.Services.GetService(typeof(Hermes.Agent.Soul.SoulService))
                as Hermes.Agent.Soul.SoulService;
            if (soulService is null) return false;
            if (!soulService.IsFirstRun()) return false;

            ShellNavigation.SelectedItem = null;
            NavigateToTag("welcome");
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"MainWindow.TryRouteFirstRun failed: {ex}");
            return false;
        }
    }

    /// <summary>
    /// Called by Welcome/Setup pages when the user finishes or skips the wizard.
    /// Strips the <c>&lt;!-- UNCONFIGURED --&gt;</c> markers from SOUL.md/USER.md
    /// so <see cref="Hermes.Agent.Soul.SoulService.IsFirstRun"/> returns false on
    /// next launch.
    /// <para>
    /// Contract: callers MUST <c>await</c> this method before navigating away
    /// from the wizard (otherwise <see cref="HermesDesktop.Views.ChatPage"/>
    /// can re-read a still-marked file and bounce the user back into onboarding,
    /// the original Cursor Bugbot race). Navigation itself is the caller's
    /// responsibility — each caller explicitly invokes <c>NavigateToTag("chat")</c>
    /// so that the failure mode of "marker write failed" still surfaces a usable
    /// Chat page rather than silently leaving the user on the wizard.
    /// </para>
    /// <para>Best-effort: write failures are logged but never thrown.</para>
    /// </summary>
    internal async Task MarkFirstRunCompleteAsync()
    {
        try
        {
            var soulService = App.Services.GetService(typeof(Hermes.Agent.Soul.SoulService))
                as Hermes.Agent.Soul.SoulService;

            if (soulService is not null)
            {
                try
                {
                    await soulService.MarkConfiguredAsync();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"MainWindow.MarkFirstRunCompleteAsync strip failed: {ex}");
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"MainWindow.MarkFirstRunCompleteAsync failed: {ex}");
        }
    }

    private async void ScheduleDeferredUpdateCheck()
    {
        try
        {
            if (!UpdateService.GetCheckOnStartupEnabled() || UpdateService.IsUpdateCheckDisabledByEnvironment())
                return;

            await Task.Delay(TimeSpan.FromSeconds(5));
            var svc = App.Services.GetRequiredService<UpdateService>();
            var result = await svc.CheckForUpdatesAsync();
            if (result.Status != PortableUpdateCheckStatus.UpdateAvailable || result.Offer is null)
                return;

            var offer = result.Offer;
            _pendingReleasePageUri = offer.ReleasePageUri;
            string tagName = offer.TagName;
            DispatcherQueue.TryEnqueue(() =>
            {
                UpdateAvailableInfoBar.Title = ResourceLoader.GetString("UpdateBannerTitle");
                UpdateAvailableInfoBar.Message = string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    ResourceLoader.GetString("UpdateBannerMessage"),
                    tagName);
                UpdateAvailableInfoBar.Severity = InfoBarSeverity.Informational;
                UpdateAvailableInfoBar.Visibility = Visibility.Visible;
                UpdateAvailableInfoBar.IsOpen = true;
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Best-effort: never block shell startup on update telemetry.
        }
    }

    private void OnUpdateBannerClosed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        sender.Visibility = Visibility.Collapsed;
    }

    private async void OnUpdateBannerOpenReleaseClick(object sender, RoutedEventArgs e)
    {
        var u = _pendingReleasePageUri;
        if (u is not null)
            _ = await Launcher.LaunchUriAsync(u);
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        // Only react to presenter changes — Position/Size events fire constantly
        // during drag and would thrash the layout tree for no benefit.
        if (!args.DidPresenterChange) return;

        var newKind = sender.Presenter?.Kind ?? AppWindowPresenterKind.Default;
        if (newKind == _lastPresenterKind) return;
        _lastPresenterKind = newKind;

        // Walk to the root visual element. Window.Content is the XAML root we
        // own; UpdateLayout() forces an immediate measure+arrange pass so the
        // stretched-frame artifact never reaches the compositor.
        if (Content is FrameworkElement root)
        {
            root.InvalidateMeasure();
            root.InvalidateArrange();
            root.UpdateLayout();
        }
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is string tag)
        {
            NavigateToTag(tag);
        }
    }

    internal void NavigateToTag(string tag)
    {
        if (PageMap.TryGetValue(tag, out System.Type? pageType) && ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType);
        }
    }
}
