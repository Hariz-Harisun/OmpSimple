using Xunit;

namespace OmpSimple.Tests;

public class RateLimitTests
{
    [Fact]
    public void Mouser_DefaultRateRecordsDailyMinuteAndBatchLimits()
    {
        using var state = TestState.Create();
        var tracker = DistributorRateTracker.CreateDefault(state.Path);

        var mouser = tracker.GetInfo("Mouser");

        Assert.Equal(30, mouser.PerMinuteLimit);
        Assert.Equal(1000, mouser.DailyLimit);
        Assert.Equal(50, mouser.BatchSize);
    }

    [Fact]
    public void RemainingDisplay_ShowsRemainingPercentageAndCount()
    {
        using var state = TestState.Create();
        var tracker = DistributorRateTracker.CreateDefault(state.Path);
        tracker.RecordCalls("Mouser", 304);

        Assert.Equal(
            "Remaining [##############------] 69.6% (696 / 1000)",
            tracker.GetRemainingDisplay("Mouser"));
    }

    [Fact]
    public void StateFile_PersistsCallsForNextRun()
    {
        using var state = TestState.Create();
        var firstRun = DistributorRateTracker.CreateDefault(state.Path);
        firstRun.RecordCalls("Mouser", 304);

        var nextRun = DistributorRateTracker.CreateDefault(state.Path);

        Assert.Equal(
            "Remaining [##############------] 69.6% (696 / 1000)",
            nextRun.GetRemainingDisplay("Mouser"));
    }

    [Fact]
    public void RemainingDisplay_ClampsUsageAtDailyLimit()
    {
        using var state = TestState.Create();
        var tracker = DistributorRateTracker.CreateDefault(state.Path);
        tracker.RecordCalls("Mouser", 1200);

        Assert.Equal(
            "Remaining [--------------------] 0.0% (0 / 1000)",
            tracker.GetRemainingDisplay("Mouser"));
    }

    private sealed class TestState : IDisposable
    {
        public string DirectoryPath { get; }
        public string Path => System.IO.Path.Combine(DirectoryPath, "rate-limits.json");

        private TestState(string directoryPath) => DirectoryPath = directoryPath;

        public static TestState Create()
        {
            var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "OmpSimpleTest_" + Guid.NewGuid());
            Directory.CreateDirectory(directory);
            return new TestState(directory);
        }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
                Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}
