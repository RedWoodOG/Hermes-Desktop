namespace Hermes.Agent.Security.Validators;

using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Token-aware validator that complements regex checks with structure and policy checks.
/// </summary>
public sealed class TokenizedCommandValidator : IShellValidator
{
    public string Name => "TokenizedCommand";

    private static readonly HashSet<string> SubprocessCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "bash", "sh", "zsh", "dash", "fish",
        "pwsh", "powershell", "cmd", "python", "python3",
        "node", "ruby", "perl", "lua", "deno"
    };

    private static readonly HashSet<string> FileWriteCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "rm", "mv", "cp", "touch", "mkdir", "rmdir", "truncate", "dd",
        "chmod", "chown", "ln", "tee", "sed", "install"
    };

    private static readonly string[] SensitivePrefixes =
    {
        "/etc/",
        "/boot/",
        "/dev/",
        "/proc/",
        "/sys/",
        "/usr/",
        "/bin/",
        "/sbin/",
        "c:\\windows\\",
        "c:\\program files\\",
    };

    private static readonly Regex WindowsDrivePrefix = new(
        @"^[a-zA-Z]:\\",
        RegexOptions.Compiled);

    public SecurityResult Validate(string command, ShellContext? context)
    {
        if (!ShellCommandTokenizer.TryTokenize(command, out var tokens, out var parseError))
        {
            return SecurityResult.NeedsReview($"Could not tokenize command safely: {parseError}");
        }

        if (tokens.Count == 0)
            return SecurityResult.Safe();

        if (HasProcessSubstitution(command))
            return SecurityResult.NeedsReview("Process substitution detected - verify input and expansion safety");

        var baseCommand = tokens[0].Value;
        if (HasExcessiveChaining(command))
            return SecurityResult.TooComplex("Too many command separators/chains for safe static analysis");

        if (context is { AllowSubprocess: false } && SubprocessCommands.Contains(baseCommand))
            return SecurityResult.Dangerous($"Subprocess execution not allowed in this context: {baseCommand}");

        var writeIntent = HasWriteIntent(baseCommand, tokens);
        if (context is { AllowFileSystemWrite: false } && writeIntent)
            return SecurityResult.Dangerous($"Filesystem writes are disabled in this context: {baseCommand}");

        if (writeIntent && TargetsSensitivePath(tokens, context?.WorkingDirectory))
            return SecurityResult.Dangerous("Command targets a sensitive filesystem location");

        if (ContainsPathTraversal(tokens))
            return SecurityResult.NeedsReview("Path traversal pattern detected in command arguments");

        return SecurityResult.Safe();
    }

    private static bool HasWriteIntent(string baseCommand, IReadOnlyList<ShellToken> tokens)
    {
        if (FileWriteCommands.Contains(baseCommand))
            return true;

        // Redirection is write intent, even for nominally read-only commands.
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Value is ">" or ">>" or "1>" or "1>>" or "2>" or "2>>")
                return true;
        }

        // sed -i modifies files in place.
        if (baseCommand.Equals("sed", StringComparison.OrdinalIgnoreCase))
        {
            return tokens.Any(t => t.Value.Equals("-i", StringComparison.OrdinalIgnoreCase) ||
                                   t.Value.StartsWith("-i", StringComparison.OrdinalIgnoreCase));
        }

        return false;
    }

    private static bool HasExcessiveChaining(string command)
    {
        var separators = 0;
        var inSingle = false;
        var inDouble = false;
        var escaped = false;

        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (c == '\\')
            {
                escaped = true;
                continue;
            }

            if (c == '\'' && !inDouble)
            {
                inSingle = !inSingle;
                continue;
            }

            if (c == '"' && !inSingle)
            {
                inDouble = !inDouble;
                continue;
            }

            if (inSingle || inDouble)
                continue;

            if (c == ';')
                separators++;
            else if (i + 1 < command.Length && c == '&' && command[i + 1] == '&')
            {
                separators++;
                i++;
            }
            else if (i + 1 < command.Length && c == '|' && command[i + 1] == '|')
            {
                separators++;
                i++;
            }
        }

        return separators > 3;
    }

    private static bool HasProcessSubstitution(string command)
    {
        return command.Contains("<(", StringComparison.Ordinal) ||
               command.Contains(">(", StringComparison.Ordinal);
    }

    private static bool ContainsPathTraversal(IReadOnlyList<ShellToken> tokens)
    {
        return tokens.Any(t => t.Value.Contains("../", StringComparison.Ordinal) ||
                               t.Value.Contains("..\\", StringComparison.Ordinal));
    }

    private static bool TargetsSensitivePath(IReadOnlyList<ShellToken> tokens, string? workingDirectory)
    {
        foreach (var token in tokens)
        {
            var raw = token.Value.Trim().Trim('"', '\'');
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            if (LooksSensitive(raw))
                return true;

            if (!LooksPathLike(raw))
                continue;

            try
            {
                var fullPath = Path.IsPathRooted(raw)
                    ? Path.GetFullPath(raw)
                    : Path.GetFullPath(raw, workingDirectory ?? Directory.GetCurrentDirectory());

                if (LooksSensitive(fullPath.Replace('\\', '/')))
                    return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"TokenizedCommandValidator path expansion failed for '{raw}': {ex}");
            }
        }

        return false;
    }

    private static bool LooksPathLike(string token)
    {
        return token.StartsWith('/') ||
               token.StartsWith("./", StringComparison.Ordinal) ||
               token.StartsWith("../", StringComparison.Ordinal) ||
               token.StartsWith(".\\", StringComparison.Ordinal) ||
               token.StartsWith("..\\", StringComparison.Ordinal) ||
               token.StartsWith("~/", StringComparison.Ordinal) ||
               token.Contains('\\', StringComparison.Ordinal) ||
               WindowsDrivePrefix.IsMatch(token);
    }

    private static bool LooksSensitive(string token)
    {
        var normalized = token.Replace('\\', '/').ToLowerInvariant();
        foreach (var prefix in SensitivePrefixes)
        {
            var p = prefix.Replace('\\', '/').ToLowerInvariant();
            if (normalized.StartsWith(p, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}

internal readonly record struct ShellToken(string Value);

internal static class ShellCommandTokenizer
{
    internal static bool TryTokenize(
        string command,
        out List<ShellToken> tokens,
        out string? parseError)
    {
        tokens = new List<ShellToken>();
        parseError = null;

        var current = new StringBuilder();
        var inSingle = false;
        var inDouble = false;
        var escaped = false;

        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];

            if (escaped)
            {
                current.Append(c);
                escaped = false;
                continue;
            }

            if (c == '\\')
            {
                escaped = true;
                current.Append(c);
                continue;
            }

            if (c == '\'' && !inDouble)
            {
                inSingle = !inSingle;
                current.Append(c);
                continue;
            }

            if (c == '"' && !inSingle)
            {
                inDouble = !inDouble;
                current.Append(c);
                continue;
            }

            if (!inSingle && !inDouble && char.IsWhiteSpace(c))
            {
                FlushCurrentToken(current, tokens);
                continue;
            }

            if (!inSingle && !inDouble && IsStandaloneOperator(c))
            {
                FlushCurrentToken(current, tokens);
                tokens.Add(new ShellToken(c.ToString()));
                continue;
            }

            current.Append(c);
        }

        if (inSingle || inDouble)
        {
            parseError = "Unbalanced quotes";
            return false;
        }

        FlushCurrentToken(current, tokens);
        return true;
    }

    private static bool IsStandaloneOperator(char c) => c is '>' or '<';

    private static void FlushCurrentToken(StringBuilder current, List<ShellToken> tokens)
    {
        if (current.Length == 0)
            return;

        tokens.Add(new ShellToken(current.ToString()));
        current.Clear();
    }
}
