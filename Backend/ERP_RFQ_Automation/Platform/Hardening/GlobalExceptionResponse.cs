using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace ERP_RFQ_Automation.Platform.Hardening;

/// <summary>
/// The body the global exception handler answers with: the same generic sentence as before (no
/// exception detail ever leaves the process — DATA-12, SEC-16) PLUS the request's correlation id,
/// so a user who reports "an unexpected error occurred" can quote the one value that finds the
/// server-side stack trace. The id is also echoed on the response header, because the handler
/// runs on a cleared response and the middleware's own echo does not survive that.
/// </summary>
public static class GlobalExceptionResponse
{
    public const string Message = "An unexpected error occurred.";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static Task WriteAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var correlationId = TenantLoggingMiddleware.ResolveCorrelationId(context);
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/json";
        context.Response.Headers[TenantLoggingMiddleware.CorrelationHeader] = correlationId;
        return context.Response.WriteAsync(
            JsonSerializer.Serialize(new { error = Message, correlationId }, Json),
            context.RequestAborted);
    }
}
