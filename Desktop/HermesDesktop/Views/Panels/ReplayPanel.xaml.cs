using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hermes.Agent.Core;
using HermesDesktop.Models;
using HermesDesktop.Services;
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

    // The ReplayPanel DataTemplate uses x:Bind against ActivityDisplayItem with
    // x:DataType, so the IL trimmer must keep every public property of
    // ActivityDisplayItem reachable from XAML. PublishTrimmed is disabled in
    // HermesDesktop.csproj precisely because WinUI 3 compiled bindings are not
    // trim-safe; this DynamicDependency on the ctor is belt-and-suspenders so
    // re-enabling trimming later cannot silently strip the model and reproduce
    // the "Cannot create instance of type ReplayPanel [Line: 0 Position: 0]"
    // XamlParseException at startup.
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors, typeof(ActivityDisplayItem))]
    public ReplayPanel()
    {
        try
        {
            InitializeComponent();
            ActivityList.ItemsSource = Activities;
            Activities.CollectionChanged += (_, _) => UpdateEmptyState();
        }
        catch (Exception ex)
        {
            // The WinUI XAML loader collapses any exception thrown from a UserControl
            // constructor into an opaque XamlParseException ("Cannot create instance of
            // type 'ReplayPanel' [Line: 0 Position: 0]") with no InnerException, because
            // the failure crosses the WinRT ABI as a bare HRESULT. Capture the real
            // exception to the startup log here, while we still have managed stack
            // frames, so future crash reports include the actual root cause instead of
            // the opaque wrapper.
            StartupDiagnostics.LogControlConstructorFailure(nameof(ReplayPanel), ex);
            throw;
        }
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
            // Newest activity is inserted at index 0 so the most recent tool call
            // sits at the top of the panel without the user having to scroll. This
            // matches conventional dev tool log ordering (terminal scrollback,
            // browser devtools network panel, etc.). PlaybackAsync below iterates
            // the collection in chronological order via OrderBy(Timestamp) so the
            // replay still plays oldest → newest regardless of display order.
            Activities.Insert(0, new ActivityDisplayItem(entry));
        }
        UpdateEmptyState();

        // Auto-scroll to the newest entry, which is now at index 0 (top).
        if (Activities.Count > 0)
            ActivityList.ScrollIntoView(Activities[0]);
    }

    /// <summary>
    /// Load a full session's activity entries. Entries are inserted in
    /// descending-timestamp order so the most recent tool call ends up at index
    /// 0 (top of the panel), matching live-add ordering.
    /// </summary>
    public void LoadSession(List<ActivityEntry> entries)
    {
        Activities.Clear();
        foreach (var entry in entries.OrderByDescending(e => e.Timestamp))
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
        // Display order is newest-first, but playback should always run
        // chronologically (oldest → newest) so the user watches the agent's
        // work unfold in the same order it actually happened. Snapshot to a
        // local list so an Activities mutation mid-playback can't desync us.
        var ordered = Activities.OrderBy(a => a.Timestamp).ToList();
        foreach (var item in ordered)
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
