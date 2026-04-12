namespace Hermes.Agent.Dreamer;

using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

/// <summary>Lightweight RSS fetch - writes markdown summaries into inbox-rss/.</summary>
public sealed class RssFetcher
{
    private readonly HttpClient _http;
    private readonly DreamerRoom _room;
    private readonly ILogger<RssFetcher> _logger;
    private DateTime _lastRunUtc = DateTime.MinValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="RssFetcher"/> class with the HTTP client, target room, and logger dependencies.
    /// </summary>
    /// <param name="http">The <see cref="HttpClient"/> used to download feed content.</param>
    /// <param name="room">The <see cref="DreamerRoom"/> that provides the inbox RSS directory for output files.</param>
    /// <param name="logger">The logger used to record warnings for individual feed failures.</param>
    public RssFetcher(HttpClient http, DreamerRoom room, ILogger<RssFetcher> logger)
    {
        _http = http;
        _room = room;
        _logger = logger;
    }

    /// <summary>
    /// Fetches configured RSS/Atom feeds and writes per-feed markdown digests to the room inbox when due.
    /// </summary>
    /// <remarks>
    /// Execution is throttled to at most once every six hours; if <paramref name="feeds"/> is empty or the throttle interval has not elapsed, the method returns immediately.
    /// For each URL it attempts to download, parse (RSS or Atom) and write a markdown file containing up to 8 entries. Individual feed failures are logged as warnings and do not stop processing of other feeds. Cancellation is honored and will be rethrown.
    /// The internal last-run timestamp is updated only if at least one feed write succeeds.
    /// </remarks>
    /// <param name="feeds">A read-only list of feed URLs to fetch.</param>
    /// <param name="ct">A cancellation token that cancels network and file operations.</param>
    public async Task RunIfDueAsync(IReadOnlyList<string> feeds, CancellationToken ct)
    {
        if (feeds.Count == 0) return;
        if ((DateTime.UtcNow - _lastRunUtc).TotalHours < 6) return;

        bool anySuccess = false;
        foreach (var url in feeds)
        {
            if (!TryValidateFeed(url, out var feedUri, out var canonicalUrl, out var storageStem))
            {
                _logger.LogWarning("Skipping invalid RSS feed URL {Url}", url);
                continue;
            }

            try
            {
                var xml = await _http.GetStringAsync(feedUri, ct);
                var doc = XDocument.Parse(xml);
                XNamespace ns = "http://www.w3.org/2005/Atom";
                var items = doc.Descendants("item").Take(8).ToList();
                if (items.Count == 0)
                    items = doc.Descendants(ns + "entry").Take(8).ToList();

                var lines = new List<string> { $"# RSS digest: {canonicalUrl}", "" };
                foreach (var el in items)
                {
                    var title = el.Element("title")?.Value ?? el.Element(ns + "title")?.Value ?? "(untitled)";
                    var link = el.Element("link")?.Value ?? el.Element(ns + "link")?.Attribute("href")?.Value ?? "";
                    lines.Add($"- **{title}** - {link}");
                }

                // Hash the canonical URL for uniqueness without exposing the raw query in the filename.
                var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalUrl));
                var feedHash = Convert.ToHexString(hashBytes)[..16].ToLowerInvariant();
                var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
                var path = Path.Combine(_room.InboxRssDir, $"rss-{storageStem}-{feedHash}-{timestamp}.md");
                await File.WriteAllTextAsync(path, string.Join("\n", lines), ct);
                anySuccess = true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "RSS fetch failed for {Url}", url);
            }
        }

        if (anySuccess)
            _lastRunUtc = DateTime.UtcNow;
    }

    private static bool TryValidateFeed(string? rawUrl, out Uri feedUri, out string canonicalUrl, out string storageStem)
    {
        feedUri = null!;
        canonicalUrl = "";
        storageStem = "";

        if (string.IsNullOrWhiteSpace(rawUrl))
            return false;

        var trimmed = rawUrl.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out feedUri))
            return false;

        if (feedUri.Scheme is not ("http" or "https"))
            return false;

        if (string.IsNullOrWhiteSpace(feedUri.Host))
            return false;

        canonicalUrl = feedUri.AbsoluteUri;
        storageStem = BuildStorageStem(feedUri);
        if (storageStem.Length == 0)
            return false;

        if (storageStem.Contains('/') || storageStem.Contains('\\'))
            return false;

        return Path.GetFileName(storageStem) == storageStem;
    }

    private static string BuildStorageStem(Uri feedUri)
    {
        const int maxLength = 48;
        var parts = new List<string>();

        AddStorageStemPart(parts, Uri.EscapeDataString(feedUri.IdnHost.ToLowerInvariant()));

        if (!feedUri.IsDefaultPort)
            AddStorageStemPart(parts, feedUri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var escapedPath = feedUri.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
        foreach (var segment in escapedPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var decodedSegment = Uri.UnescapeDataString(segment);
            AddStorageStemPart(parts, Uri.EscapeDataString(decodedSegment));
        }

        var normalized = string.Join("-", parts).Trim('-');
        if (normalized.Length > maxLength)
            normalized = TrimDanglingPercentEncoding(normalized[..maxLength]).Trim('-');

        if (normalized.Length == 0)
            return "feed";

        return normalized;
    }

    private static void AddStorageStemPart(List<string> parts, string rawPart)
    {
        var normalized = SanitizeStorageStemPart(rawPart);
        if (normalized.Length > 0)
            parts.Add(normalized);
    }

    private static string SanitizeStorageStemPart(string rawPart)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(rawPart.Length);
        var lastWasSeparator = false;

        foreach (var c in rawPart)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
                lastWasSeparator = false;
                continue;
            }

            if (c == '%')
            {
                builder.Append(c);
                lastWasSeparator = false;
                continue;
            }

            if (c is '-' or '_' or '.' or '/' or '\\' || char.IsWhiteSpace(c) || Array.IndexOf(invalidChars, c) >= 0)
            {
                if (!lastWasSeparator && builder.Length > 0)
                    builder.Append('-');

                lastWasSeparator = true;
            }
        }

        return builder.ToString().Trim('-');
    }

    private static string TrimDanglingPercentEncoding(string value)
    {
        if (value.EndsWith('%'))
            return value[..^1];

        if (value.Length >= 2 && value[^2] == '%' && IsHex(value[^1]))
            return value[..^2];

        return value;
    }

    private static bool IsHex(char c) =>
        c is >= '0' and <= '9'
        or >= 'a' and <= 'f'
        or >= 'A' and <= 'F';
}
