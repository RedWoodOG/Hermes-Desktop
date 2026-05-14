namespace Hermes.Agent.LLM;

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

/// <summary>
/// JSON-backed CRUD store for <see cref="SavedModelProfile"/>. Persists to
/// <c>&lt;storageDir&gt;/saved-models.json</c> using a temp-file + rename for
/// crash-safety. Thread-safe for reads/writes via an in-memory lock around the
/// concurrent index.
/// </summary>
public sealed class SavedModelStore
{
    private const string FileName = "saved-models.json";

    private readonly string _path;
    private readonly ILogger<SavedModelStore> _logger;
    private readonly ConcurrentDictionary<string, SavedModelProfile> _profiles = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public SavedModelStore(string storageDir, ILogger<SavedModelStore> logger)
    {
        _logger = logger;
        Directory.CreateDirectory(storageDir);
        _path = Path.Combine(storageDir, FileName);
        Load();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var raw = File.ReadAllText(_path);
            if (string.IsNullOrWhiteSpace(raw)) return;
            var doc = JsonSerializer.Deserialize<PersistedDocument>(raw, JsonOptions);
            if (doc?.Profiles is null) return;
            foreach (var p in doc.Profiles)
            {
                if (!string.IsNullOrEmpty(p.Id))
                    _profiles[p.Id] = p;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load saved models from {Path}", _path);
        }
    }

    /// <summary>All saved profiles, favorites first then alphabetical by name.</summary>
    public IReadOnlyList<SavedModelProfile> List() =>
        _profiles.Values
            .OrderByDescending(p => p.IsFavorite)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public SavedModelProfile? Get(string id) =>
        _profiles.TryGetValue(id, out var p) ? p : null;

    /// <summary>
    /// Insert or update a profile. Returns the persisted instance.
    /// <para>
    /// If <c>SaveAsync</c> throws (disk full, permission denied, locked file…), the
    /// in-memory state is rolled back so memory and disk do not diverge — the caller
    /// observes a single failed operation rather than a partially-applied mutation
    /// (CodeRabbit, 2026-05-14).
    /// </para>
    /// </summary>
    public async Task<SavedModelProfile> UpsertAsync(SavedModelProfile profile, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(profile.Id))
            throw new ArgumentException("SavedModelProfile.Id is required", nameof(profile));
        if (string.IsNullOrWhiteSpace(profile.Name))
            throw new ArgumentException("SavedModelProfile.Name is required", nameof(profile));
        if (string.IsNullOrWhiteSpace(profile.Provider))
            throw new ArgumentException("SavedModelProfile.Provider is required", nameof(profile));
        if (string.IsNullOrWhiteSpace(profile.ModelId))
            throw new ArgumentException("SavedModelProfile.ModelId is required", nameof(profile));

        bool hadPrevious = _profiles.TryGetValue(profile.Id, out var previous);
        _profiles[profile.Id] = profile;
        try
        {
            await SaveAsync(ct).ConfigureAwait(false);
            return profile;
        }
        catch
        {
            if (hadPrevious && previous is not null)
                _profiles[profile.Id] = previous;
            else
                _profiles.TryRemove(profile.Id, out _);
            throw;
        }
    }

    /// <summary>
    /// Delete by id. No-op if missing. Returns whether anything was removed.
    /// <para>
    /// If <c>SaveAsync</c> throws after the in-memory removal, the entry is restored
    /// so memory and disk stay consistent (CodeRabbit, 2026-05-14).
    /// </para>
    /// </summary>
    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        if (!_profiles.TryRemove(id, out var removed)) return false;
        try
        {
            await SaveAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            _profiles[id] = removed!;
            throw;
        }
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var doc = new PersistedDocument
            {
                Profiles = _profiles.Values
                    .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList()
            };
            var raw = JsonSerializer.Serialize(doc, JsonOptions);
            var tmp = _path + ".tmp";
            await File.WriteAllTextAsync(tmp, raw, ct).ConfigureAwait(false);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist saved models to {Path}", _path);
            throw;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private sealed class PersistedDocument
    {
        public int Version { get; set; } = 1;
        public List<SavedModelProfile> Profiles { get; set; } = new();
    }
}
