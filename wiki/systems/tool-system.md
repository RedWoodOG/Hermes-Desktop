---
title: Tool System
type: system
tags: [tools, security, agent]
created: 2026-04-09
updated: 2026-04-09
sources: [src/Core/models.cs, src/Tools/]
---

# Tool System

## ITool Interface

```csharp
public interface ITool {
    string Name { get; }
    string Description { get; }
    Type ParametersType { get; }
    Task<ToolResult> ExecuteAsync(object parameters, CancellationToken ct);
}
```

ToolResult: `Ok(string content)` or `Fail(string error, Exception? ex)`.

## Registered Tools (27+)

| Tool | File | Category |
|------|------|----------|
| read_file | readfiletool.cs | Filesystem |
| write_file | writefiletool.cs | Filesystem |
| edit_file | editfiletool.cs | Filesystem |
| glob | globtool.cs | Filesystem |
| grep | greptool.cs | Filesystem |
| patch | patchtool.cs | Filesystem |
| bash | bashtool.cs | Execution |
| terminal | terminaltool.cs | Execution |
| code_sandbox | codesandboxtool.cs | Execution |
| browser | browsertool.cs | Web |
| web_fetch | webfetchtool.cs | Web |
| web_search | websearchtool.cs | Web |
| agent | agenttool.cs | Meta |
| ask_user | askusertool.cs | Interaction |
| skill_invoke | skillinvoketool.cs | Skills |
| memory | memorytool.cs | Memory |
| session_search | sessionsearchtool.cs | Search |
| checkpoint | checkpointtool.cs | State |
| todo_write | todowritetool.cs | Task tracking |
| send_message | sendmessagetool.cs | Gateway |
| schedule_cron | schedulecrontool.cs | Scheduling |
| image_generation | imagegenerationtool.cs | Media |
| transcription | transcriptiontool.cs | Media |
| tts | ttstool.cs | Media |
| vision | visiontool.cs | Media |
| home_assistant | homeassistanttool.cs | IoT |
| lsp | lsptool.cs | Code intelligence |
| osv | osvtool.cs | Security |
| mixture_of_agents | mixtureofagentstool.cs | Advanced |

## Tool Definition Schema

Agent.BuildParameterSchema uses reflection on ParametersType:
- Properties become JSON Schema properties (type mapped: string, int, bool, array)
- Non-nullable value types become required
- System.ComponentModel.DescriptionAttribute provides field descriptions
- Property names converted to camelCase

## Parallel Execution

ParallelSafeTools (read-only): `read_file, glob, grep, web_fetch, web_search, session_search, skill_invoke, memory, lsp`

NeverParallelTools: `ask_user`

`ShouldParallelize`: true when count > 1, no NeverParallel present, ALL calls are ParallelSafe. Uses SemaphoreSlim(8) with Task.WhenAll.

## Secret Scanning

After every tool result, `SecretScanner.ContainsSecrets()` checks for API keys (20+ prefix patterns), auth headers, JSON secret fields, private keys, DB connection strings, URL credentials. Detected secrets are replaced with `[REDACTED]`.

## Tool Call ID Normalization

`NormalizeToolCallIds` ensures deterministic IDs: if provider returns empty ID, generates `call_{turnNumber}_{callIndex}`. Prevents cache invalidation across providers.

## Key Files
- `src/Core/models.cs` -- ITool, ToolResult, ToolCall, ToolDefinition
- `src/Tools/*.cs` -- 27+ tool implementations
- `src/security/SecretScanner.cs` -- secret detection and redaction

## See Also
- [[../patterns/parallel-tool-execution]]
- [[agent-loop]]
