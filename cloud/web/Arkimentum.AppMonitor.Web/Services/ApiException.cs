using System.Net;

namespace Arkimentum.AppMonitor.Web.Services;

/// <summary>
/// A failed call to the admin API, built from the <see cref="Api.Contracts.ApiError"/> body the server returns.
///
/// <para>
/// 401 and 403 are told apart deliberately: 401 means the access token is missing or expired (sign in again), 403
/// means the signed-in account has no access to <em>this</em> organization - a different problem with a different
/// answer, and the one an administrator is most likely to hit after switching tenants.
/// </para>
/// </summary>
public sealed class ApiException : Exception
{
    public ApiException(HttpStatusCode status, string? code, string message, string? traceId = null) : base(message)
    {
        Status = status;
        Code = code;
        TraceId = traceId;
    }

    public HttpStatusCode Status { get; }

    /// <summary>The machine-readable <c>code</c> of the error body, e.g. <c>validation_failed</c>.</summary>
    public string? Code { get; }

    public string? TraceId { get; }

    public bool IsUnauthorized => Status == HttpStatusCode.Unauthorized;

    public bool IsForbidden => Status == HttpStatusCode.Forbidden;

    public bool IsConflict => Status == HttpStatusCode.Conflict;

    public bool IsNotFound => Status == HttpStatusCode.NotFound;

    /// <summary>The sentence a page shows in its error banner.</summary>
    public string Describe() => Status switch
    {
        HttpStatusCode.Unauthorized => "Your sign-in has expired. Sign in again to continue.",
        HttpStatusCode.Forbidden => "You have no access to this organization.",
        HttpStatusCode.NotFound => "That is not there (any more).",
        HttpStatusCode.Conflict => Message,
        _ => Message,
    };

    /// <summary>The sentence to show for any failure, API or not (a dropped connection, a CORS refusal, …).</summary>
    public static string Describe(Exception exception) => exception switch
    {
        ApiException api => api.Describe(),
        OperationCanceledException => "The request was cancelled.",
        HttpRequestException ex => $"The server could not be reached ({ex.Message}).",
        _ => $"{exception.GetType().Name}: {exception.Message}",
    };
}
