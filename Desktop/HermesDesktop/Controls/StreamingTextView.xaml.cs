using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using System.Text;

namespace HermesDesktop.Controls;

/// <summary>
/// Control for displaying streaming text with real-time updates.
/// </summary>
public sealed partial class StreamingTextView : UserControl
{
    private readonly StringBuilder _content = new();
    private readonly Run _textRun = new();
    private bool _isStreaming;
    
    public StreamingTextView()
    {
        InitializeComponent();
        
        var paragraph = new Paragraph();
        paragraph.Inlines.Add(_textRun);
        ContentBlock.Blocks.Add(paragraph);
    }
    
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(StreamingTextView),
            new PropertyMetadata(string.Empty, OnTextChanged));
    
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }
    
    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (StreamingTextView)d;
        control._textRun.Text = e.NewValue as string ?? string.Empty;
    }
    
    /// <summary>
    /// Append text during streaming.
    /// </summary>
    public void AppendText(string text)
    {
        _content.Append(text);
        _textRun.Text = _content.ToString();
        
        // Position cursor at end
        UpdateCursorPosition();
    }
    
    /// <summary>
    /// Start streaming mode - show cursor.
    /// </summary>
    public void StartStreaming()
    {
        _isStreaming = true;
        CursorIndicator.Visibility = Visibility.Visible;
        CursorAnimation();
    }
    
    /// <summary>
    /// End streaming mode - hide cursor.
    /// </summary>
    public void EndStreaming()
    {
        _isStreaming = false;
        CursorIndicator.Visibility = Visibility.Collapsed;
    }
    
    /// <summary>
    /// Clear all content.
    /// </summary>
    public void Clear()
    {
        _content.Clear();
        _textRun.Text = string.Empty;
    }
    
    /// <summary>
    /// Get full text content.
    /// </summary>
    public string GetContent() => _content.ToString();
    
    private void UpdateCursorPosition()
    {
        // Position cursor after last character
        // This is simplified - real impl would measure text position
    }
    
    private async void CursorAnimation()
    {
        while (_isStreaming)
        {
            CursorIndicator.Opacity = CursorIndicator.Opacity > 0.5 ? 0.2 : 1.0;
            await Task.Delay(500);
        }
    }
}
