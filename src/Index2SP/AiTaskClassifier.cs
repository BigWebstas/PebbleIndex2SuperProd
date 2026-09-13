using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;

namespace Index2SP;

/// <summary>
/// Asks Claude to pick the Super Productivity project and tags for a transcription, from the
/// project/tag lists the caller already pulled from the SP API. Never throws — any failure
/// (disabled, no key, network, timeout, malformed response, an id that isn't in the lists
/// passed in) returns null, and the caller keeps whatever PayloadConverter already set from
/// the static config.
/// </summary>
public sealed class AiTaskClassifier
{
    private readonly AppConfig.AiClassifierConfig _config;
    private readonly Logger _log;

    public AiTaskClassifier(AppConfig.AiClassifierConfig config, Logger log)
    {
        _config = config;
        _log = log;
    }

    public sealed record Classification(string? ProjectId, List<string> TagIds, bool IsNote);

    public async Task<Classification?> ClassifyAsync(
        string transcription,
        IReadOnlyList<SpNamedItem> projects,
        IReadOnlyList<SpNamedItem> tags,
        CancellationToken ct)
    {
        if (!_config.Enabled || string.IsNullOrWhiteSpace(_config.ApiKey)) return null;
        if (projects.Count == 0 && tags.Count == 0) return null;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_config.TimeoutSeconds));

        try
        {
            using var client = new AnthropicClient { ApiKey = _config.ApiKey };
            var response = await client.Messages.Create(new MessageCreateParams
            {
                Model = _config.Model,
                MaxTokens = 256,
                Messages = [new() { Role = Role.User, Content = BuildPrompt(transcription, projects, tags) }],
                Tools = [ClassifyTool],
                ToolChoice = new ToolChoiceTool { Name = ClassifyToolName },
            }, cancellationToken: cts.Token);

            foreach (var block in response.Content)
            {
                if (!block.TryPickToolUse(out ToolUseBlock? toolUse) || toolUse.Name != ClassifyToolName)
                    continue;

                var projectId = toolUse.Input.TryGetValue("projectId", out var pEl) && pEl.ValueKind == JsonValueKind.String
                    ? pEl.GetString()
                    : null;

                var tagIds = new List<string>();
                if (toolUse.Input.TryGetValue("tagIds", out var tEl) && tEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in tEl.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s)
                            tagIds.Add(s);
                }

                var resolvedProjectId = projectId is not null && projects.Any(p => p.Id == projectId) ? projectId : null;
                var resolvedTagIds = tagIds.Where(t => tags.Any(x => x.Id == t)).ToList();
                var isNote = toolUse.Input.TryGetValue("isNote", out var nEl) && nEl.ValueKind == JsonValueKind.True;

                _log.Info($"AI classify: project={resolvedProjectId ?? "(none)"}, tags=[{string.Join(',', resolvedTagIds)}], isNote={isNote}");
                return new Classification(resolvedProjectId, resolvedTagIds, isNote);
            }

            _log.Warn("AI classify: response had no classify tool_use block");
            return null;
        }
        catch (Exception ex) when (ex is AnthropicException or OperationCanceledException or JsonException)
        {
            _log.Warn($"AI classify failed, using static config instead: {ex.Message}");
            return null;
        }
    }

    private const string ClassifyToolName = "classify";

    private static readonly Tool ClassifyTool = new()
    {
        Name = ClassifyToolName,
        Description = "Pick the Super Productivity project and tags for this voice-note transcription.",
        InputSchema = new InputSchema
        {
            Properties = new Dictionary<string, JsonElement>
            {
                ["projectId"] = JsonSerializer.SerializeToElement(new
                {
                    type = new[] { "string", "null" },
                    description = "id from the project list, or null if none fit well",
                }),
                ["tagIds"] = JsonSerializer.SerializeToElement(new
                {
                    type = "array",
                    items = new { type = "string" },
                    description = "ids from the tag list, or [] if none fit",
                }),
                ["isNote"] = JsonSerializer.SerializeToElement(new
                {
                    type = "boolean",
                    description = "true if this is a note, idea, or reference to save rather " +
                                   "than an actionable to-do (e.g. a fact, a quote, something to remember)",
                }),
            },
            Required = ["projectId", "tagIds", "isNote"],
        },
    };

    private static string BuildPrompt(string transcription, IReadOnlyList<SpNamedItem> projects, IReadOnlyList<SpNamedItem> tags)
    {
        var sb = new StringBuilder();
        sb.Append("You are filing a voice-note transcription into Super Productivity. ");
        sb.Append("Pick the single best-fitting project and zero or more fitting tags from the lists below, ");
        sb.Append("based on their titles. Only use ids that appear in these lists. ");
        sb.Append("If nothing fits well, return null for projectId and [] for tagIds — do not force a match. ");
        sb.Append("Also decide whether this is a note to save (a fact, idea, or reference) rather than an ");
        sb.Append("actionable to-do, and set isNote accordingly.\n\n");

        sb.Append("Projects:\n");
        if (projects.Count == 0) sb.Append("(none)\n");
        foreach (var p in projects) sb.Append("- ").Append(p.Id).Append(": ").Append(p.Title).Append('\n');

        sb.Append("\nTags:\n");
        if (tags.Count == 0) sb.Append("(none)\n");
        foreach (var t in tags) sb.Append("- ").Append(t.Id).Append(": ").Append(t.Title).Append('\n');

        sb.Append("\nTranscription:\n").Append(transcription);
        return sb.ToString();
    }
}
