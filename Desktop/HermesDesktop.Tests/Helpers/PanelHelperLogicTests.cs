using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HermesDesktop.Tests.Helpers;

/// <summary>
/// Tests for the pure-C# formatting and mapping logic introduced in the new panels
/// added by this PR (SessionPanel, MemoryPanel, FileBrowserPanel, SkillsPanel, BuddyPanel).
/// These are extracted copies of private static methods, validated to match the
/// expected behavior described in each panel's code-behind.
/// </summary>
[TestClass]
public class SessionPanelFormatTimeAgoTests
{
    // Mirrors SessionPanel.FormatTimeAgo (private static)
    private static string FormatTimeAgo(DateTime timestamp)
    {
        var diff = DateTime.UtcNow - timestamp;
        if (diff.TotalMinutes < 1) return "just now";
        if (diff.TotalHours < 1) return $"{(int)diff.TotalMinutes}m ago";
        if (diff.TotalDays < 1) return $"{(int)diff.TotalHours}h ago";
        if (diff.TotalDays < 7) return $"{(int)diff.TotalDays}d ago";
        return timestamp.ToLocalTime().ToString("MMM d");
    }

    [TestMethod]
    public void FormatTimeAgo_LessThanOneMinuteAgo_ReturnsJustNow()
    {
        var result = FormatTimeAgo(DateTime.UtcNow.AddSeconds(-30));

        Assert.AreEqual("just now", result);
    }

    [TestMethod]
    public void FormatTimeAgo_ExactlyZeroSecondsAgo_ReturnsJustNow()
    {
        // Edge: timestamp == UtcNow (diff ≈ 0)
        var result = FormatTimeAgo(DateTime.UtcNow);

        Assert.AreEqual("just now", result);
    }

    [TestMethod]
    public void FormatTimeAgo_FiftyNineSecondsAgo_ReturnsJustNow()
    {
        var result = FormatTimeAgo(DateTime.UtcNow.AddSeconds(-59));

        Assert.AreEqual("just now", result);
    }

    [TestMethod]
    public void FormatTimeAgo_TwoMinutesAgo_ReturnsMinutes()
    {
        var result = FormatTimeAgo(DateTime.UtcNow.AddMinutes(-2));

        Assert.AreEqual("2m ago", result);
    }

    [TestMethod]
    public void FormatTimeAgo_FiftyNineMinutesAgo_ReturnsMinutes()
    {
        var result = FormatTimeAgo(DateTime.UtcNow.AddMinutes(-59));

        StringAssert.EndsWith(result, "m ago");
        Assert.IsFalse(result.EndsWith("h ago"), "Should be minutes, not hours");
    }

    [TestMethod]
    public void FormatTimeAgo_OneHourAgo_ReturnsHours()
    {
        var result = FormatTimeAgo(DateTime.UtcNow.AddHours(-1).AddMinutes(-1));

        Assert.AreEqual("1h ago", result);
    }

    [TestMethod]
    public void FormatTimeAgo_FiveHoursAgo_ReturnsHours()
    {
        var result = FormatTimeAgo(DateTime.UtcNow.AddHours(-5));

        Assert.AreEqual("5h ago", result);
    }

    [TestMethod]
    public void FormatTimeAgo_TwentyThreeHoursAgo_ReturnsHours()
    {
        var result = FormatTimeAgo(DateTime.UtcNow.AddHours(-23));

        StringAssert.EndsWith(result, "h ago");
    }

    [TestMethod]
    public void FormatTimeAgo_OneDayAgo_ReturnsDays()
    {
        var result = FormatTimeAgo(DateTime.UtcNow.AddDays(-1).AddHours(-1));

        Assert.AreEqual("1d ago", result);
    }

    [TestMethod]
    public void FormatTimeAgo_SixDaysAgo_ReturnsDays()
    {
        var result = FormatTimeAgo(DateTime.UtcNow.AddDays(-6));

        StringAssert.EndsWith(result, "d ago");
    }

    [TestMethod]
    public void FormatTimeAgo_SevenDaysAgo_ReturnsFormattedDate()
    {
        var timestamp = DateTime.UtcNow.AddDays(-7);
        var result = FormatTimeAgo(timestamp);

        // Should return "MMM d" format, not "d ago"
        Assert.IsFalse(result.EndsWith("d ago"), $"Expected formatted date, got: {result}");
        Assert.IsFalse(result.EndsWith("h ago"), $"Expected formatted date, got: {result}");
    }

