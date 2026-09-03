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
    private WebApplication? _app;

    public WebhookServer(AppConfig config, Logger log, CaptureTagResolver captureTag, Outbox outbox)
    {
        _config = config;
        _log = log;
        _captureTag = captureTag;
        _outbox = outbox;
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

        var testPhrase = _config.TestEventPhrase;
        if (!string.IsNullOrWhiteSpace(testPhrase) &&
            string.Equals(payload.Transcription?.Trim(), testPhrase.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            _log.Info($"Connectivity-test webhook from {remote} — notifying, no task created");
            TestEventReceived?.Invoke(remote);
            return Results.Json(new { ok = true, data = new { test = true, message = "test event received" } });
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

        try
        {
            using var sp = new SuperProductivityClient(_config.SuperProductivity);
            // Not ctx.RequestAborted: if a slow tunnel drops the connection just as SP creates the
            // task, cancelling here would make us re-queue it and create a duplicate on retry. The
            // HTTP client's own 15 s timeout bounds the call.
            await _captureTag.ApplyAsync(sp, taskReq, CancellationToken.None);
            var result = await sp.CreateTaskAsync(taskReq, CancellationToken.None);
            _log.Info($"Created Super Productivity task{(result.TaskId is null ? "" : $" {result.TaskId}")}: \"{taskReq.Title}\"");
            TaskCreated?.Invoke(taskReq.Title, result.TaskId);
            return Results.Json(new { ok = true, data = new { taskId = result.TaskId, title = taskReq.Title } });
        }
        catch (SpApiException ex) when (ex.Permanent)
        {
            // Retrying the same request can't help (bad token, rejected body) — fail loudly.
            _log.Error($"Super Productivity rejected task \"{taskReq.Title}\" — not queued", ex);
            WebhookFailed?.Invoke(ex.Message);
            return Results.Json(new { ok = false, error = new { message = ex.Message } },
                statusCode: StatusCodes.Status502BadGateway);
        }
        catch (Exception ex) when (ex is SpApiException or HttpRequestException or TaskCanceledException)
        {
            // SP unreachable / transient — persist and let the outbox retry it.
            _log.Warn($"Could not deliver task \"{taskReq.Title}\" now ({ex.Message}) — queuing for retry");
            _outbox.Enqueue(taskReq);
            TaskQueued?.Invoke(taskReq.Title);
            return Results.Json(new { ok = true, data = new { queued = true, title = taskReq.Title } },
                statusCode: StatusCodes.Status202Accepted);
        }
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

/// <summary>No-op <see cref="IHostLifetime"/> so the in-process web host doesn't touch OS signals.</summary>
internal sealed class NoopHostLifetime : IHostLifetime
{
    public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
