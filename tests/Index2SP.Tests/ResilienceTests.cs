using System.Net;
using System.Net.Sockets;
using Xunit;

namespace Index2SP.Tests;

public sealed class ResilienceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "index2sp-tests-" + Guid.NewGuid().ToString("N"));

    public ResilienceTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly PebblePayload Payload = new() { Transcription = "buy milk", Client = "ring" };

    [Fact]
    public void Tracker_RemembersCompletedIdsAcrossRestart()
    {
        var path = Path.Combine(_dir, "seen.json");
        var first = new DuplicateMessageTracker(persistPath: path);
        Assert.Equal(DuplicateMessageTracker.Claim.New, first.TryClaim("a"));
        first.Complete("a", succeeded: true);
        Assert.Equal(DuplicateMessageTracker.Claim.New, first.TryClaim("b"));
        first.Complete("b", succeeded: false);

        var second = new DuplicateMessageTracker(persistPath: path);
        Assert.Equal(DuplicateMessageTracker.Claim.AlreadyProcessed, second.TryClaim("a"));
        Assert.Equal(DuplicateMessageTracker.Claim.New, second.TryClaim("b"));
    }

    [Fact]
    public void Tracker_StartsEmptyWhenFileIsCorrupt()
    {
        var path = Path.Combine(_dir, "seen.json");
        File.WriteAllText(path, "{not json");
        Assert.Equal(DuplicateMessageTracker.Claim.New, new DuplicateMessageTracker(persistPath: path).TryClaim("a"));
    }

    [Fact]
    public void Inbox_ReturnsEntriesLeftBehind_ButNotRemovedOnes()
    {
        var inbox = new Inbox(_dir, new Logger());
        inbox.Save("left-behind", Payload);
        inbox.Save("finished", Payload);
        inbox.Remove("finished");

        var pending = new Inbox(_dir, new Logger()).TakePending();
        var item = Assert.Single(pending);
        Assert.Equal("left-behind", item.Id);
        Assert.Equal("buy milk", item.Payload.Transcription);
        Assert.Equal(1, item.Attempts);
    }

    [Fact]
    public void Inbox_GivesUpOnAnEntryThatKeepsCrashing()
    {
        new Inbox(_dir, new Logger()).Save("poison", Payload);

        for (var i = 0; i < Inbox.MaxAttempts; i++)
            Assert.Single(new Inbox(_dir, new Logger()).TakePending());

        Assert.Empty(new Inbox(_dir, new Logger()).TakePending());
        Assert.Single(Directory.GetFiles(Path.Combine(_dir, "inbox", "failed")));
    }

    [Fact]
    public void Inbox_DropsEntriesTooOldToReplay()
    {
        var now = DateTimeOffset.UtcNow;
        new Inbox(_dir, new Logger(), () => now - Inbox.MaxAge - TimeSpan.FromMinutes(1)).Save("stale", Payload);

        Assert.Empty(new Inbox(_dir, new Logger(), () => now).TakePending());
    }

    [Fact]
    public void SelfRestart_StopsAfterTooManyCrashesInTheWindow()
    {
        var now = DateTimeOffset.UtcNow;
        DateTimeOffset[] Crashes(int n) => Enumerable.Range(0, n).Select(i => now - TimeSpan.FromMinutes(i)).ToArray();

        Assert.True(SelfRestart.ShouldRestart(Crashes(SelfRestart.MaxRestarts), now));
        Assert.False(SelfRestart.ShouldRestart(Crashes(SelfRestart.MaxRestarts + 1), now));
        // Old crashes outside the window don't count.
        Assert.True(SelfRestart.ShouldRestart(Crashes(SelfRestart.MaxRestarts).Append(now - TimeSpan.FromHours(2)), now));
    }

    [Fact]
    public async Task Server_RetriesBindingUntilTheOldInstanceReleasesThePort()
    {
        var holder = new TcpListener(IPAddress.Loopback, 0);
        holder.Start();
        var port = ((IPEndPoint)holder.LocalEndpoint).Port;
        _ = Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ => holder.Stop());

        await using var server = new WebhookServer(new AppConfig { Port = port }, new Logger(), null!, null!, null!);
        await server.StartAsync();

        Assert.True(server.IsRunning);
        await server.StopAsync();
    }
}
