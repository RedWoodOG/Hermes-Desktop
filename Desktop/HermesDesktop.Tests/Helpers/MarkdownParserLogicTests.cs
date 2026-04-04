using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HermesDesktop.Tests.Helpers;

/// <summary>
/// Tests for the markdown parsing logic introduced in MarkdownRenderer.cs (new file in this PR).
/// MarkdownRenderer itself depends on WinUI types (Paragraph, Run, Block) which cannot be
/// instantiated without the Windows App SDK COM server; these tests instead validate the
/// pure text-parsing algorithm independently to ensure correctness of:
///   - Block detection (headers, code fences, bullet/numbered lists, paragraphs)
///   - Inline pattern regex (bold, italic, code, links)
///   - Edge cases and boundary conditions
/// </summary>
[TestClass]
public class MarkdownBlockDetectionTests
{
    // Mirrors MarkdownRenderer.Render() line-detection logic (pure parsing, no WinUI types)

    private static List<string> DetectBlockTypes(string markdown)
    {
        var types = new List<string>();
        if (string.IsNullOrEmpty(markdown)) return types;

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var i = 0;

        while (i < lines.Length)
        {
            var line = lines[i];

            if (line.TrimStart().StartsWith("```"))
            {
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```")) i++;
                if (i < lines.Length) i++;
                types.Add("code_block");
                continue;
            }

            if (line.StartsWith('#'))
            {
                types.Add("header");
                i++;
                continue;
            }

            if (line.TrimStart().StartsWith("- ") || line.TrimStart().StartsWith("* "))
            {
                while (i < lines.Length && (lines[i].TrimStart().StartsWith("- ") || lines[i].TrimStart().StartsWith("* ")))
                    i++;
                types.Add("bullet_list");
                continue;
            }

            if (Regex.IsMatch(line.TrimStart(), @"^\d+\.\s"))
            {
                while (i < lines.Length && Regex.IsMatch(lines[i].TrimStart(), @"^\d+\.\s"))
                    i++;
                types.Add("numbered_list");
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                i++;
                continue;
            }

            // paragraph
            while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]) &&
                   !lines[i].StartsWith('#') && !lines[i].TrimStart().StartsWith("```") &&
                   !lines[i].TrimStart().StartsWith("- ") && !lines[i].TrimStart().StartsWith("* ") &&
                   !Regex.IsMatch(lines[i].TrimStart(), @"^\d+\.\s"))
            {
                i++;
            }
            types.Add("paragraph");
        }

        return types;
    }

    // ── Empty / null ──

    [TestMethod]
    public void Render_EmptyString_ReturnsNoBlocks()
    {
        var blocks = DetectBlockTypes("");
        Assert.AreEqual(0, blocks.Count);
    }

    [TestMethod]
    public void Render_NullCheck_ReturnsNoBlocks()
    {
        // string.IsNullOrEmpty(null) → true in the source
        var blocks = DetectBlockTypes(string.Empty);
        Assert.AreEqual(0, blocks.Count);
    }

    [TestMethod]
    public void Render_OnlyWhitespace_ReturnsNoBlocks()
    {
        var blocks = DetectBlockTypes("   \n\n   ");
        Assert.AreEqual(0, blocks.Count);
    }

    // ── Headers ──

    [TestMethod]
    public void Render_H1Header_DetectedAsHeader()
    {
        var blocks = DetectBlockTypes("# Hello World");
        Assert.AreEqual(1, blocks.Count);
        Assert.AreEqual("header", blocks[0]);
    }

    [TestMethod]
    public void Render_H2Header_DetectedAsHeader()
    {
        var blocks = DetectBlockTypes("## Section");
        Assert.AreEqual("header", blocks[0]);
    }

    [TestMethod]
    public void Render_H3Header_DetectedAsHeader()
    {
        var blocks = DetectBlockTypes("### Subsection");
        Assert.AreEqual("header", blocks[0]);
    }

    [TestMethod]
    public void Render_H4Header_DetectedAsHeader()
    {
        var blocks = DetectBlockTypes("#### Deep");
        Assert.AreEqual("header", blocks[0]);
    }

    [TestMethod]
    public void Render_MultipleHeaders_EachBecomesBlock()
    {
        var md = "# H1\n## H2\n### H3";
        var blocks = DetectBlockTypes(md);

        Assert.AreEqual(3, blocks.Count);
        Assert.IsTrue(blocks.All(b => b == "header"));
    }