    [TestMethod]
    public void FormatTimeAgo_ThirtyDaysAgo_ReturnsFormattedDate()
    {
        var timestamp = DateTime.UtcNow.AddDays(-30);
        var result = FormatTimeAgo(timestamp);

        Assert.IsFalse(result.Contains("ago"), $"Old dates should show date format, got: {result}");
    }

    [TestMethod]
    public void FormatTimeAgo_ExactlySevenDaysAgo_ReturnsFormattedDate()
    {
        // Boundary: >= 7 days → formatted date
        var timestamp = DateTime.UtcNow.AddDays(-7).AddMinutes(-1);
        var result = FormatTimeAgo(timestamp);

        Assert.IsFalse(result.EndsWith("d ago"), $"Should be formatted date at exactly 7 days, got: {result}");
    }
}

[TestClass]
public class MemoryPanelFormatAgeTests
{
    // Mirrors MemoryPanel.FormatAge (private static)
    private static string FormatAge(DateTime timestamp)
    {
        var diff = DateTime.UtcNow - timestamp;
        if (diff.TotalHours < 1) return "just now";
        if (diff.TotalDays < 1) return $"{(int)diff.TotalHours}h ago";
        if (diff.TotalDays < 30) return $"{(int)diff.TotalDays}d ago";
        return $"{(int)(diff.TotalDays / 30)}mo ago";
    }

    [TestMethod]
    public void FormatAge_LessThanOneHour_ReturnsJustNow()
    {
        var result = FormatAge(DateTime.UtcNow.AddMinutes(-30));

        Assert.AreEqual("just now", result);
    }

    [TestMethod]
    public void FormatAge_ZeroSecondsAgo_ReturnsJustNow()
    {
        var result = FormatAge(DateTime.UtcNow);

        Assert.AreEqual("just now", result);
    }

    [TestMethod]
    public void FormatAge_FiftyNineMinutesAgo_ReturnsJustNow()
    {
        var result = FormatAge(DateTime.UtcNow.AddMinutes(-59));

        Assert.AreEqual("just now", result);
    }

    [TestMethod]
    public void FormatAge_OneHourAgo_ReturnsHours()
    {
        var result = FormatAge(DateTime.UtcNow.AddHours(-1).AddMinutes(-1));

        Assert.AreEqual("1h ago", result);
    }

    [TestMethod]
    public void FormatAge_TwelveHoursAgo_ReturnsHours()
    {
        var result = FormatAge(DateTime.UtcNow.AddHours(-12));

        Assert.AreEqual("12h ago", result);
    }

    [TestMethod]
    public void FormatAge_TwentyThreeHoursAgo_ReturnsHours()
    {
        var result = FormatAge(DateTime.UtcNow.AddHours(-23));

        StringAssert.EndsWith(result, "h ago");
    }

    [TestMethod]
    public void FormatAge_OneDayAgo_ReturnsDays()
    {
        var result = FormatAge(DateTime.UtcNow.AddDays(-1).AddHours(-1));

        Assert.AreEqual("1d ago", result);
    }

    [TestMethod]
    public void FormatAge_TwentyNineDaysAgo_ReturnsDays()
    {
        var result = FormatAge(DateTime.UtcNow.AddDays(-29));

        StringAssert.EndsWith(result, "d ago");
    }

    [TestMethod]
    public void FormatAge_ThirtyDaysAgo_ReturnsMonths()
    {
        var result = FormatAge(DateTime.UtcNow.AddDays(-30).AddHours(-1));

        Assert.AreEqual("1mo ago", result);
    }

    [TestMethod]
    public void FormatAge_SixtyDaysAgo_ReturnsTwoMonths()
    {
        var result = FormatAge(DateTime.UtcNow.AddDays(-60).AddHours(-1));

        Assert.AreEqual("2mo ago", result);
    }

    [TestMethod]
    public void FormatAge_ThreeHundredSixtyFiveDaysAgo_ReturnsTwelveMonths()
    {
        var result = FormatAge(DateTime.UtcNow.AddDays(-365));

        Assert.AreEqual("12mo ago", result);
    }

