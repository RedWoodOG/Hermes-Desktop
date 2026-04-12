using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hermes.Agent.Core;
using HermesDesktop.Models;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace HermesDesktop.Views.Panels;

public sealed partial class ReplayPanel : UserControl
{
    public ObservableCollection<ActivityDisplayItem> Activities { get; } = new();

    private bool _isRecording;
    private bool _isPlaying;
    private CancellationTokenSource? _playCts;

    public bool IsRecording => _isRecording;

    /// <summary>Raised when the user toggles recording on/off.</summary>
    public event Action<bool>? RecordingToggled;

    public ReplayPanel()
    {
        InitializeComponent();
        ActivityList.ItemsSource = Activities;
        Activities.CollectionChanged += (_, _) => UpdateEmptyState();
    }

    /// <summary>
    /// Add or update an activity entry. Dispatches to UI thread.
    /// </summary>
    public void AddActivity(ActivityEntry entry)
    {
        if (DispatcherQueue.HasThreadAccess)
            AddOrUpdateInternal(entry);
        else
            DispatcherQueue.TryEnqueue(() => AddOrUpdateInternal(entry));
    }

    private void AddOrUpdateInternal(ActivityEntry entry)
    {
        // Check if we already have this entry (update case)
        var existing = Activities.FirstOrDefault(a => a.Id == entry.Id);
        if (existing is not null)
        {
            existing.UpdateFrom(entry);
        }
        else
        {
            Activities.Add(new ActivityDisplayItem(entry));
        }
        UpdateEmptyState();

        // Auto-scroll to latest
        if (Activities.Count > 0)
            ActivityList.ScrollIntoView(Activities[^1]);
    }

    /// <summary>
    /// Load a full session's activity entries.
    /// </summary>
    public void LoadSession(List<ActivityEntry> entries)
    {
        Activities.Clear();
        foreach (var entry in entries)
            Activities.Add(new ActivityDisplayItem(entry));
        UpdateEmptyState();
    }

    /// <summary>
    /// Clear all activity entries.
    /// </summary>
    public void Clear()
    {
        StopPlayback();
        Activities.Clear();
        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        EmptyState.Visibility = Activities.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ActivityList.Visibility = Activities.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Recording toggle ──

    private void RecordToggle_Click(object sender, RoutedEventArgs e)
    {
        _isRecording = RecordToggle.IsChecked == true;
        RecordDot.Fill = _isRecording
            ? new SolidColorBrush(ColorHelper.FromArgb(255, 220, 60, 60))
            : new SolidColorBrush(Colors.Gray);
        RecordLabel.Text = _isRecording ? "Recording" : "Record";
        RecordingToggled?.Invoke(_isRecording);
    }

    // ── Item click (expand/collapse) ──

    private void ActivityList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ActivityDisplayItem item)
            item.IsExpanded = !item.IsExpanded;
    }

    // ── Playback ──

    private async void PlayBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isPlaying)
        {
            StopPlayback();
            return;
        }

        if (Activities.Count == 0) return;

        _isPlaying = true;
        PlayBtn.Content = "\u25A0 Stop";
        _playCts = new CancellationTokenSource();

        try
        {
            await PlaybackAsync(_playCts.Token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            StopPlayback();
        }
    }

    private async Task PlaybackAsync(CancellationToken ct)
    {
        foreach (var item in Activities)
        {
            ct.ThrowIfCancellationRequested();

            // Highlight this item
            foreach (var a in Activities) a.IsHighlighted = false;
            item.IsHighlighted = true;
            item.IsExpanded = true;
            ActivityList.ScrollIntoView(item);

            // Show screenshot if available
            if (item.HasScreenshot)
            {
                try
                {
                    var bitmap = new BitmapImage(new Uri(item.ScreenshotPath!));
                    PreviewImage.Source = bitmap;
                    PreviewArea.Visibility = Visibility.Visible;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"ReplayPanel failed to load screenshot preview {item.ScreenshotPath}: {ex}");
                    PreviewArea.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                PreviewArea.Visibility = Visibility.Collapsed;
            }

            // Wait proportional to duration, minimum 400ms, maximum 2s
            var delay = Math.Clamp(item.DurationMs > 0 ? item.DurationMs : 500, 400, 2000);
            await Task.Delay((int)delay, ct);
        }
    }

    private void StopPlayback()
    {
        _isPlaying = false;
        PlayBtn.Content = "\u25B6 Play";
        _playCts?.Cancel();
        _playCts?.Dispose();
        _playCts = null;
        PreviewArea.Visibility = Visibility.Collapsed;
        foreach (var a in Activities) a.IsHighlighted = false;
    }

    // ── Clear button ──

    private void ClearBtn_Click(object sender, RoutedEventArgs e) => Clear();
}
