using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
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
    private WebApplication? _app;

    public WebhookServer(AppConfig config, Logger log)
    {
        _config = config;
        _log = log;
    }

    public bool IsRunning => _app is not null;

    /// <summary>Raised on the thread pool after a task is created.</summary>
    public event Action<string, string?>? TaskCreated;   // (title, taskId)
    /// <summary>Raised on the thread pool when a webhook call fails.</summary>
    public event Action<string>? WebhookFailed;          // (message)

    public async Task StartAsync()
    {
        if (_app is not null) return;

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production,
            ContentRootPath = AppContext.BaseDirectory,
            ApplicationName = "Index2SP",
        });

        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Limits.MaxRequestBodySize = 30 * 1024 * 1024; // Index audio clips are small; 30 MB is plenty
            k.AddServerHeader = false;
        });
        builder.WebHost.UseUrls($"http://{_config.ListenAddress}:{_config.Port}");

        var app = builder.Build();

        app.MapGet("/health", () => Results.Json(new { ok = true, service = "index2sp", version = AppInfo.Version }));

        // Cast to Delegate so the returned IResult is written to the response
        // (a bare method group binds as RequestDelegate and discards it).
        app.MapPost(_config.WebhookPath, (Delegate)HandleWebhookAsync);

        // Any other path -> 404 with a hint (helps while wiring up the tunnel).
        app.MapFallback(() => Results.Json(
            new { ok = false, error = new { message = $"POST your Pebble webhook to {_config.WebhookPath}" } },
            statusCode: StatusCodes.Status404NotFound));

        await app.StartAsync();
        _app = app;

        _log.Info($"Webhook listener started on http://{_config.ListenAddress}:{_config.Port}{_config.WebhookPath}");
        if (string.IsNullOrEmpty(_config.InboundAuthToken))
            _log.Warn("No inboundAuthToken set — anyone who can reach the listener can create tasks.");
    }

    public async Task StopAsync()
    {
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

        if (!IsAuthorized(ctx.Request))
        {
            _log.Warn($"Rejected webhook from {remote}: bad/missing Authorization");
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
        try
        {
            var form = await ctx.Request.ReadFormAsync();

            long? recordedAt = null;
            if (long.TryParse(form["recordedAt"].ToString(), out var ms)) recordedAt = ms;

            var audio = form.Files["audio"];
            long? audioSize = null;
            if (long.TryParse(ctx.Request.Headers["X-Audio-Size"].ToString(), out var hdrSize)) audioSize = hdrSize;
            else if (audio is not null) audioSize = audio.Length;

            payload = new PebblePayload
            {
                Transcription = form["transcription"].ToString(),
                RecordedAtMs = recordedAt,
                Client = form["client"].ToString(),
                HasAudio = audio is not null,
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

        _log.Info($"Webhook from {remote}: client='{payload.Client}', " +
                  $"transcription={(string.IsNullOrWhiteSpace(payload.Transcription) ? "none" : payload.Transcription!.Length + " chars")}, " +
                  $"audio={(payload.HasAudio ? "yes" : "no")}");

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

        try
        {
            using var sp = new SuperProductivityClient(_config.SuperProductivity);
            await ApplyCaptureTagAsync(sp, taskReq, ctx.RequestAborted);
            var result = await sp.CreateTaskAsync(taskReq, ctx.RequestAborted);
            _log.Info($"Created Super Productivity task{(result.TaskId is null ? "" : $" {result.TaskId}")}: \"{taskReq.Title}\"");
            TaskCreated?.Invoke(taskReq.Title, result.TaskId);
            return Results.Json(new { ok = true, data = new { taskId = result.TaskId, title = taskReq.Title } });
        }
        catch (Exception ex) when (ex is SpApiException or HttpRequestException or TaskCanceledException)
        {
            _log.Error($"Failed to create task \"{taskReq.Title}\"", ex);
            WebhookFailed?.Invoke(ex.Message);
            return Results.Json(new { ok = false, error = new { message = ex.Message } },
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private string? _resolvedCaptureTagId;

    /// <summary>Adds the configured capture tag (by id, or resolved from name via GET /tags) to the task.</summary>
    private async Task ApplyCaptureTagAsync(SuperProductivityClient sp, SpTaskRequest task, CancellationToken ct)
    {
        var cfg = _config.SuperProductivity;
        string? tagId = null;

        if (!string.IsNullOrWhiteSpace(cfg.CaptureTagId))
        {
            tagId = cfg.CaptureTagId.Trim();
        }
        else if (!string.IsNullOrWhiteSpace(cfg.CaptureTagName))
        {
            tagId = _resolvedCaptureTagId;
            if (tagId is null)
            {
                try
                {
                    var name = cfg.CaptureTagName.Trim();
                    var tags = await sp.GetTagsAsync(ct);
                    var match = tags.FirstOrDefault(t => string.Equals(t.Title, name, StringComparison.OrdinalIgnoreCase));
                    if (match is null)
                    {
                        _log.Warn($"captureTagName \"{name}\" not found in Super Productivity — task will not carry that tag");
                        return;
                    }
                    tagId = _resolvedCaptureTagId = match.Id;
                    _log.Info($"Resolved captureTagName \"{name}\" -> {match.Id}");
                }
                catch (Exception ex) when (ex is SpApiException or HttpRequestException or TaskCanceledException)
                {
                    _log.Warn($"Could not resolve captureTagName: {ex.Message}");
                    return;
                }
            }
        }

        if (tagId is null) return;

        task.TagIds ??= new List<string>();
        if (!task.TagIds.Contains(tagId))
            task.TagIds.Add(tagId);
    }

    private bool IsAuthorized(HttpRequest request)
    {
        var expected = _config.InboundAuthToken;
        if (string.IsNullOrEmpty(expected)) return true; // auth disabled

        var header = request.Headers.Authorization.ToString().Trim();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            header = header["Bearer ".Length..].Trim();

        return FixedTimeEquals(header, expected);
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
