using Hermes.Agent.Commands;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HermesDesktop.Tests.Services;

[TestClass]
public sealed class CommandRegistryTests
{
    [TestMethod]
    public void Parse_SplitsSlashCommandAndArguments()
    {
        var parsed = CommandRegistry<object>.Parse("/model gpt-5.4 high");

        Assert.AreEqual("model", parsed.Name);
        Assert.AreEqual("gpt-5.4 high", parsed.Arguments);
    }

    [TestMethod]
    public async Task TryExecuteAsync_RoutesAliases()
    {
        var called = "";
        var registry = new CommandRegistry<object>()
            .Register(new RegisteredCommand<object>(
                "help",
                "Show help",
                (_, args, _) =>
                {
                    called = args;
                    return Task.CompletedTask;
                },
                aliases: ["skills"]));

        var executed = await registry.TryExecuteAsync("/skills now", new object(), CancellationToken.None);

        Assert.IsTrue(executed);
        Assert.AreEqual("now", called);
    }

    [TestMethod]
    public async Task TryExecuteAsync_ReturnsFalseForUnknownCommand()
    {
        var registry = new CommandRegistry<object>();

        var executed = await registry.TryExecuteAsync("/missing", new object(), CancellationToken.None);

        Assert.IsFalse(executed);
    }

    [TestMethod]
    public void FormatHelp_UsesRegisteredMetadata()
    {
        var registry = new CommandRegistry<object>()
            .Register(new RegisteredCommand<object>(
                "new",
                "Start a new chat",
                (_, _, _) => Task.CompletedTask,
                usage: "/new"));

        var help = registry.FormatHelp();

        StringAssert.Contains(help, "Available slash commands:");
        StringAssert.Contains(help, "/new - Start a new chat");
    }
}
