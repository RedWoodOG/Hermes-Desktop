using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HermesDesktop.Views.Controls;

public enum ApprovalDecision { AllowOnce, AllowSession, AlwaysAllow, Deny }

public sealed partial class ApprovalCard : UserControl
{
    private TaskCompletionSource<ApprovalDecision>? _tcs;

    public ApprovalCard()
    {
        InitializeComponent();
    }

    public Task<ApprovalDecision> ShowApprovalAsync(string command)
    {
        CommandText.Text = command;
        _tcs = new TaskCompletionSource<ApprovalDecision>();
        return _tcs.Task;
    }

    private void Resolve(ApprovalDecision decision)
    {
        _tcs?.TrySetResult(decision);
        Visibility = Visibility.Collapsed;
    }

    private void AllowOnce_Click(object sender, RoutedEventArgs e) => Resolve(ApprovalDecision.AllowOnce);
    private void AllowSession_Click(object sender, RoutedEventArgs e) => Resolve(ApprovalDecision.AllowSession);
    private void AlwaysAllow_Click(object sender, RoutedEventArgs e) => Resolve(ApprovalDecision.AlwaysAllow);
    private void Deny_Click(object sender, RoutedEventArgs e) => Resolve(ApprovalDecision.Deny);
}
