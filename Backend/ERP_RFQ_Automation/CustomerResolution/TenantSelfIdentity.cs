using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.CustomerResolution;

/// <summary>
/// The tenant's own mail domains: what "us" means for an e-mail address. One loader, so the
/// learner, the resolver and routing can never hold different ideas of it.
///
/// It was read from EmailConfigurations alone, which lists the INGESTION mailboxes and nothing
/// else. The people who forward bids are the tenant's staff, and they write from their own
/// addresses, often on a different domain from the mailbox: the mailbox is rfq@alquraishi.com
/// and the salesman is ahmed@alquraishi.com.sa. That domain was then not "us", so one reviewer
/// confirming a forwarded SEC bid taught ahmed@alquraishi.com.sa as SEC's Email at 1.00 and
/// alquraishi.com.sa as SEC's Domain at 0.95, and every later forward from any colleague
/// linked to SEC whatever the attachment said. The tenant's own active users now count too.
/// </summary>
public static class TenantSelfIdentity
{
    /// <summary>The cap the mailbox read has always had; a tenant does not run fifty intake mailboxes.</summary>
    public const int MaximumMailboxesRead = 50;

    /// <summary>
    /// A bound on the user read, well above any plan's seat limit. Staff share a handful of
    /// domains, so a domain carried only by users past this row is the only thing it can miss.
    /// </summary>
    public const int MaximumUsersRead = 1_000;

    /// <summary>
    /// The tenant's own mail domains, bare and lower-case, compared case-insensitively.
    ///
    /// IgnoreQueryFilters with an explicit tenant predicate on every read, exactly as the
    /// learner and the resolution service read the mailboxes. The mailbox poller and the
    /// extraction worker run with no tenant under the BYPASSRLS pipeline role, where the EF
    /// filter is a no-op; the predicate here is the only scope there is. On Users it also matters
    /// inside a request: that filter lets rows with no business unit through (platform
    /// operators), and a platform operator's domain is not this tenant's.
    /// </summary>
    public static async Task<IReadOnlySet<string>> LoadSelfDomainsAsync(
        ErpRfqAutomationContext db, long businessUnitId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var mailboxes = await db.EmailConfigurations.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.BusinessUnitId == businessUnitId && c.EmailAddress != null)
            .Select(c => c.EmailAddress!)
            .Distinct()
            .Take(MaximumMailboxesRead)
            .ToListAsync(ct);

        var userEmails = await db.Users.AsNoTracking().IgnoreQueryFilters()
            .Where(u => u.Buid == businessUnitId && u.IsActive == true && u.Email != null)
            .OrderBy(u => u.Id)
            .Select(u => u.Email)
            .Take(MaximumUsersRead)
            .ToListAsync(ct);

        return SelfDomainsFrom(mailboxes, userEmails);
    }

    /// <summary>
    /// The pure half of <see cref="LoadSelfDomainsAsync"/>, so the rule can be stated without a
    /// database.
    ///
    /// A mailbox domain counts as it always has. A user's domain counts only when it could
    /// belong to one organisation (<see cref="IdentityDomainGuard.IsOrganisationDomain"/> with
    /// no self list): a colleague registered with a gmail address does not make gmail.com "us",
    /// and if it did, every gmail buyer's exact address would be thrown away as our own.
    /// Nexora's placeholder addresses and portal relays are refused the same way.
    /// </summary>
    public static IReadOnlySet<string> SelfDomainsFrom(
        IEnumerable<string?> mailboxAddresses, IEnumerable<string?> userEmails)
    {
        ArgumentNullException.ThrowIfNull(mailboxAddresses);
        ArgumentNullException.ThrowIfNull(userEmails);

        var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mailbox in mailboxAddresses)
        {
            var domain = RoutingValueNormalizer.DomainFromEmail(mailbox);
            if (!string.IsNullOrWhiteSpace(domain)) domains.Add(domain);
        }
        foreach (var email in userEmails)
        {
            var domain = RoutingValueNormalizer.DomainFromEmail(email);
            if (domain is not null && IdentityDomainGuard.IsOrganisationDomain(domain)) domains.Add(domain);
        }
        return domains;
    }

    /// <summary>
    /// Whether an address or domain is OURS, by both tests: it is one of the tenant's own domains
    /// or a host under one (<see cref="IdentityDomainGuard.IsSelfDomain"/>), or the domain's own
    /// name spells one of the tenant's names (<see cref="CustomerAliasLearner.DomainLabelSpellsName"/>),
    /// which is the only test that knows a colleague who has no Nexora login.
    ///
    /// The second test lived in the learner alone. A salesman with no login forwarding from
    /// ahmed@alquraishi.com.sa was "us" to the learner, which refused to learn from him, and a
    /// stranger to the resolver and to routing: an alquraishi.com.sa → Saudi Electricity Domain row
    /// taught before the learner refused it still linked every forward from any colleague to SEC at
    /// 0.95 and handed it to SEC's owner. Three readers asked one question and got two answers.
    ///
    /// A rejection rule, so it errs towards "ours": a customer whose domain happens to spell one of
    /// our distinctive name words ("quraishi.com") loses its domain and exact-address links and is
    /// resolved from its document instead. That costs a suggestion. The other error costs a
    /// colleague's forward linked to whichever customer was taught first.
    /// </summary>
    /// <param name="selfNames">
    /// The tenant's own names, raw or as LooseKeys: the business-unit name and the vendor block the
    /// document prints. Null or blank entries are ignored.
    /// </param>
    public static bool IsOurs(
        string? domainOrAddress, IEnumerable<string>? selfDomains, IEnumerable<string?>? selfNames)
    {
        var domain = IdentityDomainGuard.DomainOf(domainOrAddress);
        if (domain is null) return false;
        if (IdentityDomainGuard.IsSelfDomain(domain, selfDomains)) return true;
        return selfNames is not null
               && selfNames.Any(name => !string.IsNullOrWhiteSpace(name)
                                        && CustomerAliasLearner.DomainLabelSpellsName(domain, name));
    }

    /// <summary>
    /// <see cref="IsOurs(string?, IEnumerable{string}?, IEnumerable{string?}?)"/> with the domains and
    /// names the evidence carries (the tenant's names and the document's vendor block), so the
    /// resolver's guard and the corpus loader cannot ask it two different ways.
    /// </summary>
    public static bool IsOurs(string? domainOrAddress, LeadClientEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return IsOurs(domainOrAddress, evidence.TenantSelfDomains,
            new List<string?>(evidence.TenantSelfNameKeys) { evidence.SupplierNameOnDocument });
    }
}
