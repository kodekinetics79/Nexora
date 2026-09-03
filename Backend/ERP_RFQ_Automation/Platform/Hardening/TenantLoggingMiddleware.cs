using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace ERP_RFQ_Automation.Platform.Hardening;

/// <summary>
/// Pushes tenant id (<c>businessUnitId</c> claim), a request/correlation id, and
/// the request path into the logging scope so every log line produced while
/// handling a request carries tenant + correlation context. Provider-agnostic:
/// the scope is consumed by any <see cref="ILogger"/> provider that renders
/// scopes (the default console logger does when
/// <c>Logging:Console:IncludeScopes</c> is enabled; structured providers capture
/// the key/value pairs directly).
///
/// The correlation id is taken from an inbound <c>X-Correlation-ID</c> header when
/// present (so a caller/gateway can propagate one), otherwise it falls back to
/// the framework's <see cref="HttpContext.TraceIdentifier"/>. It is echoed back
/// on the response so clients can correlate too.
/// </summary>
public sealed class TenantLoggingMiddleware
{
    public const string CorrelationHeader = "X-Correlation-ID";

    private readonly RequestDelegate _next;
    private readonly ILogger<TenantLoggingMiddleware> _logger;

    public TenantLoggingMiddleware(RequestDelegate next, ILogger<TenantLoggingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = ResolveCorrelationId(context);
        var tenantId = context.User?.FindFirst("businessUnitId")?.Value;

        // Echo the correlation id back before the response starts.
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[CorrelationHeader] = correlationId;
            return Task.CompletedTask;
        });

        var scope = new Dictionary<string, object?>
        {
            ["CorrelationId"] = correlationId,
            ["TenantId"] = string.IsNullOrWhiteSpace(tenantId) ? null : tenantId,
            ["RequestPath"] = context.Request.Path.Value
        };

        using (_logger.BeginScope(scope))
        {
            await _next(context);
        }
    }

    /// <summary>The inbound <c>X-Correlation-ID</c> when present, else the framework's trace
    /// identifier. Public so the global exception handler names the same id the log lines carry.</summary>
    public static string ResolveCorrelationId(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue(CorrelationHeader, out var header))
        {
            var value = header.ToString();
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return context.TraceIdentifier;
    }
}
