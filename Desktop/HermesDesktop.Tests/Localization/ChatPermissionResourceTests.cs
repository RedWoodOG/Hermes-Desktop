using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HermesDesktop.Tests.Localization;

internal sealed class SolutionRootNotFoundException : Exception
{
    public SolutionRootNotFoundException(string message)
        : base(message)
    {
    }
}

[TestClass]
public sealed class ChatPermissionResourceTests
{
    [TestMethod]
    public void ChatPagePermissionResources_AllReferencedKeysExist()
    {
        var root = FindRepoRoot();
        var chatPagePath = Path.Combine(root, "Desktop", "HermesDesktop", "Views", "ChatPage.xaml.cs");
        var referencedKeys = Regex.Matches(
                File.ReadAllText(chatPagePath),
                "ResourceLoader\\.GetString\\(\"(ChatPermission[^\"]+)\"\\)")
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        foreach (var culture in new[] { "en-us", "zh-cn" })
        {
            var resourcePath = Path.Combine(root, "Desktop", "HermesDesktop", "Strings", culture, "Resources.resw");
            var resourceKeys = XDocument.Load(resourcePath)
                .Descendants("data")
                .Select(element => element.Attribute("name")?.Value)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.Ordinal);

            var missing = referencedKeys
                .Where(key => !resourceKeys.Contains(key))
                .ToArray();

            CollectionAssert.AreEqual(
                Array.Empty<string>(),
                missing,
                $"Missing ChatPage permission resource keys in {culture}: {string.Join(", ", missing)}");
        }
    }

    private static string FindRepoRoot()
    {
        var directory = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(directory))
        {
            var marker = Path.Combine(directory, "HermesDesktop.sln");
            if (File.Exists(marker))
            {
                return directory;
            }

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new SolutionRootNotFoundException("Could not find HermesDesktop.sln from test output directory.");
    }
}
