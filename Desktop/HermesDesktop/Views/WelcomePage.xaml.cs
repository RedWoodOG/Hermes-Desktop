using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HermesDesktop.Views;

/// <summary>
/// Bundle E.8 — first-run welcome screen extracted from the in-chat onboarding
/// path. Hosts no business logic; just routes the user to <see cref="SetupPage"/>
/// (Get started) or directly into <see cref="ChatPage"/> (Skip).
/// </summary>
public sealed partial class WelcomePage : Page
{
    public WelcomePage()
    {
        InitializeComponent();
    }

    private void GetStarted_Click(object sender, RoutedEventArgs e)
    {
        if (App.Current is App app && app.MainWindow is { } window)
            window.NavigateToTag("setup");
    }

    private async void Skip_Click(object sender, RoutedEventArgs e)
    {
        if (App.Current is App app && app.MainWindow is { } window)
        {
            // Await the marker-strip BEFORE navigating so ChatPage's IsFirstRun()
            // re-read sees the cleared state and does not loop back to onboarding.
            await window.MarkFirstRunCompleteAsync();
            window.NavigateToTag("chat");
        }
    }
}
