using System.Collections.Generic;
using System.IO;
using System.Linq;
using Hermes.Agent.Updates;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HermesDesktop.Tests.Updates;

/// <summary>
/// Bundle E.4 tests for <see cref="WingetManifestGenerator"/>.
///
/// Validates:
/// <list type="bullet">
///   <item>Every <c>{{TOKEN}}</c> is substituted (no residual placeholders).</item>
///   <item>Missing required keys throw <c>ArgumentException</c> rather than emit broken YAML.</item>
///   <item>The output directory layout matches Winget's sharded
///   <c>manifests/&lt;letter&gt;/&lt;publisher&gt;/&lt;package&gt;/&lt;version&gt;/</c> convention.</item>
///   <item>SHA-256 of a known input matches the expected hex digest.</item>
/// </list>
/// </summary>
[TestClass]
public class WingetManifestGeneratorTests
{
    private static IReadOnlyDictionary<string, string> AllValues() => new Dictionary<string, string>
    {
        ["PACKAGE_IDENTIFIER"]     = "VyreVaultStudios.HermesDesktop",
        ["PACKAGE_NAME"]           = "Hermes Desktop",
        ["PUBLISHER"]              = "VyreVault Studios",
        ["PUBLISHER_URL"]          = "https://github.com/RedWoodOG/Hermes-Desktop",
        ["LICENSE"]                = "MIT",
        ["LICENSE_URL"]            = "https://github.com/RedWoodOG/Hermes-Desktop/blob/main/LICENSE",
        ["MONIKER"]                = "hermes",
        ["SHORT_DESCRIPTION"]      = "Local-first agent shell for Windows.",
        ["PORTABLE_COMMAND_ALIAS"] = "hermes-desktop",
        ["VERSION"]                = "2.5.4",
        ["INSTALLER_URL"]          = "https://example.com/HermesDesktop-portable-x64.zip",
        ["INSTALLER_SHA256"]       = "DEADBEEF" + new string('0', 56),
        ["RELEASE_DATE"]           = "2026-05-13",
        ["RELEASE_NOTES_URL"]      = "https://example.com/notes",
    };

    // ── Render ──

    [TestMethod]
    public void Render_KnownTokens_ReplacesAllPlaceholders()
    {
        const string tpl =
            "PackageIdentifier: {{PACKAGE_IDENTIFIER}}\n" +
            "PackageVersion: {{VERSION}}\n" +
            "InstallerSha256: {{INSTALLER_SHA256}}\n";

        var rendered = WingetManifestGenerator.Render(tpl, AllValues());

        StringAssert.Contains(rendered, "PackageIdentifier: VyreVaultStudios.HermesDesktop");
        StringAssert.Contains(rendered, "PackageVersion: 2.5.4");
        Assert.IsFalse(rendered.Contains("{{"), "Rendered output must not contain unsubstituted tokens.");
    }

    [TestMethod]
    [ExpectedException(typeof(System.ArgumentException))]
    public void Render_MissingRequiredKey_Throws()
    {
        var values = AllValues().ToDictionary(kv => kv.Key, kv => kv.Value);
        values.Remove("VERSION");

        WingetManifestGenerator.Render("PackageVersion: {{VERSION}}", values);
    }

    [TestMethod]
    [ExpectedException(typeof(System.ArgumentException))]
    public void Render_EmptyRequiredKey_Throws()
    {
        var values = AllValues().ToDictionary(kv => kv.Key, kv => kv.Value);
        values["VERSION"] = "";

        WingetManifestGenerator.Render("PackageVersion: {{VERSION}}", values);
    }

    [TestMethod]
    [ExpectedException(typeof(System.ArgumentException))]
    public void Render_UnknownTokenInTemplate_Throws()
    {
        WingetManifestGenerator.Render("PackageVersion: {{UNKNOWN_TOKEN}}", AllValues());
    }

    // ── GetRelativeManifestPath ──

    [TestMethod]
    public void GetRelativeManifestPath_KnownIdentifier_ReturnsCanonicalLayout()
    {
        var path = WingetManifestGenerator.GetRelativeManifestPath("VyreVaultStudios.HermesDesktop", "2.5.4");
        Assert.AreEqual("manifests/v/VyreVaultStudios/HermesDesktop/2.5.4", path);
    }

    [TestMethod]
    public void GetRelativeManifestPath_SinglePublisherChar_ShardsByLetter()
    {
        var path = WingetManifestGenerator.GetRelativeManifestPath("AcmeCorp.Tool", "1.0.0");
        Assert.AreEqual("manifests/a/AcmeCorp/Tool/1.0.0", path);
    }

    [TestMethod]
    [ExpectedException(typeof(System.ArgumentException))]
    public void GetRelativeManifestPath_NoDot_Throws()
    {
        WingetManifestGenerator.GetRelativeManifestPath("SinglePart", "1.0.0");
    }

