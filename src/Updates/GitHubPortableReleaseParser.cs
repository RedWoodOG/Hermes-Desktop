namespace Hermes.Agent.Updates;

using System;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

/// <summary>
/// Parses GitHub <c>GET /repos/{owner}/{repo}/releases/latest</c> JSON and validates portable-update artifacts.
/// </summary>
public static class GitHubPortableReleaseParser
{
    public const string DefaultOwner = "RedWoodOG";
    public const string DefaultRepo = "Hermes-Desktop";
    public const string PortableZipAssetName = "HermesDesktop-portable-x64.zip";
    public const string PortableSha256AssetName = PortableZipAssetName + ".sha256";

    /// <summary>HTTPS API base — host/path are fixed; do not concatenate user-controlled segments into requests.</summary>
    public static Uri LatestReleaseApiUri(string owner, string repo) =>
        new($"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/releases/latest", UriKind.Absolute);

    /// <summary>
    /// Extracts a <see cref="Version"/> from a release tag like <c>v2.5.4</c> or <c>2.5.4</c>.
    /// Prerelease labels after a hyphen are ignored for comparison (e.g. <c>v2.5.4-beta1</c> → 2.5.4).
    /// </summary>
    public static bool TryParseVersionFromTag(string? tagName, out Version? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(tagName))
            return false;

        string trimmed = tagName.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
            trimmed = trimmed[1..];

        int dash = trimmed.IndexOf('-');
        if (dash >= 0)
            trimmed = trimmed[..dash];

        trimmed = trimmed.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return false;

        // Pad to at least major.minor for Version.Parse (e.g. "2" -> invalid)
        var parts = trimmed.Split('.');
        if (parts.Length == 1)
            trimmed += ".0";
        else if (parts.Length == 2)
            trimmed += ".0";

        return Version.TryParse(trimmed, out version);
    }

    /// <summary>
    /// Reads informational / assembly version for the desktop app (e.g. <c>2.5.4+abc</c> → <c>2.5.4</c>).
    /// </summary>
    public static bool TryParseDesktopAssemblyVersion(Assembly assembly, out Version? version)
    {
        version = null;
        string? info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        string core = string.IsNullOrWhiteSpace(info)
            ? assembly.GetName().Version?.ToString() ?? "0.0"
            : info.Split('+')[0].Trim();

        int dash = core.IndexOf('-');
        if (dash >= 0)
            core = core[..dash].Trim();

        if (string.IsNullOrEmpty(core))
            core = assembly.GetName().Version?.ToString() ?? "0.0";

        return TryParseVersionFromTag(core, out version);
    }

    /// <summary>
    /// Parses GitHub latest-release JSON. Returns <c>null</c> if required fields or the portable zip asset are missing.
    /// </summary>
    public static PortableReleaseOffer? TryParseLatestReleaseJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("tag_name", out var tagEl))
                return null;
            string? tagName = tagEl.GetString();
            if (!TryParseVersionFromTag(tagName, out var version) || version is null)
                return null;

            if (!root.TryGetProperty("html_url", out var htmlEl))
                return null;
            string? htmlUrl = htmlEl.GetString();
            if (string.IsNullOrWhiteSpace(htmlUrl) || !Uri.TryCreate(htmlUrl, UriKind.Absolute, out var releasePage))
                return null;

            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                return null;

            Uri? zipUri = null;
            Uri? shaUri = null;
            foreach (var asset in assets.EnumerateArray())
            {
                if (!asset.TryGetProperty("name", out var nameEl) ||
                    !asset.TryGetProperty("browser_download_url", out var urlEl))
                    continue;

                string? name = nameEl.GetString();
                string? url = urlEl.GetString();
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
                    continue;

                if (!Uri.TryCreate(url, UriKind.Absolute, out var absolute))
                    continue;

                if (string.Equals(name, PortableZipAssetName, StringComparison.Ordinal))
                    zipUri = absolute;
                else if (string.Equals(name, PortableSha256AssetName, StringComparison.Ordinal))
                    shaUri = absolute;
            }

            if (zipUri is null || tagName is null)
                return null;

            return new PortableReleaseOffer(tagName, version, releasePage, zipUri, shaUri);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parses a shasum-style line: <c>{64 hex}</c> optional <c>  filename</c>. Hex is case-insensitive.
    /// </summary>
    public static bool TryParseSha256SumFile(string text, out ReadOnlyMemory<byte> digest32)
    {
        digest32 = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        ReadOnlySpan<char> span = text.AsSpan().Trim();
        int space = span.IndexOf(' ');
        ReadOnlySpan<char> hexPart = space >= 0 ? span[..space].Trim() : span;

        if (hexPart.Length != 64)
            return false;

        var bytes = new byte[32];
        for (int i = 0; i < 32; i++)
        {
            int hi = ParseHexNibble(hexPart[2 * i]);
            int lo = ParseHexNibble(hexPart[(2 * i) + 1]);
            if (hi < 0 || lo < 0)
                return false;
            bytes[i] = (byte)((hi << 4) | lo);
        }

        digest32 = bytes;
        return true;
    }

    private static int ParseHexNibble(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    /// <summary>Computes SHA-256 of a stream from its current position to the end.</summary>
    public static async Task<byte[]> ComputeSha256Async(Stream stream, CancellationToken cancellationToken)
    {
        using var sha = SHA256.Create();
        return await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Constant-time comparison of 32-byte digests.</summary>
    public static bool FixedTimeEquals(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) =>
        a.Length == 32 && b.Length == 32 && CryptographicOperations.FixedTimeEquals(a, b);

    /// <summary>Compares release <paramref name="latest"/> to <paramref name="current"/>.</summary>
    public static bool IsNewerThan(Version latest, Version current)
    {
        ArgumentNullException.ThrowIfNull(latest);
        ArgumentNullException.ThrowIfNull(current);
        return NormalizeVersion(latest).CompareTo(NormalizeVersion(current)) > 0;
    }

    private static Version NormalizeVersion(Version v) =>
        new(v.Major, v.Minor, v.Build < 0 ? 0 : v.Build, v.Revision < 0 ? 0 : v.Revision);
}
