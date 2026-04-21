using Microsoft.UI.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Hermes.Agent.Core;
using Hermes.Agent.Diagnostics;
using Hermes.Agent.LLM;
using Hermes.Agent.Transcript;
using Hermes.Agent.Memory;
using Hermes.Agent.Skills;
using Hermes.Agent.Permissions;
using Hermes.Agent.Tasks;
using Hermes.Agent.Buddy;
using Hermes.Agent.Context;
using Hermes.Agent.Agents;
using Hermes.Agent.Coordinator;
using Hermes.Agent.Mcp;
using Hermes.Agent.Analytics;
using Hermes.Agent.Plugins;
using Hermes.Agent.Soul;
using Hermes.Agent.Tools;
using Hermes.Agent.Gateway;
using Hermes.Agent.Gateway.Platforms;
using Hermes.Agent.Dreamer;
using HermesDesktop.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;

namespace HermesDesktop;

public partial class App : Application
{
    private Window? _window;
    private static readonly object _dreamerCtsLock = new();
    private static readonly object _dreamerHttpClientsLock = new();
    private static System.Threading.CancellationTokenSource? _dreamerCts;
    private static DreamerHttpClients? _dreamerHttpClients;

    /// <summary>Global service provider for DI — accessed by pages via App.Services.</summary>
    /// <remarks>
    /// Starts as <see cref="UninitializedAppServiceProvider"/> so <see cref="TryGetAppLogger"/> can call
    /// <see cref="IServiceProvider.GetService"/> without throwing before <see cref="OnLaunched"/> builds the real provider.
    /// <see cref="Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService"/> throws if invoked before services are registered.
    /// </remarks>
    public static IServiceProvider Services { get; private set; } = UninitializedAppServiceProvider.Instance;

    /// <summary>
    /// Initializes application components and wires up application-level unhandled exception handling.
    /// </summary>
    /// <remarks>
    /// Registers OnAppUnhandledException to run when the app encounters an unhandled exception (used to cancel background workers such as the Dreamer loop).
    /// </remarks>
    public App()
    {
        InitializeComponent();
        this.UnhandledException += OnAppUnhandledException;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    /// <summary>
    /// Cancels the Dreamer background loop when the application encounters an unhandled exception.
    /// </summary>
    private void OnAppUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        var logger = TryGetAppLogger();
        var exception = e.Exception;

        try
        {
            if (logger is not null)
                logger.LogError(exception, "Unhandled UI exception");
            else
                BestEffort.LogFailure(null, exception, "handling unhandled UI exception");
        }
        catch (Exception ex)
        {
            BestEffort.LogFailure(null, ex, "logging unhandled UI exception");
            BestEffort.LogFailure(null, exception, "handling unhandled UI exception");
        }

        TryCancelDreamerCts(logger, "app unhandled exception");

        if (exception is OperationCanceledException or ObjectDisposedException)
            e.Handled = true;
    }

    private static void OnProcessExit(object? sender, EventArgs e)
    {
        var logger = TryGetAppLogger();
        TryCancelAndDisposeDreamerCts(logger, "process exit");
        TryDisposeDreamerHttpClients(logger, "process exit");
    }

    private static ILogger<App>? TryGetAppLogger()
    {
        if (ReferenceEquals(Services, UninitializedAppServiceProvider.Instance))
            return null;

        try
        {
            return Services.GetService<ILogger<App>>();
        }
        catch (Exception ex)
        {
            BestEffort.LogFailure(null, ex, "resolving app logger");
            return null;
        }
    }

    private static void TryCancelDreamerCts(ILogger? logger, string reason)
    {
        System.Threading.CancellationTokenSource? dreamerCts;
        lock (_dreamerCtsLock)
        {
            dreamerCts = _dreamerCts;
        }

        try
        {
            dreamerCts?.Cancel();
        }
        catch (Exception ex)
        {
            BestEffort.LogFailure(logger, ex, "cancelling Dreamer cancellation token source", $"reason={reason}");
        }
    }

    private static void TryCancelAndDisposeDreamerCts(ILogger? logger, string reason)
    {
        System.Threading.CancellationTokenSource? dreamerCts;
        lock (_dreamerCtsLock)
        {
            dreamerCts = _dreamerCts;
            _dreamerCts = null;
        }

        TryCancelAndDisposeDreamerCts(dreamerCts, logger, reason);
    }

    private static void TryCancelAndDisposeDreamerCts(System.Threading.CancellationTokenSource? dreamerCts, ILogger? logger, string reason)
    {
        if (dreamerCts is null)
            return;

        try
        {
            dreamerCts.Cancel();
            dreamerCts.Dispose();
        }
        catch (Exception ex)
        {
            BestEffort.LogFailure(logger, ex, "cancelling and disposing Dreamer cancellation token source", $"reason={reason}");

            try
            {
                dreamerCts.Dispose();
            }
            catch (Exception disposeEx)
            {
                BestEffort.LogFailure(logger, disposeEx, "disposing Dreamer cancellation token source", $"reason={reason}");
            }
        }
    }

