namespace Hermes.Agent.Execution;

// ══════════════════════════════════════════════
// Modal Serverless Execution Backend
// ══════════════════════════════════════════════
//
// Upstream ref: tools/environments/modal.py
// Modal requires Python SDK for proper sandbox management.
// This backend is a placeholder — use Docker or SSH instead.

/// <summary>
/// Modal serverless execution backend.
/// NOT YET IMPLEMENTED — Modal requires Python SDK integration.
/// Use Docker or SSH backends as alternatives.
/// </summary>
public sealed class ModalBackend : IExecutionBackend
{
    private readonly ExecutionConfig _config;

    public ModalBackend(ExecutionConfig config) => _config = config;
    public ExecutionBackendType Type => ExecutionBackendType.Modal;

    public Task<ExecutionResult> ExecuteAsync(
        string command, string? workingDirectory, int? timeoutMs,
        bool background, CancellationToken ct)
    {
        return Task.FromResult(new ExecutionResult
        {
            Output = "Modal backend is not yet implemented. " +
                     "Modal requires Python SDK (pip install modal) for proper sandbox management.\n\n" +
                     "Alternatives:\n" +
                     "  - Set backend to 'docker' for containerized execution\n" +
                     "  - Set backend to 'ssh' for remote execution\n" +
                     "  - Set backend to 'local' for direct execution",
            ExitCode = -1,
            DurationMs = 0
        });
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
