using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ERP_RFQ_Automation.Notifications;
using Microsoft.Extensions.Logging;

namespace ERP_RFQ_Automation.Sla;

/// <summary>One row of a stale-quote digest email.</summary>
public sealed class StaleQuoteDigestLine
{
    public string QuoteNo { get; set; } = string.Empty;
    public string? CustomerName { get; set; }
    public DateTime? SentOn { get; set; }
    public int DaysWaiting { get; set; }
}

/// <summary>
/// SLA-owned outbound email surface. Deliberately independent of
/// <c>INotificationService</c>/<c>NotificationService</c> (owned by another agent
/// this wave): it talks to the Notifications module's <see cref="IEmailSender"/>
/// transport directly and applies the same resilient never-throw pattern — an
/// email failure is logged and swallowed, never breaking the sweep or a business
/// transaction.
///
/// NOTE on templating: the module's IEmailTemplateRenderer is name-locked to the
/// static EmailTemplates dictionary; registering SLA templates there would mean
/// editing the Notifications module. The two SLA templates (deadline-alert,
/// stale-quotes-digest) therefore live here, rendered with the same
/// {{token}} substitution semantics (unknown tokens stripped).
/// </summary>
public interface ISlaNotifications
{
    /// <summary>Deadline alert for a lead (warn / critical / overdue). Never throws.</summary>
    Task<bool> SendDeadlineAlertAsync(
        string toEmail, string? toName, string level, string entityLabel,
        string headline, string detail, long businessUnitId, CancellationToken ct = default);

    /// <summary>Daily per-owner digest of stale quotes. Never throws.</summary>
    Task<bool> SendStaleQuotesDigestAsync(
        string toEmail, string? toName, IReadOnlyList<StaleQuoteDigestLine> lines,
        long businessUnitId, CancellationToken ct = default);
}

public sealed class SlaNotifications : ISlaNotifications
{
    private static readonly Regex TokenRegex = new(@"\{\{\s*([A-Za-z0-9_]+)\s*\}\}", RegexOptions.Compiled);

    private readonly IEmailSender _emailSender;
    private readonly ILogger<SlaNotifications> _logger;

    public SlaNotifications(IEmailSender emailSender, ILogger<SlaNotifications> logger)
    {
        _emailSender = emailSender;
        _logger = logger;
    }

    // ---------------- templates ----------------

    private const string Shell = """
<!DOCTYPE html>
<html lang="en">
<head><meta charset="utf-8" /><meta name="viewport" content="width=device-width, initial-scale=1.0" /><title>{{title}}</title></head>
<body style="margin:0; padding:0; background-color:#eef2f7;">
  <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background-color:#eef2f7;">
    <tr><td align="center" style="padding:24px 12px;">
      <table role="presentation" width="600" cellpadding="0" cellspacing="0" style="width:600px; max-width:600px; background-color:#ffffff; border-radius:12px; overflow:hidden; font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;">
        <tr><td style="background-color:#0f172a; padding:20px 32px;">
          <span style="font-size:20px; font-weight:700; color:#ffffff;">Nexora</span>
          <span style="font-size:12px; color:#94a3b8; margin-left:8px;">Deadlines &amp; follow-ups</span>
        </td></tr>
        <tr><td style="padding:28px 32px 8px 32px; color:#0f172a; font-size:15px; line-height:1.55;">{{content}}</td></tr>
        <tr><td style="padding:20px 32px 28px 32px;">
          <hr style="border:none; border-top:1px solid #e2e8f0; margin:0 0 12px 0;" />
          <p style="margin:0; color:#64748b; font-size:12px; line-height:1.6;">This is an automated reminder from the Nexora platform.</p>
        </td></tr>
      </table>
    </td></tr>
  </table>
</body>
</html>
""";

    private const string DeadlineAlertContent = """
<div style="display:inline-block; padding:4px 12px; border-radius:999px; background-color:{{badgeBg}}; color:{{badgeFg}}; font-size:12px; font-weight:700; text-transform:uppercase; letter-spacing:0.5px; margin-bottom:14px;">{{levelLabel}}</div>
<h1 style="margin:0 0 12px 0; font-size:19px; color:#0f172a;">{{headline}}</h1>
<p style="margin:0 0 12px 0;">Hi {{toName}},</p>
<p style="margin:0 0 12px 0;">{{detail}}</p>
<p style="margin:0 0 4px 0; color:#64748b; font-size:14px;">Reference: <strong style="color:#0f172a;">{{entityLabel}}</strong></p>
""";

    private const string DeadlineAlertText = """
{{levelLabel}}: {{headline}}

Hi {{toName}},

{{detail}}

Reference: {{entityLabel}}

— Nexora (automated reminder)
""";

    private const string DigestIntro = """
<h1 style="margin:0 0 12px 0; font-size:19px; color:#0f172a;">Quotes waiting on a customer reply</h1>
<p style="margin:0 0 12px 0;">Hi {{toName}},</p>
<p style="margin:0 0 16px 0;">These quotes were sent but haven't had a reply. A quick follow-up call or email usually gets things moving — or record the outcome if you already know it.</p>
""";

    // ---------------- API ----------------

