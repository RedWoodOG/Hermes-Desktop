namespace Hermes.Agent.LLM;

/// <summary>
/// Manages multiple API keys for a provider with thread-safe least-used selection
/// and automatic rotation on 401 failures.
/// Matches the official Hermes Agent credential_pool.py behavior.
/// </summary>
public sealed class CredentialPool
{
    private readonly object _lock = new();
    private readonly List<PoolEntry> _entries = new();
    private int _roundRobinIndex;

    /// <summary>A single credential in the pool.</summary>
    private sealed class PoolEntry
    {
        public required string ApiKey { get; init; }
        public string? Label { get; init; }
        public int RequestCount { get; set; }
        public bool IsFailed { get; set; }
        public DateTime? FailedAt { get; set; }
        public int? LastErrorCode { get; set; }
        public string? LastErrorReason { get; set; }
        public int ActiveLeases { get; set; }
    }

    /// <summary>Cooldown for rate-limit errors (429).</summary>
    public TimeSpan RateLimitCooldown { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Cooldown for other errors (401, 500, etc).</summary>
    public TimeSpan DefaultCooldown { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Max concurrent leases per credential.</summary>
    public int MaxConcurrentLeases { get; set; } = 1;

    /// <summary>Selection strategy.</summary>
    public PoolStrategy Strategy { get; set; } = PoolStrategy.LeastUsed;

    /// <summary>Number of credentials in the pool.</summary>
    public int Count
    {
        get { lock (_lock) return _entries.Count; }
    }

    /// <summary>Whether any healthy credentials remain.</summary>
    public bool HasHealthyCredentials
    {
        get
        {
            lock (_lock)
            {
                return _entries.Any(e => !e.IsFailed || IsRecovered(e));
            }
        }
    }

    /// <summary>Add a credential to the pool.</summary>
    public void Add(string apiKey, string? label = null)
    {
        lock (_lock)
        {
            // Don't add duplicates
            if (_entries.Any(e => e.ApiKey == apiKey)) return;
            _entries.Add(new PoolEntry { ApiKey = apiKey, Label = label });
        }
    }

    /// <summary>
    /// Get the next API key to use based on the configured strategy.
    /// Returns null if all credentials are exhausted.
    /// </summary>
    public string? GetNext()
    {
        lock (_lock)
        {
            var healthy = _entries.Where(e => !e.IsFailed || IsRecovered(e)).ToList();
            if (healthy.Count == 0) return null;

            // If recovered, reset failure state
            foreach (var e in healthy.Where(e => e.IsFailed && IsRecovered(e)))
            {
                e.IsFailed = false;
                e.FailedAt = null;
            }

            PoolEntry selected;
            switch (Strategy)
            {
                case PoolStrategy.LeastUsed:
                    selected = healthy.MinBy(e => e.RequestCount) ?? healthy[0];
                    break;
                case PoolStrategy.RoundRobin:
                    _roundRobinIndex = _roundRobinIndex % healthy.Count;
                    selected = healthy[_roundRobinIndex];
                    _roundRobinIndex++;
                    break;
                case PoolStrategy.Random:
                    selected = healthy[Random.Shared.Next(healthy.Count)];
                    break;
                default: // FillFirst
                    selected = healthy[0];
                    break;
            }

            selected.RequestCount++;
            return selected.ApiKey;
        }
    }

    /// <summary>
    /// Mark a credential as failed with error code context.
    /// Upstream ref: credential_pool.py mark_exhausted_and_rotate
    /// Cooldown: 1 hour for 429 (rate limit), 24 hours for others.
    /// </summary>
    public void MarkFailed(string apiKey, int? statusCode = null, string? reason = null)
    {
        lock (_lock)
        {
            var entry = _entries.FirstOrDefault(e => e.ApiKey == apiKey);
            if (entry is not null)
            {
                entry.IsFailed = true;
                entry.FailedAt = DateTime.UtcNow;
                entry.LastErrorCode = statusCode;
                entry.LastErrorReason = reason;
            }
        }
    }

    /// <summary>Reset all failure states.</summary>
    public void ResetAll()
    {
        lock (_lock)
        {
            foreach (var e in _entries)
            {
                e.IsFailed = false;
                e.FailedAt = null;
                e.LastErrorCode = null;
                e.LastErrorReason = null;
            }
        }
    }

    // ── Lease System (upstream: acquire_lease/release_lease) ──

    /// <summary>
    /// Acquire a lease on a credential for concurrent use.
    /// Prefers credentials below MaxConcurrentLeases soft cap.
    /// Returns API key or null if all exhausted.
    /// </summary>
    public string? AcquireLease()
    {
        lock (_lock)
        {
            var healthy = _entries.Where(e => !e.IsFailed || IsRecovered(e)).ToList();
            if (healthy.Count == 0) return null;

            // Prefer entries below the soft cap
            var belowCap = healthy.Where(e => e.ActiveLeases < MaxConcurrentLeases).ToList();
            var pool = belowCap.Count > 0 ? belowCap : healthy;

            // Pick least-leased, break ties by priority (index)
            var selected = pool.MinBy(e => e.ActiveLeases) ?? pool[0];
            selected.ActiveLeases++;
            selected.RequestCount++;
            return selected.ApiKey;
        }
    }

    /// <summary>Release a lease on a credential.</summary>
    public void ReleaseLease(string apiKey)
    {
        lock (_lock)
        {
            var entry = _entries.FirstOrDefault(e => e.ApiKey == apiKey);
            if (entry is not null && entry.ActiveLeases > 0)
                entry.ActiveLeases--;
        }
    }

    /// <summary>Active lease count for a credential.</summary>
    public int GetLeaseCount(string apiKey)
    {
        lock (_lock)
        {
            var entry = _entries.FirstOrDefault(e => e.ApiKey == apiKey);
            return entry?.ActiveLeases ?? 0;
        }
    }

    private bool IsRecovered(PoolEntry entry)
    {
        if (!entry.FailedAt.HasValue) return false;
        var cooldown = entry.LastErrorCode == 429 ? RateLimitCooldown : DefaultCooldown;
        return DateTime.UtcNow - entry.FailedAt.Value > cooldown;
    }
}

public enum PoolStrategy
{
    FillFirst,
    RoundRobin,
    Random,
    LeastUsed
}
