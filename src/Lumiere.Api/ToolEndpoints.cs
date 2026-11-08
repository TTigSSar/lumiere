using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Lumiere.Api;

/// <summary>
/// Live-tool endpoints (ADR-014). Reads only <c>tool.arguments</c>, the top-level <c>phoneNumber</c>
/// (call metadata, ADR-014 / ADR-006 amendment) and the X-Tool-Secret header; the rest of the request
/// body (transcript, conversation, prompt) is never read, logged or stored.
/// </summary>
public static class ToolEndpoints
{
    public static void MapToolEndpoints(this WebApplication app)
    {
        app.MapPost("/tools/check-availability", (HttpContext http, BookingService booking) =>
            Handle(http, "check-availability", (a, _, ct) =>
                booking.CheckAvailabilityAsync(a.GetValueOrDefault("service"), a.GetValueOrDefault("date_hint"), a.GetValueOrDefault("part_of_day"), ct)));

        app.MapPost("/tools/book", (HttpContext http, BookingService booking) =>
            Handle(http, "book", (a, callerPhone, ct) =>
                booking.BookAsync(a.GetValueOrDefault("slot_id"), a.GetValueOrDefault("first_name"), a.GetValueOrDefault("last_name"), callerPhone, ct)));
    }

    private static async Task<IResult> Handle(HttpContext http, string endpoint,
        Func<IReadOnlyDictionary<string, string>, string?, CancellationToken, Task<ToolResult>> run)
    {
        var started = Stopwatch.GetTimestamp();
        var log = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Lumiere.Tools");
        var ct = http.RequestAborted;

        if (!Authorized(http))
        {
            log.LogWarning("tool {Endpoint} -> 401", endpoint);
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        var request = await ReadRequestAsync(http.Request, ct);
        if (request is not { } parsed)
        {
            log.LogWarning("tool {Endpoint} -> 400", endpoint);
            return Results.BadRequest(new { ok = false, error_code = "BAD_REQUEST" });
        }

        var (args, metadataPhone) = parsed;
        var (callerPhone, source) = Caller.Resolve(metadataPhone, args.GetValueOrDefault("caller_phone"));
        var result = await run(args, callerPhone, ct);
        log.LogInformation("tool {Endpoint} -> {Outcome} in {LatencyMs} ms, phone {Phone} source={PhoneSource}", endpoint, result.ErrorCode ?? "OK",
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, Caller.MaskPhone(callerPhone), source);
        return Results.Json(result);
    }

    private static bool Authorized(HttpContext http)
    {
        var expected = http.RequestServices.GetRequiredService<IConfiguration>()["TOOL_SHARED_SECRET"];
        var given = http.Request.Headers["X-Tool-Secret"].ToString();
        if (string.IsNullOrEmpty(expected) || given.Length == 0) return false;
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)), SHA256.HashData(Encoding.UTF8.GetBytes(given)));
    }

    /// <summary>
    /// Primitive values of <c>tool.arguments</c> as strings plus the top-level <c>phoneNumber</c> (string only,
    /// else null); null when the body is malformed. Nothing else in the body is read.
    /// </summary>
    private static async Task<(IReadOnlyDictionary<string, string> Args, string? MetadataPhone)?> ReadRequestAsync(HttpRequest request, CancellationToken ct)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("tool", out var tool) || tool.ValueKind != JsonValueKind.Object
                || !tool.TryGetProperty("arguments", out var arguments) || arguments.ValueKind != JsonValueKind.Object)
                return null;

            var result = new Dictionary<string, string>();
            foreach (var p in arguments.EnumerateObject())
            {
                switch (p.Value.ValueKind)
                {
                    case JsonValueKind.String: result[p.Name] = p.Value.GetString()!; break;
                    case JsonValueKind.Number: result[p.Name] = p.Value.GetRawText(); break;
                    case JsonValueKind.True or JsonValueKind.False: result[p.Name] = p.Value.GetBoolean() ? "true" : "false"; break;
                    case JsonValueKind.Null: break;
                    default: return null;
                }
            }
            var metadataPhone = doc.RootElement.TryGetProperty("phoneNumber", out var pn) && pn.ValueKind == JsonValueKind.String
                ? pn.GetString()
                : null;
            return (result, metadataPhone);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
