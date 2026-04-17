using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace SanctumPreview;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = "Sanctum Preview";
        AppWindow.Resize(new SizeInt32(1480, 960));
        GlyphNav.SetSelected("dashboard");
    }
}
