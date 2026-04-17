using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace HermesDesktop.Controls;

public sealed partial class GlyphSidebar : UserControl
{
    public event EventHandler<string>? NavigationRequested;

    private string _selectedTag = "";

    private static readonly (string Glyph, string Tag, string Tooltip)[] NavItemDefs = new[]
    {
        ("\u2302", "dashboard", "Dashboard"),      // ⌂
        ("\u2709", "chat", "Chat"),                // ✉
        ("\u2605", "agent", "Agent"),              // ★
        ("\u2726", "skills", "Skills"),            // ✦
        ("\u25C7", "memory", "Memory"),            // ◇
        ("\u263C", "buddy", "Buddy"),              // ☼
        ("\u2194", "integrations", "Integrations"),// ↔
    };

    public GlyphSidebar()
    {
        InitializeComponent();
        BuildNavItems();
    }

    private void BuildNavItems()
    {
        foreach (var (glyph, tag, tooltip) in NavItemDefs)
        {
            var button = CreateNavButton(glyph, tag, tooltip);
            NavItems.Items.Add(button);
        }
    }

    private Grid CreateNavButton(string glyph, string tag, string tooltip)
    {
        var container = new Grid
        {
            Width = 44,
            Height = 44,
            Tag = tag,
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Colors.Transparent),
        };

        // Active indicator (left bar) — hidden by default
        var indicator = new Border
        {
            Name = "Indicator",
            Tag = "indicator",
            Width = 3,
            Height = 20,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(-10, 0, 0, 0),
            CornerRadius = new CornerRadius(2),
            Visibility = Visibility.Collapsed,
            Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(0, 1),
                GradientStops =
                {
                    new GradientStop { Color = ColorHelper.FromArgb(255, 255, 215, 0), Offset = 0 },
                    new GradientStop { Color = ColorHelper.FromArgb(255, 212, 160, 23), Offset = 1 },
                }
            },
        };

        var text = new TextBlock
        {
            Text = glyph,
            FontSize = 18,
            Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 58, 69, 85)), // #3A4555
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };

        container.Children.Add(indicator);
        container.Children.Add(text);

        ToolTipService.SetToolTip(container, new ToolTip
        {
            Content = tooltip,
            Background = new SolidColorBrush(ColorHelper.FromArgb(255, 26, 32, 48)), // #1A2030
            BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 42, 53, 69)), // #2A3545
            Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 192, 200, 212)), // #C0C8D4
            CornerRadius = new CornerRadius(8),
        });

        container.PointerEntered += (s, e) =>
        {
            if (container.Tag as string != _selectedTag)
            {
                text.Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 122, 133, 149)); // #7A8595
                container.Background = new SolidColorBrush(ColorHelper.FromArgb(2, 255, 255, 255)); // #FFFFFF06
            }
        };

        container.PointerExited += (s, e) =>
        {
            if (container.Tag as string != _selectedTag)
            {
                text.Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 58, 69, 85));
                container.Background = new SolidColorBrush(Colors.Transparent);
            }
        };

        container.Tapped += (s, e) =>
        {
            var t = container.Tag as string ?? "";
            SetSelected(t);
            NavigationRequested?.Invoke(this, t);
        };

        return container;
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        SetSelected("settings");
        NavigationRequested?.Invoke(this, "settings");
    }

    public void SetSelected(string tag)
    {
        _selectedTag = tag;

        // Update all nav items
        foreach (var item in NavItems.Items.OfType<Grid>())
        {
            var itemTag = item.Tag as string ?? "";
            var isActive = itemTag == tag;

            var textBlock = item.Children.OfType<TextBlock>().FirstOrDefault();
            var indicator = item.Children.OfType<Border>().FirstOrDefault(b => b.Tag as string == "indicator");

            if (textBlock is not null)
            {
                textBlock.Foreground = new SolidColorBrush(isActive
                    ? ColorHelper.FromArgb(255, 255, 215, 0)   // #FFD700
                    : ColorHelper.FromArgb(255, 58, 69, 85));  // #3A4555
            }

            item.Background = new SolidColorBrush(isActive
                ? ColorHelper.FromArgb(16, 212, 160, 23) // #D4A01710
                : Colors.Transparent);

            if (indicator is not null)
                indicator.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
        }

        // Update settings button
        var settingsText = SettingsButton.Content as TextBlock;
        if (settingsText is not null)
        {
            settingsText.Foreground = new SolidColorBrush(tag == "settings"
                ? ColorHelper.FromArgb(255, 255, 215, 0)
                : ColorHelper.FromArgb(255, 58, 69, 85));
        }
    }
}
