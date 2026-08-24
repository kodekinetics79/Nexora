using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Ingestion.Assembly;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Security.DocumentInspection;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace ERP_RFQ_Automation.Ingestion.Triage;

/// <param name="BatchId">The batch every job of this message shares.</param>
/// <param name="Queued">Number of jobs enqueued (duplicates count — they are handled work).</param>
/// <param name="SkippedAttachments">"filename (reason)" for every attachment that could not be enqueued.</param>
public sealed record EmailEnqueueResult(Guid BatchId, int Queued, IReadOnlyList<string> SkippedAttachments);

/// <param name="Scheduled">Components handed to the durable queue on this pass.</param>
/// <param name="AlreadyScheduled">Components whose job was verified to already exist.</param>
/// <param name="Held">
/// Components left recoverable. Non-zero means the message is NOT fully scheduled and must not
/// be acknowledged.
/// </param>
/// <param name="Verdict">The manifest verdict that permitted or blocked this pass.</param>
public sealed record EmailScheduleResult(
    Guid BatchId, int Scheduled, int AlreadyScheduled, int Held, EmailManifestVerdict Verdict)
{
    /// <summary>True when every Process component of the message now holds a durable job.</summary>
    public bool FullyScheduled => Held == 0 && Verdict == EmailManifestVerdict.Compatible;
}

/// <summary>
/// The ONE routine that turns an email into extraction jobs: one job per supported
/// attachment plus one job for the sender's fresh body text, all sharing a batch id and a
/// provenance sidecar that names the real <see cref="EmailIngest"/>.
///
/// It is shared deliberately. The mailbox poller and the manual "reprocess as inquiry"
/// endpoint MUST fan out identically — a replayed message that took a different path would
/// not be a replay, and the difference would only ever be discovered on the message someone
/// was trying to rescue.
/// </summary>
public static class EmailIngestEnqueuer
{
    public const long MaxAttachmentBytes = 25 * 1024 * 1024;

    // ------------------------------------------------------------------------------------
    // THE DURABLE CANONICAL SCHEDULER — and now the only one.
    //
    // The legacy EnqueueAsync fan-out that used to sit above this line is GONE, together with
    // both of its production callers. It walked message.Attachments, which is not the MIME tree
    // — MimeKit yields only entities whose Content-Disposition says "attachment", so a
    // forwarded enquiry from Outlook or Gmail carried no disposition header and was invisible —
    // and it produced one extraction job, and therefore one Lead, per file. A buyer who sent a
    // covering note and two priced schedules became three Leads, each quotable from a third of
    // the request, with nothing in the data saying they belonged together.
    //
    // Nothing in production fans out on message.Attachments any more. EmailInquiryManifestPlanner
    // is the single MIME walk, and this method is the single door to the queue.
    // ------------------------------------------------------------------------------------

    /// <summary>Reason recorded when the stored original stops matching what was captured.</summary>
    public const string ManifestMismatchReason = "manifest_mismatch";

    /// <summary>Reason recorded when the durable queue refused the component.</summary>
    public const string SchedulingFailedReason = "scheduling_failed";

    /// <summary>
    /// Reason recorded when the malware scanner REFUSED a part of the message.
    ///
    /// <para>Distinct from <see cref="SchedulingFailedReason"/> on purpose. A refusal is a
    /// verdict, not an outage: it is terminal, it outranks every other component's outcome, and
    /// the message must be acknowledged to the mailbox so the same infected attachment is not
    /// re-downloaded, re-decoded and re-fed to the scanner on every poll for the next day.</para>
    /// </summary>
    public const string MalwareRefusedReason = "malware_detected";

    /// <summary>What a person is told when a part of their message was refused by the scanner.</summary>
    public const string MalwareRefusedDetail =
        "A part of this message was refused because the malware scanner reported it as unsafe. "
        + "It has not been opened or processed, and no inquiry was created from it.";

    /// <summary>What a person is told when inspection stopped without a verdict.</summary>
    public const string InspectionUnavailableDetail =
        "This part of the message could not be checked for malware because the scanner was "
        + "unavailable, so it has not been processed yet.";

    /// <summary>What a person is told when inspection refused the file on its own merits.</summary>
    public const string InspectionRefusedDetail =
        "This part of the message could not be accepted for processing, so it was not read. "
        + "The original email and every part of it are retained.";

