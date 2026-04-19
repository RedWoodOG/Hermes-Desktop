using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using Hermes.Agent.Transcript;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HermesDesktop.Views.Panels;

public sealed class SessionListItem
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string TimeAgo { get; set; } = "";
    public string MessageCount { get; set; } = "";
    /// <summary>"green" (live, &lt;5min), "gold" (idle, &lt;7d), "grey" (closed).</summary>
    public Brush StatusBrush { get; set; } = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    /// <summary>JetBrains Mono meta line: "Nt \u2022 time \u2022 model-hint".</summary>
    public string MetaLine { get; set; } = "";
}

public sealed partial class SessionPanel : UserControl
{
    private readonly TranscriptStore _transcriptStore;

    public ObservableCollection<SessionListItem> Sessions { get; } = new();
    public event Action<string>? SessionSelected;

    public SessionPanel()
    {
        InitializeComponent();
        _transcriptStore = App.Services.GetRequiredService<TranscriptStore>();
        Loaded += async (_, _) => await RefreshAsync();
    }

    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    public async System.Threading.Tasks.Task RefreshAsync()
    {
        Sessions.Clear();
        var ids = _transcriptStore.GetAllSessionIds();

        foreach (var id in ids.OrderByDescending(i => i))
        {
            try
            {
                var messages = await _transcriptStore.LoadSessionAsync(id, CancellationToken.None);
                var firstUser = messages.FirstOrDefault(m => m.Role == "user");
                var title = firstUser?.Content?.Length > 60
                    ? firstUser.Content[..60] + "..."
                    : firstUser?.Content ?? "(empty)";

                var last = messages.LastOrDefault();
                var ago = last is not null ? FormatTimeAgo(last.Timestamp) : "";

                // Sanctum status: green live (<5min), gold idle (<7d), grey closed.
                Brush statusBrush;
                if (last is null)
                    statusBrush = Res("AppTextMutedBrush");
                else
                {
                    var diff = DateTime.UtcNow - last.Timestamp;
                    statusBrush = diff.TotalMinutes < 5
                        ? Res("SessionGreenBrush")
                        : diff.TotalDays < 7
                            ? Res("AppAccentBrush")
                            : Res("AppTextMutedBrush");
                }

                Sessions.Add(new SessionListItem
                {
                    Id = id,
                    Title = title,
                    TimeAgo = ago,
                    MessageCount = $"{messages.Count} msgs",
                    StatusBrush = statusBrush,
                    MetaLine = $"{messages.Count}t \u2022 {ago}",
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SessionPanel skipping unreadable session {id}: {ex}");
            }
        }

        SessionList.ItemsSource = Sessions;
        EmptyState.Visibility = Sessions.Count == 0
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;
    }

    public event Action<string>? SessionDeleted;

    private void NewSession_Click(object sender, RoutedEventArgs e) => SessionSelected?.Invoke("");

    private async void DeleteSession_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string sessionId) return;

        await _transcriptStore.DeleteSessionAsync(sessionId, CancellationToken.None);
        var item = Sessions.FirstOrDefault(s => s.Id == sessionId);
        if (item is not null) Sessions.Remove(item);

        EmptyState.Visibility = Sessions.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        SessionDeleted?.Invoke(sessionId);
    }

    private void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SessionList.SelectedItem is SessionListItem item)
            SessionSelected?.Invoke(item.Id);
    }

    private static string FormatTimeAgo(DateTime timestamp)
    {
        var diff = DateTime.UtcNow - timestamp;
        if (diff.TotalMinutes < 1) return "just now";
        if (diff.TotalHours < 1) return $"{(int)diff.TotalMinutes}m ago";
        if (diff.TotalDays < 1) return $"{(int)diff.TotalHours}h ago";
        if (diff.TotalDays < 7) return $"{(int)diff.TotalDays}d ago";
        return timestamp.ToLocalTime().ToString("MMM d");
    }
}
