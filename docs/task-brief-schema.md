# Task Brief Schema

## Why Briefs Exist

Local and open-source models drift. They lose track of the objective, wander into irrelevant topics, produce wrong output formats, and stop before the task is actually done. Briefs solve this by making **every constraint explicit and machine-enforceable** before agents ever spawn.

A brief is the contract between the user and the coordinator (M). It answers:

| Question | Brief Field |
|---|---|
| What exactly must be produced? | `objective` |
| What's in scope? | `focus` |
| What's explicitly off-limits? | `exclude` |
| How many agents, doing what? | `agentCount` + `agentRoles` |
| What does the output look like? | `outputFormat` + `outputTemplate` |
| How do we know it's done RIGHT? | `verifyMethod` (`Checklist`, `Contains`, `Schema`, `Manual`) + verification data |
| What kind of task is this? | `type` + `typeGuidance` |
| Who handles blockers? | `escalateTo` |

## Workflow

```
1. User says something (possibly vague)
         │
         ▼
2. M drafts a brief (structured JSON)
         │
         ▼
3. User reviews and approves (or refines)
         │
         ▼
4. M reads the brief, spawns agents with formulaic prompts
         │
         ▼
5. Agents work within hard boundaries from the brief
         │
         ▼
6. M verifies output using the brief's verification method:
   - Checklist: LLM evaluates each item in verifyChecklist
   - Contains: Machine checks for required strings in verifyContains
   - Schema: Validates JSON against verifySchema
   - Manual: Routes to verifyManual (human review)
         │
         ▼
7. Pass → Done │ Fail → Retry or escalate
```

**The user never writes YAML/JSON.** They describe what they want. M structures it. The user says "yes" or "change X".

## Schema Reference

### Core Fields

| Field | Type | Required | Description |
|---|---|---|---|
| `id` | string | auto | Unique ID (e.g., `T-001`) |
| `title` | string | yes | Short imperative title |
| `type` | enum | yes | `Research`, `Coding`, `Analysis`, `Brainstorming`, `Design`, `Review`, `Writing` |
| `status` | enum | auto | `Draft` → `Approved` → `InProgress` → `Completed` / `Failed` / `Blocked` |
| `objective` | string | yes | ONE sentence. No ambiguity. No compound goals. |

### Scope (Hard Boundaries)

| Field | Type | Required | Description |
|---|---|---|---|
| `focus` | string[] | recommended | What IS in scope. Agents only work within these. |
| `exclude` | string[] | optional | What is NOT in scope. Agents must not touch these. |
| `depthLimit` | string | optional | Prevents rabbit-holing. E.g., "3 sources per topic" |

### Agent Configuration

| Field | Type | Required | Description |
|---|---|---|---|
| `agentCount` | int | yes | How many agents to spawn. Decided at brief time. |
| `agentRoles` | string[] | recommended | Named roles. Length should match `agentCount`. |
| `roleInstructions` | dict | optional | Per-role instructions injected into agent prompts. |
| `maxTurnsPerAgent` | int | recommended | Hard turn limit. Default 10 for local models. |

### Output Contract

| Field | Type | Required | Description |
|---|---|---|---|
| `outputFormat` | enum | yes | `Markdown`, `Json`, `Csv`, `Code`, `Table`, `Bullets`, `Freeform` |
| `outputTemplate` | string | optional | Literal template agents must fill in. |
| `destination` | string | yes | File path, or `"return"` for inline result. |

### Verification

| Field | Type | Required | Description |
|---|---|---|---|
| `verifyMethod` | enum | yes | `Checklist`, `Contains`, `Schema`, `Manual` |
| `verifyChecklist` | string[] | if checklist | Yes/no assertions. Must be testable. |
| `verifyContains` | string[] | if contains | Strings the output must include. Machine-checkable. |
| `verifySchema` | string | if schema | JSON schema to validate against. |

### Escalation

| Field | Type | Required | Description |
|---|---|---|---|
| `escalateTo` | string | recommended | Who to notify when blocked. |
| `escalateWhen` | string[] | optional | Specific conditions that trigger escalation. |

## Type-Specific Guidance (`typeGuidance`)

Each brief type has known keys that control type-specific behavior:

### Research
| Key | Values | Default |
|---|---|---|
| `sources` | `web`, `docs`, `code` (comma-separated) | - |
| `depth` | `surface`, `standard`, `deep` | `standard` |
| `citations` | `true`, `false` | `false` |
| `source_count` | number per topic | - |

### Coding
| Key | Values | Default |
|---|---|---|
| `language` | any language name | **required** |
| `framework` | framework name | - |
| `tests` | `true`, `false` | `false` |
| `lint_rules` | path to lint config | - |

### Analysis
| Key | Values | Default |
|---|---|---|
| `analysis_framework` | `SWOT`, `5-forces`, `comparison`, etc. | - |
| `dimensions` | comma-separated comparison axes | - |

### Brainstorming
| Key | Values | Default |
|---|---|---|
| `style` | `wild`, `practical`, `balanced` | `balanced` |
| `quantity` | target number of ideas | - |
| `do_not_reuse` | comma-separated past ideas | - |

### Design
| Key | Values | Default |
|---|---|---|
| `design_format` | `wireframe`, `spec`, `mvp` | - |
| `audience` | `developers`, `end-users`, etc. | - |

### Review
| Key | Values | Default |
|---|---|---|
| `perspective` | `security`, `performance`, `ux` | - |
| `review_depth` | `surface`, `thorough`, `line-by-line` | `thorough` |

