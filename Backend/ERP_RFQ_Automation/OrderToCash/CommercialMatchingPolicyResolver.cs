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
    /// The single question the landed-cost formula asks of policy: how much of the tax the supplier
    /// charged us belongs in the cost of the goods? 100 means none of it does.
    /// </summary>
    public static async Task<decimal> ResolveSupplierInputTaxRecoverablePercentAsync(
        this ErpRfqAutomationContext context, long businessUnitId,
        CancellationToken cancellationToken = default)
        => (await context.ResolveAsync(businessUnitId, cancellationToken)).SupplierInputTaxRecoverablePercent;

    /// <summary>
    /// The single question the sell side asks of policy: at what rate is output tax derived on a
    /// standard-rated line? Null means the tenant has not stated one, and a null rate derives no
    /// tax at all rather than deriving zero — see <c>CommercialMatchingPolicy.OutputTaxRatePercent</c>.
    /// </summary>
    public static async Task<decimal?> ResolveOutputTaxRatePercentAsync(
        this ErpRfqAutomationContext context, long businessUnitId,
        CancellationToken cancellationToken = default)
        => (await context.ResolveAsync(businessUnitId, cancellationToken)).OutputTaxRatePercent;
}
