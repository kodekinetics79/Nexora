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

    public static async Task<EmailEnqueueResult> EnqueueAsync(
        MimeMessage message,
        EmailIngest ingest,
        long businessUnitId,
        string? clientEmail,
        IDocumentIngestion ingestion,
        EmailTriageDecision triage,
        EmailBodyParts bodyParts,
        ILogger logger,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(ingest);
        ArgumentNullException.ThrowIfNull(ingestion);
        ArgumentNullException.ThrowIfNull(triage);
        ArgumentNullException.ThrowIfNull(bodyParts);

        var batchId = Guid.NewGuid();
        var queued = 0;
        var skippedAttachments = new List<string>();
        // ING-08: prefer the durable ingest key over the raw header. They are the same string
        // whenever the message carries a Message-Id; when it does not, the ingest key is a
        // stable content hash, so the group key survives a replay instead of changing every
        // time (it used to fall straight through to the row id, which the triage screen — which
        // looks messages up by `email:{EmailIngest.MessageId}` — could never match).
        var messageKey = Coalesce(message.MessageId, ingest.MessageId) ?? ingest.Id.ToString();

        var metadata = new ExtractionJobMetadata
        {
            SourceOccurrenceId = $"email:{messageKey}:body",
            LogicalGroupKey = $"email:{messageKey}",
            EmailIngestId = ingest.Id,
            FromEmail = message.From.Mailboxes.FirstOrDefault()?.Address,
            Subject = message.Subject ?? "",
            SourceReceivedAtUtc = message.Date,
            ClientEmail = clientEmail,
            LeadSource = "Email",
            EmailSource = "Text Only",
            // An email BODY is conversational prose, not a document.
            BodyShape = "prose",
            TriageOutcome = triage.Outcome.ToString(),
            TriageReasonCodes = triage.ReasonCodes,
            ThreadContinuation = triage.ThreadContinuation,
            CommercialDocumentTypeHint = triage.CommercialDocumentTypeHint
        };

        void RecordSkippedAttachment(string fileName, string reason)
        {
            skippedAttachments.Add($"{fileName} ({reason})");
            logger.LogWarning("Skipping email attachment {FileName} for ingest {IngestId}: {Reason}.",
                fileName, ingest.Id, reason);
        }

        // Set once durable evidence storage refuses a write. Every later attachment would fail
        // identically — the store is what is down, not the file — so they are recorded as
        // skipped for the same, true reason instead of each paying a doomed upload.
        EvidenceStorageUnavailableException? storageOutage = null;

        // 1) Attachments first, so the body job's provenance can carry the complete skip list.
        var attachmentOrdinal = 0;
        foreach (var att in message.Attachments)
        {
            ct.ThrowIfCancellationRequested();
            attachmentOrdinal++;
            if (att is not MimePart part)
            {
                RecordSkippedAttachment(
                    att.ContentDisposition?.FileName ?? $"attachment #{attachmentOrdinal}",
                    "embedded email message is not ingested");
                continue;
            }
            if (part.FileName == null)
            {
                RecordSkippedAttachment($"attachment #{attachmentOrdinal}", "attachment has no filename");
                continue;
            }
            var ext = Path.GetExtension(part.FileName).ToLowerInvariant();
            if (!ERP_RFQ_Automation.Services.EmailService.IsSupportedExtension(ext))
            {
                RecordSkippedAttachment(part.FileName, $"unsupported file type '{ext}'");
                continue;
            }
            if (storageOutage is not null)
            {
                RecordSkippedAttachment(part.FileName, "document storage is unavailable");
                continue;
            }

            try
            {
                using var ms = new MemoryStream();
                await part.Content.DecodeToAsync(ms, ct);
                if (ms.Length == 0)
                {
                    RecordSkippedAttachment(part.FileName, "attachment content is empty");
                    continue;
                }
                if (ms.Length > MaxAttachmentBytes)
                {
                    RecordSkippedAttachment(part.FileName,
                        $"exceeds the {MaxAttachmentBytes / (1024 * 1024)} MB size limit ({ms.Length} bytes)");
                    continue;
                }

                var attachmentMetadata = new ExtractionJobMetadata
                {
                    SourceOccurrenceId = $"email:{messageKey}:attachment:{attachmentOrdinal}",
                    LogicalGroupKey = metadata.LogicalGroupKey,
                    EmailIngestId = ingest.Id,
                    FromEmail = metadata.FromEmail,
                    Subject = metadata.Subject,
                    SourceReceivedAtUtc = metadata.SourceReceivedAtUtc,
                    ClientEmail = metadata.ClientEmail,
                    LeadSource = "Email",
                    EmailSource = GetFileTypeLabel(ext),
                    // The commercial hint is inherited (a supplier's quotation PDF is still a
                    // supplier quotation) but BodyShape is NOT: an attachment is a document
                    // and keeps the structured routing.
                    CommercialDocumentTypeHint = triage.CommercialDocumentTypeHint,
                    TriageOutcome = triage.Outcome.ToString(),
                    TriageReasonCodes = triage.ReasonCodes,
                    ThreadContinuation = triage.ThreadContinuation
                };
                var attachmentResult = await ingestion.IngestAsync(
                    ms.ToArray(), part.FileName, businessUnitId, ExtractionSourceType.Email,
                    batchId, priority: 0, attachmentMetadata, ct);
                queued++;
                logger.LogInformation("Enqueued attachment {FileName} as job {JobId} ({Outcome}) for ingest {IngestId}.",
                    part.FileName, attachmentResult.JobId, attachmentResult.Outcome, ingest.Id);
            }
            catch (EvidenceStorageUnavailableException ex)
            {
                // Storage, not the attachment. The reason recorded here is durable and rendered
                // in the triage UI, so it must name a cause the reader can act on and nothing else.
                storageOutage = ex;
                RecordSkippedAttachment(part.FileName, "document storage is unavailable");
                logger.LogError(ex,
                    "Durable evidence storage is unavailable while enqueueing attachment {FileName} for ingest "
                    + "{IngestId} (configuration fault: {IsConfigurationFault}); remaining attachments were not attempted.",
                    part.FileName, ingest.Id, ex.IsConfigurationFault);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Poison-attachment isolation: continue with the remaining documents.
                //
                // The skip MUST also reach the durable ledger. This was the one skip path that
                // recorded nothing, so a transient storage failure on the single attachment
                // carrying the BoQ left a Queued ingest, a body-only lead, and a log line — the
                // exact loss the ING-06 note below describes, reintroduced through the catch.
                //
                // The reason is durable AND user-visible, so it must not carry the exception type
                // name: that field used to publish "AmazonS3Exception" straight onto the triage
                // screen. The type belongs in the log line underneath, with the rest of it.
                RecordSkippedAttachment(part.FileName, "it could not be queued");
                logger.LogError(ex, "Failed to enqueue attachment {FileName} for ingest {IngestId}.",
                    part.FileName, ingest.Id);
            }
        }

        // ING-06: the skip list is attached to the body job's provenance BEFORE the body branch
        // is evaluated, and recorded on the EmailIngest UNCONDITIONALLY below.
        //
        // It used to be assigned inside the `else` of "did the body carry fresh text?". A
        // quoted-only reply carrying one supported attachment and one unsupported one (a
        // forwarded .msg, say) therefore recorded the dropped file NOWHERE: no metadata, no
        // ingest column, and — because a job WAS enqueued — not even the "nothing queued"
        // ParseStatus summary. The loss existed only as a Warning in a log nobody reads.
        if (skippedAttachments.Count > 0)
            metadata.SkippedAttachments = skippedAttachments.ToArray();
        RecordSkippedAttachments(ingest, skippedAttachments);

        // 2) The body — the sender's OWN words only. A body that is nothing but a quoted
        //    thread produces no job; its attachments (above) still do.
        try
        {
            var body = bodyParts.Fresh;
            if (string.IsNullOrWhiteSpace(body))
            {
                logger.LogInformation(
                    "Email body for ingest {IngestId} carried no fresh text after quote stripping "
                    + "(thread depth {Depth}); body job skipped.", ingest.Id, bodyParts.ThreadDepth);
            }
            else
            {
                var bodyDocument =
                    $"Subject: {message.Subject}\nFrom: {message.From}\nDate: {message.Date:yyyy-MM-dd}\n\n{body}";
                var bodyName = $"{SanitizeFileName(message.Subject ?? "email")}_body.txt";
                var bodyResult = await ingestion.IngestAsync(
                    Encoding.UTF8.GetBytes(bodyDocument), bodyName, businessUnitId,
                    ExtractionSourceType.Email, batchId, priority: 0, metadata, ct);
                queued++;
                logger.LogInformation("Enqueued email body as job {JobId} ({Outcome}) for ingest {IngestId}.",
                    bodyResult.JobId, bodyResult.Outcome, ingest.Id);
            }
        }
        catch (EvidenceStorageUnavailableException ex)
        {
            logger.LogError(ex,
                "Durable evidence storage is unavailable while enqueueing the email body for ingest {IngestId} "
                + "(configuration fault: {IsConfigurationFault}). No job was created; the message stays unprocessed "
                + "and is not lost.", ingest.Id, ex.IsConfigurationFault);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to enqueue email body for ingest {IngestId}.", ingest.Id);
        }

        return new EmailEnqueueResult(batchId, queued, skippedAttachments);
    }

    // ------------------------------------------------------------------------------------
    // STEP 2 — the durable canonical scheduler.
    //
    // MIGRATION NOTE: EnqueueAsync above is the legacy fan-out and still walks
    // message.Attachments. It is deleted in the next increment, together with its two call
    // sites, because removing it breaks both at compile time and they must land atomically.
    // Nothing new calls it. This is a migration, not a competing implementation.
    // ------------------------------------------------------------------------------------

    /// <summary>Reason recorded when the stored original stops matching what was captured.</summary>
    public const string ManifestMismatchReason = "manifest_mismatch";

    /// <summary>Reason recorded when the durable queue refused the component.</summary>
    public const string SchedulingFailedReason = "scheduling_failed";

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
            if (component.ExtractionJobId is { } existingJobId && !component.IsRecoverableHold)
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
                    BuildMetadata(assembly, component, ingest, clientEmail, triage), ct);

                await coordinator.RecordComponentQueuedAsync(
                    assembly.BusinessUnitId, assembly.Id, component.ComponentKey, result.JobId, ct);
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