    /// <summary>
    /// Schedules every Process component of a message that does not already hold a durable job.
    ///
    /// <para><b>Idempotency is the database's, not ours.</b>
    /// <c>SourceOccurrenceIdentity.BuildKey</c> composes the occurrence key as
    /// <c>{batchId}:{sourceType}:{sha256(SourceOccurrenceId)}</c> — the batch id is INSIDE the
    /// key. A random batch id per pass therefore produced a different occurrence key for the
    /// same component every time, so <c>ux_source_document_occurrences_tenant_idempotency</c>
    /// never fired, a second occurrence was created,
    /// <c>UX_ExtractionJobs_BU_SourceOccurrence</c> never fired, and the component received two
    /// extraction jobs — two Leads for one email part.</para>
    ///
    /// <para>Feeding that key a <b>derived</b> batch id and the <b>persisted</b>
    /// <c>ComponentKey</c> makes both existing unique indexes do the work. No new queue, no new
    /// constraint, no advisory lock of our own.</para>
    /// </summary>
    /// <param name="components">Persisted rows — the authority on identity and disposition.</param>
    /// <param name="plan">
    /// The plan those rows came from, carrying decoded bytes. Verified against the rows before
    /// anything is scheduled.
    /// </param>
    public static async Task<EmailScheduleResult> ScheduleAsync(
        EmailInquiryAssembly assembly,
        IReadOnlyCollection<EmailInquiryComponent> components,
        EmailInquiryManifest plan,
        EmailIngest ingest,
        string? clientEmail,
        IDocumentIngestion ingestion,
        EmailTriageDecision triage,
        IEmailInquiryAssemblyCoordinator coordinator,
        ILogger logger,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(components);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(ingest);
        ArgumentNullException.ThrowIfNull(ingestion);
        ArgumentNullException.ThrowIfNull(triage);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(logger);

        var batchId = DeriveBatchId(assembly.Id, assembly.MessageKey);

        // Compatible is the ONLY schedulable verdict, and the branch is on the TYPE. Parsing the
        // human-readable detail for control flow is how a hold becomes a substring match that
        // stops matching the day someone improves the wording.
        var verification = EmailComponentManifestVerifier.Verify(
            assembly.ManifestContractVersion, assembly.ExpectedComponentCount, components, plan);

        if (!verification.IsCompatible)
        {
            var detail = EmailComponentManifestVerifier.Describe(verification.Mismatches);
            logger.LogError(
                "Manifest {Verdict} on assembly {AssemblyId} (business unit {BusinessUnitId}, "
                + "mailbox {EmailConfigurationId}, ingest {EmailIngestId}): {Detail}",
                verification.Verdict, assembly.Id, assembly.BusinessUnitId,
                assembly.EmailConfigurationId, assembly.EmailIngestId, detail);

            // Hold every part still in flight. Scheduling the subset that happens to match would
            // build a Lead from a message we cannot vouch for.
            var held = 0;
            foreach (var component in components.Where(c => !c.IsTerminal).OrderBy(c => c.Ordinal))
            {
                await coordinator.RecordComponentOutcomeAsync(
                    assembly.BusinessUnitId, assembly.Id, component.ComponentKey,
                    EmailInquiryComponentStatus.FailedRecoverable,
                    ManifestMismatchReason, detail, null, ct);
                held++;
            }
            // Evaluated here for the same reason as the compatible path below: a refused manifest
            // whose components are ALL already terminal records no outcome, so nothing else would
            // ever ask the barrier what the message now is.
            await coordinator.ReevaluateAsync(assembly.Id, assembly.BusinessUnitId, ct);
            return new EmailScheduleResult(batchId, 0, 0, held, verification.Verdict);
        }

        var plans = plan.Components.ToDictionary(c => c.ComponentKey, StringComparer.Ordinal);
        var scheduled = 0;
        var alreadyScheduled = 0;
        var heldByFailure = 0;

        foreach (var component in components.OrderBy(c => c.Ordinal))
        {
            ct.ThrowIfCancellationRequested();

            // Terminal at capture — Skipped, Ignored, RefusedSecurity, StructuralOnly — stays
            // represented on the message and produces no job by design.
            if (component.IsTerminal) continue;

            // A HELD component must be re-scheduled, not counted as done.
            //
            // Without this, the moment capture is wired every component the worker holds keeps
            // its job reference, DurableJobBelongsToComponentAsync confirms the job is genuinely
            // its own, and the component is counted alreadyScheduled forever. Nothing sweeps
            // holds, so every assembly parks in FailedRecoverable and email-to-Lead throughput
            // goes to zero - the outage removed at 3a672dd, re-armed on a delay.
            if (component.ExtractionJobId is { } heldJobId && component.IsRecoverableHold)
            {
                // A job-bound hold belongs to governed dead-letter recovery. Re-submitting the
                // same content returns that same exhausted job and merely makes the component
                // look active while nothing can claim it.
                logger.LogWarning(
                    "Component {ComponentKey} of assembly {AssemblyId} remains held on job "
                    + "{ExtractionJobId}; an audited dead-letter recovery is required.",
                    component.ComponentKey, assembly.Id, heldJobId);
                heldByFailure++;
                continue;
            }

            if (component.ExtractionJobId is { } existingJobId)
            {
                // A non-null id is NOT proof a job exists. Verified against the tenant's own
                // jobs; if it has gone, the component is rescheduled rather than counted as done.
                if (await coordinator.DurableJobBelongsToComponentAsync(
                        assembly.BusinessUnitId, existingJobId, batchId, component.ComponentKey, ct))
                {
                    alreadyScheduled++;
                    continue;
                }
                logger.LogWarning(
                    "Component {ComponentKey} of assembly {AssemblyId} references job "
                    + "{ExtractionJobId}, which is not that component's own durable job for "
                    + "business unit {BusinessUnitId} (purged, or belonging to another component "
                    + "or message); rescheduling.",
                    component.ComponentKey, assembly.Id, existingJobId, assembly.BusinessUnitId);
            }

            var componentPlan = plans[component.ComponentKey];

            try
            {
                var result = await ingestion.IngestAsync(
                    componentPlan.Content.ToArray(),
                    component.FileName ?? $"component-{component.Ordinal}",
                    assembly.BusinessUnitId, ExtractionSourceType.Email,
                    batchId, priority: 0,
                    BuildMetadata(assembly, component, ingest, clientEmail, triage),
                    // THE ownership authority, written with the job row. The sidecar built above
                    // carries the same ids as diagnostic hints and is explicitly not trusted to
                    // authorize anything; this parameter is.
                    component.Id, ct);

                await coordinator.RecordComponentQueuedAsync(
                    assembly.BusinessUnitId, assembly.Id, component.ComponentKey, result.JobId, ct,
                    result.StoragePath, result.SourceDocumentOccurrenceId);
                scheduled++;

                logger.LogInformation(
                    "Scheduled component {ComponentKey} (ordinal {Ordinal}, {Kind}) of assembly "
                    + "{AssemblyId} as job {ExtractionJobId} ({Outcome}) for business unit "
                    + "{BusinessUnitId}, mailbox {EmailConfigurationId}, ingest {EmailIngestId}.",
                    component.ComponentKey, component.Ordinal, component.Kind, assembly.Id,
                    result.JobId, result.Outcome, assembly.BusinessUnitId,
                    assembly.EmailConfigurationId, assembly.EmailIngestId);
            }
            catch (EvidenceStorageUnavailableException exception)
            {
                // The store, not the file. Holding this component holds the WHOLE message, which
                // is what stops a body-only Lead when the attachment carrying the priced lines
                // could not be stored.
                logger.LogError(exception,
                    "Durable evidence storage is unavailable while scheduling component "
                    + "{ComponentKey} of assembly {AssemblyId} (configuration fault: "
                    + "{IsConfigurationFault}); the message is held.",
                    component.ComponentKey, assembly.Id, exception.IsConfigurationFault);
                await coordinator.RecordComponentOutcomeAsync(
                    assembly.BusinessUnitId, assembly.Id, component.ComponentKey,
                    EmailInquiryComponentStatus.FailedRecoverable,
                    EvidenceStorageUnavailableException.ErrorCode,
                    "Document storage was unavailable, so this part has not been processed yet.",
                    null, ct);
                heldByFailure++;
            }
            // BEFORE the catch-all, and that order is the whole point.
            //
            // Inspection runs SYNCHRONOUSLY inside IngestAsync — quarantine, scan, promote —
            // so its verdict arrives here as an exception rather than as a job outcome. The
            // catch-all below swallowed it and recorded the generic "could not be queued yet",
            // which had two consequences and both cost real money. A malware verdict became a
            // RECOVERABLE hold, so EmailInquiryComponentStatus.RefusedSecurity had no writer at
            // all and the state machine's highest-priority branch was unreachable; and because a
            // hold makes the message unsafe to acknowledge, the mailbox kept the infected message
            // unread and every poll cycle re-downloaded it, re-decoded it and re-fed it to the
            // scanner. The retryable case lost just as much: the generic constant erased the
            // security_scanner_unavailable code that EmailInquiryComponentClosure keys on, so a
            // scanner outage was indistinguishable from an unreadable file.
            catch (DocumentInspectionException exception)
            {
                var errorCode = exception.Inspection.ErrorCode;

                // A refusal outranks everything. Terminal, absorbing, and NOT held: the message
                // is safe to acknowledge precisely because there is nothing left to retry.
                if (exception.Inspection.MalwareStatus == MalwareScanStatus.Infected)
                {
                    logger.LogWarning(
                        "Component {ComponentKey} of assembly {AssemblyId} was refused by the "
                        + "malware scanner ({ErrorCode}); the message is refused on security "
                        + "grounds and will not be re-fetched.",
                        component.ComponentKey, assembly.Id, errorCode);
                    await coordinator.RecordComponentOutcomeAsync(
                        assembly.BusinessUnitId, assembly.Id, component.ComponentKey,
                        EmailInquiryComponentStatus.RefusedSecurity,
                        MalwareRefusedReason, MalwareRefusedDetail, null, ct);
                    continue;
                }

                // Everything else is decided by the ONE shared infrastructure-vs-content rule,
                // fed the scanner's own code rather than a constant. A scanner outage HOLDS (the
                // file is presumed readable once it is back); a file inspection refused on its
                // own merits — a macro-enabled workbook, an unreadable container — is a content
                // fault and is terminal, because no job exists to dead-letter it and a hold with
                // no mover is how a message disappears.
                var resolved = EmailInquiryComponentClosure.StatusFor(errorCode);
                logger.LogError(exception,
                    "Document inspection stopped component {ComponentKey} of assembly "
                    + "{AssemblyId} ({ErrorCode}); it is recorded as {Resolved}.",
                    component.ComponentKey, assembly.Id, errorCode, resolved);
                await coordinator.RecordComponentOutcomeAsync(
                    assembly.BusinessUnitId, assembly.Id, component.ComponentKey, resolved,
                    errorCode,
                    resolved == EmailInquiryComponentStatus.FailedRecoverable
                        ? InspectionUnavailableDetail
                        : InspectionRefusedDetail,
                    null, ct);
                if (resolved == EmailInquiryComponentStatus.FailedRecoverable) heldByFailure++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception,
                    "Failed to schedule component {ComponentKey} of assembly {AssemblyId}.",
                    component.ComponentKey, assembly.Id);
                await coordinator.RecordComponentOutcomeAsync(
                    assembly.BusinessUnitId, assembly.Id, component.ComponentKey,
                    EmailInquiryComponentStatus.FailedRecoverable,
                    SchedulingFailedReason,
                    "This part of the message could not be queued for processing yet.",
                    null, ct);
                heldByFailure++;
            }
        }

