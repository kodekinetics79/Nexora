using ERP_RFQ_Automation.Extraction;
using System.Text.Json;
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

/// <summary>What a resume attempt did. Counts and a typed verdict — never message content.</summary>
public enum EmailInquiryResumeOutcome
{
    /// <summary>No component of this message is held without a job. Nothing to do.</summary>
    NothingToResume,

    /// <summary>Scheduling ran again and at least one held component now holds a durable job.</summary>
    Resumed,

    /// <summary>The stored original is gone, so scheduling can never be re-driven from it.</summary>
    EvidenceLost,

    /// <summary>
    /// The stored original no longer agrees with what was captured, so scheduling refuses. A
    /// re-drive cannot fix this and repeating it forever is how a message is never decided.
    /// </summary>
    ManifestRefused,

    /// <summary>
    /// The message was classified as a supplier document rather than a request to quote. Its
    /// parts are not owed extraction jobs at all, so resuming would be scheduling work that
    /// nothing wants.
    /// </summary>
    NotAnInquiry,

    /// <summary>Scheduling ran and every held component is still held.</summary>
    StillHeld
}

/// <param name="Scheduled">Held components that now hold a durable job.</param>
/// <param name="StillHeld">Held components that could not be scheduled on this pass.</param>
public sealed record EmailInquiryResumeResult(
    EmailInquiryResumeOutcome Outcome, int Scheduled, int StillHeld);

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

    /// <summary>
    /// Re-drives scheduling for a message whose parts were HELD without ever reaching the queue,
    /// from the durable original rather than from the mailbox.
    ///
    /// <para><b>Why this had to exist.</b> Four scheduling failures record
    /// <see cref="EmailInquiryComponentStatus.FailedRecoverable"/> with no
    /// <c>ExtractionJobId</c> — a manifest refusal, an evidence-storage outage, an inspection
    /// fault and the catch-all. Nothing could move any of them afterwards. The recovery sweep
    /// looked only at Pending/Inspecting/Extracting components; the operator dead-letter queue
    /// needs a job that was never created; and the mailbox re-poll window is
    /// <c>max(lastSuccessfulPoll, now - MinLookbackDays)</c>, one day by default, so within
    /// 24-48 hours the message left the search window for good. The customer's request was
    /// captured, durable, and unreachable.</para>
    ///
    /// <para>It runs the SAME door a fresh message takes —
    /// <c>EmailIngestEnqueuer.ScheduleAsync</c> — against a manifest re-planned from the stored
    /// bytes, so a replay cannot take a different path from the original. The bound on how long
    /// this may be attempted belongs to the caller, not here: this method reports what happened
    /// and never decides that a message has run out of chances.</para>
    /// </summary>
    Task<EmailInquiryResumeResult> ResumeSchedulingAsync(
        long businessUnitId, long assemblyId, CancellationToken ct = default);
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
    private readonly IRawEmailEvidenceReader _rawEmail;
    private readonly ILogger<EmailInquiryIntakeService> _log;

    public EmailInquiryIntakeService(
        ErpRfqAutomationContext context,
        IEmailInquiryCaptureService capture,
        IEmailInquiryAssemblyCoordinator coordinator,
        IDocumentIngestion ingestion,
        IRawEmailEvidenceReader rawEmail,
        ILogger<EmailInquiryIntakeService> log)
    {
        _context = context;
        _capture = capture;
        _coordinator = coordinator;
        _ingestion = ingestion;
        _rawEmail = rawEmail;
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

        // Classification never outranks evidence durability. Noise is captured through the
        // same immutable raw-mail boundary as an inquiry, then terminates explicitly without
        // scheduling extraction. That makes a false-positive rejection reviewable/replayable
        // after a restart instead of leaving its only copy on container-local disk.
        if (triage.Outcome == EmailTriageOutcome.Noise)
        {
            await _coordinator.MarkNoInquiryAsync(
                assembly,
                $"triage_noise: {string.Join(',', triage.ReasonCodes)}",
                ct);
            return new EmailInquiryIntakeResult(
                assembly.Id,
                EmailIngestEnqueuer.DeriveBatchId(assembly.Id, assembly.MessageKey),
                Scheduled: 0,
                AlreadyScheduled: 0,
                Held: 0,
                assembly.ExpectedComponentCount,
                capture.AlreadyCaptured,
                capture.SafeToMarkSeen,
                FailureReason: null);
        }

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
            // A compatible manifest with an unbound held part needs the original MIME bytes to
            // retry scheduling, so it stays unread and the next poll resumes the same assembly.
            // A manifest-contract refusal is durable/operator-visible and cannot improve by
            // hammering the mailbox, so that permanent hold remains safe to acknowledge.
            capture.SafeToMarkSeen
                && (schedule.Held == 0 || schedule.Verdict != EmailManifestVerdict.Compatible),
            schedule.Held > 0 && schedule.Verdict == EmailManifestVerdict.Compatible
                ? "component_scheduling_incomplete"
                : null);
    }

    public async Task<EmailInquiryResumeResult> ResumeSchedulingAsync(
        long businessUnitId, long assemblyId, CancellationToken ct = default)
    {
        var assembly = await _context.EmailInquiryAssemblies
            .FirstOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == assemblyId, ct)
            ?? throw new InvalidOperationException(
                $"Email inquiry assembly {assemblyId} was not found for this tenant.");

        var components = await _context.EmailInquiryComponents
            .Where(c => c.BusinessUnitId == businessUnitId && c.AssemblyId == assemblyId)
            .OrderBy(c => c.Ordinal)
            .ToListAsync(ct);

        // The exact population this method exists for, and nothing else. A hold that DOES name a
        // job belongs to audited dead-letter recovery: re-submitting the same content returns
        // that same exhausted job and merely makes the component look active while nothing can
        // claim it.
        var heldWithoutJob = components
            .Count(c => c.Status == EmailInquiryComponentStatus.FailedRecoverable
                        && c.ExtractionJobId is null);
        if (heldWithoutJob == 0)
            return new EmailInquiryResumeResult(EmailInquiryResumeOutcome.NothingToResume, 0, 0);

        var ingest = await _context.EmailIngests
            .Include(e => e.EmailConfiguration)
            .FirstOrDefaultAsync(e => e.Id == assembly.EmailIngestId, ct);
        if (ingest is null)
            return new EmailInquiryResumeResult(
                EmailInquiryResumeOutcome.EvidenceLost, 0, heldWithoutJob);

        // A supplier quote or invoice is not owed extraction jobs at all — the worker completes
        // such a job without creating a Lead. Scheduling one here would queue work whose only
        // possible outcome is to be routed away again.
        var triage = ReconstructTriage(ingest);
        if (triage.Outcome == EmailTriageOutcome.CommercialNonInquiry
            || triage.Outcome == EmailTriageOutcome.Noise)
            return new EmailInquiryResumeResult(
                EmailInquiryResumeOutcome.NotAnInquiry, 0, heldWithoutJob);

        var message = await _rawEmail.TryLoadAsync(businessUnitId, ingest, ct);
        if (message is null)
        {
            _log.LogError(
                "Assembly {AssemblyId} (business unit {BusinessUnitId}) has {Held} part(s) held "
                + "without a processing job, and no copy of the original message survives, so "
                + "scheduling cannot be re-driven.",
                assemblyId, businessUnitId, heldWithoutJob);
            return new EmailInquiryResumeResult(
                EmailInquiryResumeOutcome.EvidenceLost, 0, heldWithoutJob);
        }

        // Recomputed from the stored bytes, exactly as the poller computes it from the live
        // ones — the fresh body is a deterministic function of the message and is not stored
        // separately. DRIFT IS SELF-DETECTING rather than silent: the body component's hash was
        // recorded from this same derivation at capture, so a divergence makes the manifest
        // verifier refuse and this message finalizes into a human's hands instead of being
        // rescheduled against content nobody recorded.
        var freshBodyText = EmailBodyNormalizer.Normalize(ExtractBodyText(message)).Fresh;

        var plan = await EmailInquiryManifestPlanner.PlanAsync(
            message, assembly.MessageKey, freshBodyText, ct: ct);

        var schedule = await EmailIngestEnqueuer.ScheduleAsync(
            assembly, components, plan, ingest, assembly.SenderAddress, _ingestion, triage,
            _coordinator, _log, ct);

        if (schedule.Verdict != EmailManifestVerdict.Compatible)
        {
            _log.LogError(
                "Scheduling could not be resumed for assembly {AssemblyId} (business unit "
                + "{BusinessUnitId}): the stored original and the recorded parts no longer agree "
                + "({Verdict}). Retrying cannot change that.",
                assemblyId, businessUnitId, schedule.Verdict);
            return new EmailInquiryResumeResult(
                EmailInquiryResumeOutcome.ManifestRefused, 0, heldWithoutJob);
        }

        _log.LogInformation(
            "Scheduling resumed for assembly {AssemblyId} (business unit {BusinessUnitId}) from "
            + "durable evidence: {Scheduled} of {Held} previously unscheduled part(s) now hold a "
            + "processing job, {StillHeld} remain held.",
            assemblyId, businessUnitId, schedule.Scheduled, heldWithoutJob, schedule.Held);

        return new EmailInquiryResumeResult(
            schedule.Scheduled > 0
                ? EmailInquiryResumeOutcome.Resumed
                : EmailInquiryResumeOutcome.StillHeld,
            schedule.Scheduled, schedule.Held);
    }

    /// <summary>
    /// The triage verdict as the ledger recorded it, so a replay carries the decision the gate
    /// actually reached rather than re-deciding it from a message whose context has moved on.
    ///
    /// <para><c>CommercialDocumentTypeHint</c> is deliberately absent: it is not persisted, and
    /// inventing one would route a supplier quote into lead creation. The caller refuses to
    /// resume a <c>CommercialNonInquiry</c> message for exactly that reason, so the hint can
    /// never be needed on this path.</para>
    /// </summary>
    private static EmailTriageDecision ReconstructTriage(EmailIngest ingest)
    {
        var outcome = Enum.TryParse<EmailTriageOutcome>(ingest.TriageOutcome, out var parsed)
            ? parsed
            // Null for every message ingested before the gate existed. Uncertain, not Inquiry:
            // it is the outcome that carries no claim either way.
            : EmailTriageOutcome.Uncertain;

        string[] reasons;
        try
        {
            reasons = string.IsNullOrWhiteSpace(ingest.TriageReasonJson)
                ? []
                : JsonSerializer.Deserialize<string[]>(ingest.TriageReasonJson) ?? [];
        }
        catch (JsonException)
        {
            reasons = [];
        }

        return new EmailTriageDecision(
            outcome, reasons, CommercialDocumentTypeHint: null,
            ThreadContinuation: !string.IsNullOrWhiteSpace(ingest.InReplyToMessageId)
                                || !string.IsNullOrWhiteSpace(ingest.ReferencesJson));
    }

    /// <summary>
    /// DRIFT GUARD: mirrors <c>EmailService.GetEmailBody</c>. Both derive the same text from the
    /// same message, and a divergence is caught by the manifest verifier rather than producing a
    /// second, different body for one email.
    /// </summary>
    private static string ExtractBodyText(MimeMessage message)
    {
        var plain = message.GetTextBody(MimeKit.Text.TextFormat.Plain);
        if (!string.IsNullOrWhiteSpace(plain)) return plain;
        var html = message.GetTextBody(MimeKit.Text.TextFormat.Html);
        return html is null
            ? string.Empty
            : System.Text.RegularExpressions.Regex.Replace(html, "<.*?>", " ")
                .Replace("&nbsp;", " ")
                .Replace("\r\n", "\n");
    }
}
