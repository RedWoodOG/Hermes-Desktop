using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace HermesDesktop.Controls;

/// <summary>
/// Control for displaying tool use invocations with input/output.
/// </summary>
public sealed partial class ToolUseView : UserControl
{
    public ToolUseView()
    {
        InitializeComponent();
    }
    
    public static readonly DependencyProperty ToolNameProperty =
        DependencyProperty.Register(nameof(ToolName), typeof(string), typeof(ToolUseView),
            new PropertyMetadata(string.Empty, OnToolNameChanged));
    
    public string ToolName
    {
        get => (string)GetValue(ToolNameProperty);
        set => SetValue(ToolNameProperty, value);
    }
    
    private static void OnToolNameChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (ToolUseView)d;
        control.ToolNameBlock.Text = e.NewValue as string ?? string.Empty;
    }
    
    public static readonly DependencyProperty StatusProperty =
        DependencyProperty.Register(nameof(Status), typeof(ToolStatus), typeof(ToolUseView),
            new PropertyMetadata(ToolStatus.Pending, OnStatusChanged));
    
    public ToolStatus Status
    {
        get => (ToolStatus)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }
    
    private static void OnStatusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (ToolUseView)d;
        var status = (ToolStatus)e.NewValue;
        
        control.StatusBlock.Text = status switch
        {
            ToolStatus.Pending => "Pending",
            ToolStatus.Running => "Running",
            ToolStatus.Success => "Success",
            ToolStatus.Error => "Error",
            _ => "Unknown"
        };
        
        control.StatusBadge.Background = status switch
        {
            ToolStatus.Pending => new SolidColorBrush(Color.FromArgb(255, 128, 128, 128)),
            ToolStatus.Running => new SolidColorBrush(Color.FromArgb(255, 227, 139, 82)),
            ToolStatus.Success => new SolidColorBrush(Color.FromArgb(255, 76, 175, 80)),
            ToolStatus.Error => new SolidColorBrush(Color.FromArgb(255, 244, 67, 54)),
            _ => new SolidColorBrush(Color.FromArgb(255, 128, 128, 128))
        };
    }
    
    public static readonly DependencyProperty InputProperty =
        DependencyProperty.Register(nameof(Input), typeof(string), typeof(ToolUseView),
            new PropertyMetadata(null, OnInputChanged));
    
    public string? Input
    {
        get => (string?)GetValue(InputProperty);
        set => SetValue(InputProperty, value);
    }
    
    private static void OnInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (ToolUseView)d;
        var input = e.NewValue as string;
        
        if (!string.IsNullOrEmpty(input))
        {
            control.InputBlock.Text = input;
            control.InputExpander.Visibility = Visibility.Visible;
        }
        else
        {
            control.InputExpander.Visibility = Visibility.Collapsed;
        }
    }
    
    public static readonly DependencyProperty OutputProperty =
        DependencyProperty.Register(nameof(Output), typeof(string), typeof(ToolUseView),
            new PropertyMetadata(null, OnOutputChanged));
    
    public string? Output
    {
        get => (string?)GetValue(OutputProperty);
        set => SetValue(OutputProperty, value);
    }
    
    private static void OnOutputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (ToolUseView)d;
        var output = e.NewValue as string;
        
        if (!string.IsNullOrEmpty(output))
        {
            control.OutputBlock.Text = output;
            control.OutputBorder.Visibility = Visibility.Visible;
        }
        else
        {
            control.OutputBorder.Visibility = Visibility.Collapsed;
        }
    }
    
    /// <summary>
    /// Set tool state to running.
    /// </summary>
    public void SetRunning()
    {
        Status = ToolStatus.Running;
    }
    
    /// <summary>
    /// Set tool state to success with output.
    /// </summary>
    public void SetSuccess(string output)
    {
        Status = ToolStatus.Success;
        Output = output;
    }
    
    /// <summary>
    /// Set tool state to error with message.
    /// </summary>
    public void SetError(string error)
    {
        Status = ToolStatus.Error;
        Output = error;
    }
}

public enum ToolStatus
{
    Pending,
    Running,
    Success,
    Error
}
