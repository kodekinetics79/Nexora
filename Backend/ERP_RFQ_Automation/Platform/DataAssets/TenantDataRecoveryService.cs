using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Lifecycle;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Services;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Platform.DataAssets;

public sealed partial class TenantDataRecoveryService(
    ErpRfqAutomationContext context,
    IPlatformAuditService audit,
    TimeProvider? timeProvider = null)
{
    public const string RecordAction = "tenant.data-recovery-evidence.record";
    public const string CertificateAction = "tenant.deletion-certificate.issue";
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private const string DecisionBoundary =
        "A certificate proves the registered boundaries and immutable evidence known to Nexora. " +
        "Unknown or unregistered provider state is a blocker, never an implicit deletion success.";

    public async Task<IReadOnlyList<TenantDataRecoveryEvidenceDto>> ListAsync(
        long tenantId, CancellationToken ct) =>
        (await context.Set<TenantDataRecoveryEvidence>().AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .OrderBy(x => x.RecordedUtc).ThenBy(x => x.Id)
            .ToListAsync(ct)).Select(ToDto).ToList();

    public async Task<TenantDataRecoveryEvidenceDto> RecordAsync(
        long tenantId,
        RecordTenantDataRecoveryEvidenceRequest request,
        ClaimsPrincipal actor,
        HttpContext? httpContext,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!TenantDataRecoveryEvidenceTypes.All.Contains(request.EvidenceType))
            throw new TenantDataAssetValidationException($"Unknown recovery evidence type '{request.EvidenceType}'.");

        var scopeKey = Required(request.ScopeKey, 80, "A recovery evidence scope is required.").ToLowerInvariant();
        var provider = Opaque(request.OpaqueProviderReference, 256, "provider reference");
        var backupSet = string.IsNullOrWhiteSpace(request.OpaqueBackupSetReference)
            ? null : Opaque(request.OpaqueBackupSetReference, 256, "backup-set reference");
        var evidenceReference = Opaque(request.EvidenceReference, 256, "evidence reference");
        var hash = Required(request.EvidenceSha256, 64, "An evidence SHA-256 is required.").ToLowerInvariant();
        if (!Sha256().IsMatch(hash))
            throw new TenantDataAssetValidationException("Evidence SHA-256 must be 64 hexadecimal characters.");
        var correlationId = Opaque(request.CorrelationId, 128, "correlation ID");
        var idempotencyKey = Opaque(request.IdempotencyKey, 128, "idempotency key");
        var reason = Required(request.Reason, 1000, "An evidence reason is required.");
        var now = _time.GetUtcNow().UtcDateTime;
        var completed = Utc(request.CompletedUtc, "Completion time");
        if (completed > now.AddMinutes(5))
            throw new TenantDataAssetValidationException("Completion time cannot be in the future.");
        if (request.ConfiguredRpoSeconds is <= 0 || request.ConfiguredRtoSeconds is <= 0)
            throw new TenantDataAssetValidationException("Configured RPO and RTO must be positive when supplied.");
        if (request.CustomerRowsObserved is < 0)
            throw new TenantDataAssetValidationException("Observed customer row count cannot be negative.");

        DateTime? started = request.OperationStartedUtc is null
            ? null : Utc(request.OperationStartedUtc.Value, "Operation start time");
        DateTime? recoveryPoint = request.RecoveryPointUtc is null
            ? null : Utc(request.RecoveryPointUtc.Value, "Recovery point");
        int? actualRecoverySeconds = null;
        if (request.EvidenceType == TenantDataRecoveryEvidenceTypes.RestoreDrillCompleted)
        {
            if (backupSet is null || started is null || recoveryPoint is null || request.ConfiguredRpoSeconds is null
                || request.ConfiguredRtoSeconds is null)
                throw new TenantDataAssetValidationException(
                    "Restore drill evidence requires a backup set, recovery point, operation start, configured RPO, and configured RTO.");
            if (completed < started)
                throw new TenantDataAssetValidationException("Restore completion cannot precede its start.");
            if (recoveryPoint > started)
                throw new TenantDataAssetValidationException("Restore recovery point cannot be later than the operation start.");
            actualRecoverySeconds = checked((int)Math.Ceiling((completed - started.Value).TotalSeconds));
        }
        if (request.EvidenceType == TenantDataRecoveryEvidenceTypes.BackupSetObserved
            && (backupSet is null || recoveryPoint is null || request.RetainUntilUtc is null))
            throw new TenantDataAssetValidationException(
                "Backup-set evidence requires an opaque backup-set reference, recovery point, and retention expiry.");
        if (request.EvidenceType == TenantDataRecoveryEvidenceTypes.TombstoneReapplied
            && request.CustomerRowsObserved != 0)
            throw new TenantDataAssetValidationException(
                "Tombstone reapplication is complete only after the restored customer row count is zero.");

        return await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            context.ChangeTracker.Clear();
            await using var tx = await context.Database.BeginTransactionAsync(ct);
            var existing = await context.Set<TenantDataRecoveryEvidence>().SingleOrDefaultAsync(
                x => x.TenantId == tenantId && x.IdempotencyKey == idempotencyKey, ct);
            if (existing is not null)
            {
                if (SameEvidence(existing, request, scopeKey, provider, backupSet, evidenceReference,
                        hash, correlationId, completed, started, recoveryPoint))
                    return ToDto(existing);
                throw new TenantDataAssetConflictException(
                    "The idempotency key is already bound to different recovery evidence.");
            }

            var tenant = await context.Set<Tenant>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == tenantId, ct)
                         ?? throw new TenantDataAssetNotFoundException($"Tenant {tenantId} was not found.");
            if (request.TenantDataAssetId is long assetId)
            {
                var assetExists = await context.Set<TenantDataAsset>().AsNoTracking()
                    .AnyAsync(x => x.Id == assetId && x.TenantId == tenantId && x.LogicalKey == scopeKey, ct);
                if (!assetExists)
                    throw new TenantDataAssetValidationException(
                        "The recovery evidence does not match a registered tenant data asset.");
            }

            var destructiveConfirmation = request.EvidenceType is
                TenantDataRecoveryEvidenceTypes.BackupDestructionConfirmed or
                TenantDataRecoveryEvidenceTypes.SubprocessorDeletionConfirmed;
            if (destructiveConfirmation)
            {
                // Same transaction-scoped fence as legal-hold placement. Hold-first blocks this
                // confirmation; destruction-first makes hold placement wait until the immutable
                // evidence and audit occurrence commit.
                await TenantLegalHoldFence.AcquireTransactionAsync(context, tenantId, ct);
                if (await context.Set<TenantLegalHold>().AsNoTracking()
                        .AnyAsync(x => x.TenantId == tenantId && x.ReleasedOn == null, ct))
                    throw new TenantDataAssetConflictException(
                        "Active legal hold prevents backup or subprocessor destruction confirmation.");
            }

            var offboarding = await context.Set<TenantOffboarding>().AsNoTracking()
                .SingleOrDefaultAsync(x => x.TenantId == tenantId, ct);
            if (request.EvidenceType == TenantDataRecoveryEvidenceTypes.RestoreDrillCompleted
                && offboarding?.PurgedOn is not null)
            {
                var tombstoneApplied = await context.Set<TenantDataRecoveryEvidence>().AsNoTracking()
                    .AnyAsync(x => x.TenantId == tenantId && x.ScopeKey == scopeKey
                                   && x.EvidenceType == TenantDataRecoveryEvidenceTypes.TombstoneReapplied
                                   && x.CustomerRowsObserved == 0 && x.CompletedUtc >= started
                                   && x.CompletedUtc <= completed
                                   && x.OpaqueBackupSetReference == backupSet
                                   && x.CorrelationId == correlationId, ct);
                if (!tombstoneApplied || request.CustomerRowsObserved != 0)
                    throw new TenantDataAssetConflictException(
                        "A purged tenant restore cannot complete until its tombstone is reapplied and zero customer rows remain.");
            }

            var row = new TenantDataRecoveryEvidence
            {
                TenantId = tenantId,
                TenantDataAssetId = request.TenantDataAssetId,
                ScopeKey = scopeKey,
                EvidenceType = request.EvidenceType,
                OpaqueProviderReference = provider,
                OpaqueBackupSetReference = backupSet,
                RecoveryPointUtc = recoveryPoint,
                OperationStartedUtc = started,
                CompletedUtc = completed,
                ConfiguredRpoSeconds = request.ConfiguredRpoSeconds,
                ConfiguredRtoSeconds = request.ConfiguredRtoSeconds,
                ActualRecoverySeconds = actualRecoverySeconds,
                RetainUntilUtc = request.RetainUntilUtc is null ? null : Utc(request.RetainUntilUtc.Value, "Retention expiry"),
                CustomerRowsObserved = request.CustomerRowsObserved,
                EvidenceReference = evidenceReference,
                EvidenceSha256 = hash,
                CorrelationId = correlationId,
                IdempotencyKey = idempotencyKey,
                ActorPlatformUserId = ActorId(actor),
                ActorEmail = ActorEmail(actor),
                Reason = reason,
                RecordedUtc = now
            };
            context.Set<TenantDataRecoveryEvidence>().Add(row);
            await context.SaveChangesAsync(ct);
            await audit.WriteAsync(actor, RecordAction, nameof(TenantDataRecoveryEvidence), row.Id.ToString(),
                new { row.ScopeKey, row.EvidenceType, row.EvidenceReference, row.EvidenceSha256,
                    row.CorrelationId, row.CompletedUtc, tenantSlug = tenant.Slug }, tenantId, httpContext, ct);
            await tx.CommitAsync(ct);
            return ToDto(row);
        });
    }

    public async Task<TenantDeletionCertificationDecisionDto> DecisionAsync(long tenantId, CancellationToken ct)
    {
        var evaluated = _time.GetUtcNow().UtcDateTime;
        var tenant = await context.Set<Tenant>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == tenantId, ct)
                     ?? throw new TenantDataAssetNotFoundException($"Tenant {tenantId} was not found.");
        var offboarding = await context.Set<TenantOffboarding>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId, ct);
        var assets = await context.Set<TenantDataAsset>().AsNoTracking()
            .Where(x => x.TenantId == tenantId).OrderBy(x => x.LogicalKey).ToListAsync(ct);
        var evidence = await context.Set<TenantDataRecoveryEvidence>().AsNoTracking()
            .Where(x => x.TenantId == tenantId).OrderBy(x => x.Id).ToListAsync(ct);
        var blockers = new List<string>();
        var selected = new List<long>();

        if (offboarding?.PurgedOn is null)
            blockers.Add("Tenant database purge has not completed.");
        if (await context.Set<TenantLegalHold>().AsNoTracking()
                .AnyAsync(x => x.TenantId == tenantId && x.ReleasedOn == null, ct))
            blockers.Add("An active legal hold prevents deletion certification.");
        if (assets.Count == 0)
            blockers.Add("No tenant data boundaries are registered; external data state is unknown.");
        foreach (var requiredType in TenantDataAssetTypes.All.Order())
            if (assets.All(x => !string.Equals(x.AssetType, requiredType, StringComparison.Ordinal)))
                blockers.Add($"Required data boundary type '{requiredType}' is not registered; provider state is unknown.");

        foreach (var asset in assets.Where(x => x.Disposition != TenantDataAssetDispositions.PreserveOperatorEvidence))
        {
            var residency = evidence.LastOrDefault(x => x.ScopeKey == asset.LogicalKey
                && x.EvidenceType == TenantDataRecoveryEvidenceTypes.ResidencyVerified
                && x.OpaqueProviderReference == asset.OpaqueProviderReference);
            if (residency is null)
                blockers.Add($"Data boundary '{asset.LogicalKey}' has no residency-verification evidence.");
            else
                selected.Add(residency.Id);

            var requiredType = asset.AssetType is TenantDataAssetTypes.Subprocessor or TenantDataAssetTypes.AiOcrProvider
                ? TenantDataRecoveryEvidenceTypes.SubprocessorDeletionConfirmed
                : TenantDataRecoveryEvidenceTypes.BackupDestructionConfirmed;
            var proof = evidence.LastOrDefault(x => x.ScopeKey == asset.LogicalKey
                                                    && x.EvidenceType == requiredType
                                                    && offboarding?.PurgedOn is not null
                                                    && x.CompletedUtc >= offboarding.PurgedOn);
            if (proof is null)
                blockers.Add($"Data boundary '{asset.LogicalKey}' has no post-purge {requiredType} evidence.");
            else
                selected.Add(proof.Id);
        }

        var primary = assets.SingleOrDefault(x =>
            x.AssetType == TenantDataAssetTypes.PostgreSqlTenantScope
            && x.LogicalKey == TenantDataAssetRegistryService.PostgreSqlLogicalKey);
        if (primary is not null && offboarding?.PurgedOn is DateTime purgedUtc)
        {
            var restore = evidence.LastOrDefault(x => x.ScopeKey == primary.LogicalKey
                && x.EvidenceType == TenantDataRecoveryEvidenceTypes.RestoreDrillCompleted
                && x.CompletedUtc >= purgedUtc && x.OperationStartedUtc is not null
                && x.RecoveryPointUtc is not null && x.OpaqueBackupSetReference is not null
                && x.ConfiguredRpoSeconds is > 0 && x.ConfiguredRtoSeconds is > 0
                && x.ActualRecoverySeconds is not null
                && x.ActualRecoverySeconds <= x.ConfiguredRtoSeconds
                && x.RecoveryPointUtc <= x.OperationStartedUtc
                && (x.OperationStartedUtc.Value - x.RecoveryPointUtc.Value).TotalSeconds <= x.ConfiguredRpoSeconds
                && x.CustomerRowsObserved == 0);
            if (restore is null)
            {
                blockers.Add("The primary PostgreSQL boundary has no post-purge restore drill within configured RPO/RTO with zero customer rows.");
            }
            else
            {
                var backup = evidence.LastOrDefault(x => x.ScopeKey == primary.LogicalKey
                    && x.EvidenceType == TenantDataRecoveryEvidenceTypes.BackupSetObserved
                    && x.OpaqueBackupSetReference == restore.OpaqueBackupSetReference
                    && x.RecoveryPointUtc == restore.RecoveryPointUtc
                    && x.CompletedUtc <= restore.OperationStartedUtc);
                var tombstone = evidence.LastOrDefault(x => x.ScopeKey == primary.LogicalKey
                    && x.EvidenceType == TenantDataRecoveryEvidenceTypes.TombstoneReapplied
                    && x.OpaqueBackupSetReference == restore.OpaqueBackupSetReference
                    && x.CorrelationId == restore.CorrelationId
                    && x.CustomerRowsObserved == 0
                    && x.CompletedUtc >= restore.OperationStartedUtc
                    && x.CompletedUtc <= restore.CompletedUtc);
                if (backup is null)
                    blockers.Add("The post-purge restore drill is not linked to a previously observed backup set and recovery point.");
                else
                    selected.Add(backup.Id);
                if (tombstone is null)
                    blockers.Add("The post-purge restore drill has no in-window tombstone reapplication with zero customer rows.");
                else
                    selected.Add(tombstone.Id);
                if (backup is not null && tombstone is not null)
                    selected.Add(restore.Id);
            }
        }

        return new TenantDeletionCertificationDecisionDto(
            tenant.Id, blockers.Count == 0, blockers, selected.Distinct().Order().ToArray(), evaluated, DecisionBoundary);
    }

    public async Task<TenantDeletionCertificateDto> CertifyAsync(
        long tenantId, CreateTenantDeletionCertificateRequest request, ClaimsPrincipal actor,
        HttpContext? httpContext, CancellationToken ct)
    {
        var reason = Required(request.Reason, 1000, "A deletion certification reason is required.");
        return await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            context.ChangeTracker.Clear();
            await using var tx = await context.Database.BeginTransactionAsync(ct);
            await TenantLegalHoldFence.AcquireTransactionAsync(context, tenantId, ct);
            var existing = await context.Set<TenantDeletionCertificate>().AsNoTracking()
                .SingleOrDefaultAsync(x => x.TenantId == tenantId, ct);
            if (existing is not null) return ToDto(existing);

            var decision = await DecisionAsync(tenantId, ct);
            if (!decision.Ready)
                throw new TenantDataAssetConflictException(string.Join(" ", decision.Blockers));
            var tenant = await context.Set<Tenant>().AsNoTracking().SingleAsync(x => x.Id == tenantId, ct);
            var offboarding = await context.Set<TenantOffboarding>().AsNoTracking()
                .SingleAsync(x => x.TenantId == tenantId, ct);
            var evidenceJson = JsonSerializer.Serialize(decision.EvidenceIds.Order());
            var row = new TenantDeletionCertificate
            {
                TenantId = tenantId,
                TenantSlug = tenant.Slug,
                PurgedUtc = offboarding.PurgedOn!.Value,
                CertifiedUtc = _time.GetUtcNow().UtcDateTime,
                ActorPlatformUserId = ActorId(actor),
                ActorEmail = ActorEmail(actor),
                EvidenceManifestSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(evidenceJson)))
                    .ToLowerInvariant(),
                EvidenceIdsJson = evidenceJson,
                Reason = reason
            };
            context.Set<TenantDeletionCertificate>().Add(row);
            await context.SaveChangesAsync(ct);
            await audit.WriteAsync(actor, CertificateAction, nameof(TenantDeletionCertificate), row.Id.ToString(),
                new { row.TenantSlug, row.PurgedUtc, row.EvidenceManifestSha256, decision.EvidenceIds },
                tenantId, httpContext, ct);
            await tx.CommitAsync(ct);
            return ToDto(row);
        });
    }

    private static TenantDataRecoveryEvidenceDto ToDto(TenantDataRecoveryEvidence x) => new(
        x.Id, x.TenantId, x.TenantDataAssetId, x.ScopeKey, x.EvidenceType,
        x.OpaqueProviderReference, x.OpaqueBackupSetReference, x.RecoveryPointUtc,
        x.OperationStartedUtc, x.CompletedUtc, x.ConfiguredRpoSeconds, x.ConfiguredRtoSeconds,
        x.ActualRecoverySeconds, x.RetainUntilUtc, x.CustomerRowsObserved, x.EvidenceReference,
        x.EvidenceSha256, x.CorrelationId, x.ActorEmail, x.Reason, x.RecordedUtc);

    private static TenantDeletionCertificateDto ToDto(TenantDeletionCertificate x) => new(
        x.Id, x.TenantId, x.TenantSlug, x.PurgedUtc, x.CertifiedUtc, x.ActorEmail,
        x.EvidenceManifestSha256, JsonSerializer.Deserialize<long[]>(x.EvidenceIdsJson) ?? [], x.Reason);

    private static bool SameEvidence(
        TenantDataRecoveryEvidence stored, RecordTenantDataRecoveryEvidenceRequest request,
        string scopeKey, string provider, string? backupSet, string evidenceReference, string hash,
        string correlationId, DateTime completed, DateTime? started, DateTime? recoveryPoint) =>
        stored.TenantDataAssetId == request.TenantDataAssetId
        && stored.ScopeKey == scopeKey && stored.EvidenceType == request.EvidenceType
        && stored.OpaqueProviderReference == provider && stored.OpaqueBackupSetReference == backupSet
        && stored.RecoveryPointUtc == recoveryPoint && stored.OperationStartedUtc == started
        && stored.CompletedUtc == completed && stored.ConfiguredRpoSeconds == request.ConfiguredRpoSeconds
        && stored.ConfiguredRtoSeconds == request.ConfiguredRtoSeconds
        && stored.RetainUntilUtc == request.RetainUntilUtc
        && stored.CustomerRowsObserved == request.CustomerRowsObserved
        && stored.EvidenceReference == evidenceReference && stored.EvidenceSha256 == hash
        && stored.CorrelationId == correlationId;

    private static DateTime Utc(DateTime value, string label)
    {
        if (value.Kind == DateTimeKind.Unspecified)
            throw new TenantDataAssetValidationException($"{label} must include a UTC offset.");
        return value.ToUniversalTime();
    }

    private static string Required(string? value, int max, string message)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized)) throw new TenantDataAssetValidationException(message);
        if (normalized.Length > max) throw new TenantDataAssetValidationException($"Value exceeds {max} characters.");
        return normalized;
    }

    private static string Opaque(string? value, int max, string label)
    {
        var normalized = Required(value, max, $"An opaque {label} is required.");
        if (normalized.Any(char.IsWhiteSpace) || normalized.Contains("://", StringComparison.Ordinal)
            || normalized.Contains('@') || normalized.Contains('=') || normalized.Contains('?'))
            throw new TenantDataAssetValidationException($"The {label} must be an opaque identifier.");
        return normalized;
    }

    private static long ActorId(ClaimsPrincipal actor) =>
        long.TryParse(actor.FindFirst("sub")?.Value ?? actor.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id)
        && id > 0 ? id : throw new TenantDataAssetValidationException("A platform actor ID is required.");

    private static string ActorEmail(ClaimsPrincipal actor) =>
        actor.FindFirst("email")?.Value?.Trim() ?? actor.FindFirst(ClaimTypes.Email)?.Value?.Trim()
        ?? throw new TenantDataAssetValidationException("A platform actor email is required.");

    [GeneratedRegex("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256();
}
