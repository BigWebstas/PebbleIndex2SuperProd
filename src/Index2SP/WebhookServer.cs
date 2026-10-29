using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Index2SP;

/// <summary>
/// Hosts a Kestrel listener that accepts the Pebble Index 01 multipart webhook and turns each
/// call into a Super Productivity task. Designed to sit behind a user-run HTTPS tunnel.
/// </summary>
public sealed class WebhookServer : IAsyncDisposable
{
    private readonly AppConfig _config;
    private readonly Logger _log;
    private readonly CaptureTagResolver _captureTag;
    private readonly Outbox _outbox;
    private readonly AiTaskClassifier _classifier;
    private readonly DuplicateMessageTracker _seenMessages;
    private readonly Inbox? _inbox;
    private readonly string _webhookPath;
    private readonly string _healthPath;
    private WebApplication? _app;
    private SuperProductivityClient? _spClient;

    public WebhookServer(AppConfig config, Logger log, CaptureTagResolver captureTag, Outbox outbox, AiTaskClassifier classifier,
        string? stateDir = null)
    {
        // With a state directory, handled message ids and in-flight webhooks survive a restart;
        // without one (tests), both stay in memory only.
        _seenMessages = new DuplicateMessageTracker(
            persistPath: stateDir is null ? null : Path.Combine(stateDir, "seen-messages.json"));
        _inbox = stateDir is null ? null : new Inbox(stateDir, log);

        _config = config;
        _log = log;
        _captureTag = captureTag;
        _outbox = outbox;
        _classifier = classifier;

        _webhookPath = _config.WebhookPath.TrimEnd('/');
        if (string.IsNullOrEmpty(_webhookPath)) _webhookPath = "/pebble";
        _healthPath = _webhookPath + "/health";
    }

    public bool IsRunning => _app is not null;

    /// <summary>Raised on the thread pool after a task is created.</summary>
    public event Action<string, string?>? TaskCreated;   // (title, taskId)
    /// <summary>Raised on the thread pool when a webhook call fails and nothing was queued.</summary>
    public event Action<string>? WebhookFailed;          // (message)
    /// <summary>Raised on the thread pool when SP was unreachable and the task went to the outbox.</summary>
    public event Action<string>? TaskQueued;             // (title)
    /// <summary>Raised on the thread pool for a Pebble connectivity-test webhook (no task created).</summary>
    public event Action<string>? TestEventReceived;      // (remote)

    /// <summary>Supplies the "service: state" lines for a spoken status request (set by the tray,
    /// which owns the health checks). Null disables status requests.</summary>
    public Func<string>? StatusReport { get; set; }

    /// <summary>A short utterance containing "status" — "status", "what's the status", "service
    /// status". Capped at six words so a task like "check the order status with the bank today"
    /// still becomes a task.</summary>
    internal static bool IsStatusRequest(string? transcription)
    {
        if (string.IsNullOrWhiteSpace(transcription)) return false;
        var words = transcription.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return words.Length <= 6 && words.Any(w => w.Trim('.', ',', '?', '!', '\'', '"').EndsWith("status", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A port still held by a previous instance (a quick restart) usually frees up within
    /// seconds, so binding is retried a few times before giving up.</summary>
    private const int BindAttempts = 5;

    public async Task StartAsync()
    {
        if (_app is not null) return;

        // Claimed before the listener opens, so a sender's retry of a webhook we're about to
        // replay is recognised as a duplicate rather than processed twice.
        var replay = ClaimPendingInbox();

        for (var attempt = 1; ; attempt++)
        {
            var app = BuildApp();
            try
            {
                await app.StartAsync();
                _app = app;
                break;
            }
            catch (Exception ex)
            {
                await app.DisposeAsync();
                if (ex is not IOException || attempt >= BindAttempts) throw;
                _log.Warn($"Could not bind {_config.ListenAddress}:{_config.Port} ({ex.Message}) — retrying ({attempt}/{BindAttempts - 1})");
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }

        LogStarted();
        if (replay.Count > 0) _ = Task.Run(() => ReplayInboxAsync(replay));
    }

    private WebApplication BuildApp()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production,
            ContentRootPath = AppContext.BaseDirectory,
            ApplicationName = "Index2SP",
        });

        builder.Logging.ClearProviders();

        // We host this inside the Avalonia app and manage shutdown ourselves. The default
        // ConsoleLifetime would install its own SIGTERM/SIGINT handlers that cancel the signal
        // without ever exiting (we never call app.WaitForShutdown), leaving the process unkillable.
        builder.Services.AddSingleton<IHostLifetime, NoopHostLifetime>();

        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Limits.MaxRequestBodySize = 30 * 1024 * 1024; // Index audio clips are small; 30 MB is plenty
            k.AddServerHeader = false;
        });
        builder.WebHost.UseUrls($"http://{_config.ListenAddress}:{_config.Port}");

        var app = builder.Build();

        app.MapGet("/health", () => Results.Json(new { ok = true, service = "index2sp", version = AppInfo.Version }));
        app.MapGet(_webhookPath, () => Results.Json(new { ok = true, service = "index2sp", version = AppInfo.Version }));
        if (!string.Equals(_webhookPath, "/health", StringComparison.OrdinalIgnoreCase))
        {
            app.MapGet(_healthPath, () => Results.Json(new { ok = true, service = "index2sp", version = AppInfo.Version }));
        }

        // Cast to Delegate so the returned IResult is written to the response
        // (a bare method group binds as RequestDelegate and discards it).
        app.MapPost(_webhookPath, (Delegate)HandleWebhookAsync);

        // Any other path -> 404 with a hint (helps while wiring up the tunnel).
        app.MapFallback(() => Results.Json(
            new { ok = false, error = new { message = $"POST your Pebble webhook to {_webhookPath}" } },
            statusCode: StatusCodes.Status404NotFound));

