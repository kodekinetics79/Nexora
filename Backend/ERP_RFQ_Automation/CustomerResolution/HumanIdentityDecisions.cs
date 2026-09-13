using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.CustomerResolution;

/// <summary>
/// OWNER DECISION 2026-09-13, POLICY A ("learn slowly, never guess"): "A buyer's exact email address is learned once reps
/// confirm it for the same customer twice, and never for anyone else", and a company name or a portal account is learned
/// only when the document itself names that customer. This is the one read of what people have decided about one
/// identifying value: an exact address, a printed company name (by its name key) or a buyer portal's pair. The learner counts
/// and vetoes with it; the resolution service and routing refuse, with it, a learned value that a person has decided, on a
/// lead carrying it, for another customer.
///
/// WHY THE READERS NEED IT, NOT ONLY THE LEARNER. The learner takes a contradicted value back only when it next runs, and it
/// runs only on a review's "approve" and on link-client. The review screen's Save records the same human decision without
/// running it. So k.lee@hdec.com, confirmed twice for Hyundai and then saved for Aramco, kept linking the next message to
/// Hyundai at 1.00; an Arabic-only name trusted for SEC kept linking SEC at 0.90 after a print carrying it was saved for
/// Aramco; and a relink saved on that screen left the rejected customer's learned name and portal pair trusted. A decision
/// that stands is now read where the value is used, whichever screen recorded it.
///
/// A decision is the lead's CURRENT customer under a human status (<see cref="LeadCustomerResolutionService.HumanDecidedStatuses"/>).
/// It is read whenever it was taken: a lead carries no record of when a person decided it, and a relink saved today on a lead
/// ingested last year is exactly the decision that must count.
///
/// EXACTLY THIS ADDRESS. A lead carries it when its envelope sender, its sender column or its printed buyer address is the
/// bare address or ends in "&lt;address&gt;", trimmed and compared case-insensitively, in SQL. The learner used to match a
/// substring through one newest-first window of 200 rows: lee@hdec.com matched every k.lee@hdec.com lead, and 200 newer
/// decisions pushed an older decision for another customer out of the window, so the address was learned despite it.
///
/// THIS NAME, BY ITS KEY. A lead carries a name when its printed company name has the name's
/// <see cref="CustomerNameNormalizer.LooseKey"/>, the key the alias is stored and matched under, so a pick written with a double
/// space, a trailing full stop or a tatweel counts. LooseKey cannot run in SQL and no text filter is a safe stand-in (it also
/// folds diacritics and presentation forms), so the distinct (customer, printed name) pairs of the tenant's human decisions are
/// read and keyed here. THIS PAIR, BY ITS KEY: the same for the printed portal name and our vendor code at that portal
/// (<see cref="PortalPairKey"/>). A tenant past <see cref="MaximumDistinctDecisionsRead"/> distinct pairs cannot be checked,
/// and every learned name, or pair, is then treated as decided for another customer: nothing unchecked is trusted.
/// </summary>
internal static class HumanIdentityDecisions
{
    /// <summary>
    /// Distinct (customer, printed name) or (customer, portal, vendor code) pairs read from the tenant's human decisions. Far
    /// above any realistic book of decided documents; past it, every learned name or pair is refused.
    /// </summary>
    public const int MaximumDistinctDecisionsRead = CustomerAliasLearner.MaximumPrintedNameDecisionsRead;

    /// <summary>One customer a person decided a lead carrying a value for, and that value as the lead printed it.</summary>
    public sealed record Carrier(long CustomerId, string Key, string Printed);

    // ── addresses ─────────────────────────────────────────────────────────────

    /// <summary>The leads a person decided that carry exactly <paramref name="address"/> (lower-case, bare).</summary>
    public static IQueryable<Lead> CarryingAddress(IQueryable<Lead> leads, long businessUnitId, string address)
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
    public static Task<bool> AddressDecidedForAnotherCustomerAsync(
        ErpRfqAutomationContext db, long businessUnitId, string address, long customerId, long? exceptLeadId, CancellationToken ct)
    {
        var decisions = CarryingAddress(db.Leads.AsNoTracking().IgnoreQueryFilters(), businessUnitId, address)
            .Where(l => l.CustomerId != customerId);
        if (exceptLeadId is long except)
            decisions = decisions.Where(l => l.Id != except);
        return decisions.AnyAsync(ct);
    }

    // ── printed company names and portal pairs ────────────────────────────────

    /// <summary>
    /// The customers people decided leads printing a company name with <paramref name="nameKey"/> for, one row per distinct
    /// spelling per customer; null when the tenant is past <see cref="MaximumDistinctDecisionsRead"/>.
    /// </summary>
    /// <param name="exceptLeadId">The lead under review, whose stored customer may not yet be the one just picked.</param>
    public static async Task<IReadOnlyList<Carrier>?> NameCarriersAsync(
        ErpRfqAutomationContext db, long businessUnitId, string nameKey, long? exceptLeadId, CancellationToken ct)
    {
        var carriers = await ReadNameCarriersAsync(db, businessUnitId, exceptLeadId, ct);
        return carriers?.Where(carrier => string.Equals(carrier.Key, nameKey, StringComparison.Ordinal)).ToList();
    }

    /// <summary>
    /// The stored "portal|our-vendor-code" key, the one the resolver's S3 and the learner compare; null when either half is
    /// missing or normalises to nothing.
    /// </summary>
    public static string? PortalPairKey(string? portalName, string? supplierAccountRef)
    {
        if (string.IsNullOrWhiteSpace(portalName) || string.IsNullOrWhiteSpace(supplierAccountRef)) return null;
        var portalKey = CustomerNameNormalizer.LooseKey(portalName);
        var accountKey = RoutingValueNormalizer.Normalize(CustomerIdentifierType.ErpAccount, supplierAccountRef);
        return portalKey.Length == 0 || accountKey.Length == 0 ? null : $"{portalKey}|{accountKey}";
    }

