using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Ingestion.Triage;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace ERP_RFQ_Automation.Ingestion.Assembly;

/// <summary>What one message's intake produced. Counts and identity only — never content.</summary>
public sealed record EmailInquiryIntakeResult(
    long? AssemblyId,
    Guid BatchId,
    int Scheduled,
    int AlreadyScheduled,
    int Held,
    int ExpectedComponents,
    bool AlreadyCaptured,
    bool SafeToAcknowledge,
    string? FailureReason)
{
    /// <summary>True when the message is durably captured AND every processable part is queued.</summary>
    public bool FullyAccepted =>
        SafeToAcknowledge && AssemblyId is not null && Held == 0 && FailureReason is null;

    public static EmailInquiryIntakeResult Refused(string reason) =>
        new(null, Guid.Empty, 0, 0, 0, 0, false, false, reason);
}

public interface IEmailInquiryIntakeService
{
    /// <summary>
    /// THE one way a message enters the system: durable capture, then scheduling of every
    /// processable component.
    /// </summary>
    Task<EmailInquiryIntakeResult> CaptureAndScheduleAsync(
        MimeMessage message,
        EmailIngest ingest,
        EmailConfiguration configuration,
        string? freshBodyText,
        EmailTriageDecision triage,
        string? clientEmail,
        CancellationToken ct = default);
}

/// <summary>
/// The canonical intake path, shared by the mailbox poller and the manual reprocess endpoint.
///
/// <para><b>Why it is one class and not two call sites.</b> The two producers drifted before:
/// each walked <c>message.Attachments</c> itself, which is not the MIME tree — it yields only
/// entities whose Content-Disposition says "attachment", so a forwarded enquiry from Outlook or
/// Gmail was invisible to both. Sharing the routine is what makes "poll" and "reprocess" the
/// same operation with a different trigger, which is the only way the two can be reasoned about
/// together.</para>
///
/// <para><b>Order is the contract.</b> Capture writes the raw message to durable evidence and
/// commits the assembly and every component row in ONE transaction BEFORE anything is queued.
/// Only then is the message safe to acknowledge to the mailbox: a message marked \Seen whose
/// bytes were never stored is unrecoverable, and the mailbox is the only other copy.</para>
/// </summary>
public sealed class EmailInquiryIntakeService : IEmailInquiryIntakeService
{
    private readonly ErpRfqAutomationContext _context;
    private readonly IEmailInquiryCaptureService _capture;
    private readonly IEmailInquiryAssemblyCoordinator _coordinator;
    private readonly IDocumentIngestion _ingestion;
    private readonly ILogger<EmailInquiryIntakeService> _log;

    public EmailInquiryIntakeService(
        ErpRfqAutomationContext context,
        IEmailInquiryCaptureService capture,
        IEmailInquiryAssemblyCoordinator coordinator,
        IDocumentIngestion ingestion,
        ILogger<EmailInquiryIntakeService> log)
    {
        _context = context;
        _capture = capture;
        _coordinator = coordinator;
        _ingestion = ingestion;
        _log = log;
    }

    public async Task<EmailInquiryIntakeResult> CaptureAndScheduleAsync(
        MimeMessage message,
        EmailIngest ingest,
        EmailConfiguration configuration,
        string? freshBodyText,
        EmailTriageDecision triage,
        string? clientEmail,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(ingest);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(triage);

        EmailInquiryCaptureResult capture;
        try
        {
            capture = await _capture.CaptureAsync(message, ingest, configuration, freshBodyText, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Capture is the step that makes the message survivable. If it throws, the caller
            // must NOT acknowledge: the mailbox copy is the only one left.
            _log.LogError(exception,
                "Durable capture failed for ingest {IngestId} (business unit {BusinessUnitId}, "
                + "mailbox {EmailConfigurationId}). The message is not acknowledged.",
                ingest.Id, configuration.BusinessUnitId, configuration.Id);
            return EmailInquiryIntakeResult.Refused("capture_failed");
        }

        if (capture.Assembly is null)
        {
            _log.LogError(
                "Durable capture produced no assembly for ingest {IngestId}; the message is not "
                + "acknowledged.", ingest.Id);
            return EmailInquiryIntakeResult.Refused("capture_incomplete");
        }

        var assembly = capture.Assembly;

        // Re-planned from the SAME bytes capture used, so the manifest verifier compares like
        // with like. A mismatch means the persisted components and the message no longer agree,
        // and ScheduleAsync holds the whole message rather than scheduling the subset that fits.
        var plan = await EmailInquiryManifestPlanner.PlanAsync(
            message, assembly.MessageKey, freshBodyText, ct: ct);

        var components = await _context.EmailInquiryComponents
            .Where(c => c.BusinessUnitId == assembly.BusinessUnitId && c.AssemblyId == assembly.Id)
            .OrderBy(c => c.Ordinal)
            .ToListAsync(ct);

        var schedule = await EmailIngestEnqueuer.ScheduleAsync(
            assembly, components, plan, ingest, clientEmail, _ingestion, triage, _coordinator, _log, ct);

        _log.LogInformation(
            "Intake for ingest {IngestId} (business unit {BusinessUnitId}, mailbox "
            + "{EmailConfigurationId}): assembly {AssemblyId}, {Expected} expected component(s), "
            + "{Scheduled} scheduled, {AlreadyScheduled} already scheduled, {Held} held, "
            + "verdict {Verdict}.",
            ingest.Id, assembly.BusinessUnitId, configuration.Id, assembly.Id,
            assembly.ExpectedComponentCount, schedule.Scheduled, schedule.AlreadyScheduled,
            schedule.Held, schedule.Verdict);

        return new EmailInquiryIntakeResult(
            assembly.Id,
            schedule.BatchId,
            schedule.Scheduled,
            schedule.AlreadyScheduled,
            schedule.Held,
            assembly.ExpectedComponentCount,
            capture.AlreadyCaptured,
            // The mailbox may be told the message was read once the bytes are DURABLE. A part
            // that could not be queued is a recoverable hold on a message we already own — it is
            // visible, re-schedulable, and re-reading it from IMAP would only duplicate it.
            capture.SafeToMarkSeen,
            null);
    }
}
