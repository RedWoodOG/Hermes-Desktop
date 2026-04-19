using System.Text.Json;
using Hermes.Agent.Buddy;
using Hermes.Agent.Core;
using Hermes.Agent.LLM;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HermesDesktop.Tests.Services;

/// <summary>Minimal <see cref="IChatClient"/> for buddy tests (avoids Moq in guardrail diff).</summary>
internal sealed class BuddyTestChatClient : IChatClient
{
    private readonly Func<CancellationToken, Task<string>> _completeAsync;

    public BuddyTestChatClient(string response)
        : this(_ => Task.FromResult(response))
    {
    }

    public BuddyTestChatClient(Func<CancellationToken, Task<string>> completeAsync)
    {
        _completeAsync = completeAsync;
    }

    public Task<string> CompleteAsync(IEnumerable<Message> messages, CancellationToken ct) =>
        _completeAsync(ct);

    public Task<ChatResponse> CompleteWithToolsAsync(
        IEnumerable<Message> messages,
        IEnumerable<ToolDefinition> tools,
        CancellationToken ct) =>
        throw new NotImplementedException();

    public IAsyncEnumerable<string> StreamAsync(IEnumerable<Message> messages, CancellationToken ct) =>
        throw new NotImplementedException();

    public IAsyncEnumerable<StreamEvent> StreamAsync(
        string? systemPrompt,
        IEnumerable<Message> messages,
        IEnumerable<ToolDefinition>? tools = null,
        CancellationToken ct = default) =>
        throw new NotImplementedException();
}

[TestClass]
public class BuddyServiceTests
{
    private sealed class SimulatedNetworkFailure : Exception
    {
        public SimulatedNetworkFailure() : base("network")
        {
        }
    }

    [TestMethod]
    public void BuddyGenerator_ForcedSpecies_Cat_IsUncommon()
    {
        var b = new BuddyGenerator("test-user", "Cat").Generate();
        Assert.AreEqual("Cat", b.Species);
        Assert.AreEqual(BuddyRarity.Uncommon, b.Rarity);
    }

    [TestMethod]
    public void BuddyGenerator_SurpriseRoll_IsDeterministicPerUser()
    {
        var a = new BuddyGenerator("same").Generate();
        var b = new BuddyGenerator("same").Generate();
        Assert.AreEqual(a.Species, b.Species);
        Assert.AreEqual(a.Rarity, b.Rarity);
        Assert.AreEqual(a.Eyes, b.Eyes);
    }

    [TestMethod]
    public async Task BuddyService_PersistsToJsonFile_AndReloadsWithoutLlm()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hermes-buddy-test-" + Guid.NewGuid().ToString("n"));
        var path = Path.Combine(dir, "buddy.json");
        Directory.CreateDirectory(dir);
        var completeCount = 0;
        try
        {
            var chat = new BuddyTestChatClient(ct =>
            {
                completeCount++;
                return Task.FromResult("NAME: Moss\nPERSONALITY: Quiet and curious.");
            });

            var svc1 = new BuddyService(path, chat);
            Assert.IsFalse(svc1.HasSavedBuddy);

            var buddy = await svc1.GetBuddyAsync("win-user", "Blob", CancellationToken.None);
            Assert.AreEqual("Blob", buddy.Species);
            Assert.AreEqual("Moss", buddy.Name);
            Assert.IsTrue(svc1.HasSavedBuddy);

            var svc2 = new BuddyService(path, chat);
            var reloaded = await svc2.GetBuddyAsync("someone-else", CancellationToken.None);
            // Stored user id wins for generation; first save used win-user
            Assert.AreEqual("Blob", reloaded.Species);
            Assert.AreEqual("Moss", reloaded.Name);
            Assert.AreEqual(1, completeCount);
        }
        finally
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }

    [TestMethod]
    public async Task BuddyService_LlmFailure_StillProducesBuddyAndSaves()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hermes-buddy-fail-" + Guid.NewGuid().ToString("n"));
        var path = Path.Combine(dir, "buddy.json");
        Directory.CreateDirectory(dir);
        try
        {
            var chat = new BuddyTestChatClient(_ =>
                Task.FromException<string>(new SimulatedNetworkFailure()));

            var svc = new BuddyService(path, chat);
            var buddy = await svc.GetBuddyAsync("u-fail", "Dot", CancellationToken.None);
            Assert.IsFalse(string.IsNullOrWhiteSpace(buddy.Name));
            Assert.IsFalse(string.IsNullOrWhiteSpace(buddy.Personality));
            Assert.IsTrue(File.Exists(path));

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var root = doc.RootElement;
            Assert.AreEqual("u-fail", root.GetProperty("UserId").GetString());
            Assert.AreEqual("Dot", root.GetProperty("ChosenSpecies").GetString());
        }
        finally
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch
            {
            }
        }
    }
}
