namespace Hermes.Agent.LLM;

/// <summary>
/// User-defined reusable model profile. Lives in <c>projectDir/saved-models.json</c>.
/// Distinct from <see cref="ModelCatalog"/> (static, ships with the app) and from
/// per-agent <c>AgentProfile.PreferredModel</c> (single-use override).
/// </summary>
/// <param name="Id">Opaque stable identifier (GUID by default). Used as the primary key by <see cref="SavedModelStore"/>.</param>
/// <param name="Name">Human-readable label shown in the picker.</param>
/// <param name="Provider">Provider key matching <see cref="ChatClientFactory"/> (e.g. <c>openai</c>, <c>anthropic</c>, <c>custom</c>).</param>
/// <param name="ModelId">Provider-specific model identifier sent on the wire.</param>
/// <param name="BaseUrl">Optional override base URL (for self-hosted / proxy endpoints).</param>
/// <param name="ApiKeyEnvVar">Environment variable that holds the API key. Never persist the key itself.</param>
/// <param name="ContextLength">Optional context-window hint used by the UI; not enforced.</param>
/// <param name="IsFavorite">Sticky UI flag — sort favorites first.</param>
public sealed record SavedModelProfile(
    string Id,
    string Name,
    string Provider,
    string ModelId,
    string? BaseUrl,
    string? ApiKeyEnvVar,
    int? ContextLength,
    bool IsFavorite
)
{
    /// <summary>Create a new profile with a freshly generated GUID id.</summary>
    public static SavedModelProfile Create(
        string name,
        string provider,
        string modelId,
        string? baseUrl = null,
        string? apiKeyEnvVar = null,
        int? contextLength = null,
        bool isFavorite = false) =>
        new(
            Id: Guid.NewGuid().ToString("N"),
            Name: name,
            Provider: provider,
            ModelId: modelId,
            BaseUrl: baseUrl,
            ApiKeyEnvVar: apiKeyEnvVar,
            ContextLength: contextLength,
            IsFavorite: isFavorite);
}
