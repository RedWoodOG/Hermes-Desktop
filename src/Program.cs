using System;
using Hermes.Agent.Core;
using Hermes.Agent.LLM;
using Hermes.Agent.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using var shutdownCts = new CancellationTokenSource();

ConsoleCancelEventHandler cancelHandler = (_, e) =>
{
    if (shutdownCts.IsCancellationRequested)
    {
        return;
    }

    e.Cancel = true;
    shutdownCts.Cancel();
};

Console.CancelKeyPress += cancelHandler;

try
{
    var rootCommand = new RootCommand("Hermes.C# AI Agent CLI");
    var messageArgument = new Argument<string>("message")
    {
        Description = "Message to send",
    };

    var chatCommand = new Command("chat", "Send a message to Hermes")
    {
        messageArgument,
    };

    chatCommand.SetAction(parseResult =>
    {
        Environment.ExitCode = InvokeChatAsync(parseResult, messageArgument, shutdownCts.Token).GetAwaiter().GetResult();
    });

    rootCommand.Subcommands.Add(chatCommand);

    var parseResult = rootCommand.Parse(args);
    var exitCode = await parseResult.InvokeAsync();
    return shutdownCts.IsCancellationRequested ? 130 : (Environment.ExitCode != 0 ? Environment.ExitCode : exitCode);
}
catch (OperationCanceledException) when (shutdownCts.IsCancellationRequested)
{
    return 130;
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
}

static async Task<int> InvokeChatAsync(
    ParseResult parseResult,
    Argument<string> messageArgument,
    CancellationToken cancellationToken)
{
    cancellationToken.ThrowIfCancellationRequested();

    var message = parseResult.GetValue(messageArgument);
    if (string.IsNullOrWhiteSpace(message))
    {
        Console.Error.WriteLine("message is required.");
        return 1;
    }

    var services = new ServiceCollection();

    services.AddLogging(builder =>
    {
        builder.AddConsole();
        builder.SetMinimumLevel(LogLevel.Information);
    });

    // TODO: Load config from ~/.hermes-cs/config.yaml
    var config = new LlmConfig
    {
        Provider = "custom",
        Model = "minimax-m2.7:cloud",
        BaseUrl = "http://127.0.0.1:11434/v1",
        ApiKey = "no-key-required"
    };

    services.AddSingleton<IChatClient>(new OpenAiClient(config, new HttpClient()));
    services.AddSingleton<IAgent, Agent>();
    services.AddSingleton<ITool, TerminalTool>();

    using var serviceProvider = services.BuildServiceProvider();
    var agent = serviceProvider.GetRequiredService<IAgent>();
    var logger = serviceProvider.GetRequiredService<ILogger<Program>>();

    var session = new Session
    {
        Id = Guid.NewGuid().ToString(),
        Platform = "cli",
        UserId = Environment.UserName,
    };

    foreach (var tool in serviceProvider.GetServices<ITool>())
        agent.RegisterTool(tool);

    try
    {
        logger.LogInformation("Sending message: {Message}", message);
        var response = await agent.ChatAsync(message, session, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        Console.WriteLine(response);
        return 0;
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        throw;
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Chat failed");
        return 1;
    }
}
