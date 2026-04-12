namespace Hermes.Agent.Dreamer;

/// <summary>Observable state for UI (Dashboard) — updated from the Dreamer background loop.</summary>
public sealed class DreamerStatus
{
    private readonly object _lock = new();
    private string _phase = "idle";
    private int _walkCount;
    private string _lastWalkSummary = "";
    private string _lastPostcardPreview = "";
    private string _startupFailureMessage = "";
    private double _topSignalScore;
    private string _topSignalSlug = "";
    private string _lastLocalDigestHint = "";

    public DreamerStatusSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            return new DreamerStatusSnapshot(
                _phase,
                _walkCount,
                _lastWalkSummary,
                _lastPostcardPreview,
                _startupFailureMessage,
                _topSignalScore,
                _topSignalSlug,
                _lastLocalDigestHint);
        }
    }

    public void SetPhase(string phase)
    {
        lock (_lock) { _phase = phase; }
    }

    public void ClearStartupFailure()
    {
        lock (_lock) { _startupFailureMessage = ""; }
    }

    public void SetStartupFailure(string message)
    {
        lock (_lock)
        {
            _phase = "startup-failed";
            _startupFailureMessage = message;
        }
    }

    public void AfterWalk(string walkPreview, int walkNumber, double topScore, string topSlug)
    {
        lock (_lock)
        {
            _phase = "idle";
            _walkCount = walkNumber;
            _lastWalkSummary = walkPreview;
            _topSignalScore = topScore;
            _topSignalSlug = topSlug;
        }
    }

    public void SetPostcardPreview(string text)
    {
        lock (_lock) { _lastPostcardPreview = text; }
    }

    public void SetLastLocalDigestHint(string relativePath)
    {
        lock (_lock) { _lastLocalDigestHint = relativePath; }
    }
}

public readonly record struct DreamerStatusSnapshot(
    string Phase,
    int WalkCount,
    string LastWalkSummary,
    string LastPostcardPreview,
    string StartupFailureMessage,
    double TopSignalScore,
    string TopSignalSlug,
    string LastLocalDigestHint);
