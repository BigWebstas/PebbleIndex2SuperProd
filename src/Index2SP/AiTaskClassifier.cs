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

    public sealed record Classification(
        string? ProjectId, List<string> TagIds, bool IsNote, bool IsShopping, List<string> ShoppingItems, List<string> JoplinTagIds);

    /// <param name="joplinTags">Existing Joplin tags, only consulted (and only asked of the AI)
    /// while <see cref="AppConfig.AiClassifierConfig.RequireTags"/> is on. Pass an empty list
    /// when Joplin isn't enabled.</param>
    public async Task<Classification?> ClassifyAsync(
        string transcription,
        IReadOnlyList<SpNamedItem> projects,
        IReadOnlyList<SpNamedItem> tags,
        IReadOnlyList<SpNamedItem> joplinTags,
        CancellationToken ct)
    {
        if (!_config.Enabled || string.IsNullOrWhiteSpace(_config.ApiKey)) return null;
        if (projects.Count == 0 && tags.Count == 0) return null;

        var requireTags = _config.RequireTags;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_config.TimeoutSeconds));

        try
        {
            using var client = new AnthropicClient { ApiKey = _config.ApiKey };
            var response = await client.Messages.Create(new MessageCreateParams
            {
                Model = _config.Model,
                MaxTokens = 256,
                Messages = [new() { Role = Role.User, Content = BuildPrompt(transcription, projects, tags, joplinTags, requireTags) }],
                Tools = [BuildClassifyTool(requireTags)],
                ToolChoice = new ToolChoiceTool { Name = ClassifyToolName },
            }, cancellationToken: cts.Token);

            foreach (var block in response.Content)
            {
                if (!block.TryPickToolUse(out ToolUseBlock? toolUse) || toolUse.Name != ClassifyToolName)
                    continue;

                var projectId = toolUse.Input.TryGetValue("projectId", out var pEl) && pEl.ValueKind == JsonValueKind.String
                    ? pEl.GetString()
                    : null;

                var tagIds = ReadStringArray(toolUse.Input, "tagIds");
                var isNote = toolUse.Input.TryGetValue("isNote", out var nEl) && nEl.ValueKind == JsonValueKind.True;
                var isShopping = toolUse.Input.TryGetValue("isShopping", out var sEl) && sEl.ValueKind == JsonValueKind.True;
                var shoppingItems = ReadStringArray(toolUse.Input, "shoppingItems");
                var joplinTagIds = requireTags ? ReadStringArray(toolUse.Input, "joplinTagIds") : new List<string>();

                var resolvedProjectId = projectId is not null && projects.Any(p => p.Id == projectId) ? projectId : null;
                var resolvedTagIds = tagIds.Where(t => tags.Any(x => x.Id == t)).ToList();
                var resolvedJoplinTagIds = joplinTagIds.Where(t => joplinTags.Any(x => x.Id == t)).ToList();

                _log.Info($"AI classify: project={resolvedProjectId ?? "(none)"}, tags=[{string.Join(',', resolvedTagIds)}], " +
                          $"isNote={isNote}, isShopping={isShopping}, shoppingItems=[{string.Join(',', shoppingItems)}], " +
                          $"joplinTags=[{string.Join(',', resolvedJoplinTagIds)}]");
                return new Classification(resolvedProjectId, resolvedTagIds, isNote, isShopping, shoppingItems, resolvedJoplinTagIds);
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

    private static List<string> ReadStringArray(IReadOnlyDictionary<string, JsonElement> input, string key)
    {
        var result = new List<string>();
        if (input.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s)
                    result.Add(s.Trim());
        }
        return result;
    }

    private const string ClassifyToolName = "classify";

    private static JsonElement BuildTagIdsSchema(bool requireTags)
    {
        if (requireTags)
        {
            return JsonSerializer.SerializeToElement(new
            {
                type = "array",
                items = new { type = "string" },
                minItems = 1,
                description = "Required — at least one id from the tag list below. Pick the closest fit " +
                               "if nothing is a perfect match. Never return an empty array.",
            });
        }

        return JsonSerializer.SerializeToElement(new
        {
            type = "array",
            items = new { type = "string" },
            description = "ids from the tag list, or [] if none fit",
        });
    }

    private static Tool BuildClassifyTool(bool requireTags)
    {
        var properties = new Dictionary<string, JsonElement>
        {
            ["projectId"] = JsonSerializer.SerializeToElement(new
            {
                type = new[] { "string", "null" },
                description = "id from the project list, or null if none fit well",
            }),
            ["tagIds"] = BuildTagIdsSchema(requireTags),
            ["isNote"] = JsonSerializer.SerializeToElement(new
            {
                type = "boolean",
                description = "true if this is a note, idea, or reference to save rather " +
                               "than an actionable to-do (e.g. a fact, a quote, something to remember)",
            }),
            ["isShopping"] = JsonSerializer.SerializeToElement(new
            {
                type = "boolean",
                description = "true if this is a shopping / errands item — something to buy or pick up " +
                               "(e.g. groceries, a store run, an online order)",
            }),
            ["shoppingItems"] = JsonSerializer.SerializeToElement(new
            {
                type = "array",
                items = new { type = "string" },
                description = "Only when isShopping is true: one entry per distinct item to buy, each " +
                               "stripped down to just the item itself — e.g. [\"bread\"] from \"add bread " +
                               "to my shopping list\", or [\"bread\", \"milk\", \"eggs\"] from \"add bread, " +
                               "milk, and eggs to my shopping list\". [] when isShopping is false.",
            }),
        };

        var required = new List<string> { "projectId", "tagIds", "isNote", "isShopping", "shoppingItems" };

        if (requireTags)
        {
            properties["joplinTagIds"] = JsonSerializer.SerializeToElement(new
            {
                type = "array",
                items = new { type = "string" },
                description = "Required when isNote is true — at least one id from the Joplin tags list " +
                               "below (closest fit if nothing is perfect). Use [] when isNote is false.",
            });
            required.Add("joplinTagIds");
        }

        return new Tool
        {
            Name = ClassifyToolName,
            Description = "Pick the Super Productivity project and tags for this voice-note transcription.",
            InputSchema = new InputSchema { Properties = properties, Required = required },
        };
    }

    private static string BuildPrompt(
        string transcription, IReadOnlyList<SpNamedItem> projects, IReadOnlyList<SpNamedItem> tags,
        IReadOnlyList<SpNamedItem> joplinTags, bool requireTags)
    {
        var sb = new StringBuilder();
        sb.Append("You are filing a voice-note transcription into Super Productivity. ");
        sb.Append("Pick the single best-fitting project from the list below, based on its title. ");
        sb.Append("Only use ids that appear in the lists given. ");
        sb.Append("If no project fits well, return null for projectId — do not force a match. ");

        if (requireTags)
            sb.Append("You must also pick at least one tag for tagIds — the closest fit if nothing is a " +
                      "perfect match. Never return an empty array for tagIds.\n");
        else
            sb.Append("Pick zero or more fitting tags for tagIds, or [] if none fit — do not force a match.\n");

        sb.Append("Also decide whether this is a note to save (a fact, idea, or reference) rather than an ");
        sb.Append("actionable to-do, and set isNote accordingly. Separately, set isShopping if this is a ");
        sb.Append("shopping or errands item — something to buy or pick up. When isShopping is true, also set ");
        sb.Append("shoppingItems to one entry per distinct item, each stripped of phrasing like \"add to my ");
        sb.Append("shopping list\" or \"I need to pick up\" — e.g. [\"bread\"], or [\"bread\", \"milk\", \"eggs\"] ");
        sb.Append("when more than one item is mentioned, so each becomes its own task.\n");

        if (requireTags)
            sb.Append("When isNote is true, you must also pick at least one tag for joplinTagIds from the " +
                      "Joplin tags list below — the closest fit if nothing is perfect. Use [] when isNote is false.\n");

        sb.Append('\n');
        sb.Append("Projects:\n");
        if (projects.Count == 0) sb.Append("(none)\n");
        foreach (var p in projects) sb.Append("- ").Append(p.Id).Append(": ").Append(p.Title).Append('\n');

        sb.Append("\nTags:\n");
        if (tags.Count == 0) sb.Append("(none)\n");
        foreach (var t in tags) sb.Append("- ").Append(t.Id).Append(": ").Append(t.Title).Append('\n');

        if (requireTags)
        {
            sb.Append("\nJoplin tags:\n");
            if (joplinTags.Count == 0) sb.Append("(none)\n");
            foreach (var t in joplinTags) sb.Append("- ").Append(t.Id).Append(": ").Append(t.Title).Append('\n');
        }

        sb.Append("\nTranscription:\n").Append(transcription);
        return sb.ToString();
    }
}
