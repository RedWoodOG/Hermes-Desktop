using System;
using System.Threading;
using Hermes.Agent.Buddy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HermesDesktop.Views.Panels;

public sealed partial class BuddyPanel : UserControl
{
    private readonly BuddyService _buddyService;

    public BuddyPanel()
    {
        InitializeComponent();
        _buddyService = App.Services.GetRequiredService<BuddyService>();
        Loaded += async (_, _) => await LoadBuddyAsync();
    }

    private async System.Threading.Tasks.Task LoadBuddyAsync()
    {
        try
        {
            var storedId = _buddyService.TryReadStoredUserId();
            var winUser = Environment.UserName?.Trim();
            var userId = !string.IsNullOrWhiteSpace(storedId)
                ? storedId.Trim()
                : (string.IsNullOrEmpty(winUser) ? "default" : winUser);

            if (!_buddyService.HasSavedBuddy)
            {
                EmptyState.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                BuddyContent.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                return;
            }

            var buddy = await _buddyService.GetBuddyAsync(userId, CancellationToken.None);

            EmptyState.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            BuddyContent.Visibility = Microsoft.UI.Xaml.Visibility.Visible;

            AsciiArtText.Text = BuddyRenderer.RenderAscii(buddy);
            BuddyNameText.Text = buddy.Name ?? "Unnamed";
            SpeciesText.Text = buddy.Species;
            RarityText.Text = buddy.Rarity;
            RarityBadge.Background = GetRarityColor(buddy.Rarity);

            IntBar.Value = buddy.Stats.Intelligence;
            EnrBar.Value = buddy.Stats.Energy;
            CreBar.Value = buddy.Stats.Creativity;
            FrnBar.Value = buddy.Stats.Friendliness;

            SoulText.Text = buddy.Personality ?? "This buddy hasn't found its soul yet.";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"BuddyPanel failed to load buddy: {ex}");
            BuddyNameText.Text = "Buddy unavailable";
            SoulText.Text = "Could not load buddy. Check LLM connection.";
        }
    }

    private static SolidColorBrush GetRarityColor(string rarity) => rarity.ToLowerInvariant() switch
    {
        "legendary" => new SolidColorBrush(ColorHelper.FromArgb(255, 255, 200, 50)),
        "rare" => new SolidColorBrush(ColorHelper.FromArgb(255, 100, 140, 220)),
        "uncommon" => new SolidColorBrush(ColorHelper.FromArgb(255, 100, 200, 100)),
        _ => new SolidColorBrush(ColorHelper.FromArgb(255, 140, 140, 140))
    };
}
