using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.OrderToCash;

/// <summary>
/// The one way to read a tenant's <see cref="CommercialMatchingPolicy"/>.
///
/// <para>Reads only. A tenant that has never had its policy edited has no row, and a read path is
/// the wrong place to start writing one — it would turn every price calculation into a write and
/// race with itself. Absence of a row therefore means <see cref="CommercialMatchingPolicy.DefaultFor"/>,
/// so the defaults are declared once on the entity instead of being re-typed at each call site.</para>
/// </summary>
public static class CommercialMatchingPolicyResolver
{
    public static async Task<CommercialMatchingPolicy> ResolveAsync(this ErpRfqAutomationContext context,
        long businessUnitId, CancellationToken cancellationToken = default)
        => await context.CommercialMatchingPolicies.AsNoTracking()
               .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId, cancellationToken)
           ?? CommercialMatchingPolicy.DefaultFor(businessUnitId);

    /// <summary>
    /// The single question the landed-cost formula asks of policy: does the tax the supplier
    /// charged us belong in the cost of the goods?
    /// </summary>
    public static async Task<bool> ResolveSupplierInputTaxRecoverableAsync(
        this ErpRfqAutomationContext context, long businessUnitId,
        CancellationToken cancellationToken = default)
        => (await context.ResolveAsync(businessUnitId, cancellationToken)).SupplierInputTaxRecoverable;
}
