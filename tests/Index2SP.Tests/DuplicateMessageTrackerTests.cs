using Xunit;

namespace Index2SP.Tests;

public class DuplicateMessageTrackerTests
{
    [Fact]
    public void SameIdWhileProcessing_IsInProgress()
    {
        var tracker = new DuplicateMessageTracker();
        Assert.Equal(DuplicateMessageTracker.Claim.New, tracker.TryClaim("a"));
        Assert.Equal(DuplicateMessageTracker.Claim.InProgress, tracker.TryClaim("a"));
        Assert.Equal(DuplicateMessageTracker.Claim.New, tracker.TryClaim("b"));
    }

    [Fact]
    public void SucceededId_IsRejectedAsAlreadyProcessed()
    {
        var tracker = new DuplicateMessageTracker();
        tracker.TryClaim("a");
        tracker.Complete("a", succeeded: true);
        Assert.Equal(DuplicateMessageTracker.Claim.AlreadyProcessed, tracker.TryClaim("a"));
    }

    [Fact]
    public void FailedId_IsReleasedForRetry()
    {
        var tracker = new DuplicateMessageTracker();
        tracker.TryClaim("a");
        tracker.Complete("a", succeeded: false);
        Assert.Equal(DuplicateMessageTracker.Claim.New, tracker.TryClaim("a"));
    }

    [Fact]
    public void CompletedId_IsForgottenAfterRetention()
    {
        var now = DateTimeOffset.UnixEpoch;
        var tracker = new DuplicateMessageTracker(() => now);
        tracker.TryClaim("a");
        tracker.Complete("a", succeeded: true);

        now += DuplicateMessageTracker.Retention + TimeSpan.FromSeconds(1);
        Assert.Equal(DuplicateMessageTracker.Claim.New, tracker.TryClaim("a"));
    }
}
