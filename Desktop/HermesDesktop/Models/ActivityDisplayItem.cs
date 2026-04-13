using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Hermes.Agent.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;

namespace HermesDesktop.Models;

/// <summary>
/// UI-friendly wrapper around ActivityEntry for data binding in ReplayPanel.
/// </summary>
public sealed class ActivityDisplayItem : INotifyPropertyChanged
{
    private ActivityStatus _status;
    private long _durationMs;
    private string _outputSummary = "";
    private string _outputFull = "";
    private bool _isExpanded;
    private string? _diffPreview;
    private string? _screenshotPath;
    private bool _isHighlighted;

    public ActivityDisplayItem(ActivityEntry entry)
    {
        Id = entry.Id;
        Timestamp = entry.Timestamp;
        ToolName = entry.ToolName;
        ToolCallId = entry.ToolCallId;
        InputSummary = entry.InputSummary;
        InputFull = entry.InputSummary;
        _outputSummary = entry.OutputSummary;
        _outputFull = entry.OutputSummary;
        _status = entry.Status;
        _durationMs = entry.DurationMs;
        _diffPreview = entry.DiffPreview;
        _screenshotPath = entry.ScreenshotPath;
    }

    public string Id { get; }
    public DateTime Timestamp { get; }
    public string ToolName { get; }
    public string? ToolCallId { get; }
    public string InputSummary { get; }
    public string InputFull { get; set; }

    /// <summary>
    /// Full tool result text shown in the expanded activity row. Notifies on
    /// change so x:Bind OneWay updates fire when the agent finishes a tool call
    /// and the row transitions from Running → Success/Failed via UpdateFrom.
    /// Without PropertyChanged here the expanded "Output" panel stays empty
    /// even when the tool returned data (the original "missing terminal output"
    /// bug — InputFull/OutputFull were plain auto-properties so the OneWay
    /// binding only saw the empty initial value).
    /// </summary>
    public string OutputFull
    {
        get => _outputFull;
        set
        {
            _outputFull = value;
            OnPropertyChanged();
        }
    }

    public string FormattedTime => Timestamp.ToLocalTime().ToString("HH:mm:ss");

    public string DurationText => _durationMs switch
    {
        0 when _status == ActivityStatus.Running => "...",
        < 1000 => $"{_durationMs}ms",
        _ => $"{_durationMs / 1000.0:F1}s"
    };

    public SolidColorBrush DurationColor => _durationMs switch
    {
        < 500 => new SolidColorBrush(ColorHelper.FromArgb(255, 80, 180, 80)),   // green
        < 2000 => new SolidColorBrush(ColorHelper.FromArgb(255, 220, 180, 60)), // yellow
        _ => new SolidColorBrush(ColorHelper.FromArgb(255, 220, 80, 80))        // red
    };

    public SolidColorBrush StatusColor => _status switch
    {
        ActivityStatus.Running => new SolidColorBrush(ColorHelper.FromArgb(255, 80, 140, 220)),
        ActivityStatus.Success => new SolidColorBrush(ColorHelper.FromArgb(255, 80, 180, 80)),
        ActivityStatus.Failed => new SolidColorBrush(ColorHelper.FromArgb(255, 220, 80, 80)),
        ActivityStatus.Denied => new SolidColorBrush(ColorHelper.FromArgb(255, 220, 180, 60)),
        _ => new SolidColorBrush(Colors.Gray)
    };

    public string StatusIcon => _status switch
    {
        ActivityStatus.Running => "\u25CC",  // ◌
        ActivityStatus.Success => "\u25CF",  // ●
        ActivityStatus.Failed => "\u25CF",   // ●
        ActivityStatus.Denied => "\u25CF",   // ●
        _ => "\u25CB"                        // ○
    };

    public ActivityStatus Status
    {
        get => _status;
        set
        {
            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusColor));
            OnPropertyChanged(nameof(StatusIcon));
        }
    }

    public long DurationMs
    {
        get => _durationMs;
        set
        {
            _durationMs = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DurationText));
            OnPropertyChanged(nameof(DurationColor));
        }
    }

    public string OutputSummary
    {
        get => _outputSummary;
        set
        {
            _outputSummary = value;
            OnPropertyChanged();
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; OnPropertyChanged(); }
    }

    public bool IsHighlighted
    {
        get => _isHighlighted;
        set
        {
            _isHighlighted = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HighlightBackground));
        }
    }

    public SolidColorBrush HighlightBackground => _isHighlighted
        ? new SolidColorBrush(ColorHelper.FromArgb(30, 255, 180, 60))
        : new SolidColorBrush(Colors.Transparent);

    public string? DiffPreview
    {
        get => _diffPreview;
        set { _diffPreview = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasDiff)); }
    }

    public string? ScreenshotPath
    {
        get => _screenshotPath;
        set { _screenshotPath = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasScreenshot)); }
    }

    public bool HasDiff => !string.IsNullOrEmpty(_diffPreview);
    public bool HasScreenshot => !string.IsNullOrEmpty(_screenshotPath);

    /// <summary>
    /// Update this display item from a new/updated ActivityEntry (same Id).
    /// </summary>
    public void UpdateFrom(ActivityEntry entry)
    {
        Status = entry.Status;
        DurationMs = entry.DurationMs;
        OutputSummary = entry.OutputSummary;
        OutputFull = entry.OutputSummary;
        DiffPreview = entry.DiffPreview;
        ScreenshotPath = entry.ScreenshotPath;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
