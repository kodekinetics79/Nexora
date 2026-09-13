using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.CustomerResolution;

/// <summary>
/// OWNER DECISION 2026-09-13, POLICY A ("learn slowly, never guess"): "A buyer's exact email address is learned once reps
/// confirm it for the same customer twice, and never for anyone else." This is the one read of what people have decided
/// about one exact address. The learner counts and vetoes with it; the resolution service and routing refuse, with it, a
/// learned address that a person has since decided for another customer.
///
/// WHY THE READERS NEED IT, NOT ONLY THE LEARNER. The learner takes a contradicted address back only when it next runs, and it
/// runs only on a review's "approve" and on link-client. The review screen's Save records the same human decision without
/// running it. So k.lee@hdec.com, confirmed twice for Hyundai and then saved for Aramco, kept linking the next message to
/// Hyundai at 1.00, and routing assigned Hyundai's owner and wrote Hyundai onto the lead. A decision that stands is now read
/// where the address is used, whichever screen recorded it.
///
/// EXACTLY THIS ADDRESS. A lead carries it when its envelope sender, its sender column or its printed buyer address is the
/// bare address or ends in "&lt;address&gt;", trimmed and compared case-insensitively, in SQL. The learner used to match a
/// substring through one newest-first window of 200 rows: lee@hdec.com matched every k.lee@hdec.com lead, and 200 newer
/// decisions pushed an older decision for another customer out of the window, so the address was learned despite it.
/// A decision is the lead's CURRENT customer under a human status (<see cref="LeadCustomerResolutionService.HumanDecidedStatuses"/>).
/// </summary>
internal static class HumanAddressDecisions
{
    /// <summary>The leads a person decided that carry exactly <paramref name="address"/> (lower-case, bare).</summary>
    public static IQueryable<Lead> Carrying(IQueryable<Lead> leads, long businessUnitId, string address)
    {
        var bracketed = "<" + address + ">";
        var human = LeadCustomerResolutionService.HumanDecidedStatuses;
        return leads.Where(l => l.BusinessUnitId == businessUnitId
                                && l.CustomerId != null
                                && human.Contains(l.CustomerMatchStatus)
                                && ((l.Clientemail != null
                                     && (l.Clientemail.Trim().ToLower() == address
                                         || l.Clientemail.Trim().ToLower().EndsWith(bracketed)))
                                    || (l.CustomerBuyerEmailExtracted != null
                                        && (l.CustomerBuyerEmailExtracted.Trim().ToLower() == address
                                            || l.CustomerBuyerEmailExtracted.Trim().ToLower().EndsWith(bracketed)))
                                    || (l.EmailIngests != null
                                        && (l.EmailIngests.FromEmail.Trim().ToLower() == address
                                            || l.EmailIngests.FromEmail.Trim().ToLower().EndsWith(bracketed)))));
    }

    /// <summary>
    /// Whether a person has decided a lead carrying exactly <paramref name="address"/> for a customer other than
    /// <paramref name="customerId"/>. An EXISTS, filtered on the customer before anything could cap it.
    /// </summary>
    /// <param name="exceptLeadId">The lead under review, whose stored customer may not yet be the one just picked.</param>
    public static Task<bool> DecidedForAnotherCustomerAsync(
        ErpRfqAutomationContext db, long businessUnitId, string address, long customerId, long? exceptLeadId, CancellationToken ct)
    {
        var decisions = Carrying(db.Leads.AsNoTracking().IgnoreQueryFilters(), businessUnitId, address)
            .Where(l => l.CustomerId != customerId);
        if (exceptLeadId is long except)
            decisions = decisions.Where(l => l.Id != except);
        return decisions.AnyAsync(ct);
    }

    /// <summary>Whether a row is an address the learner wrote from people's confirmations, the only kind this read may overrule.</summary>
    public static bool IsTaughtAddress(CustomerIdentifierType type, string? source)
        => type == CustomerIdentifierType.Email
           && string.Equals(source, CustomerIdentifierSources.LeadReviewLearned, StringComparison.Ordinal);

    /// <summary>
    /// The ids of the taught addresses a person has since decided for a customer other than the one the row names. Such an
    /// address is nobody's: the learner expires it at its next run, and until then the resolver and routing must not match it.
    /// One EXISTS per row; callers pass only the rows for the addresses on the lead, at most one per address because an Email
    /// is exclusive per tenant. A row a person entered, or the migration backfilled, is never passed: this overrules
    /// confirmations, never a fact somebody set.
    /// </summary>
    public static async Task<HashSet<long>> ContradictedAsync(
        ErpRfqAutomationContext db, long businessUnitId, IEnumerable<(long Id, long CustomerId, string Address)> taughtAddresses,
        CancellationToken ct)
    {
        var contradicted = new HashSet<long>();
        foreach (var (id, customerId, address) in taughtAddresses)
            if (await DecidedForAnotherCustomerAsync(db, businessUnitId, address, customerId, exceptLeadId: null, ct))
                contradicted.Add(id);
        return contradicted;
    }
}
