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
///
/// A user's domain is ours only where no customer of this tenant is on record with it. A login is
/// not always staff: a demo, consultant or implementer account can sit on a customer's own domain
/// (tenant 7 has users and customer identities on the same domains), and counting that domain as
/// ours threw away every Email and Domain row a person had registered for that customer, in the
/// resolver, the corpus loader, the learner and routing at once.
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
    /// A bound on how many distinct user domains are checked against the tenant's customer records.
    /// Staff share a handful of domains. A domain past the bound stays ours, which is how every user
    /// domain was read before the check existed, so the bound can cost a link but never make one.
    /// </summary>
    public const int MaximumUserDomainsChecked = 25;

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

        var customerHeld = await LoadCustomerHeldUserDomainsAsync(db, businessUnitId, mailboxes, userEmails, ct);
        return SelfDomainsFrom(mailboxes, userEmails, customerHeld);
    }

    /// <summary>
    /// The user domains (never a mailbox domain) that an active customer of this tenant is on record
    /// with: an active, verified Email or Domain identifier a person entered
    /// (<see cref="CustomerIdentifierSources.EnteredByAPerson"/>) on that domain or a host under it,
    /// or an active contact writing from it.
    ///
    /// What the platform inferred does not count. A reviewer's confirmation of one forwarded bid used
    /// to mint the colleague's own address and domain on SEC as LeadReviewLearned rows; if those rows
    /// could take a staff domain out of "ours", the defect this loader was written for would return
    /// through the very rows it left behind.
    /// </summary>
    private static async Task<IReadOnlyList<string>> LoadCustomerHeldUserDomainsAsync(
        ErpRfqAutomationContext db, long businessUnitId,
        IReadOnlyCollection<string> mailboxes, IReadOnlyCollection<string?> userEmails, CancellationToken ct)
    {
        var mailboxDomains = mailboxes.Select(IdentityDomainGuard.DomainOf)
            .Where(domain => domain is not null).Select(domain => domain!).ToList();
        var candidates = userEmails
            .Select(RoutingValueNormalizer.DomainFromEmail)
            .Where(domain => domain is not null
                             && IdentityDomainGuard.IsOrganisationDomain(domain)
                             && !IdentityDomainGuard.IsSelfDomain(domain, mailboxDomains))
            .Select(domain => domain!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(domain => domain, StringComparer.Ordinal)
            .Take(MaximumUserDomainsChecked)
            .ToList();
        if (candidates.Count == 0) return [];

        var enteredByAPerson = CustomerIdentifierSources.EnteredByAPerson;
        var held = new List<string>();
        foreach (var domain in candidates)
        {
            var atDomain = "@" + domain;
            var underDomain = "." + domain;
            var holds = await db.Set<CustomerIdentifier>().AsNoTracking().IgnoreQueryFilters()
                .Where(i => i.BusinessUnitId == businessUnitId
                            && i.EffectiveTo == null
                            && i.IsVerified
                            && enteredByAPerson.Contains(i.Source)
                            && ((i.IdentifierType == CustomerIdentifierType.Email
                                 && (i.NormalizedValue.EndsWith(atDomain) || i.NormalizedValue.EndsWith(underDomain)))
                                || (i.IdentifierType == CustomerIdentifierType.Domain
                                    && (i.NormalizedValue == domain || i.NormalizedValue.EndsWith(underDomain))))
                            && db.Customers.Any(customer => customer.Buid == businessUnitId
                                                            && customer.Id == i.CustomerId
                                                            && customer.IsActive != false))
                .AnyAsync(ct);
            if (!holds)
            {
                holds = await db.Contacts.AsNoTracking().IgnoreQueryFilters()
                    .Where(contact => contact.BusinessUnitId == businessUnitId
                                      && contact.IsActive != false
                                      && contact.Email != null
                                      && (contact.Email.Trim().ToLower().EndsWith(atDomain)
                                          || contact.Email.Trim().ToLower().EndsWith(underDomain))
                                      && db.Customers.Any(customer => customer.Buid == businessUnitId
                                                                      && customer.Id == contact.CustomerId
                                                                      && customer.IsActive != false))
                    .AnyAsync(ct);
            }
            if (holds) held.Add(domain);
        }
        return held;
    }

    /// <summary>
    /// The pure half of <see cref="LoadSelfDomainsAsync"/>, so the rule can be stated without a
    /// database.
    ///
    /// A mailbox domain counts as it always has, whoever else is on record with it: the tenant
    /// configured that mailbox, so its domain is ours by definition. A user's domain counts only
    /// when it could belong to one organisation (<see cref="IdentityDomainGuard.IsOrganisationDomain"/>
    /// with no self list): a colleague registered with a gmail address does not make gmail.com
    /// "us", and if it did, every gmail buyer's exact address would be thrown away as our own.
    /// Nexora's placeholder addresses and portal relays are refused the same way. And it counts only
    /// when no customer holds it (<paramref name="customerHeldDomains"/>: domains or addresses, a
    /// held host under the user's domain counts as holding it): a login on a customer's own domain is
    /// a demo or a consultant, not a reason to stop recognising that customer.
    /// </summary>
    public static IReadOnlySet<string> SelfDomainsFrom(
        IEnumerable<string?> mailboxAddresses, IEnumerable<string?> userEmails,
        IEnumerable<string?>? customerHeldDomains = null)
    {
        ArgumentNullException.ThrowIfNull(mailboxAddresses);
        ArgumentNullException.ThrowIfNull(userEmails);

        var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mailboxDomains = new List<string>();
        foreach (var mailbox in mailboxAddresses)
        {
            var domain = RoutingValueNormalizer.DomainFromEmail(mailbox);
            if (string.IsNullOrWhiteSpace(domain)) continue;
            domains.Add(domain);
            mailboxDomains.Add(domain);
        }

        var held = (customerHeldDomains ?? [])
            .Select(IdentityDomainGuard.DomainOf)
            .Where(domain => domain is not null)
            .Select(domain => domain!)
            .ToList();
        foreach (var email in userEmails)
        {
            var domain = RoutingValueNormalizer.DomainFromEmail(email);
            if (domain is null || !IdentityDomainGuard.IsOrganisationDomain(domain)) continue;
            var underAMailbox = IdentityDomainGuard.IsSelfDomain(domain, mailboxDomains);
            if (!underAMailbox && held.Any(heldDomain => IdentityDomainGuard.IsSelfDomain(heldDomain, [domain])))
                continue;
            domains.Add(domain);
        }
        return domains;
    }

    /// <summary>
    /// The names whose spelling makes a domain ours: the tenant's own configured names, and the
    /// document's vendor block only when it is a spelling of one of them
    /// (<see cref="SelfIdentityGuard.IsSelfName"/>).
    ///
    /// The vendor block is an extracted value, and the extractor is told to put any "To:",
    /// "Supplier" or "Quote To" block there, so it can hold the BUYER's name. It was added to these
    /// names as it stood, and a Marafiq print whose vendor block was read as "MARAFIQ" made
    /// marafiq.com.sa ours: Marafiq's registered address (1.00) and domain (0.95) were discarded in
    /// the resolver, the corpus loader and routing, and the lead came back with no customer. The
    /// vendor block still suppresses a NAME wherever names are compared; it never makes an address
    /// ours on its own.
    /// </summary>
    public static IReadOnlyList<string> DomainSelfNames(IEnumerable<string?> tenantNames, string? documentVendorBlock)
    {
        ArgumentNullException.ThrowIfNull(tenantNames);
        var names = new List<string>();
        foreach (var name in tenantNames)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            var trimmed = name.Trim();
            if (!names.Contains(trimmed, StringComparer.Ordinal)) names.Add(trimmed);
        }
        if (names.Count == 0 || string.IsNullOrWhiteSpace(documentVendorBlock)) return names;

        var vendorKey = CustomerNameNormalizer.LooseKey(documentVendorBlock);
        var tenantKeys = names.Select(CustomerNameNormalizer.LooseKey).Where(key => key.Length > 0).ToList();
        var vendor = documentVendorBlock.Trim();
        if (vendorKey.Length > 0
            && SelfIdentityGuard.IsSelfName(vendorKey, tenantKeys)
            && !names.Contains(vendor, StringComparer.Ordinal))
            names.Add(vendor);
        return names;
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
    /// The tenant's own names, raw or as LooseKeys (<see cref="DomainSelfNames"/>): never the
    /// document's vendor block as it was extracted. Null or blank entries are ignored.
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
    /// names the evidence carries, so the resolver's guard and the corpus loader cannot ask it two
    /// different ways. The names are the tenant's (<see cref="LeadClientEvidence.TenantSelfNameKeys"/>)
    /// and the document's vendor block only where it spells one of them (<see cref="DomainSelfNames"/>).
    /// </summary>
    public static bool IsOurs(string? domainOrAddress, LeadClientEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return IsOurs(domainOrAddress, evidence.TenantSelfDomains,
            DomainSelfNames(evidence.TenantSelfNameKeys, evidence.SupplierNameOnDocument));
    }
}
