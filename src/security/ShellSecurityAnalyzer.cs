namespace Hermes.Agent.Security;

using System.Text.RegularExpressions;
using Hermes.Agent.Security.Validators;

/// <summary>
/// Result of shell command security analysis.
/// </summary>
public enum SecurityClassification
{
    /// <summary>
    /// Command is safe to execute.
    /// </summary>
    Safe,
    
    /// <summary>
    /// Command needs user review before execution.
    /// </summary>
    NeedsReview,
    
    /// <summary>
    /// Command is dangerous and should be denied.
    /// </summary>
    Dangerous,
    
    /// <summary>
    /// Command is too complex to analyze - fail closed.
    /// </summary>
    TooComplex
}

/// <summary>
/// Result of security analysis.
/// </summary>
public sealed record SecurityResult(
    SecurityClassification Classification,
    string? Reason = null,
    IReadOnlyList<string>? Warnings = null,
    IReadOnlyList<string>? Suggestions = null)
{
    public static SecurityResult Safe() => new(SecurityClassification.Safe);
    public static SecurityResult NeedsReview(string reason) => new(SecurityClassification.NeedsReview, reason);
    public static SecurityResult Dangerous(string reason) => new(SecurityClassification.Dangerous, reason);
    public static SecurityResult TooComplex(string reason) => new(SecurityClassification.TooComplex, reason);
}

/// <summary>
/// Analyzes shell commands for security risks.
/// Uses a chain of validators to classify commands.
/// </summary>
public sealed class ShellSecurityAnalyzer
{
    private readonly List<IShellValidator> _validators;
    
    public ShellSecurityAnalyzer()
    {
        _validators = new List<IShellValidator>
        {
            new MetacharacterValidator(),
            new InjectionValidator(),
            new RedirectionValidator(),
            new PrivilegeValidator(),
            new FileSystemValidator(),
            new NetworkValidator(),
            new ProcessValidator(),
            new EnvironmentValidator(),
            new PipeValidator(),
            new SubshellValidator(),
            new HistoryValidator(),
            new WildcardValidator(),
            new QuotingValidator(),
            new EncodingValidator(),
            new TimeoutValidator(),
            new BackgroundValidator(),
            new SudoValidator(),
            new RmValidator(),
            new CurlValidator(),
            new EvalValidator(),
        };
    }
    
    /// <summary>
    /// Analyze a shell command for security risks.
    /// </summary>
    public SecurityResult Analyze(string command, ShellContext? context = null)
    {
        var warnings = new List<string>();
        var suggestions = new List<string>();
        
        // Normalize command first
        var normalized = CommandNormalizer.Normalize(command);
        
        // Run through all validators
        foreach (var validator in _validators)
        {
            var result = validator.Validate(normalized, context);
            
            if (result.Classification == SecurityClassification.Dangerous)
            {
                return SecurityResult.Dangerous($"{validator.Name}: {result.Reason}");
            }
            
            if (result.Classification == SecurityClassification.TooComplex)
            {
                return SecurityResult.TooComplex($"{validator.Name}: {result.Reason}");
            }
            
            if (result.Warnings is not null)
                warnings.AddRange(result.Warnings);
            
            if (result.Suggestions is not null)
                suggestions.AddRange(result.Suggestions);
        }
        
        // If any warnings, needs review
        if (warnings.Count > 0)
        {
            return new SecurityResult(
                SecurityClassification.NeedsReview,
                "Command has warnings that require review",
                warnings,
                suggestions);
        }
        
        return SecurityResult.Safe();
    }
}

/// <summary>
/// Context for shell command execution.
/// </summary>
public sealed class ShellContext
{
    public string? WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
    public bool IsSandboxed { get; init; }
    public bool AllowNetwork { get; init; } = true;
    public bool AllowFileSystemWrite { get; init; } = true;
    public bool AllowSubprocess { get; init; } = true;
}

/// <summary>
/// Interface for shell command validators.
/// </summary>
public interface IShellValidator
{
    string Name { get; }
    SecurityResult Validate(string command, ShellContext? context);
}
