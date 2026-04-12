namespace Hermes.Agent.Diagnostics;

using System.Diagnostics;
using Microsoft.Extensions.Logging;

public static class BestEffort
{
    public static void Run(Action action, ILogger? logger, string operation, string? context = null)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            LogFailure(logger, ex, operation, context);
        }
    }

    public static T Run<T>(Func<T> action, T fallback, ILogger? logger, string operation, string? context = null)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            LogFailure(logger, ex, operation, context);
            return fallback;
        }
    }

    public static async Task RunAsync(Func<Task> action, ILogger? logger, string operation, string? context = null)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            LogFailure(logger, ex, operation, context);
        }
    }

    public static async Task<T> RunAsync<T>(Func<Task<T>> action, T fallback, ILogger? logger, string operation, string? context = null)
    {
        try
        {
            return await action();
        }
        catch (Exception ex)
        {
            LogFailure(logger, ex, operation, context);
            return fallback;
        }
    }

    public static void LogFailure(ILogger? logger, Exception ex, string operation, string? context = null)
    {
        if (logger is not null)
        {
            if (string.IsNullOrWhiteSpace(context))
                logger.LogWarning(ex, "Best-effort operation failed while {Operation}", operation);
            else
                logger.LogWarning(ex, "Best-effort operation failed while {Operation}. Context: {Context}", operation, context);

            return;
        }

        if (string.IsNullOrWhiteSpace(context))
            Debug.WriteLine($"Best-effort operation failed while {operation}: {ex}");
        else
            Debug.WriteLine($"Best-effort operation failed while {operation}. Context: {context}. Exception: {ex}");
    }
}
