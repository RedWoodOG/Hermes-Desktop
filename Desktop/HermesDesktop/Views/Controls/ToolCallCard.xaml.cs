using HermesDesktop.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace HermesDesktop.Views.Controls;

public sealed partial class ToolCallCard : UserControl
{
    private bool _isExpanded;
    private ToolCallInfo? _boundInfo;

    public ToolCallCard()
    {
        InitializeComponent();
        Tapped += OnTapped;
    }

    public void Bind(ToolCallInfo info)
    {
        if (_boundInfo is not null) _boundInfo.PropertyChanged -= OnInfoChanged;
        _boundInfo = info;
        _boundInfo.PropertyChanged += OnInfoChanged;
        ToolNameText.Text = info.Name;
        ArgsText.Text = info.Arguments;
        ResultText.Text = info.Result ?? "(pending)";
        UpdateStatus(info.Status);
    }

    private void OnInfoChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() => {
            if (e.PropertyName == nameof(ToolCallInfo.Status)) UpdateStatus(_boundInfo!.Status);
            if (e.PropertyName == nameof(ToolCallInfo.Result)) ResultText.Text = _boundInfo!.Result ?? "";
        });
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
