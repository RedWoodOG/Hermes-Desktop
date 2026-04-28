using Hermes.Agent.Context;
using Hermes.Agent.Core;
using Hermes.Agent.LLM;
using Hermes.Agent.Soul;
using Hermes.Agent.Transcript;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace HermesDesktop.Tests.Soul;

[TestClass]
public sealed class SoulSystemTests
{
    [TestMethod]
    public async Task SoulService_FirstRunDefault_UsesFullTemplateWithUnconfiguredMarker()
    {
        var temp = CreateTempDir();
        try
        {
            var service = new SoulService(temp, NullLogger<SoulService>.Instance);

            var soul = await service.LoadFileAsync(SoulFileType.Soul);
            var user = await service.LoadFileAsync(SoulFileType.User);

            StringAssert.Contains(soul, "<!-- UNCONFIGURED -->");
            StringAssert.Contains(soul, "# Hermes Agent Identity");
            StringAssert.Contains(user, "<!-- UNCONFIGURED -->");
            Assert.IsTrue(service.IsFirstRun());
        }
        finally
        {
            DeleteTempDir(temp);
        }
    }

    [TestMethod]
    public async Task GetRuntimeSoulNameAsync_UsesSoulFileAsRuntimeTruth()
    {
        var temp = CreateTempDir();
        var templates = CreateTempDir();
        var profiles = CreateTempDir();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(templates, "default.md"),
                "---\nname: Default\ndescription: Default soul\n---\n\n# Default Soul\nShip calmly.");

            var service = new SoulService(temp, NullLogger<SoulService>.Instance);
            var registry = new SoulRegistry([templates], NullLogger<SoulRegistry>.Instance);
            var manager = new AgentProfileManager(profiles, service, NullLogger<AgentProfileManager>.Instance);

            await service.SaveFileAsync(SoulFileType.Soul, "# Default Soul\nShip calmly.");
            Assert.AreEqual("Default", await service.GetRuntimeSoulNameAsync(registry, manager));

            var profile = new AgentProfile
            {
                Name = "Focused Builder",
                Description = "Test profile",
                SoulContent = "# Profile Soul\nBuild with care.",
                IsActive = false
            };
            await manager.SaveProfileAsync(profile);
            await manager.ActivateProfileAsync(profile);
            Assert.AreEqual("Focused Builder", await service.GetRuntimeSoulNameAsync(registry, manager));

            await service.SaveFileAsync(SoulFileType.Soul, "# Hand Edited\nNo template match.");
            Assert.AreEqual("Custom", await service.GetRuntimeSoulNameAsync(registry, manager));
        }
        finally
        {
            DeleteTempDir(temp);
            DeleteTempDir(templates);
            DeleteTempDir(profiles);
        }
    }

    [TestMethod]
    public async Task ContextManager_PrepareContext_IncludesProjectRules()
    {
        var hermesHome = CreateTempDir();
        var transcriptsDir = CreateTempDir();
        var projectDir = Path.Combine(CreateTempDir(), "HermesProject");
        try
        {
            Directory.CreateDirectory(projectDir);
            var service = new SoulService(hermesHome, NullLogger<SoulService>.Instance);
            await service.SaveFileAsync(SoulFileType.Soul, "# Runtime Soul\nUse the soul.");
            await service.SaveFileAsync(
                SoulFileType.ProjectRules,
                "# Project Rules\nAlways honor the project rule sentinel.",
                projectDir);

            var chatClient = new Mock<IChatClient>(MockBehavior.Strict);
            var manager = new ContextManager(
                new TranscriptStore(transcriptsDir, eagerFlush: true),
                chatClient.Object,
                new TokenBudget(maxTokens: 8000, recentTurnWindow: 6),
                new PromptBuilder("Stable system prompt"),
                NullLogger<ContextManager>.Instance,
                service,
                projectDir);

            var messages = await manager.PrepareContextAsync(
                "soul-project-rules",
                "hello",
                retrievedContext: null,
                CancellationToken.None);

            Assert.IsTrue(messages.Count > 0);
            Assert.AreEqual("system", messages[0].Role);
            StringAssert.Contains(messages[0].Content, "[Agent Identity]");
            StringAssert.Contains(messages[0].Content, "[Project Rules]");
            StringAssert.Contains(messages[0].Content, "project rule sentinel");
        }
        finally
        {
            DeleteTempDir(hermesHome);
            DeleteTempDir(transcriptsDir);
            DeleteTempDir(Path.GetDirectoryName(projectDir)!);
        }
    }

    [TestMethod]
    public async Task AgentProfileManager_ActivateProfile_AppliesPreferredModelAndProvider()
    {
        var hermesHome = CreateTempDir();
        var profiles = CreateTempDir();
        try
        {
            var factory = new ChatClientFactory(
                new LlmConfig
                {
                    Provider = "openai",
                    Model = "initial-model",
                    ApiKey = "test-key"
                },
                new HttpClient(),
                NullLogger<ChatClientFactory>.Instance);

            var service = new SoulService(hermesHome, NullLogger<SoulService>.Instance);
            var manager = new AgentProfileManager(profiles, service, NullLogger<AgentProfileManager>.Instance, factory);
            var profile = new AgentProfile
            {
                Name = "Claude Pair",
                Description = "Switches provider and model",
                SoulContent = "# Claude Pair\nPair thoughtfully.",
                PreferredProvider = "anthropic",
                PreferredModel = "claude-test",
                IsActive = false
            };

            await manager.SaveProfileAsync(profile);
            await manager.ActivateProfileAsync(profile);

            Assert.AreEqual("anthropic", factory.CurrentProvider);
            Assert.AreEqual("claude-test", factory.CurrentModel);
            Assert.AreEqual("# Claude Pair\nPair thoughtfully.", await service.LoadFileAsync(SoulFileType.Soul));
        }
        finally
        {
            DeleteTempDir(hermesHome);
            DeleteTempDir(profiles);
        }
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "hermes-soul-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDir(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }
}
