using System.Text.Json.Serialization;

namespace Index2SP;

/// <summary>
/// The fields Pebble Index 01 sends in its multipart/form-data webhook.
/// See https://help.repebble.com/en/articles/15724406
/// </summary>
public sealed record PebblePayload
{
    /// <summary>Transcribed voice note. May be absent if the webhook is configured "audio only".</summary>
    public string? Transcription { get; init; }

    /// <summary>Milliseconds since Unix epoch, as sent. Always present.</summary>
    public long? RecordedAtMs { get; init; }

    /// <summary>Device identifier, e.g. "ring". Always present.</summary>
    public string? Client { get; init; }

    /// <summary>True when an "audio" file part was included (we don't persist it, just note it).</summary>
    public bool HasAudio { get; init; }

    /// <summary>Size in bytes from the X-Audio-Size header, when present.</summary>
    public long? AudioSizeBytes { get; init; }

    public DateTimeOffset? RecordedAt =>
        RecordedAtMs is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
}

/// <summary>
/// Body for POST {SuperProductivity}/tasks. Only fields the Local REST API accepts as writable.
/// Nulls are omitted so we never send an explicit null the API might reject.
/// </summary>
public sealed class SpTaskRequest
{
    [JsonPropertyName("title")]
    public required string Title { get; set; }

    [JsonPropertyName("notes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Notes { get; set; }

    [JsonPropertyName("projectId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProjectId { get; set; }

    [JsonPropertyName("tagIds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? TagIds { get; set; }
}

/// <summary>Standard Super Productivity response envelope: { ok, data?, error? }.</summary>
public sealed class SpEnvelope
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("data")] public System.Text.Json.JsonElement Data { get; set; }
    [JsonPropertyName("error")] public SpError? Error { get; set; }

    public sealed class SpError
    {
        [JsonPropertyName("code")] public string? Code { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
    }
}
