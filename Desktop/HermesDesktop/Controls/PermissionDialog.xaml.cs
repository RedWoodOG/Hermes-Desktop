using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HermesDesktop.Controls;

/// <summary>
/// Dialog for requesting user permission for tool execution.
/// </summary>
public sealed partial class PermissionDialog : UserControl
{
    private PermissionDecision _decision = PermissionDecision.Pending;
    
    public PermissionDialog()
    {
        InitializeComponent();
    }
    
    public static readonly DependencyProperty ToolNameProperty =
        DependencyProperty.Register(nameof(ToolName), typeof(string), typeof(PermissionDialog),
            new PropertyMetadata(string.Empty, OnToolNameChanged));
    
    public string ToolName
    {
        get => (string)GetValue(ToolNameProperty);
        set => SetValue(ToolNameProperty, value);
    }
    
    private static void OnToolNameChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (PermissionDialog)d;
        control.ToolBlock.Text = $"Tool: {e.NewValue}";
    }
    
    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(PermissionDialog),
            new PropertyMetadata(string.Empty, OnDescriptionChanged));
    
    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }
    
    private static void OnDescriptionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (PermissionDialog)d;
        control.DescriptionBlock.Text = e.NewValue as string ?? string.Empty;
    }
    
    public static readonly DependencyProperty DetailsProperty =
        DependencyProperty.Register(nameof(Details), typeof(string), typeof(PermissionDialog),
            new PropertyMetadata(null, OnDetailsChanged));
    
    public string? Details
    {
        get => (string?)GetValue(DetailsProperty);
        set => SetValue(DetailsProperty, value);
    }
    
    private static void OnDetailsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (PermissionDialog)d;
        control.DetailsBlock.Text = e.NewValue as string ?? string.Empty;
    }
    
    public event EventHandler<PermissionResult>? PermissionResolved;
    
    private void OptionsPanel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OptionsPanel.SelectedItem is RadioButton radio && radio.Tag is string tag)
        {
            _decision = tag switch
            {
                "allow_once" => PermissionDecision.AllowOnce,
                "allow_always" => PermissionDecision.AllowAlways,
                "deny" => PermissionDecision.Deny,
                _ => PermissionDecision.Pending
            };
        }
    }
    
    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        PermissionResolved?.Invoke(this, new PermissionResult(PermissionDecision.Deny));
    }
    
    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        if (_decision == PermissionDecision.Pending)
        {
            _decision = PermissionDecision.AllowOnce;
        }
        
        PermissionResolved?.Invoke(this, new PermissionResult(_decision));
    }
    
    /// <summary>
    /// Show the permission dialog as a flyout.
    /// </summary>
    public static async Task<PermissionResult> ShowAsync(
        FrameworkElement anchor,
        string toolName,
        string description,
        string? details = null)
    {
        var tcs = new TaskCompletionSource<PermissionResult>();
        
        var dialog = new PermissionDialog
        {
            ToolName = toolName,
            Description = description,
            Details = details
        };
        
        dialog.PermissionResolved += (s, e) => tcs.TrySetResult(e);
        
        var flyout = new Flyout { Content = dialog };
        flyout.ShowAt(anchor);
        
        return await tcs.Task;
    }
}

public enum PermissionDecision
{
    Pending,
    AllowOnce,
    AllowAlways,
    Deny
}

public sealed record PermissionResult(PermissionDecision Decision);