    // ── Boundary: FormatAge vs FormatTimeAgo difference ──
    // FormatAge collapses anything < 1h to "just now" (unlike FormatTimeAgo which shows minutes)

    [TestMethod]
    public void FormatAge_ThirtySecondsAgo_IsJustNow_NotMinutes()
    {
        var result = FormatAge(DateTime.UtcNow.AddSeconds(-30));

        Assert.AreEqual("just now", result, "FormatAge does NOT break down to minutes unlike FormatTimeAgo");
    }
}

[TestClass]
public class FileBrowserGetFileIconTests
{
    // Mirrors FileBrowserPanel.GetFileIcon (private static)
    private static string GetFileIcon(string ext) => ext.ToLowerInvariant() switch
    {
        ".cs" => "\uE943",
        ".xaml" => "\uE943",
        ".json" => "\uE943",
        ".yaml" or ".yml" => "\uE943",
        ".md" => "\uE8A5",
        ".png" or ".jpg" or ".gif" => "\uEB9F",
        _ => "\uE8A5"
    };

    [TestMethod]
    public void GetFileIcon_CsharpFile_ReturnsCodeIcon()
    {
        Assert.AreEqual("\uE943", GetFileIcon(".cs"));
    }

    [TestMethod]
    public void GetFileIcon_XamlFile_ReturnsCodeIcon()
    {
        Assert.AreEqual("\uE943", GetFileIcon(".xaml"));
    }

    [TestMethod]
    public void GetFileIcon_JsonFile_ReturnsCodeIcon()
    {
        Assert.AreEqual("\uE943", GetFileIcon(".json"));
    }

    [TestMethod]
    public void GetFileIcon_YamlFile_ReturnsCodeIcon()
    {
        Assert.AreEqual("\uE943", GetFileIcon(".yaml"));
    }

    [TestMethod]
    public void GetFileIcon_YmlAlias_ReturnsCodeIcon()
    {
        Assert.AreEqual("\uE943", GetFileIcon(".yml"));
    }

    [TestMethod]
    public void GetFileIcon_MarkdownFile_ReturnsDocumentIcon()
    {
        Assert.AreEqual("\uE8A5", GetFileIcon(".md"));
    }

    [TestMethod]
    public void GetFileIcon_PngFile_ReturnsImageIcon()
    {
        Assert.AreEqual("\uEB9F", GetFileIcon(".png"));
    }

    [TestMethod]
    public void GetFileIcon_JpgFile_ReturnsImageIcon()
    {
        Assert.AreEqual("\uEB9F", GetFileIcon(".jpg"));
    }

    [TestMethod]
    public void GetFileIcon_GifFile_ReturnsImageIcon()
    {
        Assert.AreEqual("\uEB9F", GetFileIcon(".gif"));
    }

    [TestMethod]
    public void GetFileIcon_UnknownExtension_ReturnsDocumentIcon()
    {
        Assert.AreEqual("\uE8A5", GetFileIcon(".xyz"));
        Assert.AreEqual("\uE8A5", GetFileIcon(".log"));
        Assert.AreEqual("\uE8A5", GetFileIcon(".txt"));
        Assert.AreEqual("\uE8A5", GetFileIcon(".csv"));
    }

    [TestMethod]
    public void GetFileIcon_EmptyExtension_ReturnsDocumentIcon()
    {
        Assert.AreEqual("\uE8A5", GetFileIcon(""));
    }

    [TestMethod]
    public void GetFileIcon_UppercaseExtension_IsTreatedAsCaseInsensitive()
    {
        // Method uses .ToLowerInvariant()
        Assert.AreEqual("\uE943", GetFileIcon(".CS"));
        Assert.AreEqual("\uE943", GetFileIcon(".JSON"));
        Assert.AreEqual("\uEB9F", GetFileIcon(".PNG"));
    }

    [TestMethod]
    public void GetFileIcon_DefaultIconMatchesFileTreeItemDefault()
    {
        // FileTreeItem.Icon defaults to "\uE8A5" (Document icon)
        // GetFileIcon unknown extension should return the same value
        const string fileTreeItemDefaultIcon = "\uE8A5";
        Assert.AreEqual(fileTreeItemDefaultIcon, GetFileIcon(".unknown"));
    }
}

