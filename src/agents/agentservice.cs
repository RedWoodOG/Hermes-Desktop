namespace Hermes.Agent.Agents;

using System.Diagnostics;
using System.Text.Json;
using Hermes.Agent.Core;
using Hermes.Agent.LLM;
using Microsoft.Extensions.Logging;

// ── Async-local agent ID tracking ──

public static class AgentTracker
{
    private static readonly AsyncLocal<string?> _currentAgentId = new();
    public static string? CurrentAgentId
    {
        get => _currentAgentId.Value;
        set => _currentAgentId.Value = value;
    }
}

// =============================================
// Agent Service
// =============================================

public sealed class AgentService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<AgentService> _logger;
    private readonly string _worktreesDir;
    private readonly IChatClient _chatClient;
    private readonly ILoggerFactory _loggerFactory;

    public AgentService(
        IServiceProvider services,
        ILogger<AgentService> logger,
        ILoggerFactory loggerFactory,
        IChatClient chatClient,
        string worktreesDir)
    {
        _services = services;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _chatClient = chatClient;
        _worktreesDir = worktreesDir;
        Directory.CreateDirectory(worktreesDir);
    }

    public async Task<AgentResult> SpawnAgentAsync(AgentRequest request, CancellationToken ct)
    {
        var agentId = GenerateAgentId();
        _logger.LogInformation("Spawning agent {AgentId}: {Description}", agentId, request.Description);

        try
        {
            var isolation = request.Isolation?.ToLower() switch
            {
                "worktree" => await CreateWorktreeIsolationAsync(request, ct),
                "remote" => await CreateRemoteIsolationAsync(request, ct),
                _ => IsolationStrategy.None
            };

            var context = new AgentContext
            {
                AgentId = agentId,
                Prompt = request.Prompt,
                Model = request.Model ?? "default",
                WorkingDirectory = isolation.WorkingDirectory ?? Environment.CurrentDirectory,
                IsSubagent = true,
                ParentAgentId = AgentTracker.CurrentAgentId,
                TeamName = request.TeamName,
                AllowedTools = request.AllowedTools
            };

            var runner = new AgentRunner(context, _chatClient, _loggerFactory.CreateLogger<AgentRunner>());

            if (request.RunInBackground)
            {
                _ = Task.Run(async () =>
                {
                    AgentTracker.CurrentAgentId = agentId;
                    try { await runner.RunAsync(ct); }
                    catch (Exception ex) { _logger.LogError(ex, "Background agent {AgentId} failed", agentId); }
                    finally { if (isolation.Cleanup is not null) await isolation.Cleanup(); }
                }, ct);

                return new AgentResult { AgentId = agentId, Status = "spawned", BackgroundTaskId = agentId };
            }

            AgentTracker.CurrentAgentId = agentId;
            try
            {
                var result = await runner.RunAsync(ct);
                _logger.LogInformation("Agent {AgentId} completed: {Status}", agentId, result.Status);
                return result;
            }
            finally
            {
                if (isolation.Cleanup is not null) await isolation.Cleanup();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to spawn agent {AgentId}", agentId);
            return new AgentResult { AgentId = agentId, Status = "failed", Error = ex.Message };
        }
    }

    // ── Worktree Isolation ──

    private async Task<IsolationStrategy> CreateWorktreeIsolationAsync(AgentRequest request, CancellationToken ct)
    {
        var worktreeName = $"agent-{Guid.NewGuid():N}";
        var worktreePath = Path.Combine(_worktreesDir, worktreeName);

        try
        {
            _logger.LogInformation("Creating worktree at {Path}", worktreePath);
            await RunGitAsync($"worktree add {worktreePath}", ct);

            return new IsolationStrategy
            {
                Type = "worktree",
                WorkingDirectory = worktreePath,
                Cleanup = async () =>
                {
                    _logger.LogInformation("Cleaning up worktree {Path}", worktreePath);
                    await RunGitAsync($"worktree remove {worktreePath} --force", CancellationToken.None);
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create worktree");
            return IsolationStrategy.None;
        }
    }

    // ── Remote SSH Isolation ──

    private async Task<IsolationStrategy> CreateRemoteIsolationAsync(AgentRequest request, CancellationToken ct)
    {
        var remote = request.RemoteConfig;
        if (remote is null)
        {
            _logger.LogWarning("Remote isolation requested but no RemoteConfig provided");
            return IsolationStrategy.None;
        }

        var remoteDir = $"/tmp/hermes-agent-{Guid.NewGuid():N}";
        _logger.LogInformation("Creating remote isolation on {Host}:{Dir}", remote.Host, remoteDir);

        try
        {
            // Create remote working directory
            await RunSshAsync(remote, $"mkdir -p {remoteDir}", ct);

            // Clone the workspace to remote (or copy via scp)
            var localWorkspace = request.WorkspaceRoot ?? Environment.CurrentDirectory;
            if (Directory.Exists(Path.Combine(localWorkspace, ".git")))
            {
                // Git clone is more efficient for git repos
                var repoUrl = await GetGitRemoteUrlAsync(ct);
                if (repoUrl is not null)
                    await RunSshAsync(remote, $"cd {remoteDir} && git clone {repoUrl} . 2>/dev/null || true", ct);
                else
                    await RunScpAsync(remote, localWorkspace, remoteDir, ct);
            }
            else
            {
                await RunScpAsync(remote, localWorkspace, remoteDir, ct);
            }

            return new IsolationStrategy
            {
                Type = "remote",
                WorkingDirectory = remoteDir,
                RemoteConfig = remote,
                Cleanup = async () =>
                {
                    _logger.LogInformation("Cleaning up remote directory {Dir} on {Host}", remoteDir, remote.Host);
                    await RunSshAsync(remote, $"rm -rf {remoteDir}", CancellationToken.None);
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create remote isolation on {Host}", remote.Host);
            return IsolationStrategy.None;
        }
    }

    // ── Process helpers ──

    private static async Task RunGitAsync(string args, CancellationToken ct)
    {
        var result = await RunProcessAsync("git", args, ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"git {args} failed: {result.StdErr}");
    }

    private static async Task RunSshAsync(RemoteConfig remote, string command, CancellationToken ct)
    {
        var sshArgs = BuildSshArgs(remote) + $" \"{command}\"";
        var result = await RunProcessAsync("ssh", sshArgs, ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"SSH command failed: {result.StdErr}");
    }

    private static async Task RunScpAsync(RemoteConfig remote, string localPath, string remotePath, CancellationToken ct)
    {
        var keyArg = remote.KeyPath is not null ? $"-i \"{remote.KeyPath}\" " : "";
        var portArg = remote.Port != 22 ? $"-P {remote.Port} " : "";
        var args = $"{keyArg}{portArg}-r \"{localPath}\" {remote.User}@{remote.Host}:{remotePath}";
        var result = await RunProcessAsync("scp", args, ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"SCP failed: {result.StdErr}");
    }

    private static string BuildSshArgs(RemoteConfig remote)
    {
        var parts = new List<string>();
        if (remote.KeyPath is not null) parts.Add($"-i \"{remote.KeyPath}\"");
        if (remote.Port != 22) parts.Add($"-p {remote.Port}");
        parts.Add($"{remote.User}@{remote.Host}");
        return string.Join(" ", parts);
    }

    private static async Task<string?> GetGitRemoteUrlAsync(CancellationToken ct)
    {
        try
        {
            var result = await RunProcessAsync("git", "remote get-url origin", ct);
            return result.ExitCode == 0 ? result.StdOut.Trim() : null;
        }
        catch { return null; }
    }

    private static async Task<ProcessResult> RunProcessAsync(string fileName, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private string GenerateAgentId() => $"agent_{Guid.NewGuid():N}"[..20];

    private sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);
}

// =============================================
// Agent Runner — Actually executes agent tasks
// =============================================

public sealed class AgentRunner
{
    private readonly AgentContext _context;
    private readonly IChatClient _chatClient;
    private readonly ILogger<AgentRunner> _logger;

    public AgentRunner(AgentContext context, IChatClient chatClient, ILogger<AgentRunner> logger)
    {
        _context = context;
        _chatClient = chatClient;
        _logger = logger;
    }

    public async Task<AgentResult> RunAsync(CancellationToken ct)
    {
        _logger.LogInformation("Agent {AgentId} starting: {Prompt}", _context.AgentId, _context.Prompt[..Math.Min(100, _context.Prompt.Length)]);

        try
        {
            // Create agent instance with tool execution loop
            var agent = new Core.Agent(_chatClient, _logger as ILogger<Core.Agent>
                ?? Microsoft.Extensions.Logging.LoggerFactoryExtensions.CreateLogger<Core.Agent>(
                    new LoggerFactory()));

            // Register allowed tools
            if (_context.AllowedTools is not null)
            {
                foreach (var tool in _context.AllowedTools)
                    agent.RegisterTool(tool);
            }

            // Build system prompt with agent context
            var systemPrompt = BuildSystemPrompt();
            var session = new Session
            {
                Id = _context.AgentId,
                Platform = "agent"
            };

            // Inject system message
            session.AddMessage(new Message { Role = "system", Content = systemPrompt });

            // Run the chat loop (Agent.ChatAsync handles tool calling internally)
            var response = await agent.ChatAsync(_context.Prompt, session, ct);

            _logger.LogInformation("Agent {AgentId} completed successfully", _context.AgentId);
            return new AgentResult
            {
                AgentId = _context.AgentId,
                Status = "completed",
                Output = response
            };
        }
        catch (OperationCanceledException)
        {
            return new AgentResult { AgentId = _context.AgentId, Status = "cancelled" };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent {AgentId} failed", _context.AgentId);
            return new AgentResult
            {
                AgentId = _context.AgentId,
                Status = "failed",
                Error = ex.Message
            };
        }
    }

    private string BuildSystemPrompt()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("You are a specialized subagent working on a specific task.");
        sb.AppendLine($"Agent ID: {_context.AgentId}");
        sb.AppendLine($"Working directory: {_context.WorkingDirectory}");

        if (_context.ParentAgentId is not null)
            sb.AppendLine($"Parent agent: {_context.ParentAgentId}");
        if (_context.TeamName is not null)
            sb.AppendLine($"Team: {_context.TeamName}");

        sb.AppendLine();
        sb.AppendLine("Focus on completing your assigned task efficiently. Use the tools available to you.");
        sb.AppendLine("When done, provide a clear summary of what you accomplished.");

        return sb.ToString();
    }
}

// =============================================
// Team Manager
// =============================================

public sealed class TeamManager
{
    private readonly string _teamsDir;
    private readonly MailboxService _mailbox;
    private readonly ILogger<TeamManager> _logger;

    public TeamManager(string teamsDir, MailboxService mailbox, ILogger<TeamManager> logger)
    {
        _teamsDir = teamsDir;
        _mailbox = mailbox;
        _logger = logger;
        Directory.CreateDirectory(teamsDir);
    }

    public async Task<TeamResult> CreateTeamAsync(string teamName, string? description, CancellationToken ct)
    {
        var teamPath = Path.Combine(_teamsDir, $"{teamName}.json");
        if (File.Exists(teamPath))
            throw new TeamAlreadyExistsException(teamName);

        var team = new Team
        {
            TeamName = teamName,
            Description = description,
            LeadAgentId = AgentTracker.CurrentAgentId ?? "main-thread",
            Members = [],
            CreatedAt = DateTime.UtcNow
        };

        await SaveTeamAsync(team, ct);
        _logger.LogInformation("Created team {TeamName} led by {Lead}", teamName, team.LeadAgentId);

        return new TeamResult { TeamName = teamName, TeamFilePath = teamPath, LeadAgentId = team.LeadAgentId };
    }

    public async Task DeleteTeamAsync(string teamName, CancellationToken ct)
    {
        var team = await LoadTeamAsync(teamName, ct);
        var running = team.Members.Where(m => m.Status == "active" && m.AgentId != team.LeadAgentId).ToList();
        if (running.Count > 0)
            throw new TeamHasRunningMembersException($"Cannot delete team with {running.Count} running members");

        await CleanupTeamWorktreesAsync(team);
        var path = Path.Combine(_teamsDir, $"{teamName}.json");
        if (File.Exists(path)) File.Delete(path);
        _logger.LogInformation("Deleted team {TeamName}", teamName);
    }

    public async Task AddMemberAsync(string teamName, TeamMember member, CancellationToken ct)
    {
        var team = await LoadTeamAsync(teamName, ct);
        team.Members.Add(member);
        await SaveTeamAsync(team, ct);
        _logger.LogInformation("Added {MemberId} to team {TeamName}", member.AgentId, teamName);
    }

    public async Task UpdateMemberStatusAsync(string teamName, string agentId, string status, CancellationToken ct)
    {
        var team = await LoadTeamAsync(teamName, ct);
        var member = team.Members.FirstOrDefault(m => m.AgentId == agentId);
        if (member is not null)
        {
            member.Status = status;
            await SaveTeamAsync(team, ct);
        }
    }

    public async Task<Team> LoadTeamAsync(string teamName, CancellationToken ct)
    {
        var path = Path.Combine(_teamsDir, $"{teamName}.json");
        if (!File.Exists(path)) throw new TeamNotFoundException(teamName);
        var json = await File.ReadAllTextAsync(path, ct);
        return JsonSerializer.Deserialize<Team>(json, JsonOpts) ?? throw new TeamNotFoundException(teamName);
    }

    public async Task<List<string>> ListTeamsAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_teamsDir)) return [];
        return Directory.EnumerateFiles(_teamsDir, "*.json")
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .ToList();
    }

    private async Task SaveTeamAsync(Team team, CancellationToken ct)
    {
        var path = Path.Combine(_teamsDir, $"{team.TeamName}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(team, JsonOpts), ct);
    }

    private async Task CleanupTeamWorktreesAsync(Team team)
    {
        foreach (var m in team.Members.Where(m => m.WorktreePath is not null && Directory.Exists(m.WorktreePath)))
        {
            try { Directory.Delete(m.WorktreePath!, true); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to remove worktree {Path}", m.WorktreePath); }
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
}

// =============================================
// Mailbox Service (Inter-agent messaging)
// =============================================

public sealed class MailboxService
{
    private readonly string _mailboxDir;
    private readonly ILogger<MailboxService> _logger;

    public MailboxService(string mailboxDir, ILogger<MailboxService> logger)
    {
        _mailboxDir = mailboxDir;
        _logger = logger;
        Directory.CreateDirectory(mailboxDir);
    }

    public async Task SendMessageAsync(string recipient, string message, string? fromAgentId, CancellationToken ct)
    {
        var path = GetMailboxPath(recipient);
        var msg = new MailboxMessage
        {
            From = fromAgentId ?? AgentTracker.CurrentAgentId ?? "unknown",
            Content = message,
            Timestamp = DateTime.UtcNow,
            Read = false
        };

        var mailbox = File.Exists(path) ? await LoadMailboxAsync(path, ct) : new Mailbox();
        mailbox.Messages.Add(msg);
        await SaveMailboxAsync(path, mailbox, ct);
        _logger.LogInformation("Message sent to {Recipient} from {From}", recipient, msg.From);
    }

    public async Task<List<MailboxMessage>> ReadMessagesAsync(string agentName, CancellationToken ct)
    {
        var path = GetMailboxPath(agentName);
        if (!File.Exists(path)) return [];

        var mailbox = await LoadMailboxAsync(path, ct);
        foreach (var msg in mailbox.Messages.Where(m => !m.Read))
            msg.Read = true;

        await SaveMailboxAsync(path, mailbox, ct);
        return mailbox.Messages.OrderByDescending(m => m.Timestamp).ToList();
    }

    public async Task<int> GetUnreadCountAsync(string agentName, CancellationToken ct)
    {
        var path = GetMailboxPath(agentName);
        if (!File.Exists(path)) return 0;
        var mailbox = await LoadMailboxAsync(path, ct);
        return mailbox.Messages.Count(m => !m.Read);
    }

    public async Task ClearMailboxAsync(string agentName, CancellationToken ct)
    {
        var path = GetMailboxPath(agentName);
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>Poll-based subscription — checks for new messages at interval.</summary>
    public async IAsyncEnumerable<MailboxMessage> SubscribeAsync(
        string agentName,
        TimeSpan pollInterval,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var lastSeen = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(pollInterval, ct);
            var path = GetMailboxPath(agentName);
            if (!File.Exists(path)) continue;

            var mailbox = await LoadMailboxAsync(path, ct);
            var newMessages = mailbox.Messages.Where(m => m.Timestamp > lastSeen && !m.Read).ToList();
            foreach (var msg in newMessages)
            {
                lastSeen = msg.Timestamp;
                yield return msg;
            }
        }
    }

    private string GetMailboxPath(string name) => Path.Combine(_mailboxDir, $"{name}.json");

    private async Task<Mailbox> LoadMailboxAsync(string path, CancellationToken ct)
    {
        var json = await File.ReadAllTextAsync(path, ct);
        return JsonSerializer.Deserialize<Mailbox>(json, JsonOpts) ?? new Mailbox();
    }

    private async Task SaveMailboxAsync(string path, Mailbox mailbox, CancellationToken ct)
    {
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(mailbox, JsonOpts), ct);
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
}

// =============================================
// Types
// =============================================

public sealed class AgentRequest
{
    public required string Description { get; init; }
    public required string Prompt { get; init; }
    public string? Model { get; init; }
    public bool RunInBackground { get; init; }
    public string? Name { get; init; }
    public string? TeamName { get; init; }
    public string? Isolation { get; init; }
    public RemoteConfig? RemoteConfig { get; init; }
    public string? WorkspaceRoot { get; init; }
    public List<ITool>? AllowedTools { get; init; }
}

public sealed class AgentResult
{
    public required string AgentId { get; init; }
    public required string Status { get; init; }
    public string? Output { get; init; }
    public string? Error { get; init; }
    public string? BackgroundTaskId { get; init; }
}

public sealed class AgentContext
{
    public required string AgentId { get; init; }
    public required string Prompt { get; init; }
    public required string Model { get; init; }
    public required string WorkingDirectory { get; init; }
    public bool IsSubagent { get; init; }
    public string? ParentAgentId { get; init; }
    public string? TeamName { get; init; }
    public List<ITool>? AllowedTools { get; init; }
}

public sealed class RemoteConfig
{
    public required string Host { get; init; }
    public int Port { get; init; } = 22;
    public required string User { get; init; }
    public string? KeyPath { get; init; }
}

public sealed class IsolationStrategy
{
    public required string Type { get; init; }
    public string? WorkingDirectory { get; init; }
    public Func<Task>? Cleanup { get; init; }
    public RemoteConfig? RemoteConfig { get; init; }

    public static IsolationStrategy None => new() { Type = "none" };
}

public sealed class Team
{
    public required string TeamName { get; init; }
    public string? Description { get; init; }
    public required string LeadAgentId { get; init; }
    public List<TeamMember> Members { get; init; } = [];
    public DateTime CreatedAt { get; init; }
}

public sealed class TeamMember
{
    public required string AgentId { get; init; }
    public required string Name { get; init; }
    public required string Role { get; init; }
    public string Status { get; set; } = "idle";
    public string? WorktreePath { get; init; }
}

public sealed class TeamResult
{
    public required string TeamName { get; init; }
    public required string TeamFilePath { get; init; }
    public required string LeadAgentId { get; init; }
}

public sealed class Mailbox { public List<MailboxMessage> Messages { get; init; } = []; }

public sealed class MailboxMessage
{
    public required string From { get; init; }
    public required string Content { get; init; }
    public DateTime Timestamp { get; init; }
    public bool Read { get; set; }
}

// ── Exceptions ──

public sealed class TeamAlreadyExistsException(string teamName) : Exception($"Team '{teamName}' already exists");
public sealed class TeamNotFoundException(string teamName) : Exception($"Team '{teamName}' not found");
public sealed class TeamHasRunningMembersException(string message) : Exception(message);
