using HermesDesktop.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace HermesDesktop.Views.Controls;

public sealed partial class ToolCallCard : UserControl
{
    private bool _isExpanded;

    public ToolCallCard()
    {
        InitializeComponent();
        Tapped += OnTapped;
    }

    public void Bind(ToolCallInfo info)
    {
        ToolNameText.Text = info.Name;
        ArgsText.Text = info.Arguments;
        ResultText.Text = info.Result ?? "(pending)";
        UpdateStatus(info.Status);

        info.PropertyChanged += (_, e) =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (e.PropertyName == nameof(info.Status)) UpdateStatus(info.Status);
                if (e.PropertyName == nameof(info.Result)) ResultText.Text = info.Result ?? "";
            });
        };
    }

    private void UpdateStatus(string status)
    {
        StatusText.Text = status switch
        {
            "running" => "Running...",
            "completed" => "Done",
            "error" => "Error",
            _ => "Pending"
        };
    }

    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        _isExpanded = !_isExpanded;
        DetailPanel.Visibility = _isExpanded ? Visibility.Visible : Visibility.Collapsed;
    }
}
