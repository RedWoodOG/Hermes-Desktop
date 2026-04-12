using System.Net;
using System.Net.Http;
using System.Text;
using Hermes.Agent.Dreamer;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HermesDesktop.Tests.Services;

[TestClass]
public sealed class RssFetcherTests
{
    [TestMethod]
    public async Task RunIfDueAsync_SkipsInvalidFeedUrls_BeforeFetching()
    {
        var hermesHome = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var room = new DreamerRoom(hermesHome);
            room.EnsureLayout();

            var handler = new RecordingHandler();
            using var http = new HttpClient(handler);
            var fetcher = new RssFetcher(http, room, NullLogger<RssFetcher>.Instance);

            await fetcher.RunIfDueAsync(
                new[]
                {
                    "not a url",
                    "ftp://example.com/feed.xml",
                    "https://example.com/feed.xml"
                },
                CancellationToken.None);

            Assert.AreEqual(1, handler.RequestUris.Count);
            Assert.AreEqual("https://example.com/feed.xml", handler.RequestUris[0].AbsoluteUri);
            Assert.AreEqual(1, Directory.EnumerateFiles(room.InboxRssDir, "*.md").Count());
        }
        finally
        {
            if (Directory.Exists(hermesHome))
                Directory.Delete(hermesHome, recursive: true);
        }
    }

    [TestMethod]
    public async Task RunIfDueAsync_WritesPercentEncodedInboxRssFileName()
    {
        var hermesHome = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var room = new DreamerRoom(hermesHome);
            room.EnsureLayout();

            var handler = new RecordingHandler();
            using var http = new HttpClient(handler);
            var fetcher = new RssFetcher(http, room, NullLogger<RssFetcher>.Instance);

            await fetcher.RunIfDueAsync(
                new[]
                {
                    "https://Example.com/news/feed.xml?token=abc%3Cdef%3E&topic=AI#latest"
                },
                CancellationToken.None);

            var file = Directory.EnumerateFiles(room.InboxRssDir, "*.md").Single();
            var name = Path.GetFileName(file);

            StringAssert.StartsWith(name, "rss-example-com-news-feed-xml-");
            AssertStringDoesNotContain(name, "?");
            AssertStringDoesNotContain(name, "#");
            AssertStringDoesNotContain(name, ":");
            AssertStringDoesNotContain(name, "/");
            AssertStringDoesNotContain(name, "\\");
        }
        finally
        {
            if (Directory.Exists(hermesHome))
                Directory.Delete(hermesHome, recursive: true);
        }
    }

    [TestMethod]
    public async Task RunIfDueAsync_WritesSeparatorFreeFileName_ForTraversalStyleFeedUrls()
    {
        var hermesHome = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var room = new DreamerRoom(hermesHome);
            room.EnsureLayout();

            var handler = new RecordingHandler();
            using var http = new HttpClient(handler);
            var fetcher = new RssFetcher(http, room, NullLogger<RssFetcher>.Instance);

            await fetcher.RunIfDueAsync(
                new[]
                {
                    "https://example.com/%2e%2e/%2fwindows%5csystem32/feed.xml?token=../../secret#frag"
                },
                CancellationToken.None);

            var file = Directory.EnumerateFiles(room.InboxRssDir, "*.md").Single();
            var name = Path.GetFileName(file);
            var stem = ExtractStorageStem(name);

            Assert.AreEqual(name, Path.GetFileName(name));
            AssertStringDoesNotContain(name, "/");
            AssertStringDoesNotContain(name, "\\");
            StringAssert.Contains(stem, "%2fwindows%5csystem32");
        }
        finally
        {
            if (Directory.Exists(hermesHome))
                Directory.Delete(hermesHome, recursive: true);
        }
    }

    [TestMethod]
    public async Task RunIfDueAsync_ClampsStorageStem_ForOverlongMaliciousUrls()
    {
        var hermesHome = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var room = new DreamerRoom(hermesHome);
            room.EnsureLayout();

            var handler = new RecordingHandler();
            using var http = new HttpClient(handler);
            var fetcher = new RssFetcher(http, room, NullLogger<RssFetcher>.Instance);
            var longSegment = new string('a', 96);
            var maliciousUrl = $"https://example.com/{longSegment}/%2f%5c/{longSegment}/feed.xml";

            await fetcher.RunIfDueAsync(new[] { maliciousUrl }, CancellationToken.None);

            var file = Directory.EnumerateFiles(room.InboxRssDir, "*.md").Single();
            var stem = ExtractStorageStem(Path.GetFileName(file));

            Assert.IsTrue(stem.Length <= 48, $"Expected clamped storage stem but got {stem.Length}: {stem}");
            AssertStringDoesNotContain(stem, "/");
            AssertStringDoesNotContain(stem, "\\");
        }
        finally
        {
            if (Directory.Exists(hermesHome))
                Directory.Delete(hermesHome, recursive: true);
        }
    }

    private static void AssertStringDoesNotContain(string value, string substring)
    {
        Assert.IsFalse(
            value.Contains(substring, StringComparison.Ordinal),
            $"{value} should not contain {substring}.");
    }

    private static string ExtractStorageStem(string fileName)
    {
        const string prefix = "rss-";
        const string suffix = ".md";

        Assert.IsTrue(fileName.StartsWith(prefix, StringComparison.Ordinal));
        Assert.IsTrue(fileName.EndsWith(suffix, StringComparison.Ordinal));

        var content = fileName[prefix.Length..^suffix.Length];
        var lastDash = content.LastIndexOf('-');
        Assert.IsTrue(lastDash > 0, $"Expected timestamp suffix in filename: {fileName}");

        var secondLastDash = content.LastIndexOf('-', lastDash - 1);
        Assert.IsTrue(secondLastDash > 0, $"Expected hash suffix in filename: {fileName}");

        return content[..secondLastDash];
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private const string RssXml = """
<rss version="2.0">
  <channel>
    <title>Example Feed</title>
    <item>
      <title>Entry One</title>
      <link>https://example.com/entry-one</link>
    </item>
  </channel>
</rss>
""";

        public List<Uri> RequestUris { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(RssXml, Encoding.UTF8, "application/rss+xml")
            });
        }
    }
}