    public async Task<bool> SendDeadlineAlertAsync(
        string toEmail, string? toName, string level, string entityLabel,
        string headline, string detail, long businessUnitId, CancellationToken ct = default)
    {
        var (badgeBg, badgeFg, levelLabel) = level switch
        {
            "overdue" => ("#fee2e2", "#b91c1c", "Overdue"),
            "critical" => ("#ffedd5", "#c2410c", "Critical"),
            "escalated" => ("#fee2e2", "#b91c1c", "Needs attention"),
            _ => ("#fef9c3", "#a16207", "Heads up")
        };

        var model = new Dictionary<string, string?>
        {
            ["title"] = headline,
            ["toName"] = string.IsNullOrWhiteSpace(toName) ? "there" : toName,
            ["levelLabel"] = levelLabel,
            ["badgeBg"] = badgeBg,
            ["badgeFg"] = badgeFg,
            ["headline"] = headline,
            ["detail"] = detail,
            ["entityLabel"] = entityLabel
        };

        var subject = Substitute("{{levelLabel}}: {{headline}}", model);
        var html = Substitute(Shell.Replace("{{content}}", DeadlineAlertContent), model);
        var text = Substitute(DeadlineAlertText, model);

        return await SendAsync("deadline-alert", toEmail, toName, subject, html, text, businessUnitId, ct);
    }

    public async Task<bool> SendStaleQuotesDigestAsync(
        string toEmail, string? toName, IReadOnlyList<StaleQuoteDigestLine> lines,
        long businessUnitId, CancellationToken ct = default)
    {
        if (lines is not { Count: > 0 }) return false;

        var model = new Dictionary<string, string?>
        {
            ["title"] = "Quotes waiting on a customer reply",
            ["toName"] = string.IsNullOrWhiteSpace(toName) ? "there" : toName
        };

        var rowsHtml = string.Join("", lines.Select(l => $"""
<tr>
  <td style="padding:8px 0; border-bottom:1px solid #f1f5f9; font-size:14px; font-weight:700; color:#0f172a;">{System.Net.WebUtility.HtmlEncode(l.QuoteNo)}</td>
  <td style="padding:8px 0; border-bottom:1px solid #f1f5f9; font-size:14px; color:#334155;">{System.Net.WebUtility.HtmlEncode(l.CustomerName ?? "—")}</td>
  <td style="padding:8px 0; border-bottom:1px solid #f1f5f9; font-size:14px; color:#334155;">{(l.SentOn?.ToString("dd MMM yyyy") ?? "—")}</td>
  <td style="padding:8px 0; border-bottom:1px solid #f1f5f9; font-size:14px; font-weight:700; color:#b45309;">{l.DaysWaiting} day{(l.DaysWaiting == 1 ? "" : "s")}</td>
</tr>
"""));

        var content = Substitute(DigestIntro, model) + $"""
<table role="presentation" cellpadding="0" cellspacing="0" width="100%" style="border-collapse:collapse; margin:4px 0 8px 0;">
  <tr>
    <td style="padding:6px 0; font-size:12px; color:#64748b; text-transform:uppercase; font-weight:700;">Quote</td>
    <td style="padding:6px 0; font-size:12px; color:#64748b; text-transform:uppercase; font-weight:700;">Customer</td>
    <td style="padding:6px 0; font-size:12px; color:#64748b; text-transform:uppercase; font-weight:700;">Sent</td>
    <td style="padding:6px 0; font-size:12px; color:#64748b; text-transform:uppercase; font-weight:700;">Waiting</td>
  </tr>
  {rowsHtml}
</table>
""";

        var html = Substitute(Shell.Replace("{{content}}", content), model);

        model["digestLines"] = string.Join("\n", lines.Select(l =>
            $"  - {l.QuoteNo} ({l.CustomerName ?? "unknown customer"}), sent {(l.SentOn?.ToString("dd MMM yyyy") ?? "n/a")}, waiting {l.DaysWaiting} day(s)"));
        var text = Substitute("""
Quotes waiting on a customer reply

Hi {{toName}},

These quotes were sent but haven't had a reply yet:

{{digestLines}}

A quick follow-up usually gets things moving — or record the outcome if you already know it.

— Nexora (automated reminder)
""", model);

        var subject = $"{lines.Count} quote{(lines.Count == 1 ? "" : "s")} waiting on a customer reply";
        return await SendAsync("stale-quotes-digest", toEmail, toName, subject, html, text, businessUnitId, ct);
    }

    // ---------------- internals ----------------

    private async Task<bool> SendAsync(
        string template, string toEmail, string? toName, string subject,
        string html, string text, long businessUnitId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(toEmail))
        {
            _logger.LogWarning("[SlaNotifications] Skipping '{Template}' — no recipient email. BU={Bu}", template, businessUnitId);
            return false;
        }

        try
        {
            var message = new EmailMessage
            {
                Subject = subject,
                HtmlBody = html,
                TextBody = text,
                BusinessUnitId = businessUnitId.ToString()
            };
            message.AddTo(toEmail, toName);

            await _emailSender.SendAsync(message, ct).ConfigureAwait(false);
            _logger.LogInformation("[SlaNotifications] Dispatched '{Template}' to {To}. BU={Bu}", template, toEmail, businessUnitId);
            return true;
        }
        catch (Exception ex)
        {
            // Never rethrow: an SLA email failure must not break the sweep or a business flow.
            _logger.LogError(ex, "[SlaNotifications] Failed to send '{Template}' to {To}. BU={Bu}. Suppressed.", template, toEmail, businessUnitId);
            return false;
        }
    }

    private static string Substitute(string template, IDictionary<string, string?> model) =>
        TokenRegex.Replace(template, m => model.TryGetValue(m.Groups[1].Value, out var v) ? v ?? string.Empty : string.Empty);
}
