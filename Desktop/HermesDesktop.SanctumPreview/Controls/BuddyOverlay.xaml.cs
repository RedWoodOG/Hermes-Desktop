using Microsoft.UI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Numerics;

namespace SanctumPreview.Controls;

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