        // THE MESSAGE IS EVALUATED BEFORE THIS METHOD RETURNS, on every path.
        //
        // Every other trigger for the barrier is a REPORT — a component was queued, a component
        // failed, a component produced a result. A message whose every part was already terminal
        // at capture produces no report of any kind: the loop above skips each terminal component
        // and falls straight out, so nothing was queued, nothing failed, and none of the four
        // callers of ReevaluateCoreAsync ever fired. That message stayed at Captured with
        // CompletedComponentCount = 0 forever, and — because a manifest-compatible pass with
        // nothing held is safe to acknowledge — it was flagged \Seen and suppressed by the ledger
        // on every subsequent poll. It could not be found afterwards either: the recovery sweep
        // asks for ReadyForAssembly assemblies and non-terminal components, and this message has
        // neither.
        //
        // The evaluation belongs HERE rather than at the call site because this is the door every
        // scheduling pass goes through. Guarding the callers one at a time leaves the next one to
        // reintroduce the silence. It is idempotent — the verdict is a pure function of the
        // component rows — so the passes that already reevaluated simply recompute the same
        // answer.
        await coordinator.ReevaluateAsync(assembly.Id, assembly.BusinessUnitId, ct);

        return new EmailScheduleResult(
            batchId, scheduled, alreadyScheduled, heldByFailure, EmailManifestVerdict.Compatible);
    }

    private static ExtractionJobMetadata BuildMetadata(
        EmailInquiryAssembly assembly, EmailInquiryComponent component,
        EmailIngest ingest, string? clientEmail, EmailTriageDecision triage)
        => new()
        {
            // ComponentKey IS the source occurrence: that field's documented contract is "stable
            // identity of this receipt within its source system, such as an email attachment id
            // or MIME ordinal", which is precisely what a ComponentKey is. Read from the row,
            // never recomputed — recomputing is how the two walks came to disagree.
            SourceOccurrenceId = component.ComponentKey,
            LogicalGroupKey = $"email:{assembly.MessageKey}",
            EmailIngestId = ingest.Id,
            // Typed ownership. These are diagnostic HINTS: the sidecar carrying them is
            // best-effort by its own contract, so it can never authorize a tenant mutation. The
            // authoritative mapping is the persisted component row.
            EmailInquiryAssemblyId = assembly.Id,
            EmailInquiryComponentId = component.Id,
            EmailInquiryComponentKey = component.ComponentKey,
            BusinessUnitId = assembly.BusinessUnitId,
            FromEmail = assembly.SenderAddress,
            Subject = assembly.Subject ?? string.Empty,
            SourceReceivedAtUtc = assembly.ReceivedAtUtc,
            ClientEmail = clientEmail,
            LeadSource = "Email",
            EmailSource = component.Kind == EmailInquiryComponentKind.Body
                ? "Text Only"
                : GetFileTypeLabel(Path.GetExtension(component.FileName ?? string.Empty).ToLowerInvariant()),
            // An email BODY is conversational prose; an attachment keeps structured routing.
            BodyShape = component.Kind == EmailInquiryComponentKind.Body ? "prose" : null,
            TriageOutcome = triage.Outcome.ToString(),
            TriageReasonCodes = triage.ReasonCodes,
            ThreadContinuation = triage.ThreadContinuation,
            CommercialDocumentTypeHint = triage.CommercialDocumentTypeHint
        };

    /// <summary>
    /// A stable batch id for an assembly.
    ///
    /// <para>Derived, never generated. It is part of the occurrence idempotency key, so
    /// <c>Guid.NewGuid()</c> here splits one message across two batches on any retry and defeats
    /// both unique indexes. <c>GetHashCode()</c> is equally unusable — it is not stable across
    /// processes or framework versions, and this identity must survive a restart.</para>
    /// </summary>
    internal static Guid DeriveBatchId(long assemblyId, string messageKey)
    {
        var seed = System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes($"nexora:email-assembly-batch:v1:{assemblyId}:{messageKey}"));
        return new Guid(seed.AsSpan(0, 16));
    }

    /// <summary>
    /// ING-06: THE single owner of the durable skipped-attachment record.
    ///
    /// Every fan-out path (mailbox poller, manual reprocess, and the legacy direct-extraction
    /// path via <c>EmailService</c>) writes through here, so "an attachment was dropped" can
    /// never again depend on which branch the message happened to take. The caller owns the
    /// SaveChanges — both existing callers already save the ingest immediately afterwards.
    ///
    /// The value is written unconditionally, including the clear-to-null case: a replay that
    /// skips nothing must not leave a stale list claiming a loss that no longer applies.
    /// </summary>
    public static void RecordSkippedAttachments(EmailIngest ingest, IReadOnlyList<string> skipped)
    {
        ArgumentNullException.ThrowIfNull(ingest);
        if (skipped is null || skipped.Count == 0)
        {
            ingest.SkippedAttachmentsJson = null;
            return;
        }

        // Column is varchar(2000). A message with a pathological number of skipped attachments
        // must still record SOMETHING truthful rather than fail the ingest, so entries are
        // dropped from the tail and the remainder is counted explicitly.
        const int MaxJson = 2000;
        var entries = new List<string>(skipped);
        var json = JsonSerializer.Serialize(entries);
        while (json.Length > MaxJson && entries.Count > 1)
        {
            var omitted = skipped.Count - (entries.Count - 1);
            entries.RemoveAt(entries.Count - 1);
            entries[^1] = $"... and {omitted} more skipped attachment(s)";
            json = JsonSerializer.Serialize(entries);
        }
        ingest.SkippedAttachmentsJson = json.Length <= MaxJson ? json : json[..MaxJson];
    }

    private static string? Coalesce(string? first, string? second)
    {
        if (!string.IsNullOrWhiteSpace(first)) return first.Trim();
        return string.IsNullOrWhiteSpace(second) ? null : second.Trim();
    }

    /// <summary>File-type label used for Lead.EmailSource. Single owner — the email door and
    /// the reprocess endpoint must label identical bytes identically.</summary>
    internal static string GetFileTypeLabel(string ext) => ext switch
    {
        ".pdf" => "PDF",
        ".doc" or ".docx" => "Word",
        ".xls" or ".xlsx" or ".xlsm" => "Excel",
        ".csv" => "CSV",
        ".txt" => "Text",
        ".jpg" or ".jpeg" => "JPEG",
        ".png" => "PNG",
        ".bmp" => "BMP",
        ".gif" => "GIF",
        ".tif" or ".tiff" => "TIFF",
        ".webp" => "WebP",
        _ => "Unknown"
    };

    internal static string SanitizeFileName(string fileName)
        => string.Join("_", fileName.Split(Path.GetInvalidFileNameChars(),
            StringSplitOptions.RemoveEmptyEntries)).Replace(" ", "_");
}