    // ── ComputeFileSha256 ──

    [TestMethod]
    public void ComputeFileSha256_KnownInput_ReturnsKnownDigest()
    {
        var temp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(temp, "hello"); // SHA-256("hello") = 2cf24dba...
            var hash = WingetManifestGenerator.ComputeFileSha256(temp);
            Assert.AreEqual("2CF24DBA5FB0A30E26E83B2AC5B9E29E1B161E5C1FA7425E73043362938B9824", hash);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    // ── GenerateAll (end-to-end against real templates) ──

    [TestMethod]
    public void GenerateAll_WithAllTemplates_WritesThreeFilesInExpectedLayout()
    {
        var installerTpl =
            "PackageIdentifier: {{PACKAGE_IDENTIFIER}}\n" +
            "PackageVersion: {{VERSION}}\n" +
            "InstallerType: zip\n" +
            "NestedInstallerType: portable\n" +
            "ReleaseDate: {{RELEASE_DATE}}\n" +
            "NestedInstallerFiles:\n" +
            "  - RelativeFilePath: HermesDesktop.exe\n" +
            "    PortableCommandAlias: {{PORTABLE_COMMAND_ALIAS}}\n" +
            "Installers:\n" +
            "  - Architecture: x64\n" +
            "    InstallerUrl: {{INSTALLER_URL}}\n" +
            "    InstallerSha256: {{INSTALLER_SHA256}}\n" +
            "ManifestType: installer\n" +
            "ManifestVersion: 1.6.0\n";

        var localeTpl =
            "PackageIdentifier: {{PACKAGE_IDENTIFIER}}\n" +
            "PackageVersion: {{VERSION}}\n" +
            "PackageLocale: en-US\n" +
            "Publisher: {{PUBLISHER}}\n" +
            "PublisherUrl: {{PUBLISHER_URL}}\n" +
            "PackageName: {{PACKAGE_NAME}}\n" +
            "License: {{LICENSE}}\n" +
            "LicenseUrl: {{LICENSE_URL}}\n" +
            "ShortDescription: {{SHORT_DESCRIPTION}}\n" +
            "Moniker: {{MONIKER}}\n" +
            "ReleaseNotesUrl: {{RELEASE_NOTES_URL}}\n" +
            "ManifestType: defaultLocale\n" +
            "ManifestVersion: 1.6.0\n";

        var versionTpl =
            "PackageIdentifier: {{PACKAGE_IDENTIFIER}}\n" +
            "PackageVersion: {{VERSION}}\n" +
            "DefaultLocale: en-US\n" +
            "ManifestType: version\n" +
            "ManifestVersion: 1.6.0\n";

        var outputRoot = Path.Combine(Path.GetTempPath(), $"winget-test-{System.Guid.NewGuid():N}");
        try
        {
            var paths = WingetManifestGenerator.GenerateAll(
                installerTemplate: installerTpl,
                localeTemplate: localeTpl,
                versionTemplate: versionTpl,
                values: AllValues(),
                outputRoot: outputRoot);

            Assert.AreEqual(3, paths.Count);

            foreach (var p in paths)
            {
                Assert.IsTrue(File.Exists(p), $"Expected file {p} was not written.");
                var content = File.ReadAllText(p);
                Assert.IsFalse(content.Contains("{{"), $"File {p} contains unsubstituted tokens.");
            }

            // Verify the directory layout: manifests/v/VyreVaultStudios/HermesDesktop/2.5.4
            var expected = Path.Combine(
                outputRoot,
                "manifests", "v", "VyreVaultStudios", "HermesDesktop", "2.5.4");
            Assert.IsTrue(Directory.Exists(expected), $"Expected layout dir {expected} not found.");

            // File names must follow Winget's naming convention.
            var files = Directory.GetFiles(expected).Select(Path.GetFileName).OrderBy(n => n).ToArray();
            CollectionAssert.AreEqual(
                new[]
                {
                    "VyreVaultStudios.HermesDesktop.installer.yaml",
                    "VyreVaultStudios.HermesDesktop.locale.en-US.yaml",
                    "VyreVaultStudios.HermesDesktop.yaml",
                },
                files);
        }
        finally
        {
            if (Directory.Exists(outputRoot))
                Directory.Delete(outputRoot, recursive: true);
        }
    }

    [TestMethod]
    public void RequiredKeys_EveryEntry_IsNonEmpty()
    {
        Assert.IsTrue(WingetManifestGenerator.RequiredKeys.Count > 0);
        foreach (var key in WingetManifestGenerator.RequiredKeys)
            Assert.IsFalse(string.IsNullOrWhiteSpace(key));
    }
}
