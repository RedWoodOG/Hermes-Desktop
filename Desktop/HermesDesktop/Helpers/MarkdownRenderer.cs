using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace HermesDesktop.Helpers;

/// <summary>
/// Converts markdown text into WinUI RichTextBlock Blocks.
/// Supports: headers, bold, italic, code spans, code blocks, bullet lists, links.
/// </summary>
public static class MarkdownRenderer
{
    private static readonly SolidColorBrush CodeBackground = new(ColorHelper.FromArgb(255, 17, 22, 28));
    private static readonly SolidColorBrush CodeForeground = new(ColorHelper.FromArgb(255, 226, 139, 82));
    private static readonly SolidColorBrush LinkForeground = new(ColorHelper.FromArgb(255, 100, 180, 255));
    private static readonly SolidColorBrush HeaderForeground = new(ColorHelper.FromArgb(255, 232, 238, 247));
    private static readonly SolidColorBrush TextForeground = new(ColorHelper.FromArgb(255, 200, 210, 220));
    private static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas, Courier New");

    /// <summary>Render markdown string into a list of Blocks for a RichTextBlock.</summary>
    public static List<Block> Render(string markdown)
    {
        var blocks = new List<Block>();
        if (string.IsNullOrEmpty(markdown)) return blocks;

        var lines = markdown.Split('\n');
        var i = 0;

        while (i < lines.Length)
        {
            var line = lines[i];

            // Code block (``` fenced)
            if (line.TrimStart().StartsWith("```"))
            {
                var lang = line.TrimStart().Length > 3 ? line.TrimStart()[3..].Trim() : "";
                var codeLines = new List<string>();
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```"))
                {
                    codeLines.Add(lines[i]);
                    i++;
                }
                if (i < lines.Length) i++; // skip closing ```

                blocks.Add(CreateCodeBlock(string.Join("\n", codeLines), lang));
                continue;
            }

            // Header (# ## ### etc.)
            if (line.StartsWith('#'))
            {
                var level = 0;
                while (level < line.Length && line[level] == '#') level++;
                var text = line[level..].Trim();
                blocks.Add(CreateHeader(text, level));
                i++;
                continue;
            }

            // Bullet list (- or *)
            if (line.TrimStart().StartsWith("- ") || line.TrimStart().StartsWith("* "))
            {
                var listItems = new List<string>();
                while (i < lines.Length && (lines[i].TrimStart().StartsWith("- ") || lines[i].TrimStart().StartsWith("* ")))
                {
                    listItems.Add(lines[i].TrimStart()[2..]);
                    i++;
                }
                blocks.Add(CreateBulletList(listItems));
                continue;
            }

            // Numbered list (1. 2. etc.)
            if (Regex.IsMatch(line.TrimStart(), @"^\d+\.\s"))
            {
                var listItems = new List<string>();
                while (i < lines.Length && Regex.IsMatch(lines[i].TrimStart(), @"^\d+\.\s"))
                {
                    listItems.Add(Regex.Replace(lines[i].TrimStart(), @"^\d+\.\s*", ""));
                    i++;
                }
                blocks.Add(CreateNumberedList(listItems));
                continue;
            }

            // Empty line
            if (string.IsNullOrWhiteSpace(line))
            {
                i++;
                continue;
            }

            // Regular paragraph (may contain inline formatting)
            var paraLines = new List<string>();
            while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]) &&
                   !lines[i].StartsWith('#') && !lines[i].TrimStart().StartsWith("```") &&
                   !lines[i].TrimStart().StartsWith("- ") && !lines[i].TrimStart().StartsWith("* "))
            {
                paraLines.Add(lines[i]);
                i++;
            }
            blocks.Add(CreateParagraph(string.Join(" ", paraLines)));
        }

        return blocks;
    }

    private static Paragraph CreateHeader(string text, int level)
    {
        var para = new Paragraph { Margin = new Thickness(0, 8, 0, 4) };
        var run = new Run
        {
            Text = text,
            FontWeight = FontWeights.SemiBold,
            Foreground = HeaderForeground,
            FontSize = level switch
            {
                1 => 22,
                2 => 18,
                3 => 16,
                _ => 14
            }
        };
        para.Inlines.Add(run);
        return para;
    }

    private static Paragraph CreateParagraph(string text)
    {
        var para = new Paragraph { Margin = new Thickness(0, 2, 0, 2) };
        AddInlineFormatting(para.Inlines, text);
        return para;
    }

    private static Paragraph CreateCodeBlock(string code, string language)
    {
        var para = new Paragraph
        {
            Margin = new Thickness(0, 6, 0, 6),
            FontFamily = MonoFont,
            FontSize = 13
        };

        if (!string.IsNullOrEmpty(language))
        {
            para.Inlines.Add(new Run
            {
                Text = language + "\n",
                FontSize = 11,
                Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 149, 162, 177))
            });
        }

        para.Inlines.Add(new Run
        {
            Text = code,
            Foreground = CodeForeground
        });

        return para;
    }

    private static Paragraph CreateBulletList(List<string> items)
    {
        var para = new Paragraph { Margin = new Thickness(12, 2, 0, 2) };
        for (var i = 0; i < items.Count; i++)
        {
            if (i > 0) para.Inlines.Add(new LineBreak());
            para.Inlines.Add(new Run { Text = "  •  ", Foreground = CodeForeground });
            AddInlineFormatting(para.Inlines, items[i]);
        }
        return para;
    }

    private static Paragraph CreateNumberedList(List<string> items)
    {
        var para = new Paragraph { Margin = new Thickness(12, 2, 0, 2) };
        for (var i = 0; i < items.Count; i++)
        {
            if (i > 0) para.Inlines.Add(new LineBreak());
            para.Inlines.Add(new Run { Text = $"  {i + 1}.  ", Foreground = CodeForeground });
            AddInlineFormatting(para.Inlines, items[i]);
        }
        return para;
    }

    /// <summary>Parse inline markdown: **bold**, *italic*, `code`, [links](url)</summary>
    private static void AddInlineFormatting(InlineCollection inlines, string text)
    {
        // Pattern: **bold**, *italic*, `code`, [text](url)
        var pattern = @"(\*\*(.+?)\*\*)|(\*(.+?)\*)|(`(.+?)`)|(\[(.+?)\]\((.+?)\))";
        var lastIndex = 0;

        foreach (Match match in Regex.Matches(text, pattern))
        {
            // Add text before this match
            if (match.Index > lastIndex)
            {
                inlines.Add(new Run
                {
                    Text = text[lastIndex..match.Index],
                    Foreground = TextForeground
                });
            }

            if (match.Groups[1].Success) // **bold**
            {
                inlines.Add(new Run
                {
                    Text = match.Groups[2].Value,
                    FontWeight = FontWeights.Bold,
                    Foreground = HeaderForeground
                });
            }
            else if (match.Groups[3].Success) // *italic*
            {
                inlines.Add(new Run
                {
                    Text = match.Groups[4].Value,
                    FontStyle = Windows.UI.Text.FontStyle.Italic,
                    Foreground = TextForeground
                });
            }
            else if (match.Groups[5].Success) // `code`
            {
                inlines.Add(new Run
                {
                    Text = match.Groups[6].Value,
                    FontFamily = MonoFont,
                    Foreground = CodeForeground
                });
            }
            else if (match.Groups[7].Success) // [text](url)
            {
                var hyperlink = new Hyperlink { NavigateUri = new Uri(match.Groups[9].Value, UriKind.RelativeOrAbsolute) };
                hyperlink.Inlines.Add(new Run
                {
                    Text = match.Groups[8].Value,
                    Foreground = LinkForeground
                });
                inlines.Add(hyperlink);
            }

            lastIndex = match.Index + match.Length;
        }

        // Remaining text
        if (lastIndex < text.Length)
        {
            inlines.Add(new Run
            {
                Text = text[lastIndex..],
                Foreground = TextForeground
            });
        }
    }
}
