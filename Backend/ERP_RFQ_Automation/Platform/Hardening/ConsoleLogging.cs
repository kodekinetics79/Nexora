using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace ERP_RFQ_Automation.Platform.Hardening;

/// <summary>
/// The console logger configuration for every host this process builds.
///
/// <para><b>Why this exists.</b> The host used the default console provider with the simple
/// formatter, which does not render scopes unless <c>Logging:Console:IncludeScopes</c> is set —
/// and nothing set it. So the tenant id and correlation id that
/// <see cref="TenantLoggingMiddleware"/> pushes into the scope for every request never reached a
/// single log line, and a production incident could not be traced from the response's
/// <c>X-Correlation-ID</c> back to the lines it produced.</para>
///
/// <para>Outside Development the formatter is JSON with scopes included, so each line carries
/// <c>CorrelationId</c>, <c>TenantId</c> and <c>RequestPath</c> as fields a log search can filter
/// on. In Development it stays the single-line simple formatter — with scopes — because a person
/// is reading it.</para>
/// </summary>
public static class ConsoleLogging
{
    public static ILoggingBuilder AddNexoraConsole(this ILoggingBuilder logging, bool isDevelopment)
    {
        ArgumentNullException.ThrowIfNull(logging);

        if (isDevelopment)
        {
            logging.AddSimpleConsole(options =>
            {
                options.IncludeScopes = true;
                options.SingleLine = true;
                options.TimestampFormat = "HH:mm:ss ";
            });
        }
        else
        {
            logging.AddJsonConsole(options =>
            {
                options.IncludeScopes = true;
                options.UseUtcTimestamp = true;
                options.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
            });
        }

        return logging;
    }
}
