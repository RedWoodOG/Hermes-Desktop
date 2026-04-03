using System.Collections.ObjectModel;
using System.Linq;
using Hermes.Agent.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HermesDesktop.Views.Panels;

public sealed class TaskListItem
{
    public string TaskId { get; set; } = "";
    public string Description { get; set; } = "";
    public string StatusLabel { get; set; } = "";
    public string PriorityLabel { get; set; } = "";
    public string DueLabel { get; set; } = "";
    public SolidColorBrush StatusColor { get; set; } = new(Colors.Gray);
    public SolidColorBrush DescriptionBrush { get; set; } = new(ColorHelper.FromArgb(255, 232, 238, 247));
    public SolidColorBrush DueBrush { get; set; } = new(ColorHelper.FromArgb(255, 149, 162, 177));
}

public sealed partial class TaskPanel : UserControl
{
    private readonly TaskManager _taskManager;
    public ObservableCollection<TaskListItem> Tasks { get; } = new();

    public TaskPanel()
    {
        InitializeComponent();
        _taskManager = App.Services.GetRequiredService<TaskManager>();
        Loaded += (_, _) => Refresh();
    }

    public void Refresh()
    {
        Tasks.Clear();
        foreach (var task in _taskManager.GetOrderedTasks())
        {
            Tasks.Add(new TaskListItem
            {
                TaskId = task.TaskId,
                Description = task.Description,
                StatusLabel = task.Status.ToString(),
                PriorityLabel = $"{task.Priority}",
                DueLabel = task.DueDate?.ToLocalTime().ToString("MMM d") ?? "",
                StatusColor = GetStatusColor(task.Status),
                DescriptionBrush = task.Status == TaskStatus.Completed
                    ? new SolidColorBrush(ColorHelper.FromArgb(255, 100, 120, 100))
                    : new SolidColorBrush(ColorHelper.FromArgb(255, 232, 238, 247)),
                DueBrush = task.IsOverdue
                    ? new SolidColorBrush(ColorHelper.FromArgb(255, 255, 100, 100))
                    : new SolidColorBrush(ColorHelper.FromArgb(255, 149, 162, 177))
            });
        }
        TaskList.ItemsSource = Tasks;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private static SolidColorBrush GetStatusColor(TaskStatus status) => status switch
    {
        TaskStatus.Pending => new SolidColorBrush(ColorHelper.FromArgb(255, 120, 120, 120)),
        TaskStatus.InProgress => new SolidColorBrush(ColorHelper.FromArgb(255, 80, 140, 220)),
        TaskStatus.Completed => new SolidColorBrush(ColorHelper.FromArgb(255, 80, 180, 80)),
        TaskStatus.Failed => new SolidColorBrush(ColorHelper.FromArgb(255, 220, 80, 80)),
        TaskStatus.Blocked => new SolidColorBrush(ColorHelper.FromArgb(255, 220, 160, 60)),
        _ => new SolidColorBrush(Colors.Gray)
    };
}
