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
using HermesDesktop.Diagnostics;
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

        // Logging
        services.AddLogging(builder => builder.AddDebug().SetMinimumLevel(LogLevel.Information));

        // LLM config from environment/config.yaml
        var llmConfig = HermesEnvironment.CreateLlmConfig();
        services.AddSingleton(llmConfig);
        services.AddSingleton<HttpClient>();
        services.AddSingleton<IChatClient>(sp =>
            new OpenAiClient(sp.GetRequiredService<LlmConfig>(), sp.GetRequiredService<HttpClient>()));

        // Core agent
        services.AddSingleton<Agent>();

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

        // Chat service (pure C# — no sidecar)
        services.AddSingleton<HermesChatService>();

        var provider = services.BuildServiceProvider();
        #region agent log
        try
        {
            var agent = provider.GetRequiredService<Agent>();
            DesktopDebugLog.Write("H3", "App.xaml.cs:ConfigureServices", "startup_agent_tool_count",
                new { toolCount = agent.Tools.Count });
        }
        catch
        {
            /* debug ingest only */
        }
        #endregion
        return provider;
    }
}