    private static void SetDreamerCts(System.Threading.CancellationTokenSource dreamerCts, ILogger? logger, string reason)
    {
        System.Threading.CancellationTokenSource? previousDreamerCts;
        lock (_dreamerCtsLock)
        {
            previousDreamerCts = _dreamerCts;
            _dreamerCts = dreamerCts;
        }

        TryCancelAndDisposeDreamerCts(previousDreamerCts, logger, reason);
    }

    private static void TryCancelAndDisposeDreamerCtsIfCurrent(System.Threading.CancellationTokenSource dreamerCts, ILogger? logger, string reason)
    {
        var shouldDispose = false;
        lock (_dreamerCtsLock)
        {
            if (ReferenceEquals(_dreamerCts, dreamerCts))
            {
                _dreamerCts = null;
                shouldDispose = true;
            }
        }

        if (shouldDispose)
            TryCancelAndDisposeDreamerCts(dreamerCts, logger, reason);
    }

    private static DreamerHttpClients GetOrCreateDreamerHttpClients()
    {
        lock (_dreamerHttpClientsLock)
        {
            _dreamerHttpClients ??= new DreamerHttpClients(
                walk: DreamerHttpClientFactory.Create(TimeSpan.FromMinutes(4)),
                echo: DreamerHttpClientFactory.Create(TimeSpan.FromMinutes(3)),
                rss: DreamerHttpClientFactory.Create(TimeSpan.FromMinutes(2)));

            return _dreamerHttpClients;
        }
    }

    private static void TryDisposeDreamerHttpClients(ILogger? logger, string reason)
    {
        DreamerHttpClients? dreamerHttpClients;
        lock (_dreamerHttpClientsLock)
        {
            dreamerHttpClients = _dreamerHttpClients;
            _dreamerHttpClients = null;
        }

        if (dreamerHttpClients is null)
            return;

        try
        {
            dreamerHttpClients.Dispose();
        }
        catch (Exception ex)
        {
            BestEffort.LogFailure(logger, ex, "disposing Dreamer HTTP clients", $"reason={reason}");
        }
    }

