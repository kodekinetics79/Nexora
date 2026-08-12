using System.Security.Claims;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Services;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Platform.DataAssets;

/// <summary>
/// The identities automated platform work acts under.
///
/// <para>Deliberately NOT the operator who pressed submit. Every row these writes leave behind —
/// <c>TenantDataAssets.CreatedBy</c>, <c>VerifiedBy</c>, and the actor email on the audit record —
/// has to make it obvious at a glance that a probe did this and not a person, because the whole
/// value of the manual path was that a human had looked. The audit row still carries the
/// requesting operator in its metadata, so "who asked for this tenant" is never lost; what is not
/// available is any way to read an automated verification as a human attestation.</para>
/// </summary>
public static class PlatformAutomationActors
{
    public const string Provisioning = "system:provisioning";
}

/// <param name="Configured">False when the deployment declares no boundaries at all.</param>
/// <param name="Failure">
/// Non-null when something the manifest DID declare could not be honoured. The caller must fail on
/// this: a boundary that was declared and could not be registered or verified is not the same
/// thing as one that was never declared.
/// </param>
public sealed record PlatformDataBoundaryProvisionResult(
    bool Configured,
    string? Failure,
    string? PrimaryScopeState,
    string? EvidenceReference,
    string? EvidenceSha256,
    IReadOnlyList<string> RegisteredLogicalKeys,
    IReadOnlyList<string> AlreadyRegisteredLogicalKeys,
    IReadOnlyList<string> UndeclaredAssetTypes,
    IReadOnlyList<string> ManifestDefects);

public interface IPlatformDataBoundaryProvisioner
{
    /// <summary>
    /// Registers and, where it can, verifies this tenant's boundaries on the AMBIENT scoped
    /// context — deliberately not on one the caller hands in.
    ///
    /// <para>The provisioning step that calls this is already inside its own transaction on that
    /// same scoped context, and every write below has to land in it: the asset rows, the audit
    /// records and the step's own verdict commit together or not at all. Taking a context as a
    /// parameter would make that a coincidence of wiring rather than a guarantee, and the failure
    /// it invites — half the work on a connection that commits and half on one that does not — is
    /// exactly the class of bug the step journal was built to stop.</para>
    /// </summary>
    Task<PlatformDataBoundaryProvisionResult> EnsureAsync(
        long tenantId, ClaimsPrincipal actor, CancellationToken ct);
}

