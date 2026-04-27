using System.Text.Json;
using Hermes.Agent.Mcp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HermesDesktop.Tests.Services;

[TestClass]
public class McpToolParametersTests
{
    [TestMethod]
    public void ToToolArguments_DirectArguments_UsesProviderNativeShape()
    {
        var parameters = JsonSerializer.Deserialize<McpToolParameters>(
            """{"path":"C:\\Temp\\example.txt","limit":10}""");

        var args = parameters!.ToToolArguments();

        Assert.IsTrue(args.HasValue);
        Assert.AreEqual("C:\\Temp\\example.txt", args.Value.GetProperty("path").GetString());
        Assert.AreEqual(10, args.Value.GetProperty("limit").GetInt32());
        Assert.IsFalse(args.Value.TryGetProperty("arguments", out _));
    }

    [TestMethod]
    public void ToToolArguments_BackCompatArgumentsWrapper_UnwrapsArguments()
    {
        var parameters = JsonSerializer.Deserialize<McpToolParameters>(
            """{"arguments":{"path":"C:\\Temp\\example.txt"}}""");

        var args = parameters!.ToToolArguments();

        Assert.IsTrue(args.HasValue);
        Assert.AreEqual("C:\\Temp\\example.txt", args.Value.GetProperty("path").GetString());
    }
}
