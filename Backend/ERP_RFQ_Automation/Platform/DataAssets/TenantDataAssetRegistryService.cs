using System.Security.Claims;
using System.Text.RegularExpressions;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Services;
using ERP_RFQ_Automation.Platform.Lifecycle;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Platform.DataAssets;

public sealed class TenantDataAssetRegistryService(
    ErpRfqAutomationContext context,
    IPlatformAuditService audit)
{
    public const string RegisterAction = "tenant.data-asset.register";
    public const string VerifyAction = "tenant.data-asset.verify";
    public const string PostgreSqlLogicalKey = "postgresql.primary";
    private const string DecisionBoundary =
        "This decision certifies only the tenant data-readiness gate. It does not activate the tenant " +
        "and does not certify commercial, identity, subprocessor, backup-restore, or production readiness.";

    private static readonly Regex Sha256 = new("^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant);

    public async Task<IReadOnlyList<TenantDataAssetDto>> ListAsync(long tenantId, CancellationToken ct)
    {
        await RequireTenantAsync(tenantId, ct);
        var rows = await context.Set<TenantDataAsset>().AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .OrderBy(x => x.LogicalKey)
            .ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<TenantDataAssetDto> RegisterAsync(
        long tenantId,
        RegisterTenantDataAssetRequest request,
        ClaimsPrincipal actor,
        HttpContext? httpContext,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tenant = await RequireTenantAsync(tenantId, ct);
        var logicalKey = Required(request.LogicalKey, 80, "A logical asset key is required.").ToLowerInvariant();
        if (!string.Equals(logicalKey, PostgreSqlLogicalKey, StringComparison.Ordinal))
            throw new TenantDataAssetValidationException(
                $"The first data-readiness slice supports only logical key '{PostgreSqlLogicalKey}'.");

        var providerReference = OpaqueReference(request.OpaqueProviderReference, 256, "provider reference");
        var region = Required(request.Region, 64, "A data region is required.").ToLowerInvariant();
        var classification = Required(request.Classification, 64, "A data classification is required.");
        var disposition = Required(request.Disposition, 96, "A data disposition is required.");
        var backupPolicy = OpaqueReference(request.BackupPolicyReference, 128, "backup policy reference");
        var reason = Required(request.Reason, 1000, "A registration reason is required.");
        if (!string.Equals(classification, TenantDataAssetClassifications.CustomerData, StringComparison.Ordinal))
            throw new TenantDataAssetValidationException(
                $"PostgreSQL tenant scopes must be classified as '{TenantDataAssetClassifications.CustomerData}'.");
        if (!string.Equals(disposition, TenantDataAssetDispositions.BackupRetainedUntilExpiryThenDestroy,
                StringComparison.Ordinal))
            throw new TenantDataAssetValidationException(
                $"PostgreSQL tenant scopes must use disposition " +
                $"'{TenantDataAssetDispositions.BackupRetainedUntilExpiryThenDestroy}'.");
        if (request.BackupPolicyVersion <= 0)
            throw new TenantDataAssetValidationException("Backup policy version must be positive.");
        if (!string.IsNullOrWhiteSpace(tenant.DataRegion)
            && !string.Equals(region, tenant.DataRegion.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new TenantDataAssetValidationException(
                $"Asset region '{region}' does not match the tenant's contractual data region '{tenant.DataRegion}'.");

        var createdBy = Actor(actor);
        return await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            context.ChangeTracker.Clear();
            await using var tx = await context.Database.BeginTransactionAsync(ct);
            var existing = await context.Set<TenantDataAsset>().SingleOrDefaultAsync(
                x => x.TenantId == tenantId && x.LogicalKey == logicalKey, ct);
            if (existing is not null)
            {
                if (SameRegistration(existing, providerReference, region, classification, disposition,
                        backupPolicy, request.BackupPolicyVersion))
                    return ToDto(existing);
                throw new TenantDataAssetConflictException(
                    $"Tenant {tenantId} already has a different data asset '{logicalKey}'.");
            }

            var now = DateTime.UtcNow;
            var row = new TenantDataAsset
            {
                TenantId = tenantId,
                LogicalKey = logicalKey,
                AssetType = TenantDataAssetTypes.PostgreSqlTenantScope,
                OpaqueProviderReference = providerReference,
                Region = region,
                Classification = classification,
                Disposition = disposition,
                BackupPolicyReference = backupPolicy,
                BackupPolicyVersion = request.BackupPolicyVersion,
                Status = TenantDataAssetStatuses.Registered,
                CreatedOn = now,
                CreatedBy = createdBy,
                Version = 1
            };
            context.Set<TenantDataAsset>().Add(row);
            await context.SaveChangesAsync(ct);
            await audit.WriteAsync(actor, RegisterAction, nameof(TenantDataAsset), row.Id.ToString(),
                new
                {
                    row.LogicalKey,
                    row.AssetType,
                    row.OpaqueProviderReference,
                    row.Region,
                    row.Classification,
                    row.Disposition,
                    row.BackupPolicyReference,
                    row.BackupPolicyVersion,
                    reason
                }, tenantId, httpContext, ct);
            await tx.CommitAsync(ct);
            return ToDto(row);
        });
    }

    public async Task<TenantDataAssetDto> VerifyAsync(
        long tenantId,
        long assetId,
        VerifyTenantDataAssetRequest request,
        ClaimsPrincipal actor,
        HttpContext? httpContext,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tenant = await RequireTenantAsync(tenantId, ct);
        var observedRegion = Required(request.ObservedRegion, 64, "An observed region is required.").ToLowerInvariant();
        var evidenceReference = OpaqueReference(request.EvidenceReference, 256, "verification evidence reference");
        var evidenceSha256 = Required(request.EvidenceSha256, 64, "A verification evidence SHA-256 is required.")
            .ToLowerInvariant();
        var reason = Required(request.Reason, 1000, "A verification reason is required.");
        if (!Sha256.IsMatch(evidenceSha256))
            throw new TenantDataAssetValidationException("Verification evidence SHA-256 must be 64 hexadecimal characters.");
        if (tenant.PrimaryBusinessUnitId is not long expectedScope)
            throw new TenantDataAssetConflictException(
                "The tenant has no primary business-unit scope, so its PostgreSQL boundary cannot be verified.");
        if (request.ObservedBusinessUnitId != expectedScope)
            throw new TenantDataAssetValidationException(
                $"Observed business-unit scope {request.ObservedBusinessUnitId} does not match tenant scope {expectedScope}.");

        return await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            context.ChangeTracker.Clear();
            await using var tx = await context.Database.BeginTransactionAsync(ct);
            var row = await context.Set<TenantDataAsset>().SingleOrDefaultAsync(
                          x => x.Id == assetId && x.TenantId == tenantId, ct)
                      ?? throw new TenantDataAssetNotFoundException("The tenant data asset was not found.");
            if (row.Version != request.ExpectedVersion)
            {
                if (row.Version == request.ExpectedVersion + 1
                    && SameVerification(row, request.ObservedBusinessUnitId, observedRegion,
                        evidenceReference, evidenceSha256))
                    return ToDto(row);
                throw new TenantDataAssetConflictException("The data asset changed; reload it before verifying.");
            }
            if (!string.Equals(row.AssetType, TenantDataAssetTypes.PostgreSqlTenantScope, StringComparison.Ordinal)
                || !string.Equals(row.LogicalKey, PostgreSqlLogicalKey, StringComparison.Ordinal))
                throw new TenantDataAssetValidationException("Only the primary PostgreSQL tenant scope can be verified in this slice.");
            if (!string.Equals(row.Region, observedRegion, StringComparison.OrdinalIgnoreCase))
                throw new TenantDataAssetValidationException(
                    $"Observed region '{observedRegion}' does not match registered region '{row.Region}'.");

            var now = DateTime.UtcNow;
            row.Status = TenantDataAssetStatuses.Verified;
            row.VerifiedBusinessUnitId = request.ObservedBusinessUnitId;
            row.VerificationEvidenceReference = evidenceReference;
            row.VerificationEvidenceSha256 = evidenceSha256;
            row.VerificationVersion++;
            row.VerifiedOn = now;
            row.VerifiedBy = Actor(actor);
            row.ModifiedOn = now;
            row.ModifiedBy = row.VerifiedBy;
            row.Version++;
            await context.SaveChangesAsync(ct);
            await audit.WriteAsync(actor, VerifyAction, nameof(TenantDataAsset), row.Id.ToString(),
                new
                {
                    row.LogicalKey,
                    row.Status,
                    row.VerifiedBusinessUnitId,
                    row.Region,
                    row.VerificationEvidenceReference,
                    row.VerificationEvidenceSha256,
                    row.VerificationVersion,
                    reason
                }, tenantId, httpContext, ct);
            await tx.CommitAsync(ct);
            return ToDto(row);
        });
    }

    public async Task<TenantActivationDataDecisionDto> ActivationDataDecisionAsync(
        long tenantId, CancellationToken ct)
    {
        var tenant = await RequireTenantAsync(tenantId, ct);
        var asset = await context.Set<TenantDataAsset>().AsNoTracking().SingleOrDefaultAsync(
            x => x.TenantId == tenantId && x.LogicalKey == PostgreSqlLogicalKey, ct);
        var offboarding = await context.Set<TenantOffboarding>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId, ct);
        var blockers = new List<string>();
        if (tenant.Status is TenantStatus.Archived or TenantStatus.Suspended)
            blockers.Add($"Tenant lifecycle status {tenant.Status} is not activation-eligible.");
        if (offboarding?.Stage is TenantOffboardingStage.PendingDeletion or TenantOffboardingStage.Purged
            || offboarding?.PurgeStartedOn is not null)
            blockers.Add("Tenant offboarding or purge state is not activation-eligible.");
        if (offboarding?.PersonalDataErasedOn is not null)
            blockers.Add("Tenant personal data has been erased and requires governed reprovisioning.");
        if (tenant.PrimaryBusinessUnitId is null)
            blockers.Add("Tenant primary business-unit scope is not provisioned.");
        if (string.IsNullOrWhiteSpace(tenant.DataRegion))
            blockers.Add("Tenant contractual data region is not recorded.");
        if (asset is null)
        {
            blockers.Add("Primary PostgreSQL tenant-scope asset is not registered.");
        }
        else
        {
            if (!string.Equals(asset.Status, TenantDataAssetStatuses.Verified, StringComparison.Ordinal))
                blockers.Add("Primary PostgreSQL tenant-scope asset is not verified.");
            if (!string.Equals(asset.Region, tenant.DataRegion?.Trim(), StringComparison.OrdinalIgnoreCase))
                blockers.Add("Registered PostgreSQL region does not match the tenant's contractual data region.");
            if (asset.VerifiedBusinessUnitId != tenant.PrimaryBusinessUnitId)
                blockers.Add("Verified PostgreSQL scope does not match the tenant's primary business unit.");
            if (asset.VerificationVersion <= 0 || string.IsNullOrWhiteSpace(asset.VerificationEvidenceSha256))
                blockers.Add("PostgreSQL verification evidence is incomplete.");
            if (asset.BackupPolicyVersion <= 0 || string.IsNullOrWhiteSpace(asset.BackupPolicyReference))
                blockers.Add("PostgreSQL backup policy is not versioned.");
        }

        return new TenantActivationDataDecisionDto(
            tenantId,
            blockers.Count == 0,
            blockers.Count == 0 ? "DataGateReady" : "Blocked",
            blockers,
            asset is null ? null : ToDto(asset),
            DecisionBoundary);
    }

    private async Task<Tenant> RequireTenantAsync(long tenantId, CancellationToken ct) =>
        await context.Set<Tenant>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == tenantId, ct)
        ?? throw new TenantDataAssetNotFoundException($"Tenant {tenantId} was not found.");

    private static string Required(string? value, int maxLength, string message)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized)) throw new TenantDataAssetValidationException(message);
        if (normalized.Length > maxLength)
            throw new TenantDataAssetValidationException($"Value exceeds the maximum length of {maxLength} characters.");
        return normalized;
    }

    private static string OpaqueReference(string? value, int maxLength, string label)
    {
        var normalized = Required(value, maxLength, $"An opaque {label} is required.");
        if (normalized.Any(char.IsWhiteSpace) || normalized.Contains("://", StringComparison.Ordinal)
            || normalized.Contains('@') || normalized.Contains('=') || normalized.Contains('?'))
            throw new TenantDataAssetValidationException(
                $"The {label} must be an opaque identifier, not a URL, connection string, credential, or query string.");
        return normalized;
    }

    private static string Actor(ClaimsPrincipal actor) =>
        actor.FindFirst("email")?.Value?.Trim()
        ?? actor.FindFirst(ClaimTypes.Email)?.Value?.Trim()
        ?? throw new TenantDataAssetValidationException("A platform actor email is required.");

    private static TenantDataAssetDto ToDto(TenantDataAsset row) => new(
        row.Id, row.TenantId, row.LogicalKey, row.AssetType, row.OpaqueProviderReference,
        row.Region, row.Classification, row.Disposition, row.BackupPolicyReference,
        row.BackupPolicyVersion, row.Status, row.VerifiedBusinessUnitId,
        row.VerificationEvidenceReference, row.VerificationEvidenceSha256,
        row.VerificationVersion, row.VerifiedOn, row.VerifiedBy, row.Version);

    private static bool SameRegistration(
        TenantDataAsset row, string providerReference, string region, string classification,
        string disposition, string backupPolicy, int backupPolicyVersion) =>
        string.Equals(row.AssetType, TenantDataAssetTypes.PostgreSqlTenantScope, StringComparison.Ordinal)
        && string.Equals(row.OpaqueProviderReference, providerReference, StringComparison.Ordinal)
        && string.Equals(row.Region, region, StringComparison.OrdinalIgnoreCase)
        && string.Equals(row.Classification, classification, StringComparison.Ordinal)
        && string.Equals(row.Disposition, disposition, StringComparison.Ordinal)
        && string.Equals(row.BackupPolicyReference, backupPolicy, StringComparison.Ordinal)
        && row.BackupPolicyVersion == backupPolicyVersion;

    private static bool SameVerification(
        TenantDataAsset row, long businessUnitId, string region, string evidenceReference, string evidenceSha256) =>
        string.Equals(row.Status, TenantDataAssetStatuses.Verified, StringComparison.Ordinal)
        && row.VerifiedBusinessUnitId == businessUnitId
        && string.Equals(row.Region, region, StringComparison.OrdinalIgnoreCase)
        && string.Equals(row.VerificationEvidenceReference, evidenceReference, StringComparison.Ordinal)
        && string.Equals(row.VerificationEvidenceSha256, evidenceSha256, StringComparison.Ordinal);
}
