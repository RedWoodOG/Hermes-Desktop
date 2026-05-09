using Hermes.Agent.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HermesDesktop.Tests.Services;

[TestClass]
public sealed class StreamingTextAccumulatorTests
{
    [TestMethod]
    public void FlushIfDue_HoldsSmallDeltaUntilInterval()
    {
        var clock = new ManualTimeProvider();
        var accumulator = new StreamingTextAccumulator(clock, TimeSpan.FromMilliseconds(50), maxBufferedChars: 100);

        accumulator.Append("hello");

        Assert.AreEqual("", accumulator.FlushIfDue());

        clock.Advance(TimeSpan.FromMilliseconds(50));

        Assert.AreEqual("hello", accumulator.FlushIfDue());
    }

    [TestMethod]
    public void FlushIfDue_FlushesImmediatelyWhenBufferIsLarge()
    {
        var accumulator = new StreamingTextAccumulator(
            new ManualTimeProvider(),
            TimeSpan.FromMinutes(1),
            maxBufferedChars: 4);

        accumulator.Append("hello");

        Assert.AreEqual("hello", accumulator.FlushIfDue());
    }

    [TestMethod]
    public void FlushIfDue_DoesNotSplitSurrogatePairDuringTimedFlush()
    {
        var clock = new ManualTimeProvider();
        var accumulator = new StreamingTextAccumulator(clock, TimeSpan.Zero, maxBufferedChars: 100);

        var emoji = char.ConvertFromUtf32(0x1F680);
        accumulator.Append(emoji[0].ToString());

        Assert.AreEqual("", accumulator.FlushIfDue());

        accumulator.Append(emoji[1].ToString());

        Assert.AreEqual(emoji, accumulator.FlushIfDue());
    }

    [TestMethod]
    public void Flush_ReleasesPendingTextRegardlessOfInterval()
    {
        var accumulator = new StreamingTextAccumulator(
            new ManualTimeProvider(),
            TimeSpan.FromMinutes(1),
            maxBufferedChars: 100);

        accumulator.Append("final");

        Assert.AreEqual("final", accumulator.Flush());
        Assert.IsFalse(accumulator.HasPending);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }
}
