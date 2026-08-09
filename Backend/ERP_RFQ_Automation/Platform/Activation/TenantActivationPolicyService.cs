using System.Security.Claims;
using ERP_RFQ_Automation.Billing;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.DataAssets;
using ERP_RFQ_Automation.Platform.Entitlements;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Provisioning;
using ERP_RFQ_Automation.Platform.Services;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Platform.Lifecycle;

namespace ERP_RFQ_Automation.Platform.Activation;

public interface ITenantActivationPolicyService
{
    Task<TenantActivationDecision?> EvaluateAsync(long tenantId, CancellationToken ct = default);
    Task<TenantActivationDecision> ActivateAsync(long tenantId, ClaimsPrincipal actor,
        HttpContext httpContext, CancellationToken ct = default);
    Task<ActivationControlEvidenceReceipt> RecordControlEvidenceAsync(long tenantId, string controlCode,
        RecordActivationControlEvidenceRequest request, ClaimsPrincipal actor, HttpContext httpContext,
        CancellationToken ct = default);
}

/// <summary>The only authority permitted to transition a newly provisioned tenant to Active.</summary>
public sealed class TenantActivationPolicyService(
    ErpRfqAutomationContext db,
    IPlatformAuditService audit,
    ITenantAccessService tenantAccess) : ITenantActivationPolicyService
{
    private const long ActivationAdvisorySalt = unchecked((long)0x4E45584F52414143UL); // NEXORAAC
    public async Task<TenantActivationDecision?> EvaluateAsync(long tenantId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var tenant = await db.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
            .Include(x => x.Plan).SingleOrDefaultAsync(x => x.Id == tenantId, ct);
        if (tenant is null) return null;

        var controls = new List<ActivationControlDecision>();
        void Add(string code, bool satisfied, string detail, params string?[] refs) =>
            controls.Add(new(code, satisfied, detail,
                refs.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToArray()));

        Add("identity.legal-customer",
            Has(tenant.LegalName) && Has(tenant.RegistrationNumber) && Has(tenant.CountryCode) && Has(tenant.ContactEmail),
            "Legal name, registration number, country and customer contact must be recorded.", $"tenant:{tenant.Id}");

        var plan = tenant.Plan;
        Add("commercial.plan", plan is { IsActive: true },
            "An active trial, standard or approved custom plan must be assigned.",
            plan is null ? null : $"plan:{plan.Id}:created:{plan.CreatedOn:O}");

        Add("billing.account-recipient", Has(tenant.BillingContactName) && Has(tenant.BillingContactEmail)
             && Has(tenant.BillingAddress) && tenant.PaymentTermsDays is > 0,
            "Billing recipient, billing address and positive payment terms must be recorded.", $"tenant:{tenant.Id}:billing");

        Add("billing.currency-tax", string.Equals(tenant.BaseCurrencyCode, "USD", StringComparison.OrdinalIgnoreCase)
             && (Has(tenant.TaxNumber) || tenant.BillingMode is TenantBillingMode.Internal or TenantBillingMode.Partner),
            "The supported USD billing currency and tax identity or an explicit non-billable treatment must be recorded.",
            $"tenant:{tenant.Id}:tax");

        RateCard? card = null;
        if (tenant.RateCardId is long cardId)
            card = await db.Set<RateCard>().AsNoTracking().Include(x => x.Lines)
                .SingleOrDefaultAsync(x => x.Id == cardId, ct);
        Add("commercial.rate-card", card is { IsActive: true }
             && string.Equals(card.Currency, tenant.BaseCurrencyCode, StringComparison.OrdinalIgnoreCase)
             && card.EffectiveFromUtc <= now
             && (card.EffectiveToUtc is null || card.EffectiveToUtc > now) && card.Lines.Count > 0,
            "A pinned, effective rate card with at least one priced meter is required.",
            card is null ? null : $"rate-card:{card.Id}:v{card.Version}");

        Add("commercial.approved-terms", tenant.ContractStartOn is not null && tenant.BillingStartsOn is not null
             && (tenant.ContractEndOn is null || tenant.ContractEndOn >= tenant.ContractStartOn),
            "Effective commercial and billing dates must be approved and internally consistent.", $"tenant:{tenant.Id}:terms");

        var typed = plan is not null && TypedEntitlementCatalog.TryParse(plan.Features, out var values, out _)
                    && TypedEntitlementCatalog.Keys.All(values.ContainsKey);
        Add("entitlements.typed-hard-limits", typed && plan is
            { MaxSeats: > 0, MaxDocsPerMonth: > 0, MaxConcurrentExtractionJobs: > 0, Weight: > 0 },
            "All typed keys and finite positive seat, document and extraction limits are required.",
            plan is null ? null : $"plan:{plan.Id}:entitlements");

        var adminActive = tenant.PrimaryBusinessUnitId is long bu && await (
                from user in db.Users.IgnoreQueryFilters().AsNoTracking()
                join role in db.SetupMasters.IgnoreQueryFilters().AsNoTracking()
                    on new { Id = user.RoleId!.Value, Bu = user.Buid!.Value }
                    equals new { Id = role.SetupId, Bu = role.BusinessUnitId }
                where user.Buid == bu && user.IsActive == true && user.RoleId != null
                      && role.IsActive == true && role.RoleRank >= RoleRanks.Admin
                select user.Id).AnyAsync(ct);
        Add("admin.first-activated", adminActive,
            "The founding privileged tenant administrator must have activated their account.",
            tenant.PrimaryBusinessUnitId is long adminBu ? $"business-unit:{adminBu}:admin" : null);

        // The tenant identity plane has no persisted MFA assurance yet. Do not infer it from a
        // password or from platform-operator MFA; they are different principals.
        var securityEvidence = await ActiveEvidenceAsync(tenantId, "security.privileged-mfa-policy", now, ct);
        Add("security.privileged-mfa-policy", securityEvidence is { Disposition: "approved" },
            "Tenant privileged MFA/security-policy evidence requires a current Owner-approved attestation.",
            securityEvidence?.EvidenceReference);

        var asset = await db.Set<TenantDataAsset>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.TenantId == tenantId && x.LogicalKey == TenantDataAssetRegistryService.PostgreSqlLogicalKey, ct);
        var dataReady = asset is { Status: TenantDataAssetStatuses.Verified }
                        && asset.VerifiedBusinessUnitId == tenant.PrimaryBusinessUnitId
                        && string.Equals(asset.Region, tenant.DataRegion, StringComparison.OrdinalIgnoreCase)
                        && asset.VerificationVersion > 0 && Has(asset.VerificationEvidenceSha256)
                        && asset.BackupPolicyVersion > 0 && Has(asset.BackupPolicyReference);
        Add("data.residency-isolation", dataReady && Has(tenant.DataRegion),
            "Hosting region, tenant database scope and versioned backup-policy evidence must agree.",
            asset is null ? null : $"tenant-data-asset:{asset.Id}:v{asset.Version}", asset?.VerificationEvidenceReference);

        var auditHealthy = await db.Set<PlatformAuditLog>().AsNoTracking()
            .AnyAsync(x => x.ActAsTenantId == tenantId && x.Result == PlatformAuditResults.Success, ct);
        Add("audit.health", auditHealthy,
            "At least one persisted successful privileged tenant audit occurrence is required.", $"platform-audit:tenant:{tenant.Id}");

        var integrationEvidence = await ActiveEvidenceAsync(tenantId, "integrations.mandatory", now, ct);
        Add("integrations.mandatory", integrationEvidence is { Disposition: "approved" or "deferred" },
            "Mandatory integration health requires current evidence or an explicit effective deferral.",
            integrationEvidence?.EvidenceReference);

        var provisioningComplete = tenant.PrimaryBusinessUnitId is not null &&
            (await db.Set<ProvisioningExecution>().AsNoTracking().AnyAsync(x =>
                 x.TenantId == tenantId && x.State == ProvisioningExecutionState.Succeeded, ct)
             || await db.Set<PlatformAuditLog>().AsNoTracking().AnyAsync(x =>
                 x.ActAsTenantId == tenantId && x.Action == "tenant.provision" && x.Result == PlatformAuditResults.Success, ct));
        Add("provisioning.completed-verified", provisioningComplete,
            "Provisioning must be terminally successful and auditable.", $"tenant:{tenant.Id}:provisioning");

        var offboarding = await db.Set<TenantOffboarding>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId, ct);
        var destructiveStateClear = offboarding is null
            || offboarding.Stage == TenantOffboardingStage.NotScheduled
               && offboarding.PurgeStartedOn is null && offboarding.PurgeExecutedOn is null
               && offboarding.PersonalDataErasedOn is null;
        Add("data.lifecycle-operational", destructiveStateClear,
            "Pending deletion, purge, or erasure state cannot become operational.",
            offboarding is null ? null : $"tenant-offboarding:{offboarding.Id}");

        var activeLegalHold = await db.Set<TenantLegalHold>().AsNoTracking()
            .AnyAsync(x => x.TenantId == tenantId && x.ReleasedOn == null, ct);

        var blockers = controls.Where(x => !x.Satisfied).Select(x => x.Code).ToArray();
        var warnings = new List<string>();
        if (tenant.Status == TenantStatus.Active)
            warnings.Add("Tenant is already Active; this is a current-state reassessment, not transition evidence.");

        var pastDue = await db.Set<SubscriptionInvoice>().AsNoTracking().AnyAsync(x =>
            x.TenantId == tenantId && x.DueAtUtc < now
            && x.TotalAmount > x.CreditedAmount + x.PaidAmount && x.Status != SubscriptionInvoiceStatus.Void, ct);
        return new TenantActivationDecision(tenant.Id, blockers.Length == 0,
            CommercialState(tenant, pastDue), AccessState(tenant), DataState(offboarding),
            activeLegalHold ? "ACTIVE" : "NONE", controls, blockers,
            warnings, TenantActivationPolicy.Version, now);
    }

    public async Task<TenantActivationDecision> ActivateAsync(long tenantId, ClaimsPrincipal actor,
        HttpContext httpContext, CancellationToken ct = default)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (db.Database.IsNpgsql())
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_advisory_xact_lock({tenantId ^ ActivationAdvisorySalt});", ct);
            var tenant = await db.Set<Tenant>().IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == tenantId, ct)
                ?? throw new KeyNotFoundException($"Tenant {tenantId} was not found.");
            if (tenant.Status != TenantStatus.Provisioning)
                throw new InvalidOperationException($"Only a Provisioning tenant can be activated; current status is {tenant.Status}.");

            var decision = await EvaluateAsync(tenantId, ct) ?? throw new KeyNotFoundException();
            if (!decision.Ready) throw new TenantActivationBlockedException(decision);

            tenant.Status = TenantStatus.Active;
            tenant.ModifiedOn = DateTime.UtcNow;
            tenant.ModifiedBy = actor.FindFirst("email")?.Value ?? actor.Identity?.Name ?? "platform";
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(actor, "tenant.activate", nameof(Tenant), tenant.Id.ToString(),
                new { decision.PolicyVersion, decision.EvaluatedAtUtc, Evidence = decision.Controls.SelectMany(x => x.EvidenceReferences).Distinct() },
                tenant.Id, httpContext, ct);
            await tx.CommitAsync(ct);
            if (tenant.PrimaryBusinessUnitId is long bu) tenantAccess.Evict(bu);
            return decision;
        });
    }

    public async Task<ActivationControlEvidenceReceipt> RecordControlEvidenceAsync(long tenantId, string controlCode,
        RecordActivationControlEvidenceRequest request, ClaimsPrincipal actor, HttpContext httpContext,
        CancellationToken ct = default)
    {
        if (controlCode is not ("security.privileged-mfa-policy" or "integrations.mandatory"))
            throw new ArgumentOutOfRangeException(nameof(controlCode), "This control is derived from authoritative system state.");
        var disposition = request.Disposition.Trim().ToLowerInvariant();
        if (disposition is not ("approved" or "deferred")
            || controlCode == "security.privileged-mfa-policy" && disposition != "approved")
            throw new ArgumentException("Disposition is not permitted for this activation control.");
        if (!SafeEvidenceReference(request.EvidenceReference)
            || request.EvidenceSha256.Length != 64 || request.EvidenceSha256.Any(c => !Uri.IsHexDigit(c))
            || request.EffectiveFromUtc.Kind != DateTimeKind.Utc
            || request.EffectiveToUtc is { Kind: not DateTimeKind.Utc }
            || request.EffectiveToUtc <= request.EffectiveFromUtc
            || string.IsNullOrWhiteSpace(request.Reason))
            throw new ArgumentException("Evidence reference, SHA-256, UTC effective window and reason are required.");
        if (!await db.Set<Tenant>().IgnoreQueryFilters().AnyAsync(x => x.Id == tenantId, ct))
            throw new KeyNotFoundException();

        await audit.WriteAsync(actor, EvidenceAction(controlCode), nameof(Tenant), tenantId.ToString(),
            new { disposition, request.EvidenceReference, evidenceSha256 = request.EvidenceSha256.ToLowerInvariant(),
                request.EffectiveFromUtc, request.EffectiveToUtc, reason = request.Reason.Trim(),
                PolicyVersion = TenantActivationPolicy.Version }, tenantId, httpContext, ct);
        return new(tenantId, controlCode, disposition, request.EvidenceReference,
            request.EffectiveFromUtc, request.EffectiveToUtc, TenantActivationPolicy.Version);
    }

    private async Task<Evidence?> ActiveEvidenceAsync(long tenantId, string code, DateTime now, CancellationToken ct)
    {
        var metadata = await db.Set<PlatformAuditLog>().AsNoTracking()
            .Where(x => x.ActAsTenantId == tenantId && x.Action == EvidenceAction(code)
                && x.Result == PlatformAuditResults.Success)
            .OrderByDescending(x => x.Id).Select(x => x.Metadata).FirstOrDefaultAsync(ct);
        if (metadata is null) return null;
        try
        {
            var value = JsonSerializer.Deserialize<Evidence>(metadata, new JsonSerializerOptions
                { PropertyNameCaseInsensitive = true });
            return value is not null && value.EffectiveFromUtc <= now
                   && (value.EffectiveToUtc is null || value.EffectiveToUtc > now) ? value : null;
        }
        catch (JsonException) { return null; }
    }

    private static string EvidenceAction(string code) => $"tenant.activation-control.{code}";
    private sealed record Evidence(string Disposition, string EvidenceReference,
        DateTime EffectiveFromUtc, DateTime? EffectiveToUtc);

    private static bool Has(string? value) => !string.IsNullOrWhiteSpace(value);
    private static bool SafeEvidenceReference(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "urn")
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
            return false;
        return uri.Scheme == "urn" || !string.IsNullOrWhiteSpace(uri.Host);
    }
    private static string CommercialState(Tenant t, bool pastDue) => t.Status == TenantStatus.Archived ? "TERMINATED" :
        t.Status == TenantStatus.PastDue || pastDue ? "PAST_DUE" : t.BillingMode == TenantBillingMode.Trial ? "TRIAL" :
        t.Status == TenantStatus.Active ? "ACTIVE" : "PROSPECT";
    private static string DataState(TenantOffboarding? state) => state switch
    {
        { Stage: TenantOffboardingStage.Purged } => "PURGED",
        { PurgeExecutedOn: not null } => "PURGING",
        { PurgeStartedOn: not null } => "PURGE_CLAIMED",
        { Stage: TenantOffboardingStage.PendingDeletion } => "DELETION_SCHEDULED",
        _ => "LIVE"
    };
    private static string AccessState(Tenant t) => t.Status switch
    {
        TenantStatus.Active => "ENABLED", TenantStatus.PastDue => "RESTRICTED_PAST_DUE",
        TenantStatus.Suspended => "SUSPENDED",
        TenantStatus.Archived => "CLOSED", _ => "RESTRICTED"
    };
}
