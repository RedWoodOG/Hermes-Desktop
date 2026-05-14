namespace Hermes.Agent.Updates;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

/// <summary>
/// Pure, testable Winget manifest generator. Reads templates from <c>build/winget/</c>,
/// substitutes <c>{{TOKEN}}</c> placeholders, validates that all required values are
/// present, and writes the trio (<c>Version.yaml</c>, <c>Locale.en-US.yaml</c>,
/// <c>Installer.yaml</c>) to the canonical <c>manifests/v/VyreVaultStudios/HermesDesktop/&lt;version&gt;/</c> path.
///
/// Wrapped by <c>scripts/Generate-WingetManifests.ps1</c> in CI; called directly by tests.
/// </summary>
public static class WingetManifestGenerator
{
    /// <summary>
    /// All required substitution keys. <c>Render</c> throws if any are missing from the
    /// passed dictionary so a forgotten value can never silently ship as <c>{{TOKEN}}</c>.
    /// </summary>
    public static readonly IReadOnlyList<string> RequiredKeys = new[]
    {
        "PACKAGE_IDENTIFIER",
        "PACKAGE_NAME",
        "PUBLISHER",
        "PUBLISHER_URL",
        "LICENSE",
        "LICENSE_URL",
        "MONIKER",
        "SHORT_DESCRIPTION",
        "PORTABLE_COMMAND_ALIAS",
        "VERSION",
        "INSTALLER_URL",
        "INSTALLER_SHA256",
        "RELEASE_DATE",
        "RELEASE_NOTES_URL",
    };

    private static readonly Regex TokenPattern = new(@"\{\{([A-Z0-9_]+)\}\}", RegexOptions.Compiled);

    /// <summary>
    /// Render a single template, substituting every <c>{{KEY}}</c> with <c>values[KEY]</c>.
    /// Throws <see cref="ArgumentException"/> if any required key from <see cref="RequiredKeys"/>
    /// is missing OR if the template references a key not in <paramref name="values"/>.
    /// </summary>
    public static string Render(string template, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(values);

        foreach (var key in RequiredKeys)
        {
            if (!values.ContainsKey(key) || string.IsNullOrWhiteSpace(values[key]))
                throw new ArgumentException($"Missing required template value for {{{key}}}.", nameof(values));
        }

        var unmatched = new List<string>();
        var rendered = TokenPattern.Replace(template, match =>
        {
            var key = match.Groups[1].Value;
            if (!values.TryGetValue(key, out var value))
            {
                unmatched.Add(key);
                return match.Value;
            }
            return value;
        });

        if (unmatched.Count > 0)
            throw new ArgumentException(
                "Template referenced unknown tokens: " + string.Join(", ", unmatched),
                nameof(template));

        // Sanity check: no {{...}} left in output (shouldn't happen, but guards against
        // future regex regressions).
        if (TokenPattern.IsMatch(rendered))
            throw new InvalidOperationException("Rendered manifest still contains unsubstituted tokens.");

        return rendered;
    }

    /// <summary>
    /// Computes the <c>microsoft/winget-pkgs</c> directory layout for a given package and version.
    /// For <c>VyreVaultStudios.HermesDesktop</c>+<c>2.5.4</c> this returns
    /// <c>manifests/v/VyreVaultStudios/HermesDesktop/2.5.4</c>.
    /// </summary>
    public static string GetRelativeManifestPath(string packageIdentifier, string version)
    {
        if (string.IsNullOrWhiteSpace(packageIdentifier))
            throw new ArgumentException("packageIdentifier must be non-empty.", nameof(packageIdentifier));
        if (string.IsNullOrWhiteSpace(version))
            throw new ArgumentException("version must be non-empty.", nameof(version));

        var parts = packageIdentifier.Split('.');
        if (parts.Length < 2)
            throw new ArgumentException(
                "packageIdentifier must contain at least one '.' (e.g. Publisher.Package).",
                nameof(packageIdentifier));

        // Winget shards by first letter of the publisher segment, lowercased.
        var firstLetter = char.ToLowerInvariant(parts[0][0]);
        var segments = new List<string> { "manifests", firstLetter.ToString() };
        segments.AddRange(parts);
        segments.Add(version);
        return string.Join('/', segments);
    }

    /// <summary>
    /// Compute the SHA-256 hex digest of a file. Used to populate <c>InstallerSha256</c>
    /// when no precomputed sidecar is available.
    /// </summary>
    public static string ComputeFileSha256(string filePath)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(filePath);
        var hash = sha.ComputeHash(fs);
        return Convert.ToHexString(hash).ToUpperInvariant();
    }

    /// <summary>
    /// Render all three manifests and write them to
    /// <paramref name="outputRoot"/>/<c>GetRelativeManifestPath</c>.
    /// </summary>
    /// <returns>Absolute paths of the three files in this fixed order:
    /// Version, Locale, Installer.</returns>
    public static IReadOnlyList<string> GenerateAll(
        string installerTemplate,
        string localeTemplate,
        string versionTemplate,
        IReadOnlyDictionary<string, string> values,
        string outputRoot)
    {
        if (string.IsNullOrWhiteSpace(outputRoot))
            throw new ArgumentException("outputRoot must be non-empty.", nameof(outputRoot));

        var packageId = values["PACKAGE_IDENTIFIER"];
        var version = values["VERSION"];

        var relPath = GetRelativeManifestPath(packageId, version);
        var dir = Path.Combine(outputRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(dir);

        var versionPath = Path.Combine(dir, $"{packageId}.yaml");
        var localePath = Path.Combine(dir, $"{packageId}.locale.en-US.yaml");
        var installerPath = Path.Combine(dir, $"{packageId}.installer.yaml");

        File.WriteAllText(versionPath, Render(versionTemplate, values));
        File.WriteAllText(localePath, Render(localeTemplate, values));
        File.WriteAllText(installerPath, Render(installerTemplate, values));

        return new[] { versionPath, localePath, installerPath };
    }

    /// <summary>
    /// Today's UTC date in <c>yyyy-MM-dd</c> form, formatted for the <c>ReleaseDate</c> field.
    /// Exposed as a static helper so tests can swap the clock without touching DateTime directly.
    /// </summary>
    public static string GetUtcReleaseDate() =>
        DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
