using Hermes.Agent.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HermesDesktop.Tests.Services;

[TestClass]
public sealed class McpConfigCasingTests
{
    [TestMethod]
    public async Task LoadFromConfigAsync_CamelCaseMcpServers_LoadsConfigs()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hermes-mcp-config-" + Guid.NewGuid().ToString("n"));
        var path = Path.Combine(dir, "mcp.json");
        Directory.CreateDirectory(dir);

        try
        {
            await File.WriteAllTextAsync(
                path,
                """
                {
                  "mcpServers": {
                    "example": {
                      "command": "node",
                      "args": ["server.js"],
                      "env": { "FOO": "bar" }
                    }
                  }
                }
                """);

            var manager = new McpManager(NullLogger<McpManager>.Instance);

            await manager.LoadFromConfigAsync(path);

            Assert.AreEqual(1, manager.ConfiguredServerCount);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}
