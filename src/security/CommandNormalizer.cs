namespace Hermes.Agent.Security;

using System.Text.RegularExpressions;

/// <summary>
/// Normalizes shell commands by stripping safe wrappers and standardizing format.
/// </summary>
public static class CommandNormalizer
{
    private static readonly Regex[] SafeWrapperPatterns =
    {
        // timeout wrapper
        new(@"^timeout\s+\d+\s+", RegexOptions.IgnoreCase),
        // nice wrapper
        new(@"^nice\s+(-\d+\s+)?", RegexOptions.IgnoreCase),
        // nohup wrapper
        new(@"^nohup\s+", RegexOptions.IgnoreCase),
        // ionice wrapper
        new(@"^ionice\s+(-c\s+\d+\s+)?", RegexOptions.IgnoreCase),
        // taskset wrapper
        new(@"^taskset\s+[0-9a-f]+\s+", RegexOptions.IgnoreCase),
        // strace wrapper (debugging)
        new(@"^strace\s+(-\w+\s+)*", RegexOptions.IgnoreCase),
        // time wrapper
        new(@"^time\s+", RegexOptions.IgnoreCase),
        // stdbuf wrapper
        new(@"^stdbuf\s+(-[ioe]\s+\d+\s+)+", RegexOptions.IgnoreCase),
        // env wrapper (with env vars)
        new(@"^env\s+(-[iu]\s+\S+\s+)*", RegexOptions.IgnoreCase),
        // setsid wrapper
        new(@"^setsid\s+", RegexOptions.IgnoreCase),
        // unshare wrapper
        new(@"^unshare\s+(-\w+\s+)*", RegexOptions.IgnoreCase),
    };
    
    private static readonly Regex[] SafeEnvPrefixes =
    {
        // Common safe env var prefixes
        new(@"^LANG=\S+\s+", RegexOptions.IgnoreCase),
        new(@"^LC_ALL=\S+\s+", RegexOptions.IgnoreCase),
        new(@"^TERM=\S+\s+", RegexOptions.IgnoreCase),
        new(@"^DISPLAY=\S+\s+", RegexOptions.IgnoreCase),
        new(@"^HOME=\S+\s+", RegexOptions.IgnoreCase),
        new(@"^PATH=\S+\s+", RegexOptions.IgnoreCase),
        new(@"^USER=\S+\s+", RegexOptions.IgnoreCase),
        new(@"^PWD=\S+\s+", RegexOptions.IgnoreCase),
        new(@"^TMPDIR=\S+\s+", RegexOptions.IgnoreCase),
        new(@"^EDITOR=\S+\s+", RegexOptions.IgnoreCase),
        new(@"^VISUAL=\S+\s+", RegexOptions.IgnoreCase),
        new(@"^PAGER=\S+\s+", RegexOptions.IgnoreCase),
        new(@"^BROWSER=\S+\s+", RegexOptions.IgnoreCase),
    };
    
    /// <summary>
    /// Normalize a command by stripping safe wrappers and env prefixes.
    /// </summary>
    public static string Normalize(string command)
    {
        var normalized = command.Trim();
        
        // Strip safe wrappers iteratively
        bool changed;
        do
        {
            changed = false;
            foreach (var pattern in SafeWrapperPatterns)
            {
                var newCmd = pattern.Replace(normalized, "");
                if (newCmd != normalized)
                {
                    normalized = newCmd.Trim();
                    changed = true;
                }
            }
        } while (changed);
        
        // Strip safe env var prefixes
        do
        {
            changed = false;
            foreach (var pattern in SafeEnvPrefixes)
            {
                var newCmd = pattern.Replace(normalized, "");
                if (newCmd != normalized)
                {
                    normalized = newCmd.Trim();
                    changed = true;
                }
            }
        } while (changed);
        
        // Normalize whitespace
        normalized = Regex.Replace(normalized, @"\s+", " ");
        
        return normalized;
    }
    
    /// <summary>
    /// Extract the base command (first word) from a normalized command.
    /// </summary>
    public static string GetBaseCommand(string command)
    {
        var normalized = Normalize(command);
        var parts = normalized.Split(' ', 2);
        return parts[0];
    }
    
    /// <summary>
    /// Check if a command uses PowerShell syntax.
    /// </summary>
    public static bool IsPowerShell(string command)
    {
        var trimmed = command.TrimStart();
        return trimmed.StartsWith("pwsh", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("powershell", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains(" -Command ", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains(" -ScriptBlock ", StringComparison.OrdinalIgnoreCase);
    }
    
    /// <summary>
    /// Split command by pipes, respecting quotes.
    /// </summary>
    public static List<string> SplitByPipe(string command)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var escaped = false;
        
        foreach (var c in command)
        {
            if (escaped)
            {
                current.Append(c);
                escaped = false;
                continue;
            }
            
            switch (c)
            {
                case '\\':
                    escaped = true;
                    current.Append(c);
                    break;
                    
                case '\'':
                    inSingleQuote = !inSingleQuote;
                    current.Append(c);
                    break;
                    
                case '"':
                    inDoubleQuote = !inDoubleQuote;
                    current.Append(c);
                    break;
                    
                case '|':
                    if (!inSingleQuote && !inDoubleQuote)
                    {
                        parts.Add(current.ToString().Trim());
                        current.Clear();
                    }
                    else
                    {
                        current.Append(c);
                    }
                    break;
                    
                default:
                    current.Append(c);
                    break;
            }
        }
        
        if (current.Length > 0)
            parts.Add(current.ToString().Trim());
        
        return parts;
    }
}
