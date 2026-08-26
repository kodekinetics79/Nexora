using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.Models;

namespace ERP_RFQ_Automation.Authorization;

/// <summary>
/// The ONE definition of "which customers may this caller read" (FR-CST-02).
///
/// <para>It is a single composable <see cref="IQueryable{T}"/> filter rather than a check each
/// caller writes for itself, because the requirement covers three surfaces at once — the customer
/// records, the dashboards and the tasks — and three hand-written variants of an authorization
/// predicate is how one of them ends up one clause short. The customer list, the customer detail
/// read, the global quick search and the release-01 dashboard all call THIS method; delete it and
/// four compilations fail.</para>
///
/// <para><b>What null means.</b> A customer with no <see cref="Customer.AccountTeamId"/> is waiting
/// in the governed routing queue; it is not tenant-readable commercial data. Ordinary account
/// reads therefore exclude it for members and managers unless the caller is a currently named
/// primary/backup owner. The queue exposes only the minimal facts required to claim or assign the
/// account. This prevents every sales representative from seeing the complete legacy/unassigned
/// book while still giving administrators a tenant-wide remediation path.</para>
///
/// <para><b>Named ownership still reads.</b> A rep who is the primary or backup owner of an account
/// (<see cref="CustomerOwnership"/>) keeps access to it even when the account sits in another team's
/// book — that assignment is a deliberate act by a manager, and revoking read access to an account
/// somebody has been made responsible for would break the routing they were assigned by. The
/// ownership row must be active and effective at the stated instant; an expired one grants nothing.</para>
/// </summary>
public static class AccountTeamReadFilter
{
    /// <summary>
    /// Restricts a customer query to <paramref name="scope"/>. Always call this AFTER the tenant
    /// predicate, never instead of it: this is an intra-tenant authorization filter and it says
    /// nothing at all about business units.
    /// </summary>
    public static IQueryable<Customer> InAccountScope(
        this IQueryable<Customer> customers,
        ErpRfqAutomationContext db,
        long businessUnitId,
        AccountTeamScope scope,
        DateTime asOfUtc)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.IsTenantWide) return customers;

        var teamIds = scope.TeamIds;
        var userId = scope.UserId;

        return customers.Where(customer =>
            customer.AccountTeamId != null && teamIds.Contains(customer.AccountTeamId.Value)
            || db.Set<CustomerOwnership>().Any(ownership =>
                ownership.BusinessUnitId == businessUnitId
                && ownership.CustomerId == customer.Id
                && ownership.IsActive
                && (ownership.PrimaryUserId == userId || ownership.BackupUserId == userId)
                && ownership.EffectiveFrom <= asOfUtc
                && (ownership.EffectiveTo == null || ownership.EffectiveTo > asOfUtc)));
    }

    /// <summary>
    /// The ids of the customers in scope, as a subquery other read paths can join against without
    /// duplicating the rule. Returns null when the scope is tenant-wide, so a caller can skip the
    /// join entirely rather than filtering against "every customer".
    /// </summary>
    public static IQueryable<long>? CustomerIdsInScope(
        ErpRfqAutomationContext db,
        long businessUnitId,
        AccountTeamScope scope,
        DateTime asOfUtc)
        => scope.IsTenantWide
            ? null
            : db.Customers
                .Where(c => c.Buid == businessUnitId)
                .InAccountScope(db, businessUnitId, scope, asOfUtc)
                .Select(c => c.Id);
}
