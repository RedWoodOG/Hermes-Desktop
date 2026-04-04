namespace Hermes.Agent.Security.Validators;

using System.Text.RegularExpressions;

/// <summary>
/// Validates shell metacharacters that could enable injection.
/// </summary>
public sealed class MetacharacterValidator : IShellValidator
{
    public string Name => "Metacharacter";
    
    private static readonly Regex DangerousMetachars = new(
        @"[\x00-\x1f\x7f]|\$\(|\$\{|`|\|\||&&|;;|!!",
        RegexOptions.Compiled);
    
    public SecurityResult Validate(string command, ShellContext? context)
    {
        // Check for control characters
        if (DangerousMetachars.IsMatch(command))
        {
            var matches = DangerousMetachars.Matches(command);
            var found = matches.Cast<Match>().Select(m => m.Value).Distinct();
            
            // Command substitution is allowed but needs review
            if (command.Contains("$(") || command.Contains("`"))
            {
                return SecurityResult.NeedsReview("Command substitution detected - verify input is not user-controlled");
            }
            
            // Chained commands need review
            if (command.Contains("||") || command.Contains("&&"))
            {
                return SecurityResult.NeedsReview("Command chaining detected - verify all parts are safe");
            }
            
            // Control characters are dangerous
            if (command.Any(c => c < 0x20 && c != '\t' && c != '\n' && c != '\r'))
            {
                return SecurityResult.Dangerous("Control characters in command");
            }
        }
        
        return SecurityResult.Safe();
    }
}

/// <summary>
/// Validates against command injection patterns.
/// </summary>
public sealed class InjectionValidator : IShellValidator
{
    public string Name => "Injection";
    
    private static readonly Regex[] InjectionPatterns =
    {
        new(@";\s*rm\s", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@";\s*curl\s.*\|\s*(ba)?sh", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@";\s*wget\s.*\|\s*(ba)?sh", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"&&\s*rm\s", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\|\s*rm\s+-rf\s+/", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\$\([^)]*\)\s*;", RegexOptions.Compiled),
        new(@"`[^`]*`\s*;", RegexOptions.Compiled),
    };
    
    public SecurityResult Validate(string command, ShellContext? context)
    {
        foreach (var pattern in InjectionPatterns)
        {
            if (pattern.IsMatch(command))
            {
                return SecurityResult.Dangerous($"Potential injection pattern detected: {pattern}");
            }
        }
        
        return SecurityResult.Safe();
    }
}

/// <summary>
/// Validates file redirection operations.
/// </summary>
public sealed class RedirectionValidator : IShellValidator
{
    public string Name => "Redirection";
    
    public SecurityResult Validate(string command, ShellContext? context)
    {
        var warnings = new List<string>();
        
        // Check for output redirection to sensitive paths
        if (command.Contains(">") || command.Contains(">>"))
        {
            var redirectMatch = Regex.Match(command, @">+\s*(\S+)");
            if (redirectMatch.Success)
            {
                var target = redirectMatch.Groups[1].Value;
                
                // Check for sensitive paths
                if (target.StartsWith("/etc/") || 
                    target.StartsWith("/boot/") ||
                    target.StartsWith("/dev/") ||
                    target.StartsWith("/proc/") ||
                    target.StartsWith("/sys/"))
                {
                    return SecurityResult.Dangerous($"Redirecting to sensitive path: {target}");
                }
                
                // Check for overwriting system files
                if (target.StartsWith("/usr/") || target.StartsWith("/bin/"))
                {
                    warnings.Add($"Writing to system directory: {target}");
                }
            }
        }
        
        // Check for input redirection from sensitive sources
        if (command.Contains("<"))
        {
            // Generally safe, but note it
        }
        
        // Check for here-doc
        if (command.Contains("<<"))
        {
            warnings.Add("Here-document detected - verify content is safe");
        }
        
        return warnings.Count > 0 
            ? new SecurityResult(SecurityClassification.NeedsReview, null, warnings)
            : SecurityResult.Safe();
    }
}

/// <summary>
/// Validates privilege escalation commands.
/// </summary>
public sealed class PrivilegeValidator : IShellValidator
{
    public string Name => "Privilege";
    
