namespace Hermes.Agent.Dreamer;

using Hermes.Agent.Analytics;
using Hermes.Agent.Gateway;
using Hermes.Agent.LLM;
using Hermes.Agent.Transcript;
using Microsoft.Extensions.Logging;

/// <summary>
/// Background Dreamer: periodic local-model walks, signal scoring, optional digests to Discord,
/// and sandboxed build sprints. Disabled when <c>dreamer.enabled</c> is false in config.yaml.
/// Started from the desktop host via <see cref="RunForeverAsync"/> (no generic host required).
/// </summary>
public sealed class DreamerService
{
    private readonly string _configPath;
    private readonly string _transcriptsDir;
    private readonly DreamerRoom _room;
    private readonly IChatClient _walkClient;
    private readonly IChatClient _echoClient;
    private readonly TranscriptStore _transcripts;
    private readonly GatewayService _gateway;
    private readonly InsightsService _insights;
    private readonly DreamerStatus _status;
    private readonly ILogger<DreamerService> _logger;
    private readonly DreamWalk _walk;
    private readonly EchoDetector _echo;
    private readonly SignalScorer _signals;
    private readonly BuildSprint _build;
    private readonly RssFetcher? _rss;
    private int _walkNumber;

    public DreamerService(
        string hermesHome,
        string configPath,
        string transcriptsDir,
        DreamerRoom room,
        IChatClient walkClient,
        IChatClient echoClient,
        TranscriptStore transcripts,
        GatewayService gateway,
        InsightsService insights,
        DreamerStatus status,
        RssFetcher? rssFetcher,
        ILogger<DreamerService> logger,
        ILoggerFactory loggerFactory)
    {
        _configPath = configPath;
        _transcriptsDir = transcriptsDir;
        _room = room;
        _walkClient = walkClient;
        _echoClient = echoClient;
        _transcripts = transcripts;
        _gateway = gateway;
        _insights = insights;
        _status = status;
        _rss = rssFetcher;
        _logger = logger;
        _walk = new DreamWalk(room, walkClient, loggerFactory.CreateLogger<DreamWalk>());
        _echo = new EchoDetector(echoClient, loggerFactory.CreateLogger<EchoDetector>());
        _signals = new SignalScorer(room, loggerFactory.CreateLogger<SignalScorer>());
        _build = new BuildSprint(room, loggerFactory.CreateLogger<BuildSprint>());
    }

