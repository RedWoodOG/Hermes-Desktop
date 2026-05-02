namespace Hermes.Agent.Tools;

using System.Text;

public sealed record WindowsAutomationNode(
    string Ref,
    string ControlType,
    string Name,
    string AutomationId,
    string ClassName,
    int Depth,
    IReadOnlyList<WindowsAutomationNode> Children);

public static class WindowsAutomationTreeFormatter
{
    public static string Format(IEnumerable<WindowsAutomationNode> roots)
    {
        var sb = new StringBuilder();

        foreach (var root in roots)
        {
            AppendNode(sb, root);
        }

        return sb.ToString().TrimEnd();
    }

    private static void AppendNode(StringBuilder sb, WindowsAutomationNode node)
    {
        var indent = new string(' ', Math.Max(0, node.Depth) * 2);
        var name = string.IsNullOrWhiteSpace(node.Name) ? "(unnamed)" : node.Name.Trim();

        sb.Append(indent);
        sb.Append(string.IsNullOrWhiteSpace(node.ControlType) ? "Control" : node.ControlType);
        sb.Append(": ");
        sb.Append(name);
        sb.Append(" [ref=");
        sb.Append(node.Ref);

        AppendMetadata(sb, "automationId", node.AutomationId);
        AppendMetadata(sb, "class", node.ClassName);

        sb.AppendLine("]");

        foreach (var child in node.Children)
        {
            AppendNode(sb, child);
        }
    }

    private static void AppendMetadata(StringBuilder sb, string name, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        sb.Append(' ');
        sb.Append(name);
        sb.Append('=');
        sb.Append(value.Trim());
    }
}
