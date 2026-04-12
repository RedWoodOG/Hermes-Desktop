using Hermes.Agent.Analytics;
using Hermes.Agent.Dreamer;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HermesDesktop.Tests.Services;

[TestClass]
public sealed class DreamerStatusInsightsTests
{
    [TestMethod]
    public void SetStartupFailure_UpdatesSnapshotForUi()
    {
        var status = new DreamerStatus();

        status.SetStartupFailure("missing config");

        var snapshot = status.GetSnapshot();
        Assert.AreEqual("startup-failed", snapshot.Phase);
        Assert.AreEqual("missing config", snapshot.StartupFailureMessage);
    }

    [TestMethod]
    public void RecordDreamerStartupFailure_PersistsTelemetryFields()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            var insights = new InsightsService(dir);

            insights.RecordDreamerStartupFailure(new InvalidOperationException("walk client init failed"));
            insights.Save();

            var reloaded = new InsightsService(dir).GetInsights();
            Assert.IsNotNull(reloaded.Dreamer);
            Assert.AreEqual(1, reloaded.Dreamer!.StartupFailures);
            Assert.AreEqual("walk client init failed", reloaded.Dreamer.LastStartupFailureMessage);
            Assert.IsNotNull(reloaded.Dreamer.LastStartupFailureUtc);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}