    // ── the readers' refusal ──────────────────────────────────────────────────

    /// <summary>
    /// A row the learner wrote from people's confirmations, of a kind this read can overrule: an address, a printed company
    /// name or a portal pair. A row a person entered, or the migration backfilled, is never one: this overrules confirmations,
    /// never a fact somebody set.
    /// </summary>
    public static bool IsTaughtFact(CustomerIdentifierType type, string? source)
        => type is CustomerIdentifierType.Email or CustomerIdentifierType.Alias or CustomerIdentifierType.PortalAccount
           && string.Equals(source, CustomerIdentifierSources.LeadReviewLearned, StringComparison.Ordinal);

    /// <summary>
    /// The ids of the taught facts a person has decided, on a lead carrying the value, for a customer other than the one the
    /// row names. Such a value is nobody's while that decision stands: the learner takes it back when it next runs on such a
    /// decision, and until then the resolver and routing must not match it. An address costs one EXISTS per row (callers pass
    /// only the rows for the addresses on the lead, at most one per address because an Email is exclusive per tenant); names
    /// cost one read of the distinct decided spellings, and pairs one more, however many rows are passed.
    /// </summary>
    public static async Task<HashSet<long>> ContradictedAsync(
        ErpRfqAutomationContext db, long businessUnitId,
        IEnumerable<(long Id, long CustomerId, CustomerIdentifierType Type, string Value)> taughtFacts, CancellationToken ct)
    {
        var facts = taughtFacts.ToList();
        var contradicted = new HashSet<long>();

        foreach (var (id, customerId, _, address) in facts.Where(fact => fact.Type == CustomerIdentifierType.Email))
            if (await AddressDecidedForAnotherCustomerAsync(db, businessUnitId, address, customerId, exceptLeadId: null, ct))
                contradicted.Add(id);

        var names = facts.Where(fact => fact.Type == CustomerIdentifierType.Alias).ToList();
        if (names.Count > 0)
            Refuse(names, await ReadNameCarriersAsync(db, businessUnitId, exceptLeadId: null, ct));

        var pairs = facts.Where(fact => fact.Type == CustomerIdentifierType.PortalAccount).ToList();
        if (pairs.Count > 0)
            Refuse(pairs, await ReadPortalPairCarriersAsync(db, businessUnitId, ct));

        return contradicted;

        void Refuse(List<(long Id, long CustomerId, CustomerIdentifierType Type, string Value)> rows, IReadOnlyList<Carrier>? carriers)
        {
            if (carriers is null)
            {
                foreach (var row in rows) contradicted.Add(row.Id);
                return;
            }
            var decidedFor = carriers
                .GroupBy(carrier => carrier.Key, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Select(carrier => carrier.CustomerId).ToHashSet(), StringComparer.Ordinal);
            foreach (var row in rows)
                if (decidedFor.TryGetValue(row.Value, out var customers) && customers.Any(customer => customer != row.CustomerId))
                    contradicted.Add(row.Id);
        }
    }

    // ── reads ─────────────────────────────────────────────────────────────────

    private static IQueryable<Lead> HumanDecisions(ErpRfqAutomationContext db, long businessUnitId)
    {
        var human = LeadCustomerResolutionService.HumanDecidedStatuses;
        return db.Leads.AsNoTracking().IgnoreQueryFilters()
            .Where(l => l.BusinessUnitId == businessUnitId && l.CustomerId != null && human.Contains(l.CustomerMatchStatus));
    }

    private static async Task<IReadOnlyList<Carrier>?> ReadNameCarriersAsync(
        ErpRfqAutomationContext db, long businessUnitId, long? exceptLeadId, CancellationToken ct)
    {
        var decisions = HumanDecisions(db, businessUnitId).Where(l => l.CustomerCompanyNameExtracted != null);
        if (exceptLeadId is long except)
            decisions = decisions.Where(l => l.Id != except);
        var rows = await decisions
            .Select(l => new { CustomerId = l.CustomerId!.Value, Printed = l.CustomerCompanyNameExtracted! })
            .Distinct()
            .OrderBy(row => row.CustomerId)
            .Take(MaximumDistinctDecisionsRead + 1)
            .ToListAsync(ct);
        if (rows.Count > MaximumDistinctDecisionsRead) return null;
        return rows
            .Select(row => new Carrier(row.CustomerId, CustomerNameNormalizer.LooseKey(row.Printed), row.Printed))
            .Where(carrier => carrier.Key.Length > 0)
            .ToList();
    }

    private static async Task<IReadOnlyList<Carrier>?> ReadPortalPairCarriersAsync(
        ErpRfqAutomationContext db, long businessUnitId, CancellationToken ct)
    {
        var rows = await HumanDecisions(db, businessUnitId)
            .Where(l => l.CustomerPortalNameExtracted != null && l.SupplierAccountRefOnDocument != null)
            .Select(l => new
            {
                CustomerId = l.CustomerId!.Value,
                Portal = l.CustomerPortalNameExtracted!,
                Account = l.SupplierAccountRefOnDocument!
            })
            .Distinct()
            .OrderBy(row => row.CustomerId)
            .Take(MaximumDistinctDecisionsRead + 1)
            .ToListAsync(ct);
        if (rows.Count > MaximumDistinctDecisionsRead) return null;
        return rows
            .Select(row => (row.CustomerId, Key: PortalPairKey(row.Portal, row.Account), Printed: $"{row.Portal} / {row.Account}"))
            .Where(row => row.Key is not null)
            .Select(row => new Carrier(row.CustomerId, row.Key!, row.Printed))
            .ToList();
    }
}
