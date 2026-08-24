using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ERP_RFQ_Automation.HealthChecks;

/// <summary>
/// Writes a readiness report an operator can act on WITHOUT log access.
///
/// <para><b>Why this exists.</b> <c>/ready</c> used the framework's default writer, which emits
/// the single word <c>Unhealthy</c> and nothing else. On 2026-08-24 the deployment had been
/// 503-ing for the life of the process and the two failing check names —
/// <c>email-poll-channel</c> and <c>background-workers</c> — had to be recovered by grepping
/// Render's log stream. A probe whose whole job is to say something is wrong should not require
/// a second system to say WHAT.</para>
///
/// <para><b>What it is allowed to say.</b> Check name, status, duration and the check's own
/// description. Deliberately NOT included:</para>
/// <list type="bullet">
///   <item><description><see cref="HealthReportEntry.Exception"/> — exception messages and stack
///   traces are where connection strings, hostnames and credentials actually leak. Npgsql's
///   messages in particular carry host and database. The exception stays in the logs, where
///   authentication already gates it.</description></item>
///   <item><description><see cref="HealthReportEntry.Data"/> — a free-form bag each check fills
///   in for itself. Nothing can promise what a future check puts there, so it is never
///   serialised to an anonymous caller.</description></item>
/// </list>
///
/// <para><b>And the descriptions are still redacted.</b> <c>/ready</c> is
/// <c>AllowAnonymous</c> (the app sets an authorization fallback policy, so probes must opt out
/// of it) and on Render it is reachable from the internet. A description naming a customer's
/// mailbox is tenant data. <see cref="Redact"/> masks the local part of every address, any
/// userinfo in a URI, and any <c>key=value</c> pair whose key looks like a secret — which leaves
/// the diagnosis intact, because the actionable identifier is the mailbox ID and the domain, not
/// the local part.</para>
/// </summary>
public static class HealthReportResponseWriter
{
    /// <summary>Long enough for the per-mailbox channel description on a multi-mailbox tenant,
    /// short enough that a check cannot turn the probe into a log sink.</summary>
    internal const int MaxDescriptionLength = 2000;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private static readonly Regex UriUserInfo = new(
        @"(?<scheme>[A-Za-z][A-Za-z0-9+.-]*://)(?<user>[^/\s:@]+)(?::(?<pass>[^/\s@]*))?@",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SecretAssignment = new(
        @"(?i)\b(password|pwd|passwd|secret|token|api[_-]?key|apikey|access[_-]?key|credential|"
        + @"connection[_-]?string|user\s?id|uid|username|user)\b\s*[=:]\s*(""[^""]*""|'[^']*'|[^\s;,)]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex EmailAddress = new(
        @"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The <c>ResponseWriter</c> for <c>MapHealthChecks</c>. Entries are ordered by name so two
    /// readings of the same probe diff cleanly.
    /// </summary>
    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(report);

        context.Response.ContentType = "application/json; charset=utf-8";

        var payload = new
        {
            status = report.Status.ToString(),
            totalDurationMs = Round(report.TotalDuration),
            // The failing names first, hoisted out of the list, because the first question an
            // operator asks a red probe is "which one".
            failing = report.Entries
                .Where(e => e.Value.Status != HealthStatus.Healthy)
                .Select(e => e.Key)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray(),
            checks = report.Entries
                .OrderBy(e => e.Key, StringComparer.Ordinal)
                .Select(e => new
                {
                    name = e.Key,
                    status = e.Value.Status.ToString(),
                    durationMs = Round(e.Value.Duration),
                    description = Redact(e.Value.Description)
                })
                .ToArray()
        };

        return context.Response.WriteAsync(JsonSerializer.Serialize(payload, Json));
    }

    private static double Round(TimeSpan duration)
        => Math.Round(duration.TotalMilliseconds, 1, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Strips the things a public probe must never carry, in the order that matters: URI userinfo
    /// first (it contains an <c>@</c> and would otherwise be half-eaten by the address rule),
    /// then <c>key=value</c> secrets, then bare addresses.
    /// </summary>
    internal static string? Redact(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return description;

        var text = UriUserInfo.Replace(description, m => m.Groups["scheme"].Value + "***@");
        text = SecretAssignment.Replace(text, m => m.Groups[1].Value + "=***");
        // The local part identifies a person; the domain identifies the tenant's mail provider
        // and is what makes the message diagnosable. Keep the second, drop the first.
        text = EmailAddress.Replace(text, m =>
        {
            var at = m.Value.IndexOf('@');
            return "***" + m.Value[at..];
        });

        return text.Length <= MaxDescriptionLength
            ? text
            : string.Concat(text.AsSpan(0, MaxDescriptionLength), "… (truncated)");
    }
}
