using Hermes.Agent.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HermesDesktop.Tests.Tools;

[TestClass]
public sealed class PostEditDiagnosticsHookTests
{
    [TestMethod]
    public void DetectChangedFilePaths_ReadsCamelCaseFilePathForMutatingTools()
    {
        var paths = PostEditDiagnosticsHook.DetectChangedFilePaths(
            "edit_file",
            """{"filePath":"src/Core/Agent.cs","oldString":"a","newString":"b"}""");

        CollectionAssert.AreEqual(new[] { "src/Core/Agent.cs" }, paths.ToArray());
    }

    [TestMethod]
    public void DetectChangedFilePaths_ReadsSnakeCaseFilePath()
    {
        var paths = PostEditDiagnosticsHook.DetectChangedFilePaths(
            "patch",
            """{"file_path":"src/Tools/PatchTool.cs","patch":"@@ -1 +1 @@"}""");

        CollectionAssert.AreEqual(new[] { "src/Tools/PatchTool.cs" }, paths.ToArray());
    }

    [TestMethod]
    public void DetectChangedFilePaths_IgnoresNonMutatingTools()
    {
        var paths = PostEditDiagnosticsHook.DetectChangedFilePaths(
            "read_file",
            """{"filePath":"src/Core/Agent.cs"}""");

        Assert.AreEqual(0, paths.Count);
    }

    [TestMethod]
    public void DetectChangedFilePaths_InvalidJsonReturnsEmptyList()
    {
        var paths = PostEditDiagnosticsHook.DetectChangedFilePaths("write_file", "{not-json");

        Assert.AreEqual(0, paths.Count);
    }

    [TestMethod]
    public void FormatDiagnostics_IncludesSeverityLocationAndMessage()
    {
        var formatted = PostEditDiagnosticsHook.FormatDiagnostics(new[]
        {
            new PostEditDiagnostic(
                "src/Core/Agent.cs",
                12,
                8,
                "Example diagnostic",
                PostEditDiagnosticSeverity.Warning)
        });

        StringAssert.StartsWith(formatted, "Post-edit diagnostics:");
        StringAssert.Contains(formatted, "[WARN] src/Core/Agent.cs:12:8: Example diagnostic");
    }

    [TestMethod]
    public async Task BuildReportAsync_DefaultOptionsStaySilent()
    {
        var hook = new PostEditDiagnosticsHook();
        var toolCall = new ToolCall
        {
            Id = "call-1",
            Name = "write_file",
            Arguments = """{"filePath":"src/NewFile.cs","content":"text"}"""
        };

        var report = await hook.BuildReportAsync(toolCall, ToolResult.Ok("wrote file"), CancellationToken.None);

        Assert.IsNull(report);
    }

    [TestMethod]
    public async Task BuildReportAsync_CanEmitOptInNoDiagnosticsMessage()
    {
        var hook = new PostEditDiagnosticsHook(
            new NoOpPostEditDiagnosticsProvider(),
            new PostEditDiagnosticsOptions
            {
                Enabled = true,
                IncludeNoDiagnosticsMessage = true
            });
        var toolCall = new ToolCall
        {
            Id = "call-1",
            Name = "write_file",
            Arguments = """{"filePath":"src/NewFile.cs","content":"text"}"""
        };

        var report = await hook.BuildReportAsync(toolCall, ToolResult.Ok("wrote file"), CancellationToken.None);

        Assert.IsNotNull(report);
        StringAssert.Contains(report, "Post-edit diagnostics: no diagnostics for changed file(s):");
        StringAssert.Contains(report, "- src/NewFile.cs");
    }

    [TestMethod]
    public async Task BuildReportAsync_CanEmitOptInDisabledMessage()
    {
        var hook = new PostEditDiagnosticsHook(
            options: new PostEditDiagnosticsOptions
            {
                IncludeDisabledMessage = true
            });
        var toolCall = new ToolCall
        {
            Id = "call-1",
            Name = "patch",
            Arguments = """{"filePath":"src/Core/Agent.cs","patch":"@@ -1 +1 @@"}"""
        };

        var report = await hook.BuildReportAsync(toolCall, ToolResult.Ok("patched"), CancellationToken.None);

        Assert.IsNotNull(report);
        StringAssert.Contains(report, "Post-edit diagnostics disabled for changed file(s):");
        StringAssert.Contains(report, "- src/Core/Agent.cs");
    }
}