    public SecurityResult Validate(string command, ShellContext? context)
    {
        var normalized = command.ToLowerInvariant();
        
        // Check for sudo
        if (normalized.StartsWith("sudo ") || normalized.Contains(" sudo "))
        {
            return SecurityResult.NeedsReview("Sudo command requires elevated privileges");
        }
        
        // Check for su
        if (normalized.StartsWith("su ") || normalized.Contains(" su "))
        {
            return SecurityResult.Dangerous("User switching command detected");
        }
        
        // Check for doas
        if (normalized.StartsWith("doas ") || normalized.Contains(" doas "))
        {
            return SecurityResult.NeedsReview("Doas command requires elevated privileges");
        }
        
        // Check for pkexec
        if (normalized.StartsWith("pkexec ") || normalized.Contains(" pkexec "))
        {
            return SecurityResult.NeedsReview("PolicyKit execution requires elevated privileges");
        }
        
        // Check for runas (Windows)
        if (normalized.StartsWith("runas ") || normalized.Contains(" runas "))
        {
            return SecurityResult.NeedsReview("Runas command requires elevated privileges");
        }
        
        return SecurityResult.Safe();
    }
}

/// <summary>
/// Validates filesystem operations.
/// </summary>
public sealed class FileSystemValidator : IShellValidator
{
    public string Name => "FileSystem";
    
    public SecurityResult Validate(string command, ShellContext? context)
    {
        var normalized = command.ToLowerInvariant();
        
        // Check for disk operations
        if (normalized.Contains("dd ") || normalized.Contains("dd if=") || normalized.Contains("dd of="))
        {
            return SecurityResult.Dangerous("Disk operations with dd can cause data loss");
        }
        
        // Check for format commands
        if (normalized.Contains("mkfs.") || normalized.Contains("format "))
        {
            return SecurityResult.Dangerous("Disk formatting command detected");
        }
        
        // Check for mount operations
        if (normalized.StartsWith("mount ") || normalized.Contains(" mount "))
        {
            return SecurityResult.NeedsReview("Mount operation requires review");
        }
        
        // Check for unmount operations
        if (normalized.StartsWith("umount ") || normalized.StartsWith("unmount "))
        {
            return SecurityResult.NeedsReview("Unmount operation requires review");
        }
        
        // Check for chmod/chown on sensitive paths
        if (normalized.Contains("chmod ") || normalized.Contains("chown "))
        {
            if (normalized.Contains("/etc/") || 
                normalized.Contains("/usr/") ||
                normalized.Contains("/bin/"))
            {
                return SecurityResult.Dangerous("Modifying permissions on system directories");
            }
        }
        
        return SecurityResult.Safe();
    }
}

/// <summary>
/// Validates network operations.
/// </summary>
public sealed class NetworkValidator : IShellValidator
{
    public string Name => "Network";
    
    public SecurityResult Validate(string command, ShellContext? context)
    {
        var normalized = command.ToLowerInvariant();
        
        if (context is { AllowNetwork: false })
        {
            // Check for any network commands
            if (normalized.Contains("curl ") || normalized.Contains("wget ") ||
                normalized.Contains("nc ") || normalized.Contains("netcat ") ||
                normalized.Contains("ssh ") || normalized.Contains("scp ") ||
                normalized.Contains("rsync ") || normalized.Contains("ftp "))
            {
                return SecurityResult.Dangerous("Network access not allowed in this context");
            }
        }
        
        // Check for network configuration
        if (normalized.Contains("iptables ") || normalized.Contains("ip route") ||
            normalized.Contains("ifconfig ") || normalized.Contains("ip addr"))
        {
            return SecurityResult.NeedsReview("Network configuration command detected");
        }
        
        // Check for port binding
        if (normalized.Contains("-l ") && (normalized.Contains("nc ") || normalized.Contains("netcat ")))
        {
            return SecurityResult.NeedsReview("Port binding detected - potential security risk");
        }
        
        return SecurityResult.Safe();
    }
}

/// <summary>
/// Validates process operations.
/// </summary>
public sealed class ProcessValidator : IShellValidator
{
    public string Name => "Process";
    
