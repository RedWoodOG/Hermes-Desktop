using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace HermesDesktop.Controls;

/// <summary>
/// Control for displaying syntax-highlighted code blocks with copy functionality.
/// </summary>
public sealed partial class CodeBlockView : UserControl
{
    public CodeBlockView()
    {
        InitializeComponent();
    }
    
    public static readonly DependencyProperty CodeProperty =
        DependencyProperty.Register(nameof(Code), typeof(string), typeof(CodeBlockView),
            new PropertyMetadata(string.Empty, OnCodeChanged));
    
    public string Code
    {
        get => (string)GetValue(CodeProperty);
        set => SetValue(CodeProperty, value);
    }
    
    private static void OnCodeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (CodeBlockView)d;
        control.CodeBlock.Text = e.NewValue as string ?? string.Empty;
    }
    
    public static readonly DependencyProperty CodeLanguageProperty =
        DependencyProperty.Register(nameof(CodeLanguage), typeof(string), typeof(CodeBlockView),
            new PropertyMetadata("code", OnCodeLanguageChanged));

    public string CodeLanguage
    {
        get => (string)GetValue(CodeLanguageProperty);
        set => SetValue(CodeLanguageProperty, value);
    }

    private static void OnCodeLanguageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (CodeBlockView)d;
        control.LanguageBlock.Text = e.NewValue as string ?? "code";
    }
    
    public static readonly DependencyProperty FilePathProperty =
        DependencyProperty.Register(nameof(FilePath), typeof(string), typeof(CodeBlockView),
            new PropertyMetadata(null, OnFilePathChanged));
    
    public string? FilePath
    {
        get => (string?)GetValue(FilePathProperty);
        set => SetValue(FilePathProperty, value);
    }
    
    private static void OnFilePathChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (CodeBlockView)d;
        var path = e.NewValue as string;
        
        if (!string.IsNullOrEmpty(path))
        {
            control.FilePathBlock.Text = path;
            control.FilePathBlock.Visibility = Visibility.Visible;
        }
        else
        {
            control.FilePathBlock.Visibility = Visibility.Collapsed;
        }
    }
    
    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        var dataPackage = new DataPackage();
        dataPackage.SetText(Code);
        Clipboard.SetContent(dataPackage);
        
        // Show feedback
        CopyButton.Content = "Copied!";
        
        // Reset after delay
        _ = Task.Run(async () =>
        {
            await Task.Delay(2000);
            DispatcherQueue.TryEnqueue(() => CopyButton.Content = "Copy");
        });
    }
    
    /// <summary>
    /// Parse markdown code block and create view.
    /// </summary>
    public static CodeBlockView FromMarkdown(string markdown)
    {
        // Parse ```language\ncode```
        var lines = markdown.Split('\n');
        var language = "code";
        var code = new System.Text.StringBuilder();
        var inCode = false;
        
        foreach (var line in lines)
        {
            if (line.StartsWith("```"))
            {
                if (!inCode)
                {
                    language = line.Substring(3).Trim();
                    if (string.IsNullOrEmpty(language)) language = "code";
                    inCode = true;
                }
                else
                {
                    break;
                }
            }
            else if (inCode)
            {
                code.AppendLine(line);
            }
        }
        
        return new CodeBlockView
        {
            CodeLanguage = language,
            Code = code.ToString().TrimEnd()
        };
    }
}
