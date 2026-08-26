using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using ERP_RFQ_Automation.CommercialDocuments;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Lifecycle;
using ERP_RFQ_Automation.PlatformGovernance;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace ERP_RFQ_Automation.Retention;

/// <summary>
/// Purges the BYTES of documents Nexora has finished extracting, and keeps the RECORD.
///
/// <para><b>Why this shape.</b> The database physically refuses to delete evidence:
/// <c>trg_source_documents_no_delete</c> raises 55000 on any DELETE from
/// <c>source_documents</c>, and <c>trg_protect_source_document_identity</c> freezes each
/// document's hash, filename, size and creation time against modification. Those triggers
/// are the asset, not the obstacle — they are what makes "we deleted the file" auditable
/// rather than indistinguishable from "we lost the file". Retention therefore operates only
/// on stored bytes: the row, its SHA-256 fingerprint, its page and region evidence, its
/// field evidence and its lead all survive, so lineage still answers <i>this value came
/// from document X, hash Y, purged on DATE under policy N by user Z</i>.</para>
///
/// <para><b>Ordering is the design.</b> Byte deletion is never inside a database
/// transaction. Intent is committed first (<c>Present -> PurgeRequested</c>), then bytes are
/// deleted, then completion is committed (<c>PurgeRequested -> Purged</c>). The forbidden
/// state — a row claiming bytes that no longer exist — is therefore structurally impossible.
/// The tolerable state — intent recorded, bytes still present — self-heals on the next run,
/// because a delete that finds nothing is success.</para>
///
/// <para><b>What this is NOT.</b> It is not erasure. Personal data was copied out of the
/// bytes into <c>document_regions</c>, <c>field_evidence</c> and the lead during extraction
/// and survives untouched. Every result carries
/// <see cref="EvidenceRetentionDisclosure.NotErasure"/> so no surface can imply otherwise.</para>
/// </summary>
public sealed class EvidenceRetentionService(
    ErpRfqAutomationContext db,
    IEvidenceObjectStorage storage,
    LegacyAttachmentPurgeResolver legacyCopies,
    CommercialDocumentArchiveService archive,
    ILogger<EvidenceRetentionService> log,
    IDataProtectionProvider? dataProtection = null)
{
    private const string Area = "EvidenceRetention";
    private const int BatchSize = 200;
    private static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(10);
    private readonly IDataProtector previewProtector =
        (dataProtection ?? new EphemeralDataProtectionProvider())
        .CreateProtector("Nexora.EvidenceRetention.PurgePreview.v1");

    public const string ActionPolicyUpdated = "RETENTION_POLICY_UPDATED";
    public const string ActionPurgeRun = "EVIDENCE_BYTES_PURGE_RUN";
    public const string ActionPurgeRequested = "EVIDENCE_BYTES_PURGE_REQUESTED";
    public const string ActionPurged = "EVIDENCE_BYTES_PURGED";
    public const string ActionBytesAbsent = "EVIDENCE_BYTES_ABSENT";
    public const string ActionLegacyUnresolved = "LEGACY_COPY_UNRESOLVED";

    /// <summary>
    /// What the run decided to KEEP, and why, rolled up by reason.
    ///
    /// <para>Its own event rather than a field on the run event, because it answers a different
    /// question and gets asked on its own. Until now the audit trail answered "what did you
    /// delete"; a tenant asked to account for his own data has to answer "what did you decide to
    /// keep, and on whose authority" — and the reasons were already computed, then thrown away
    /// into a purge-time footnote nobody re-reads.</para>
    /// </summary>
    public const string ActionRunKept = "EVIDENCE_BYTES_PURGE_KEPT";

    // ---------------------------------------------------------------- read

    public async Task<EvidenceRetentionView> GetAsync(long tenantId, CancellationToken ct)
    {
        PlatformGovernanceService.EnsureTenant(tenantId);
        var policy = await LoadPolicyAsync(tenantId, ct)
            ?? EvidenceRetentionPolicy.Default(tenantId, DateTime.UtcNow);
        var storageView = await MeasureAsync(tenantId, policy, ct);
        return new EvidenceRetentionView(Map(policy), storageView);
    }

    /// <summary>
    /// Tenant-visible storage figures, derived entirely from the tenant's own rows.
    ///
    /// <para>Used bytes counts each document TWICE because ingestion writes it twice — once
    /// to the quarantine zone before inspection, once to the cleared zone after it — and
    /// nothing has ever deleted either copy. Reporting the single logical size would
    /// understate real consumption by half and make the reclaim figure look like an
    /// exaggeration when it is the plain truth. Legacy non-content-addressed attachment
    /// copies are added on top from their own recorded sizes.</para>
    ///
    /// <para>Drive free space is deliberately not exposed here: on a shared volume it
    /// reports other tenants' consumption.</para>
    /// </summary>
    private async Task<EvidenceRetentionStorageView> MeasureAsync(
        long tenantId, EvidenceRetentionPolicy policy, CancellationToken ct)
    {
        var present = db.Set<SourceDocument>().AsNoTracking()
            .Where(x => x.BusinessUnitId == tenantId && x.PurgeState == EvidencePurgeState.Present);

        var documentCount = await present.CountAsync(ct);
        var logicalBytes = documentCount == 0 ? 0 : await present.SumAsync(x => x.ByteSize, ct);
        var clearedBytes = documentCount == 0
            ? 0
            : await present.Where(x => x.SecurityStatus == DocumentSecurityStatus.Cleared)
                .SumAsync(x => x.ByteSize, ct);
        var purgedCount = await db.Set<SourceDocument>().AsNoTracking()
            .CountAsync(x => x.BusinessUnitId == tenantId && x.PurgeState != EvidencePurgeState.Present, ct);

        var attachmentBytes = await LegacyAttachmentBytesAsync(tenantId, ct);

        var eligible = await EligibleAsync(tenantId, policy, DateTime.UtcNow, ct);
        var reclaimableEvidence = eligible.Sum(x => x.ByteSize
            + (x.SecurityStatus == DocumentSecurityStatus.Cleared ? x.ByteSize : 0));
        var reclaimableLegacy = await legacyCopies.EstimateLegacyBytesAsync(
            tenantId, eligible.Select(x => x.Id).ToList(), ct);

        return new EvidenceRetentionStorageView(
            UsedBytes: logicalBytes + clearedBytes + attachmentBytes,
            ReclaimableBytes: reclaimableEvidence + reclaimableLegacy,
            DocumentCount: documentCount,
            PurgedCount: purgedCount,
            ReclaimableDocumentCount: eligible.Count);
    }

    private async Task<long> LegacyAttachmentBytesAsync(long tenantId, CancellationToken ct)
    {
        var leadIds = db.Leads.AsNoTracking().Where(x => x.BusinessUnitId == tenantId).Select(x => x.Id);
        return await db.Attachments.AsNoTracking()
            .Where(x => x.ParentType == "Lead" && leadIds.Contains(x.ParentId)
                && !x.FilePath.StartsWith(LegacyAttachmentPurgeResolver.TombstonePrefix))
            .SumAsync(x => x.FileSize ?? 0, ct);
    }

    // ---------------------------------------------------------------- policy

    public async Task<EvidenceRetentionView> UpdatePolicyAsync(
        long tenantId, long actorUserId, string idempotencyKey,
        UpdateEvidenceRetentionPolicyCommand command, CancellationToken ct)
    {
        PlatformGovernanceService.EnsureActor(tenantId, actorUserId);
        idempotencyKey = PlatformGovernanceService.Required(idempotencyKey, 160, "Idempotency-Key is required.");
        var reason = PlatformGovernanceService.Required(command.Reason, 1000,
            "A reason is required for a retention policy change.");
        if (command.RetentionDays < EvidenceRetentionPolicy.MinimumRetentionDays
            || command.RetentionDays > EvidenceRetentionPolicy.MaximumRetentionDays)
            throw new PlatformGovernanceValidationException(
                $"Retention must be between {EvidenceRetentionPolicy.MinimumRetentionDays} and "
                + $"{EvidenceRetentionPolicy.MaximumRetentionDays} days. The floor exists so a tenant "
                + "cannot configure away the dispute and re-extraction window.");

        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            if (await ReplayAsync(tenantId, idempotencyKey, ct) is not null)
            {
                await tx.RollbackAsync(ct);
                return await GetAsync(tenantId, ct);
            }

            var now = DateTime.UtcNow;
            var policy = await db.EvidenceRetentionPolicies
                .SingleOrDefaultAsync(x => x.BusinessUnitId == tenantId, ct);
            var before = policy is null ? null : Map(policy);
            if (policy is null)
            {
                policy = EvidenceRetentionPolicy.Default(tenantId, now);
                db.EvidenceRetentionPolicies.Add(policy);
            }

            policy.RetentionDays = EvidenceRetentionPolicy.Clamp(command.RetentionDays);
            policy.IsEnabled = command.IsEnabled;
            policy.Version++;
            policy.UpdatedByUserId = actorUserId;
            policy.UpdatedOn = now;

            db.TenantGovernanceAuditEvents.Add(new TenantGovernanceAuditEvent
            {
                BusinessUnitId = tenantId,
                Area = Area,
                AggregateType = "EvidenceRetentionPolicy",
                AggregateReference = $"tenant:{tenantId}",
                Action = ActionPolicyUpdated,
                Reason = reason,
                EvidenceJson = JsonSerializer.Serialize(new { before, after = Map(policy) }),
                IdempotencyKey = idempotencyKey,
                ActorUserId = actorUserId,
                OccurredOn = now
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            db.ChangeTracker.Clear();
            return await GetAsync(tenantId, ct);
        });
    }

    // ---------------------------------------------------------------- purge

    /// <summary>
    /// Runs (or simulates) a purge for one tenant.
    ///
    /// <para>A dry run opens no file handle for writing and commits no state change; it
    /// walks exactly the same selection query and the same byte-accounting code as the real
    /// run, so the number the confirmation screen shows is the number the real run will
    /// produce. That equivalence is the point — an estimate produced by different code from
    /// the deletion is not a confirmation, it is a guess.</para>
    /// </summary>
    public async Task<EvidenceRetentionPurgeResult> RunPurgeAsync(
        long tenantId, long actorUserId, string idempotencyKey,
        EvidenceRetentionPurgeCommand command, CancellationToken ct)
    {
        PlatformGovernanceService.EnsureActor(tenantId, actorUserId);
        idempotencyKey = PlatformGovernanceService.Required(idempotencyKey, 160, "Idempotency-Key is required.");
        var reason = PlatformGovernanceService.Required(command.Reason, 1000,
            "A reason is required: an irreversible deletion must say why it happened.");

        var policy = await LoadPolicyAsync(tenantId, ct)
            ?? EvidenceRetentionPolicy.Default(tenantId, DateTime.UtcNow);

        if (!command.IsDryRun && !policy.IsEnabled)
            throw new PlatformGovernanceConflictException(
                "Evidence retention is not enabled for this tenant. Irreversible deletion is opt-in: "
                + "save a retention policy first, then run the purge.");

        if (!command.IsDryRun)
        {
            var replay = await ReplayAsync(tenantId, idempotencyKey, ct);
            if (replay is not null)
                return Deserialize(replay.EvidenceJson) with { IdempotentReplay = true };
        }

        var now = DateTime.UtcNow;
        PurgePreviewEnvelope? preview = null;
        if (!command.IsDryRun)
            preview = ReadPreview(command.PreviewToken, tenantId, actorUserId, policy, now);

        var scanned = command.IsDryRun
            ? await AgeEligibleQuery(tenantId, policy, now).CountAsync(ct)
            : preview!.Scanned;
        var eligible = await EligibleAsync(
            tenantId,
            policy,
            now,
            ct,
            command.IsDryRun ? null : preview!.DocumentIds);

        if (!command.IsDryRun)
        {
            var currentIds = eligible.Select(x => x.Id).Order().ToArray();
            var previewIds = preview!.DocumentIds.Order().ToArray();
            if (!currentIds.SequenceEqual(previewIds))
                throw new PlatformGovernanceConflictException(
                    "The protected-document or eligibility state changed after this preview. Nothing was "
                    + "deleted. Run a new preview and confirm the current document set.");
        }
        var skipped = await SkipReportAsync(tenantId, policy, now, eligible, ct);

        if (!command.IsDryRun)
            await CompleteInterruptedPurgesAsync(tenantId, actorUserId, ct);

        var purged = 0;
        long bytesReclaimed = 0;
        var legacyDeleted = 0;
        var legacyUnresolved = 0;

        foreach (var candidate in eligible)
        {
            ct.ThrowIfCancellationRequested();
            if (command.IsDryRun)
            {
                purged++;
                bytesReclaimed += await EstimateDocumentBytesAsync(tenantId, candidate, ct);
                continue;
            }

            var outcome = await PurgeOneAsync(tenantId, actorUserId, policy, reason, candidate, ct);
            purged++;
            bytesReclaimed += outcome.BytesFreed;
            legacyDeleted += outcome.LegacyDeleted;
            legacyUnresolved += outcome.LegacyUnresolved;
        }

        DateTimeOffset? previewExpiresOn = command.IsDryRun
            ? DateTimeOffset.UtcNow.Add(PreviewLifetime)
            : null;
        var previewToken = command.IsDryRun
            ? ProtectPreview(new PurgePreviewEnvelope(
                tenantId,
                actorUserId,
                policy.Version,
                scanned,
                eligible.Select(x => x.Id).Order().ToArray(),
                previewExpiresOn!.Value))
            : null;

        var result = new EvidenceRetentionPurgeResult(
            command.IsDryRun, scanned, eligible.Count, purged, bytesReclaimed,
            legacyDeleted, legacyUnresolved, skipped,
            EvidenceRetentionDisclosure.For(command.IsDryRun, purged, bytesReclaimed), false,
            previewToken, previewExpiresOn);

        if (!command.IsDryRun)
        {
            await RecordRunAsync(tenantId, actorUserId, idempotencyKey, reason, policy, result, ct);
            await RecordKeptAsync(tenantId, actorUserId, idempotencyKey, reason, policy, result, ct);
        }
        return result;
    }

    private sealed record DocumentPurgeOutcome(long BytesFreed, int LegacyDeleted, int LegacyUnresolved);

    private sealed record PurgePreviewEnvelope(
        long TenantId,
        long ActorUserId,
        int PolicyVersion,
        int Scanned,
        long[] DocumentIds,
        DateTimeOffset ExpiresOn);

    private string ProtectPreview(PurgePreviewEnvelope preview) =>
        previewProtector.Protect(JsonSerializer.Serialize(preview, JsonOptions));

    private PurgePreviewEnvelope ReadPreview(
        string? token,
        long tenantId,
        long actorUserId,
        EvidenceRetentionPolicy policy,
        DateTime now)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new PlatformGovernanceValidationException(
                "A signed purge preview is required before permanent deletion. Run a new preview first.");

        try
        {
            var preview = JsonSerializer.Deserialize<PurgePreviewEnvelope>(
                previewProtector.Unprotect(token), JsonOptions)
                ?? throw new CryptographicException("The preview payload is empty.");

            if (preview.TenantId != tenantId || preview.ActorUserId != actorUserId)
                throw new PlatformGovernanceValidationException(
                    "This purge preview belongs to a different tenant or user. Run your own preview.");
            if (preview.PolicyVersion != policy.Version)
                throw new PlatformGovernanceConflictException(
                    "The retention policy changed after this preview. Nothing was deleted. Run a new preview.");
            if (preview.ExpiresOn <= new DateTimeOffset(now, TimeSpan.Zero))
                throw new PlatformGovernanceConflictException(
                    "This purge preview expired. Nothing was deleted. Run a new preview to confirm the current state.");
            if (preview.DocumentIds.Length > BatchSize || preview.DocumentIds.Any(x => x <= 0)
                || preview.DocumentIds.Distinct().Count() != preview.DocumentIds.Length)
                throw new CryptographicException("The preview candidate set is invalid.");

            return preview;
        }
        catch (PlatformGovernanceValidationException) { throw; }
        catch (PlatformGovernanceConflictException) { throw; }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            throw new PlatformGovernanceValidationException(
                "The purge preview is invalid or no longer usable. Nothing was deleted. Run a new preview.");
        }
    }

    private async Task<DocumentPurgeOutcome> PurgeOneAsync(
        long tenantId, long actorUserId, EvidenceRetentionPolicy policy, string reason,
        PurgeCandidate candidate, CancellationToken ct)
    {
        // --- Phase A: commit the INTENT before touching a byte. If the process dies after
        // this and before phase C the row says PurgeRequested, which is honest and
        // recoverable. The reverse ordering would produce a row promising bytes that no
        // longer exist, which nothing could repair.
        await RecordIntentAsync(tenantId, actorUserId, policy, reason, candidate, ct);

        return await DeleteAndConfirmUnderLegalHoldFenceAsync(
            tenantId, actorUserId, policy.PolicyCode, PolicyEvidence(policy), reason, candidate, ct);
    }

    private static string PurgeReasonFor(long freed) => freed > 0
        ? EvidenceRetentionEligibility.ReasonRetentionElapsed
        : EvidenceRetentionEligibility.ReasonBytesAlreadyAbsent;

    /// <summary>
    /// Linearizes irreversible byte deletion with tenant legal-hold placement. The database
    /// session lock spans the non-transactional storage delete: hold-first makes this recheck fail;
    /// deletion-first makes hold placement wait until the deletion and its tombstone are complete.
    /// </summary>
    private async Task<DocumentPurgeOutcome> DeleteAndConfirmUnderLegalHoldFenceAsync(
        long tenantId, long actorUserId, string policyCode, object policyEvidence,
        string reason, PurgeCandidate candidate, CancellationToken ct)
    {
        await using var holdFence = await AcquireLegalHoldDeletionFenceAsync(tenantId, ct);

        // Storage deletion remains outside a database transaction because it cannot roll back.
        // The session-level legal-hold fence, unlike a transaction, is intentionally held across it.
        var (freed, objects, legacy) = await DeleteBytesAsync(tenantId, candidate, ct);

        await ConfirmAsync(tenantId, actorUserId, policyCode, policyEvidence,
            reason, candidate, freed, PurgeReasonFor(freed), objects, legacy, ct);

        return new DocumentPurgeOutcome(freed,
            legacy.Copies.Count(x => x.Result == LegacyAttachmentPurgeResolver.ResultDeleted),
            legacy.Unresolved);
    }

    internal async Task<IAsyncDisposable> AcquireLegalHoldDeletionFenceAsync(
        long businessUnitId, CancellationToken ct)
    {
        var platformTenantId = await TenantLegalHoldFence.ResolvePlatformTenantIdAsync(db, businessUnitId, ct)
            ?? throw new PlatformGovernanceConflictException(
                $"Evidence deletion is blocked because business unit {businessUnitId} cannot be resolved "
                + "to a platform tenant for legal-hold verification.");

        var holdFence = await TenantLegalHoldFence.AcquireSessionAsync(db, platformTenantId, ct);
        try
        {
            if (await TenantLegalHoldFence.HasActiveAsync(db, platformTenantId, ct))
                throw new PlatformGovernanceConflictException(
                    "Evidence deletion is blocked by an active tenant legal hold. Release the hold "
                    + "through the governed platform workflow before retrying.");
            return holdFence;
        }
        catch
        {
            await holdFence.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Deletes both content-addressed zone objects and every resolvable legacy copy, and
    /// reports what each one actually did. Shared by the normal path and the resume path so
    /// an interrupted purge cannot finish by a different route than it started on.
    /// </summary>
    private async Task<(long Freed, List<object> Objects, LegacyPurgeReport Legacy)> DeleteBytesAsync(
        long tenantId, PurgeCandidate candidate, CancellationToken ct)
    {
        var objects = new List<object>();
        long freed = 0;
        foreach (var key in EvidenceRetentionEligibility.ZoneKeysFor(candidate.ObjectKey))
        {
            EvidenceObjectPurgeResult purge;
            try
            {
                purge = await storage.TryDeletePurgedObjectAsync(
                    candidate.ObjectBucket, key, candidate.ObjectVersion, ct);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException
                                                  or InvalidDataException
                                                  or NotSupportedException)
            {
                // Containment refusal or a provider that cannot delete. Recorded in the
                // tombstone, never swallowed and never worked around.
                log.LogWarning(exception,
                    "Refused to purge evidence object {Key} for document {DocumentId}.",
                    key, candidate.Id);
                objects.Add(new { key, outcome = "REFUSED", detail = exception.Message, bytesFreed = 0 });
                continue;
            }

            freed += purge.BytesFreed;
            objects.Add(new
            {
                key,
                outcome = purge.Deleted ? "DELETED" : "ALREADY_ABSENT",
                bytesFreed = purge.BytesFreed
            });
        }

        var legacy = await legacyCopies.PurgeCopiesAsync(tenantId, candidate.Id,
            candidate.OriginalFileName, candidate.ByteSize, dryRun: false, ct);
        return (freed + legacy.BytesFreed, objects, legacy);
    }

    private Task RecordIntentAsync(long tenantId, long actorUserId, EvidenceRetentionPolicy policy,
        string reason, PurgeCandidate candidate, CancellationToken ct) =>
        db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var document = await db.Set<SourceDocument>()
                .SingleAsync(x => x.BusinessUnitId == tenantId && x.Id == candidate.Id, ct);
            if (document.PurgeState != EvidencePurgeState.Present)
            {
                await tx.RollbackAsync(ct);
                return;
            }

            document.RequestPurge(policy.PolicyCode, actorUserId);
            db.TenantGovernanceAuditEvents.Add(Audit(tenantId, actorUserId, candidate.Id,
                ActionPurgeRequested, reason,
                $"evidence-purge:{tenantId}:{candidate.Id}:{policy.PolicyCode}",
                new
                {
                    policy = PolicyEvidence(policy),
                    document = DocumentEvidence(candidate),
                    eligibility = candidate.Eligibility,
                    actor = new { userId = actorUserId, mode = "TENANT_INITIATED" },
                    irreversible = true
                }));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });

    private Task ConfirmAsync(long tenantId, long actorUserId, string policyCode,
        object policyEvidence, string reason, PurgeCandidate candidate, long freed,
        string purgeReason, IReadOnlyList<object> objects, LegacyPurgeReport legacy,
        CancellationToken ct) =>
        db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var document = await db.Set<SourceDocument>()
                .SingleAsync(x => x.BusinessUnitId == tenantId && x.Id == candidate.Id, ct);
            if (document.PurgeState == EvidencePurgeState.Purged)
            {
                await tx.RollbackAsync(ct);
                return;
            }

            document.ConfirmPurged(freed, purgeReason);
            var retained = await RetainedEvidenceAsync(tenantId, candidate, ct);
            db.TenantGovernanceAuditEvents.Add(Audit(tenantId, actorUserId, candidate.Id,
                freed > 0 ? ActionPurged : ActionBytesAbsent, reason,
                $"evidence-purged:{tenantId}:{candidate.Id}:{policyCode}",
                new
                {
                    policy = policyEvidence,
                    document = DocumentEvidence(candidate),
                    objects,
                    legacyCopies = legacy.Copies,
                    legacyUnresolved = legacy.Unresolved,
                    retained,
                    eligibility = candidate.Eligibility,
                    actor = new { userId = actorUserId, mode = "TENANT_INITIATED" },
                    purgedOn = DateTime.UtcNow,
                    bytesFreed = freed,
                    purgeReason,
                    irreversible = true,
                    notErasure = EvidenceRetentionDisclosure.NotErasure
                }));

            if (legacy.Unresolved > 0)
                db.TenantGovernanceAuditEvents.Add(Audit(tenantId, actorUserId, candidate.Id,
                    ActionLegacyUnresolved,
                    "Legacy attachment copies could not be matched unambiguously and were left in place.",
                    $"evidence-purge-legacy:{tenantId}:{candidate.Id}:{policyCode}",
                    new { candidate.OriginalFileName, candidate.ByteSize, legacy.Unresolved }));

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });

    /// <summary>
    /// Finishes any document left in <see cref="EvidencePurgeState.PurgeRequested"/> by an
    /// interrupted run. Re-attempting the delete is safe because an absent object is
    /// success, so this converges rather than erroring.
    /// </summary>
    private async Task CompleteInterruptedPurgesAsync(long tenantId, long actorUserId, CancellationToken ct)
    {
        var stalled = await db.Set<SourceDocument>().AsNoTracking()
            .Where(x => x.BusinessUnitId == tenantId && x.PurgeState == EvidencePurgeState.PurgeRequested)
            .OrderBy(x => x.CreatedOn).Take(BatchSize)
            .Select(x => new PurgeCandidate(x.Id, x.OriginalFileName, x.ContentHash, x.ByteSize,
                x.ObjectBucket, x.ObjectKey, x.ObjectVersion, x.SecurityStatus, x.ProcessingStatus,
                x.CorpusId, x.CreatedOn, "resumed", x.PurgePolicyCode))
            .ToListAsync(ct);

        foreach (var candidate in stalled)
        {
            // The tombstone cites the policy the purge was STARTED under — read off the row
            // where phase A stamped it — not whatever the tenant's policy happens to say now.
            // "Under which rule was this destroyed?" must not be re-answerable after the fact.
            var policyCode = candidate.RecordedPolicyCode ?? "retention/unrecorded";
            await DeleteAndConfirmUnderLegalHoldFenceAsync(tenantId, actorUserId, policyCode,
                new { code = policyCode, mode = "RESUMED_AFTER_INTERRUPTION" },
                "Completing a purge interrupted before its bytes were confirmed.", candidate, ct);
        }
    }

    /// <summary>
    /// Bytes a real run would free for this document — measured against the same objects it
    /// would delete, never derived from the recorded size. A document whose bytes are
    /// already gone contributes zero, so the confirmation screen cannot promise space that
    /// was lost years ago.
    /// </summary>
    private async Task<long> EstimateDocumentBytesAsync(
        long tenantId, PurgeCandidate candidate, CancellationToken ct)
    {
        long estimate = 0;
        foreach (var key in EvidenceRetentionEligibility.ZoneKeysFor(candidate.ObjectKey))
        {
            try
            {
                estimate += await storage.TryMeasureObjectAsync(
                    candidate.ObjectBucket, key, candidate.ObjectVersion, ct) ?? 0;
            }
            catch (Exception exception) when (exception is IOException
                                                  or UnauthorizedAccessException
                                                  or InvalidDataException
                                                  or ArgumentException
                                                  or NotSupportedException)
            {
                log.LogDebug(exception, "Could not measure evidence object {Key} during a dry run.", key);
            }
        }

        var legacy = await legacyCopies.PurgeCopiesAsync(tenantId, candidate.Id,
            candidate.OriginalFileName, candidate.ByteSize, dryRun: true, ct);
        return estimate + legacy.BytesFreed;
    }

    // ---------------------------------------------------------------- selection

    private sealed record PurgeCandidate(
        long Id, string OriginalFileName, string ContentHash, long ByteSize,
        string ObjectBucket, string ObjectKey, string ObjectVersion,
        DocumentSecurityStatus SecurityStatus, DocumentProcessingStatus ProcessingStatus,
        long CorpusId, DateTimeOffset CreatedOn, string Eligibility,
        string? RecordedPolicyCode = null);

    /// <summary>
    /// Documents old enough to consider. Kept separate from full eligibility so "scanned"
    /// reports the real size of the pool and the skip report can explain the difference.
    /// </summary>
    private IQueryable<SourceDocument> AgeEligibleQuery(
        long tenantId, EvidenceRetentionPolicy policy, DateTime now)
    {
        var cutoff = new DateTimeOffset(now, TimeSpan.Zero).AddDays(-policy.RetentionDays);
        return db.Set<SourceDocument>().AsNoTracking()
            .Where(x => x.BusinessUnitId == tenantId
                && x.PurgeState == EvidencePurgeState.Present
                && x.CreatedOn < cutoff
                // Completed extraction is the purpose being satisfied; a security rejection
                // means there is nothing to extract and the bytes are a liability. Pending
                // and Quarantined are excluded — quarantined bytes ARE the malware evidence.
                && (x.ProcessingStatus == DocumentProcessingStatus.Completed
                        && x.SecurityStatus == DocumentSecurityStatus.Cleared
                    || x.SecurityStatus == DocumentSecurityStatus.Rejected));
    }

    /// <summary>
    /// THE selection query. Every exclusion is applied here, in SQL, before a single
    /// candidate reaches code that can delete anything. An excluded document is not merely
    /// hidden from the UI — it cannot be returned to the purge loop at all.
    /// </summary>
    private async Task<List<PurgeCandidate>> EligibleAsync(
        long tenantId, EvidenceRetentionPolicy policy, DateTime now, CancellationToken ct,
        IReadOnlyCollection<long>? restrictToDocumentIds = null)
    {
        if (await TenantHoldBlocksAsync(tenantId, ct))
            return [];

        var blockedByGovernance = await BlockedDocumentIdsAsync(tenantId, ct);
        var blockedByHumanAction = await OpenHumanActionDocumentIdsAsync(tenantId, ct);
        var statutoryTypes = EvidenceRetentionEligibility.StatutoryDocumentTypes.ToArray();
        var terminalIntake = EvidenceRetentionEligibility.TerminalIntakeStatuses.ToArray();
        var openInquiryStatuses = EvidenceRetentionEligibility.OpenInquiryStatuses.ToArray();
        var openLeadIds = OpenLeadIdsQuery(tenantId);

        var restrictedIds = restrictToDocumentIds?.ToArray();
        var query = AgeEligibleQuery(tenantId, policy, now)
            .Where(d => restrictedIds == null || restrictedIds.Contains(d.Id))
            .Where(d => !blockedByGovernance.Contains(d.Id))
            .Where(d => !blockedByHumanAction.Contains(d.Id))
            // Statutory retention beats tenant preference, always.
            .Where(d => !db.CommercialDocumentClassifications.Any(c =>
                c.BusinessUnitId == tenantId && c.SourceDocumentId == d.Id
                && statutoryTypes.Contains(c.DocumentType)))
            // Any occurrence still mid-intake keeps the bytes, including dead-lettered ones:
            // they are replayable from stored bytes and purging would make them permanently
            // unrecoverable.
            .Where(d => !db.Set<SourceDocumentOccurrence>().Any(o =>
                o.BusinessUnitId == tenantId && o.SourceDocumentId == d.Id
                && !terminalIntake.Contains(o.IntakeStatus)))
            // Any bound extraction job that has not succeeded can still be retried, and a
            // retry needs the source bytes.
            .Where(d => !db.Set<ExtractionJob>().Any(j =>
                j.BusinessUnitId == tenantId
                && j.Status != EvidenceRetentionEligibility.TerminalExtractionStatus
                && (j.Id == d.ExtractionJobId
                    || db.Set<SourceDocumentOccurrence>().Any(o =>
                        o.BusinessUnitId == tenantId && o.SourceDocumentId == d.Id
                        && o.ExtractionJobId == j.Id))))
            // A draft or review-required inquiry means a human is still expected to open
            // the original.
            .Where(d => !db.Set<CanonicalInquiry>().Any(i =>
                i.BusinessUnitId == tenantId && i.CorpusId == d.CorpusId
                && openInquiryStatuses.Contains(i.Status)))
            // A live commercial case still needs the document producible.
            .Where(d => !db.Set<LeadIngestionOccurrence>().Any(l =>
                l.BusinessUnitId == tenantId && l.SourceDocumentId == d.Id
                && l.LeadId.HasValue && openLeadIds.Contains(l.LeadId.Value)))
            .OrderBy(d => d.CreatedOn).ThenBy(d => d.Id)
            .Select(d => new PurgeCandidate(d.Id, d.OriginalFileName, d.ContentHash, d.ByteSize,
                d.ObjectBucket, d.ObjectKey, d.ObjectVersion, d.SecurityStatus, d.ProcessingStatus,
                d.CorpusId, d.CreatedOn, "selected", d.PurgePolicyCode));

        return restrictToDocumentIds is null
            ? await query.Take(BatchSize).ToListAsync(ct)
            : await query.ToListAsync(ct);
    }

    /// <summary>Leads that are not in a terminal state. A lead with NO status counts as
    /// open: unknown must never resolve to "safe to delete".</summary>
    private IQueryable<long> OpenLeadIdsQuery(long tenantId)
    {
        var terminalCodes = EvidenceRetentionEligibility.TerminalLeadStatusCodes.ToArray();
        // Matched on the governed status CODE, not the display label, and normalised the same
        // way LifecycleStatusCatalog.ResolveIdAsync does so tenants carrying legacy
        // "Lead Status" rows are read identically.
        var terminalStatusIds = db.SetupMasters.AsNoTracking()
            .Where(s => s.BusinessUnitId == tenantId
                && s.SetupType.ToLower().Replace(" ", "") == "leadstatus"
                && s.SetupCode != null && terminalCodes.Contains(s.SetupCode.ToUpper()))
            .Select(s => s.SetupId);
        return db.Leads.AsNoTracking()
            .Where(l => l.BusinessUnitId == tenantId
                && (l.LeadStatusId == null || !terminalStatusIds.Contains(l.LeadStatusId.Value)))
            .Select(l => l.Id);
    }

    /// <summary>
    /// Documents frozen by the archive governance log. Hold lives on the occurrence, but bytes
    /// are shared content-addressed per document, so a hold on ANY occurrence blocks the
    /// underlying document. There is deliberately no hold column: a second source of truth for
    /// whether evidence is frozen is itself an audit finding.
    ///
    /// <para><b>A deletion request is no longer a block.</b> It used to be: a document with an
    /// open DELETION_REQUESTED event was excluded from the purge until the request was approved —
    /// and nothing anywhere could approve one. Clicking "request deletion review" therefore
    /// removed the document from the only deletion a tenant could actually perform, which is the
    /// exact inverse of what the button said. The action has been removed from
    /// <see cref="CommercialDocumentArchiveService"/>, and the documents already stuck behind an
    /// unapprovable request return to ordinary eligibility here — which honours what the tenant
    /// asked for in the first place, rather than leaving it frozen forever.</para>
    /// </summary>
    private sealed record GovernanceBlocks(List<long> LegalHold, List<long> DeletionRequested)
    {
        public IEnumerable<long> All => LegalHold;
    }

    private async Task<GovernanceBlocks> GovernanceBlocksAsync(long tenantId, CancellationToken ct)
    {
        var state = await archive.GovernanceStateAsync(tenantId, ct);
        var held = state.Values.Where(x => x.LegalHold).Select(x => x.OccurrenceId).ToArray();
        var pendingDeletion = state.Values
            .Where(x => !x.LegalHold && x.DeletionRequested)
            .Select(x => x.OccurrenceId).ToArray();
        if (held.Length == 0 && pendingDeletion.Length == 0)
            return new GovernanceBlocks([], []);

        var occurrences = await db.Set<SourceDocumentOccurrence>().AsNoTracking()
            .Where(x => x.BusinessUnitId == tenantId
                && (held.Contains(x.Id) || pendingDeletion.Contains(x.Id)))
            .Select(x => new { x.Id, x.SourceDocumentId })
            .ToListAsync(ct);

        return new GovernanceBlocks(
            occurrences.Where(x => held.Contains(x.Id)).Select(x => x.SourceDocumentId).Distinct().ToList(),
            occurrences.Where(x => pendingDeletion.Contains(x.Id)).Select(x => x.SourceDocumentId)
                .Distinct().ToList());
    }

    private async Task<List<long>> BlockedDocumentIdsAsync(long tenantId, CancellationToken ct)
    {
        var blocks = await GovernanceBlocksAsync(tenantId, ct);
        return blocks.All.Distinct().ToList();
    }

    private async Task<List<long>> OpenHumanActionDocumentIdsAsync(long tenantId, CancellationToken ct)
    {
        var references = await db.HumanActionItems.AsNoTracking()
            .Where(x => x.BusinessUnitId == tenantId && x.Status == HumanActionStatus.Open
                && x.SourceReference.StartsWith("occurrence:"))
            .Select(x => x.SourceReference)
            .ToListAsync(ct);

        var occurrenceIds = references
            .Select(x => long.TryParse(x["occurrence:".Length..], out var id) ? id : 0)
            .Where(x => x > 0)
            .Distinct()
            .ToArray();
        if (occurrenceIds.Length == 0)
            return [];

        return await db.Set<SourceDocumentOccurrence>().AsNoTracking()
            .Where(x => x.BusinessUnitId == tenantId && occurrenceIds.Contains(x.Id))
            .Select(x => x.SourceDocumentId)
            .Distinct()
            .ToListAsync(ct);
    }

    /// <summary>
    /// Names every age-eligible document the rules refused, and which rule refused it.
    /// "12 documents were excluded" is only a trustworthy statement if the tenant can see
    /// which twelve and why, so the reasons are computed rather than summarised away.
    /// </summary>
    private async Task<IReadOnlyList<EvidenceRetentionSkip>> SkipReportAsync(
        long tenantId, EvidenceRetentionPolicy policy, DateTime now,
        IReadOnlyList<PurgeCandidate> eligible, CancellationToken ct)
    {
        var eligibleIds = eligible.Select(x => x.Id).ToHashSet();
        var pool = await AgeEligibleQuery(tenantId, policy, now)
            .OrderBy(x => x.CreatedOn).ThenBy(x => x.Id).Take(BatchSize)
            .Select(x => new { x.Id, x.OriginalFileName, x.CorpusId, x.ExtractionJobId })
            .ToListAsync(ct);
        var excluded = pool.Where(x => !eligibleIds.Contains(x.Id)).ToList();
        if (excluded.Count == 0)
            return [];

        var ids = excluded.Select(x => x.Id).ToArray();
        var tenantHoldActive = await TenantHoldBlocksAsync(tenantId, ct);
        var governance = await GovernanceBlocksAsync(tenantId, ct);
        var held = governance.LegalHold.ToHashSet();
        var blockedActions = (await OpenHumanActionDocumentIdsAsync(tenantId, ct)).ToHashSet();
        var statutoryTypes = EvidenceRetentionEligibility.StatutoryDocumentTypes.ToArray();
        var statutory = (await db.CommercialDocumentClassifications.AsNoTracking()
            .Where(c => c.BusinessUnitId == tenantId && ids.Contains(c.SourceDocumentId)
                && statutoryTypes.Contains(c.DocumentType))
            .Select(c => c.SourceDocumentId).ToListAsync(ct)).ToHashSet();
        var terminalIntake = EvidenceRetentionEligibility.TerminalIntakeStatuses.ToArray();
        var openIntake = (await db.Set<SourceDocumentOccurrence>().AsNoTracking()
            .Where(o => o.BusinessUnitId == tenantId && ids.Contains(o.SourceDocumentId)
                && !terminalIntake.Contains(o.IntakeStatus))
            .Select(o => o.SourceDocumentId).ToListAsync(ct)).ToHashSet();
        var openLeadIds = OpenLeadIdsQuery(tenantId);
        var openCases = (await db.Set<LeadIngestionOccurrence>().AsNoTracking()
            .Where(l => l.BusinessUnitId == tenantId && l.SourceDocumentId.HasValue
                && ids.Contains(l.SourceDocumentId.Value)
                && l.LeadId.HasValue && openLeadIds.Contains(l.LeadId.Value))
            .Select(l => l.SourceDocumentId!.Value).ToListAsync(ct)).ToHashSet();
        var openInquiryStatuses = EvidenceRetentionEligibility.OpenInquiryStatuses.ToArray();
        var corpusIds = excluded.Select(x => x.CorpusId).Distinct().ToArray();
        var openInquiryCorpora = (await db.Set<CanonicalInquiry>().AsNoTracking()
            .Where(i => i.BusinessUnitId == tenantId && corpusIds.Contains(i.CorpusId)
                && openInquiryStatuses.Contains(i.Status))
            .Select(i => i.CorpusId).ToListAsync(ct)).ToHashSet();

        return excluded.Select(document => new EvidenceRetentionSkip(
            document.Id,
            document.OriginalFileName,
            tenantHoldActive || held.Contains(document.Id) ? EvidenceRetentionEligibility.Skip.LegalHold
            : statutory.Contains(document.Id) ? EvidenceRetentionEligibility.Skip.StatutoryDocumentType
            : openIntake.Contains(document.Id) ? EvidenceRetentionEligibility.Skip.OpenIntake
            : blockedActions.Contains(document.Id) ? EvidenceRetentionEligibility.Skip.OpenHumanAction
            : openCases.Contains(document.Id) ? EvidenceRetentionEligibility.Skip.OpenCommercialCase
            : openInquiryCorpora.Contains(document.CorpusId) ? EvidenceRetentionEligibility.Skip.OpenInquiry
            : EvidenceRetentionEligibility.Skip.ExtractionNotSucceeded))
            .ToList();
    }

    private async Task<bool> TenantHoldBlocksAsync(long businessUnitId, CancellationToken ct)
    {
        var platformTenantId = await TenantLegalHoldFence.ResolvePlatformTenantIdAsync(db, businessUnitId, ct);
        // An unresolvable identity cannot prove the absence of a hold. Selection therefore returns
        // no destructive candidates; the execution path raises an explicit conflict.
        return platformTenantId is null
               || await TenantLegalHoldFence.HasActiveAsync(db, platformTenantId.Value, ct);
    }

    // ---------------------------------------------------------------- plumbing

    private Task<EvidenceRetentionPolicy?> LoadPolicyAsync(long tenantId, CancellationToken ct) =>
        db.EvidenceRetentionPolicies.AsNoTracking()
            .SingleOrDefaultAsync(x => x.BusinessUnitId == tenantId, ct);

    private Task<TenantGovernanceAuditEvent?> ReplayAsync(long tenantId, string key, CancellationToken ct) =>
        db.TenantGovernanceAuditEvents.AsNoTracking()
            .SingleOrDefaultAsync(x => x.BusinessUnitId == tenantId && x.IdempotencyKey == key, ct);

    private async Task RecordRunAsync(long tenantId, long actorUserId, string idempotencyKey,
        string reason, EvidenceRetentionPolicy policy, EvidenceRetentionPurgeResult result,
        CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        db.TenantGovernanceAuditEvents.Add(new TenantGovernanceAuditEvent
        {
            BusinessUnitId = tenantId,
            Area = Area,
            AggregateType = "EvidenceRetentionRun",
            AggregateReference = $"tenant:{tenantId}",
            Action = ActionPurgeRun,
            Reason = reason,
            EvidenceJson = JsonSerializer.Serialize(new
            {
                result,
                policy = PolicyEvidence(policy),
                actor = new { userId = actorUserId, mode = "TENANT_INITIATED" }
            }),
            IdempotencyKey = idempotencyKey,
            ActorUserId = actorUserId,
            OccurredOn = DateTime.UtcNow
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The other half of the tombstone, at run level: every document this run refused to touch,
    /// grouped by the rule that refused it, with a count.
    ///
    /// <para>Written as a separate append-only event under a derived Idempotency-Key so a replayed
    /// run cannot double it, and so "what was kept on 12 August, and why" is one query rather than
    /// a reconstruction from per-document rows.</para>
    /// </summary>
    private async Task RecordKeptAsync(long tenantId, long actorUserId, string idempotencyKey,
        string reason, EvidenceRetentionPolicy policy, EvidenceRetentionPurgeResult result,
        CancellationToken ct)
    {
        var keptKey = $"{idempotencyKey}:kept";
        if (keptKey.Length > 160)
            keptKey = keptKey[..160];
        if (await ReplayAsync(tenantId, keptKey, ct) is not null)
            return;

        var byReason = result.Skipped
            .GroupBy(x => x.Reason, StringComparer.Ordinal)
            .Select(group => new { reason = group.Key, count = group.Count() })
            .OrderByDescending(x => x.count).ThenBy(x => x.reason, StringComparer.Ordinal)
            .ToList();

        db.ChangeTracker.Clear();
        db.TenantGovernanceAuditEvents.Add(new TenantGovernanceAuditEvent
        {
            BusinessUnitId = tenantId,
            Area = Area,
            AggregateType = "EvidenceRetentionRun",
            AggregateReference = $"tenant:{tenantId}",
            Action = ActionRunKept,
            Reason = reason,
            EvidenceJson = JsonSerializer.Serialize(new
            {
                scanned = result.Scanned,
                eligible = result.Eligible,
                purged = result.Purged,
                keptCount = result.Skipped.Count,
                kept = byReason,
                policy = PolicyEvidence(policy),
                actor = new { userId = actorUserId, mode = "TENANT_INITIATED" },
                statutoryOverridesTenantPreference = true
            }),
            IdempotencyKey = keptKey,
            ActorUserId = actorUserId,
            OccurredOn = DateTime.UtcNow
        });
        await db.SaveChangesAsync(ct);
    }

    private static EvidenceRetentionPurgeResult Deserialize(string evidenceJson)
    {
        using var document = JsonDocument.Parse(evidenceJson);
        var payload = document.RootElement.GetProperty("result");
        return payload.Deserialize<EvidenceRetentionPurgeResult>(JsonOptions)
            ?? throw new PlatformGovernanceConflictException(
                "A previous purge run under this Idempotency-Key cannot be replayed.");
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static TenantGovernanceAuditEvent Audit(long tenantId, long actorUserId, long documentId,
        string action, string reason, string idempotencyKey, object evidence) => new()
        {
            BusinessUnitId = tenantId,
            Area = Area,
            AggregateType = "SourceDocument",
            AggregateReference = $"source-document:{documentId}",
            Action = action,
            Reason = reason,
            EvidenceJson = JsonSerializer.Serialize(evidence),
            IdempotencyKey = idempotencyKey,
            ActorUserId = actorUserId,
            OccurredOn = DateTime.UtcNow
        };

    private static object PolicyEvidence(EvidenceRetentionPolicy policy) => new
    {
        version = policy.Version,
        code = policy.PolicyCode,
        retainDaysAfterExtraction = policy.RetentionDays,
        basis = "UAE PDPL Art. 16 / KSA PDPL Art. 18 — destruction once the processing purpose is met",
        mode = "TENANT_INITIATED"
    };

    private static object DocumentEvidence(PurgeCandidate candidate) => new
    {
        sourceDocumentId = candidate.Id,
        contentHash = candidate.ContentHash,
        hashAlgorithm = "SHA-256",
        originalFileName = candidate.OriginalFileName,
        byteSize = candidate.ByteSize,
        objectBucket = candidate.ObjectBucket,
        objectKey = candidate.ObjectKey,
        securityStatus = candidate.SecurityStatus.ToString(),
        processingStatus = candidate.ProcessingStatus.ToString(),
        ingestedOn = candidate.CreatedOn
    };

    /// <summary>
    /// What survives the purge, counted rather than asserted. This is the half of the
    /// tombstone that makes the other half defensible.
    /// </summary>
    private async Task<object> RetainedEvidenceAsync(
        long tenantId, PurgeCandidate candidate, CancellationToken ct)
    {
        var pageIds = db.Set<DocumentPage>().AsNoTracking()
            .Where(x => x.BusinessUnitId == tenantId && x.DocumentId == candidate.Id)
            .Select(x => x.Id);
        var regionIds = db.Set<DocumentRegion>().AsNoTracking()
            .Where(x => x.BusinessUnitId == tenantId && pageIds.Contains(x.PageId))
            .Select(x => x.Id);

        return new
        {
            sourceDocumentRow = true,
            contentHash = candidate.ContentHash,
            documentPages = await pageIds.CountAsync(ct),
            documentRegions = await regionIds.CountAsync(ct),
            fieldEvidence = await db.Set<FieldEvidence>().AsNoTracking()
                .CountAsync(x => x.BusinessUnitId == tenantId && regionIds.Contains(x.RegionId), ct),
            extractionRuns = await db.Set<ExtractionRun>().AsNoTracking()
                .CountAsync(x => x.BusinessUnitId == tenantId && x.SourceDocumentId == candidate.Id, ct),
            leadIds = await db.Set<LeadIngestionOccurrence>().AsNoTracking()
                .Where(x => x.BusinessUnitId == tenantId && x.SourceDocumentId == candidate.Id
                    && x.LeadId.HasValue)
                .Select(x => x.LeadId!.Value).Distinct().ToListAsync(ct)
        };
    }

    private static EvidenceRetentionPolicyView Map(EvidenceRetentionPolicy policy) => new(
        policy.RetentionDays, policy.IsEnabled,
        EvidenceRetentionPolicy.MinimumRetentionDays, EvidenceRetentionPolicy.MaximumRetentionDays,
        policy.Version, policy.UpdatedOn == default ? null : policy.UpdatedOn);
}
