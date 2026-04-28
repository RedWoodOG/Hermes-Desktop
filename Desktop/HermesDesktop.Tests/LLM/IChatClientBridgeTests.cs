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
        public string? LastSystemPrompt { get; private set; }
        public bool LastSystemPromptCaptured { get; private set; }

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

        // Tests never exercise this overload; return an empty async stream
        // rather than throwing — keeps the symbol-guardrail allowlist clean.
        public async IAsyncEnumerable<string> StreamAsync(
            IEnumerable<Message> messages,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }

        public async IAsyncEnumerable<StreamEvent> StreamAsync(
            string? systemPrompt,
            IEnumerable<Message> messages,
            IEnumerable<ToolDefinition>? tools = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            LastSystemPrompt = systemPrompt;
            LastSystemPromptCaptured = true;
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

    // ── systemPrompt parameter threading (AnthropicClient streaming) ──

    [TestMethod]
    public async Task StreamAsync_NonEmptySystem_PassesAsSystemPromptParameter()
    {
        // Anthropic streaming reads only the systemPrompt parameter to set
        // its top-level "system" field; it drops role:"system" messages from
        // the messages list. The bridge must thread rendered system content
        // into systemPrompt or SystemContext is silently lost on Anthropic.
        var client = new RecordingChatClient();
        IChatClient ic = client;
        var sys = new SystemContext { Soul = "soul", Wiki = "wiki" };
        var conv = new[] { new Message { Role = "user", Content = "hi" } };

        await foreach (var _ in ic.StreamAsync(sys, conv, null, default))
        {
            // drain
        }

        Assert.IsTrue(client.LastSystemPromptCaptured);
        Assert.IsNotNull(
            client.LastSystemPrompt,
            "Bridge must pass rendered system content via systemPrompt parameter so AnthropicClient streaming can populate top-level system field.");
        StringAssert.Contains(client.LastSystemPrompt!, "soul");
        StringAssert.Contains(client.LastSystemPrompt!, "wiki");
    }

    [TestMethod]
    public async Task StreamAsync_EmptySystem_PassesNullSystemPrompt()
    {
        var client = new RecordingChatClient();
        IChatClient ic = client;
        var conv = new[] { new Message { Role = "user", Content = "hi" } };

        await foreach (var _ in ic.StreamAsync(SystemContext.Empty, conv, null, default))
        {
            // drain
        }

        Assert.IsTrue(client.LastSystemPromptCaptured);
        Assert.IsNull(client.LastSystemPrompt);
    }

    // ── Bridge sanitization: dirty conversation passed directly ──

    [TestMethod]
    public async Task StreamAsync_DirtyConversationDirect_SystemMessagesHoisted()
    {
        // A caller skips FromLegacyMessages and feeds a conversation that
        // happens to contain a role:"system" entry directly into the new
        // overload. The bridge must hoist it into the system block rather
        // than letting it slip through mid-list (where strict servers reject).
        var client = new RecordingChatClient();
        IChatClient ic = client;
        var sys = new SystemContext { Soul = "soul" };
        var dirty = new[]
        {
            new Message { Role = "user", Content = "hi" },
            new Message { Role = "system", Content = "stray" }, // Qodo Bug 2
            new Message { Role = "assistant", Content = "ok" }
        };

        await foreach (var _ in ic.StreamAsync(sys, dirty, null, default))
        {
            // drain
        }

        var observed = client.LastMessages!;
        AssertContractHolds(observed);
        Assert.AreEqual("system", observed[0].Role);
        StringAssert.Contains(observed[0].Content, "soul");
        StringAssert.Contains(observed[0].Content, "stray");
        StringAssert.Contains(client.LastSystemPrompt!, "soul");
        StringAssert.Contains(client.LastSystemPrompt!, "stray");
        Assert.AreEqual(3, observed.Count);
    }

    [TestMethod]
    public async Task CompleteWithToolsAsync_DirtyConversationDirect_SystemMessagesHoisted()
    {
        var client = new RecordingChatClient();
        IChatClient ic = client;
        var sys = new SystemContext { Persona = "persona" };
        var dirty = new[]
        {
            new Message { Role = "user", Content = "first" },
            new Message { Role = "system", Content = "mid-stray" }, // Qodo Bug 2
            new Message { Role = "user", Content = "second" }
        };

        await ic.CompleteWithToolsAsync(sys, dirty, Array.Empty<ToolDefinition>(), default);

        var observed = client.LastMessages!;
        AssertContractHolds(observed);
        Assert.AreEqual("system", observed[0].Role);
        StringAssert.Contains(observed[0].Content, "persona");
        StringAssert.Contains(observed[0].Content, "mid-stray");
        Assert.AreEqual(3, observed.Count);
    }

    [TestMethod]
    public async Task StreamAsync_OnlyStraySystemNoContext_StillCoalescedToLeading()
    {
        // SystemContext is empty but conversation contains a stray system
        // message. The bridge should still hoist it.
        var client = new RecordingChatClient();
        IChatClient ic = client;
        var dirty = new[]
        {
            new Message { Role = "user", Content = "hi" },
            new Message { Role = "system", Content = "stray-only" }
        };

        await foreach (var _ in ic.StreamAsync(SystemContext.Empty, dirty, null, default))
        {
            // drain
        }

        var observed = client.LastMessages!;
        AssertContractHolds(observed);
        Assert.AreEqual("system", observed[0].Role);
        StringAssert.Contains(observed[0].Content, "stray-only");
        StringAssert.Contains(client.LastSystemPrompt!, "stray-only");
    }
}