    public SecurityResult Validate(string command, ShellContext? context)
    {
        var normalized = command.ToLowerInvariant();
        
        // Check for kill commands
        if (normalized.StartsWith("kill ") || normalized.Contains(" kill "))
        {
            if (normalized.Contains("-9") || normalized.Contains("-KILL"))
            {
                return SecurityResult.NeedsReview("Force kill command detected");
            }
        }
        
        // Check for killall
        if (normalized.StartsWith("killall "))
        {
            return SecurityResult.NeedsReview("Killall command affects multiple processes");
        }
        
        // Check for pkill
        if (normalized.StartsWith("pkill "))
        {
            return SecurityResult.NeedsReview("Pkill command affects multiple processes");
        }
        
        // Check for service management
        if (normalized.StartsWith("systemctl ") || normalized.StartsWith("service "))
        {
            return SecurityResult.NeedsReview("Service management command detected");
        }
        
        return SecurityResult.Safe();
    }
}

/// <summary>
/// Validates environment variable usage.
/// </summary>
public sealed class EnvironmentValidator : IShellValidator
{
    public string Name => "Environment";
    
    private static readonly HashSet<string> DangerousEnvVars = new(StringComparer.OrdinalIgnoreCase)
    {
        "LD_PRELOAD",
        "LD_LIBRARY_PATH",
        "DYLD_INSERT_LIBRARIES",
        "DYLD_LIBRARY_PATH",
        "PYTHONPATH",
        "NODE_PATH",
        "PERL5LIB",
        "RUBYLIB",
        "PATH", // Needs review
        "IFS",
        "CDPATH",
        "ENV",
        "BASH_ENV",
    };
    
    public SecurityResult Validate(string command, ShellContext? context)
    {
        var warnings = new List<string>();
        
        // Check for env var assignments
        var envMatches = Regex.Matches(command, @"^(\w+)=|[\s;](\w+)=");
        foreach (Match match in envMatches)
        {
            var varName = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            
            if (DangerousEnvVars.Contains(varName))
            {
                if (varName.Equals("LD_PRELOAD", StringComparison.OrdinalIgnoreCase) ||
                    varName.Equals("DYLD_INSERT_LIBRARIES", StringComparison.OrdinalIgnoreCase))
                {
                    return SecurityResult.Dangerous($"Setting dangerous environment variable: {varName}");
                }
                
                warnings.Add($"Setting sensitive environment variable: {varName}");
            }
        }
        
        // Check for export with dangerous vars
        if (command.Contains("export "))
        {
            var exportMatch = Regex.Match(command, @"export\s+(\w+)=");
            if (exportMatch.Success)
            {
                var varName = exportMatch.Groups[1].Value;
                if (DangerousEnvVars.Contains(varName))
                {
                    warnings.Add($"Exporting sensitive environment variable: {varName}");
                }
            }
        }
        
        return warnings.Count > 0
            ? new SecurityResult(SecurityClassification.NeedsReview, null, warnings)
            : SecurityResult.Safe();
    }
}

/// <summary>
/// Validates pipe operations.
/// </summary>
public sealed class PipeValidator : IShellValidator
{
    public string Name => "Pipe";
    
    public SecurityResult Validate(string command, ShellContext? context)
    {
        var parts = CommandNormalizer.SplitByPipe(command);
        
        if (parts.Count > 1)
        {
            // Check each part of the pipeline
            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                
                // Piping to shell is dangerous
                if (trimmed == "sh" || trimmed == "bash" || trimmed == "zsh" || 
                    trimmed == "dash" || trimmed == "fish")
                {
                    return SecurityResult.Dangerous("Piping to shell interpreter - potential code execution");
                }
                
                // Piping to eval is dangerous
                if (trimmed.StartsWith("eval "))
                {
                    return SecurityResult.Dangerous("Piping to eval - potential code execution");
                }
            }
        }
        
        return SecurityResult.Safe();
    }
}

/// <summary>
/// Validates subshell operations.
/// </summary>
public sealed class SubshellValidator : IShellValidator
{
    public string Name => "Subshell";
    
