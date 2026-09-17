using EventTracking.Persistence;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using EventTracking.Api.Persistence;
using Npgsql;

namespace EventTracking.Api.Services;

public sealed record BodyLimit(int Bytes);

public static class RequestBoundary
{
    public static async Task Invoke(HttpContext context, RequestDelegate next)
    {
        var started = Stopwatch.GetTimestamp();
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Requests");
        try
        {
            // Bound actual bytes as well as Content-Length, including chunked requests, before JSON binding.
            if (HttpMethods.IsPost(context.Request.Method))
            {
                int maxBytes = context.GetEndpoint()?.Metadata.GetMetadata<BodyLimit>()?.Bytes ?? EventValidation.MaxEventBytes;
                if (context.Request.ContentLength > maxBytes)
                {
                    await TooLarge(context, maxBytes);
                    return;
                }
                using var buffer = new MemoryStream();
                byte[] chunk = new byte[4096];
                while (true)
                {
                    int read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted);
                    if (read == 0) break;
                    if (buffer.Length + read > maxBytes)
                    {
                        await TooLarge(context, maxBytes);
                        return;
                    }
                    buffer.Write(chunk, 0, read);
                }
                buffer.Position = 0;
                if (maxBytes > EventValidation.MaxEventBytes && context.Request.HasJsonContentType())
                {
                    using var document = await JsonDocument.ParseAsync(buffer, cancellationToken: context.RequestAborted);
                    if (document.RootElement.ValueKind == JsonValueKind.Object
                        && document.RootElement.TryGetProperty("events", out var events) && events.ValueKind == JsonValueKind.Array)
                        foreach (var item in events.EnumerateArray())
                            if (Encoding.UTF8.GetByteCount(item.GetRawText()) > EventValidation.MaxEventBytes)
                            { await TooLarge(context, EventValidation.MaxEventBytes); return; }
                    buffer.Position = 0;
                }
                var original = context.Request.Body;
                context.Request.Body = buffer;
                try { await next(context); }
                finally { context.Request.Body = original; }
            }
            else await next(context);
        }
        catch (EventQueueFullException)
        {
            context.Response.Headers.RetryAfter = "1";
            await Results.Problem(statusCode: 503, title: "The event queue is full. Retry later.").ExecuteAsync(context);
        }
        catch (BadHttpRequestException exception)
        {
            await Results.Problem(statusCode: exception.StatusCode, title: "Invalid request body or parameters.")
                .ExecuteAsync(context);
        }
        catch (JsonException)
        {
            await Results.Problem(statusCode: 400, title: "Invalid JSON body.").ExecuteAsync(context);
        }
        catch (EventConflictException conflict)
        {
            await Results.Problem(statusCode: 409, title: "Event ID was already used for a different normalized payload.",
                extensions: new Dictionary<string, object?> { ["eventId"] = conflict.EventId }).ExecuteAsync(context);
        }
        catch (Exception error) when (error is NpgsqlException or TimeoutException or StorageQuotaException or StorageProfileException)
        {
            logger.LogWarning("Storage rejected request with {ErrorType}; trace {TraceId}", error.GetType().Name, context.TraceIdentifier);
            context.Response.Headers.RetryAfter = "1";
            await Results.Problem(statusCode: 503, title: error is StorageQuotaException ? "Storage quota reached. Run retention or increase capacity."
                : error is StorageProfileException ? "Storage profile changed; restart with matching configuration." : "Persistence is unavailable. Retry with the same event IDs.").ExecuteAsync(context);
        }
        finally
        {
            // Deliberately omit credentials, payloads, user IDs and raw query strings.
            logger.LogInformation("HTTP {Method} endpoint {Endpoint} returned {StatusCode} in {ElapsedMs} ms; trace {TraceId}",
                context.Request.Method, context.GetEndpoint()?.DisplayName, context.Response.StatusCode,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds, context.TraceIdentifier);
        }
    }

    private static Task TooLarge(HttpContext context, int bytes) => Results.Problem(statusCode: 413,
        title: $"Request or event exceeds the {bytes} byte limit.").ExecuteAsync(context);
}