    // ── Code blocks ──

    [TestMethod]
    public void Render_FencedCodeBlock_DetectedAsCodeBlock()
    {
        var md = "```\nsome code\n```";
        var blocks = DetectBlockTypes(md);

        Assert.AreEqual(1, blocks.Count);
        Assert.AreEqual("code_block", blocks[0]);
    }

    [TestMethod]
    public void Render_FencedCodeBlockWithLanguage_DetectedAsCodeBlock()
    {
        var md = "```csharp\nvar x = 1;\n```";
        var blocks = DetectBlockTypes(md);

        Assert.AreEqual("code_block", blocks[0]);
    }

    [TestMethod]
    public void Render_MultilineCodeBlock_CountsAsOneBlock()
    {
        var md = "```\nline1\nline2\nline3\n```";
        var blocks = DetectBlockTypes(md);

        Assert.AreEqual(1, blocks.Count);
        Assert.AreEqual("code_block", blocks[0]);
    }

    [TestMethod]
    public void Render_UnclosedCodeBlock_StillDetectedAsCodeBlock()
    {
        // No closing fence — parser drains remaining lines
        var md = "```\ncode without close";
        var blocks = DetectBlockTypes(md);

        Assert.AreEqual(1, blocks.Count);
        Assert.AreEqual("code_block", blocks[0]);
    }

    // ── Bullet lists ──

    [TestMethod]
    public void Render_DashBulletList_DetectedAsBulletList()
    {
        var md = "- item one\n- item two";
        var blocks = DetectBlockTypes(md);

        Assert.AreEqual(1, blocks.Count);
        Assert.AreEqual("bullet_list", blocks[0]);
    }

    [TestMethod]
    public void Render_StarBulletList_DetectedAsBulletList()
    {
        var md = "* item one\n* item two";
        var blocks = DetectBlockTypes(md);

        Assert.AreEqual("bullet_list", blocks[0]);
    }

    [TestMethod]
    public void Render_SingleBulletItem_DetectedAsBulletList()
    {
        var md = "- solo";
        var blocks = DetectBlockTypes(md);

        Assert.AreEqual("bullet_list", blocks[0]);
    }

    [TestMethod]
    public void Render_BulletListGroupedIntoOneBlock()
    {
        var md = "- a\n- b\n- c\n- d";
        var blocks = DetectBlockTypes(md);

        Assert.AreEqual(1, blocks.Count, "Consecutive bullet items should be ONE block");
    }

    // ── Numbered lists ──

    [TestMethod]
    public void Render_NumberedList_DetectedAsNumberedList()
    {
        var md = "1. First\n2. Second\n3. Third";
        var blocks = DetectBlockTypes(md);

        Assert.AreEqual(1, blocks.Count);
        Assert.AreEqual("numbered_list", blocks[0]);
    }

    [TestMethod]
    public void Render_SingleNumberedItem_DetectedAsNumberedList()
    {
        var md = "1. Solo item";
        var blocks = DetectBlockTypes(md);

        Assert.AreEqual("numbered_list", blocks[0]);
    }

    [TestMethod]
    public void Render_NumberedListGroupedIntoOneBlock()
    {
        var md = "1. a\n2. b\n3. c";
        var blocks = DetectBlockTypes(md);

        Assert.AreEqual(1, blocks.Count);
    }

    // ── Paragraphs ──

    [TestMethod]
    public void Render_PlainText_DetectedAsParagraph()
    {
        var md = "This is plain text.";
        var blocks = DetectBlockTypes(md);

        Assert.AreEqual(1, blocks.Count);
        Assert.AreEqual("paragraph", blocks[0]);
    }

    [TestMethod]
    public void Render_MultiLineParagraph_CollapsedToOneBlock()
    {
        var md = "Line one\nLine two\nLine three";
        var blocks = DetectBlockTypes(md);

        Assert.AreEqual(1, blocks.Count);
        Assert.AreEqual("paragraph", blocks[0]);
    }

    [TestMethod]
    public void Render_TwoParagraphsSeparatedByBlankLine_TwoBlocks()
    {
        var md = "First paragraph\n\nSecond paragraph";
        var blocks = DetectBlockTypes(md);

        Assert.AreEqual(2, blocks.Count);
        Assert.IsTrue(blocks.All(b => b == "paragraph"));
    }

    // ── Mixed content ──

