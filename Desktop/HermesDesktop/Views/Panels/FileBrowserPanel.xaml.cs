using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using HermesDesktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HermesDesktop.Views.Panels;

public sealed class FileTreeItem
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public string Icon { get; set; } = "\uE8A5"; // Document icon
    public bool IsDirectory { get; set; }
    public ObservableCollection<FileTreeItem> Children { get; } = new();
}

public sealed partial class FileBrowserPanel : UserControl
{
    private string _rootPath;

    public FileBrowserPanel()
    {
        InitializeComponent();
        _rootPath = HermesEnvironment.AgentWorkingDirectory;
        FileTree.Expanding += FileTree_Expanding;
        Loaded += (_, _) => LoadDirectory(_rootPath);
    }

    public void LoadDirectory(string path)
    {
        _rootPath = path;
        BreadcrumbText.Text = path;
        FileTree.RootNodes.Clear();

        try
        {
            foreach (var dir in Directory.GetDirectories(path).OrderBy(d => Path.GetFileName(d)).Take(50))
            {
                FileTree.RootNodes.Add(new TreeViewNode
                {
                    Content = new FileTreeItem
                    {
                        Name = Path.GetFileName(dir), FullPath = dir, IsDirectory = true, Icon = "\uE8B7"
                    },
                    HasUnrealizedChildren = true
                });
            }

            foreach (var file in Directory.GetFiles(path).OrderBy(f => Path.GetFileName(f)).Take(100))
            {
                FileTree.RootNodes.Add(new TreeViewNode
                {
                    Content = new FileTreeItem
                    {
                        Name = Path.GetFileName(file), FullPath = file, IsDirectory = false,
                        Icon = GetFileIcon(Path.GetExtension(file))
                    }
                });
            }
        }
        catch (Exception ex)
        {
            BreadcrumbText.Text = $"Error: {ex.Message}";
        }
    }

    private void FileTree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (args.Node.Content is not FileTreeItem item || !item.IsDirectory) return;
        if (args.Node.Children.Count > 0) return;

        try
        {
            foreach (var dir in Directory.GetDirectories(item.FullPath).OrderBy(d => Path.GetFileName(d)).Take(50))
            {
                args.Node.Children.Add(new TreeViewNode
                {
                    Content = new FileTreeItem
                    {
                        Name = Path.GetFileName(dir), FullPath = dir, IsDirectory = true, Icon = "\uE8B7"
                    },
                    HasUnrealizedChildren = true
                });
            }

            foreach (var file in Directory.GetFiles(item.FullPath).OrderBy(f => Path.GetFileName(f)).Take(100))
            {
                args.Node.Children.Add(new TreeViewNode
                {
                    Content = new FileTreeItem
                    {
                        Name = Path.GetFileName(file), FullPath = file, IsDirectory = false,
                        Icon = GetFileIcon(Path.GetExtension(file))
                    }
                });
            }
        }
        catch { /* permission denied etc */ }
    }

    private void FileTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is not TreeViewNode node || node.Content is not FileTreeItem item) return;
        if (item.IsDirectory) return;

        try
        {
            var content = File.ReadAllText(item.FullPath);
            if (content.Length > 10000) content = content[..10000] + "\n\n[...truncated]";
            PreviewText.Text = content;
            PreviewBorder.Visibility = Visibility.Visible;
            BreadcrumbText.Text = item.FullPath;
        }
        catch (Exception ex)
        {
            PreviewText.Text = $"Cannot preview: {ex.Message}";
            PreviewBorder.Visibility = Visibility.Visible;
        }
    }

    private static string GetFileIcon(string ext) => ext.ToLowerInvariant() switch
    {
        ".cs" => "\uE943",     // Code
        ".xaml" => "\uE943",
        ".json" => "\uE943",
        ".yaml" or ".yml" => "\uE943",
        ".md" => "\uE8A5",    // Document
        ".png" or ".jpg" or ".gif" => "\uEB9F", // Image
        _ => "\uE8A5"
    };
}
