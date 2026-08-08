using ERP_RFQ_Automation.Billing;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Platform.Entitlements;

/// <summary>
/// The capacity a tenant gets while its commercial configuration is incomplete.
///
/// <para><b>The leak this closes.</b> "No plan → no limit" was the contracted fail-open,
/// and it is right for a legacy BusinessUnit with no platform Tenant row — that customer
/// predates the control plane and must not be broken by it. It is wrong for a Tenant row
/// that exists, says <c>Billable</c>, and simply has no plan attached: that tenant is
/// charged no subscription (the billing statement's own revenue-risk marker) AND handed
/// unmetered seats and unmetered documents. The same omission opened both taps. The same
/// is true of an <c>Internal</c> or <c>Partner</c> exemption with nothing written down:
/// an exemption nobody recorded is not a decision, it is an oversight wearing a decision's
/// clothes, and it should not carry unlimited capacity.</para>
///
/// <para><b>Why a floor and not a refusal.</b> Denying outright would brick a live
/// customer over an operator's data-entry gap, and cutting a customer off is a commercial
/// decision, not an inference a quota check gets to make. A small, non-zero, METERED
/// allowance bounds the exposure, keeps the tenant working, and surfaces the account: the
/// denial message names what is unconfigured, so the first person to hit it knows exactly
/// what is unfinished and where to fix it.</para>
///
/// <para><b>Why a grace window.</b> Provisioning creates the Tenant before a plan is
/// necessarily chosen, so a brand-new tenant legitimately has none for a while. The
/// window is measured from <c>Tenant.CreatedOn</c> — the moment provisioning committed —
/// so setup is never interrupted and a tenant still unconfigured a fortnight later is not
/// mid-setup, it is forgotten.</para>
/// </summary>
public static class UnplannedTenantAllowance
{
    /// <summary>How long after provisioning an unconfigured tenant keeps full capacity.</summary>
    public static readonly TimeSpan ProvisioningGrace = TimeSpan.FromDays(14);

    /// <summary>Active users an unconfigured tenant may hold past the grace window.</summary>
    public const int MaxSeats = 3;

    /// <summary>Documents per UTC month an unconfigured tenant may submit past the grace window.</summary>
    public const int MaxDocsPerMonth = 50;

    /// <summary>
    /// Whether the floor binds this resolution.
    ///
    /// <para>Every "unknown" answers no. A snapshot with no Tenant row is the legacy
    /// fail-open and stays unlimited; a snapshot whose commercial terms could not be
    /// resolved (a reduced model, a test double, an execution role without the column
    /// grant) stays unlimited too, because tightening capacity on the strength of a value
    /// we did not actually read is how a missing grant becomes a customer outage. That is
    /// also what keeps this predicate honest about the one thing it cannot see: without
    /// SELECT on <c>BillingModeReason</c> an unrecorded exemption is invisible here, so it
    /// is left uncapped and is instead reported by the platform-plane revenue readout,
    /// which runs under a role that can read it.</para>
    ///
    /// <para><c>Trial</c> is treated exactly like <c>Billable</c> — an unbounded trial is
    /// the same leak wearing a friendlier name.</para>
    /// </summary>
    public static bool AppliesTo(TenantAccessSnapshot access, DateTime nowUtc)
        => access.HasTenant
           && access.CommercialConfigurationRequired
           && access.CreatedOn is DateTime createdOn
           && nowUtc - createdOn > ProvisioningGrace;

    internal static string Reason(TenantAccessSnapshot access, string resource, int limit, int current)
    {
        // Both shapes reach here with no plan, so the cause is told apart by billing mode:
        // a Billable/Trial tenant is missing its plan, an Internal/Partner one is missing
        // the written decision that would justify serving it for nothing.
        var cause = access.BillingMode is TenantBillingMode.Internal or TenantBillingMode.Partner
            ? $"This tenant's {access.BillingMode} billing exemption has no recorded reason"
            : "No plan is assigned to this tenant";
        return $"{cause}, so it is running on the unconfigured-tenant allowance of {limit} {resource} and " +
               $"{current} are already in use. Billing mode is {access.BillingMode?.ToString() ?? "unknown"} and the " +
               $"tenant was provisioned more than {ProvisioningGrace.TotalDays:0} days ago. Complete its commercial " +
               "configuration in the platform console's billing screen to restore full capacity.";
    }
}

/// <summary>
/// Default <see cref="IEntitlementService"/>. Counts (active users, monthly
/// extraction jobs) are read live — they are cheap indexed counts and must not lag —
/// while the tenant→plan resolution is served from the ~60s
/// <see cref="ITenantAccessService"/> cache.
/// </summary>
public sealed class EntitlementService : IEntitlementService
{
    private readonly ITenantAccessService _tenantAccess;
    private readonly ErpRfqAutomationContext _context;

    public EntitlementService(ITenantAccessService tenantAccess, ErpRfqAutomationContext context)
    {
        _tenantAccess = tenantAccess;
        _context = context;
    }

