using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using OokiGrader.Application.Identifiers;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.Host.Middleware;

public sealed class IdempotencyMiddleware(
    RequestDelegate next,
    IdempotencyLockProvider locks,
    ILogger<IdempotencyMiddleware> logger)
{
    private const int MaximumStoredResponseBytes = 2 * 1024 * 1024;
    private static readonly string[] PreservedHeaders =
        ["ETag", "Location", "Cache-Control"];
    private static readonly Action<ILogger, string, Exception?>
        LogPersistenceFailure = LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(1_201, "IdempotencyPersistenceFailure"),
            "Could not persist idempotency response for {Route}.");

    public async Task InvokeAsync(
        HttpContext context,
        OokiGraderDbContext db,
        TimeProvider timeProvider)
    {
        if (!IsEligible(context))
        {
            await next(context);
            return;
        }

        var header = context.Request.Headers["Idempotency-Key"];
        if (header.Count == 0)
        {
            if (RequiresIdempotency(context))
            {
                await WriteProblemAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "IDEMPOTENCY_KEY_REQUIRED",
                    "この操作にはIdempotency-Keyが必要です。");
                return;
            }

            await next(context);
            return;
        }

        var idempotencyKey = header.Count == 1
            ? header[0]?.Trim()
            : null;
        if (!IsValidKey(idempotencyKey))
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status400BadRequest,
                "IDEMPOTENCY_KEY_INVALID",
                "Idempotency-KeyにはUUIDまたはULIDを指定してください。");
            return;
        }

        var actorKey = context.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var route = GetRoute(context);
        var requestHash = await ComputeRequestHashAsync(
            context.Request,
            context.RequestAborted);
        var lockKey = $"{actorKey}\n{route}\n{idempotencyKey}";
        await using var lease = await locks.AcquireAsync(
            lockKey,
            context.RequestAborted);

        var now = timeProvider.GetUtcNow();
        await db.IdempotencyRecords
            .Where(record => record.ExpiresAt <= now)
            .ExecuteDeleteAsync(context.RequestAborted);
        var existing = await db.IdempotencyRecords
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.ActorKey == actorKey
                    && record.Route == route
                    && record.IdempotencyKey == idempotencyKey,
                context.RequestAborted);
        if (existing is not null)
        {
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(existing.CanonicalRequestHash),
                    Encoding.ASCII.GetBytes(requestHash)))
            {
                await WriteProblemAsync(
                    context,
                    StatusCodes.Status409Conflict,
                    "IDEMPOTENCY_KEY_REUSED",
                    "同じIdempotency-Keyが異なるリクエストに使用されました。");
                return;
            }

            await ReplayAsync(context, existing);
            return;
        }

        var originalBody = context.Response.Body;
        await using var capture = new MemoryStream();
        context.Response.Body = capture;
        try
        {
            await next(context);
        }
        catch
        {
            context.Response.Body = originalBody;
            throw;
        }

        var responseBytes = capture.ToArray();
        context.Response.Body = originalBody;
        if (ShouldStore(context.Response, responseBytes.Length))
        {
            db.IdempotencyRecords.Add(new IdempotencyRecordEntity
            {
                Id = UlidId.New(now),
                ActorKey = actorKey,
                Route = route,
                IdempotencyKey = idempotencyKey!,
                CanonicalRequestHash = requestHash,
                ResponseStatusCode = context.Response.StatusCode,
                ResponseContentType = context.Response.ContentType,
                ResponseHeadersJson = SerializeHeaders(context.Response),
                ResponseBodyJson = responseBytes.Length == 0
                    ? null
                    : Encoding.UTF8.GetString(responseBytes),
                CreatedAt = now,
                ExpiresAt = now.AddHours(24),
            });
            try
            {
                await db.SaveChangesAsync(context.RequestAborted);
            }
            catch (DbUpdateException exception)
            {
                LogPersistenceFailure(logger, route, exception);
            }
        }

        context.Response.ContentLength = responseBytes.Length;
        await originalBody.WriteAsync(responseBytes, context.RequestAborted);
    }

    private static bool IsEligible(HttpContext context) =>
        context.User.Identity?.IsAuthenticated == true
        && context.User.FindFirstValue(ClaimTypes.NameIdentifier) is not null
        && context.Request.Path.StartsWithSegments("/api/v1")
        && (HttpMethods.IsPost(context.Request.Method)
            || HttpMethods.IsPut(context.Request.Method)
            || HttpMethods.IsPatch(context.Request.Method)
            || HttpMethods.IsDelete(context.Request.Method))
        && !context.Request.Path.StartsWithSegments("/api/v1/auth");

    private static bool RequiresIdempotency(HttpContext context) =>
        context.GetEndpoint()?.Metadata
            .GetMetadata<AllowNonIdempotentMutationMetadata>() is null;

    private static bool IsValidKey(string? value) =>
        value is { Length: > 0 and <= 64 }
        && (Guid.TryParse(value, out _)
            || UlidId.IsCanonical(value.ToUpperInvariant()));

    private static string GetRoute(HttpContext context)
    {
        var pattern = (context.GetEndpoint() as RouteEndpoint)?
            .RoutePattern.RawText;
        return $"{context.Request.Method}:{pattern ?? context.Request.Path.Value}";
    }

    private static async Task<string> ComputeRequestHashAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(
            $"{request.Method}\n{request.Path.Value}\n{request.QueryString.Value}" +
            $"\n{request.Headers.IfMatch.FirstOrDefault()}\n"));
        if (request.ContentLength is null or 0)
        {
            return Convert.ToHexString(hash.GetHashAndReset())
                .ToLowerInvariant();
        }

        request.EnableBuffering();
        if (request.ContentType?.StartsWith(
                "application/json",
                StringComparison.OrdinalIgnoreCase) == true
            && request.ContentLength <= 2 * 1024 * 1024)
        {
            using var body = new MemoryStream();
            await request.Body.CopyToAsync(body, cancellationToken);
            var bytes = body.ToArray();
            try
            {
                using var document = JsonDocument.Parse(bytes);
                using var canonical = new MemoryStream();
                using (var writer = new Utf8JsonWriter(canonical))
                {
                    WriteCanonicalJson(writer, document.RootElement);
                }

                hash.AppendData(canonical.ToArray());
            }
            catch (JsonException)
            {
                hash.AppendData(bytes);
            }
        }
        else
        {
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await request.Body.ReadAsync(
                       buffer,
                       cancellationToken)) > 0)
            {
                hash.AppendData(buffer.AsSpan(0, read));
            }
        }

        request.Body.Position = 0;
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void WriteCanonicalJson(
        Utf8JsonWriter writer,
        JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject()
                             .OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonicalJson(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText());
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                writer.WriteRawValue(element.GetRawText());
                break;
        }
    }

    private static bool ShouldStore(HttpResponse response, int bodyLength) =>
        response.StatusCode < StatusCodes.Status500InternalServerError
        && bodyLength <= MaximumStoredResponseBytes
        && (bodyLength == 0
            || response.ContentType?.Contains(
                "json",
                StringComparison.OrdinalIgnoreCase) == true);

    private static string? SerializeHeaders(HttpResponse response)
    {
        var values = PreservedHeaders
            .Where(name => response.Headers.ContainsKey(name))
            .ToDictionary(
                name => name,
                name => response.Headers[name].ToArray(),
                StringComparer.OrdinalIgnoreCase);
        return values.Count == 0 ? null : JsonSerializer.Serialize(values);
    }

    private static async Task ReplayAsync(
        HttpContext context,
        IdempotencyRecordEntity record)
    {
        context.Response.StatusCode = record.ResponseStatusCode;
        context.Response.ContentType = record.ResponseContentType;
        context.Response.Headers["Idempotency-Replayed"] = "true";
        if (!string.IsNullOrWhiteSpace(record.ResponseHeadersJson))
        {
            var headers = JsonSerializer.Deserialize<Dictionary<string, string[]>>(
                record.ResponseHeadersJson);
            if (headers is not null)
            {
                foreach (var (name, values) in headers)
                {
                    context.Response.Headers[name] = values;
                }
            }
        }

        if (record.ResponseBodyJson is not null)
        {
            await context.Response.WriteAsync(
                record.ResponseBodyJson,
                context.RequestAborted);
        }
    }

    private static Task WriteProblemAsync(
        HttpContext context,
        int status,
        string code,
        string detail)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        return context.Response.WriteAsJsonAsync(
            new
            {
                type =
                    $"https://ooki-grader.local/problems/{code.ToLowerInvariant().Replace('_', '-')}",
                title = "リクエストを処理できません",
                status,
                code,
                detail,
                instance = context.Request.Path.Value,
                correlationId = context.TraceIdentifier,
            },
            context.RequestAborted);
    }
}