    public SecurityResult Validate(string command, ShellContext? context)
    {
        // Count nested subshells
        var depth = 0;
        var maxDepth = 0;
        
        foreach (var c in command)
        {
            if (c == '(') { depth++; maxDepth = Math.Max(maxDepth, depth); }
            else if (c == ')') { depth--; }
        }
        
        if (maxDepth > 3)
        {
            return SecurityResult.TooComplex($"Too many nested subshells ({maxDepth})");
        }
        
        if (maxDepth > 1)
        {
            return SecurityResult.NeedsReview($"Nested subshells detected (depth: {maxDepth})");
        }
        
        return SecurityResult.Safe();
    }
}

/// <summary>
/// Validates history operations.
/// </summary>
public sealed class HistoryValidator : IShellValidator
{
    public string Name => "History";
    
    public SecurityResult Validate(string command, ShellContext? context)
    {
        var normalized = command.ToLowerInvariant();
        
        // Check for history manipulation
        if (normalized.Contains("history -c") || normalized.Contains("history -d"))
        {
            return SecurityResult.NeedsReview("History manipulation detected");
        }
        
        // Check for unset HISTFILE
        if (normalized.Contains("unset histfile") || normalized.Contains("histsize=0"))
        {
            return SecurityResult.NeedsReview("History disabling detected - potential malicious activity");
        }
        
        return SecurityResult.Safe();
    }
}

/// <summary>
/// Validates wildcard usage.
/// </summary>
public sealed class WildcardValidator : IShellValidator
{
    public string Name => "Wildcard";
    
    public SecurityResult Validate(string command, ShellContext? context)
    {
        // Check for dangerous wildcard patterns
        if (command.Contains("/*") || command.Contains("/.*"))
        {
            return SecurityResult.NeedsReview("Wildcard on root directory - could affect many files");
        }
        
        // Check for rm with wildcards
        var normalized = command.ToLowerInvariant();
        if (normalized.StartsWith("rm ") && (command.Contains("*") || command.Contains("?")))
        {
            return SecurityResult.NeedsReview("rm with wildcards - verify target files");
        }
        
        return SecurityResult.Safe();
    }
}

/// <summary>
/// Validates quoting patterns.
/// </summary>
public sealed class QuotingValidator : IShellValidator
{
    public string Name => "Quoting";
    
    public SecurityResult Validate(string command, ShellContext? context)
    {
        var singleQuotes = command.Count(c => c == '\'');
        var doubleQuotes = command.Count(c => c == '"');
        
        // Unmatched quotes
        if (singleQuotes % 2 != 0)
        {
            return SecurityResult.Dangerous("Unmatched single quote - could cause unexpected behavior");
        }
        
        if (doubleQuotes % 2 != 0)
        {
            return SecurityResult.Dangerous("Unmatched double quote - could cause unexpected behavior");
        }
        
        // Check for mixed quoting that might indicate injection
        if (singleQuotes > 0 && doubleQuotes > 0)
        {
            return SecurityResult.NeedsReview("Mixed quoting detected - verify intent");
        }
        
        return SecurityResult.Safe();
    }
}

/// <summary>
/// Validates encoding/obfuscation patterns.
/// </summary>
public sealed class EncodingValidator : IShellValidator
{
    public string Name => "Encoding";
    
    public SecurityResult Validate(string command, ShellContext? context)
    {
        var normalized = command.ToLowerInvariant();
        
        // Check for base64 decoding and execution
        if (normalized.Contains("base64") && normalized.Contains("-d"))
        {
            if (normalized.Contains("|") && (normalized.Contains("sh") || normalized.Contains("bash")))
            {
                return SecurityResult.Dangerous("Base64 decode and execute - potential obfuscated code");
            }
        }
        
        // Check for hex encoding
        if (normalized.Contains("xxd -r") || normalized.Contains("printf \\x"))
        {
            return SecurityResult.NeedsReview("Hex encoding detected - verify content");
        }
        
        // Check for URL encoding
        var urlEncodedMatch = Regex.Match(command, "%[0-9A-Fa-f]{2}");
        if (urlEncodedMatch.Success)
        {
            return SecurityResult.NeedsReview("URL encoding detected - verify content");
        }
        
        return SecurityResult.Safe();
    }
}

/// <summary>
/// Validates timeout patterns.
/// </summary>
public sealed class TimeoutValidator : IShellValidator
{
    public string Name => "Timeout";
    