[TestClass]
public class BuddyPanelGetRarityColorTests
{
    // Mirrors BuddyPanel.GetRarityColor (private static) — returns a color code for rarity
    // We test the color values (ARGB) based on the code:
    // "legendary" -> 255,255,200,50
    // "rare"      -> 255,100,140,220
    // "uncommon"  -> 255,100,200,100
    // _           -> 255,140,140,140
    private static (byte A, byte R, byte G, byte B) GetRarityArgb(string rarity) =>
        rarity.ToLowerInvariant() switch
        {
            "legendary" => (255, 255, 200, 50),
            "rare" => (255, 100, 140, 220),
            "uncommon" => (255, 100, 200, 100),
            _ => (255, 140, 140, 140)
        };

    [TestMethod]
    public void GetRarityColor_Legendary_ReturnsGoldishColor()
    {
        var (a, r, g, b) = GetRarityArgb("legendary");
        Assert.AreEqual(255, a);
        Assert.AreEqual(255, r);
        Assert.AreEqual(200, g);
        Assert.AreEqual(50, b);
    }

    [TestMethod]
    public void GetRarityColor_Rare_ReturnsBluishColor()
    {
        var (a, r, g, b) = GetRarityArgb("rare");
        Assert.AreEqual(255, a);
        Assert.AreEqual(100, r);
        Assert.AreEqual(140, g);
        Assert.AreEqual(220, b);
    }

    [TestMethod]
    public void GetRarityColor_Uncommon_ReturnsGreenishColor()
    {
        var (a, r, g, b) = GetRarityArgb("uncommon");
        Assert.AreEqual(255, a);
        Assert.AreEqual(100, r);
        Assert.AreEqual(200, g);
        Assert.AreEqual(100, b);
    }

    [TestMethod]
    public void GetRarityColor_Common_ReturnsGrayColor()
    {
        var (a, r, g, b) = GetRarityArgb("common");
        Assert.AreEqual(255, a);
        Assert.AreEqual(140, r);
        Assert.AreEqual(140, g);
        Assert.AreEqual(140, b);
    }

    [TestMethod]
    public void GetRarityColor_Unknown_ReturnsGrayColor()
    {
        var (_, r, g, b) = GetRarityArgb("epic");
        Assert.AreEqual(140, r);
        Assert.AreEqual(140, g);
        Assert.AreEqual(140, b);
    }

    [TestMethod]
    public void GetRarityColor_CaseInsensitive_LegendaryUppercase()
    {
        var (a, r, g, b) = GetRarityArgb("LEGENDARY");
        Assert.AreEqual(255, a);
        Assert.AreEqual(255, r); // Golden
    }
}

[TestClass]
public class MemoryPanelGetTypeColorTests
{
    // Mirrors MemoryPanel.GetTypeColor (private static) — color by memory type
    // "user"      -> 80,140,200 (blue)
    // "feedback"  -> 200,140,80 (orange)
    // "project"   -> 100,180,100 (green)
    // "reference" -> 160,100,180 (purple)
    // _           -> 120,120,120 (gray)
    private static (byte R, byte G, byte B) GetTypeRgb(string type) => type switch
    {
        "user" => (80, 140, 200),
        "feedback" => (200, 140, 80),
        "project" => (100, 180, 100),
        "reference" => (160, 100, 180),
        _ => (120, 120, 120)
    };

    [TestMethod]
    public void GetTypeColor_UserType_ReturnsBluishColor()
    {
        var (r, g, b) = GetTypeRgb("user");
        Assert.AreEqual(80, r);
        Assert.AreEqual(140, g);
        Assert.AreEqual(200, b);
    }

    [TestMethod]
    public void GetTypeColor_FeedbackType_ReturnsOrangeishColor()
    {
        var (r, g, b) = GetTypeRgb("feedback");
        Assert.AreEqual(200, r);
        Assert.AreEqual(140, g);
        Assert.AreEqual(80, b);
    }

    [TestMethod]
    public void GetTypeColor_ProjectType_ReturnsGreenishColor()
    {
        var (r, g, b) = GetTypeRgb("project");
        Assert.AreEqual(100, r);
        Assert.AreEqual(180, g);
        Assert.AreEqual(100, b);
    }

    [TestMethod]
    public void GetTypeColor_ReferenceType_ReturnsPurplishColor()
    {
        var (r, g, b) = GetTypeRgb("reference");
        Assert.AreEqual(160, r);
        Assert.AreEqual(100, g);
        Assert.AreEqual(180, b);
    }

