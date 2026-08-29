using System.Text;

namespace Index2SP;

/// <summary>
/// Turns a Pebble Index 01 payload into a Super Productivity task request.
/// Title = the transcription (truncated to the API limit); full transcription plus capture
/// metadata go into notes.
/// </summary>
public static class PayloadConverter
{
    public sealed class ConversionException(string message) : Exception(message);

    public static SpTaskRequest ToTask(PebblePayload payload, AppConfig config)
    {
        var transcription = (payload.Transcription ?? string.Empty).Trim();

        if (transcription.Length == 0)
        {
            // "audio only" webhook, or transcription failed. We have nothing to name a task.
            throw new ConversionException(
                "payload has no transcription text (webhook is likely set to 'audio only', " +
                "or transcription failed) — nothing to create a task from");
        }

        var max = config.TitleMaxLength;
        var title = transcription.Length <= max
            ? transcription
            : transcription[..(max - 1)].TrimEnd() + "…";

        // Collapse internal newlines in the title only; keep them in notes.
        title = CollapseWhitespace(title);

        var notes = BuildNotes(transcription, payload);

        var task = new SpTaskRequest
        {
            Title = title,
            Notes = notes,
        };

        if (!string.IsNullOrWhiteSpace(config.SuperProductivity.ProjectId))
            task.ProjectId = config.SuperProductivity.ProjectId.Trim();

        if (config.SuperProductivity.TagIds is { Count: > 0 } tags)
            task.TagIds = tags.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList();

        return task;
    }

    private static string BuildNotes(string transcription, PebblePayload payload)
    {
        var sb = new StringBuilder();
        sb.Append(transcription);
        sb.Append("\n\n---\n");
        sb.Append("Captured via Pebble Index 01\n");

        if (payload.RecordedAt is { } recorded)
        {
            sb.Append("Recorded: ")
              .Append(recorded.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz"))
              .Append("  (")
              .Append(recorded.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"))
              .Append(")\n");
        }

        if (!string.IsNullOrWhiteSpace(payload.Client))
            sb.Append("Client: ").Append(payload.Client!.Trim()).Append('\n');

        if (payload.HasAudio)
        {
            sb.Append("Audio: attached to webhook");
            if (payload.AudioSizeBytes is { } bytes)
                sb.Append(" (").Append(FormatBytes(bytes)).Append(')');
            sb.Append('\n');
        }

        return sb.ToString().TrimEnd();
    }

    private static string CollapseWhitespace(string s)
    {
        var sb = new StringBuilder(s.Length);
        var lastWasSpace = false;
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace) sb.Append(' ');
                lastWasSpace = true;
            }
            else
            {
                sb.Append(ch);
                lastWasSpace = false;
            }
        }
        return sb.ToString().Trim();
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }
}
