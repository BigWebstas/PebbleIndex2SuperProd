using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;

namespace Index2SP;

/// <summary>
/// Asks an AI backend (Claude, Gemini, OpenAI, or a local Ollama model — <see
/// cref="AppConfig.AiClassifierConfig.Provider"/>) to pick the Super Productivity project and
/// tags for a transcription, from the project/tag lists the caller already pulled from the SP
/// API. Never throws — any failure (disabled, no key, network, timeout, malformed response, an
/// id that isn't in the lists passed in) returns null, and the caller keeps whatever
/// PayloadConverter already set from the static config.
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
        string? ProjectId, List<string> TagIds, bool IsNote, bool IsShopping, List<string> ShoppingItems, List<string> JoplinTagIds,
        bool IsCalendarEvent, string? EventTitle, DateTimeOffset? EventStart, DateTimeOffset? EventEnd, bool EventAllDay,
        bool IsMessage, string? MessageRecipient, string? MessageText, string? MessagePlatform);

    /// <param name="joplinTags">Existing Joplin tags, only consulted (and only asked of the AI)
    /// while <see cref="AppConfig.AiClassifierConfig.RequireTags"/> is on. Pass an empty list
    /// when Joplin isn't enabled.</param>
    /// <param name="referenceTime">When the transcription was recorded, in local time — used so
    /// the AI can resolve relative dates like "the 18th" or "next Tuesday" for both the Super
    /// Productivity due date and (when enabled) the Google Calendar event.</param>
    /// <param name="includeMessage">Whether to ask the AI to detect a "send a message to X" intent
    /// at all — pass the caller's Beeper enabled flag.</param>
    public async Task<Classification?> ClassifyAsync(
        string transcription,
        IReadOnlyList<SpNamedItem> projects,
        IReadOnlyList<SpNamedItem> tags,
        IReadOnlyList<SpNamedItem> joplinTags,
        DateTimeOffset referenceTime,
        bool includeMessage,
        CancellationToken ct)
    {
        if (!_config.Enabled) return null;
        if (projects.Count == 0 && tags.Count == 0) return null;

        var requireTags = _config.RequireTags;
        var fields = BuildFieldSpecs(requireTags, includeMessage);
        var prompt = BuildPrompt(transcription, projects, tags, joplinTags, requireTags, referenceTime, includeMessage);

        var primary = _config.Provider;
        var args = await TryClassifyWithProviderAsync(primary, prompt, fields, ct);

        if (args is null)
        {
            var fallback = _config.FallbackProvider;
            if (!string.IsNullOrWhiteSpace(fallback) && fallback != primary)
            {
                _log.Warn($"AI classify ({primary}) failed — trying fallback provider {fallback}");
                args = await TryClassifyWithProviderAsync(fallback, prompt, fields, ct);
            }
        }

        if (args is null)
        {
            _log.Warn("AI classify: no provider produced a usable result, using static config instead");
            return null;
        }

        return BuildClassification(args, projects, tags, joplinTags, requireTags, includeMessage);
    }

    /// <summary>One provider attempt: null on any failure (no credential, network, timeout,
    /// malformed response, or a response with no usable tool call) — never throws, so the caller
    /// can try a fallback provider or give up.</summary>
    private async Task<IReadOnlyDictionary<string, JsonElement>?> TryClassifyWithProviderAsync(
        string provider, string prompt, List<FieldSpec> fields, CancellationToken ct)
    {
        if (!HasCredential(provider)) return null;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_config.TimeoutSeconds));

        try
        {
            var args = provider switch
            {
                "gemini" => await ClassifyWithGeminiAsync(prompt, fields, cts.Token),
                "openai" => await ClassifyWithOpenAiAsync(prompt, fields, cts.Token),
                "ollama" => await ClassifyWithOllamaAsync(prompt, fields, cts.Token),
                _ => await ClassifyWithClaudeAsync(prompt, fields, cts.Token),
            };

            if (args is null)
                _log.Warn($"AI classify ({provider}): response had no usable tool call");

            return args;
        }
        catch (Exception ex) when (ex is AnthropicException or HttpRequestException or OperationCanceledException or JsonException)
        {
            _log.Warn($"AI classify ({provider}) failed: {ex.Message}");
            return null;
        }
    }

    private bool HasCredential(string provider) => provider switch
    {
        "gemini" => !string.IsNullOrWhiteSpace(_config.GeminiApiKey),
        "openai" => !string.IsNullOrWhiteSpace(_config.OpenAiApiKey),
        "ollama" => !string.IsNullOrWhiteSpace(_config.OllamaModel),
        _ => !string.IsNullOrWhiteSpace(_config.ApiKey),
    };

    /// <summary>Probes whichever provider is currently selected — used by the tray's health
    /// check and "Test all connections". Throws with a human-readable message on any failure
    /// (not configured, network, auth, model not found/pulled); never returns a failure as a
    /// plain value, matching the other integration clients' TestAsync methods.</summary>
    public async Task<string> TestAsync(CancellationToken ct = default)
    {
        if (!_config.Enabled)
            throw new InvalidOperationException("AI classifier is disabled.");

        var provider = _config.Provider;
        if (!HasCredential(provider))
            throw new InvalidOperationException($"No credential set for {provider}.");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_config.TimeoutSeconds));

        return provider switch
        {
            "gemini" => await TestGeminiAsync(cts.Token),
            "openai" => await TestOpenAiAsync(cts.Token),
            "ollama" => await TestOllamaAsync(cts.Token),
            _ => await TestClaudeAsync(cts.Token),
        };
    }

    private async Task<string> TestClaudeAsync(CancellationToken ct)
    {
        using var client = new AnthropicClient { ApiKey = _config.ApiKey };
        var model = await client.Models.Retrieve(_config.Model, cancellationToken: ct);
        return $"OK — Claude reachable, model \"{model.ID}\" available";
    }

    private async Task<string> TestGeminiAsync(CancellationToken ct)
    {
        var model = string.IsNullOrWhiteSpace(_config.GeminiModel) ? "gemini-2.5-flash" : _config.GeminiModel.Trim();
        using var http = new HttpClient();
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}");
        req.Headers.Add("x-goog-api-key", _config.GeminiApiKey);
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Gemini returned HTTP {(int)resp.StatusCode} for model \"{model}\".");

        return $"OK — Gemini reachable, model \"{model}\" available";
    }

    private async Task<string> TestOpenAiAsync(CancellationToken ct)
    {
        var model = string.IsNullOrWhiteSpace(_config.OpenAiModel) ? "gpt-4o-mini" : _config.OpenAiModel.Trim();
        using var http = new HttpClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.openai.com/v1/models/{Uri.EscapeDataString(model)}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.OpenAiApiKey);
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"OpenAI returned HTTP {(int)resp.StatusCode} for model \"{model}\".");

        return $"OK — OpenAI reachable, model \"{model}\" available";
    }

    private async Task<string> TestOllamaAsync(CancellationToken ct)
    {
        using var ollama = new OllamaClient(_config.OllamaBaseUrl, _config.TimeoutSeconds);
        var models = await ollama.GetModelsAsync(ct);
        if (!models.Any(m => m.Id == _config.OllamaModel))
            throw new Exception($"Reached Ollama but model \"{_config.OllamaModel}\" isn't in the pulled list " +
                                 $"({models.Count} model(s) pulled).");

        return $"OK — Ollama reachable, model \"{_config.OllamaModel}\" is pulled";
    }

    /// <summary>Real Claude models available to this API key, newest first — used to build the
    /// tray's model picker instead of the hardcoded fallback list.</summary>
    public async Task<IReadOnlyList<SpNamedItem>> ListClaudeModelsAsync(CancellationToken ct = default)
    {
        using var client = new AnthropicClient { ApiKey = _config.ApiKey };
        var page = await client.Models.List(new Anthropic.Models.Models.ModelListParams { Limit = 100 }, cancellationToken: ct);
        return page.Items
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => new SpNamedItem(m.ID, m.DisplayName))
            .ToList();
    }

    /// <summary>Real Gemini models available to this API key that support generateContent (the
    /// call classification uses) — used to build the tray's model picker instead of the
    /// hardcoded fallback list. Gemini's list endpoint doesn't expose a creation date, so this
    /// keeps the API's own ordering.</summary>
    public async Task<IReadOnlyList<SpNamedItem>> ListGeminiModelsAsync(CancellationToken ct = default)
    {
        var list = new List<SpNamedItem>();
        string? pageToken = null;
        var pages = 0;
        do
        {
            var url = "https://generativelanguage.googleapis.com/v1beta/models?pageSize=100" +
                      (pageToken is null ? "" : $"&pageToken={Uri.EscapeDataString(pageToken)}");
            using var http = new HttpClient();
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("x-goog-api-key", _config.GeminiApiKey);
            using var resp = await http.SendAsync(req, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new Exception($"Gemini returned HTTP {(int)resp.StatusCode} listing models: {Truncate(text)}");

            var root = JsonSerializer.Deserialize<JsonElement>(text);
            if (root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in models.EnumerateArray())
                {
                    var supportsGenerate = m.TryGetProperty("supportedGenerationMethods", out var methods) &&
                        methods.ValueKind == JsonValueKind.Array &&
                        methods.EnumerateArray().Any(x => x.GetString() == "generateContent");
                    if (!supportsGenerate) continue;

                    var name = m.TryGetProperty("name", out var nEl) ? nEl.GetString() : null;
                    if (name is not { Length: > 0 }) continue;
                    var id = name.StartsWith("models/") ? name["models/".Length..] : name;

                    var displayName = m.TryGetProperty("displayName", out var dEl) ? dEl.GetString() : null;
                    list.Add(new SpNamedItem(id, string.IsNullOrWhiteSpace(displayName) ? id : displayName));
                }
            }

            pageToken = root.TryGetProperty("nextPageToken", out var npt) ? npt.GetString() : null;
            pages++;
        } while (!string.IsNullOrWhiteSpace(pageToken) && pages < 5);

        return list;
    }

    // ---- Claude (Anthropic Messages API, official SDK) ----------------

    private async Task<IReadOnlyDictionary<string, JsonElement>?> ClassifyWithClaudeAsync(
        string prompt, List<FieldSpec> fields, CancellationToken ct)
    {
        using var client = new AnthropicClient { ApiKey = _config.ApiKey };
        var response = await client.Messages.Create(new MessageCreateParams
        {
            Model = _config.Model,
            MaxTokens = 320,
            Messages = [new() { Role = Role.User, Content = prompt }],
            Tools = [ToAnthropicTool(fields)],
            ToolChoice = new ToolChoiceTool { Name = ClassifyToolName },
        }, cancellationToken: ct);

        foreach (var block in response.Content)
            if (block.TryPickToolUse(out ToolUseBlock? toolUse) && toolUse.Name == ClassifyToolName)
                return toolUse.Input;

        return null;
    }

    private static Tool ToAnthropicTool(List<FieldSpec> fields)
    {
        var properties = new Dictionary<string, JsonElement>();
        foreach (var f in fields)
            properties[f.Name] = BuildJsonSchema(f);

        return new Tool
        {
            Name = ClassifyToolName,
            Description = "Pick the Super Productivity project and tags for this voice-note transcription.",
            InputSchema = new InputSchema { Properties = properties, Required = fields.Select(f => f.Name).ToList() },
        };
    }

    // ---- Gemini (raw REST — no official first-party .NET SDK) ---------

    private async Task<IReadOnlyDictionary<string, JsonElement>?> ClassifyWithGeminiAsync(
        string prompt, List<FieldSpec> fields, CancellationToken ct)
    {
        var model = string.IsNullOrWhiteSpace(_config.GeminiModel) ? "gemini-2.5-flash" : _config.GeminiModel.Trim();
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}:generateContent";

        var properties = new Dictionary<string, object>();
        foreach (var f in fields)
            properties[f.Name] = BuildGeminiSchema(f);

        var body = new
        {
            contents = new[] { new { role = "user", parts = new[] { new { text = prompt } } } },
            tools = new[]
            {
                new
                {
                    functionDeclarations = new[]
                    {
                        new
                        {
                            name = ClassifyToolName,
                            description = "Pick the Super Productivity project and tags for this voice-note transcription.",
                            parameters = new { type = "OBJECT", properties, required = fields.Select(f => f.Name).ToArray() },
                        },
                    },
                },
            },
            toolConfig = new { functionCallingConfig = new { mode = "ANY", allowedFunctionNames = new[] { ClassifyToolName } } },
        };

        using var http = new HttpClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        req.Headers.Add("x-goog-api-key", _config.GeminiApiKey);
        using var resp = await http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _log.Warn($"Gemini classify: HTTP {(int)resp.StatusCode}: {Truncate(text)}");
            return null;
        }

        var root = JsonSerializer.Deserialize<JsonElement>(text);
        if (!root.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var candidate in candidates.EnumerateArray())
        {
            if (!candidate.TryGetProperty("content", out var content) || !content.TryGetProperty("parts", out var parts))
                continue;
            foreach (var part in parts.EnumerateArray())
                if (part.TryGetProperty("functionCall", out var fc) && fc.TryGetProperty("args", out var args))
                    return ToDict(args);
        }

        return null;
    }

    // ---- OpenAI (raw REST — Chat Completions API) ----------------------

    private async Task<IReadOnlyDictionary<string, JsonElement>?> ClassifyWithOpenAiAsync(
        string prompt, List<FieldSpec> fields, CancellationToken ct)
    {
        var model = string.IsNullOrWhiteSpace(_config.OpenAiModel) ? "gpt-4o-mini" : _config.OpenAiModel.Trim();
        var body = new
        {
            model,
            messages = new[] { new { role = "user", content = prompt } },
            tools = new[] { BuildFunctionToolWrapper(fields, strict: true) },
            tool_choice = new { type = "function", function = new { name = ClassifyToolName } },
        };

        using var http = new HttpClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.OpenAiApiKey);
        using var resp = await http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _log.Warn($"OpenAI classify: HTTP {(int)resp.StatusCode}: {Truncate(text)}");
            return null;
        }

        var root = JsonSerializer.Deserialize<JsonElement>(text);
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var choice in choices.EnumerateArray())
        {
            if (!choice.TryGetProperty("message", out var message) || !message.TryGetProperty("tool_calls", out var toolCalls) ||
                toolCalls.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var call in toolCalls.EnumerateArray())
            {
                if (!call.TryGetProperty("function", out var fn)) continue;
                if (fn.TryGetProperty("name", out var nameEl) && nameEl.GetString() != ClassifyToolName) continue;
                // Chat Completions returns arguments as a JSON-encoded string, not a nested object.
                if (fn.TryGetProperty("arguments", out var argsEl) && argsEl.ValueKind == JsonValueKind.String &&
                    argsEl.GetString() is { Length: > 0 } argsJson)
                {
                    return ToDict(JsonSerializer.Deserialize<JsonElement>(argsJson));
                }
            }
        }

        return null;
    }

    // ---- Ollama (local, OpenAI-compatible /api/chat) -------------------

    private async Task<IReadOnlyDictionary<string, JsonElement>?> ClassifyWithOllamaAsync(
        string prompt, List<FieldSpec> fields, CancellationToken ct)
    {
        var baseUrl = string.IsNullOrWhiteSpace(_config.OllamaBaseUrl) ? "http://127.0.0.1:11434" : _config.OllamaBaseUrl.TrimEnd('/');
        var body = new
        {
            model = _config.OllamaModel,
            messages = new[] { new { role = "user", content = prompt } },
            stream = false,
            tools = new[] { BuildFunctionToolWrapper(fields, strict: false) },
        };

        using var http = new HttpClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/chat") { Content = JsonContent.Create(body) };
        using var resp = await http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _log.Warn($"Ollama classify: HTTP {(int)resp.StatusCode}: {Truncate(text)}");
            return null;
        }

        var root = JsonSerializer.Deserialize<JsonElement>(text);
        // Ollama can't force a tool call the way the hosted providers can — a model that ignores
        // the request (or doesn't support tools at all) simply has no tool_calls here, and this
        // falls through to null like any other classify failure.
        if (!root.TryGetProperty("message", out var message) || !message.TryGetProperty("tool_calls", out var toolCalls) ||
            toolCalls.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var call in toolCalls.EnumerateArray())
        {
            if (!call.TryGetProperty("function", out var fn)) continue;
            if (fn.TryGetProperty("name", out var nameEl) && nameEl.GetString() != ClassifyToolName) continue;
            if (fn.TryGetProperty("arguments", out var args))
                return ToDict(args);
        }

        return null;
    }

    // ---- shared: field schema, response parsing, classification -------

    private enum FieldKind { StringOrNull, StringArray, Boolean }

    private sealed record FieldSpec(string Name, FieldKind Kind, string Description);

    private const string ClassifyToolName = "classify";

    private static List<FieldSpec> BuildFieldSpecs(bool requireTags, bool includeMessage)
    {
        var fields = new List<FieldSpec>
        {
            new("projectId", FieldKind.StringOrNull, "id from the project list, or null if none fit well"),
            new("tagIds", FieldKind.StringArray, requireTags
                ? "Required — at least one id from the tag list below. Pick the closest fit if nothing is " +
                  "a perfect match. Never return an empty array."
                : "ids from the tag list, or [] if none fit"),
            new("isNote", FieldKind.Boolean, "true if this is a note, idea, or reference to save rather " +
                "than an actionable to-do (e.g. a fact, a quote, something to remember)"),
            new("isShopping", FieldKind.Boolean, "true if this is a shopping / errands item — something to " +
                "buy or pick up (e.g. groceries, a store run, an online order)"),
            new("shoppingItems", FieldKind.StringArray, "Only when isShopping is true: one entry per " +
                "distinct item to buy, each stripped down to just the item itself — e.g. [\"bread\"] from " +
                "\"add bread to my shopping list\", or [\"bread\", \"milk\", \"eggs\"] from \"add bread, milk, " +
                "and eggs to my shopping list\". [] when isShopping is false."),
        };

        if (requireTags)
            fields.Add(new("joplinTagIds", FieldKind.StringArray, "Required when isNote is true — at least " +
                "one id from the Joplin tags list below (closest fit if nothing is perfect). Use [] when " +
                "isNote is false."));

        fields.Add(new("isCalendarEvent", FieldKind.Boolean, "true if this describes something happening at " +
            "a specific date/time — a calendar event (e.g. \"dinner with parents on the 18th at 5pm\", " +
            "\"doctor appointment next Tuesday at 10am\") rather than an open-ended to-do."));
        fields.Add(new("eventTitle", FieldKind.StringOrNull, "Required when isCalendarEvent is true: a " +
            "short event title, e.g. \"Dinner with parents\". Null otherwise."));
        fields.Add(new("eventStart", FieldKind.StringOrNull, "Required when isCalendarEvent is true: the " +
            "event's start date/time as ISO 8601 with a UTC offset, e.g. \"2026-09-18T17:00:00-06:00\" — " +
            "resolve relative dates (\"the 18th\", \"next Tuesday\") against the reference time given below. " +
            "Null otherwise."));
        fields.Add(new("eventEnd", FieldKind.StringOrNull, "Only if an explicit end time or duration was " +
            "mentioned, same ISO 8601 format as eventStart. Null otherwise — a 1-hour default is used."));
        fields.Add(new("eventAllDay", FieldKind.Boolean, "true only when isCalendarEvent is true and no " +
            "specific time was given — just a date (e.g. \"mom's birthday is the 20th\"). Otherwise false."));

        if (includeMessage)
        {
            fields.Add(new("isMessage", FieldKind.Boolean, "true if this is a request to send a chat " +
                "message to a specific person — e.g. \"send a message to Abbie say hello\", \"tell Abbie " +
                "I'll be late\", \"text Abbie hello\". False for anything else, including messages the user " +
                "is just describing or recalling rather than asking to send right now."));
            fields.Add(new("messageRecipient", FieldKind.StringOrNull, "Required when isMessage is true: " +
                "just the recipient's name, e.g. \"Abbie\" from \"send a message to Abbie say hello\". Null " +
                "otherwise."));
            fields.Add(new("messageText", FieldKind.StringOrNull, "Required when isMessage is true: just " +
                "the message content to send, stripped of phrasing like \"send a message to X say\" or " +
                "\"tell X\" — e.g. \"hello\" from \"send a message to Abbie say hello\". Null otherwise."));
            fields.Add(new("messagePlatform", FieldKind.StringOrNull, "Only when isMessage is true and a " +
                "specific messaging app was named, e.g. \"telegram\", \"whatsapp\", \"signal\", \"imessage\", " +
                "\"google messages\", \"instagram\" — from phrasing like \"message Abbie on Telegram\" or " +
                "\"text Abbie on WhatsApp\". Null when no platform was mentioned."));
        }

        return fields;
    }

    /// <summary>Standard (lowercase) JSON Schema for one field — used by Claude, OpenAI, and
    /// Ollama, all of which accept the plain JSON Schema <c>["string","null"]</c> union-type
    /// idiom for optional fields.</summary>
    private static JsonElement BuildJsonSchema(FieldSpec f) => f.Kind switch
    {
        FieldKind.StringOrNull => JsonSerializer.SerializeToElement(new
        {
            type = new[] { "string", "null" },
            description = f.Description,
        }),
        FieldKind.Boolean => JsonSerializer.SerializeToElement(new { type = "boolean", description = f.Description }),
        FieldKind.StringArray => JsonSerializer.SerializeToElement(new
        {
            type = "array",
            items = new { type = "string" },
            description = f.Description,
        }),
        _ => throw new InvalidOperationException($"Unhandled field kind {f.Kind}"),
    };

    /// <summary>Gemini's Schema object uses UPPERCASE type names and a separate boolean
    /// <c>nullable</c> flag instead of the <c>["string","null"]</c> union-type idiom the other
    /// three providers accept — a real, documented structural difference, not a style choice.</summary>
    private static object BuildGeminiSchema(FieldSpec f) => f.Kind switch
    {
        FieldKind.StringOrNull => new { type = "STRING", nullable = true, description = f.Description },
        FieldKind.Boolean => new { type = "BOOLEAN", description = f.Description },
        FieldKind.StringArray => new { type = "ARRAY", items = new { type = "STRING" }, description = f.Description },
        _ => throw new InvalidOperationException($"Unhandled field kind {f.Kind}"),
    };

    /// <summary>OpenAI Chat Completions / Ollama <c>/api/chat</c> tool wrapper — both use the
    /// same <c>{type:"function", function:{...}}</c> shape and standard JSON Schema types.
    /// <paramref name="strict"/> adds OpenAI's Structured Outputs constraints (<c>strict</c> +
    /// <c>additionalProperties: false</c>); Ollama ignores unknown fields, so it's left off there
    /// rather than assumed supported.</summary>
    private static object BuildFunctionToolWrapper(List<FieldSpec> fields, bool strict)
    {
        var properties = new Dictionary<string, JsonElement>();
        foreach (var f in fields)
            properties[f.Name] = BuildJsonSchema(f);

        object parameters = strict
            ? new
            {
                type = "object",
                properties,
                required = fields.Select(f => f.Name).ToArray(),
                additionalProperties = false,
            }
            : new
            {
                type = "object",
                properties,
                required = fields.Select(f => f.Name).ToArray(),
            };

        // Target-typed so the compiler can box each anonymous type to `object` independently —
        // used as an object-initializer value below, the ternary would otherwise need the two
        // (structurally different) branches to share a type on their own.
        object function = strict
            ? new
            {
                name = ClassifyToolName,
                description = "Pick the Super Productivity project and tags for this voice-note transcription.",
                parameters,
                strict = true,
            }
            : new
            {
                name = ClassifyToolName,
                description = "Pick the Super Productivity project and tags for this voice-note transcription.",
                parameters,
            };

        return new { type = "function", function };
    }

    private static IReadOnlyDictionary<string, JsonElement> ToDict(JsonElement obj)
    {
        var dict = new Dictionary<string, JsonElement>();
        if (obj.ValueKind == JsonValueKind.Object)
            foreach (var prop in obj.EnumerateObject())
                dict[prop.Name] = prop.Value;
        return dict;
    }

    private static string Truncate(string s, int n = 300) => s.Length <= n ? s : s[..n] + "…";

    private Classification BuildClassification(
        IReadOnlyDictionary<string, JsonElement> input, IReadOnlyList<SpNamedItem> projects, IReadOnlyList<SpNamedItem> tags,
        IReadOnlyList<SpNamedItem> joplinTags, bool requireTags, bool includeMessage)
    {
        var projectId = input.TryGetValue("projectId", out var pEl) && pEl.ValueKind == JsonValueKind.String ? pEl.GetString() : null;
        var tagIds = ReadStringArray(input, "tagIds");
        var isNote = input.TryGetValue("isNote", out var nEl) && nEl.ValueKind == JsonValueKind.True;
        var isShopping = input.TryGetValue("isShopping", out var sEl) && sEl.ValueKind == JsonValueKind.True;
        var shoppingItems = ReadStringArray(input, "shoppingItems");
        var joplinTagIds = requireTags ? ReadStringArray(input, "joplinTagIds") : new List<string>();

        var isCalendarEvent = input.TryGetValue("isCalendarEvent", out var ceEl) && ceEl.ValueKind == JsonValueKind.True;
        var eventTitle = input.TryGetValue("eventTitle", out var etEl) && etEl.ValueKind == JsonValueKind.String
            ? etEl.GetString()
            : null;
        var eventStart = ReadDateTimeOffset(input, "eventStart");
        var eventEnd = ReadDateTimeOffset(input, "eventEnd");
        var eventAllDay = input.TryGetValue("eventAllDay", out var adEl) && adEl.ValueKind == JsonValueKind.True;

        // A calendar event needs at least a title and a start time to be usable.
        if (isCalendarEvent && (eventTitle is null || eventStart is null))
            isCalendarEvent = false;

        var isMessage = false;
        string? messageRecipient = null;
        string? messageText = null;
        string? messagePlatform = null;
        if (includeMessage)
        {
            isMessage = input.TryGetValue("isMessage", out var imEl) && imEl.ValueKind == JsonValueKind.True;
            messageRecipient = input.TryGetValue("messageRecipient", out var mrEl) && mrEl.ValueKind == JsonValueKind.String
                ? mrEl.GetString()
                : null;
            messageText = input.TryGetValue("messageText", out var mtEl) && mtEl.ValueKind == JsonValueKind.String
                ? mtEl.GetString()
                : null;
            messagePlatform = input.TryGetValue("messagePlatform", out var mpEl) && mpEl.ValueKind == JsonValueKind.String
                ? mpEl.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(messagePlatform)) messagePlatform = null;
            // A message needs both a recipient and something to say to be usable.
            if (isMessage && (string.IsNullOrWhiteSpace(messageRecipient) || string.IsNullOrWhiteSpace(messageText)))
                isMessage = false;
        }

        var resolvedProjectId = projectId is not null && projects.Any(p => p.Id == projectId) ? projectId : null;
        var resolvedTagIds = tagIds.Where(t => tags.Any(x => x.Id == t)).ToList();
        var resolvedJoplinTagIds = joplinTagIds.Where(t => joplinTags.Any(x => x.Id == t)).ToList();

        _log.Info($"AI classify: project={resolvedProjectId ?? "(none)"}, tags=[{string.Join(',', resolvedTagIds)}], " +
                  $"isNote={isNote}, isShopping={isShopping}, shoppingItems=[{string.Join(',', shoppingItems)}], " +
                  $"joplinTags=[{string.Join(',', resolvedJoplinTagIds)}], isCalendarEvent={isCalendarEvent}" +
                  (isCalendarEvent ? $", eventStart={eventStart:O}" : "") +
                  $", isMessage={isMessage}" + (isMessage ? $", messageRecipient={messageRecipient}, messagePlatform={messagePlatform ?? "(any)"}" : ""));

        return new Classification(resolvedProjectId, resolvedTagIds, isNote, isShopping, shoppingItems, resolvedJoplinTagIds,
            isCalendarEvent, eventTitle, eventStart, eventEnd, eventAllDay, isMessage, messageRecipient, messageText, messagePlatform);
    }

    private static DateTimeOffset? ReadDateTimeOffset(IReadOnlyDictionary<string, JsonElement> input, string key)
    {
        if (input.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(el.GetString(), null, System.Globalization.DateTimeStyles.None, out var dt))
        {
            return dt;
        }
        return null;
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

    private static string BuildPrompt(
        string transcription, IReadOnlyList<SpNamedItem> projects, IReadOnlyList<SpNamedItem> tags,
        IReadOnlyList<SpNamedItem> joplinTags, bool requireTags, DateTimeOffset referenceTime, bool includeMessage)
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

        sb.Append("Also decide whether this describes a calendar event — something happening at a specific ");
        sb.Append("date/time — and set isCalendarEvent, eventTitle, eventStart, eventEnd, and eventAllDay ");
        sb.Append("accordingly (see their descriptions); this also sets the due date on the Super Productivity ");
        sb.Append("task itself. Right now it is ");
        sb.Append(referenceTime.ToString("yyyy-MM-dd HH:mm zzz")).Append(" (").Append(referenceTime.ToString("dddd"));
        sb.Append(") — resolve relative dates like \"the 18th\", \"tomorrow\", or \"next Tuesday\" against this.\n");

        if (includeMessage)
            sb.Append("Also decide whether this is a request to send a chat message to someone right now " +
                      "(e.g. \"send a message to Abbie say hello\", \"tell Abbie I'll be late\") and set " +
                      "isMessage, messageRecipient, messageText, and messagePlatform accordingly (see their " +
                      "descriptions) — messagePlatform only when a specific app like Telegram or WhatsApp was named.\n");

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
