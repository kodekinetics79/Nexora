using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Services;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Platform.DataAssets;

/// <param name="DataRegionRecorded">
/// Non-null when the tenant carried no contractual region and this action recorded the one the
/// deployment declares. Null when the tenant already had one — a region that is already recorded
/// is a contractual claim and is never rewritten here.
/// </param>
/// <param name="PrimaryScopeState">verified, already-verified, or null when nothing was verified.</param>
public sealed record ApplyPlatformDataBoundariesResult(
    string? DataRegionRecorded,
    string? PrimaryScopeState,
    string? EvidenceReference,
    IReadOnlyList<string> RegisteredLogicalKeys,
    IReadOnlyList<string> AlreadyRegisteredLogicalKeys,
    TenantActivationDataDecisionDto Decision);

public interface IPlatformDataBoundaryApplier
{
    /// <summary>
    /// Registers and verifies one tenant's data boundaries from the deployment manifest, on demand.
    /// </summary>
    Task<ApplyPlatformDataBoundariesResult> ApplyAsync(
        long tenantId, ClaimsPrincipal actor, HttpContext? httpContext, CancellationToken ct);
}

/// <summary>
/// The same automation the provisioning run performs, reachable for a tenant that was provisioned
/// before this deployment declared its estate.
///
/// <para><b>The defect this closes.</b> <see cref="PlatformDataBoundaryProvisioner"/> has always
/// been able to register and verify these boundaries from configuration, but only at one instant:
/// the <c>data-boundaries</c> provisioning step. Every tenant created before
/// <c>Platform:DataBoundaries</c> was populated — and every tenant on a deployment that populated
/// it afterwards — was left on the manual path forever, which is an operator typing this
/// deployment's own provider reference, region, backup policy and a SHA-256 of an evidence
/// document about a database the platform runs itself. Re-running the whole provisioning step is
/// not an answer either: it is retriable, but it lives on an execution record that a tenant which
/// finished provisioning months ago should not be dragged back through.</para>
///
/// <para><b>What it does NOT relax.</b> Nothing. It calls the same provisioner, which calls the
/// same registry and the same probe, so a boundary is still refused when it conflicts with what is
/// registered, and the PostgreSQL scope is still verified only from a live observation of the
/// running database. A deployment that declares nothing still gets the manual path. The one thing
/// this adds is the region backfill below, and only into an empty column.</para>
/// </summary>
public sealed class PlatformDataBoundaryApplier(
    ErpRfqAutomationContext db,
    IPlatformDataBoundaryManifest manifest,
    IPlatformDataBoundaryProvisioner provisioner,
    TenantDataAssetRegistryService registry,
    IPlatformAuditService audit) : IPlatformDataBoundaryApplier
{
    public const string RegionAction = "tenant.data-region.update";

    /// <summary>
    /// Written into the audit record for the backfill. The region is not being decided here — it is
    /// being copied from the deployment's own declaration, and the record says so in those words.
    /// </summary>
    private const string RegionReason =
        "Recorded from the platform data-boundary manifest (Platform:DataBoundaries:PostgreSqlTenantScope:Region) "
        + "while registering this tenant's data boundaries. This is the region the deployment declares for "
        + "the database every tenant lives in, not an operator's assertion about one customer.";

    public async Task<ApplyPlatformDataBoundariesResult> ApplyAsync(
        long tenantId, ClaimsPrincipal actor, HttpContext? httpContext, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);

        var primary = manifest.For(TenantDataAssetTypes.PostgreSqlTenantScope);
        if (primary is null)
            throw new TenantDataAssetValidationException(
                manifest.DefectFor(TenantDataAssetTypes.PostgreSqlTenantScope) is { Length: > 0 } defect
                    ? "This deployment declares its primary PostgreSQL boundary, but the declaration is "
                      + $"unusable and was refused: {defect} Correct Platform:DataBoundaries:PostgreSqlTenantScope "
                      + "and restart the API, or register the boundary by hand."
                    : "This deployment has not declared what its own database is, so there is nothing to "
                      + "register from. Set Platform__DataBoundaries__PostgreSqlTenantScope__OpaqueProviderReference, "
                      + "__Region, __BackupPolicyReference and __BackupPolicyVersion on the API service, or "
                      + "register the boundary by hand.");

        // The registration runs as the automation, not as the operator who pressed the button —
        // exactly as the provisioning step does it. Every row it leaves behind (CreatedBy,
        // VerifiedBy) has to make it obvious that a probe did this and not a person, while the
        // audit record still carries the operator's own platform id so "who asked for this" is
        // never lost. An operator principal with no usable id is refused rather than audited to
        // nobody: an automated registration nobody can see afterwards is worse than the manual form.
        var operatorId = actor.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                         ?? actor.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!long.TryParse(operatorId, out var parsedOperatorId) || parsedOperatorId <= 0)
            throw new TenantDataAssetValidationException(
                "This session carries no platform actor id, so an automated registration could not "
                + "be audited to anybody. Sign in again, or register the boundary by hand.");

        var automation = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(JwtRegisteredClaimNames.Sub, parsedOperatorId.ToString()),
            new Claim("email", PlatformAutomationActors.Provisioning)
        ], nameof(PlatformDataBoundaryApplier)));

        var tenant = await db.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
                         .SingleOrDefaultAsync(x => x.Id == tenantId, ct)
                     ?? throw new TenantDataAssetNotFoundException($"Tenant {tenantId} was not found.");

        if (tenant.Status == TenantStatus.Archived)
            throw new TenantDataAssetConflictException(
                "An archived tenant is a retention-controlled record. Restore it before registering "
                + "data boundaries against it.");

        // A region that is already recorded is left exactly as it is, even when it disagrees with
        // the manifest. Rewriting it would be satisfying a residency control by editing the claim
        // instead of the data — the probe below fails on the disagreement and names both sides,
        // which is the outcome an operator has to see.
        var backfillRegion = string.IsNullOrWhiteSpace(tenant.DataRegion) ? primary.Region : null;

        string? recordedRegion = null;
        PlatformDataBoundaryProvisionResult result = null!;

        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            recordedRegion = null;
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            if (backfillRegion is not null)
            {
                var target = await db.Set<Tenant>().IgnoreQueryFilters()
                    .SingleAsync(x => x.Id == tenantId, ct);

                // Re-read rather than trusted from above: another operator may have recorded a
                // region between the read and this transaction, and theirs is the contractual one.
                if (string.IsNullOrWhiteSpace(target.DataRegion))
                {
                    target.DataRegion = backfillRegion;
                    target.ModifiedOn = DateTime.UtcNow;
                    target.ModifiedBy = actor.FindFirst("email")?.Value ?? PlatformAutomationActors.Provisioning;
                    await db.SaveChangesAsync(ct);
                    await audit.WriteAsync(actor, RegionAction, nameof(Tenant), tenantId.ToString(),
                        new { from = (string?)null, to = backfillRegion, reason = RegionReason },
                        tenantId, httpContext, ct);
                    recordedRegion = backfillRegion;
                }
            }

            result = await provisioner.EnsureAsync(tenantId, automation, ct);

            // A declared boundary that could not be honoured rolls the whole action back, region
            // backfill included: half of it is a tenant carrying a residency claim nobody verified.
            if (result.Failure is { Length: > 0 } failure)
                throw new TenantDataAssetConflictException(failure);

            await tx.CommitAsync(ct);
        });

        var decision = await registry.ActivationDataDecisionAsync(tenantId, ct);
        return new ApplyPlatformDataBoundariesResult(
            recordedRegion, result.PrimaryScopeState, result.EvidenceReference,
            result.RegisteredLogicalKeys, result.AlreadyRegisteredLogicalKeys, decision);
    }
}
