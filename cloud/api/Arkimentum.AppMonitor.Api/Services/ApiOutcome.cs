using System.Net;
using Arkimentum.AppMonitor.Api.Contracts;

namespace Arkimentum.AppMonitor.Api.Services;

/// <summary>Stable machine-readable error codes; the agent and the console branch on these, never on the message.</summary>
public static class ErrorCodes
{
    public const string BadRequest = "bad_request";
    public const string Unauthorized = "unauthorized";
    public const string Forbidden = "forbidden";
    public const string NotFound = "not_found";
    public const string Conflict = "conflict";
    public const string ValidationFailed = "validation_failed";
    public const string RateLimited = "rate_limited";
    public const string InvalidEnrollmentKey = "invalid_enrollment_key";
    public const string OrganizationInactive = "organization_inactive";
    public const string Internal = "internal_error";
}

/// <summary>
/// The result of a handler: a status code plus either a payload or an <see cref="ApiError"/>. Handlers never touch
/// HttpRequest/HttpResponse, which is what makes them straightforward to unit-test.
/// </summary>
public sealed record ApiOutcome<T>(int Status, T? Value, ApiError? Error = null, string? ETag = null, int? RetryAfterSeconds = null)
{
    public bool IsSuccess => Status is >= 200 and < 300;

    public static ApiOutcome<T> Ok(T value, string? etag = null) => new((int)HttpStatusCode.OK, value, null, etag);
    public static ApiOutcome<T> Created(T value) => new((int)HttpStatusCode.Created, value);
    public static ApiOutcome<T> NoContent() => new((int)HttpStatusCode.NoContent, default);
    public static ApiOutcome<T> NotModified(string? etag) => new((int)HttpStatusCode.NotModified, default, null, etag);

    public static ApiOutcome<T> Failure(HttpStatusCode status, string code, string message, int? retryAfterSeconds = null) =>
        new((int)status, default, new ApiError { Code = code, Message = message }, null, retryAfterSeconds);

    public static ApiOutcome<T> BadRequest(string message) => Failure(HttpStatusCode.BadRequest, ErrorCodes.BadRequest, message);
    public static ApiOutcome<T> Unauthorized(string message = "Authentication is required.") => Failure(HttpStatusCode.Unauthorized, ErrorCodes.Unauthorized, message);
    public static ApiOutcome<T> Forbidden(string message = "You may not manage this organization.") => Failure(HttpStatusCode.Forbidden, ErrorCodes.Forbidden, message);
    public static ApiOutcome<T> NotFound(string message = "Not found.") => Failure(HttpStatusCode.NotFound, ErrorCodes.NotFound, message);
    public static ApiOutcome<T> Conflict(string message) => Failure(HttpStatusCode.Conflict, ErrorCodes.Conflict, message);
    public static ApiOutcome<T> Invalid(string message) => Failure(HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed, message);
}