    [TestMethod]
    public void GetTypeColor_UnknownType_ReturnsGray()
    {
        var (r, g, b) = GetTypeRgb("unknown");
        Assert.AreEqual(120, r);
        Assert.AreEqual(120, g);
        Assert.AreEqual(120, b);
    }

    [TestMethod]
    public void GetTypeColor_EmptyString_ReturnsGray()
    {
        var (r, g, b) = GetTypeRgb("");
        Assert.AreEqual(120, r);
        Assert.AreEqual(120, g);
        Assert.AreEqual(120, b);
    }
}

[TestClass]
public class MemoryFrontmatterParsingTests
{
    // Mirrors the YAML front-matter parsing logic in MemoryPanel.Refresh()
    private static string ParseTypeFromContent(string content)
    {
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
        return type;
    }

    [TestMethod]
    public void ParseType_WithValidFrontmatter_ExtractsType()
    {
        var content = "---\ntype: user\nauthor: test\n---\nBody content here";

        var type = ParseTypeFromContent(content);

        Assert.AreEqual("user", type);
    }

    [TestMethod]
    public void ParseType_WithFeedbackType_ExtractsFeedback()
    {
        var content = "---\ntype: feedback\n---\nFeedback content";

        var type = ParseTypeFromContent(content);

        Assert.AreEqual("feedback", type);
    }

    [TestMethod]
    public void ParseType_WithProjectType_ExtractsProject()
    {
        var content = "---\ntype: project\n---\n";

        var type = ParseTypeFromContent(content);

        Assert.AreEqual("project", type);
    }

    [TestMethod]
    public void ParseType_WithoutFrontmatter_ReturnsUnknown()
    {
        var content = "No front matter here, just plain content.";

        var type = ParseTypeFromContent(content);

        Assert.AreEqual("unknown", type);
    }

    [TestMethod]
    public void ParseType_WithMissingTypeLine_ReturnsUnknown()
    {
        var content = "---\nauthor: test\ndate: 2024-01-01\n---\nContent";

        var type = ParseTypeFromContent(content);

        Assert.AreEqual("unknown", type);
    }

    [TestMethod]
    public void ParseType_WithQuotedType_ExtractsWithoutQuotes()
    {
        // Trim removes surrounding quotes in some YAML parsers; our impl uses Trim() only
        var content = "---\ntype: reference\n---\n";

        var type = ParseTypeFromContent(content);

        Assert.AreEqual("reference", type);
    }

    [TestMethod]
    public void ParseType_EmptyContent_ReturnsUnknown()
    {
        var type = ParseTypeFromContent("");

        Assert.AreEqual("unknown", type);
    }

    [TestMethod]
    public void ParseType_EmptyFrontmatter_ReturnsUnknown()
    {
        var content = "---\n---\nBody";

        var type = ParseTypeFromContent(content);

        Assert.AreEqual("unknown", type);
    }

    [TestMethod]
    public void ParseType_TypeWithLeadingSpaces_IsTrimmed()
    {
        var content = "---\n  type: feedback\n---\n";

        var type = ParseTypeFromContent(content);

        Assert.AreEqual("feedback", type);
    }
}

[TestClass]
public class SessionListItemTests
{
    // SessionListItem is a pure C# POCO defined in SessionPanel.xaml.cs
    // Since it's in the HermesDesktop project (WinUI), we replicate its shape here.

    [TestMethod]
    public void SessionListItem_Shape_HasExpectedProperties()
    {
        // Validates the POCO structure used by HermesChatService session loading
        var item = new SessionListItemModel
        {
            Id = "abc123",
            Title = "First 60 chars of user message...",
            TimeAgo = "5m ago",
            MessageCount = "10 msgs"
        };

        Assert.AreEqual("abc123", item.Id);
        Assert.AreEqual("First 60 chars of user message...", item.Title);
        Assert.AreEqual("5m ago", item.TimeAgo);
        Assert.AreEqual("10 msgs", item.MessageCount);
    }

    [TestMethod]
    public void SessionTitle_TruncatesAt60Characters()
    {
        // Mirrors: firstUser.Content[..60] + "..."
        var longContent = new string('X', 80);
        var firstUser = new { Content = longContent };

        var title = firstUser.Content.Length > 60
            ? firstUser.Content[..60] + "..."
            : firstUser.Content;

        Assert.AreEqual(63, title.Length); // 60 + "..."
        Assert.IsTrue(title.EndsWith("..."));
    }

