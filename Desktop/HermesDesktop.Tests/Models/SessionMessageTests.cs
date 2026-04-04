using Hermes.Agent.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HermesDesktop.Tests.Models;

/// <summary>
/// Tests for Session and Message — the core data types consumed by HermesChatService
/// after the PR rewrote it to use direct in-process agent execution.
/// </summary>
[TestClass]
public class SessionTests
{
    // ── Construction ──

    [TestMethod]
    public void Session_DefaultProperties_AreInitialized()
    {
        var session = new Session { Id = "abc123", Platform = "desktop" };

        Assert.AreEqual("abc123", session.Id);
        Assert.AreEqual("desktop", session.Platform);
        Assert.IsNotNull(session.Messages);
        Assert.AreEqual(0, session.Messages.Count);
        Assert.IsNotNull(session.State);
    }

    [TestMethod]
    public void Session_CreatedAt_IsApproximatelyNow()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);
        var session = new Session { Id = "s1" };
        var after = DateTime.UtcNow.AddSeconds(1);

        Assert.IsTrue(session.CreatedAt >= before && session.CreatedAt <= after,
            $"CreatedAt {session.CreatedAt} should be between {before} and {after}");
    }

    [TestMethod]
    public void Session_WithNullPlatform_IsValid()
    {
        var session = new Session { Id = "noplat" };
        Assert.IsNull(session.Platform);
    }

    // ── AddMessage ──

    [TestMethod]
    public void AddMessage_SingleMessage_AppearsInMessages()
    {
        var session = new Session { Id = "s1" };
        var msg = new Message { Role = "user", Content = "Hello" };

        session.AddMessage(msg);

        Assert.AreEqual(1, session.Messages.Count);
        Assert.AreSame(msg, session.Messages[0]);
    }

    [TestMethod]
    public void AddMessage_MultipleMessages_PreservesOrder()
    {
        var session = new Session { Id = "s2" };
        var userMsg = new Message { Role = "user", Content = "Hi" };
        var assistantMsg = new Message { Role = "assistant", Content = "Hello!" };

        session.AddMessage(userMsg);
        session.AddMessage(assistantMsg);

        Assert.AreEqual(2, session.Messages.Count);
        Assert.AreEqual("user", session.Messages[0].Role);
        Assert.AreEqual("assistant", session.Messages[1].Role);
    }

    [TestMethod]
    public void AddMessage_UpdatesLastActivityAt()
    {
        var session = new Session { Id = "s3" };
        var originalActivity = session.LastActivityAt;

        // Small delay so timestamps differ
        System.Threading.Thread.Sleep(10);
        session.AddMessage(new Message { Role = "user", Content = "test" });

        Assert.IsTrue(session.LastActivityAt >= originalActivity,
            "LastActivityAt should be updated after AddMessage");
    }

    [TestMethod]
    public void AddMessage_EmptyContent_IsAccepted()
    {
        var session = new Session { Id = "s4" };

        session.AddMessage(new Message { Role = "system", Content = "" });

        Assert.AreEqual(1, session.Messages.Count);
        Assert.AreEqual("", session.Messages[0].Content);
    }

    [TestMethod]
    public void AddMessage_ToolRole_IsStoredCorrectly()
    {
        var session = new Session { Id = "s5" };
        var toolMsg = new Message
        {
            Role = "tool",
            Content = "result content",
            ToolCallId = "call-001",
            ToolName = "bash"
        };

        session.AddMessage(toolMsg);

        var stored = session.Messages[0];
        Assert.AreEqual("tool", stored.Role);
        Assert.AreEqual("call-001", stored.ToolCallId);
        Assert.AreEqual("bash", stored.ToolName);
    }

    // ── State dictionary ──

    [TestMethod]
    public void Session_State_CanStoreArbitraryValues()
    {
        var session = new Session { Id = "s6" };
        session.State["key"] = "value";
        session.State["count"] = 42;

        Assert.AreEqual("value", session.State["key"]);
        Assert.AreEqual(42, session.State["count"]);
    }
}

[TestClass]
public class MessageTests
{
    // ── Construction ──

    [TestMethod]
    public void Message_RequiredProperties_AreSet()
    {
        var msg = new Message { Role = "user", Content = "Hello world" };

        Assert.AreEqual("user", msg.Role);
        Assert.AreEqual("Hello world", msg.Content);
    }

