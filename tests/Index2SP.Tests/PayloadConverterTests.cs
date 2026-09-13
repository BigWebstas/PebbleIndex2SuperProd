using Xunit;

namespace Index2SP.Tests;

public class PayloadConverterTests
{
    private static AppConfig NewConfig(int titleMaxLength = 300) => new()
    {
        TitleMaxLength = titleMaxLength,
    };

    [Fact]
    public void ToTask_UsesTranscriptionAsTitle()
    {
        var payload = new PebblePayload { Transcription = "  Buy milk  " };

        var task = PayloadConverter.ToTask(payload, NewConfig());

        Assert.Equal("Buy milk", task.Title);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ToTask_ThrowsWhenTranscriptionIsBlank(string? transcription)
    {
        var payload = new PebblePayload { Transcription = transcription };

        Assert.Throws<PayloadConverter.ConversionException>(() => PayloadConverter.ToTask(payload, NewConfig()));
    }

    [Fact]
    public void ToTask_TruncatesTitleAtConfiguredLength()
    {
        var payload = new PebblePayload { Transcription = new string('a', 50) };

        var task = PayloadConverter.ToTask(payload, NewConfig(titleMaxLength: 10));

        Assert.Equal(10, task.Title.Length);
        Assert.EndsWith("…", task.Title);
    }

    [Fact]
    public void ToTask_CollapsesInternalWhitespaceInTitle()
    {
        var payload = new PebblePayload { Transcription = "Line one\n\nLine   two" };

        var task = PayloadConverter.ToTask(payload, NewConfig());

        Assert.Equal("Line one Line two", task.Title);
    }

    [Fact]
    public void ToTask_NotesContainFullTranscriptionAndCaptureMetadata()
    {
        var payload = new PebblePayload
        {
            Transcription = "Full transcription text",
            Client = "ring",
            HasAudio = true,
            AudioSizeBytes = 2048,
            RecordedAtMs = 1_700_000_000_000,
        };

        var task = PayloadConverter.ToTask(payload, NewConfig());

        Assert.NotNull(task.Notes);
        Assert.Contains("Full transcription text", task.Notes);
        Assert.Contains("Captured via Pebble Index 01", task.Notes);
        Assert.Contains("Client: ring", task.Notes);
        Assert.Contains("Audio: attached to webhook", task.Notes);
        Assert.Contains("2 KB", task.Notes);
    }

    [Fact]
    public void ToTask_AppliesStaticProjectAndTags()
    {
        var config = NewConfig();
        config.SuperProductivity.ProjectId = "proj-1";
        config.SuperProductivity.TagIds = ["tag-a", "tag-b"];
        var payload = new PebblePayload { Transcription = "Do the thing" };

        var task = PayloadConverter.ToTask(payload, config);

        Assert.Equal("proj-1", task.ProjectId);
        Assert.Equal(["tag-a", "tag-b"], task.TagIds);
    }

    [Fact]
    public void ToTask_LeavesProjectAndTagsNullWhenNotConfigured()
    {
        var payload = new PebblePayload { Transcription = "Do the thing" };

        var task = PayloadConverter.ToTask(payload, NewConfig());

        Assert.Null(task.ProjectId);
        Assert.Null(task.TagIds);
    }
}