    public async Task RunForeverAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DreamerService loop starting (config reload each cycle)");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var config = DreamerConfig.Load(_configPath);
                if (!config.Enabled)
                {
                    _status.SetPhase("disabled");
                    await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken);
                    continue;
                }

                _room.EnsureLayout();
                var interval = TimeSpan.FromMinutes(Math.Clamp(config.WalkIntervalMinutes, 1, 120));
                await Task.Delay(interval, stoppingToken);

                await RunOneCycleAsync(config, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Dreamer cycle error");
                _status.SetPhase("error");
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }

        _logger.LogInformation("DreamerService stopped");
    }

    private async Task RunOneCycleAsync(DreamerConfig config, CancellationToken ct)
    {
        _status.SetPhase("walking");
        if (_rss is not null)
            await _rss.RunIfDueAsync(config.RssFeeds, ct);

        var research = await BuildResearchContextAsync(config, ct);
        var prior = ReadLatestWalkExcerpt();
        var walkText = await _walk.RunAsync(config, research, prior, ct);
        _walkNumber++;
        _insights.RecordDreamerWalk();

        var echo = await _echo.ScoreEchoAsync(walkText, prior, ct);
        _signals.ProcessWalk(walkText, echo, config, out var slug);
        _insights.RecordDreamerSignal();

        var board = _signals.LoadBoard();
        var top = board.Projects.OrderByDescending(kv => kv.Value.Score).FirstOrDefault();
        var topSlug = top.Key ?? "";
        var topScore = top.Value?.Score ?? 0;
        _status.AfterWalk(walkText[..Math.Min(400, walkText.Length)], _walkNumber, topScore, topSlug);

        if (_signals.ShouldTriggerBuild(slug, config, out var ps) && slug is not null)
        {
            _status.SetPhase("building");
            await _build.RunAsync(slug, walkText, config.Autonomy, ct);
            _signals.ResetProjectAfterBuild(slug);
            _insights.RecordDreamerBuild();
            _logger.LogInformation("Dreamer build sprint completed for {Slug}", slug);
        }

        await MaybeSendDigestAsync(config, walkText, ct);
        _insights.Save();
        _status.SetPhase("idle");
    }

    private async Task MaybeSendDigestAsync(DreamerConfig config, string lastWalk, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.DiscordChannelId))
            return;

        var now = DateTime.Now;
        var day = now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        foreach (var t in config.DigestTimes)
        {
            var parts = t.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2 ||
                !int.TryParse(parts[0], out var h) ||
                !int.TryParse(parts[1], out var m))
                continue;

            var target = new TimeSpan(h, m, 0);
            var slotKey = $"{day}|{t}";
            var digestPath = Path.Combine(_room.FeedbackDir, ".digest-sent.txt");
            var sent = File.Exists(digestPath) ? await File.ReadAllTextAsync(digestPath, ct) : "";
            if (sent.Contains(slotKey, StringComparison.Ordinal))
                continue;

            var delta = (now.TimeOfDay - target).Duration();
            if (delta > TimeSpan.FromMinutes(12))
                continue;

            var postcard = $"**Hermes Dreamer digest** ({t})\n{lastWalk[..Math.Min(1500, lastWalk.Length)]}";
            _status.SetPostcardPreview(postcard);
            var result = await _gateway.SendTextAsync(Platform.Discord, config.DiscordChannelId, postcard, ct);
            if (result.Success)
            {
                await File.AppendAllTextAsync(digestPath, slotKey + "\n", ct);
                _insights.RecordDreamerDigest();
                _logger.LogInformation("Dreamer digest sent for slot {Slot}", t);
            }
            else
                _logger.LogWarning("Dreamer digest failed: {Error}", result.Error);
        }
    }

    private async Task<string> BuildResearchContextAsync(DreamerConfig config, CancellationToken ct)
    {
        var chunks = new List<string>();

        if (config.InputTranscripts && Directory.Exists(_transcriptsDir))
        {
            var files = Directory.EnumerateFiles(_transcriptsDir, "*.jsonl")
                .Select(f => (f, t: File.GetLastWriteTimeUtc(f)))
                .OrderByDescending(x => x.t)
                .Take(4)
                .Select(x => x.f)
                .ToList();

            foreach (var path in files)
            {
                var id = Path.GetFileNameWithoutExtension(path);
                try
                {
                    var msgs = await _transcripts.LoadSessionAsync(id, ct);
                    var tail = msgs.TakeLast(24).Select(m => $"{m.Role}: {m.Content}");
                    chunks.Add($"### Session {id}\n" + string.Join("\n", tail));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    /* skip other exceptions */
                }
            }
        }

        if (config.InputInbox)
        {
            if (Directory.Exists(_room.InboxDir))
            {
                foreach (var md in Directory.EnumerateFiles(_room.InboxDir, "*.md").Take(6))
                {
                    var txt = await File.ReadAllTextAsync(md, ct);
                    chunks.Add($"### Inbox {Path.GetFileName(md)}\n{txt[..Math.Min(4000, txt.Length)]}");
                }
            }

            if (Directory.Exists(_room.InboxRssDir))
            {
                foreach (var md in Directory.EnumerateFiles(_room.InboxRssDir, "*.md").Take(6))
                {
                    var txt = await File.ReadAllTextAsync(md, ct);
                    chunks.Add($"### RSS Inbox {Path.GetFileName(md)}\n{txt[..Math.Min(4000, txt.Length)]}");
                }
            }
        }

        return chunks.Count == 0 ? "(no research context)" : string.Join("\n\n", chunks);
    }

    private string? ReadLatestWalkExcerpt()
    {
        if (!Directory.Exists(_room.WalksDir))
            return null;
        var latest = Directory.EnumerateFiles(_room.WalksDir, "*.md")
            .Select(f => (f, t: File.GetLastWriteTimeUtc(f)))
            .OrderByDescending(x => x.t)
            .FirstOrDefault();
        if (string.IsNullOrEmpty(latest.f)) return null;
        try
        {
            var text = File.ReadAllText(latest.f);
            return text.Length <= 3000 ? text : text[..3000];
        }
        catch { return null; }
    }
}