## Examples

### Example 1: Competitor Research

User says: *"Research what Cursor, Windsurf, Copilot, and Cline charge"*

M drafts:

```json
{
  "id": "T-001",
  "title": "Compare competitor pricing",
  "type": "Research",
  "objective": "Produce a pricing comparison table for Cursor, Windsurf, Copilot, and Cline",
  "focus": ["pricing tiers", "per-seat vs per-usage", "free tier limits"],
  "exclude": ["feature comparisons", "technical architecture", "reviews"],
  "depthLimit": "3 sources per competitor",
  "agentCount": 2,
  "agentRoles": ["researcher", "synthesizer"],
  "roleInstructions": {
    "researcher": "Find current pricing pages for all 4 competitors. Extract tier names, prices, and key limits. Return raw data only.",
    "synthesizer": "Take researcher output and produce the final comparison table. Normalize pricing to monthly per-seat where possible."
  },
  "maxTurnsPerAgent": 8,
  "outputFormat": "Table",
  "outputTemplate": "| Tool | Free Tier | Pro Tier | Enterprise | Pricing Model |\n|---|---|---|---|---|\n| Cursor | ... | ... | ... | ... |\n| Windsurf | ... | ... | ... | ... |\n| Copilot | ... | ... | ... | ... |\n| Cline | ... | ... | ... | ... |",
  "destination": "return",
  "verifyMethod": "Checklist",
  "verifyChecklist": [
    "Output contains pricing for Cursor",
    "Output contains pricing for Windsurf",
    "Output contains pricing for Copilot",
    "Output contains pricing for Cline",
    "Pricing table has at least 3 tiers per tool",
    "All prices are in USD"
  ],
  "escalateTo": "Joe",
  "typeGuidance": {
    "sources": "web",
    "depth": "standard",
    "citations": "true",
    "source_count": "3"
  }
}
```

### Example 2: Code Review

User says: *"Review the agent service for security issues"*

M drafts:

```json
{
  "id": "T-002",
  "title": "Security review of AgentService",
  "type": "Review",
  "objective": "Identify security vulnerabilities in src/agents/agentservice.cs",
  "focus": ["command injection", "path traversal", "input validation", "process spawning"],
  "exclude": ["performance", "code style", "feature suggestions"],
  "agentCount": 1,
  "agentRoles": ["security-reviewer"],
  "maxTurnsPerAgent": 15,
  "outputFormat": "Markdown",
  "outputTemplate": "## Security Review: AgentService\n\n### Critical\n- ...\n\n### High\n- ...\n\n### Medium\n- ...\n\n### Low\n- ...\n\n### Summary\n...",
  "destination": "return",
  "verifyMethod": "Checklist",
  "verifyChecklist": [
    "Review covers command injection vectors",
    "Review covers path traversal in worktree creation",
    "Review covers SSH command construction safety",
    "Each finding has severity level and remediation"
  ],
  "escalateTo": "Joe",
  "typeGuidance": {
    "perspective": "security",
    "review_depth": "line-by-line"
  }
}
```

### Example 3: Brainstorming

User says: *"Give me feature ideas for the buddy system"*

M drafts:

```json
{
  "id": "T-003",
  "title": "Brainstorm buddy system features",
  "type": "Brainstorming",
  "objective": "Generate 20 feature ideas for the Hermes buddy companion system",
  "focus": ["user engagement", "personality expression", "productivity integration"],
  "exclude": ["monetization", "social features", "third-party integrations"],
  "agentCount": 2,
  "agentRoles": ["wild-ideator", "practical-filter"],
  "roleInstructions": {
    "wild-ideator": "Generate 30 raw ideas. Be creative. Include weird ones. No self-censoring.",
    "practical-filter": "Take the raw ideas, remove duplicates, rank by feasibility, return top 20 with one-line rationale each."
  },
  "maxTurnsPerAgent": 5,
  "outputFormat": "Bullets",
  "destination": "return",
  "verifyMethod": "Checklist",
  "verifyChecklist": [
    "Output contains at least 20 distinct ideas",
    "Each idea has a one-line rationale",
    "Ideas are ranked by feasibility",
    "No monetization or social features included"
  ],
  "escalateTo": "Joe",
  "typeGuidance": {
    "style": "balanced",
    "quantity": "20"
  }
}
```

## Anti-Drift Design Principles

1. **Explicit over inferred.** If it matters, it's in the brief. Agents don't guess.
2. **Hard boundaries over soft guidance.** `focus` and `exclude` are walls, not fences.
3. **Templates over descriptions.** "Fill in this table" beats "produce a table".
4. **Machine-checkable over judgment calls.** `verifyContains` and `verifySchema` need no LLM. `verifyChecklist` items are yes/no.
5. **Turn limits are mandatory.** Local models will loop forever without them.
6. **Fewer agents with clear roles.** 2 focused agents beats 5 vague ones.
7. **M drafts, user approves.** The user's interface is natural language, not JSON.

## Integration with Existing Systems

- **TaskManager**: Each approved brief creates a linked `HermesTask` via `linkedTaskId`
- **CoordinatorService**: Reads the brief to determine decomposition strategy instead of LLM-guessing
- **AgentService**: Receives formulaic prompts generated from `BuildAgentPrompt()`
- **Verification**: Coordinator runs `VerifyOutputAsync()` before marking brief as Completed