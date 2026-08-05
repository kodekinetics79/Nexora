using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Platform.Entitlements;

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
        if (access.Plan is not { MaxSeats: > 0 } plan)
            return EntitlementDecision.Unlimited; // no tenant / no plan → no limit

        var activeUsers = await _context.Users
            .IgnoreQueryFilters()
            .AsNoTracking()
            .CountAsync(u => u.Buid == businessUnitId && u.IsActive == true, ct);

        return activeUsers >= plan.MaxSeats
            ? EntitlementDecision.Deny(plan.MaxSeats, activeUsers,
                $"Seat limit reached: the {plan.Name} plan allows at most {plan.MaxSeats} active user(s), and {activeUsers} are already active.")
            : EntitlementDecision.Permit(plan.MaxSeats, activeUsers);
    }

    public async Task<EntitlementDecision> CheckDocumentQuotaAsync(long businessUnitId, CancellationToken ct = default)
    {
        var access = await _tenantAccess.GetAccessAsync(businessUnitId, ct);
        if (access.Plan is not { MaxDocsPerMonth: > 0 } plan)
            return EntitlementDecision.Unlimited; // no tenant / no plan → no limit

        var utcNow = DateTime.UtcNow;
        var monthStartUtc = new DateTime(utcNow.Year, utcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        // Job-status billing/quota policy (P0-B1): only delivered-or-in-flight work counts.
        // Duplicate (idempotent re-submission), Failed and DeadLetter jobs are EXCLUDED from
        // the docs/month quota — the identical status filter is applied by the billing
        // documents meter (BillingStatementService); keep the two in lockstep.
        var docsThisMonth = await _context.Set<ExtractionJob>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .CountAsync(j => j.BusinessUnitId == businessUnitId
                && j.CreatedOn >= monthStartUtc
                && j.Status != ExtractionStatus.Duplicate
                && j.Status != ExtractionStatus.Failed
                && j.Status != ExtractionStatus.DeadLetter, ct);

        return docsThisMonth >= plan.MaxDocsPerMonth
            ? EntitlementDecision.Deny(plan.MaxDocsPerMonth, docsThisMonth,
                $"Monthly document quota reached: the {plan.Name} plan allows at most {plan.MaxDocsPerMonth} document(s) per month, and {docsThisMonth} have already been submitted this month.")
            : EntitlementDecision.Permit(plan.MaxDocsPerMonth, docsThisMonth);
    }

    public async Task<double> GetQueueWeightAsync(long businessUnitId, double fallbackWeight, CancellationToken ct = default)
    {
        var access = await _tenantAccess.GetAccessAsync(businessUnitId, ct);
        return access.Plan is { Weight: > 0 } plan ? plan.Weight : fallbackWeight;
    }
}
