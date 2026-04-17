using Hermes.Agent.Buddy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Numerics;

namespace HermesDesktop.Controls;

public sealed partial class BuddyOverlay : UserControl
{
    private static readonly SolidColorBrush DimBrush = new(ColorHelper.FromArgb(0x60, 0xD4, 0xA0, 0x17));
    private static readonly SolidColorBrush BrightBrush = new(ColorHelper.FromArgb(255, 255, 215, 0));

    public BuddyOverlay()
    {
        InitializeComponent();
        PointerEntered += OnPointerEntered;
        PointerExited += OnPointerExited;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var buddyService = App.Services?.GetService<BuddyService>();
        if (buddyService is null)
        {
            Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            var buddy = await buddyService.GetBuddyAsync(
                Environment.UserName, CancellationToken.None);

            BuddyArt.Text = BuddyRenderer.RenderAscii(buddy);
            MoodLabel.Text = buddy.Personality?.ToLowerInvariant() ?? "curious";
        }
        catch
        {
            // Keep default cat art
        }
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        BuddyArt.Foreground = BrightBrush;
        Translation = new Vector3(0, -4, 0);
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        BuddyArt.Foreground = DimBrush;
        Translation = Vector3.Zero;
    }
}
