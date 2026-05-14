using System.IO;
using System.Threading.Tasks;
using Hermes.Agent.Soul;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HermesDesktop.Tests.Soul;

/// <summary>
/// Bundle E.8 — sanity tests for the first-run routing primitive. The
/// MainWindow only consults <see cref="SoulService.IsFirstRun"/> + strips the
/// <c>UNCONFIGURED</c> marker, so we validate that contract here.
/// </summary>
[TestClass]
public class FirstRunFlowTests
{
    private string _tempDir = "";
    private SoulService _service = null!;

    [TestInitialize]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"hermes-firstrun-{System.Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _service = new SoulService(_tempDir, NullLogger<SoulService>.Instance);
    }

    [TestCleanup]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public async Task FreshHomeIsFirstRun()
    {
        // SoulService eagerly writes default templates with the UNCONFIGURED
        // marker. A clean home should always start in first-run state.
        await _service.LoadFileAsync(SoulFileType.Soul);
        Assert.IsTrue(_service.IsFirstRun());
    }

    [TestMethod]
    public async Task StrippingUnconfiguredMarkerExitsFirstRun()
    {
        var soul = await _service.LoadFileAsync(SoulFileType.Soul);
        StringAssert.Contains(soul, "<!-- UNCONFIGURED -->");

        // Strip the marker with any surrounding whitespace (template may use CRLF
        // or LF depending on git autocrlf — we accept both).
        var cleaned = System.Text.RegularExpressions.Regex.Replace(
            soul, @"<!-- UNCONFIGURED -->\r?\n?", "");
        await _service.SaveFileAsync(SoulFileType.Soul, cleaned);

        Assert.IsFalse(_service.IsFirstRun(),
            "After the wizard strips the UNCONFIGURED marker, IsFirstRun must return false on next launch.");
    }

    [TestMethod]
    public async Task UserMarkerAloneDoesNotKeepFirstRunActive()
    {
        // IsFirstRun only inspects SOUL.md. Even if USER.md still has the marker
        // (e.g. user skipped that question), the wizard owns the SOUL.md edit.
        await _service.LoadFileAsync(SoulFileType.User);
        var soul = await _service.LoadFileAsync(SoulFileType.Soul);
        var cleaned = System.Text.RegularExpressions.Regex.Replace(
            soul, @"<!-- UNCONFIGURED -->\r?\n?", "");
        await _service.SaveFileAsync(SoulFileType.Soul, cleaned);

        Assert.IsFalse(_service.IsFirstRun());
    }
}
