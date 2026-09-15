using System.Diagnostics;
using System.Text.Json;
using Arkimentum.AppMonitor.Api.Contracts;
using Arkimentum.AppMonitor.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Arkimentum.AppMonitor.Api.Functions;

/// <summary>
/// The only place that touches HttpRequest/HttpResponse. It turns an <see cref="ApiOutcome{T}"/> into an
/// <see cref="IActionResult"/>, serialising with <see cref="CloudJson.Options"/> so the wire format is exactly what
/// Core's CloudClient expects, and rendering every failure as an <see cref="ApiError"/>.
/// </summary>
internal static class HttpHelpers
{
    public const string JsonContentType = "application/json; charset=utf-8";

    public static string TraceId(this HttpRequest request) =>
        Activity.Current?.Id ?? request.HttpContext.TraceIdentifier;

    public static string? Authorization(this HttpRequest request) =>
        request.Headers.TryGetValue(HeaderNames.Authorization, out var values) ? values.ToString() : null;

    public static string? IfNoneMatch(this HttpRequest request) =>
        request.Headers.TryGetValue(HeaderNames.IfNoneMatch, out var values) ? values.ToString() : null;

    public static string? IfMatch(this HttpRequest request) =>
        request.Headers.TryGetValue(HeaderNames.IfMatch, out var values) ? values.ToString() : null;

    /// <summary>The caller's address, preferring the first hop of X-Forwarded-For that the Functions front end sets.</summary>
    public static string ClientIp(this HttpRequest request)
    {
        if (request.Headers.TryGetValue("X-Forwarded-For", out StringValues forwarded) && forwarded.Count > 0)
        {
            var first = forwarded.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            if (!string.IsNullOrEmpty(first))
            {
                // Azure appends the source port: "13.37.1.2:51234".
                var colon = first.LastIndexOf(':');
                if (colon > 0 && first.Count(c => c == ':') == 1) first = first[..colon];
                return first;
            }
        }
        return request.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    public static string? Query(this HttpRequest request, string name) =>
        request.Query.TryGetValue(name, out var values) && values.Count > 0 ? values[0] : null;

    public static int QueryInt(this HttpRequest request, string name, int fallback) =>
        int.TryParse(request.Query(name), out var value) ? value : fallback;

    public static bool QueryBool(this HttpRequest request, string name)
    {
        var raw = request.Query(name);
        if (string.IsNullOrWhiteSpace(raw)) return false;
        return bool.TryParse(raw, out var b) ? b : raw is "1" or "yes" or "on";
    }

    public static DateTimeOffset? QueryDate(this HttpRequest request, string name) =>
        DateTimeOffset.TryParse(request.Query(name), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var value)
            ? value
            : null;

    public static async Task<string> ReadBodyAsync(this HttpRequest request, CancellationToken ct)
    {
        using var reader = new StreamReader(request.Body, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        return await reader.ReadToEndAsync(ct);
    }

    /// <summary>Deserialises the body; returns default and sets <paramref name="raw"/> when the JSON is malformed.</summary>
    public static bool TryDeserialize<T>(string raw, out T? value, out string? problem)
    {
        value = default;
        problem = null;
        if (string.IsNullOrWhiteSpace(raw)) { problem = "A JSON body is required."; return false; }
        try
        {
            value = JsonSerializer.Deserialize<T>(raw, CloudJson.Options);
            if (value is null) { problem = "A JSON body is required."; return false; }
            return true;
        }
        catch (JsonException ex)
        {
            problem = $"The JSON body could not be read: {ex.Message}";
            return false;
        }
    }

    public static IActionResult ToResult<T>(this ApiOutcome<T> outcome, HttpRequest request)
    {
        var response = request.HttpContext.Response;
        if (!string.IsNullOrEmpty(outcome.ETag)) response.Headers.ETag = $"\"{outcome.ETag}\"";
        if (outcome.RetryAfterSeconds is { } retryAfter) response.Headers.RetryAfter = retryAfter.ToString();

        if (outcome.Status == StatusCodes.Status304NotModified) return new StatusCodeResult(StatusCodes.Status304NotModified);
        if (outcome.Status == StatusCodes.Status204NoContent) return new NoContentResult();

        if (outcome.Error is { } error)
        {
            error.TraceId ??= request.TraceId();
            return Json(error, outcome.Status);
        }

        return outcome.Value is null ? new StatusCodeResult(outcome.Status) : Json(outcome.Value, outcome.Status);
    }

    public static IActionResult Error(this HttpRequest request, int status, string code, string message) =>
        Json(new ApiError { Code = code, Message = message, TraceId = request.TraceId() }, status);

    public static IActionResult BadRequest(this HttpRequest request, string message) =>
        request.Error(StatusCodes.Status400BadRequest, ErrorCodes.BadRequest, message);

    public static IActionResult Unauthorized(this HttpRequest request, string message, string scheme)
    {
        request.HttpContext.Response.Headers.WWWAuthenticate = scheme;
        return request.Error(StatusCodes.Status401Unauthorized, ErrorCodes.Unauthorized, message);
    }

    private static IActionResult Json(object value, int status) => new ContentResult
    {
        Content = JsonSerializer.Serialize(value, value.GetType(), CloudJson.Options),
        ContentType = JsonContentType,
        StatusCode = status,
    };
}