    /// <summary>
    /// Initializes dependency injection, creates the main window, and activates the application UI.
    /// </summary>
    /// <param name="args">Activation arguments provided by the system when the application is launched.</param>
    /// <exception cref="Exception">On failure during startup the exception is reported via startup diagnostics and rethrown.</exception>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            Services = ConfigureServices();
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception ex)
        {
            StartupDiagnostics.ReportFatalStartupException(ex);
            throw;
        }
    }

    /// <summary>
    /// Configure dependency injection, register all application services, and perform post-build initialization.
    /// </summary>
    /// <remarks>
    /// Ensures the Hermes home and project directories exist and creates default SOUL.md and USER.md if missing (non-fatal on failure).
    /// Registers core services such as logging, chat clients, transcript and memory stores, skill and task managers, wiki, soul services, agent and orchestration services, tools and plugins, analytics, and Dreamer status.
    /// After building the provider it registers tools, initializes MCP (fire-and-forget), wires the UI permission callback, and starts native gateway and Dreamer background components as appropriate.
    /// </remarks>
    /// <returns>The built <see cref="ServiceProvider"/> containing the registered application services.</returns>
    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // Logging — file + debug sinks so logs are visible outside Visual Studio
        var logsDir = Path.Combine(HermesEnvironment.HermesHomePath, "hermes-cs", "logs");
        Directory.CreateDirectory(logsDir);
        services.AddLogging(builder =>
        {
            builder.AddDebug();
            builder.SetMinimumLevel(LogLevel.Information);
            // Suppress only per-request HttpClient Information chatter. If richer HTTP diagnostics
            // are enabled later, those categories can include sensitive request metadata; keeping the
            // filter scoped preserves Warning/Error transport failures and any separate app-level
            // connectivity telemetry from HermesChatService/RuntimeStatusService health checks.
            builder.AddFilter((category, level) =>
                category is null ||
                !category.StartsWith("System.Net.Http.HttpClient", StringComparison.Ordinal) ||
                level >= LogLevel.Warning);
            builder.AddProvider(new FileLoggerProvider(Path.Combine(logsDir, "hermes.log")));
        });

        // LLM config from environment/config.yaml
        var llmConfig = HermesEnvironment.CreateLlmConfig();
        services.AddSingleton(llmConfig);
        services.AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromMinutes(5) });

        // Optional credential pool for multi-key rotation
        var credentialPool = HermesEnvironment.LoadCredentialPool();
        if (credentialPool is not null)
            services.AddSingleton(credentialPool);

        // Chat client factory — enables runtime model/provider swapping
        // Pattern from Claude Code: model read from state at call time, fresh client on swap
        services.AddSingleton(sp => new ChatClientFactory(
            sp.GetRequiredService<LlmConfig>(),
            new HttpClient { Timeout = TimeSpan.FromMinutes(5) },
            sp.GetRequiredService<ILogger<ChatClientFactory>>(),
            sp.GetService<CredentialPool>()));

        // Swappable proxy — all existing IChatClient consumers automatically route
        // through the factory's current client. No code changes needed anywhere else.
        services.AddSingleton<IChatClient>(sp =>
            new SwappableChatClient(sp.GetRequiredService<ChatClientFactory>()));

        // Hermes home directory — ensure all required dirs exist on startup
        var hermesHome = HermesEnvironment.HermesHomePath;
        var projectDir = Path.Combine(hermesHome, "hermes-cs");
        foreach (var dir in new[]
        {
            hermesHome, projectDir,
            Path.Combine(hermesHome, "soul"),              // mistakes.jsonl, habits.jsonl
            Path.Combine(hermesHome, "dreamer"),            // Dreamer room (walks, projects, inbox)
            Path.Combine(projectDir, "transcripts"),
            Path.Combine(projectDir, "memory"),
            Path.Combine(projectDir, "skills"),
            Path.Combine(projectDir, "tasks"),
            Path.Combine(projectDir, "buddy"),
            Path.Combine(projectDir, "agents"),
            Path.Combine(projectDir, "analytics"),
        })
        {
            Directory.CreateDirectory(dir);
        }
        // Ensure SOUL.md and USER.md exist with defaults (non-fatal if write fails)
        try
        {
            var soulPath = Path.Combine(hermesHome, "SOUL.md");
            var userPath = Path.Combine(hermesHome, "USER.md");
            if (!File.Exists(soulPath))
                File.WriteAllText(soulPath, "# Agent Soul\n\nYou are a helpful AI assistant.\n");
            if (!File.Exists(userPath))
                File.WriteAllText(userPath, "# User Profile\n\nNo profile configured yet. Tell me about yourself.\n");
        }
        catch (Exception ex)
        {
            BestEffort.LogFailure(TryGetAppLogger(), ex, "creating default soul and user files");
        }

        // Transcript store
        var transcriptsDir = Path.Combine(projectDir, "transcripts");
        services.AddSingleton(sp => new TranscriptStore(transcriptsDir, eagerFlush: true));

        // Memory manager
        var memoryDir = Path.Combine(projectDir, "memory");
        services.AddSingleton(sp => new MemoryManager(
            memoryDir,
            sp.GetRequiredService<IChatClient>(),
            sp.GetRequiredService<ILogger<MemoryManager>>()));

        // Skill manager — copy bundled skills on first run if user dir is empty (non-fatal)
        var skillsDir = Path.Combine(projectDir, "skills");
        try
        {
            var bundledSkillsDir = FindRepoSkillsDir();
            if ((!Directory.Exists(skillsDir) || !Directory.EnumerateFileSystemEntries(skillsDir).Any())
                && bundledSkillsDir is not null && Directory.Exists(bundledSkillsDir))
            {
                CopyDirectoryRecursive(bundledSkillsDir, skillsDir);
            }
        }
        catch (Exception ex)
        {
            BestEffort.LogFailure(TryGetAppLogger(), ex, "copying bundled skills");
        }
        services.AddSingleton(sp => new SkillManager(
            skillsDir,
            sp.GetRequiredService<ILogger<SkillManager>>()));

        // Permission manager + workspace-scoped permission memory
        var permissionMemoryDir = Path.Combine(projectDir, "permissions");
        var workspacePath = HermesEnvironment.AgentWorkingDirectory;
        services.AddSingleton(sp => new WorkspacePermissionRuleStore(
            permissionMemoryDir,
            workspacePath,
            sp.GetRequiredService<ILogger<WorkspacePermissionRuleStore>>()));
        services.AddSingleton(sp =>
        {
            var store = sp.GetRequiredService<WorkspacePermissionRuleStore>();
            var context = new PermissionContext();
            foreach (var rule in store.LoadAlwaysAllowRules())
            {
                context.AlwaysAllow.Add(rule);
            }

            return new PermissionManager(
                context,
                sp.GetRequiredService<ILogger<PermissionManager>>());
        });

        // Task manager
        var tasksDir = Path.Combine(projectDir, "tasks");
        services.AddSingleton(sp => new TaskManager(
            tasksDir,
            sp.GetRequiredService<ILogger<TaskManager>>()));

        // Buddy service (persisted to buddy/buddy.json under the project dir)
        var buddyDir = Path.Combine(projectDir, "buddy");
        var buddyConfigPath = Path.Combine(buddyDir, "buddy.json");
        services.AddSingleton(sp => new BuddyService(
            buddyConfigPath,
            sp.GetRequiredService<IChatClient>()));

        // Wiki system (persistent knowledge base)
        var wikiConfig = new Hermes.Agent.Wiki.WikiConfig();
        services.AddSingleton(wikiConfig);
        services.AddSingleton<Hermes.Agent.Wiki.IWikiStorage>(sp =>
            new Hermes.Agent.Wiki.LocalWikiStorage(sp.GetRequiredService<Hermes.Agent.Wiki.WikiConfig>()));
        services.AddSingleton(sp => new Hermes.Agent.Wiki.WikiSearchIndex(
            Path.Combine(sp.GetRequiredService<Hermes.Agent.Wiki.WikiConfig>().WikiPath, ".wiki-search.db"),
            sp.GetRequiredService<ILogger<Hermes.Agent.Wiki.WikiSearchIndex>>()));
        services.AddSingleton(sp => new Hermes.Agent.Wiki.WikiManager(
            sp.GetRequiredService<Hermes.Agent.Wiki.IWikiStorage>(),
            sp.GetRequiredService<Hermes.Agent.Wiki.WikiConfig>(),
            sp.GetRequiredService<Hermes.Agent.Wiki.WikiSearchIndex>(),
            sp.GetRequiredService<ILogger<Hermes.Agent.Wiki.WikiManager>>()));

        // Soul service (persistent identity, user profile, mistakes, habits)
        services.AddSingleton(sp => new SoulService(
            hermesHome,
            sp.GetRequiredService<ILogger<SoulService>>()));

        // Soul extractor (LLM-powered transcript analysis)
        services.AddSingleton(sp => new SoulExtractor(
            sp.GetRequiredService<IChatClient>(),
            sp.GetRequiredService<ILogger<SoulExtractor>>()));

        // Soul registry (browsable soul templates)
        var soulsSearchPaths = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "skills", "souls"),       // Shipped with app
            Path.Combine(projectDir, "souls"),                               // User-installed souls
            Path.Combine(Path.GetDirectoryName(hermesHome) ?? hermesHome, "hermes-agent", "skills", "souls"), // Hermes CLI souls
        };
        services.AddSingleton(sp => new SoulRegistry(
            soulsSearchPaths,
            sp.GetRequiredService<ILogger<SoulRegistry>>()));

        // Agent profile manager (multi-agent configurations)
        var agentsDir = Path.Combine(projectDir, "agents");
        services.AddSingleton(sp => new AgentProfileManager(
            agentsDir,
            sp.GetRequiredService<SoulService>(),
            sp.GetRequiredService<ILogger<AgentProfileManager>>()));

        // Token budget & Prompt builder for Context Runtime
        services.AddSingleton(sp => new TokenBudget(maxTokens: 8000, recentTurnWindow: 6));
        services.AddSingleton(sp => new PromptBuilder(SystemPrompts.Default));

        // Context manager (with soul integration)
        services.AddSingleton(sp => new ContextManager(
            sp.GetRequiredService<TranscriptStore>(),
            sp.GetRequiredService<IChatClient>(),
            sp.GetRequiredService<TokenBudget>(),
            sp.GetRequiredService<PromptBuilder>(),
            sp.GetRequiredService<ILogger<ContextManager>>(),
            soulService: sp.GetRequiredService<SoulService>()));

        // MCP manager
        services.AddSingleton(sp => new McpManager(
            sp.GetRequiredService<ILogger<McpManager>>()));

        // Tool registry (shared across agent and subagents)
        services.AddSingleton<IToolRegistry, ToolRegistry>();

        // Plugin manager
        services.AddSingleton(sp =>
        {
            var pm = new PluginManager(sp.GetRequiredService<ILogger<PluginManager>>());
            pm.Register(new BuiltinMemoryPlugin(sp.GetRequiredService<MemoryManager>()));
            return pm;
        });

        // Analytics / Insights service
        var insightsDir = Path.Combine(projectDir, "analytics");
        services.AddSingleton(sp => new InsightsService(
            insightsDir,
            sp.GetRequiredService<ILogger<InsightsService>>()));

        // Dreamer (background free-association worker) — status for Dashboard; loop started post-build
        services.AddSingleton(_ => new DreamerStatus());

        // Core agent — wired with all optional dependencies
        services.AddSingleton(sp => new Agent(
            sp.GetRequiredService<IChatClient>(),
            sp.GetRequiredService<ILogger<Agent>>(),
            permissions: sp.GetRequiredService<PermissionManager>(),
            transcripts: sp.GetRequiredService<TranscriptStore>(),
            memories: sp.GetRequiredService<MemoryManager>(),
            contextManager: sp.GetRequiredService<ContextManager>(),
            soulService: sp.GetRequiredService<SoulService>(),
            pluginManager: sp.GetRequiredService<PluginManager>()));

        // Agent service (subagent spawning, worktree isolation)
        var worktreesDir = Path.Combine(projectDir, "worktrees");
        services.AddSingleton(sp => new AgentService(
            sp,
            sp.GetRequiredService<ILogger<AgentService>>(),
            sp.GetRequiredService<ILoggerFactory>(),
            sp.GetRequiredService<IChatClient>(),
            worktreesDir));

        // Coordinator service (multi-worker orchestration)
        var coordinatorStateDir = Path.Combine(projectDir, "coordinator");
        services.AddSingleton(sp => new CoordinatorService(
            sp.GetRequiredService<AgentService>(),
            sp.GetRequiredService<TaskManager>(),
            sp.GetRequiredService<ILogger<CoordinatorService>>(),
            sp.GetRequiredService<IChatClient>(),
            coordinatorStateDir));

        // Native C# gateway — no Python CLI required for Telegram/Discord
        services.AddSingleton(sp =>
        {
            var gatewayConfig = BuildGatewayConfig();
            return new GatewayService(gatewayConfig, sp.GetRequiredService<ILogger<GatewayService>>());
        });

        // Skill invoker (for slash command support)
        services.AddSingleton(sp => new Hermes.Agent.Skills.SkillInvoker(
            sp.GetRequiredService<Hermes.Agent.Skills.SkillManager>(),
            sp.GetRequiredService<IChatClient>(),
            sp.GetRequiredService<ILogger<Hermes.Agent.Skills.SkillInvoker>>()));

        // Chat service (pure C# — no sidecar)
        services.AddSingleton<HermesChatService>();
        services.AddSingleton<RuntimeStatusService>();

        var provider = services.BuildServiceProvider();
        Services = provider;

        // ── Post-build: Register all tools and connect MCP ──
        RegisterAllTools(provider);
        InitializeMcpAsync(provider, projectDir);

        // Wire permission prompt callback to show a ContentDialog in the UI
        WirePermissionCallback(provider);

        // Start native C# gateway if platform tokens are configured
        StartNativeGateway(provider);

        StartDreamerBackground(provider, hermesHome, projectDir);

        return provider;
    }

    /// <summary>
    /// Initializes Dreamer components and starts its continuous background loop.
    /// </summary>
    /// <param name="hermesHome">Path to the Hermes home directory (used for Dreamer layout and config).</param>
    /// <param name="projectDir">Path to the project directory (used to locate the transcripts directory).</param>
    /// <remarks>
    /// This method creates/uses long-lived HTTP clients, loads Dreamer configuration and room layout, constructs the DreamerService and related helpers, sets a static cancellation token source, and launches the Dreamer loop via a background task. Startup failures are non-fatal: exceptions are caught and written to debug output only.
    /// </remarks>
    private static void StartDreamerBackground(IServiceProvider provider, string hermesHome, string projectDir)
    {
        var logger = provider.GetService<ILogger<App>>();
        var dreamerStatus = provider.GetService<DreamerStatus>();
        var insights = provider.GetService<InsightsService>();

        try
        {
            var cfgPath = Path.Combine(hermesHome, "config.yaml");
            var lf = provider.GetRequiredService<ILoggerFactory>();
            var room = new DreamerRoom(hermesHome, lf.CreateLogger<DreamerRoom>());
            room.EnsureLayout();

            // Long-lived HttpClients for Dreamer. Do not attach logging handlers that record
            // request/response headers — LLM calls carry API keys on every request.
            // These clients are created with automatic decompression disabled, proxy usage
            // disabled, and sanitized default headers before any request-specific auth headers
            // are applied. Do not attach logging handlers that record request/response headers
            // or full requests.
            var dreamerHttpClients = GetOrCreateDreamerHttpClients();
            var walkHttp = dreamerHttpClients.Walk;
            var echoHttp = dreamerHttpClients.Echo;
            var rssHttp = dreamerHttpClients.Rss;

            // Factory methods to create fresh clients from current config
            IChatClient CreateWalkClient(DreamerConfig cfg) => new OpenAiClient(cfg.ToWalkLlmConfig(), walkHttp);
            IChatClient CreateEchoClient(DreamerConfig cfg) => new OpenAiClient(cfg.ToEchoLlmConfig(), echoHttp);

            var rss = new RssFetcher(rssHttp, room, lf.CreateLogger<RssFetcher>());
            var transcriptsDir = Path.Combine(projectDir, "transcripts");
            var dreamer = new DreamerService(
                hermesHome,
                cfgPath,
                transcriptsDir,
                room,
                CreateWalkClient,
                CreateEchoClient,
                provider.GetRequiredService<TranscriptStore>(),
                provider.GetRequiredService<GatewayService>(),
                provider.GetRequiredService<InsightsService>(),
                provider.GetRequiredService<DreamerStatus>(),
                rss,
                lf.CreateLogger<DreamerService>(),
                lf);

            var dreamerCts = new System.Threading.CancellationTokenSource();
            dreamerStatus?.ClearStartupFailure();
            SetDreamerCts(dreamerCts, logger, "starting Dreamer background loop");
            _ = Task.Run(async () =>
            {
                try
                {
                    await dreamer.RunForeverAsync(dreamerCts.Token);
                }
                finally
                {
                    TryCancelAndDisposeDreamerCtsIfCurrent(dreamerCts, logger, "Dreamer background loop exit");
                }
            });
        }
        catch (Exception ex)
        {
            dreamerStatus?.SetStartupFailure(ex.Message);
            insights?.RecordDreamerStartupFailure(ex);

            try
            {
                insights?.Save();
            }
            catch (Exception saveEx)
            {
                BestEffort.LogFailure(logger, saveEx, "persisting Dreamer startup failure insights");
            }

            if (logger is not null)
                logger.LogError(ex, "Dreamer background start failed");
            else
                BestEffort.LogFailure(null, ex, "starting Dreamer background loop");
        }
    }

    /// <summary>
    /// Wire the Agent's permission callback to show a WinUI ContentDialog when Ask is returned.
    /// Delegates UI construction to PermissionDialogService for separation of concerns.
    /// </summary>
    private static void WirePermissionCallback(IServiceProvider services)
    {
        var agent = services.GetRequiredService<Hermes.Agent.Core.Agent>();
        var permissionManager = services.GetRequiredService<PermissionManager>();
        var permissionStore = services.GetRequiredService<WorkspacePermissionRuleStore>();
        // Resolve the dialog service once; it captures the active window's
        // DispatcherQueue and XamlRoot internally and is safe to reuse across
        // many permission prompts. PermissionDialogService is the dedicated
        // dialog view + formatter extracted from this code-behind so this
        // file stays focused on app lifecycle / DI orchestration only —
        // see PermissionDialogService.cs for the construction logic plus the
        // dispatcher-shutdown deadlock guard.
        agent.PermissionPromptCallback = async (toolName, message, toolArguments) =>
        {
            if (App.Current is App app && app._window is not null)
            {
                var dialogService = new HermesDesktop.Services.PermissionDialogService(
                    app._window,
                    TryGetAppLogger());
                var decision = await dialogService.ShowPermissionDecisionAsync(message, toolName, toolArguments);
                switch (decision)
                {
                    case PermissionPromptDecision.AlwaysAllowTool:
                        // Persist for this workspace so repeated tool calls no
                        // longer prompt on future runs.
                        if (permissionManager.AddAlwaysAllowRule(toolName))
                        {
                            permissionStore.SaveAlwaysAllowRules(permissionManager.GetAlwaysAllowRulesSnapshot());
                        }
                        return true;

                    case PermissionPromptDecision.AllowOnce:
                        return true;

                    default:
                        return false;
                }
            }
            return false;
        };
    }

    /// <summary>
    /// Register all built-in tools with the Agent after DI is built.
    /// </summary>
    private static void RegisterAllTools(IServiceProvider services)
    {
        var agent = services.GetRequiredService<Agent>();
        var toolRegistry = services.GetRequiredService<IToolRegistry>();
        var httpClient = services.GetRequiredService<HttpClient>();
        var chatClient = services.GetRequiredService<IChatClient>();

        // File system tools (no constructor dependencies)
        RegisterAndTrack(agent, toolRegistry, new ReadFileTool());
        RegisterAndTrack(agent, toolRegistry, new WriteFileTool());
        RegisterAndTrack(agent, toolRegistry, new EditFileTool());
        RegisterAndTrack(agent, toolRegistry, new GlobTool());
        RegisterAndTrack(agent, toolRegistry, new GrepTool());

        // Shell execution tools
        RegisterAndTrack(agent, toolRegistry, new BashTool());
        RegisterAndTrack(agent, toolRegistry, new TerminalTool());

        // Web tools
        RegisterAndTrack(agent, toolRegistry, new WebFetchTool(httpClient));
        RegisterAndTrack(agent, toolRegistry, new WebSearchTool(
            new WebSearchConfig { Provider = "duckduckgo" }, httpClient));

        // Task management
        RegisterAndTrack(agent, toolRegistry, new TodoWriteTool());
        RegisterAndTrack(agent, toolRegistry, new ScheduleCronTool());

        // LSP tool (optional config)
        RegisterAndTrack(agent, toolRegistry, new LspTool());

        // Agent tool (subagent spawning — needs chat client and tool registry)
        RegisterAndTrack(agent, toolRegistry, new AgentTool(chatClient, toolRegistry));

        // Memory tool — delegates to MemoryManager so save/list/delete and the
        // auto-recall plugin operate on the same store and saved files always
        // carry the YAML frontmatter the scanner requires.
        var memoryManager = services.GetRequiredService<MemoryManager>();
        MigrateLegacyMemoriesIfNeeded(memoryManager.MemoryDir);
        RegisterAndTrack(agent, toolRegistry, new MemoryTool(memoryManager));

        // Session search tool
        var transcriptDir = Path.Combine(
            HermesEnvironment.HermesHomePath, "hermes-cs", "transcripts");
        RegisterAndTrack(agent, toolRegistry, new SessionSearchTool(transcriptDir));

        // Skill invoke tool
        var skillManager = services.GetRequiredService<SkillManager>();
        RegisterAndTrack(agent, toolRegistry, new SkillInvokeTool(skillManager));

        // Send message tool (wired to native C# gateway)
        var gateway = services.GetRequiredService<GatewayService>();
        RegisterAndTrack(agent, toolRegistry, new SendMessageTool(gateway));

        // Code sandbox tool
        RegisterAndTrack(agent, toolRegistry, new CodeSandboxTool());

        // Checkpoint tool
        var checkpointDir = Path.Combine(HermesEnvironment.HermesHomePath, "checkpoints");
        RegisterAndTrack(agent, toolRegistry, new CheckpointTool(checkpointDir));

        // Patch tool
        RegisterAndTrack(agent, toolRegistry, new PatchTool());
    }

    /// <summary>Register a tool with both the Agent and the shared IToolRegistry.</summary>
    private static void RegisterAndTrack(Agent agent, IToolRegistry registry, ITool tool)
    {
        agent.RegisterTool(tool);
        registry.RegisterTool(tool);
    }

    /// <summary>
    /// Pre-v2.5 the MemoryTool wrote to HermesHomePath/memories while the
    /// auto-recall plugin scanned HermesHomePath/hermes-cs/memory. If the old
    /// directory has files and the new one is empty, copy them so legacy
    /// memories remain accessible after the path alignment.
    /// </summary>
    private static void MigrateLegacyMemoriesIfNeeded(string currentMemoryDir)
    {
        try
        {
            var legacyDir = Path.Combine(HermesEnvironment.HermesHomePath, "memories");
            if (!Directory.Exists(legacyDir)) return;

            var legacyFiles = Directory.GetFiles(legacyDir, "*.md");
            if (legacyFiles.Length == 0) return;

            Directory.CreateDirectory(currentMemoryDir);
            if (Directory.EnumerateFileSystemEntries(currentMemoryDir).Any())
                return; // Current dir already populated; don't overwrite.

            foreach (var src in legacyFiles)
            {
                var dst = Path.Combine(currentMemoryDir, Path.GetFileName(src));
                if (!File.Exists(dst))
                    File.Copy(src, dst);
            }
        }
        catch (Exception ex)
        {
            BestEffort.LogFailure(TryGetAppLogger(), ex, "migrating legacy memory directory");
        }
    }

    /// <summary>Find the repo's skills/ directory by walking up from the build output to find .git or skills/.</summary>
    private static string? FindRepoSkillsDir()
    {
        // Walk up from build output to find the repo root (contains .git or skills/)
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10 && dir is not null; i++)
        {
            var skillsCandidate = Path.Combine(dir, "skills");
            if (Directory.Exists(skillsCandidate) && Directory.Exists(Path.Combine(dir, ".git")))
            {
                System.Diagnostics.Debug.WriteLine($"Found repo skills at: {skillsCandidate}");
                return skillsCandidate;
            }
            // Also check for skills/ without .git (user may have extracted without git)
            if (Directory.Exists(skillsCandidate) && Directory.EnumerateDirectories(skillsCandidate).Any())
            {
                System.Diagnostics.Debug.WriteLine($"Found skills dir at: {skillsCandidate}");
                return skillsCandidate;
            }
            dir = Path.GetDirectoryName(dir);
        }

        System.Diagnostics.Debug.WriteLine("Could not find bundled skills directory");
        return null;
    }

    /// <summary>Copy a directory tree recursively (used for first-run skill bundling).</summary>
    private static void CopyDirectoryRecursive(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            try { File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true); }
            catch (Exception ex)
            {
                BestEffort.LogFailure(TryGetAppLogger(), ex, "copying bundled skills file", $"file={file}");
            }
        }
        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            try { CopyDirectoryRecursive(dir, Path.Combine(destination, Path.GetFileName(dir))); }
            catch (Exception ex)
            {
                BestEffort.LogFailure(TryGetAppLogger(), ex, "copying bundled skills directory", $"directory={dir}");
            }
        }
    }

    /// <summary>
    /// Build gateway configuration from config.yaml platform tokens.
    /// </summary>
    private static GatewayConfig BuildGatewayConfig()
    {
        var config = new GatewayConfig();

        var telegramToken = HermesEnvironment.ReadPlatformSetting("telegram", "token");
        if (!string.IsNullOrWhiteSpace(telegramToken))
        {
            config.Platforms[Platform.Telegram] = new PlatformConfig
            {
                Enabled = true,
                Token = telegramToken
            };
        }

        var discordToken = HermesEnvironment.ReadPlatformSetting("discord", "token");
        if (!string.IsNullOrWhiteSpace(discordToken))
        {
            config.Platforms[Platform.Discord] = new PlatformConfig
            {
                Enabled = true,
                Token = discordToken
            };
        }

        return config;
    }

    /// <summary>
    /// Start the native C# gateway in the background if platform tokens are configured.
    /// Wires the agent as the message handler so incoming Telegram/Discord messages
    /// are processed by the Hermes agent.
    /// </summary>
    private static void StartNativeGateway(IServiceProvider services)
    {
        try
        {
            var gateway = services.GetRequiredService<GatewayService>();
            var agent = services.GetRequiredService<Agent>();
            var logger = services.GetRequiredService<ILogger<App>>();

            // Wire agent as the message handler
            gateway.SetAgentHandler(async (sessionId, userMessage, platform) =>
            {
                var session = new Session { Id = sessionId, Platform = platform };
                return await agent.ChatAsync(userMessage, session, CancellationToken.None);
            });

            // Create adapters for configured platforms
            var adapters = new List<IPlatformAdapter>();

            var tgToken = HermesEnvironment.ReadPlatformSetting("telegram", "token");
            if (!string.IsNullOrWhiteSpace(tgToken))
                adapters.Add(new TelegramAdapter(
                    tgToken,
                    logger: services.GetRequiredService<ILogger<TelegramAdapter>>()));

            var dcToken = HermesEnvironment.ReadPlatformSetting("discord", "token");
            if (!string.IsNullOrWhiteSpace(dcToken))
                adapters.Add(new DiscordAdapter(
                    dcToken,
                    logger: services.GetRequiredService<ILogger<DiscordAdapter>>()));

            if (adapters.Count > 0)
            {
                logger.LogInformation("Starting native gateway with {Count} platform(s)", adapters.Count);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await gateway.StartAsync(adapters, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Native gateway start failed");
                    }
                });
            }
            else
            {
                logger.LogDebug("No platform tokens configured — native gateway not started");
            }
        }
        catch (Exception ex)
        {
            BestEffort.LogFailure(TryGetAppLogger(), ex, "initializing native gateway");
        }
    }

    /// <summary>
    /// Load MCP server configs and connect (fire-and-forget on startup).
    /// <summary>
    /// Initializes the MCP subsystem by loading MCP configuration files from standard locations, connecting to configured MCP servers, and registering any discovered MCP tools with the Agent and tool registry.
    /// </summary>
    /// <param name="projectDir">Path to the project directory; used as one of the locations to search for an mcp.json configuration file.</param>
    /// <remarks>
    /// Initialization errors are non-fatal: exceptions are logged and startup continues without MCP tools.
    /// </remarks>
    private static async void InitializeMcpAsync(IServiceProvider services, string projectDir)
    {
        try
        {
            var mcpManager = services.GetRequiredService<McpManager>();
            var agent = services.GetRequiredService<Agent>();
            var toolRegistry = services.GetRequiredService<IToolRegistry>();

            // Check for MCP config in standard locations
            var mcpConfigPaths = new[]
            {
                Path.Combine(projectDir, "mcp.json"),
                Path.Combine(HermesEnvironment.HermesHomePath, "mcp.json"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".hermes", "mcp.json")
            };

            foreach (var configPath in mcpConfigPaths)
            {
                if (File.Exists(configPath))
                {
                    await mcpManager.LoadFromConfigAsync(configPath);
                }
            }

            // Connect to all configured servers
            await mcpManager.ConnectAllAsync();

            // Register discovered MCP tools with the Agent
            foreach (var mcpTool in mcpManager.Tools.Values)
            {
                agent.RegisterTool(mcpTool);
                toolRegistry.RegisterTool(mcpTool);
            }
        }
        catch (Exception ex)
        {
            // MCP initialization is non-critical — log and continue
            var logger = services.GetRequiredService<ILogger<App>>();
            logger.LogWarning(ex, "MCP initialization failed, continuing without MCP tools");
        }
    }

    private sealed class DreamerHttpClients : IDisposable
    {
        public DreamerHttpClients(HttpClient walk, HttpClient echo, HttpClient rss)
        {
            Walk = walk;
            Echo = echo;
            Rss = rss;
        }

        public HttpClient Walk { get; }
        public HttpClient Echo { get; }
        public HttpClient Rss { get; }

        public void Dispose()
        {
            DisposeClient(Walk);
            DisposeClient(Echo);
            DisposeClient(Rss);
        }

        private static void DisposeClient(HttpClient client)
        {
            client.CancelPendingRequests();
            client.Dispose();
        }
    }

    /// <summary>Sentinel <see cref="IServiceProvider"/> used only until DI is built in <see cref="OnLaunched"/>.</summary>
    private sealed class UninitializedAppServiceProvider : IServiceProvider
    {
        public static readonly UninitializedAppServiceProvider Instance = new();

        private UninitializedAppServiceProvider()
        {
        }

        public object? GetService(Type serviceType) => null;
    }
}