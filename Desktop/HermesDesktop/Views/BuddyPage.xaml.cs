using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Hermes.Agent.Buddy;
using Windows.UI;

namespace HermesDesktop.Views;

public sealed partial class BuddyPage : Page
{
    private BuddyService? _buddyService;
    private DispatcherQueueTimer? _animTimer;   // 500ms animation tick
    private DispatcherQueueTimer? _decayTimer;  // 30s decay tick
    private int _animTick;
    private bool _loaded;
    private string? _selectedSpecies;

    public BuddyPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) { StartTimers(); RefreshUI(); return; }
        _loaded = true;

        try
        {
            _buddyService = App.Services.GetRequiredService<BuddyService>();

            if (_buddyService.NeedsSelection)
            {
                ShowSelectionView();
            }
            else
            {
                var userId = Environment.UserName ?? "default";
                await _buddyService.GetBuddyAsync(userId, CancellationToken.None);
                ShowBuddyView();
                QuoteBubble.Text = BuddyQuotes.GetGreeting();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"BuddyPage load error: {ex}");
            // Fallback: show selection if loading failed
            ShowSelectionView();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _animTimer?.Stop();
        _decayTimer?.Stop();
    }

    // ============================================
    // SELECTION FLOW
    // ============================================

    private void ShowSelectionView()
    {
        SelectionView.Visibility = Visibility.Visible;
        BuddyView.Visibility = Visibility.Collapsed;
        LevelBadge.Visibility = Visibility.Collapsed;
        HeaderSubtitle.Text = "Choose your companion and give it a name";

        // Populate species grid
        var items = BuddySprites.AllSpecies.Select(s => new SpeciesItem
        {
            Name = s,
            Preview = BuddyRenderer.RenderPreview(s)
        }).ToList();
        SpeciesGrid.ItemsSource = items;
    }

    private void SpeciesCard_Click(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.Tag is not string species) return;
        _selectedSpecies = species;
        SelectedSpeciesText.Text = species;
        ConfirmBtn.IsEnabled = true;

        // Highlight selected card by iterating children
        // (ItemsRepeater doesn't have SelectedItem, so we use visual state)
        for (int i = 0; i < SpeciesGrid.ItemsSourceView.Count; i++)
        {
            var container = SpeciesGrid.TryGetElement(i);
            if (container is FrameworkElement fe)
            {
                var border = FindChildBorder(fe);
                if (border != null)
                {
                    var item = SpeciesGrid.ItemsSourceView.GetAt(i) as SpeciesItem;
                    border.BorderBrush = item?.Name == species
                        ? (Brush)Application.Current.Resources["AppAccentBrush"]
                        : (Brush)Application.Current.Resources["AppSubtleStrokeBrush"];
                }
            }
        }
    }

    private static Microsoft.UI.Xaml.Controls.Border? FindChildBorder(FrameworkElement element)
    {
        if (element is Microsoft.UI.Xaml.Controls.Border b) return b;
        if (element is Microsoft.UI.Xaml.Controls.Panel panel)
        {
            foreach (var child in panel.Children)
                if (child is Microsoft.UI.Xaml.Controls.Border cb) return cb;
        }
        return null;
    }

    private async void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedSpecies == null || _buddyService == null) return;
        var name = NameInput.Text?.Trim();
        if (string.IsNullOrEmpty(name)) name = _selectedSpecies;

        ConfirmBtn.IsEnabled = false;
        ConfirmBtn.Content = "Hatching...";

        try
        {
            var userId = Environment.UserName ?? "default";
            await _buddyService.SelectBuddyAsync(userId, _selectedSpecies, name, CancellationToken.None);
            ShowBuddyView();
            QuoteBubble.Text = $"Hi! I'm {name}! Nice to meet you!";
        }
        catch (Exception ex)
        {
            ConfirmBtn.IsEnabled = true;
            ConfirmBtn.Content = "Hatch!";
            System.Diagnostics.Debug.WriteLine($"BuddyPage hatch error: {ex}");
        }
    }

    // ============================================
    // BUDDY VIEW
    // ============================================

    private void ShowBuddyView()
    {
        SelectionView.Visibility = Visibility.Collapsed;
        BuddyView.Visibility = Visibility.Visible;
        LevelBadge.Visibility = Visibility.Visible;
        HeaderSubtitle.Text = "A Tamagotchi-style companion that grows with you";
        StartTimers();
        RefreshUI();
    }

    private void StartTimers()
    {
        // Animation timer: 500ms for sprite animation
        if (_animTimer == null)
        {
            _animTimer = DispatcherQueue.CreateTimer();
            _animTimer.Interval = TimeSpan.FromMilliseconds(500);
            _animTimer.Tick += (_, _) =>
            {
                _animTick++;
                RefreshAnimation();
            };
        }
        _animTimer.Start();

        // Decay timer: 30 seconds for needs decay
        if (_decayTimer == null)
        {
            _decayTimer = DispatcherQueue.CreateTimer();
            _decayTimer.Interval = TimeSpan.FromSeconds(30);
            _decayTimer.Tick += async (_, _) =>
            {
                try
                {
                    if (_buddyService != null)
                    {
                        await _buddyService.TickAsync();
                        RefreshUI();
                    }
                }
                catch { }
            };
        }
        _decayTimer.Start();
    }

    // ---- Interactions ----

    private async void Feed_Click(object sender, RoutedEventArgs e) => await DoInteraction(BuddyAction.Feed);
    private async void Play_Click(object sender, RoutedEventArgs e) => await DoInteraction(BuddyAction.Play);
    private async void Train_Click(object sender, RoutedEventArgs e) => await DoInteraction(BuddyAction.Train);
    private async void Pet_Click(object sender, RoutedEventArgs e) => await DoInteraction(BuddyAction.Pet);

    private async System.Threading.Tasks.Task DoInteraction(BuddyAction action)
    {
        if (_buddyService == null) return;
        try
        {
            var (resultKey, leveledUp) = await _buddyService.InteractAsync(action);
            RefreshUI();
            QuoteBubble.Text = leveledUp
                ? BuddyQuotes.GetLevelUpQuote(_buddyService.CurrentState?.Level ?? 1)
                : BuddyQuotes.GetInteractionResponse(resultKey);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"BuddyPage interaction error: {ex}");
        }
    }

    // ---- Refresh ----

    private void RefreshAnimation()
    {
        var buddy = _buddyService?.CurrentBuddy;
        if (buddy == null) return;
        var mood = _buddyService!.GetMood();
        AsciiArt.Text = BuddyRenderer.RenderFrame(buddy, mood, _animTick);
    }

    private void RefreshUI()
    {
        var buddy = _buddyService?.CurrentBuddy;
        var state = _buddyService?.CurrentState;
        if (buddy == null || state == null) return;

        var mood = _buddyService!.GetMood();

        MoodEmoji.Text = BuddyRenderer.GetMoodEmoji(mood);
        MoodText.Text = mood.ToString();
        AsciiArt.Text = BuddyRenderer.RenderFrame(buddy, mood, _animTick);
        BuddyName.Text = buddy.Name ?? buddy.Species;
        SpeciesText.Text = buddy.Species;
        RarityText.Text = buddy.Rarity;
        RarityBadge.Background = GetRarityBrush(buddy.Rarity);
        PersonalityText.Text = buddy.Personality ?? "";
        ShinyBadge.Visibility = buddy.IsShiny ? Visibility.Visible : Visibility.Collapsed;

        UpdateNeedBar(HungerBar, HungerValue, state.Hunger);
        UpdateNeedBar(HappinessBar, HappinessValue, state.Happiness);
        UpdateNeedBar(EnergyBar, EnergyValue, state.Energy);

        LevelText.Text = $"Level {state.Level}";
        LevelDisplay.Text = $"Lv. {state.Level}";
        XPDisplay.Text = $"{state.XP} / {state.XPToNextLevel} XP";
        XPBar.Maximum = state.XPToNextLevel;
        XPBar.Value = state.XP;
        TotalXPText.Text = $"{state.TotalXP} total XP";

        IntBar.Value = buddy.Stats.Intelligence; IntValue.Text = buddy.Stats.Intelligence.ToString();
        EnrBar.Value = buddy.Stats.Energy; EnrValue.Text = buddy.Stats.Energy.ToString();
        CreBar.Value = buddy.Stats.Creativity; CreValue.Text = buddy.Stats.Creativity.ToString();
        FrnBar.Value = buddy.Stats.Friendliness; FrnValue.Text = buddy.Stats.Friendliness.ToString();

        HatchedText.Text = $"Hatched: {buddy.HatchedAt:yyyy-MM-dd}";
        EyesText.Text = $"Eyes: {buddy.Eyes}";
        HatText.Text = string.IsNullOrEmpty(buddy.Hat) ? "Hat: none" : $"Hat: {buddy.Hat}";
    }

    private static void UpdateNeedBar(ProgressBar bar, TextBlock label, int value)
    {
        bar.Value = value;
        label.Text = value.ToString();
        bar.Foreground = new SolidColorBrush(value switch
        {
            > 60 => Color.FromArgb(255, 107, 203, 119),
            > 30 => Color.FromArgb(255, 220, 200, 80),
            _ => Color.FromArgb(255, 220, 100, 100)
        });
    }

    private static SolidColorBrush GetRarityBrush(string rarity) => new(rarity switch
    {
        "legendary" => Color.FromArgb(255, 200, 160, 50),
        "rare" => Color.FromArgb(255, 100, 140, 200),
        "uncommon" => Color.FromArgb(255, 100, 180, 100),
        _ => Color.FromArgb(255, 58, 58, 0)
    });
}

public sealed class SpeciesItem
{
    public string Name { get; set; } = "";
    public string Preview { get; set; } = "";
}