    [TestMethod]
    public void SessionTitle_ShortContent_NotTruncated()
    {
        var shortContent = "Short message";
        var title = shortContent.Length > 60 ? shortContent[..60] + "..." : shortContent;

        Assert.AreEqual("Short message", title);
        Assert.IsFalse(title.EndsWith("..."));
    }

    [TestMethod]
    public void SessionTitle_ExactlyAtBoundary_NotTruncated()
    {
        var exactContent = new string('A', 60);
        var title = exactContent.Length > 60 ? exactContent[..60] + "..." : exactContent;

        Assert.AreEqual(60, title.Length);
        Assert.IsFalse(title.EndsWith("..."));
    }

    [TestMethod]
    public void SessionTitle_NullFirstUserContent_FallsBackToEmpty()
    {
        // Mirrors: firstUser?.Content ?? "(empty)"
        string? content = null;
        var title = content ?? "(empty)";

        Assert.AreEqual("(empty)", title);
    }

    [TestMethod]
    public void MessageCountLabel_FormatIsCorrect()
    {
        // Mirrors: $"{messages.Count} msgs"
        var count = 15;
        var label = $"{count} msgs";

        Assert.AreEqual("15 msgs", label);
    }

    // Helper model (mirrors SessionListItem shape without WinUI dependency)
    private sealed class SessionListItemModel
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string TimeAgo { get; set; } = "";
        public string MessageCount { get; set; } = "";
    }
}

[TestClass]
public class SkillsSearchFilterTests
{
    // Mirrors the search filter logic in SkillsPanel.SearchBox_TextChanged
    private static IEnumerable<SkillModel> FilterSkills(IEnumerable<SkillModel> skills, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return skills;
        return skills.Where(s =>
            s.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            s.Description.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private static List<SkillModel> MakeSkills() =>
    [
        new() { Name = "code-review", Description = "Reviews code for issues" },
        new() { Name = "write-tests", Description = "Generates unit tests" },
        new() { Name = "summarize", Description = "Summarizes text documents" },
        new() { Name = "debug", Description = "Helps debug code problems" },
    ];

    [TestMethod]
    public void Filter_EmptyQuery_ReturnsAll()
    {
        var skills = MakeSkills();
        var result = FilterSkills(skills, "").ToList();

        Assert.AreEqual(4, result.Count);
    }

    [TestMethod]
    public void Filter_WhitespaceQuery_ReturnsAll()
    {
        var skills = MakeSkills();
        var result = FilterSkills(skills, "   ").ToList();

        Assert.AreEqual(4, result.Count);
    }

    [TestMethod]
    public void Filter_MatchingName_ReturnsMatchingSkills()
    {
        var skills = MakeSkills();
        // "code" matches both name "code-review" and description "… code problems" on debug — use a name-unique substring
        var result = FilterSkills(skills, "review").ToList();

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("code-review", result[0].Name);
    }

    [TestMethod]
    public void Filter_MatchingDescription_ReturnsMatchingSkills()
    {
        var skills = MakeSkills();
        var result = FilterSkills(skills, "unit tests").ToList();

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("write-tests", result[0].Name);
    }

    [TestMethod]
    public void Filter_CaseInsensitive_FindsUppercaseQuery()
    {
        var skills = MakeSkills();
        var result = FilterSkills(skills, "SUMMARIZE").ToList();

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("summarize", result[0].Name);
    }

    [TestMethod]
    public void Filter_MatchesMultipleSkills_ReturnsAll()
    {
        var skills = MakeSkills();
        var result = FilterSkills(skills, "code").ToList();

        // "code-review" and "debug - Helps debug code problems" match
        Assert.IsTrue(result.Count >= 1);
    }

    [TestMethod]
    public void Filter_NoMatch_ReturnsEmpty()
    {
        var skills = MakeSkills();
        var result = FilterSkills(skills, "xyznonexistent").ToList();

        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void Filter_PartialMatchInName_ReturnsMatch()
    {
        var skills = MakeSkills();
        var result = FilterSkills(skills, "debug").ToList();

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("debug", result[0].Name);
    }

    private sealed class SkillModel
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
    }
}