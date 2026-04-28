using System.Runtime.CompilerServices;
using Hermes.Agent.Core;
using Hermes.Agent.LLM;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HermesDesktop.Tests.LLM;

/// <summary>
/// Tests the default-interface-method bridge that converts SystemContext +
/// system-free conversation into the legacy single-leading-system-message
/// shape that strict OpenAI-compatible servers (vLLM-Qwen, llama.cpp strict
/// templates, TGI, LMStudio) require.
///
/// The contract under test: any call through the new IChatClient overloads
/// that take SystemContext must result in the legacy provider methods being
/// invoked with at most one system message, located at index 0, and zero
/// system messages anywhere else in the list.
/// </summary>
[TestClass]
public class IChatClientBridgeTests
{
    /// <summary>
    /// Test double that records exactly what messages the bridge passes to
    /// the legacy provider entry points. Implements only the legacy methods;
    /// the new SystemContext-aware methods come from the interface DIM.
    /// </summary>
    private sealed class RecordingChatClient : IChatClient
    {
        public List<Message>? LastMessages { get; private set; }

        public Task<string> CompleteAsync(IEnumerable<Message> messages, CancellationToken ct)
        {
            LastMessages = messages.ToList();
            return Task.FromResult("ok");
        }

        public Task<ChatResponse> CompleteWithToolsAsync(
            IEnumerable<Message> messages,
            IEnumerable<ToolDefinition> tools,
            CancellationToken ct)
        {
            LastMessages = messages.ToList();
            return Task.FromResult(new ChatResponse { Content = "ok" });
        }

        public IAsyncEnumerable<string> StreamAsync(IEnumerable<Message> messages, CancellationToken ct)
            => throw new NotImplementedException();

        public async IAsyncEnumerable<StreamEvent> StreamAsync(
            string? systemPrompt,
            IEnumerable<Message> messages,
            IEnumerable<ToolDefinition>? tools = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            LastMessages = messages.ToList();
            await Task.CompletedTask;
            yield break;
        }
    }

    private static void AssertContractHolds(List<Message>? observed)
    {
        Assert.IsNotNull(observed, "Recording client did not capture any messages.");

        // The load-bearing contract: zero system messages anywhere except possibly index 0.
        for (int i = 1; i < observed!.Count; i++)
        {
            Assert.AreNotEqual(
                "system",
                observed[i].Role,
                $"Mid-list system message at index {i} would break strict OpenAI-compatible servers.");
        }
    }

    // ── CompleteWithToolsAsync (SystemContext overload) ──

    [TestMethod]
    public async Task CompleteWithToolsAsync_NonEmptySystem_EmitsSingleLeadingSystem()
    {
        var client = new RecordingChatClient();
        IChatClient ic = client;
        var sys = new SystemContext { Soul = "soul", Wiki = "wiki" };
        var conv = new[]
        {
            new Message { Role = "user", Content = "hi" },
            new Message { Role = "assistant", Content = "ok" }
        };

        await ic.CompleteWithToolsAsync(sys, conv, Array.Empty<ToolDefinition>(), default);

        var observed = client.LastMessages!;
        Assert.AreEqual(3, observed.Count);
        Assert.AreEqual("system", observed[0].Role);
        StringAssert.Contains(observed[0].Content, "soul");
        StringAssert.Contains(observed[0].Content, "wiki");
        AssertContractHolds(observed);
    }

    [TestMethod]
    public async Task CompleteWithToolsAsync_EmptySystem_PassesConversationThrough()
    {
        var client = new RecordingChatClient();
        IChatClient ic = client;
        var conv = new[]
        {
            new Message { Role = "user", Content = "hi" }
        };

        await ic.CompleteWithToolsAsync(SystemContext.Empty, conv, Array.Empty<ToolDefinition>(), default);

        var observed = client.LastMessages!;
        Assert.AreEqual(1, observed.Count);
        Assert.AreEqual("user", observed[0].Role);
        AssertContractHolds(observed);
    }

    [TestMethod]
    public async Task CompleteWithToolsAsync_PreservesConversationOrder()
    {
        var client = new RecordingChatClient();
        IChatClient ic = client;
        var sys = new SystemContext { Persona = "p" };
        var conv = new[]
        {
            new Message { Role = "user", Content = "a" },
            new Message { Role = "assistant", Content = "b" },
            new Message { Role = "tool", Content = "c", ToolCallId = "t1" },
            new Message { Role = "user", Content = "d" }
        };

        await ic.CompleteWithToolsAsync(sys, conv, Array.Empty<ToolDefinition>(), default);

        var observed = client.LastMessages!;
        Assert.AreEqual("system", observed[0].Role);
        CollectionAssert.AreEqual(
            new[] { "a", "b", "c", "d" },
            observed.Skip(1).Select(m => m.Content).ToArray());
        AssertContractHolds(observed);
    }

    // ── StreamAsync (SystemContext overload) ──

    [TestMethod]
    public async Task StreamAsync_NonEmptySystem_EmitsSingleLeadingSystem()
    {
        var client = new RecordingChatClient();
        IChatClient ic = client;
        var sys = new SystemContext { Soul = "soul", Memory = "mem" };
        var conv = new[]
        {
            new Message { Role = "user", Content = "hi" }
        };

        await foreach (var _ in ic.StreamAsync(sys, conv, null, default))
        {
            // drain
        }

        var observed = client.LastMessages!;
        Assert.AreEqual(2, observed.Count);
        Assert.AreEqual("system", observed[0].Role);
        StringAssert.Contains(observed[0].Content, "soul");
        StringAssert.Contains(observed[0].Content, "mem");
        AssertContractHolds(observed);
    }

    [TestMethod]
    public async Task StreamAsync_EmptySystem_OmitsLeadingSystem()
    {
        var client = new RecordingChatClient();
        IChatClient ic = client;
        var conv = new[]
        {
            new Message { Role = "user", Content = "hi" }
        };

        await foreach (var _ in ic.StreamAsync(SystemContext.Empty, conv, null, default))
        {
            // drain
        }

        var observed = client.LastMessages!;
        Assert.AreEqual(1, observed.Count);
        Assert.AreEqual("user", observed[0].Role);
        AssertContractHolds(observed);
    }

    // ── Critical regression test: the exact bug shape ──

    [TestMethod]
    public async Task StreamAsync_LegacyMidConversationSystem_StillCoalescedAfterMigration()
    {
        // Simulates the bug shape from the wild: a caller (post-migration)
        // accidentally hands a conversation that already contains a system
        // message embedded mid-list. FromLegacyMessages cleans it up; the
        // bridge then ensures only the leading coalesced system reaches the
        // provider. Strict servers stay happy.
        var client = new RecordingChatClient();
        IChatClient ic = client;

        var dirtyMessages = new[]
        {
            new Message { Role = "system", Content = "soul" },
            new Message { Role = "user", Content = "first" },
            new Message { Role = "assistant", Content = "ok" },
            new Message { Role = "system", Content = "stray" }, // the bug shape
            new Message { Role = "user", Content = "second" }
        };

        var (sys, conv) = SystemContext.FromLegacyMessages(dirtyMessages);

        await foreach (var _ in ic.StreamAsync(sys, conv, null, default))
        {
            // drain
        }

        var observed = client.LastMessages!;
        Assert.AreEqual("system", observed[0].Role);
        StringAssert.Contains(observed[0].Content, "soul");
        StringAssert.Contains(observed[0].Content, "stray");
        AssertContractHolds(observed);
    }
}