/// <summary>
/// Registers, and where it can genuinely verify, the tenant data boundaries this deployment owns.
///
/// <para><b>The defect.</b> A Nexora-hosted tenant could not be activated until an operator had
/// hand-typed the platform's own provider reference, region and backup policy into a form, and
/// hand-hashed an evidence document about a database the platform runs itself. Deletion
/// certification then demanded the same exercise nine times over, once per boundary type. Three of
/// the four controls left standing on a fresh tenant were the platform asking a human to describe
/// the platform.</para>
///
/// <para><b>What it does NOT do.</b> It never writes a <c>TenantDataAsset</c> row directly — every
/// write goes through <see cref="TenantDataAssetRegistryService"/>, which holds the invariants that
/// matter: a boundary whose region disagrees with the tenant's contractual data region is refused,
/// a re-registration that differs from what is already there is a conflict rather than an
/// overwrite, and a verification is refused unless the observed scope IS the tenant's primary
/// business unit. Bypassing it to save a validation round-trip would be bypassing the only thing
/// standing between "automatic" and "asserted".</para>
///
/// <para><b>Only the primary PostgreSQL scope is VERIFIED.</b> The other boundaries are registered
/// and stay <c>Registered</c>, because the platform can observe its own database and cannot
/// observe a subprocessor's deletion queue. Registration is what deletion certification needs from
/// them — it stops asking an operator to retype the architecture — and it is emphatically not a
/// claim that anything about them has been checked.</para>
/// </summary>
public sealed class PlatformDataBoundaryProvisioner(
    ErpRfqAutomationContext db,
    IPlatformDataBoundaryManifest manifest,
    TenantDataAssetRegistryService registry,
    ITenantPostgreSqlScopeProbe probe,
    IPlatformAuditService audit) : IPlatformDataBoundaryProvisioner
{
    public const string ProbeAction = "tenant.data-boundary.probe";

    private const string RegistrationReason =
        "Registered automatically from the platform data-boundary manifest (Platform:DataBoundaries) "
        + "during tenant provisioning. The provider reference, region and backup policy are this "
        + "deployment's declared configuration, not an operator's recollection.";

    public async Task<PlatformDataBoundaryProvisionResult> EnsureAsync(
        long tenantId, ClaimsPrincipal actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);

        var defects = manifest.Defects.Select(x => $"{x.AssetType}: {x.Reason}").ToArray();
        var undeclared = TenantDataAssetTypes.All
            .Where(type => manifest.For(type) is null)
            .OrderBy(type => type, StringComparer.Ordinal)
            .ToArray();

        // Absent configuration is the degraded path, and it is a SUCCESS: the deployment has said
        // nothing about its estate, so the automation says nothing either and the operator's
        // register-then-verify screens work exactly as they did. Inventing a provider reference
        // here would be the platform certifying a residency claim nobody made.
        if (!manifest.IsConfigured)
            return new PlatformDataBoundaryProvisionResult(
                false, null, null, null, null, [], [], undeclared, defects);

        var tenant = await db.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
                         .SingleOrDefaultAsync(x => x.Id == tenantId, ct)
                     ?? throw new InvalidOperationException($"Tenant {tenantId} was not found.");

        string? primaryState = null;
        string? evidenceReference = null;
        string? evidenceSha256 = null;
        var registered = new List<string>();
        var alreadyRegistered = new List<string>();

        if (manifest.For(TenantDataAssetTypes.PostgreSqlTenantScope) is { } primary)
        {
            var observation = await probe.ObserveAsync(db, tenant, primary, ct);

            // A disagreeing probe FAILS the caller. It does not register a boundary it could not
            // confirm and it does not fall back to registering-without-verifying, because a
            // Registered-but-unverified asset reads to the activation gate as "the operator has
            // done half the work", which is a different and untrue statement.
            if (!observation.Satisfied)
                return new PlatformDataBoundaryProvisionResult(
                    true,
                    $"The primary PostgreSQL tenant scope was not observed to match the platform "
                    + $"data-boundary manifest, so it has NOT been registered or verified. "
                    + $"{observation.Failure} (probe {observation.EvidenceReference})",
                    "probe-failed", observation.EvidenceReference, observation.EvidenceSha256,
                    registered, alreadyRegistered, undeclared, defects);

            var asset = await RegisterAsync(tenantId, primary, actor, ct, registered, alreadyRegistered);

            // The document the hash is OF, written verbatim into the audit record. Stored as a
            // JSON STRING rather than as a nested object on purpose: a nested object would be
            // re-serialised by the audit writer, and a re-serialisation that reorders one property
            // produces different bytes and therefore a different SHA-256 — which would make the
            // evidence reference unrecomputable from the only place the document is kept.
            await audit.WriteAsync(actor, ProbeAction, nameof(TenantDataAsset), asset.Id.ToString(),
                new
                {
                    probe = TenantPostgreSqlScopeProbe.ProbeVersion,
                    logicalKey = primary.LogicalKey,
                    observation.EvidenceReference,
                    observation.EvidenceSha256,
                    observationJson = observation.CanonicalJson,
                    automation = PlatformAutomationActors.Provisioning
                }, tenantId, null, ct);

            if (asset.Status == TenantDataAssetStatuses.Verified
                && asset.VerifiedBusinessUnitId == observation.ObservedBusinessUnitId)
            {
                primaryState = "already-verified";
                evidenceReference = asset.VerificationEvidenceReference;
                evidenceSha256 = asset.VerificationEvidenceSha256;
            }
            else
            {
                var verified = await registry.VerifyAsync(tenantId, asset.Id, new VerifyTenantDataAssetRequest(
                        asset.Version,
                        observation.ObservedBusinessUnitId
                            ?? throw new InvalidOperationException(
                                "A satisfied probe always carries the observed business unit."),
                        observation.ObservedRegion,
                        observation.EvidenceReference,
                        observation.EvidenceSha256,
                        "Verified from a recorded platform probe of this tenant's PostgreSQL scope; "
                        + $"see audit action {ProbeAction} for the observation the hash covers."),
                    actor, null, ct);
                primaryState = "verified";
                evidenceReference = verified.VerificationEvidenceReference;
                evidenceSha256 = verified.VerificationEvidenceSha256;
            }
        }

        foreach (var boundary in manifest.Boundaries
                     .Where(x => x.AssetType != TenantDataAssetTypes.PostgreSqlTenantScope))
            await RegisterAsync(tenantId, boundary, actor, ct, registered, alreadyRegistered);

        return new PlatformDataBoundaryProvisionResult(
            true, null, primaryState, evidenceReference, evidenceSha256,
            registered, alreadyRegistered, undeclared, defects);
    }

    private async Task<TenantDataAssetDto> RegisterAsync(
        long tenantId, PlatformDataBoundary boundary,
        ClaimsPrincipal actor, CancellationToken ct, List<string> registered, List<string> alreadyRegistered)
    {
        var existed = await db.Set<TenantDataAsset>().AsNoTracking()
            .AnyAsync(x => x.TenantId == tenantId && x.LogicalKey == boundary.LogicalKey, ct);

        var asset = await registry.RegisterAsync(tenantId, new RegisterTenantDataAssetRequest(
            boundary.LogicalKey,
            boundary.OpaqueProviderReference,
            boundary.Region,
            boundary.Classification,
            boundary.Disposition,
            boundary.BackupPolicyReference,
            boundary.BackupPolicyVersion,
            RegistrationReason,
            boundary.AssetType), actor, null, ct);

        (existed ? alreadyRegistered : registered).Add(boundary.LogicalKey);
        return asset;
    }
}
