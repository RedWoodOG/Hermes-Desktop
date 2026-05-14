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
    public async Task IsFirstRun_FreshHome_ReturnsTrue()
    {
        // SoulService eagerly writes default templates with the UNCONFIGURED
        // marker. A clean home should always start in first-run state.
        await _service.LoadFileAsync(SoulFileType.Soul);
        Assert.IsTrue(_service.IsFirstRun());
    }

    [TestMethod]
    public async Task IsFirstRun_AfterMarkerStripped_ReturnsFalse()
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
    public async Task IsFirstRun_UserMarkerOnly_ReturnsFalse()
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

    // ─────────────────────────────────────────────────────────────────────
    // Regression: Cursor Bugbot — MainWindow used fire-and-forget Task.Run
    // for the marker strip, so navigation completed before disk writes.
    // The fix moves the logic into SoulService.MarkConfiguredAsync, which
    // these tests pin down as the source of truth.
    // ─────────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task MarkConfiguredAsync_BothMarkersPresent_StripsAndExitsFirstRun()
    {
        await _service.LoadFileAsync(SoulFileType.Soul);
        await _service.LoadFileAsync(SoulFileType.User);
        Assert.IsTrue(_service.IsFirstRun(), "Precondition: fresh home is first-run.");

        var changed = await _service.MarkConfiguredAsync();

        Assert.IsTrue(changed, "MarkConfiguredAsync should report it modified at least one file.");
        Assert.IsFalse(_service.IsFirstRun(),
            "After awaiting MarkConfiguredAsync, IsFirstRun must return false synchronously.");

        var soulAfter = await _service.LoadFileAsync(SoulFileType.Soul);
        var userAfter = await _service.LoadFileAsync(SoulFileType.User);
        Assert.IsFalse(soulAfter.Contains("<!-- UNCONFIGURED -->"));
        Assert.IsFalse(userAfter.Contains("<!-- UNCONFIGURED -->"));
    }

    [TestMethod]
    public async Task MarkConfiguredAsync_OnClean_IsNoOp()
    {
        await _service.LoadFileAsync(SoulFileType.Soul);
        await _service.LoadFileAsync(SoulFileType.User);
        await _service.MarkConfiguredAsync();

        var secondCallChanged = await _service.MarkConfiguredAsync();

        Assert.IsFalse(secondCallChanged,
            "Second call must be a no-op so repeated wizard taps are idempotent.");
        Assert.IsFalse(_service.IsFirstRun());
    }

    [TestMethod]
    public async Task MarkConfiguredAsync_OnReturn_DiskStateVisible()
    {
        // The Bugbot finding was: fire-and-forget meant IsFirstRun() could still
        // observe the marker after the wizard returned. Pin down the inverse:
        // the moment MarkConfiguredAsync's Task completes, the disk MUST agree.
        await _service.LoadFileAsync(SoulFileType.Soul);
        await _service.MarkConfiguredAsync();

        // Brand-new SoulService instance pointed at the same dir — simulates the
        // next app launch reading the file from scratch.
        var freshService = new SoulService(_tempDir, NullLogger<SoulService>.Instance);
        Assert.IsFalse(freshService.IsFirstRun(),
            "A new SoulService over the same home must observe the cleared marker after MarkConfiguredAsync returns.");
    }
}