    [TestMethod]
    public void Message_Timestamp_IsApproximatelyNow()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);
        var msg = new Message { Role = "user", Content = "test" };
        var after = DateTime.UtcNow.AddSeconds(1);

        Assert.IsTrue(msg.Timestamp >= before && msg.Timestamp <= after,
            $"Timestamp {msg.Timestamp} should be approximately now");
    }

    [TestMethod]
    public void Message_OptionalProperties_DefaultToNull()
    {
        var msg = new Message { Role = "user", Content = "x" };

        Assert.IsNull(msg.ToolCallId);
        Assert.IsNull(msg.ToolName);
        Assert.IsNull(msg.ToolCalls);
    }

    [TestMethod]
    public void Message_WithToolCallId_StoredCorrectly()
    {
        var msg = new Message
        {
            Role = "tool",
            Content = "result",
            ToolCallId = "call-42",
            ToolName = "read_file"
        };

        Assert.AreEqual("call-42", msg.ToolCallId);
        Assert.AreEqual("read_file", msg.ToolName);
    }

    [TestMethod]
    public void Message_WithToolCalls_StoredCorrectly()
    {
        var toolCalls = new List<ToolCall>
        {
            new() { Id = "tc1", Name = "bash", Arguments = "{\"command\":\"ls\"}" }
        };

        var msg = new Message
        {
            Role = "assistant",
            Content = "",
            ToolCalls = toolCalls
        };

        Assert.IsNotNull(msg.ToolCalls);
        Assert.AreEqual(1, msg.ToolCalls.Count);
        Assert.AreEqual("bash", msg.ToolCalls[0].Name);
    }

    [TestMethod]
    public void Message_Roles_AcceptAllAgentRoles()
    {
        // All roles used by HermesChatService
        var roles = new[] { "user", "assistant", "system", "tool" };

        foreach (var role in roles)
        {
            var msg = new Message { Role = role, Content = "content" };
            Assert.AreEqual(role, msg.Role);
        }
    }
}

[TestClass]
public class ToolResultTests
{
    [TestMethod]
    public void Ok_ReturnsSuccessWithContent()
    {
        var result = ToolResult.Ok("operation succeeded");

        Assert.IsTrue(result.Success);
        Assert.AreEqual("operation succeeded", result.Content);
        Assert.IsNull(result.Error);
    }

    [TestMethod]
    public void Fail_ReturnsFalseWithError()
    {
        var result = ToolResult.Fail("something went wrong");

        Assert.IsFalse(result.Success);
        Assert.AreEqual("something went wrong", result.Content);
    }

    [TestMethod]
    public void Fail_WithException_StoresException()
    {
        var ex = new InvalidOperationException("inner error");
        var result = ToolResult.Fail("failed with exception", ex);

        Assert.IsFalse(result.Success);
        Assert.AreSame(ex, result.Error);
    }

    [TestMethod]
    public void Ok_EmptyContent_IsValid()
    {
        var result = ToolResult.Ok("");

        Assert.IsTrue(result.Success);
        Assert.AreEqual("", result.Content);
    }
}

[TestClass]
public class ChatResponseTests
{
    [TestMethod]
    public void HasToolCalls_WithNullToolCalls_ReturnsFalse()
    {
        var response = new ChatResponse { Content = "text", ToolCalls = null };

        Assert.IsFalse(response.HasToolCalls);
    }

    [TestMethod]
    public void HasToolCalls_WithEmptyList_ReturnsFalse()
    {
        var response = new ChatResponse { Content = "text", ToolCalls = new List<ToolCall>() };

        Assert.IsFalse(response.HasToolCalls);
    }

    [TestMethod]
    public void HasToolCalls_WithOneTool_ReturnsTrue()
    {
        var response = new ChatResponse
        {
            Content = null,
            ToolCalls = new List<ToolCall>
            {
                new() { Id = "tc1", Name = "bash", Arguments = "{}" }
            }
        };

        Assert.IsTrue(response.HasToolCalls);
    }

    [TestMethod]
    public void HasToolCalls_WithMultipleTools_ReturnsTrue()
    {
        var tools = Enumerable.Range(1, 3).Select(i =>
            new ToolCall { Id = $"tc{i}", Name = $"tool{i}", Arguments = "{}" }).ToList();

        var response = new ChatResponse { ToolCalls = tools };

        Assert.IsTrue(response.HasToolCalls);
        Assert.AreEqual(3, response.ToolCalls!.Count);
    }
}