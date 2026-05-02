using Hermes.Agent.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HermesDesktop.Tests.Tools;

[TestClass]
public sealed class WindowsAutomationTreeFormatterTests
{
    [TestMethod]
    public void Format_WithNestedNodes_RendersReadableTreeWithRefs()
    {
        var tree = new[]
        {
            new WindowsAutomationNode(
                Ref: "uia_1",
                ControlType: "Window",
                Name: "Finance App",
                AutomationId: "MainWindow",
                ClassName: "WindowsForms10.Window.8.app.0.123",
                Depth: 0,
                Children:
                [
                    new WindowsAutomationNode(
                        Ref: "uia_2",
                        ControlType: "Button",
                        Name: "Approve",
                        AutomationId: "approveButton",
                        ClassName: "Button",
                        Depth: 1,
                        Children: [])
                ])
        };

        var result = WindowsAutomationTreeFormatter.Format(tree);

        StringAssert.Contains(result, "Window: Finance App [ref=uia_1 automationId=MainWindow class=WindowsForms10.Window.8.app.0.123]");
        StringAssert.Contains(result, "  Button: Approve [ref=uia_2 automationId=approveButton class=Button]");
    }

    [TestMethod]
    public void Format_WithMissingName_RendersUnnamedPlaceholder()
    {
        var tree = new[]
        {
            new WindowsAutomationNode(
                Ref: "uia_1",
                ControlType: "Pane",
                Name: "",
                AutomationId: "",
                ClassName: "",
                Depth: 0,
                Children: [])
        };

        var result = WindowsAutomationTreeFormatter.Format(tree);

        StringAssert.Contains(result, "Pane: (unnamed) [ref=uia_1]");
    }
}