        return app;
    }

    private void LogStarted()
    {
        _log.Info($"Webhook listener started on http://{_config.ListenAddress}:{_config.Port}{_webhookPath}");
        if (string.IsNullOrEmpty(_config.InboundAuthToken) && string.IsNullOrEmpty(_config.HmacSecret))
            _log.Warn("No inboundAuthToken or hmacSecret set — anyone who can reach the listener can create tasks.");
        else
        {
            if (!string.IsNullOrEmpty(_config.HmacSecret))
                _log.Info("HMAC-SHA256 signature verification enabled for inbound webhooks.");
            if (!string.IsNullOrEmpty(_config.InboundAuthToken))
                _log.Info("Inbound bearer token authorization enabled for inbound webhooks.");
        }
    }

    public async Task StopAsync()
    {
        _spClient?.Dispose();
        _spClient = null;

        if (_app is null) return;
        try
        {
            await _app.StopAsync(TimeSpan.FromSeconds(3));
            await _app.DisposeAsync();
            _log.Info("Webhook listener stopped");
        }
        finally
        {
            _app = null;
        }
    }

    private async Task<IResult> HandleWebhookAsync(HttpContext ctx)
    {
        var remote = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";

        // Lazy buffering: only buffer and copy request body into byte array if HMAC verification is actually needed.
        // For unauthenticated or bearer-only requests, stream multipart form directly with zero extra body allocation.
        bool needsHmac = !string.IsNullOrEmpty(_config.HmacSecret) || HasHmacSignatureHeader(ctx.Request.Headers);
        byte[]? bodyBytes = null;
        if (needsHmac)
        {
            ctx.Request.EnableBuffering();
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
            bodyBytes = ms.ToArray();
            ctx.Request.Body.Position = 0;
        }

        if (!IsAuthorized(ctx.Request, bodyBytes))
        {
            _log.Warn($"Rejected webhook from {remote}: bad/missing Authorization or HMAC signature");
            return Results.Json(new { ok = false, error = new { message = "unauthorized" } },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        if (!ctx.Request.HasFormContentType)
        {
            _log.Warn($"Rejected webhook from {remote}: content-type is not multipart/form-data ({ctx.Request.ContentType})");
            return Results.Json(new { ok = false, error = new { message = "expected multipart/form-data" } },
                statusCode: StatusCodes.Status415UnsupportedMediaType);
        }

        PebblePayload payload;
        IFormFile? audioFile = null;
        string? messageId = null;
        try
        {
            var form = await ctx.Request.ReadFormAsync();

            long? recordedAt = null;
            if (long.TryParse(form["recordedAt"].ToString(), out var ms)) recordedAt = ms;

            messageId = form["id"].ToString().Trim();
            audioFile = form.Files["audio"];
            long? audioSize = null;
            if (long.TryParse(ctx.Request.Headers["X-Audio-Size"].ToString(), out var hdrSize)) audioSize = hdrSize;
            else if (audioFile is not null) audioSize = audioFile.Length;

            payload = new PebblePayload
            {
                Transcription = form["transcription"].ToString(),
                RecordedAtMs = recordedAt,
                Client = form["client"].ToString(),
                HasAudio = audioFile is not null,
                AudioSizeBytes = audioSize,
            };
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to parse webhook body from {remote}", ex);
            WebhookFailed?.Invoke("Could not parse the webhook body");
            return Results.Json(new { ok = false, error = new { message = "could not parse multipart body" } },
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (string.IsNullOrEmpty(messageId))
            return await ProcessPayloadAsync(ctx.RequestAborted, remote, payload, audioFile);

        switch (_seenMessages.TryClaim(messageId))
        {
            case DuplicateMessageTracker.Claim.InProgress:
                _log.Info($"Duplicate webhook from {remote} for message id '{messageId}' — already processing");
                return Results.Json(new { ok = false, error = new { message = "already processing", id = messageId } },
                    statusCode: StatusCodes.Status409Conflict);
            case DuplicateMessageTracker.Claim.AlreadyProcessed:
                _log.Info($"Duplicate webhook from {remote} for message id '{messageId}' — already processed, ignoring");
                return Results.Json(new { ok = true, data = new { duplicate = true, id = messageId, message = "already processed" } });
        }

        // Journal before any work starts, so a crash mid-processing can be replayed on restart.
        // Audio-only webhooks aren't journaled — the audio itself isn't kept.
        if (!string.IsNullOrWhiteSpace(payload.Transcription)) _inbox?.Save(messageId, payload);

        var succeeded = false;
        try
        {
            var result = await ProcessPayloadAsync(ctx.RequestAborted, remote, payload, audioFile);
            succeeded = result is not IStatusCodeHttpResult { StatusCode: >= 400 };
            return result;
        }
        finally
        {
            // A failed attempt releases the id so the sender's retry is processed normally.
            _seenMessages.Complete(messageId, succeeded);
            _inbox?.Remove(messageId);
        }
    }

    /// <summary>Webhooks cut off by a crash or kill last time, with their ids claimed so a
    /// sender retry can't race the replay. An id already handled (persisted tracker) is dropped.</summary>
    private List<Inbox.Item> ClaimPendingInbox()
    {
        var claimed = new List<Inbox.Item>();
        foreach (var item in _inbox?.TakePending() ?? new List<Inbox.Item>())
        {
            if (_seenMessages.TryClaim(item.Id) == DuplicateMessageTracker.Claim.New)
                claimed.Add(item);
            else
                _inbox!.Remove(item.Id);
        }
        return claimed;
    }

    private async Task ReplayInboxAsync(List<Inbox.Item> items)
    {
        foreach (var item in items)
        {
            var succeeded = false;
            try
            {
                _log.Warn($"Replaying webhook '{item.Id}' that was interrupted last run");
                var result = await ProcessPayloadAsync(CancellationToken.None, "inbox-replay", item.Payload, null);
                succeeded = result is not IStatusCodeHttpResult { StatusCode: >= 400 };
            }
            catch (Exception ex)
            {
                _log.Error($"Replay of webhook '{item.Id}' failed", ex);
            }
            finally
            {
                _seenMessages.Complete(item.Id, succeeded);
                _inbox?.Remove(item.Id);
            }
        }
    }

    private async Task<IResult> ProcessPayloadAsync(CancellationToken ct, string remote, PebblePayload payload, IFormFile? audioFile)
    {
        // Pebble normally transcribes on-device; this only fires for a genuinely audio-only
        // webhook, and never overrides a transcription Pebble already sent.
        if (string.IsNullOrWhiteSpace(payload.Transcription) && audioFile is not null && _config.WhisperAsr.Enabled)
        {
            try
            {
                using var audioBytes = new MemoryStream();
                await audioFile.CopyToAsync(audioBytes, ct);
                var audioMemory = audioBytes.GetBuffer().AsMemory(0, (int)audioBytes.Length);

                ReadOnlyMemory<byte> uploadMemory = audioMemory;
                var uploadFileName = audioFile.FileName;

                try
                {
                    var (transcodedAudio, transcodedName, _) = await AudioDecoder.EnsureWavAsync(
                        audioMemory, audioFile.FileName, ct);
                    uploadMemory = transcodedAudio;
                    uploadFileName = transcodedName;
                    if (uploadMemory.Length != audioMemory.Length || !uploadMemory.Equals(audioMemory))
                    {
                        _log.Info($"Transcoded audio from {remote} ({audioMemory.Length} bytes, '{audioFile.FileName}') to WAV ({uploadMemory.Length} bytes) for Whisper ASR");
                    }
                }
                catch (Exception ex)
                {
                    _log.Warn($"Audio pre-decoding to WAV failed ({ex.Message}), uploading original audio: {uploadFileName}");
                }

                using var whisperAsr = new WhisperAsrClient(_config.WhisperAsr, _log);
                var text = await whisperAsr.TranscribeAsync(uploadMemory, uploadFileName, ct);
                if (text is not null)
                {
                    _log.Info($"Whisper ASR transcribed audio from {remote} ({uploadMemory.Length} bytes): \"{text}\"");
                    payload = payload with { Transcription = text };
                }
                else
                {
                    _log.Warn($"Whisper ASR returned no usable text from {remote} ({uploadMemory.Length} bytes audio) — falling back to normal handling (will return 422 if no text)");
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"Whisper ASR transcription failed, falling back to normal handling: {ex.Message}");
            }
        }

        _log.Info($"Webhook from {remote}: client='{payload.Client}', " +
                  $"transcription={(string.IsNullOrWhiteSpace(payload.Transcription) ? "none" : payload.Transcription!.Length + " chars")}, " +
                  $"audio={(payload.HasAudio ? "yes" : "no")}");

        var testPhrase = _config.TestEventPhrase;
        if (!string.IsNullOrWhiteSpace(testPhrase) &&
            string.Equals(payload.Transcription?.Trim(), testPhrase.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            _log.Info($"Connectivity-test webhook from {remote} — notifying, no task created");
            TestEventReceived?.Invoke(remote);
            return Results.Json(new { ok = true, data = new { test = true, message = "test event received" } });
        }

        if (StatusReport is { } statusReport && IsStatusRequest(payload.Transcription))
        {
            _log.Info($"Status request from {remote} — replying with service status, no task created");
            await SendWebhookReceiptAsync($"Status\n{statusReport()}");
            return Results.Json(new { ok = true, data = new { status = true } });
        }

        SpTaskRequest taskReq;
        try
        {
            taskReq = PayloadConverter.ToTask(payload, _config);
        }
        catch (PayloadConverter.ConversionException ex)
        {
            _log.Warn($"Nothing to create: {ex.Message}");
            WebhookFailed?.Invoke(ex.Message);
            return Results.Json(new { ok = false, error = new { message = ex.Message } },
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        // Reused across requests (and internally across the calls below) instead of opening a
        // fresh connection pool per webhook — this listener stays up for the app's lifetime.
        var sp = _spClient ??= new SuperProductivityClient(_config.SuperProductivity);
        // Not ctx.RequestAborted: if a slow tunnel drops the connection just as SP creates the
        // task, cancelling here would make us re-queue it and create a duplicate on retry. The
        // HTTP client's own 15 s timeout bounds the call.
        var routing = new AiRoutingResult(null, false);
        if (_config.AiClassifier.Enabled && !string.IsNullOrWhiteSpace(payload.Transcription))
            routing = await ApplyAiClassificationAsync(sp, taskReq, payload.Transcription, payload.RecordedAt);
        var shoppingItems = routing.ShoppingItems;

        // A non-null kind means Joplin/Calendar/Beeper/web search already succeeded. Otherwise the
        // receipt waits until the Super Productivity task(s) are confirmed created below.
        var receiptSent = false;
        if (routing.CaptureKind is not null)
        {
            await SendWebhookReceiptAsync(routing.CaptureKind);
            receiptSent = true;
        }

        if (shoppingItems is null && routing.SkipSuperProductivityTask)
        {
            _log.Info($"Routed elsewhere, skipping Super Productivity task: \"{taskReq.Title}\"");
            return Results.Json(new { ok = true, data = new { routedElsewhere = true } });
        }

        await _captureTag.ApplyAsync(sp, taskReq, CancellationToken.None);

        if (shoppingItems is { Count: > 0 })
        {
            // One task per item — "bread, milk, eggs" becomes three separate tasks, not one.
            var outcomes = new List<TaskOutcome>();
            foreach (var item in shoppingItems)
                outcomes.Add(await CreateOrQueueTaskAsync(sp, ForShoppingItem(taskReq, item, _config.TitleMaxLength, routing.ShoppingMerchant)));

            var anyPermanentFailure = outcomes.Any(o => o.Error is not null);
            if (!receiptSent && outcomes.All(o => !o.Queued && o.Error is null))
                await SendWebhookReceiptAsync("Shopping List Updated");
            return Results.Json(new
            {
                ok = !anyPermanentFailure,
                data = new
                {
                    tasks = outcomes.Select(o => new { title = o.Title, taskId = o.TaskId, queued = o.Queued, error = o.Error }),
                },
            }, statusCode: anyPermanentFailure ? StatusCodes.Status502BadGateway : StatusCodes.Status200OK);
        }

        var outcome = await CreateOrQueueTaskAsync(sp, taskReq);
        if (outcome.Error is not null)
            return Results.Json(new { ok = false, error = new { message = outcome.Error } },
                statusCode: StatusCodes.Status502BadGateway);
        if (outcome.Queued)
            return Results.Json(new { ok = true, data = new { queued = true, title = outcome.Title } },
                statusCode: StatusCodes.Status202Accepted);
        if (!receiptSent)
            await SendWebhookReceiptAsync("Task Created");
        return Results.Json(new { ok = true, data = new { taskId = outcome.TaskId, title = outcome.Title } });
    }

    private sealed record TaskOutcome(string Title, string? TaskId, bool Queued, string? Error);

    /// <summary>Result of AI classification: bare shopping items to split into separate tasks
    /// (null when this isn't a multi-item shopping capture), and whether the main Super
    /// Productivity task should be skipped because <see cref="AppConfig.AiClassifierConfig.ExclusiveRouting"/>
    /// is on and the transcription was successfully routed to Joplin/Calendar/Beeper instead.</summary>
    private sealed record AiRoutingResult(List<string>? ShoppingItems, bool SkipSuperProductivityTask, string? CaptureKind = null,
        string? ShoppingMerchant = null);

    /// <summary>
    /// Creates one task in Super Productivity, or queues it in the outbox on a transient
    /// failure. Never throws — SP being down or rejecting the request comes back as a
    /// <see cref="TaskOutcome"/> instead, so a caller filing several tasks from one webhook
    /// (the shopping-list split) can let one item's failure not block the others.
    /// </summary>
    private async Task<TaskOutcome> CreateOrQueueTaskAsync(SuperProductivityClient sp, SpTaskRequest task)
    {
        try
        {
            var result = await sp.CreateTaskAsync(task, CancellationToken.None);
            _log.Info($"Created Super Productivity task{(result.TaskId is null ? "" : $" {result.TaskId}")}" +
                      $"{(result.ViaSuperSync ? " via SuperSync" : "")}: \"{task.Title}\"");
            TaskCreated?.Invoke(task.Title, result.TaskId);
            return new TaskOutcome(task.Title, result.TaskId, false, null);
        }
        catch (SpApiException ex) when (ex.Permanent)
        {
            // Retrying the same request can't help (bad token, rejected body) — fail loudly.
            _log.Error($"Super Productivity rejected task \"{task.Title}\" — not queued", ex);
            WebhookFailed?.Invoke(ex.Message);
            return new TaskOutcome(task.Title, null, false, ex.Message);
        }
        catch (Exception ex) when (ex is SpApiException or HttpRequestException or TaskCanceledException)
        {
            // SP unreachable / transient — persist and let the outbox retry it.
            _log.Warn($"Could not deliver task \"{task.Title}\" now ({ex.Message}) — queuing for retry");
            _outbox.Enqueue(task);
            TaskQueued?.Invoke(task.Title);
            return new TaskOutcome(task.Title, null, true, null);
        }
    }

    /// <summary>Clones <paramref name="template"/> (same notes/project/tags) with its title
    /// replaced by one bare shopping item, prefixed with the merchant when the transcription named
    /// one (e.g. "Home Depot - Nails"), capped the same way PayloadConverter caps titles.</summary>
    private static SpTaskRequest ForShoppingItem(SpTaskRequest template, string item, int titleMaxLength, string? merchant)
    {
        var title = string.IsNullOrWhiteSpace(merchant) ? item : $"{merchant} - {item}";
        return new SpTaskRequest
        {
            Title = PayloadConverter.CapTitle(title, titleMaxLength),
            Notes = template.Notes,
            ProjectId = template.ProjectId,
            TagIds = template.TagIds is null ? null : new List<string>(template.TagIds),
            TimeEstimate = template.TimeEstimate,
            Priority = template.Priority,
        };
    }

    /// <summary>
    /// Lets Claude override the static projectId/tagIds on <paramref name="taskReq"/> by reading
    /// the transcription against the caller's real SP projects/tags. Never throws and never
    /// leaves the task without the static config's values — any failure here (SP list fetch,
    /// the AI call itself) just leaves taskReq as PayloadConverter built it.
    /// Returns the bare shopping items to split into separate tasks, or null when this isn't a
    /// multi-item shopping capture (the caller then uses taskReq as-is, title included).
    /// </summary>
    private async Task<AiRoutingResult> ApplyAiClassificationAsync(
        SuperProductivityClient sp, SpTaskRequest taskReq, string transcription, DateTimeOffset? recordedAt)
    {
        try
        {
            // Independent reads — fire them together instead of waiting on each in turn.
            var projectsTask = sp.GetProjectsAsync(CancellationToken.None);
            var tagsTask = sp.GetTagsAsync(CancellationToken.None);
            var joplinTagsTask = _config.Joplin.Enabled ? FetchJoplinTagsAsync() : null;
            var homeEntitiesTask = _config.HomeAssistant.Enabled ? FetchHomeEntitiesAsync() : null;

            var projects = await projectsTask;
            var tags = await tagsTask;
            var joplinTags = joplinTagsTask is null ? Array.Empty<SpNamedItem>() : await joplinTagsTask;
            var homeEntities = homeEntitiesTask is null ? Array.Empty<HomeAssistantClient.Entity>() : await homeEntitiesTask;

            // Gemini/OpenAI search under their own credential; Ollama has none of its own and
            // falls back to Claude, so it needs the same key Claude itself would.
            var hasSearchCredential = _config.AiClassifier.Provider switch
            {
                "gemini" => !string.IsNullOrWhiteSpace(_config.AiClassifier.GeminiApiKey),
                "openai" => !string.IsNullOrWhiteSpace(_config.AiClassifier.OpenAiApiKey),
                _ => !string.IsNullOrWhiteSpace(_config.AiClassifier.ApiKey),
            };
            var includeWebSearch = _config.WebSearch.Enabled && _config.Telegram.Enabled &&
                !string.IsNullOrWhiteSpace(_config.Telegram.BotToken) && !string.IsNullOrWhiteSpace(_config.WebSearch.ChatId) &&
                hasSearchCredential;

            // Answers go out in the webhook receipt, so that has to be deliverable too.
            var receiptReady = _config.Telegram.Enabled && !string.IsNullOrWhiteSpace(_config.Telegram.BotToken) &&
                _config.WebhookReceipt.Enabled && !string.IsNullOrWhiteSpace(_config.WebhookReceipt.ChatId);
            var includeQuestion = receiptReady && hasSearchCredential;

            var referenceTime = (recordedAt ?? DateTimeOffset.UtcNow).ToLocalTime();
            var result = await _classifier.ClassifyAsync(
                transcription, projects, tags, joplinTags, referenceTime, _config.Beeper.Enabled, includeWebSearch,
                _config.Sonarr.Enabled || _config.Radarr.Enabled, homeEntities, includeQuestion, CancellationToken.None);
            if (result is null) return new AiRoutingResult(null, false);

            if (result.ProjectId is not null) taskReq.ProjectId = result.ProjectId;
            if (result.TagIds.Count > 0)
            {
                taskReq.TagIds = _config.AiClassifier.RequireTags && _config.AiClassifier.AlwaysAddDefaultTag &&
                    _config.SuperProductivity.TagIds is { Count: > 0 } defaultTagIds
                        ? result.TagIds.Concat(defaultTagIds).Distinct().ToList()
                        : result.TagIds;
            }
            if (result.Priority is not null) taskReq.Priority = result.Priority;
            // Strips command phrasing ("add a task to", "remind me to") from the task's title.
            // A multi-item shopping capture overwrites this again per item below — harmless.
            if (result.TaskTitle is not null)
                taskReq.Title = PayloadConverter.CapTitle(result.TaskTitle, _config.TitleMaxLength);

            var joplinSent = false;
            if (result.IsNote && _config.Joplin.Enabled)
            {
                var joplinTagIds = result.JoplinTagIds.Count > 0 ? result.JoplinTagIds : _config.Joplin.DefaultTagIds;
                // Joplin's /notes "tags" field wants titles, not ids — resolve against the list
                // we just fetched. An id that no longer exists (stale config) is dropped silently.
                var joplinTagTitles = joplinTagIds
                    .Select(id => joplinTags.FirstOrDefault(t => t.Id == id)?.Title)
                    .Where(title => title is not null)
                    .Select(title => title!)
                    .ToList();
                joplinSent = await SendToJoplinAsync(taskReq.Title, taskReq.Notes ?? transcription, joplinTagTitles);
            }

            var calendarCreated = false;
            if (result.IsCalendarEvent && result.EventStart is not null)
            {
                // Sets the SP task's own due date regardless of Google Calendar — the calendar
                // event (below) is a separate, additive destination gated on its own toggle. Moot
                // when ExclusiveRouting ends up skipping the task entirely, but harmless either way.
                if (result.EventAllDay)
                {
                    taskReq.DueDay = result.EventStart.Value.ToString("yyyy-MM-dd");
                }
                else
                {
                    taskReq.DueWithTime = result.EventStart.Value.ToUnixTimeMilliseconds();
                    taskReq.HasPlannedTime = true;
                }

                if (_config.GoogleCalendar.Enabled)
                {
                    calendarCreated = await CreateCalendarEventAsync(
                        result.EventTitle ?? taskReq.Title, taskReq.Notes ?? transcription,
                        result.EventStart.Value, result.EventEnd, result.EventAllDay);
                }
            }

            var messageSent = false;
            if (result.IsMessage && _config.Beeper.Enabled && result.MessageRecipient is not null && result.MessageText is not null)
                messageSent = await SendBeeperMessageAsync(result.MessageRecipient, result.MessageText, result.MessagePlatform);

            var webSearchSent = false;
            if (result.IsWebSearch && includeWebSearch && result.WebSearchQuery is not null)
                webSearchSent = await RunWebSearchAndSendAsync(result.WebSearchQuery);

            string? mediaReceipt = null;
            if (result.MediaKind is not null && result.MediaTitle is not null)
                mediaReceipt = await AddMediaAsync(result.MediaKind, result.MediaTitle);

            // A "search the web" request already answered itself above; this is for plain questions.
            string? questionReceipt = null;
            if (result.HomeAnswer is not null && receiptReady)
                // Answered from Home Assistant's own states, so no web search is needed.
                questionReceipt = $"Question, {PayloadConverter.CapTitle(result.HomeAnswer, 1000)}";
            else if (result.IsQuestion && !webSearchSent && result.QuestionText is not null)
                questionReceipt = await AnswerQuestionAsync(result.QuestionText);

            string? homeReceipt = null;
            if (result.HomeAction is not null && result.HomeEntityIds is { Count: > 0 })
                homeReceipt = await CallHomeAssistantAsync(result.HomeAction, result.HomeEntityIds, homeEntities);

            if (!result.IsShopping)
            {
                // Skip the Super Productivity task only when routing elsewhere actually worked —
                // a failed/disabled destination still falls back to the normal SP task below.
                var skip = _config.AiClassifier.ExclusiveRouting && (joplinSent || calendarCreated || messageSent || webSearchSent || mediaReceipt is not null || homeReceipt is not null || questionReceipt is not null);
                // Names only a destination that actually succeeded (priority matters when several
                // fired); null leaves the receipt to the Super Productivity task fallback.
                var captureKind = webSearchSent ? "Web Search Sent"
                    : messageSent ? "Message Sent"
                    : mediaReceipt is not null ? mediaReceipt
                    : homeReceipt is not null ? homeReceipt
                    : questionReceipt is not null ? questionReceipt
                    : calendarCreated ? "Event Created"
                    : joplinSent ? "Note Created"
                    : null;
                return new AiRoutingResult(null, skip, captureKind);
            }

            // A configured shopping project always wins over the classifier's own pick —
            // it's an explicit user choice, not a guess from project titles.
            var shoppingProjectId = _config.SuperProductivity.ShoppingProjectId;
            if (!string.IsNullOrWhiteSpace(shoppingProjectId))
                taskReq.ProjectId = shoppingProjectId.Trim();

            return new AiRoutingResult(result.ShoppingItems.Count > 0 ? result.ShoppingItems : null, false,
                ShoppingMerchant: result.ShoppingMerchant);
        }
        catch (Exception ex) when (ex is SpApiException or HttpRequestException or TaskCanceledException)
        {
            _log.Warn($"AI classify skipped (couldn't load projects/tags): {ex.Message}");
            return new AiRoutingResult(null, false);
        }
    }

    /// <summary>Needed both to let the AI pick a Joplin tag (only asked for while requireTags is
    /// on) and to resolve whichever ids end up applied (AI-picked or the static default) to the
    /// titles Joplin's API actually wants. Never throws — a failure just means continuing without
    /// Joplin tags, same as before this was pulled out to run alongside the SP project/tag fetch.</summary>
    private async Task<IReadOnlyList<SpNamedItem>> FetchJoplinTagsAsync()
    {
        try
        {
            using var joplin = new JoplinClient(_config.Joplin);
            return await joplin.GetTagsAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is JoplinApiException or HttpRequestException or TaskCanceledException)
        {
            _log.Warn($"AI classify: couldn't load Joplin tags ({ex.Message}) — continuing without them");
            return Array.Empty<SpNamedItem>();
        }
    }

    /// <summary>
    /// Best-effort archive copy in Joplin for transcriptions the classifier marked as a note.
    /// Normally additive — the Super Productivity task is created either way — but with
    /// aiClassifier.exclusiveRouting on, a true result here tells the caller to skip that task
    /// instead. Any failure is just logged and never surfaces to the webhook caller.
    /// </summary>
    private async Task<bool> SendToJoplinAsync(string title, string body, IReadOnlyList<string>? tagTitles)
    {
        try
        {
            using var joplin = new JoplinClient(_config.Joplin);
            await joplin.CreateNoteAsync(title, body, tagTitles, CancellationToken.None);
            _log.Info($"Sent note to Joplin: \"{title}\"" + (tagTitles is { Count: > 0 } ? $" [{string.Join(',', tagTitles)}]" : ""));
            return true;
        }
        catch (Exception ex) when (ex is JoplinApiException or HttpRequestException or TaskCanceledException)
        {
            _log.Warn($"Could not send note to Joplin: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Best-effort calendar event for transcriptions the classifier marked as dated/timed.
    /// Normally additive — the Super Productivity task is created either way — but with
    /// aiClassifier.exclusiveRouting on, a true result here tells the caller to skip that task
    /// instead. Any failure is just logged and never surfaces to the webhook caller.
    /// </summary>
    private async Task<bool> CreateCalendarEventAsync(string title, string description, DateTimeOffset start, DateTimeOffset? end, bool allDay)
    {
        try
        {
            using var calendar = new GoogleCalendarClient(_config.GoogleCalendar);
            await calendar.CreateEventAsync(title, description, start, end, allDay, CancellationToken.None);
            _log.Info($"Created Google Calendar event: \"{title}\" at {start:yyyy-MM-dd HH:mm zzz}" + (allDay ? " (all day)" : ""));
            return true;
        }
        catch (Exception ex) when (ex is GoogleCalendarApiException or HttpRequestException or TaskCanceledException)
        {
            _log.Warn($"Could not create Google Calendar event: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Best-effort answer to a spoken question, using the same search-backed AI call as web search.
    /// Returns the receipt text ("Question, Yes it will rain tomorrow") or null when no answer came
    /// back. The answer rides in the webhook receipt itself, so nothing else is sent.
    /// </summary>
    private async Task<string?> AnswerQuestionAsync(string question)
    {
        var answer = await _classifier.RunWebSearchAsync(question, _config.WebSearch.MaxUses, CancellationToken.None);
        if (answer is null)
        {
            _log.Warn($"Question not answered: no answer for \"{question}\"");
            return null;
        }

        _log.Info($"Answered question \"{question}\"");
        return $"Question, {PayloadConverter.CapTitle(answer, 1000)}";
    }

    private async Task<IReadOnlyList<HomeAssistantClient.Entity>> FetchHomeEntitiesAsync()
    {
        try
        {
            using var ha = new HomeAssistantClient(_config.HomeAssistant);
            return await ha.GetEntitiesAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is HomeAssistantApiException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            _log.Warn($"AI classify: couldn't load Home Assistant entities ({ex.Message}) — continuing without them");
            return Array.Empty<HomeAssistantClient.Entity>();
        }
    }

    /// <summary>
    /// Best-effort device command ("turn off the kitchen lights"). Every entity is called
    /// separately; counts as success if at least one worked. Returns the receipt text
    /// ("Turned Off, Kitchen Light, Kitchen Counter") naming only the entities that succeeded, or
    /// null if none did. Normally additive, but with aiClassifier.exclusiveRouting on a non-null
    /// result tells the caller to skip the Super Productivity task.
    /// </summary>
    private async Task<string?> CallHomeAssistantAsync(string action, List<string> entityIds, IReadOnlyList<HomeAssistantClient.Entity> entities)
    {
        var done = new List<string>();
        try
        {
            using var ha = new HomeAssistantClient(_config.HomeAssistant);
            foreach (var id in entityIds)
            {
                try
                {
                    await ha.CallAsync(id, action, CancellationToken.None);
                    done.Add(entities.FirstOrDefault(e => e.Id == id)?.Name ?? id);
                    _log.Info($"Home Assistant: {action} {id}");
                }
                catch (Exception ex) when (ex is HomeAssistantApiException or HttpRequestException or TaskCanceledException)
                {
                    _log.Warn($"Home Assistant {action} {id} failed: {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException)
        {
            _log.Warn($"Home Assistant call skipped: {ex.Message}");
        }

        if (done.Count == 0) return null;
        var verb = action switch
        {
            "turn_on" => "Turned On",
            "turn_off" => "Turned Off",
            "toggle" => "Toggled",
            "open" => "Opened",
            _ => "Closed",
        };
        return $"{verb}, {string.Join(", ", done)}";
    }

    /// <summary>
    /// Best-effort add of a TV show (Sonarr) or movie (Radarr) the classifier heard the user ask
    /// for. Normally additive — the Super Productivity task is created either way — but with
    /// aiClassifier.exclusiveRouting on, a true result here tells the caller to skip that task
    /// instead. Returns the receipt text ("Show Added, Simpsons") or null. A title already in the library counts as success; a disabled integration, no
    /// match, or any API failure is just logged.
    /// </summary>
    private async Task<string?> AddMediaAsync(string kind, string title)
    {
        var series = kind == "series";
        var cfg = series ? (AppConfig.ArrConfig)_config.Sonarr : _config.Radarr;
        if (!cfg.Enabled) return null;

        try
        {
            using var arr = new ArrClient(cfg, series ? ArrClient.Kind.Series : ArrClient.Kind.Movie);
            var added = await arr.AddAsync(title, CancellationToken.None);
            if (added is null)
            {
                _log.Warn($"{(series ? "Sonarr" : "Radarr")}: no match found for \"{title}\"");
                return null;
            }

            var label = added.Year is null ? added.Title : $"{added.Title} ({added.Year})";
            _log.Info(added.AlreadyInLibrary
                ? $"{(series ? "Sonarr" : "Radarr")}: \"{label}\" is already in the library"
                : $"Added to {(series ? "Sonarr" : "Radarr")}: \"{label}\"");
            // Names the title Sonarr/Radarr actually matched, not the spoken one.
            return $"{(series ? "Show" : "Movie")} {(added.AlreadyInLibrary ? "Already In Library" : "Added")}, {added.Title}";
        }
        catch (Exception ex) when (ex is ArrApiException or HttpRequestException or TaskCanceledException)
        {
            _log.Warn($"Could not add \"{title}\" to {(series ? "Sonarr" : "Radarr")}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Best-effort receipt — "Webhook Received, Note Created" and so on. Only sent once the named
    /// destination (task, note, event, message, web search) actually succeeded, so it doubles as
    /// a delivery confirmation; a task queued for retry or rejected sends nothing.
    /// </summary>
    private async Task SendWebhookReceiptAsync(string captureKind)
    {
        var cfg = _config.WebhookReceipt;
        if (!cfg.Enabled || string.IsNullOrWhiteSpace(cfg.ChatId)) return;

        await SendTelegramMessageAsync(cfg.ChatId, $"Webhook Received, {captureKind}");
    }

    /// <summary>
    /// Best-effort web search for transcriptions the classifier marked as "search the web for
    /// X" — runs the search via Claude, then sends the summary through the Telegram bot. Normally
    /// additive — the Super Productivity task is created either way — but with
    /// aiClassifier.exclusiveRouting on, a true result here tells the caller to skip that task
    /// instead.
    /// </summary>
    private async Task<bool> RunWebSearchAndSendAsync(string query)
    {
        var summary = await _classifier.RunWebSearchAsync(query, _config.WebSearch.MaxUses, CancellationToken.None);
        if (summary is null)
        {
            _log.Warn($"Web search skipped sending: no summary for \"{query}\"");
            return false;
        }

        // The webhook receipt goes out right after this returns — a short gap keeps the two
        // Telegram messages from landing in the same instant/notification burst.
        if (_config.WebSearch.SendDelaySeconds > 0)
            await Task.Delay(TimeSpan.FromSeconds(_config.WebSearch.SendDelaySeconds), CancellationToken.None);

        return await SendTelegramMessageAsync(_config.WebSearch.ChatId, $"🔍 {query}\n\n{summary}");
    }

    /// <summary>
    /// Best-effort Telegram delivery — used only for web-search summaries and webhook receipts,
    /// each to its own configured chat id. The general "send a message to X" feature stays on
    /// Beeper (see <see cref="SendBeeperMessageAsync"/>), which is where recipient search across
    /// whatever chats already exist actually matters. Also reports a Beeper recipient that has no
    /// chat, to the webhook receipt chat.
    /// </summary>
    private async Task<bool> SendTelegramMessageAsync(string chatId, string text)
    {
        var cfg = _config.Telegram;
        if (!cfg.Enabled || string.IsNullOrWhiteSpace(cfg.BotToken) || string.IsNullOrWhiteSpace(chatId)) return false;

        try
        {
            using var telegram = new TelegramClient(cfg);
            await telegram.SendMessageAsync(chatId, text, CancellationToken.None);
            _log.Info($"Sent Telegram message: \"{text}\"");
            return true;
        }
        catch (Exception ex) when (ex is TelegramApiException or HttpRequestException or TaskCanceledException)
        {
            _log.Warn($"Could not send Telegram message: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Best-effort chat message via Beeper Desktop, to whichever network (Telegram, WhatsApp,
    /// iMessage, etc.) the recipient's chat already uses. Normally additive — the Super
    /// Productivity task is created either way — but with aiClassifier.exclusiveRouting on, a
    /// true result here tells the caller to skip that task instead. Never guesses: when
    /// <paramref name="platform"/> is given (e.g. "telegram"), only chats on that platform are
    /// considered — this is what disambiguates a recipient with several chats down to the one the
    /// user actually named. Anything left ambiguous, or a named platform with no matching chat,
    /// counts as failure, so the Super Productivity task still gets created as a fallback.
    /// </summary>
    private async Task<bool> SendBeeperMessageAsync(string recipient, string text, string? platform)
    {
        try
        {
            using var beeper = new BeeperClient(_config.Beeper);
            var matches = await beeper.SearchSingleChatsAsync(recipient, CancellationToken.None);

            if (matches.Count == 0)
            {
                _log.Warn($"Beeper message skipped: no chat found matching \"{recipient}\"");
                await SendTelegramMessageAsync(_config.WebhookReceipt.ChatId, $"No chat found: {recipient}");
                return false;
            }

            var candidates = matches;
            if (!string.IsNullOrWhiteSpace(platform))
            {
                candidates = matches.Where(m => BeeperClient.NetworkMatchesPlatform(m.Network, platform)).ToList();
                if (candidates.Count == 0)
                {
                    _log.Warn($"Beeper message skipped: \"{recipient}\" has no {platform} chat " +
                              $"(has: {string.Join(", ", matches.Select(m => m.Network))}) — not sending");
                    return false;
                }
            }

            if (candidates.Count > 1)
            {
                _log.Warn($"Beeper message skipped: \"{recipient}\" matches {candidates.Count} chats " +
                          $"({string.Join(", ", candidates.Select(m => $"{m.Title} [{m.Network}]"))}) — ambiguous, not sending");
                return false;
            }

            var chat = candidates[0];
            await beeper.SendMessageAsync(chat.ChatId, text, CancellationToken.None);
            _log.Info($"Sent Beeper message to {chat.Title} [{chat.Network}]: \"{text}\"");
            return true;
        }
        catch (Exception ex) when (ex is BeeperApiException or HttpRequestException or TaskCanceledException)
        {
            _log.Warn($"Could not send Beeper message to \"{recipient}\": {ex.Message}");
            return false;
        }
    }

    internal const string SignatureHeader = "X-Index-Signature";
    internal static readonly TimeSpan ReplayWindow = TimeSpan.FromMinutes(5);

    // Accepted delivery IDs -> expiry, so a captured signed request can't be replayed inside the window.
    private readonly Dictionary<string, DateTimeOffset> _seenDeliveries = new();

    internal static bool HasHmacSignatureHeader(IHeaderDictionary headers) =>
        !string.IsNullOrEmpty(headers[SignatureHeader].ToString());

    /// <summary>Records a delivery ID; returns false if it was already accepted within the window.</summary>
    private bool TryRegisterDelivery(IHeaderDictionary headers)
    {
        var id = headers["X-Index-Delivery"].ToString().Trim();
        var now = DateTimeOffset.UtcNow;
        lock (_seenDeliveries)
        {
            foreach (var key in _seenDeliveries.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList())
                _seenDeliveries.Remove(key);
            return _seenDeliveries.TryAdd(id, now + ReplayWindow + ReplayWindow); // timestamps are valid ±window
        }
    }

    internal bool IsAuthorized(HttpRequest request, byte[]? bodyBytes)
    {
        var expectedBearer = _config.InboundAuthToken?.Trim() ?? "";
        var expectedHmac = _config.HmacSecret?.Trim() ?? "";

        // If neither is configured, auth is disabled
        if (string.IsNullOrEmpty(expectedBearer) && string.IsNullOrEmpty(expectedHmac))
            return true;

        var bodySpan = (ReadOnlySpan<byte>)(bodyBytes ?? Array.Empty<byte>());

        var signed = false;

        // If HMAC secret is configured, require a valid HMAC-SHA256 signature
        if (!string.IsNullOrEmpty(expectedHmac))
        {
            if (!VerifyHmacSignature(request.Headers, bodySpan, expectedHmac))
            {
                _log.Warn("Inbound webhook HMAC-SHA256 signature verification failed");
                return false;
            }
            signed = true;
        }

        // If InboundAuthToken is configured:
        if (!string.IsNullOrEmpty(expectedBearer))
        {
            var header = request.Headers.Authorization.ToString().Trim();
            if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                header = header["Bearer ".Length..].Trim();

            if (string.IsNullOrEmpty(header))
                header = request.Headers["X-Widget-Token"].ToString().Trim();
            if (string.IsNullOrEmpty(header))
                header = request.Headers["X-Pebble-Token"].ToString().Trim();

            if (!FixedTimeEquals(header, expectedBearer))
            {
                // If Bearer token didn't match and HmacSecret was not explicitly set,
                // allow InboundAuthToken to serve as the HMAC secret
                if (string.IsNullOrEmpty(expectedHmac) && VerifyHmacSignature(request.Headers, bodySpan, expectedBearer))
                {
                    return AcceptDelivery(request.Headers);
                }
                _log.Warn("Inbound webhook Bearer token verification failed");
                return false;
            }
        }

        return !signed || AcceptDelivery(request.Headers);
    }

    private bool AcceptDelivery(IHeaderDictionary headers)
    {
        if (TryRegisterDelivery(headers)) return true;
        _log.Warn("Inbound webhook rejected: delivery ID already seen (replay)");
        return false;
    }

    /// <summary>
    /// Verifies Pebble Index webhook v1 signing: HMAC-SHA256 (key = UTF-8 secret) over
    /// "v1\n{timestamp}\n{delivery}\n{trigger}\n{isTest 1|0}\n" followed by the raw multipart body,
    /// sent as lowercase hex in X-Index-Signature. Also rejects timestamps outside the replay window.
    /// </summary>
    internal static bool VerifyHmacSignature(IHeaderDictionary headers, ReadOnlySpan<byte> bodyBytes, string secret, DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(secret)) return false;

        var sigHex = headers[SignatureHeader].ToString().Trim();
        if (sigHex.Length != 64) return false;

        byte[] receivedSig;
        try { receivedSig = Convert.FromHexString(sigHex); }
        catch (FormatException) { return false; }

        var version = headers["X-Index-Webhook-Version"].ToString().Trim();
        if (version != "1") return false;

        var timestamp = headers["X-Index-Timestamp"].ToString().Trim();
        if (!long.TryParse(timestamp, out var tsSeconds)) return false;
        var skew = (now ?? DateTimeOffset.UtcNow) - DateTimeOffset.FromUnixTimeSeconds(tsSeconds);
        if (skew.Duration() > ReplayWindow) return false;

        var delivery = headers["X-Index-Delivery"].ToString().Trim();
        if (delivery.Length == 0) return false;

        var trigger = headers["X-Index-Trigger"].ToString().Trim();
        var isTest = headers["X-Index-Test"].ToString().Trim().Length > 0 ? "1" : "0";
        var prefix = Encoding.UTF8.GetBytes($"v{version}\n{timestamp}\n{delivery}\n{trigger}\n{isTest}\n");

        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(secret));
        hmac.AppendData(prefix);
        hmac.AppendData(bodyBytes);
        return CryptographicOperations.FixedTimeEquals(receivedSig, hmac.GetHashAndReset());
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(ba), SHA256.HashData(bb));
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}

public static class AppInfo
{
    public static string Version =>
        typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
}

/// <summary>No-op <see cref="IHostLifetime"/> so the in-process web host doesn't touch OS signals.</summary>
internal sealed class NoopHostLifetime : IHostLifetime
{
    public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