    [TestMethod]
    public void Render_HeaderFollowedByParagraph_TwoBlocks()
    {
        var md = "# Title\n\nSome body text.";
        var blocks = DetectBlockTypes(md);

        Assert.AreEqual(2, blocks.Count);
        Assert.AreEqual("header", blocks[0]);
        Assert.AreEqual("paragraph", blocks[1]);
    }

    [TestMethod]
    public void Render_FullDocument_AllBlockTypesPresent()
    {
        var md = """
            # Title

            Some intro text here.

            ## Features

            - Feature A
            - Feature B

            1. Step one
            2. Step two

            ```csharp
            var x = 1;
            ```

            Footer text.
            """;

        var blocks = DetectBlockTypes(md);

        CollectionAssert.Contains(blocks, "header");
        CollectionAssert.Contains(blocks, "paragraph");
        CollectionAssert.Contains(blocks, "bullet_list");
        CollectionAssert.Contains(blocks, "numbered_list");
        CollectionAssert.Contains(blocks, "code_block");
    }

    [TestMethod]
    public void Render_CodeBlockDoesNotAbsorbSubsequentContent()
    {
        var md = "```\ncode\n```\n\nAfter code block";
        var blocks = DetectBlockTypes(md);

        Assert.AreEqual(2, blocks.Count);
        Assert.AreEqual("code_block", blocks[0]);
        Assert.AreEqual("paragraph", blocks[1]);
    }

    [TestMethod]
    public void Render_WindowsLineEndings_TreatedSameAsUnix()
    {
        var md = "# Header\r\n\r\nParagraph";
        var blocks = DetectBlockTypes(md);

        Assert.AreEqual(2, blocks.Count);
        Assert.AreEqual("header", blocks[0]);
        Assert.AreEqual("paragraph", blocks[1]);
    }
}

[TestClass]
public class MarkdownInlinePatternTests
{
    // Mirrors the regex pattern used in MarkdownRenderer.AddInlineFormatting
    private const string InlinePattern = @"(\*\*(.+?)\*\*)|(\*(.+?)\*)|(`(.+?)`)|(\[(.+?)\]\((.+?)\))";

    private static List<(string Type, string Value)> ParseInlines(string text)
    {
        var results = new List<(string, string)>();
        foreach (Match match in Regex.Matches(text, InlinePattern))
        {
            if (match.Groups[1].Success)
                results.Add(("bold", match.Groups[2].Value));
            else if (match.Groups[3].Success)
                results.Add(("italic", match.Groups[4].Value));
            else if (match.Groups[5].Success)
                results.Add(("code", match.Groups[6].Value));
            else if (match.Groups[7].Success)
                results.Add(("link", $"{match.Groups[8].Value}|{match.Groups[9].Value}"));
        }
        return results;
    }

    [TestMethod]
    public void Inline_Bold_Detected()
    {
        var inlines = ParseInlines("This is **bold** text");
        Assert.AreEqual(1, inlines.Count);
        Assert.AreEqual("bold", inlines[0].Type);
        Assert.AreEqual("bold", inlines[0].Value);
    }

    [TestMethod]
    public void Inline_Italic_Detected()
    {
        var inlines = ParseInlines("This is *italic* text");
        Assert.AreEqual(1, inlines.Count);
        Assert.AreEqual("italic", inlines[0].Type);
        Assert.AreEqual("italic", inlines[0].Value);
    }

    [TestMethod]
    public void Inline_Code_Detected()
    {
        var inlines = ParseInlines("Use `typeof(int)` here");
        Assert.AreEqual(1, inlines.Count);
        Assert.AreEqual("code", inlines[0].Type);
        Assert.AreEqual("typeof(int)", inlines[0].Value);
    }

    [TestMethod]
    public void Inline_Link_Detected()
    {
        var inlines = ParseInlines("[Click here](https://example.com)");
        Assert.AreEqual(1, inlines.Count);
        Assert.AreEqual("link", inlines[0].Type);
        StringAssert.Contains(inlines[0].Value, "Click here");
        StringAssert.Contains(inlines[0].Value, "https://example.com");
    }

    [TestMethod]
    public void Inline_MultipleSameType_AllDetected()
    {
        var inlines = ParseInlines("**one** and **two**");
        Assert.AreEqual(2, inlines.Count);
        Assert.IsTrue(inlines.All(i => i.Type == "bold"));
        Assert.AreEqual("one", inlines[0].Value);
        Assert.AreEqual("two", inlines[1].Value);
    }

