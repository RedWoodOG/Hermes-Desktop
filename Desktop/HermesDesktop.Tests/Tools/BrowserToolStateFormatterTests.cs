using Hermes.Agent.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HermesDesktop.Tests.Tools;

[TestClass]
public sealed class BrowserToolStateFormatterTests
{
    [TestMethod]
    public void FormatBrowserState_RendersCompactPageAndRefs()
    {
        var state = new BrowserStateSnapshot(
            Url: "https://example.com/search",
            Title: "Search",
            Viewport: new BrowserViewport(1280, 720),
            Scroll: new BrowserScrollPosition(0, 240, 0, 1800),
            ClickableRefs:
            [
                new BrowserElementRef(
                    Ref: "#submit",
                    Role: "button",
                    Tag: "button",
                    Text: "Search now")
            ],
            InputRefs:
            [
                new BrowserElementRef(
                    Ref: "input[name=\"q\"]",
                    Tag: "input",
                    Type: "search",
                    Name: "q",
                    Placeholder: "Search terms",
                    Value: "hermes")
            ]);

        var output = BrowserTool.FormatBrowserState(state);

        StringAssert.Contains(output, "url: \"https://example.com/search\"");
        StringAssert.Contains(output, "title: \"Search\"");
        StringAssert.Contains(output, "viewport: { width: 1280, height: 720 }");
        StringAssert.Contains(output, "scroll: { x: 0, y: 240, maxX: 0, maxY: 1800 }");
        StringAssert.Contains(output, "clickable_refs");
        StringAssert.Contains(output, "ref: \"#submit\"");
        StringAssert.Contains(output, "text: \"Search now\"");
        StringAssert.Contains(output, "input_refs");
        StringAssert.Contains(output, "ref: \"input[name=\\\"q\\\"]\"");
        StringAssert.Contains(output, "placeholder: \"Search terms\"");
        StringAssert.Contains(output, "value: \"hermes\"");
    }

    [TestMethod]
    public void FormatBrowserState_WithNoRefs_UsesCompactEmptyMarkers()
    {
        var state = new BrowserStateSnapshot(
            Url: "about:blank",
            Title: "",
            Viewport: new BrowserViewport(0, 0),
            Scroll: new BrowserScrollPosition(0, 0, 0, 0),
            ClickableRefs: [],
            InputRefs: []);

        var output = BrowserTool.FormatBrowserState(state);

        StringAssert.Contains(output, "clickable_refs: [");
        StringAssert.Contains(output, "input_refs: [");
        StringAssert.Contains(output, "// none");
    }
}
