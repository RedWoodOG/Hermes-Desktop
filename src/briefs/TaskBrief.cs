namespace Hermes.Agent.Briefs;

using System.Text.Json.Serialization;

// ══════════════════════════════════════════════
// Task Brief Schema — Structured task definition
// ══════════════════════════════════════════════
//
// Briefs exist to solve ONE problem: local/open-source models drift.
// Every field constrains what agents CAN do, not what they SHOULD do.
// The brief is the contract between the user and the coordinator.

// ── Enums ──

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BriefType
{
    Research,
    Coding,
    Analysis,
    Brainstorming,
    Design,
    Review,
    Writing
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OutputFormat
{
    Markdown,
    Json,
    Csv,
    Code,
    Table,
    Bullets,
    Freeform
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BriefStatus
{
    Draft,       // M drafted, awaiting user approval
    Approved,    // User approved, ready to execute
    InProgress,  // Agents are working on it
    Completed,   // All verification passed
    Failed,      // Verification failed or agents couldn't complete
    Blocked      // Waiting on escalation
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VerifyMethod
{
    Checklist,   // All items in checklist must be true
    Contains,    // Output must contain specific strings
    Schema,      // Output must match a JSON schema
    Manual       // User reviews and approves
}

// ── Core Brief Model ──

/// <summary>
/// A TaskBrief is a structured, explicit contract that tells the coordinator
/// exactly what to do, how many agents to use, what output looks like,
/// and how to verify completion. Designed for local/open-source models
/// that need hard guardrails to prevent drift.
/// </summary>
public sealed class TaskBrief
{
    // ── Identity ──

    /// <summary>Unique brief ID (e.g., "T-001").</summary>
    public required string Id { get; init; } = "";

    /// <summary>Short, imperative title (e.g., "Research competitor pricing").</summary>
    public required string Title { get; set; }

    /// <summary>Task type — drives default agent roles and sub-schema.</summary>
    public required BriefType Type { get; set; }

    public BriefStatus Status { get; set; } = BriefStatus.Draft;

    // ── Scope (hard boundaries, not suggestions) ──

    /// <summary>
    /// Exactly what the task must accomplish. One sentence, no ambiguity.
    /// Bad:  "Look into competitors"
    /// Good: "Produce a pricing comparison table for Cursor, Windsurf, Copilot, and Cline"
    /// </summary>
    public required string Objective { get; set; }

    /// <summary>
    /// Explicit list of what IS in scope. Agents only work within these boundaries.
    /// Empty = everything relevant (dangerous for weak models, discouraged).
    /// </summary>
    public List<string> Focus { get; set; } = [];

    /// <summary>
    /// Hard exclusions. Agents must NOT touch/explore/produce anything in this list.
    /// More enforceable than making focus "precise enough" — weak models need both.
    /// </summary>
    public List<string> Exclude { get; set; } = [];

    /// <summary>
    /// Maximum depth/breadth constraint to prevent rabbit-holing.
    /// E.g., "3 sources per competitor", "top-level only, no sub-components"
    /// </summary>
    public string? DepthLimit { get; set; }

    // ── Agent Configuration (decided at brief time, not runtime) ──

    /// <summary>
    /// How many agents to spawn. Explicit count prevents the coordinator
    /// from over- or under-provisioning.
    /// </summary>
    public int AgentCount { get; set; } = 1;

    /// <summary>
    /// Named roles for each agent. Length should match AgentCount.
    /// Each role becomes part of the agent's system prompt.
    /// E.g., ["researcher-cursor", "researcher-windsurf", "synthesizer"]
    /// </summary>
    public List<string> AgentRoles { get; set; } = [];

    /// <summary>
    /// Per-role instructions injected into agent system prompts.
    /// Key = role name, Value = specific instructions for that role.
    /// Keeps agent prompts formulaic and explicit.
    /// </summary>
    public Dictionary<string, string> RoleInstructions { get; set; } = new();

    /// <summary>
    /// Maximum turns/iterations per agent before forced stop.
    /// Prevents runaway agents on weak models.
    /// </summary>
    public int? MaxTurnsPerAgent { get; set; }

    // ── Output Contract (what the user gets) ──

    /// <summary>
    /// Required output format. Agents are instructed to produce exactly this.
    /// </summary>
    public OutputFormat OutputFormat { get; set; } = OutputFormat.Markdown;

    /// <summary>
    /// Output template — literal structure the output must follow.
    /// Agents receive this as "fill in this template" not "write something like this".
    /// Null = no template (freeform within format).
    /// </summary>
    public string? OutputTemplate { get; set; }

    /// <summary>
    /// Where to put the result. File path, task field, or "return" for inline.
    /// </summary>
    public string Destination { get; set; } = "return";

    // ── Verification (how M knows it's done RIGHT) ──

    public VerifyMethod VerifyMethod { get; set; } = VerifyMethod.Checklist;

    /// <summary>
    /// For Checklist method: each string is a yes/no acceptance criterion.
    /// M checks each one before marking the brief as Completed.
    /// Write these as testable assertions, not vague goals.
    /// Bad:  "Good quality research"
    /// Good: "Output includes pricing for all 4 competitors"
    /// </summary>
    public List<string> VerifyChecklist { get; set; } = [];

    /// <summary>
    /// For Contains method: output must include all of these strings.
    /// Machine-checkable — no LLM judgment needed.
    /// </summary>
    public List<string> VerifyContains { get; set; } = [];

    /// <summary>
    /// For Schema method: JSON schema that the output must validate against.
    /// </summary>
    public string? VerifySchema { get; set; }

    // ── Escalation ──

    /// <summary>Who to notify when blocked. Not if — WHO.</summary>
    public string? EscalateTo { get; set; }

    /// <summary>Conditions that trigger escalation (optional, defaults to "any blocker").</summary>
    public List<string> EscalateWhen { get; set; } = [];

    // ── Type-Specific Guidance ──

    /// <summary>
    /// Sub-schema fields driven by BriefType. Parsed from the type-specific
    /// section of the brief. Avoids a massive union type — just a dictionary
    /// that BriefService validates per type.
    /// </summary>
    public Dictionary<string, string> TypeGuidance { get; set; } = new();

    // ── Metadata ──

    public string? LinkedTaskId { get; set; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Tracks which brief this was refined from (for draft → approve flow).
    /// </summary>
    public string? ParentBriefId { get; set; }

    /// <summary>
    /// Free-form notes from the user during refinement.
    /// </summary>
    public string? Notes { get; set; }
}

// ── Type-Specific Guidance Keys ──
// These are the known keys for TypeGuidance per BriefType.
// BriefService validates that the right keys exist for each type.

public static class TypeGuidanceKeys
{
    // Research
    public const string Sources = "sources";           // "web,docs,code" — where to look
    public const string Depth = "depth";               // "surface|standard|deep"
    public const string CitationRequired = "citations"; // "true|false"
    public const string SourceCount = "source_count";   // "3" — per topic

    // Coding
    public const string Language = "language";           // "csharp"
    public const string Framework = "framework";         // "winui3"
    public const string TestRequired = "tests";          // "true|false"
    public const string LintRules = "lint_rules";        // path to lint config

    // Analysis
    public const string AnalysisFramework = "analysis_framework"; // "SWOT|5-forces|comparison"
    public const string Dimensions = "dimensions";       // "price,features,ux"

    // Brainstorming
    public const string Style = "style";                 // "wild|practical|balanced"
    public const string QuantityTarget = "quantity";      // "20"
    public const string DoNotReuse = "do_not_reuse";     // "idea1,idea2" — avoid repeats

    // Design
    public const string DesignFormat = "design_format";  // "wireframe|spec|mvp"
    public const string Audience = "audience";            // "developers|end-users"

    // Review
    public const string ReviewPerspective = "perspective"; // "security|performance|ux"
    public const string ReviewDepth = "review_depth";      // "surface|thorough|line-by-line"
}

// ── Request/Result types for BriefService ──

public sealed class BriefDraftRequest
{
    /// <summary>The user's raw, possibly vague description of what they want.</summary>
    public required string UserInput { get; init; }

    /// <summary>Optional type hint. If null, M infers from the input.</summary>
    public BriefType? TypeHint { get; init; }

    /// <summary>Optional constraints the user specified upfront.</summary>
    public Dictionary<string, string>? Constraints { get; init; }
}

public sealed class BriefValidationResult
{
    public bool IsValid { get; init; }
    public List<string> Errors { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
}
