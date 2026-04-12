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
    private readonly Func<DreamerConfig, IChatClient> _walkClientFactory;
    private readonly Func<DreamerConfig, IChatClient> _echoClientFactory;
    private readonly TranscriptStore _transcripts;
    private readonly GatewayService _gateway;
    private readonly InsightsService _insights;
    private readonly DreamerStatus _status;
    private readonly ILogger<DreamerService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly SignalScorer _signals;
    private readonly BuildSprint _build;
    private readonly RssFetcher? _rss;
    private int _walkNumber;

    /// <summary>
    /// Initializes a DreamerService and configures its runtime dependencies and working components.
    /// </summary>
    /// <param name="hermesHome">The base Hermes runtime directory used for paths and runtime state.</param>
    /// <param name="configPath">File path to the Dreamer configuration file that will be reloaded each cycle.</param>
    /// <param name="transcriptsDir">Directory containing transcript `*.jsonl` files used to build research context.</param>
    /// <param name="room">The DreamerRoom that provides workspace directories (walks, inbox, feedback) and layout management.</param>
    public DreamerService(
        string hermesHome,
        string configPath,
        string transcriptsDir,
        DreamerRoom room,
        Func<DreamerConfig, IChatClient> walkClientFactory,
        Func<DreamerConfig, IChatClient> echoClientFactory,
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
        _walkClientFactory = walkClientFactory;
        _echoClientFactory = echoClientFactory;
        _transcripts = transcripts;
        _gateway = gateway;
        _insights = insights;
        _status = status;
        _rss = rssFetcher;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _signals = new SignalScorer(room, loggerFactory.CreateLogger<SignalScorer>());
        _build = new BuildSprint(room, loggerFactory.CreateLogger<BuildSprint>());
    }

    /// <summary>
    /// Runs the Dreamer background loop: reloads configuration each cycle and executes periodic walk cycles until cancelled.
    /// </summary>
    /// <param name="stoppingToken">A cancellation token used to request graceful shutdown of the loop.</param>
    /// <returns>A task that completes when the loop exits.</returns>
    public async Task RunForeverAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DreamerService loop starting (config reload each cycle)");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var config = DreamerConfig.Load(_configPath, _logger);

                if (!config.Enabled)
                {
                    _status.SetPhase("disabled");
                    await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken);
                    continue;
                }

                try
                {
                    _room.EnsureLayout();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Dreamer room layout failed; retrying next cycle");
                    _status.SetPhase("error");
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                    continue;
                }
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
                try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); } catch (OperationCanceledException) { /* graceful stop */ }
            }
        }

        _logger.LogInformation("DreamerService stopped");
    }

    /// <summary>
    — Executes a single Dreamer cycle: runs RSS if due, builds research context, generates a new walk, scores and processes signals, may trigger a build sprint, attempts scheduled digests, records analytics, and updates status.
    /// </summary>
    /// <param name="config">Configuration values governing this cycle's behaviour (timings, inputs, digests, autonomy, etc.).</param>
    /// <param name="ct">Cancellation token used to abort ongoing operations within the cycle.</param>
    private async Task RunOneCycleAsync(DreamerConfig config, CancellationToken ct)
    {
        _status.SetPhase("walking");
        if (_rss is not null)
            await TryRunRecoverableAsync(
                "refreshing Dreamer RSS inbox",
                () => _rss.RunIfDueAsync(config.RssFeeds, ct));

        // Create fresh clients from current config
        var walkClient = _walkClientFactory(config);
        var echoClient = _echoClientFactory(config);
        var walk = new DreamWalk(_room, walkClient, _loggerFactory.CreateLogger<DreamWalk>());
        var echo = new EchoDetector(echoClient, _loggerFactory.CreateLogger<EchoDetector>());

        var research = await TryGetRecoverableAsync(
            "building Dreamer research context",
            () => BuildResearchContextAsync(config, ct),
            "(no research context)");
        var prior = ReadLatestWalkExcerpt();
        var walkText = await TryGetRecoverableAsync<string?>(
            "running Dreamer walk",
            () => walk.RunAsync(config, research, prior, ct),
            null);
        if (string.IsNullOrWhiteSpace(walkText))
        {
            _status.SetPhase("idle");
            return;
        }

        _walkNumber++;
        _insights.RecordDreamerWalk();

        var echoScore = await echo.ScoreEchoAsync(walkText, prior, ct);
        string? slug = null;
        TryRunRecoverable(
            "processing Dreamer signals",
            () =>
            {
                _signals.ProcessWalk(walkText, echoScore, config, out slug);
                _insights.RecordDreamerSignal();
            });

        var board = _signals.LoadBoard();
        var top = board.Projects.OrderByDescending(kv => kv.Value.Score).FirstOrDefault();
        var topSlug = top.Key ?? "";
        var topScore = top.Value?.Score ?? 0;
        _status.AfterWalk(walkText[..Math.Min(400, walkText.Length)], _walkNumber, topScore, topSlug);

        bool shouldBuild;
        try
        {
            shouldBuild = _signals.ShouldTriggerBuild(slug, config, out _);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dreamer build trigger evaluation failed; skipping build");
            shouldBuild = false;
        }

        if (shouldBuild && slug is not null)
        {
            _status.SetPhase("building");
            await TryRunRecoverableAsync(
                $"running Dreamer build sprint for {slug}",
                async () =>
                {
                    await _build.RunAsync(slug, walkText, config.Autonomy, ct);
                    _signals.ResetProjectAfterBuild(slug);
                    _insights.RecordDreamerBuild();
                    _logger.LogInformation("Dreamer build sprint completed for {Slug}", slug);
                });
        }

        await TryRunRecoverableAsync(
            "sending Dreamer digest",
            () => MaybeSendDigestAsync(config, walkText, ct));
        TryRunRecoverable("saving Dreamer insights", () => _insights.Save());
        _status.SetPhase("idle");
    }

    /// <summary>
    /// Sends a scheduled Discord digest containing the most recent walk when a configured digest time is due and not already sent for the day.
    /// </summary>
    /// <param name="config">Dreamer configuration providing DiscordChannelId and DigestTimes (each entry formatted as "H:M").</param>
    /// <param name="lastWalk">Text of the last walk; the message body is truncated to 1500 characters.</param>
    private async Task MaybeSendDigestAsync(DreamerConfig config, string lastWalk, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.DiscordChannelId))
            return;

        var now = DateTime.Now;
        var day = now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        foreach (var t in config.DigestTimes)
        {
            try
            {
                var parts = t.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length < 2 ||
                    !int.TryParse(parts[0], out var h) ||
                    !int.TryParse(parts[1], out var m))
                    continue;

                // Validate hour and minute ranges
                if (h < 0 || h > 23 || m < 0 || m > 59)
                    continue;

                var target = new TimeSpan(h, m, 0);
                var slotKey = $"{day}|{t}";
                var digestPath = Path.Combine(_room.FeedbackDir, ".digest-sent.txt");
                var sent = await TryGetRecoverableAsync(
                    $"reading Dreamer digest state from {digestPath}",
                    async () => File.Exists(digestPath) ? await File.ReadAllTextAsync(digestPath, ct) : "",
                    "");
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
                    await TryRunRecoverableAsync(
                        $"recording Dreamer digest slot {slotKey}",
                        () => File.AppendAllTextAsync(digestPath, slotKey + "\n", ct));
                    _insights.RecordDreamerDigest();
                    _logger.LogInformation("Dreamer digest sent for slot {Slot}", t);
                }
                else
                    _logger.LogWarning("Dreamer digest failed: {Error}", result.Error);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Dreamer digest slot {Slot} failed; continuing with remaining slots", t);
            }
        }
    }

    /// <summary>
    /// Builds a consolidated research context string composed from recent transcript sessions and inbox files based on the provided configuration.
    /// </summary>
    /// <param name="config">Controls which sources are included: when <c>InputTranscripts</c> is true, up to four most-recent transcript sessions are included; when <c>InputInbox</c> is true, inbox and RSS inbox markdown files are included.</param>
    /// <param name="ct">Cancellation token that may abort the operation.</param>
    /// <returns>
    /// A single string made of labeled chunks joined by blank lines, or the literal "(no research context)" if no chunks were collected. Session chunks contain up to the last 24 messages formatted as "Role: Content". Inbox and RSS inbox chunks include file contents truncated to 4000 characters. Up to four transcript files and up to six files per inbox directory are considered.
    /// </returns>
    private async Task<string> BuildResearchContextAsync(DreamerConfig config, CancellationToken ct)
    {
        var chunks = new List<string>();

        if (config.InputTranscripts && Directory.Exists(_transcriptsDir))
        {
            List<string> files;
            try
            {
                files = Directory.EnumerateFiles(_transcriptsDir, "*.jsonl")
                    .Select(f => (f, t: File.GetLastWriteTimeUtc(f)))
                    .OrderByDescending(x => x.t)
                    .Take(4)
                    .Select(x => x.f)
                    .ToList();
            }
            catch (Exception ex) when (IsRecoverableFileException(ex))
            {
                _logger.LogWarning(ex, "Skipping transcript research context; transcript enumeration failed");
                files = new List<string>();
            }

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
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Skipping transcript context for session {SessionId}", id);
                }
            }
        }

        if (config.InputInbox)
        {
            if (Directory.Exists(_room.InboxDir))
            {
                List<string> inboxFiles;
                try
                {
                    inboxFiles = Directory.EnumerateFiles(_room.InboxDir, "*.md").Take(6).ToList();
                }
                catch (Exception ex) when (IsRecoverableFileException(ex))
                {
                    _logger.LogWarning(ex, "Skipping Dreamer inbox context; inbox enumeration failed");
                    inboxFiles = new List<string>();
                }

                foreach (var md in inboxFiles)
                {
                    try
                    {
                        var txt = await File.ReadAllTextAsync(md, ct);
                        chunks.Add($"### Inbox {Path.GetFileName(md)}\n{txt[..Math.Min(4000, txt.Length)]}");
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex) when (ex is FileNotFoundException || ex is IOException || ex is UnauthorizedAccessException)
                    {
                        _logger.LogWarning(ex, "Skipping unreadable inbox file: {Path}", md);
                    }
                }
            }

            if (Directory.Exists(_room.InboxRssDir))
            {
                List<string> rssFiles;
                try
                {
                    rssFiles = Directory.EnumerateFiles(_room.InboxRssDir, "*.md").Take(6).ToList();
                }
                catch (Exception ex) when (IsRecoverableFileException(ex))
                {
                    _logger.LogWarning(ex, "Skipping Dreamer RSS inbox context; inbox enumeration failed");
                    rssFiles = new List<string>();
                }

                foreach (var md in rssFiles)
                {
                    try
                    {
                        var txt = await File.ReadAllTextAsync(md, ct);
                        chunks.Add($"### RSS Inbox {Path.GetFileName(md)}\n{txt[..Math.Min(4000, txt.Length)]}");
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex) when (ex is FileNotFoundException || ex is IOException || ex is UnauthorizedAccessException)
                    {
                        _logger.LogWarning(ex, "Skipping unreadable RSS inbox file: {Path}", md);
                    }
                }
            }
        }

        return chunks.Count == 0 ? "(no research context)" : string.Join("\n\n", chunks);
    }

    /// <summary>
    /// Read the most recent markdown walk file and return its contents truncated to at most 3000 characters.
    /// </summary>
    /// <returns>`string` containing the file contents truncated to at most 3000 characters, or `null` if no walk file is found or it cannot be read.</returns>
    private string? ReadLatestWalkExcerpt()
    {
        if (!Directory.Exists(_room.WalksDir))
            return null;
        try
        {
            var latest = Directory.EnumerateFiles(_room.WalksDir, "*.md")
                .Select(f => (f, t: File.GetLastWriteTimeUtc(f)))
                .OrderByDescending(x => x.t)
                .FirstOrDefault();
            if (string.IsNullOrEmpty(latest.f))
                return null;

            var text = File.ReadAllText(latest.f);
            return text.Length <= 3000 ? text : text[..3000];
        }
        catch (Exception ex) when (IsRecoverableFileException(ex))
        {
            _logger.LogWarning(ex, "Skipping prior Dreamer walk excerpt");
            return null;
        }
    }

    private void TryRunRecoverable(string operation, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dreamer error while {Operation}", operation);
        }
    }

    private async Task TryRunRecoverableAsync(string operation, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dreamer error while {Operation}", operation);
        }
    }

    private async Task<T> TryGetRecoverableAsync<T>(string operation, Func<Task<T>> action, T fallback)
    {
        try
        {
            return await action();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dreamer error while {Operation}", operation);
            return fallback;
        }
    }

    private static bool IsRecoverableFileException(Exception ex) =>
        ex is IOException or UnauthorizedAccessException;
}
