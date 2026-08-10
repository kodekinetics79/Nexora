using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ERP_RFQ_Automation.Notifications;
using ERP_RFQ_Automation.Sla;
using Microsoft.Extensions.Logging;

namespace ERP_RFQ_Automation.Reporting;

public interface IReportDelivery
{
    /// <summary>
    /// Emails the rendered report and reports the outcome in three states, not two.
    ///
    /// <para>A bool could only ever say "nothing threw", which reads a dropped SMTP connection after
    /// the body was accepted as a failure — and a retried board report is a duplicate in a
    /// director's inbox that no ledger can take back. <see cref="SlaSendOutcome"/> separates
    /// "the provider demonstrably never had it" (retry) from "it may already be delivered"
    /// (never retry, but say so loudly).</para>
    /// </summary>
    Task<SlaSendResult> SendAsync(IReadOnlyList<string> recipients, ReportDocument document,
        RenderedReport rendered, long businessUnitId, CancellationToken ct = default);
}

/// <summary>
/// Delivers a scheduled report by email with the rendered file attached.
///
/// <para>Mirrors <c>Sla/SlaNotifications</c>: it injects <see cref="IEmailSender"/> directly rather
/// than going through the name-locked template renderer, and it never throws — a failed report must
/// not kill the sweep for the tenants after it. It answers with a <see cref="SlaSendResult"/> rather
/// than a bool for the reason that module documents: a bool can only say "nothing threw", so a
/// dropped connection after acceptance reads as a failure and the report is re-sent.</para>
/// </summary>
public sealed class ReportDelivery : IReportDelivery
{
    private readonly IEmailSender _emailSender;
    private readonly ILogger<ReportDelivery> _log;

    public ReportDelivery(IEmailSender emailSender, ILogger<ReportDelivery> log)
    {
        _emailSender = emailSender;
        _log = log;
    }

    public async Task<SlaSendResult> SendAsync(IReadOnlyList<string> recipients, ReportDocument document,
        RenderedReport rendered, long businessUnitId, CancellationToken ct = default)
    {
        var addresses = recipients.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct().ToList();
        if (addresses.Count == 0)
        {
            _log.LogWarning("BU {Bu}: scheduled report '{Title}' has no recipient; nothing sent.",
                businessUnitId, document.Title);
            // Nothing entered the transport, so this is safely retryable.
            return SlaSendResult.NotSent("The subscription has no usable recipient address.");
        }

        var message = new EmailMessage
        {
            Subject = $"{document.Title} — {document.PeriodFrom:dd MMM} to {document.PeriodTo:dd MMM yyyy}",
            HtmlBody = BuildBody(document),
            TextBody = BuildTextBody(document),
            BusinessUnitId = businessUnitId.ToString(),
            Attachments = { new EmailAttachment(rendered.FileName, rendered.Content, rendered.ContentType) }
        };
        foreach (var address in addresses) message.AddTo(address);

        try
        {
            var receipt = await _emailSender.SendAsync(message, ct);
            if (receipt is null)
            {
                // The transport WAS entered and returned with no acceptance evidence. Uncertain,
                // not failed: the provider may hold the message, and a second copy of a board
                // report cannot be recalled.
                _log.LogError(
                    "BU {Bu}: scheduled report '{Title}' returned no acceptance evidence; delivery is "
                    + "UNCERTAIN and it will NOT be re-sent. Check the provider before resending by hand.",
                    businessUnitId, document.Title);
                return SlaSendResult.Uncertain("The mail provider returned no acceptance evidence.");
            }
            return SlaSendResult.Accepted(receipt);
        }
        catch (Exception ex)
        {
            // Including cancellation on shutdown: the transport was entered and we cannot see how
            // far it got.
            _log.LogError(ex, "BU {Bu}: scheduled report '{Title}' threw inside the transport; "
                              + "delivery is UNCERTAIN and it will NOT be re-sent.",
                businessUnitId, document.Title);
            return SlaSendResult.Uncertain($"SEND_THREW:{ex.GetType().Name}");
        }
    }

    /// <summary>
    /// The body states the period and where the numbers came from, then defers to the attachment.
    /// It deliberately does NOT restate a headline figure: a summary line in the mail and a table in
    /// the attachment are two places one number can disagree with itself.
    /// </summary>
    private static string BuildBody(ReportDocument document) =>
        $"""
         <p>{Escape(document.Title)}</p>
         <p>{Escape(document.TenantLabel)}<br/>
         Period: {Escape(document.PeriodLabel)}<br/>
         Generated: {document.GeneratedAt:dd MMM yyyy HH:mm} UTC</p>
         <p>The full report is attached. Every figure in it is drawn from the same records the
         Nexora dashboard reads, and any figure that could not be evidenced is stated as
         unavailable with the reason rather than shown as a number.</p>
         """;

    private static string BuildTextBody(ReportDocument document) =>
        $"{document.Title}\n{document.TenantLabel}\nPeriod: {document.PeriodLabel}\n"
        + $"Generated: {document.GeneratedAt:dd MMM yyyy HH:mm} UTC\n\nThe full report is attached.";

    private static string Escape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