    public SecurityResult Validate(string command, ShellContext? context)
    {
        // timeout command itself is safe, already stripped by normalizer
        return SecurityResult.Safe();
    }
}

/// <summary>
/// Validates background execution.
/// </summary>
public sealed class BackgroundValidator : IShellValidator
{
    public string Name => "Background";
    
    public SecurityResult Validate(string command, ShellContext? context)
    {
        // Check for background execution
        if (command.TrimEnd().EndsWith("&"))
        {
            return SecurityResult.NeedsReview("Background execution detected - process will continue after command");
        }
        
        // Check for disown
        if (command.Contains("disown"))
        {
            return SecurityResult.NeedsReview("Disown detected - process will be detached from shell");
        }
        
        return SecurityResult.Safe();
    }
}

/// <summary>
/// Validates sudo-specific patterns.
/// </summary>
public sealed class SudoValidator : IShellValidator
{
    public string Name => "Sudo";
    
    public SecurityResult Validate(string command, ShellContext? context)
    {
        var normalized = command.ToLowerInvariant();
        
        // Already handled in PrivilegeValidator, but check for specific patterns
        if (normalized.Contains("sudo "))
        {
            // Check for sudo with shell
            if (normalized.Contains("sudo -s") || normalized.Contains("sudo -i"))
            {
                return SecurityResult.Dangerous("Sudo shell access - full root access");
            }
            
            // Check for sudo with dangerous commands
            if (normalized.Contains("sudo rm") || normalized.Contains("sudo dd"))
            {
                return SecurityResult.Dangerous("Sudo with destructive command");
            }
        }
        
        return SecurityResult.Safe();
    }
}

/// <summary>
/// Validates rm-specific patterns.
/// </summary>
public sealed class RmValidator : IShellValidator
{
    public string Name => "Rm";
    
    public SecurityResult Validate(string command, ShellContext? context)
    {
        var normalized = command.ToLowerInvariant();
        
        if (normalized.StartsWith("rm ") || normalized.Contains(" rm "))
        {
            // Check for rm -rf /
            if (normalized.Contains("-rf") || normalized.Contains("-fr"))
            {
                if (normalized.Contains(" /") || normalized.Contains(" /*"))
                {
                    return SecurityResult.Dangerous("rm -rf on root directory");
                }
                
                if (normalized.Contains("~") || normalized.Contains("$home"))
                {
                    return SecurityResult.Dangerous("rm -rf on home directory");
                }
                
                return SecurityResult.NeedsReview("rm -rf detected - verify target");
            }
            
            // Check for rm --no-preserve-root
            if (normalized.Contains("--no-preserve-root"))
            {
                return SecurityResult.Dangerous("rm with --no-preserve-root");
            }
        }
        
        return SecurityResult.Safe();
    }
}

/// <summary>
/// Validates curl-specific patterns.
/// </summary>
public sealed class CurlValidator : IShellValidator
{
    public string Name => "Curl";
    
    public SecurityResult Validate(string command, ShellContext? context)
    {
        var normalized = command.ToLowerInvariant();
        
        if (normalized.Contains("curl "))
        {
            // Check for curl piped to shell
            if (normalized.Contains("|") && (normalized.Contains("sh") || normalized.Contains("bash")))
            {
                return SecurityResult.Dangerous("curl piped to shell - remote code execution");
            }
            
            // Check for curl with -k (insecure)
            if (normalized.Contains(" -k") || normalized.Contains(" --insecure"))
            {
                return SecurityResult.NeedsReview("curl with insecure flag - SSL verification disabled");
            }
        }
        
        return SecurityResult.Safe();
    }
}

/// <summary>
/// Validates eval-specific patterns.
/// </summary>
public sealed class EvalValidator : IShellValidator
{
    public string Name => "Eval";
    
    public SecurityResult Validate(string command, ShellContext? context)
    {
        var normalized = command.ToLowerInvariant();
        
        // eval is always dangerous
        if (normalized.StartsWith("eval ") || normalized.Contains("; eval ") || normalized.Contains("&& eval "))
        {
            return SecurityResult.Dangerous("eval command - arbitrary code execution");
        }
        
        return SecurityResult.Safe();
    }
}