    public async Task<EntitlementDecision> CheckSeatAvailabilityAsync(long businessUnitId, CancellationToken ct = default)
    {
        var access = await _tenantAccess.GetAccessAsync(businessUnitId, ct);

        // A plan with MaxSeats 0 is an operator saying "unlimited" about a tenant they
        // have already priced; only the total ABSENCE of a plan reaches the floor below.
        if (access.Plan is not { MaxSeats: > 0 } plan)
        {
            if (!UnplannedTenantAllowance.AppliesTo(access, DateTime.UtcNow))
                return EntitlementDecision.Unlimited; // no tenant / no plan → no limit

            var unplannedSeats = await CountActiveUsersAsync(businessUnitId, ct);
            return unplannedSeats >= UnplannedTenantAllowance.MaxSeats
                ? EntitlementDecision.Deny(UnplannedTenantAllowance.MaxSeats, unplannedSeats,
                    UnplannedTenantAllowance.Reason(access, "active user(s)",
                        UnplannedTenantAllowance.MaxSeats, unplannedSeats))
                : EntitlementDecision.Permit(UnplannedTenantAllowance.MaxSeats, unplannedSeats);
        }

        var activeUsers = await CountActiveUsersAsync(businessUnitId, ct);

        return activeUsers >= plan.MaxSeats
            ? EntitlementDecision.Deny(plan.MaxSeats, activeUsers,
                $"Seat limit reached: the '{plan.Code}' plan allows at most {plan.MaxSeats} active user(s), and {activeUsers} are already active.")
            : EntitlementDecision.Permit(plan.MaxSeats, activeUsers);
    }

    public async Task<EntitlementDecision> CheckDocumentQuotaAsync(long businessUnitId, CancellationToken ct = default)
    {
        var access = await _tenantAccess.GetAccessAsync(businessUnitId, ct);

        if (access.Plan is not { MaxDocsPerMonth: > 0 } plan)
        {
            if (!UnplannedTenantAllowance.AppliesTo(access, DateTime.UtcNow))
                return EntitlementDecision.Unlimited; // no tenant / no plan → no limit

            var unplannedDocs = await CountBillableDocsThisMonthAsync(businessUnitId, ct);
            return unplannedDocs >= UnplannedTenantAllowance.MaxDocsPerMonth
                ? EntitlementDecision.Deny(UnplannedTenantAllowance.MaxDocsPerMonth, unplannedDocs,
                    UnplannedTenantAllowance.Reason(access, "document(s) per month",
                        UnplannedTenantAllowance.MaxDocsPerMonth, unplannedDocs))
                : EntitlementDecision.Permit(UnplannedTenantAllowance.MaxDocsPerMonth, unplannedDocs);
        }

        var docsThisMonth = await CountBillableDocsThisMonthAsync(businessUnitId, ct);

        return docsThisMonth >= plan.MaxDocsPerMonth
            ? EntitlementDecision.Deny(plan.MaxDocsPerMonth, docsThisMonth,
                $"Monthly document quota reached: the '{plan.Code}' plan allows at most {plan.MaxDocsPerMonth} document(s) per month, and {docsThisMonth} have already been submitted this month.")
            : EntitlementDecision.Permit(plan.MaxDocsPerMonth, docsThisMonth);
    }

    public async Task<EntitlementDecision> CheckFeatureAsync(
        long businessUnitId, string entitlement, CancellationToken ct = default)
    {
        if (!TypedEntitlementCatalog.Keys.Contains(entitlement))
            return EntitlementDecision.Deny(0, 0, $"Unknown entitlement '{entitlement}' is denied by default.");

        if (!TypedEntitlementCatalog.IsRuntimeAvailable(entitlement))
            return EntitlementDecision.Deny(0, 0,
                $"Entitlement '{entitlement}' is not available because its server execution boundary is not implemented.");

        var access = await _tenantAccess.GetAccessAsync(businessUnitId, ct);
        if (!access.HasTenant)
            return EntitlementDecision.Deny(0, 0,
                $"Entitlement '{entitlement}' is unavailable because no governed platform tenant owns this business unit.");

        if (access.Plan is null)
            return EntitlementDecision.Deny(0, 0,
                $"Entitlement '{entitlement}' is unavailable because this tenant has no assigned plan.");

        return TypedEntitlementCatalog.IsEnabled(access.Plan.Features, entitlement)
            ? EntitlementDecision.Permit(1, 1)
            : EntitlementDecision.Deny(0, 0,
                $"The '{access.Plan.Code}' plan does not enable entitlement '{entitlement}'.");
    }

    public async Task<double> GetQueueWeightAsync(long businessUnitId, double fallbackWeight, CancellationToken ct = default)
    {
        var access = await _tenantAccess.GetAccessAsync(businessUnitId, ct);
        return access.Plan is { Weight: > 0 } plan ? plan.Weight : fallbackWeight;
    }

    private Task<int> CountActiveUsersAsync(long businessUnitId, CancellationToken ct)
        => _context.Users
            .IgnoreQueryFilters()
            .AsNoTracking()
            .CountAsync(u => u.Buid == businessUnitId && u.IsActive == true, ct);

    /// <summary>
    /// Documents counted against the monthly quota, in the current UTC month.
    ///
    /// Job-status billing/quota policy (P0-B1): only delivered-or-in-flight work counts.
    /// Duplicate (idempotent re-submission), Failed and DeadLetter jobs are EXCLUDED from
    /// the docs/month quota — the identical status filter is applied by the billing
    /// documents meter (BillingStatementService). The two are kept in lockstep by reading
    /// the SAME <see cref="BillableDocumentPolicy.NonBillableStatuses"/> array rather than
    /// by two hand-written lists that agree today: a status added to one and not the other
    /// is a tenant billed for work its quota said it never used.
    /// </summary>
    private Task<int> CountBillableDocsThisMonthAsync(long businessUnitId, CancellationToken ct)
    {
        var utcNow = DateTime.UtcNow;
        var monthStartUtc = new DateTime(utcNow.Year, utcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var nonBillable = BillableDocumentPolicy.NonBillableStatuses;

        return _context.Set<ExtractionJob>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .CountAsync(j => j.BusinessUnitId == businessUnitId
                && j.CreatedOn >= monthStartUtc
                && !nonBillable.Contains(j.Status), ct);
    }
}
