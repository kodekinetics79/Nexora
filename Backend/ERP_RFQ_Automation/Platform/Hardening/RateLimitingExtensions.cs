using System.Globalization;
using System.Threading.RateLimiting;
using ERP_RFQ_Automation.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ERP_RFQ_Automation.Platform.Hardening;

/// <summary>
/// Built-in ASP.NET Core rate limiting (<c>Microsoft.AspNetCore.RateLimiting</c> —
/// no extra package), configured to be tenant-fair: the global limiter partitions
/// by the authenticated tenant (<c>businessUnitId</c> claim) when present, else by
/// client IP, so one tenant can never starve another. Limits are config-driven
/// with generous safe fallbacks so a live pilot is not disrupted. A stricter named
/// <see cref="UploadPolicy"/> policy is provided for heavy endpoints such as
/// <c>POST /api/Extraction/upload</c> (opt in with
/// <c>[EnableRateLimiting("upload")]</c> — see HARDENING-WIRING.md).
/// </summary>
public static class RateLimitingExtensions
{
    /// <summary>Name of the stricter upload policy.</summary>
    public const string UploadPolicy = "upload";
    public const string SmtpPolicy = "smtp";

    public static IServiceCollection AddPlatformRateLimiting(
        this IServiceCollection services, IConfiguration configuration)
    {
        // SEC-H6: the request-rate limiter below is volume-based and credential-blind — it
        // cannot tell a legitimate burst from password guessing against one account. The
        // durable per-account login lockout is registered here alongside it because this is
        // the app's abuse-protection composition root and it already receives IConfiguration.
        // (A reviewer may prefer to hoist this to its own explicit line in Program.cs; the
        // registration is idempotent either way.)
        services.AddLoginThrottling(configuration);

        // Generous defaults so the limiter protects against abuse without tripping
        // legitimate pilot traffic. All overridable via config.
        var permitLimit = GetInt(configuration, "RateLimiting:PermitLimit", 600);
        var windowSeconds = GetInt(configuration, "RateLimiting:WindowSeconds", 60);
        var queueLimit = GetInt(configuration, "RateLimiting:QueueLimit", 0);

        var uploadPermitLimit = GetInt(configuration, "RateLimiting:Upload:PermitLimit", 30);
        var uploadWindowSeconds = GetInt(configuration, "RateLimiting:Upload:WindowSeconds", 60);
        var uploadQueueLimit = GetInt(configuration, "RateLimiting:Upload:QueueLimit", 0);
        var smtpPermitLimit = GetInt(configuration, "RateLimiting:Smtp:PermitLimit", 10);
        var smtpWindowSeconds = GetInt(configuration, "RateLimiting:Smtp:WindowSeconds", 60);

        var window = TimeSpan.FromSeconds(windowSeconds);
        var uploadWindow = TimeSpan.FromSeconds(uploadWindowSeconds);
        var smtpWindow = TimeSpan.FromSeconds(smtpWindowSeconds);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // Global limiter: applies to every request, partitioned per tenant/IP.
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: PartitionKey(httpContext),
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = permitLimit,
                        Window = window,
                        QueueLimit = queueLimit,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                    }));

            // Stricter named policy for heavy endpoints (uploads), also tenant/IP fair.
            options.AddPolicy(UploadPolicy, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: "upload:" + PartitionKey(httpContext),
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = uploadPermitLimit,
                        Window = uploadWindow,
                        QueueLimit = uploadQueueLimit,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                    }));

            options.AddPolicy(SmtpPolicy, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: "smtp:" + PartitionKey(httpContext),
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = smtpPermitLimit,
                        Window = smtpWindow,
                        QueueLimit = 0,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                    }));

            // 429 with Retry-After so well-behaved clients back off correctly.
            options.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;

                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)retryAfter.TotalSeconds).ToString(NumberFormatInfo.InvariantInfo);
                }

                context.HttpContext.Response.ContentType = "application/json";
                await context.HttpContext.Response.WriteAsync(
                    "{\"error\":\"Rate limit exceeded. Please retry later.\"}", cancellationToken);
            };
        });

        return services;
    }

    /// <summary>
    /// Tenant-first partition key: the authenticated <c>businessUnitId</c> claim
    /// when present, else the client IP. Guarantees per-tenant isolation and falls
    /// back to per-IP for anonymous/pre-auth traffic.
    /// </summary>
    private static string PartitionKey(HttpContext httpContext)
    {
        var businessUnitId = httpContext.User?.FindFirst("businessUnitId")?.Value;
        if (!string.IsNullOrWhiteSpace(businessUnitId))
            return "tenant:" + businessUnitId;

        var ip = httpContext.Connection.RemoteIpAddress?.ToString();
        return "ip:" + (string.IsNullOrWhiteSpace(ip) ? "unknown" : ip);
    }

    private static int GetInt(IConfiguration configuration, string key, int fallback)
    {
        var raw = configuration[key];
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : fallback;
    }
}
