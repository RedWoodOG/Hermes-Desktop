using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using HermesDesktop.Services;

namespace HermesDesktop.Views.Panels;

public sealed class MemoryListItem
{
    public string Filename { get; set; } = "";
    public string FullPath { get; set; } = "";
    public string Type { get; set; } = "unknown";
    public string Content { get; set; } = "";
    public string Age { get; set; } = "";
    public SolidColorBrush TypeColor { get; set; } = new(ColorHelper.FromArgb(255, 100, 100, 100));
    public SolidColorBrush AgeBrush { get; set; } = new(ColorHelper.FromArgb(255, 149, 162, 177));
}

public sealed partial class MemoryPanel : UserControl
{
    private readonly string _memoryDir;

    public ObservableCollection<MemoryListItem> Memories { get; } = new();

    public MemoryPanel()
    {
        InitializeComponent();
        _memoryDir = Path.Combine(HermesEnvironment.HermesHomePath, "hermes-cs", "memory");
        Loaded += (_, _) => Refresh();
    }

    public void Refresh()
    {
        Memories.Clear();
        if (!Directory.Exists(_memoryDir)) { MemoryList.ItemsSource = Memories; return; }

        foreach (var file in Directory.EnumerateFiles(_memoryDir, "*.md").OrderByDescending(f => File.GetLastWriteTimeUtc(f)))
        {
            try
            {
                var content = File.ReadAllText(file);
                var type = "unknown";
                if (content.StartsWith("---"))
                {
                    var end = content.IndexOf("---", 3);
                    if (end > 0)
                    {
                        var fm = content[3..end];
                        var typeLine = fm.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith("type:"));
                        if (typeLine is not null) type = typeLine.Split(':', 2)[1].Trim();
                    }
                }

                var lastWrite = File.GetLastWriteTimeUtc(file);
                var age = FormatAge(lastWrite);
                var daysOld = (DateTime.UtcNow - lastWrite).TotalDays;

                Memories.Add(new MemoryListItem
                {
                    Filename = Path.GetFileName(file),
                    FullPath = file,
                    Type = type,
                    Content = content,
                    Age = age,
                    TypeColor = GetTypeColor(type),
                    AgeBrush = daysOld > 30 ? new SolidColorBrush(ColorHelper.FromArgb(255, 255, 100, 100))
                             : daysOld > 14 ? new SolidColorBrush(ColorHelper.FromArgb(255, 255, 200, 100))
                             : new SolidColorBrush(ColorHelper.FromArgb(255, 100, 200, 100))
                });
            }
            catch { /* skip unreadable */ }
        }
        MemoryList.ItemsSource = Memories;
    }

    private void MemoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MemoryList.SelectedItem is MemoryListItem item)
        {
            EditorText.Text = item.Content;
            EditorBorder.Visibility = Visibility.Visible;
        }
    }

    private static string FormatAge(DateTime timestamp)
    {
        var diff = DateTime.UtcNow - timestamp;
        if (diff.TotalHours < 1) return "just now";
        if (diff.TotalDays < 1) return $"{(int)diff.TotalHours}h ago";
        if (diff.TotalDays < 30) return $"{(int)diff.TotalDays}d ago";
        return $"{(int)(diff.TotalDays / 30)}mo ago";
    }

    private static SolidColorBrush GetTypeColor(string type) => type switch
    {
        "user" => new SolidColorBrush(ColorHelper.FromArgb(255, 80, 140, 200)),
        "feedback" => new SolidColorBrush(ColorHelper.FromArgb(255, 200, 140, 80)),
        "project" => new SolidColorBrush(ColorHelper.FromArgb(255, 100, 180, 100)),
        "reference" => new SolidColorBrush(ColorHelper.FromArgb(255, 160, 100, 180)),
        _ => new SolidColorBrush(ColorHelper.FromArgb(255, 120, 120, 120))
    };
}