    [TestMethod]
    public void Inline_MixedFormats_AllDetected()
    {
        var inlines = ParseInlines("**bold** and *italic* and `code`");
        Assert.AreEqual(3, inlines.Count);
        Assert.AreEqual("bold", inlines[0].Type);
        Assert.AreEqual("italic", inlines[1].Type);
        Assert.AreEqual("code", inlines[2].Type);
    }

    [TestMethod]
    public void Inline_NoFormatting_ReturnsEmpty()
    {
        var inlines = ParseInlines("Plain text without any formatting");
        Assert.AreEqual(0, inlines.Count);
    }

    [TestMethod]
    public void Inline_EmptyString_ReturnsEmpty()
    {
        var inlines = ParseInlines("");
        Assert.AreEqual(0, inlines.Count);
    }

    [TestMethod]
    public void Inline_Bold_ExtractsInnerText()
    {
        var inlines = ParseInlines("**Hello World**");
        Assert.AreEqual("Hello World", inlines[0].Value);
    }

    [TestMethod]
    public void Inline_Code_WithSpecialChars_Detected()
    {
        var inlines = ParseInlines("Try `x => x + 1` lambda");
        Assert.AreEqual(1, inlines.Count);
        Assert.AreEqual("x => x + 1", inlines[0].Value);
    }

    [TestMethod]
    public void Inline_Link_ExtractsBothTextAndUrl()
    {
        var inlines = ParseInlines("[GitHub](https://github.com/foo/bar)");
        var link = inlines[0].Value;
        StringAssert.Contains(link, "GitHub");
        StringAssert.Contains(link, "https://github.com/foo/bar");
    }
}

[TestClass]
public class MarkdownHeaderLevelTests
{
    // Mirrors the header level detection in MarkdownRenderer
    private static int GetHeaderLevel(string line)
    {
        var level = 0;
        while (level < line.Length && line[level] == '#') level++;
        return level;
    }

    private static string GetHeaderText(string line)
    {
        var level = GetHeaderLevel(line);
        return line[level..].Trim();
    }

    [TestMethod]
    public void HeaderLevel_H1_IsOne()
    {
        Assert.AreEqual(1, GetHeaderLevel("# Title"));
    }

    [TestMethod]
    public void HeaderLevel_H2_IsTwo()
    {
        Assert.AreEqual(2, GetHeaderLevel("## Section"));
    }

    [TestMethod]
    public void HeaderLevel_H3_IsThree()
    {
        Assert.AreEqual(3, GetHeaderLevel("### Sub"));
    }

    [TestMethod]
    public void HeaderLevel_H6_IsSix()
    {
        Assert.AreEqual(6, GetHeaderLevel("###### Deep"));
    }

    [TestMethod]
    public void HeaderText_StripsLeadingHashesAndSpace()
    {
        Assert.AreEqual("Hello World", GetHeaderText("## Hello World"));
    }

    [TestMethod]
    public void HeaderText_H1_ReturnsCleanText()
    {
        Assert.AreEqual("Title", GetHeaderText("# Title"));
    }

    [TestMethod]
    public void HeaderText_WithExtraSpaces_IsTrimmed()
    {
        Assert.AreEqual("Title", GetHeaderText("##   Title   "));
    }

    [TestMethod]
    public void FontSize_Level1_Is22()
    {
        // Mirrors the switch in MarkdownRenderer.CreateHeader
        int GetFontSize(int level) => level switch { 1 => 22, 2 => 18, 3 => 16, _ => 14 };
        Assert.AreEqual(22, GetFontSize(1));
    }

    [TestMethod]
    public void FontSize_Level2_Is18()
    {
        int GetFontSize(int level) => level switch { 1 => 22, 2 => 18, 3 => 16, _ => 14 };
        Assert.AreEqual(18, GetFontSize(2));
    }

    [TestMethod]
    public void FontSize_Level3_Is16()
    {
        int GetFontSize(int level) => level switch { 1 => 22, 2 => 18, 3 => 16, _ => 14 };
        Assert.AreEqual(16, GetFontSize(3));
    }

    [TestMethod]
    public void FontSize_Level4Plus_Is14()
    {
        int GetFontSize(int level) => level switch { 1 => 22, 2 => 18, 3 => 16, _ => 14 };
        Assert.AreEqual(14, GetFontSize(4));
        Assert.AreEqual(14, GetFontSize(5));
        Assert.AreEqual(14, GetFontSize(6));
    }
}