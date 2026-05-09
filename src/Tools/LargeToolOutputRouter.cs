namespace Hermes.Agent.Tools;

using Hermes.Agent.Core;
using Hermes.Agent.Security;
using System.Security;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Routes oversized tool output into artifacts before it pollutes the model context.
/// Adapted from DeepSeek-TUI's large-output router, with deterministic local
/// summarization so every tool benefits without an extra model call.
/// </summary>
public sealed class LargeToolOutputRouter
{
    public const int DefaultThresholdTokens = 4096;
    private const int CharsPerTokenEstimate = 3;
    private const int SummaryHeadChars = 2200;
    private const int SummaryTailChars = 2200;

    private readonly string _artifactDir;
    private readonly int _thresholdTokens;
    private readonly Dictionary<string, int> _perToolThresholds;

    public LargeToolOutputRouter(
        string? artifactDir = null,
        int thresholdTokens = DefaultThresholdTokens,
        IReadOnlyDictionary<string, int>? perToolThresholds = null)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _artifactDir = artifactDir ?? Path.Combine(localAppData, "hermes", "tool_outputs");
        _thresholdTokens = Math.Max(1, thresholdTokens);
        _perToolThresholds = perToolThresholds is null
            ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, int>(perToolThresholds, StringComparer.OrdinalIgnoreCase);
    }

    public ToolResult Route(string toolName, ToolResult result)
    {
        if (!result.Success || string.IsNullOrEmpty(result.Content))
            return result;

        var content = SecretScanner.ContainsSecrets(result.Content)
            ? SecretScanner.RedactSecrets(result.Content)
            : result.Content;

        var threshold = ThresholdFor(toolName);
        var estimatedTokens = EstimateTokens(content);
        if (estimatedTokens <= threshold)
            return ReferenceEquals(content, result.Content) ? result : ToolResult.Ok(content);

        try
        {
            Directory.CreateDirectory(_artifactDir);
            var artifactPath = WriteArtifact(toolName, content);
            var summary = Summarize(toolName, content, estimatedTokens, threshold, artifactPath);
            return ToolResult.Ok(summary);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return ToolResult.Ok(SummarizeWithoutArtifact(toolName, content, estimatedTokens, threshold, ex.Message));
        }
    }

    public int ThresholdFor(string toolName) =>
        _perToolThresholds.TryGetValue(toolName, out var value) ? value : _thresholdTokens;

    public static int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        return (text.Length + CharsPerTokenEstimate - 1) / CharsPerTokenEstimate;
    }

    private string WriteArtifact(string toolName, string content)
    {
        var safeTool = string.Concat(toolName.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_'));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))[..12].ToLowerInvariant();
        var path = Path.Combine(_artifactDir, $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{safeTool}_{hash}.txt");
        File.WriteAllText(path, content, Encoding.UTF8);
        return path;
    }

    private static string Summarize(string toolName, string content, int estimatedTokens, int threshold, string artifactPath)
    {
        var headLength = Math.Min(SummaryHeadChars, content.Length);
        var tailLength = Math.Min(SummaryTailChars, Math.Max(0, content.Length - headLength));
        var head = content[..headLength];
        var tail = tailLength > 0 ? content[^tailLength..] : "";

        var omittedChars = Math.Max(0, content.Length - headLength - tailLength);
        var sb = new StringBuilder();
        sb.AppendLine($"[large-tool-output: tool={toolName}, estimated_tokens={estimatedTokens}, threshold={threshold}]");
        sb.AppendLine($"Raw output saved to: {artifactPath}");
        sb.AppendLine();
        sb.AppendLine("<head>");
        sb.AppendLine(head.TrimEnd());
        sb.AppendLine("</head>");
        if (omittedChars > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"... omitted {omittedChars} chars from middle ...");
            sb.AppendLine();
            sb.AppendLine("<tail>");
            sb.AppendLine(tail.TrimStart());
            sb.AppendLine("</tail>");
        }

        return sb.ToString();
    }

    private static string SummarizeWithoutArtifact(
        string toolName,
        string content,
        int estimatedTokens,
        int threshold,
        string artifactError)
    {
        var summary = Summarize(toolName, content, estimatedTokens, threshold, "<artifact write failed>");
        return summary + $"\nArtifact write failed: {artifactError}";
    }
}
