using Microsoft.UI.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Hermes.Agent.Core;
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
using Hermes.Agent.Plugins;
using Hermes.Agent.Soul;
using Hermes.Agent.Tools;
using HermesDesktop.Services;
using System;
using System.IO;
using System.Net.Http;

namespace HermesDesktop;

public partial class App : Application
{
    private Window? _window;

    /// <summary>Global service provider for DI — accessed by pages via App.Services.</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Services = ConfigureServices();
        _window = new MainWindow();
        _window.Activate();
    }

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

        services.AddSingleton<IChatClient>(sp =>
        {
            var config = sp.GetRequiredService<LlmConfig>();
            var http = sp.GetRequiredService<HttpClient>();
            var pool = sp.GetService<CredentialPool>(); // null if not configured
            return config.Provider?.ToLowerInvariant() switch
            {
                "anthropic" or "claude" => new AnthropicClient(config, http, pool),
                _ => new OpenAiClient(config, http, pool),
            };
        });

        // Hermes home directory
        var hermesHome = HermesEnvironment.HermesHomePath;
        var projectDir = Path.Combine(hermesHome, "hermes-cs");
        Directory.CreateDirectory(projectDir);

        // Transcript store
        var transcriptsDir = Path.Combine(projectDir, "transcripts");
        services.AddSingleton(sp => new TranscriptStore(transcriptsDir, eagerFlush: true));

        // Memory manager
        var memoryDir = Path.Combine(projectDir, "memory");
        services.AddSingleton(sp => new MemoryManager(
            memoryDir,
            sp.GetRequiredService<IChatClient>(),
            sp.GetRequiredService<ILogger<MemoryManager>>()));

        // Skill manager
        var skillsDir = Path.Combine(projectDir, "skills");
        services.AddSingleton(sp => new SkillManager(
            skillsDir,
            sp.GetRequiredService<ILogger<SkillManager>>()));

        // Permission manager
        services.AddSingleton(sp => new PermissionManager(
            new PermissionContext(),
            sp.GetRequiredService<ILogger<PermissionManager>>()));

        // Task manager
        var tasksDir = Path.Combine(projectDir, "tasks");
        services.AddSingleton(sp => new TaskManager(
            tasksDir,
            sp.GetRequiredService<ILogger<TaskManager>>()));

        // Buddy service
        var buddyDir = Path.Combine(projectDir, "buddy");
        services.AddSingleton(sp => new BuddyService(
            buddyDir,
            sp.GetRequiredService<IChatClient>()));

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

        // Skill invoker (for slash command support)
        services.AddSingleton(sp => new Hermes.Agent.Skills.SkillInvoker(
            sp.GetRequiredService<Hermes.Agent.Skills.SkillManager>(),
            sp.GetRequiredService<IChatClient>(),
            sp.GetRequiredService<ILogger<Hermes.Agent.Skills.SkillInvoker>>()));

        // Chat service (pure C# — no sidecar)
        services.AddSingleton<HermesChatService>();

        var provider = services.BuildServiceProvider();

        // ── Post-build: Register all tools and connect MCP ──
        RegisterAllTools(provider);
        InitializeMcpAsync(provider, projectDir);

        // Wire permission prompt callback to show a ContentDialog in the UI
        WirePermissionCallback(provider);

        return provider;
    }

    /// <summary>
    /// Wire the Agent's permission callback to show a WinUI ContentDialog when Ask is returned.
    /// </summary>
    private static void WirePermissionCallback(IServiceProvider services)
    {
        var agent = services.GetRequiredService<Hermes.Agent.Core.Agent>();
        agent.PermissionPromptCallback = async (toolName, message) =>
        {
            // Must dispatch to UI thread for ContentDialog
            var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();

            if (App.Current is App app && app._window is not null)
            {
                app._window.DispatcherQueue.TryEnqueue(async () =>
                {
                    try
                    {
                        var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
                        {
                            Title = $"Permission Required: {toolName}",
                            Content = message,
                            PrimaryButtonText = "Allow",
                            CloseButtonText = "Deny",
                            XamlRoot = app._window.Content.XamlRoot
                        };
                        var result = await dialog.ShowAsync();
                        tcs.TrySetResult(result == Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary);
                    }
                    catch
                    {
                        tcs.TrySetResult(false);
                    }
                });
            }
            else
            {
                tcs.TrySetResult(false);
            }

            return await tcs.Task;
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

        // Memory tool
        var memoryToolDir = Path.Combine(HermesEnvironment.HermesHomePath, "memories");
        RegisterAndTrack(agent, toolRegistry, new MemoryTool(memoryToolDir));

        // Session search tool
        var transcriptDir = Path.Combine(
            HermesEnvironment.HermesHomePath, "hermes-cs", "transcripts");
        RegisterAndTrack(agent, toolRegistry, new SessionSearchTool(transcriptDir));

        // Skill invoke tool
        var skillManager = services.GetRequiredService<SkillManager>();
        RegisterAndTrack(agent, toolRegistry, new SkillInvokeTool(skillManager));

        // Send message tool (stub — gateway integration pending)
        RegisterAndTrack(agent, toolRegistry, new SendMessageTool());

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
    /// Load MCP server configs and connect (fire-and-forget on startup).
    /// </summary>
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
}
