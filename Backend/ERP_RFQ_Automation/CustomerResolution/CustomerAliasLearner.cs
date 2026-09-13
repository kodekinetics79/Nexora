using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ERP_RFQ_Automation.CustomerResolution;

public interface ICustomerAliasLearner
{
    /// <summary>
    /// Turns ONE human correction into durable identity knowledge. Stages writes on the
    /// caller's DbContext inside the caller's transaction — the caller owns SaveChanges, so
    /// the learned edges commit with the review that taught them or not at all.
    ///
    /// One write does not wait for the caller. When the reviewer moved the lead to a different
    /// client, the rows that decision contradicts are expired in the database straight away,
    /// still inside the caller's transaction. The exclusivity check reads the database, not the
    /// change tracker, so an expiry that is only staged is invisible to it: the correction
    /// itself was refused as ALIAS_CONFLICT, and the wrong customer kept the address. See P5.
    /// </summary>
    Task<CustomerAliasLearningResult> LearnFromReviewAsync(
        long businessUnitId,
        Lead lead,
        long customerId,
        long? previousCustomerId,
        long? reviewAuditId,
        CancellationToken ct = default);
}

public sealed record CustomerAliasLearningResult(
    int Learned, int Reinforced, int Expired, IReadOnlyList<string> SkipReasons)
{
    public static readonly CustomerAliasLearningResult None = new(0, 0, 0, []);
    public string? SkippedReason => SkipReasons.Count == 0 ? null : string.Join(",", SkipReasons.Distinct());
}

/// <summary>
/// The learning loop: a reviewer picking the right client teaches Nexora to get the NEXT
/// document from that client right by itself.
///
/// It writes into the EXISTING <c>customer_identifiers</c> store rather than a parallel
/// alias table, because two tables describing one fact become two competing truths the
/// moment they disagree. Learned rows carry Source = "LeadReviewLearned", which is
/// deliberately absent from <c>CustomerIdentityMaintenance.ManagedSources</c>, so a later
/// customer-profile synchronisation cannot expire what a person taught the platform.
///
/// Poisoning safeguards, all enforced here:
///   P1 self-identity  — never learn the tenant's own name / domains / the document's vendor block.
///                       "Our domains" are the intake mailboxes AND the tenant's active users
///                       (<see cref="TenantSelfIdentity"/>), any host under them, and any domain
///                       whose own name spells ours
///   P2 synthetic      — never learn Nexora's ingestion placeholders; never a free-mail or
///                       portal-relay address AT ALL (neither Email nor Domain)
///   P3 exclusivity    — Email/ErpAccount/TaxRegistration are exclusive; on conflict SKIP, never steal
///   P4 multi-owner    — Alias/Domain/PortalAccount may point at several customers; the
///                       resolver then returns AMBIGUOUS, which is an outcome, not an error
///   P5 reversal       — a later review that CHANGES the customer expires every learned row for
///                       the rejected customer that this document's own evidence carries, whichever
///                       lead first taught it, and writes that expiry through before anything is
///                       checked against it
///   P6 approval gate  — only "approve" + an explicitly supplied customer; NEVER a machine
///                       AUTO_MATCHED result (that is the path by which one machine mistake
///                       would bootstrap itself into an authoritative alias)
///   P7 single level   — identifiers point DIRECTLY at a CustomerId; no alias -> alias
///                       indirection, no supersession chain, so cycles are structurally
///                       impossible. If customer merge/supersession is ever added, port the
///                       `visited` HashSet cycle detector from ProductIdentityResolver
///                       (Inventory/Commercial/CommercialInventoryServices.cs:36-56) FIRST.
///   P8 resemblance    — a printed name is the customer's alias only when it carries a word that
///                       names ONE company, that word (or the customer's own initials) is the
///                       customer's, and no other active customer in the tenant owns the name at
///                       least as closely. Anything else is recorded for review, never trusted
///   P9 shared network — never learn a portal pair whose supplier number was issued by the
///                       NETWORK rather than by the buyer (SharedSupplierNetworks)
///   P10 organisation  — a mailbox's Email and Domain are trusted only when something a person
///                       did ties that domain to the chosen customer: a contact on the customer
///                       at that domain, the buyer address printed on an earlier document a person
///                       linked to that customer (never the envelope sender), or the domain
///                       spelling the customer's own name. An intermediary's domain is filed
///                       unverified; a domain another customer already writes from is not filed
///   P11 portal pair   — a buyer portal's "portal|our-vendor-code" pair is trusted only when the
///                       document names the chosen customer or an earlier human decision on the same
///                       pair ties it; otherwise it is filed unverified, and a pair another customer
///                       already holds is not filed at all
/// </summary>
/// <remarks>
/// WHAT THIS CLASS STILL DOES NOT DO: corroboration.
///
/// Every row written here carries <c>ObservationCount</c>, incremented each time a human
/// confirms the same evidence again — and NOTHING reads it. CustomerIdentityResolver's
/// learned-alias tier (S3) asks only IsVerified, Source and Confidence, so ONE sighting is
/// indistinguishable from fifty, and one mis-click on one document auto-links every later
/// document that carries the same evidence. That is precisely how the live tenant's "Saudi
/// Aramco" came to own Saudi Electricity's identifiers.
///
/// Requiring a SECOND independent sighting before a learned row may AUTO-LINK is a product
/// decision the owner has not taken — it deliberately makes the platform slower to learn, and
/// that trade is theirs to make — so this class does not pretend to it. When it is taken, the
/// change is three edits and none of them are in this file:
///   1. carry the count into the read model: add ObservationCount to
///      <c>CustomerIdentifierSnapshot</c> (CustomerResolutionContracts.cs) and to the
///      projection in <c>LeadCustomerResolutionService.LoadCorpusAsync</c>, which today
///      selects Id, CustomerId, IdentifierType, NormalizedValue, IsVerified, Confidence,
///      Source and stops.
///   2. add <c>MinimumCorroboratingObservations</c> to <c>CustomerResolutionPolicy</c>, beside
///      MinimumAutoLinkConfidence, so the rule is one number a person can read.
///   3. in <c>CustomerIdentityResolver</c>'s S3 loop, beside the existing
///      <c>if (!identifier.IsVerified) continue;</c>, DEMOTE a row below the threshold to a
///      suggestion rather than dropping it — a single confirmation is still the best evidence
///      on the page, it just should not decide by itself.
/// Until then <c>CustomerAliasLearnerTests.Re_confirmation_increments_the_observation_count</c>
/// keeps the counter honest, so the day the rule is switched on, the data it needs is real.
///
/// P10 is narrower than that rule and is not a substitute for it: it asks for a second sighting
/// only of a MAILBOX DOMAIN that nothing ties to the customer, which is exactly the shape an
/// intermediary's mailbox has.
/// </remarks>
public sealed class CustomerAliasLearner : ICustomerAliasLearner
{
    public const string SkipSelfIdentity = "selfIdentity";
    public const string SkipSynthetic = "syntheticIdentity";
    public const string SkipAliasConflict = "ALIAS_CONFLICT";
    public const string SkipNoEvidence = "noEvidence";

    /// <summary>
    /// The printed company name was recorded for review instead of being trusted, because it
    /// does not resemble the customer the reviewer picked. See <see cref="ResemblesCustomerName"/>.
    /// </summary>
    public const string SkipAliasUnlikeCustomer = "aliasUnlikeCustomer";

    /// <summary>
    /// The printed company name holds nothing but country, geography and legal-form words
    /// ("SAUDI ARABIA", "SAUDI COMPANY"), so nothing was written at all. Such a name is not
    /// even worth a suggestion: it names half the country, and one click on the recognition
    /// table would promote it to an alias that links every document mentioning Saudi Arabia.
    /// </summary>
    public const string SkipAliasNotDistinctive = "aliasNotDistinctive";

    /// <summary>
    /// The printed name resembles the chosen customer, but another active customer in this
    /// tenant owns it at least as closely (the same initials, the other customer's exact name, a
    /// larger share of its words), so it was recorded for review instead of being trusted. See
    /// <see cref="NamesAnotherCustomerAtLeastAsClosely"/>.
    /// </summary>
    public const string SkipAliasNamesAnotherCustomer = "aliasNamesAnotherCustomer";

    /// <summary>The portal issues OUR supplier number itself, so the pair names nobody.</summary>
    public const string SkipSharedSupplierNetwork = "sharedSupplierNetwork";

    /// <summary>
    /// Nothing on the document, and no earlier human decision, ties the portal + vendor-code pair
    /// to the customer the reviewer picked, so it was filed unverified. See P11.
    /// </summary>
    public const string SkipPortalAccountNotTiedToCustomer = "portalAccountNotTiedToCustomer";

    /// <summary>
    /// Another active customer already holds the same portal + vendor-code pair as a fact, so it
    /// was not filed against this one. See P11.
    /// </summary>
    public const string SkipPortalAccountClaimedByAnotherCustomer = "portalAccountClaimedByAnotherCustomer";

    /// <summary>A consumer mailbox or a procurement portal's relay: evidence about a PERSON or a POSTMAN.</summary>
    public const string SkipPersonalOrRelayAddress = "personalOrRelayAddress";

    /// <summary>
    /// The mailbox belongs to an organisation nothing ties to the chosen customer, such as an EPC
    /// contractor writing about the site owner's job. Its Email was not minted and its Domain was
    /// filed unverified. See P10.
    /// </summary>
    public const string SkipDomainNotTiedToCustomer = "domainNotTiedToCustomer";

    /// <summary>
    /// Another active customer already writes from this domain (a contact, or a verified Email or
    /// Domain identifier), so the domain names neither customer on its own and was not filed.
    /// </summary>
    public const string SkipDomainClaimedByAnotherCustomer = "domainClaimedByAnotherCustomer";

    /// <summary>
    /// Where a demoted alias is filed. Deliberately NOT in
    /// <see cref="CustomerIdentifierSources.TrustedForAutoLink"/>, so the resolver's S3 tier
    /// will not link on it however many times it is seen — the row exists so a person can look
    /// at it and say "yes, that really is them", not so the machine can act on it. It is also
    /// absent from <c>CustomerIdentityMaintenance.ManagedSources</c>, exactly like
    /// <see cref="CustomerIdentifierSources.LeadReviewLearned"/>, so a profile sync cannot
    /// silently delete the evidence of what a reviewer once confirmed.
    /// </summary>
    public const string UnverifiedAliasSource = CustomerIdentifierSources.LeadReviewUnverified;

    /// <summary>
    /// How many of the tenant's other customers the alias gate reads to find one that owns the
    /// printed name more closely. A review is human-paced and the read is names only, so it is
    /// set well above any realistic customer book. A tenant past it cannot be checked, and an
    /// alias that cannot be checked is recorded for review rather than trusted.
    /// </summary>
    public const int MaximumCustomersCompared = 20_000;

    /// <summary>Contacts, identifiers and earlier leads read per mailbox domain when deciding whose domain it is.</summary>
    public const int MaximumDomainEvidenceRead = 200;

    /// <summary>A word must be at least this long before one wrong letter is read as a typing slip rather than another word.</summary>
    private const int MinimumSlipWordLength = 6;

    /// <summary>The rows this class writes. Only these may be expired by a reviewer's correction (P5).</summary>
    private static readonly string[] LearnedSources =
        [CustomerIdentifierSources.LeadReviewLearned, CustomerIdentifierSources.LeadReviewUnverified];

    private readonly ErpRfqAutomationContext _db;
    private readonly ILogger<CustomerAliasLearner>? _log;

    public CustomerAliasLearner(ErpRfqAutomationContext db, ILogger<CustomerAliasLearner>? log = null)
    {
        _db = db;
        _log = log;
    }

    public async Task<CustomerAliasLearningResult> LearnFromReviewAsync(
        long businessUnitId,
        Lead lead,
        long customerId,
        long? previousCustomerId,
        long? reviewAuditId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lead);
        if (businessUnitId <= 0) throw new ArgumentOutOfRangeException(nameof(businessUnitId));
        if (customerId <= 0) throw new ArgumentOutOfRangeException(nameof(customerId));

        // P6: only a human decision teaches. A machine AUTO_MATCH must never become an alias.
        if (!LeadCustomerMatchStatuses.IsHumanDecided(lead.CustomerMatchStatus))
            return CustomerAliasLearningResult.None;

        var now = DateTime.UtcNow;
        var skips = new List<string>();

        // What the document itself says, before any gate decides what may be learned from it.
        // P5 needs the raw evidence, not the filtered proposals: a legacy row pinning a gmail
        // sender to the wrong client is exactly what a correction must expire, even though
        // nothing about a gmail sender would be learned today.
        var addresses = new List<string>();
        foreach (var raw in new[] { ResolveSender(lead), lead.CustomerBuyerEmailExtracted })
        {
            var address = LeadCustomerResolutionService.ParseAddress(raw);
            // One address, once. An SEC print carries 57322@se.com.sa as both the sender and the
            // buyer address on the page; proposing it twice staged two identical rows, the unique
            // index refused the flush, and the whole review's learning was rolled back.
            if (!string.IsNullOrEmpty(address) && !addresses.Contains(address, StringComparer.Ordinal))
                addresses.Add(address);
        }
        var companyDisplay = string.IsNullOrWhiteSpace(lead.CustomerCompanyNameExtracted)
            ? null
            : lead.CustomerCompanyNameExtracted.Trim();
        var aliasKey = CustomerNameNormalizer.LooseKey(companyDisplay);
        var portalPair = PortalAccountPair(lead);

        // P5: the reviewer moved this lead to a different client.
        var expiredIds = new HashSet<long>();
        if (previousCustomerId.HasValue && previousCustomerId.Value != customerId)
            await ExpireContradictedAsync(
                businessUnitId, lead.Id, previousCustomerId.Value, addresses, aliasKey, portalPair?.Key,
                expiredIds, now, ct);

        var selfNameKeys = await LoadTenantSelfNameKeysAsync(businessUnitId, lead, ct);
        var selfDomains = await TenantSelfIdentity.LoadSelfDomainsAsync(_db, businessUnitId, ct);
        // The name of the customer the reviewer actually picked. Without it there is nothing to
        // compare the printed name against, and "learn whatever is printed" is exactly the bug
        // (P8). Tenant-scoped on purpose: a customer id from another tenant reads back as no
        // name at all, and a nameless customer demotes rather than trusts.
        var customerName = await _db.Customers.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.Id == customerId && c.Buid == businessUnitId)
            .Select(c => c.Name)
            .FirstOrDefaultAsync(ct);

        // Whether this document's own company-name field names an organisation other than the one the
        // reviewer picked: an EPC contractor's letterhead, a consignee, a sister company. Such a
        // document is still the customer's, but it cannot vouch for a mailbox domain (P10).
        var documentNamesSomebodyElse = NamesAnOrganisationOtherThan(companyDisplay, customerName, selfNameKeys);

        var proposals = new List<Proposal>();

        // 1. Real sender + the buyer address printed on the document. For a folder-ingested
        //    or scanned bid the printed buyer address is the ONLY place the buying
        //    organisation's real domain appears, so both are weighed identically.
        var ties = new Dictionary<string, DomainTie>(StringComparer.Ordinal);
        foreach (var address in addresses)
        {
            var domain = RoutingValueNormalizer.DomainFromEmail(address);
            if (SyntheticIdentityGuard.IsSyntheticDomain(domain)) { skips.Add(SkipSynthetic); continue; }

            // P1. The mailbox list alone missed every colleague who forwards from a staff domain
            // that is not an intake mailbox: rfq@alquraishi.com is the mailbox, the salesman is
            // ahmed@alquraishi.com.sa, and one confirmed forward taught that domain as SEC's, so
            // every later forward from any colleague linked to SEC whatever the attachment said.
            // Active users now count (TenantSelfIdentity), and so does a domain whose own name
            // spells ours, which covers the colleague who has no Nexora login at all.
            // One predicate, shared with the resolver and routing: this second test used to live
            // here alone, so the learner refused a domain the other two still linked on.
            if (TenantSelfIdentity.IsOurs(domain, selfDomains, selfNameKeys))
            {
                skips.Add(SkipSelfIdentity);
                continue;
            }

            // P2/P3: an Email identifier is the STRONGEST thing this engine can write — verified,
            // 1.00, exclusive to one customer for the whole tenant. That is only honest when the
            // mailbox belongs to the buying organisation.
            //
            // On the owner's live tenant "Saudi Aramco" ended up owning personal addresses at
            // live.com and bidnet.com, each from one confirmation on one document. A freight
            // agent forwards bids for four different end customers from one gmail account, and a
            // portal relays every buyer's RFQ from noreply@ariba.com; learning either pins EVERY
            // later forward to whichever customer happened to be confirmed first, whatever the
            // attachment says — and it does it at authoritative confidence, so no later evidence
            // on the page can outvote it.
            //
            // The address is not thrown away as evidence: an Email identifier a human entered on
            // the customer profile still matches exactly (resolver tier A1). What we refuse to do
            // is MINT one from a single document.
            if (SyntheticIdentityGuard.IsFreeMailDomain(domain)
                || SyntheticIdentityGuard.IsPortalRelayDomain(domain))
            {
                skips.Add(SkipPersonalOrRelayAddress);
                continue;
            }
            if (domain is null || !IdentityDomainGuard.IsOrganisationDomain(domain, selfDomains))
            {
                skips.Add(SkipSynthetic);
                continue;
            }

            if (!ties.TryGetValue(domain, out var tie))
                ties[domain] = tie = await TieDomainToCustomerAsync(
                    businessUnitId, lead.Id, customerId, customerName, domain, expiredIds,
                    documentNamesSomebodyElse, selfNameKeys, ct);

            // P10. Passing every guard above only says the domain belongs to SOME organisation.
            // Hyundai E&C buying for a Ras Tanura job prints buyer@hdec.com, and the reviewer
            // rightly says the document is Saudi Aramco's. Minting hdec.com as Aramco's Domain at
            // 0.95 made every later Hyundai enquiry, for any site owner, link to Aramco at S2 —
            // which decides before the passage tier's contractor and consignee rules ever run.
            // If Hyundai already had a contact at hdec.com, P3 skipped only the Email half, the
            // Domain half was still written, and all hdec.com mail was AMBIGUOUS from then on.
            if (tie.AnotherCustomerOwnsAddress(address))
            {
                skips.Add(SkipAliasConflict);
            }
            else if (tie.CustomerAddresses.Contains(address)
                     || (tie.TiedToCustomer && !tie.ClaimedByAnotherCustomer(exceptAddress: address)))
            {
                proposals.Add(new Proposal(CustomerIdentifierType.Email, address, address, true, 1.00m));
            }
            // An address nothing ties to the customer is NOT filed even as a suggestion. Email is
            // exclusive per tenant whatever its source (UX_customer_identifiers_authoritative and
            // CustomerIdentityMaintenance both ignore Source), so an unverified buyer@hdec.com
            // against Aramco would make saving Hyundai's real contact at that address fail with
            // "already linked to another customer" — and there is no command to remove it. The
            // reviewer's decision is not lost: where this document printed the buyer's address on the
            // domain, this lead's own human link is what ties the domain on the next confirmation.

            if (tie.ClaimedByAnotherCustomer(exceptAddress: null))
            {
                skips.Add(SkipDomainClaimedByAnotherCustomer);
            }
            else if (tie.TiedToCustomer)
            {
                proposals.Add(new Proposal(CustomerIdentifierType.Domain, domain, domain, true, 0.95m));
            }
            else
            {
                skips.Add(SkipDomainNotTiedToCustomer);
                proposals.Add(new Proposal(
                    CustomerIdentifierType.Domain, domain, domain, false, 0.50m, UnverifiedAliasSource));
                _log?.LogInformation(
                    "Client alias learning filed domain {Domain} for customer {CustomerId} as unverified on lead {LeadId}: nothing ties it to that customer.",
                    domain, customerId, lead.Id);
            }
        }

        // 2. The buying organisation's name, as printed.
        if (companyDisplay is not null)
        {
            if (aliasKey.Length == 0 || SelfIdentityGuard.IsSelfName(aliasKey, selfNameKeys))
            {
                skips.Add(SkipSelfIdentity);
            }
            else if (!CustomerNameDistinctiveness.HasDistinctiveToken(companyDisplay))
            {
                // The extractor took the country line of the address block. "SAUDI ARABIA"
                // confirmed once against Aramco became a trusted alias, the passage scan accepts
                // any taught alias of three letters or more, and from then on every document that
                // wrote "Kingdom of Saudi Arabia" in its buyer sentence or delivery address linked
                // to Aramco at 0.88 — including an RFQ from Saudi Kayan, who is not a customer.
                skips.Add(SkipAliasNotDistinctive);
            }
            else if (!ResemblesCustomerName(companyDisplay, customerName))
            {
                // P8. The live tenant's "Saudi Aramco" carries the aliases "FULTON COUNTY
                // GOVERNMENT", "KORICS" and "ARCENE SUPPLY SERVICES LLP" because the only gates
                // here were "not empty" and "not our own name": nothing ever compared the words
                // on the page to the words on the customer record. Each came from ONE human
                // confirmation on ONE document, and afterwards every document naming any of
                // those companies was offered to Aramco at 0.90.
                //
                // A reviewer confirming a lead is saying "this DOCUMENT belongs to this client".
                // That is a true and useful statement even when the company-name field holds the
                // end user, the consignee, a sister company or an extraction error — but it is
                // NOT the statement "this name IS this client", and only the second one may be
                // written as authoritative identity. So the evidence is still recorded, at
                // suggestion grade under a source the resolver does not trust, where a person can
                // confirm or delete it.
                //
                // Rows already poisoned in production predate this gate and are untouched by it;
                // they need a one-off review, which is a data job, not a code path.
                skips.Add(SkipAliasUnlikeCustomer);
                proposals.Add(new Proposal(
                    CustomerIdentifierType.Alias, aliasKey, companyDisplay, false, 0.50m, UnverifiedAliasSource));
                _log?.LogInformation(
                    "Client alias learning recorded \"{Alias}\" for customer {CustomerId} as unverified on lead {LeadId}: it does not resemble the customer's own name.",
                    companyDisplay, customerId, lead.Id);
            }
            else
            {
                // Resembling the chosen customer is not enough when another customer is at least
                // as good a reading of the same words. "SCC" is Saudi Cable's initials AND Saudi
                // Ceramics'; the resolver refuses to derive initials two customers share, but a
                // taught alias is never suppressed, so confirming "SCC" once for Saudi Cable made
                // every Saudi Ceramics delivery address reading "SCC Riyadh Plant 2" link to Saudi
                // Cable at 0.88.
                var otherNames = await LoadOtherCustomerNamesAsync(businessUnitId, customerId, ct);
                if (otherNames is null
                    || NamesAnotherCustomerAtLeastAsClosely(companyDisplay, customerName, otherNames))
                {
                    skips.Add(SkipAliasNamesAnotherCustomer);
                    proposals.Add(new Proposal(
                        CustomerIdentifierType.Alias, aliasKey, companyDisplay, false, 0.50m, UnverifiedAliasSource));
                    _log?.LogInformation(
                        "Client alias learning recorded \"{Alias}\" for customer {CustomerId} as unverified on lead {LeadId}: another customer in tenant {BusinessUnitId} owns that name at least as closely.",
                        companyDisplay, customerId, lead.Id, businessUnitId);
                }
                else
                {
                    proposals.Add(new Proposal(CustomerIdentifierType.Alias, aliasKey, companyDisplay, true, 0.90m));
                }
            }
        }

        // 3. Portal + OUR vendor code at that portal. The pair is unique per customer and
        //    perfectly learnable; our vendor code ALONE identifies us, never them, so it is
        //    never learned on its own.
        if (!string.IsNullOrWhiteSpace(lead.CustomerPortalNameExtracted)
            && !string.IsNullOrWhiteSpace(lead.SupplierAccountRefOnDocument))
        {
            // P9. The pair only names a buyer when the BUYER issued the number. SEC's vendor
            // code 2004414 was issued by SEC and means nothing anywhere else, so SEC's own
            // "MATERIALS E-BIDDING SYSTEM" stays learnable. Our Ariba Network ID is ONE number
            // that identifies US to every buyer on the network: learn that pair against the
            // first Ariba buyer and every later Ariba RFQ, from any buyer at all, auto-links to
            // that one customer at 0.92 — and teaching a second buyer only makes every Ariba
            // document permanently AMBIGUOUS, which no further teaching can undo.
            if (SharedSupplierNetworks.IsShared(lead.CustomerPortalNameExtracted))
                skips.Add(SkipSharedSupplierNetwork);
            else if (portalPair is { } pair)
            {
                // P11. The pair was written verified at 0.92 from one confirmation with no look at the
                // document, and the learned-portal tier decides before the passage tier. One mis-click on
                // an SEC e-bidding print therefore linked every later SEC print to Saudi Aramco at 0.92,
                // and lead 680's delivery address never got a say. Where SEC already held the pair, the
                // same click wrote it a second time and every print from SEC's portal went AMBIGUOUS for
                // ever. So a pair is a fact only when this document names the customer picked, or an
                // earlier human decision on the same pair does; SEC's own prints name SEC in the address.
                if (await PortalPairHeldByAnotherCustomerAsync(businessUnitId, customerId, pair.Key, expiredIds, ct))
                {
                    skips.Add(SkipPortalAccountClaimedByAnotherCustomer);
                }
                else if (DocumentNamesCustomer(lead, customerId, customerName, selfNameKeys)
                         || await EarlierDecisionCarriesPairAsync(businessUnitId, lead, customerId, pair.Key, ct))
                {
                    proposals.Add(new Proposal(CustomerIdentifierType.PortalAccount, pair.Key, pair.Display, true, 0.92m));
                }
                else
                {
                    skips.Add(SkipPortalAccountNotTiedToCustomer);
                    proposals.Add(new Proposal(
                        CustomerIdentifierType.PortalAccount, pair.Key, pair.Display, false, 0.50m, UnverifiedAliasSource));
                    _log?.LogInformation(
                        "Client alias learning filed portal pair {Pair} for customer {CustomerId} as unverified on lead {LeadId}: nothing ties it to that customer.",
                        pair.Key, customerId, lead.Id);
                }
            }
        }

        // 4. The RFQ-number SHAPE. Suggestion-grade FOREVER (IsVerified = false): the
        //    learned-alias tier requires IsVerified, so a numbering convention can propose a
        //    client but can never link one.
        var pattern = RfqNumberPattern.Derive(lead.Rfqno);
        if (pattern is not null)
            proposals.Add(new Proposal(
                CustomerIdentifierType.RfqNumberPattern, pattern, lead.Rfqno!.Trim(), false, 0.50m));

        if (proposals.Count == 0)
        {
            skips.Add(SkipNoEvidence);
            return new CustomerAliasLearningResult(0, 0, expiredIds.Count, skips);
        }

        // P3: authoritative types are exclusive per tenant. Reuse the store's own advisory
        // locking + exclusivity check rather than racing it — and on conflict SKIP the value,
        // never steal it from the customer that already owns it.
        var authoritative = proposals
            .Where(p => p.Type is CustomerIdentifierType.Email or CustomerIdentifierType.Phone)
            .Select(p => (p.Type, Value: (string?)p.DisplayValue))
            .ToList();
        var authoritativeBlocked = false;
        if (authoritative.Count > 0)
        {
            try
            {
                await CustomerIdentityMaintenance.EnsureAuthoritativeValuesAvailableAsync(
                    _db, businessUnitId, customerId, authoritative, ct);
            }
            catch (CustomerIdentityConflictException)
            {
                authoritativeBlocked = true;
                skips.Add(SkipAliasConflict);
                _log?.LogInformation(
                    "Client alias learning skipped an authoritative value for lead {LeadId}: it already belongs to another customer in tenant {BusinessUnitId}.",
                    lead.Id, businessUnitId);
            }
        }

        var learned = 0;
        var reinforced = 0;
        foreach (var proposal in proposals)
        {
            if (authoritativeBlocked && proposal.Type is CustomerIdentifierType.Email or CustomerIdentifierType.Phone)
                continue;

            var current = await _db.Set<CustomerIdentifier>().IgnoreQueryFilters()
                .FirstOrDefaultAsync(i => i.BusinessUnitId == businessUnitId
                                          && i.IdentifierType == proposal.Type
                                          && i.NormalizedValue == proposal.NormalizedValue
                                          && i.CustomerId == customerId
                                          && i.EffectiveTo == null, ct);
            if (current is not null)
            {
                current.ObservationCount += 1;
                current.LastObservedOn = now;
                // A human just re-confirmed it: a suggestion-grade row may be promoted, but
                // an RFQ-number SHAPE stays unverified by design.
                if (proposal.IsVerified && proposal.Type != CustomerIdentifierType.RfqNumberPattern)
                {
                    current.IsVerified = true;
                    if (current.Confidence < proposal.Confidence) current.Confidence = proposal.Confidence;
                    // The shelf moves with the flag. The resolver checks Source as well as
                    // IsVerified, so a row promoted on the flag alone stayed on the untrusted
                    // shelf: the second confirmation that finally tied a domain to its customer
                    // was counted and then ignored forever. Only this class's own unverified shelf
                    // is ever moved; a row a person typed keeps the source they gave it.
                    if (string.Equals(current.Source, UnverifiedAliasSource, StringComparison.Ordinal))
                        current.Source = proposal.Source;
                }
                reinforced++;
                continue;
            }

            _db.Set<CustomerIdentifier>().Add(new CustomerIdentifier
            {
                BusinessUnitId = businessUnitId,
                CustomerId = customerId,
                IdentifierType = proposal.Type,
                NormalizedValue = proposal.NormalizedValue,
                DisplayValue = Truncate(proposal.DisplayValue, 320),
                IsVerified = proposal.IsVerified,
                Confidence = proposal.Confidence,
                Source = proposal.Source,
                EffectiveFrom = now,
                LearnedFromLeadId = lead.Id,
                LearnedFromReviewAuditId = reviewAuditId,
                ObservationCount = 1,
                LastObservedOn = now
            });
            learned++;
        }

        return new CustomerAliasLearningResult(learned, reinforced, expiredIds.Count, skips);
    }

    /// <summary>
    /// P5. Expires what the reviewer's correction contradicts and writes the expiry through at once.
    ///
    /// It used to expire only rows whose LearnedFromLeadId was THIS lead. A reinforced row keeps
    /// the id of the first lead that taught it, and that lead can no longer change client once it
    /// has an RFQ. So when lead A (an SEC print from 57322@se.com.sa) was linked to Aramco and
    /// converted, lead B from the same sender auto-linked to Aramco at 1.00, and the rep relinked
    /// B to SEC, nothing expired: the SEC Email was refused as ALIAS_CONFLICT, se.com.sa was
    /// added for SEC beside Aramco's (so the domain went AMBIGUOUS), and lead C from the same
    /// sender still linked to Aramco. The only remedies left were deactivating Aramco or SQL.
    ///
    /// Now every active row for the rejected customer that this class wrote (LeadReviewLearned or
    /// LeadReviewUnverified) and whose value this document carries is expired, whoever taught it.
    /// A row a person typed on the rejected customer's profile is left alone: P3 still refuses to
    /// take it, because the reviewer contradicted the machine, not the profile.
    ///
    /// The RFQ-number shape is expired only when this lead taught it. Two buyers can number their
    /// enquiries the same way, so one lead turning out to be SEC's says nothing about whether
    /// Aramco also writes C + nine digits.
    ///
    /// WHY IT IS WRITTEN THROUGH: EnsureAuthoritativeValuesAvailableAsync and this class's own
    /// domain reads query the database without the change tracker, so a staged expiry is invisible
    /// to them and the correction was refused against the very row it had just expired. Flushing
    /// with SaveChanges would also flush whatever else the caller has staged, so only these rows
    /// are updated, by id. The entities stay Modified with the same value, so the caller's
    /// SaveChanges agrees with the database and a failure path that detaches changed entries
    /// leaves nothing stale behind.
    /// </summary>
    private async Task ExpireContradictedAsync(
        long businessUnitId,
        long leadId,
        long previousCustomerId,
        IReadOnlyCollection<string> addresses,
        string aliasKey,
        string? portalKey,
        HashSet<long> expiredIds,
        DateTime now,
        CancellationToken ct)
    {
        var emails = addresses.ToArray();
        var domains = addresses
            .Select(RoutingValueNormalizer.DomainFromEmail)
            .Where(domain => !string.IsNullOrEmpty(domain))
            .Select(domain => domain!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var aliases = aliasKey.Length > 0 ? new[] { aliasKey } : Array.Empty<string>();
        var portals = portalKey is { Length: > 0 } ? new[] { portalKey } : Array.Empty<string>();

        var contradicted = await _db.Set<CustomerIdentifier>().IgnoreQueryFilters()
            .Where(i => i.BusinessUnitId == businessUnitId
                        && i.CustomerId == previousCustomerId
                        && i.EffectiveTo == null)
            .Where(i => i.LearnedFromLeadId == leadId
                        || (LearnedSources.Contains(i.Source)
                            && ((i.IdentifierType == CustomerIdentifierType.Email && emails.Contains(i.NormalizedValue))
                                || (i.IdentifierType == CustomerIdentifierType.Domain && domains.Contains(i.NormalizedValue))
                                || (i.IdentifierType == CustomerIdentifierType.Alias && aliases.Contains(i.NormalizedValue))
                                || (i.IdentifierType == CustomerIdentifierType.PortalAccount && portals.Contains(i.NormalizedValue)))))
            .ToListAsync(ct);
        if (contradicted.Count == 0) return;

        foreach (var identifier in contradicted)
        {
            identifier.EffectiveTo = now;
            expiredIds.Add(identifier.Id);
        }

        var ids = expiredIds.ToArray();
        await _db.Set<CustomerIdentifier>().IgnoreQueryFilters()
            .Where(i => i.BusinessUnitId == businessUnitId && ids.Contains(i.Id))
            .ExecuteUpdateAsync(set => set.SetProperty(i => i.EffectiveTo, (DateTime?)now), ct);
    }

    /// <summary>
    /// Whose domain is this, as far as anything a PERSON did can say? Read once per domain.
    ///
    /// Tied to the chosen customer by any one of:
    ///   * a contact on that customer at the domain, or an Email/Domain identifier a person put on
    ///     that customer's record (a row this class learned does not vouch for itself);
    ///   * an earlier document a person linked to that customer that PRINTED a buyer address on the
    ///     domain, with no earlier lead from the domain (printed or envelope) that a person linked to
    ///     anybody else. A domain humans have given to two customers is an intermediary's, whichever of
    ///     them this review picked. Neither that document nor this one may name a different
    ///     organisation in its company-name field;
    ///   * the domain spelling the customer's own name (<see cref="DomainLabelSpellsName"/>).
    ///
    /// Claimed by another customer when that customer has a contact at the domain or a verified
    /// Email or Domain identifier on it (rows filed unverified claim nothing; rows expired by this
    /// review are skipped).
    /// </summary>
    private async Task<DomainTie> TieDomainToCustomerAsync(
        long businessUnitId,
        long leadId,
        long customerId,
        string? customerName,
        string domain,
        IReadOnlySet<long> expiredIds,
        bool documentNamesSomebodyElse,
        IReadOnlySet<string> selfNameKeys,
        CancellationToken ct)
    {
        var atDomain = "@" + domain;
        var tie = new DomainTie();

        var contacts = await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.BusinessUnitId == businessUnitId
                        && c.CustomerId != null
                        && c.IsActive != false
                        && c.Email != null
                        && c.Email.ToLower().EndsWith(atDomain))
            .Where(c => _db.Customers.Any(x => x.Buid == businessUnitId && x.Id == c.CustomerId && x.IsActive != false))
            .OrderBy(c => c.Id)
            .Select(c => new { CustomerId = c.CustomerId!.Value, Email = c.Email! })
            .Take(MaximumDomainEvidenceRead)
            .ToListAsync(ct);
        foreach (var contact in contacts)
        {
            var address = contact.Email.Trim().ToLowerInvariant();
            if (contact.CustomerId == customerId)
            {
                tie.TiedToCustomer = true;
                tie.CustomerAddresses.Add(address);
            }
            else
            {
                tie.OtherCustomerFacts.Add((contact.CustomerId, address));
            }
        }

        var facts = await _db.Set<CustomerIdentifier>().AsNoTracking().IgnoreQueryFilters()
            .Where(i => i.BusinessUnitId == businessUnitId
                        && i.EffectiveTo == null
                        && i.IsVerified
                        && i.Source != UnverifiedAliasSource
                        && ((i.IdentifierType == CustomerIdentifierType.Domain && i.NormalizedValue == domain)
                            || (i.IdentifierType == CustomerIdentifierType.Email && i.NormalizedValue.EndsWith(atDomain))))
            .Where(i => _db.Customers.Any(c => c.Buid == businessUnitId && c.Id == i.CustomerId && c.IsActive != false))
            .OrderBy(i => i.Id)
            .Select(i => new { i.Id, i.CustomerId, i.IdentifierType, i.NormalizedValue, i.Source })
            .Take(MaximumDomainEvidenceRead)
            .ToListAsync(ct);
        foreach (var fact in facts)
        {
            if (expiredIds.Contains(fact.Id)) continue;
            var address = fact.IdentifierType == CustomerIdentifierType.Email ? fact.NormalizedValue : null;
            if (fact.CustomerId != customerId)
            {
                tie.OtherCustomerFacts.Add((fact.CustomerId, address));
            }
            else if (!string.Equals(fact.Source, CustomerIdentifierSources.LeadReviewLearned, StringComparison.Ordinal))
            {
                tie.TiedToCustomer = true;
                if (address is not null) tie.CustomerAddresses.Add(address);
            }
        }

        if (!tie.TiedToCustomer && DomainLabelSpellsName(domain, customerName))
            tie.TiedToCustomer = true;

        if (!tie.TiedToCustomer)
        {
            // Clientemail may still carry a display name ("Ali <ali@se.com.sa>"), hence the ">" form.
            var bracketed = atDomain + ">";
            var earlier = await _db.Leads.AsNoTracking().IgnoreQueryFilters()
                .Where(l => l.BusinessUnitId == businessUnitId
                            && l.Id != leadId
                            && l.CustomerId != null
                            && ((l.Clientemail != null
                                 && (l.Clientemail.ToLower().EndsWith(atDomain) || l.Clientemail.ToLower().EndsWith(bracketed)))
                                || (l.CustomerBuyerEmailExtracted != null
                                    && (l.CustomerBuyerEmailExtracted.ToLower().EndsWith(atDomain)
                                        || l.CustomerBuyerEmailExtracted.ToLower().EndsWith(bracketed)))))
                .OrderByDescending(l => l.Id)
                .Select(l => new
                {
                    CustomerId = l.CustomerId!.Value, l.CustomerMatchStatus,
                    l.CustomerBuyerEmailExtracted, l.CustomerCompanyNameExtracted
                })
                .Take(MaximumDomainEvidenceRead)
                .ToListAsync(ct);
            // Only a HUMAN decision vouches. A machine match from this domain is the very thing
            // this rule exists to stop compounding.
            var decided = earlier.Where(row => LeadCustomerMatchStatuses.IsHumanDecided(row.CustomerMatchStatus)).ToList();
            // THE ENVELOPE NEVER VOUCHES FOR ITSELF. Any earlier human decision from a mailbox on the
            // domain used to tie it, read off the envelope sender. So an intermediary that forwarded the
            // same buyer's bids twice vouched for its own domain: a colleague with no Nexora login on a
            // group domain that does not spell our name, a freight agent on a consumer provider nobody
            // listed, two people at an EPC contractor accepting the site owner's jobs. The second
            // confirmation wrote the whole domain at 0.95 and the address at 1.00, and every other
            // person on it was linked to that customer at S2 whatever the document said. The envelope
            // says who carried the mail; a buyer address PRINTED on the document speaks for the buyer.
            // Envelope decisions for another customer still count against the tie, which only ever
            // makes this more careful. The document's company-name field is asked too, on this lead and
            // on the vouching one: a Hyundai requisition that prints its own buyer's address is still
            // Hyundai's mailbox, whoever the site belongs to.
            if (!documentNamesSomebodyElse
                && decided.Count > 0
                && decided.All(row => row.CustomerId == customerId)
                && decided.Any(row => PrintsAnAddressOn(row.CustomerBuyerEmailExtracted, domain)
                                      && !NamesAnOrganisationOtherThan(row.CustomerCompanyNameExtracted, customerName, selfNameKeys)))
                tie.TiedToCustomer = true;
        }

        return tie;
    }

    private async Task<List<string?>?> LoadOtherCustomerNamesAsync(long businessUnitId, long customerId, CancellationToken ct)
    {
        // Every active customer, not a capped sample: the resolver's own shared-initials check
        // was computed from a capped list, a customer filtered out stopped counting, and initials
        // two customers share looked unique. Past the cap this answers "cannot tell" (null).
        var names = await _db.Customers.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.Buid == businessUnitId && c.Id != customerId && c.IsActive != false)
            .OrderBy(c => c.Id)
            .Select(c => (string?)c.Name)
            .Take(MaximumCustomersCompared + 1)
            .ToListAsync(ct);
        return names.Count > MaximumCustomersCompared ? null : names;
    }

    /// <summary>
    /// Could the company name printed on the document be the customer a reviewer just picked?
    /// It must carry at least one word that names ONE company
    /// (<see cref="CustomerNameDistinctiveness"/>), and read as the customer in one of these ways:
    ///   * the customer's name itself, or its initials as the buyer writes them ("SEC", "SWCC");
    ///   * the customer's name as one part of a longer print, set off by a dash, bracket, slash
    ///     or comma ("SAUDI ARABIAN OIL COMPANY - SAUDI ARAMCO",
    ///     "Saudi Electricity Company - Eastern Operating Area");
    ///   * a shortening: every distinctive word printed is one of the customer's own ("ARAMCO",
    ///     "SAUDI ELECTRICITY CO.", "ALRAJHI" for "Al Rajhi Bank"), allowing one slipped letter
    ///     in a long word ("Electricty").
    ///
    /// It used to be enough to score 0.75 on Jaro-Winkler, and that metric rewards four shared
    /// opening letters: "SAUDI ELECTRICITY" scored 0.79 against "Saudi Aramco", so a reviewer's
    /// mis-click wrote SEC's name as a trusted Aramco alias and every lead-680-shaped SEC print went
    /// AMBIGUOUS. Sharing SAUDI, ARABIAN or NATIONAL is sharing nothing.
    ///
    /// A print carrying distinctive words the customer's name does NOT have is not accepted
    /// either, however much of the name it contains: "SAUDI ARAMCO TOTAL REFINING AND
    /// PETROCHEMICAL" is SATORP, a separate buyer, and "HYUNDAI ENGINEERING FOR SAUDI ARAMCO" is
    /// the contractor. A branch name printed that way ("MARAFIQ Yanbu Power &amp; Desalination") is
    /// recorded for review too; that false reject costs one unverified row somebody can promote,
    /// while a false accept costs an authoritative alias that quietly mis-links documents forever.
    ///
    /// Fails closed on a customer with no readable name: with nothing to compare against,
    /// "accept whatever is printed" is the bug, not the fallback.
    /// </summary>
    public static bool ResemblesCustomerName(string? extracted, string? customerName)
        => Closeness(NameShape.Of(extracted), SegmentsOf(extracted), NameShape.Of(customerName)).Tier >= ResemblingTier;

    /// <summary>
    /// True when some other customer is at least as good a reading of the printed name as the
    /// chosen one: the same initials ("SCC" for Saudi Cable and Saudi Ceramics), the other
    /// customer's exact name ("SAUDI ARAMCO" confirmed for SATORP), or a closer shortening
    /// ("ARAMCO" is all of Saudi Aramco's own words and one of SATORP's five). A tie counts
    /// against the chosen customer: two equally good readings are not one customer's alias.
    /// False when the printed name does not resemble the chosen customer at all.
    /// </summary>
    public static bool NamesAnotherCustomerAtLeastAsClosely(
        string? extracted, string? customerName, IEnumerable<string?> otherCustomerNames)
    {
        ArgumentNullException.ThrowIfNull(otherCustomerNames);
        var printed = NameShape.Of(extracted);
        var segments = SegmentsOf(extracted);
        var chosen = Closeness(printed, segments, NameShape.Of(customerName));
        if (chosen.Tier < ResemblingTier) return false;

        foreach (var name in otherCustomerNames)
            if (AtLeastAsClose(Closeness(printed, segments, NameShape.Of(name)), chosen))
                return true;
        return false;
    }

    /// <summary>
    /// True when some customer other than the owner reads the printed name as a resemblance at least as
    /// closely as the owner does. Unlike <see cref="NamesAnotherCustomerAtLeastAsClosely"/> the owner
    /// need not resemble the name at all: this is asked of a name a reviewer ALREADY taught, and a row
    /// poisoned before the gate existed ("SAUDI ELECTRICITY" on Saudi Aramco) resembles its owner not at
    /// all and is exactly the row that must give way. The same tiers, so the learner and the resolver
    /// cannot disagree about whose name a name is.
    /// </summary>
    public static bool AnotherCustomerReadsTheNameAtLeastAsClosely(
        string? printed, string? ownerName, IEnumerable<string?> otherCustomerNames)
    {
        ArgumentNullException.ThrowIfNull(otherCustomerNames);
        var shape = NameShape.Of(printed);
        var segments = SegmentsOf(printed);
        var owner = Closeness(shape, segments, NameShape.Of(ownerName));
        foreach (var name in otherCustomerNames)
            if (AtLeastAsClose(Closeness(shape, segments, NameShape.Of(name)), owner))
                return true;
        return false;
    }

    /// <summary>A resemblance at all, and at least as close as <paramref name="than"/>. A tie counts.</summary>
    private static bool AtLeastAsClose(NameCloseness other, NameCloseness than)
        => other.Tier >= ResemblingTier
           && (other.Tier > than.Tier || (other.Tier == than.Tier && other.Weight >= than.Weight));

    /// <summary>
    /// The name rules over one fixed customer list, with every customer's name shape worked out once.
    /// The resolver asks them of each taught name it finds on a page and of the name a mailbox is signed
    /// with; working out two thousand name shapes per name found would be paid on every lead.
    /// </summary>
    internal sealed class NameReadings
    {
        private readonly List<(long CustomerId, NameShape Shape)> _customers;

        public NameReadings(IEnumerable<CustomerNameSnapshot> customers)
        {
            ArgumentNullException.ThrowIfNull(customers);
            _customers = customers
                .GroupBy(customer => customer.CustomerId)
                .Select(group => (group.Key, NameShape.Of(group.First().Name)))
                .ToList();
        }

        /// <summary><see cref="AnotherCustomerReadsTheNameAtLeastAsClosely"/> for a name owned by <paramref name="ownerId"/>.</summary>
        public bool AnotherCustomerReadsAtLeastAsClosely(string printed, long ownerId)
        {
            var shape = NameShape.Of(printed);
            var segments = SegmentsOf(printed);
            NameCloseness owner = default;
            foreach (var (customerId, name) in _customers)
                if (customerId == ownerId) owner = Closeness(shape, segments, name);
            foreach (var (customerId, name) in _customers)
                if (customerId != ownerId && AtLeastAsClose(Closeness(shape, segments, name), owner))
                    return true;
            return false;
        }

        /// <summary>
        /// The one customer the printed name reads as, when it resembles that customer and no other
        /// customer reads it at least as closely; otherwise null.
        /// </summary>
        public long? ReadsAsExactlyOne(string printed)
        {
            var shape = NameShape.Of(printed);
            var segments = SegmentsOf(printed);
            long? best = null;
            NameCloseness bestCloseness = default;
            var tied = false;
            foreach (var (customerId, name) in _customers)
            {
                var closeness = Closeness(shape, segments, name);
                if (closeness.Tier < ResemblingTier) continue;
                if (best is null || closeness.Tier > bestCloseness.Tier
                    || (closeness.Tier == bestCloseness.Tier && closeness.Weight > bestCloseness.Weight))
                {
                    best = customerId;
                    bestCloseness = closeness;
                    tied = false;
                }
                else if (closeness.Tier == bestCloseness.Tier && closeness.Weight >= bestCloseness.Weight)
                {
                    tied = true;
                }
            }
            return tied ? null : best;
        }
    }

    /// <summary>
    /// Does the domain's own name spell the organisation's name? "marafiq.com.sa" spells Marafiq,
    /// "alrajhibank.com.sa" spells Al Rajhi Bank, "fultoncountyga.gov" opens with Fulton County.
    /// Read from the registrable label only ("mail.sabic.com" is sabic; "aramco.hdec.com" is
    /// hdec, whose owner chose the host names under it).
    ///
    /// Initials never count. se.com is Schneider Electric's real domain and "SE" is also the
    /// initials of Saudi Electricity: a Schneider enquiry about an SEC substation is the
    /// contractor case, not SEC speaking. A single word must be four letters or more unless it is
    /// the organisation's whole name, so "oil.com" does not spell Saudi Arabian Oil Company.
    /// Arabic words cannot appear in a domain label and are ignored.
    /// </summary>
    public static bool DomainLabelSpellsName(string? domainOrAddress, string? name)
    {
        var label = RegistrableLabel(IdentityDomainGuard.DomainOf(domainOrAddress));
        if (label is null) return false;
        var tokens = CustomerNameDistinctiveness.DistinctiveTokens(name).Where(IsLatinWord).ToList();
        if (tokens.Count == 0) return false;
        var nameKey = CustomerNameNormalizer.LooseKey(name);
        var tightKey = CustomerNameNormalizer.TightKey(name);

        foreach (var spelling in LabelSpellings(label))
        {
            if (string.Equals(spelling, tightKey, StringComparison.Ordinal)) return true;
            if (tokens.Count >= 2)
            {
                if (string.Equals(spelling, string.Concat(tokens), StringComparison.Ordinal)) return true;
                if (spelling.StartsWith(tokens[0] + tokens[1], StringComparison.Ordinal)) return true;
            }
            foreach (var token in tokens)
                if (string.Equals(spelling, token, StringComparison.Ordinal)
                    && (token.Length >= 4 || string.Equals(nameKey, token, StringComparison.Ordinal)))
                    return true;
        }
        return false;
    }

    // ── name closeness ────────────────────────────────────────────────────────

    /// <summary>
    /// Tiers, strongest first: 4 the name itself or its initials; 3 the name as one delimited part
    /// of the print; 2 every distinctive word printed is the customer's; 1 some words shared but
    /// the print carries words the customer's name does not. 2 and above resemble.
    /// </summary>
    private const int ResemblingTier = 2;

    private readonly record struct NameCloseness(int Tier, double Weight);

    private sealed record NameShape(string Key, string Tight, string Acronym, IReadOnlyList<string> Tokens)
    {
        public static NameShape Of(string? name) => new(
            CustomerNameNormalizer.LooseKey(name),
            CustomerNameNormalizer.TightKey(name),
            CustomerNameNormalizer.AcronymKey(name),
            CustomerNameDistinctiveness.DistinctiveTokens(name));
    }

    /// <summary>
    /// Characters a print uses to set one name beside another. "&amp;" is not one: it joins words
    /// inside a single name ("Power &amp; Water"). A hyphen is, although it also joins "Al-Rajhi":
    /// a part is only ever compared for EQUALITY with a whole customer name, so a stray "Al" part
    /// matches nothing.
    /// </summary>
    private static readonly char[] SegmentSeparators = ['-', '–', '—', '(', ')', '[', ']', '/', '\\', ',', ';', ':', '|'];

    private static IReadOnlyList<NameShape> SegmentsOf(string? printed)
    {
        if (string.IsNullOrWhiteSpace(printed)) return [];
        var parts = printed.Split(SegmentSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return [];
        return parts.Select(NameShape.Of).Where(shape => shape.Key.Length > 0).ToList();
    }

    private static NameCloseness Closeness(NameShape printed, IReadOnlyList<NameShape> segments, NameShape customer)
    {
        // A print with no word of its own names nobody, whatever it happens to equal (P16).
        if (printed.Key.Length == 0 || customer.Key.Length == 0 || printed.Tokens.Count == 0) return default;

        if (IsTheName(printed, customer)) return new NameCloseness(4, 1d);

        var longestPart = 0;
        foreach (var segment in segments)
            if (IsTheName(segment, customer))
                longestPart = Math.Max(longestPart, segment.Key.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        // Weighted by how many words of the print the name accounts for, so "Saudi Electricity
        // Company-DAMMAM" reads as SEC (two words) before it reads as "Al Dammam Trading" (one).
        if (longestPart > 0) return new NameCloseness(3, longestPart);

        if (customer.Tokens.Count == 0) return default;
        var owned = new HashSet<string>(StringComparer.Ordinal);
        var everyPrintedWordIsTheirs = true;
        foreach (var word in printed.Tokens)
        {
            var match = customer.Tokens.FirstOrDefault(own => SameWord(word, own));
            if (match is null) everyPrintedWordIsTheirs = false;
            else owned.Add(match);
        }
        if (owned.Count == 0) return default;
        // Weighted by the share of the CUSTOMER's words the print carries: "ARAMCO" is all of
        // Saudi Aramco's distinctive words and one fifth of SATORP's.
        return everyPrintedWordIsTheirs
            ? new NameCloseness(2, (double)owned.Count / customer.Tokens.Count)
            : new NameCloseness(1, owned.Count);
    }

    private static bool IsTheName(NameShape printed, NameShape customer)
    {
        if (printed.Key.Length == 0) return false;
        if (string.Equals(printed.Key, customer.Key, StringComparison.Ordinal)) return true;
        if (printed.Tight.Length > 0 && string.Equals(printed.Tight, customer.Tight, StringComparison.Ordinal)) return true;
        // AcronymKey is empty for a name too short to have distinctive initials and for the
        // ordinary words listed in CustomerNameNormalizer.AmbiguousAcronyms, so an empty acronym
        // must never be allowed to "equal" anything.
        return customer.Acronym.Length > 0 && string.Equals(printed.Key, customer.Acronym, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same distinctive word: identical, printed with the article glued on ("ALRAJHI" for
    /// "RAJHI", decided by <see cref="CustomerNameDistinctiveness.SharesDistinctiveToken"/> so the
    /// two classes cannot disagree about it), or one slipped letter in a long word.
    /// </summary>
    private static bool SameWord(string printed, string own)
    {
        if (string.Equals(printed, own, StringComparison.Ordinal)) return true;
        var gap = Math.Abs(printed.Length - own.Length);
        if (gap == 2) return CustomerNameDistinctiveness.SharesDistinctiveToken(printed, own);
        return gap <= 1 && IsOneLetterSlip(printed, own);
    }

    /// <summary>
    /// One letter inserted, dropped, replaced or swapped with its neighbour, in words of at least
    /// <see cref="MinimumSlipWordLength"/> letters. Shorter words are too close to other words:
    /// RAJHI and RASHID are different families.
    /// </summary>
    private static bool IsOneLetterSlip(string a, string b)
    {
        if (a.Length < MinimumSlipWordLength || b.Length < MinimumSlipWordLength) return false;
        if (Math.Abs(a.Length - b.Length) > 1) return false;
        var i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
        if (a.Length == b.Length)
        {
            if (i == a.Length) return true;
            if (a.AsSpan(i + 1).SequenceEqual(b.AsSpan(i + 1))) return true;
            return i + 1 < a.Length
                   && a[i] == b[i + 1] && a[i + 1] == b[i]
                   && a.AsSpan(i + 2).SequenceEqual(b.AsSpan(i + 2));
        }
        var (longer, shorter) = a.Length > b.Length ? (a, b) : (b, a);
        return longer.AsSpan(i + 1).SequenceEqual(shorter.AsSpan(i));
    }

    // ── domains ───────────────────────────────────────────────────────────────

    /// <summary>Second-level labels a country registry sells under ("com.sa", "gov.sa", "co.uk").</summary>
    private static readonly HashSet<string> SecondLevelLabels = new(StringComparer.Ordinal)
    {
        "com", "net", "org", "gov", "edu", "co", "ac", "sch", "med", "ltd", "plc", "mil", "or", "ne", "go", "nic"
    };

    private static string? RegistrableLabel(string? domain)
    {
        if (string.IsNullOrEmpty(domain)) return null;
        var labels = domain.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (labels.Length < 2) return null;
        var index = labels.Length - 2;
        if (labels.Length >= 3 && labels[^1].Length == 2 && SecondLevelLabels.Contains(labels[index])) index--;
        return labels[index];
    }

    private static IEnumerable<string> LabelSpellings(string label)
    {
        var letters = new string(label.Where(char.IsAsciiLetter).ToArray()).ToUpperInvariant();
        if (letters.Length < 3) yield break;
        yield return letters;
        // "alrajhibank", "alquraishi": the article is glued on in a domain even when the name drops it.
        if (letters.Length > 5 && (letters.StartsWith("AL", StringComparison.Ordinal) || letters.StartsWith("EL", StringComparison.Ordinal)))
            yield return letters[2..];
    }

    private static bool IsLatinWord(string token) => token.All(c => c is >= 'A' and <= 'Z');

    /// <summary>What one domain is, as far as the tenant's own records say. See <see cref="TieDomainToCustomerAsync"/>.</summary>
    private sealed class DomainTie
    {
        public bool TiedToCustomer { get; set; }

        /// <summary>Exact addresses a person put on the chosen customer at this domain.</summary>
        public HashSet<string> CustomerAddresses { get; } = new(StringComparer.Ordinal);

        /// <summary>Another customer's hold on the domain; Address is null for a whole-domain row.</summary>
        public List<(long CustomerId, string? Address)> OtherCustomerFacts { get; } = [];

        public bool AnotherCustomerOwnsAddress(string address) =>
            OtherCustomerFacts.Any(fact => string.Equals(fact.Address, address, StringComparison.Ordinal));

        /// <summary>
        /// Any hold at all when <paramref name="exceptAddress"/> is null; otherwise a hold on the domain
        /// other than that one address, which P3's exclusivity check answers for on its own.
        /// </summary>
        public bool ClaimedByAnotherCustomer(string? exceptAddress) =>
            OtherCustomerFacts.Any(fact => exceptAddress is null
                                           || !string.Equals(fact.Address, exceptAddress, StringComparison.Ordinal));
    }

    // ── lead reads ────────────────────────────────────────────────────────────

    private static (string Key, string Display)? PortalAccountPair(Lead lead)
    {
        var key = PortalAccountKey(lead.CustomerPortalNameExtracted, lead.SupplierAccountRefOnDocument);
        if (key is null) return null;
        return (key,
            $"{lead.CustomerPortalNameExtracted!.Trim()} / {lead.SupplierAccountRefOnDocument!.Trim()}");
    }

    /// <summary>The stored "portal|our-vendor-code" key, the same one the resolver's S3 compares; null when either half is missing.</summary>
    private static string? PortalAccountKey(string? portalName, string? supplierAccountRef)
    {
        if (string.IsNullOrWhiteSpace(portalName) || string.IsNullOrWhiteSpace(supplierAccountRef)) return null;
        var portalKey = CustomerNameNormalizer.LooseKey(portalName);
        var accountKey = RoutingValueNormalizer.Normalize(CustomerIdentifierType.ErpAccount, supplierAccountRef);
        return portalKey.Length == 0 || accountKey.Length == 0 ? null : $"{portalKey}|{accountKey}";
    }

    /// <summary>
    /// Whether the company-name field names an organisation other than the chosen customer: it carries a
    /// word that names one company, is not our own name, and does not resemble the customer
    /// (<see cref="ResemblesCustomerName"/>). A blank, generic or self-naming field names nobody else.
    /// </summary>
    private static bool NamesAnOrganisationOtherThan(string? companyName, string? customerName, IEnumerable<string> selfNameKeys)
    {
        if (string.IsNullOrWhiteSpace(companyName) || !CustomerNameDistinctiveness.HasDistinctiveToken(companyName))
            return false;
        if (SelfIdentityGuard.IsSelfName(CustomerNameNormalizer.LooseKey(companyName), selfNameKeys)) return false;
        return !ResemblesCustomerName(companyName, customerName);
    }

    /// <summary>Whether a printed buyer address (either shape, "Name &lt;a@b&gt;" or "a@b") is on the domain.</summary>
    private static bool PrintsAnAddressOn(string? printedBuyerAddress, string domain)
        => string.Equals(
            RoutingValueNormalizer.DomainFromEmail(LeadCustomerResolutionService.ParseAddress(printedBuyerAddress)),
            domain, StringComparison.Ordinal);

    /// <summary>
    /// P11. Whether this document names the customer the reviewer picked, read exactly as the resolver
    /// reads a page: its headers and delivery addresses, against that one customer's name and initials,
    /// with every rule a passage hit obeys (one word in an address is only offered, a name that runs on
    /// into a longer company name is only offered). Item text never counts: "SEC-specified barcode" is a
    /// specification, not a buyer.
    /// </summary>
    private static bool DocumentNamesCustomer(Lead lead, long customerId, string? customerName, IEnumerable<string> selfNameKeys)
    {
        if (string.IsNullOrWhiteSpace(customerName)) return false;
        var statements = LeadCustomerResolutionService.Passages(lead)
            .Where(passage => passage.Role != PassageRole.ItemText)
            .ToList();
        if (statements.Count == 0) return false;
        var outcome = CustomerIdentityResolver.Resolve(
            new LeadClientEvidence
            {
                BusinessUnitId = lead.BusinessUnitId, LeadId = lead.Id,
                SupplierNameOnDocument = lead.SupplierNameOnDocument,
                TenantSelfNameKeys = selfNameKeys.ToList(),
                Passages = statements
            },
            new ClientResolutionCorpus { Customers = [new CustomerNameSnapshot(customerId, customerName)] },
            new CustomerResolutionPolicy());
        return outcome.CustomerId == customerId;
    }

    /// <summary>P11. Whether another active customer already holds this pair as a fact (a row expired by this review does not count).</summary>
    private async Task<bool> PortalPairHeldByAnotherCustomerAsync(
        long businessUnitId, long customerId, string pairKey, IReadOnlySet<long> expiredIds, CancellationToken ct)
    {
        var holders = await _db.Set<CustomerIdentifier>().AsNoTracking().IgnoreQueryFilters()
            .Where(i => i.BusinessUnitId == businessUnitId
                        && i.EffectiveTo == null
                        && i.IsVerified
                        && i.Source != UnverifiedAliasSource
                        && i.IdentifierType == CustomerIdentifierType.PortalAccount
                        && i.NormalizedValue == pairKey
                        && i.CustomerId != customerId)
            .Where(i => _db.Customers.Any(c => c.Buid == businessUnitId && c.Id == i.CustomerId && c.IsActive != false))
            .OrderBy(i => i.Id)
            .Select(i => i.Id)
            .Take(MaximumDomainEvidenceRead)
            .ToListAsync(ct);
        return holders.Any(id => !expiredIds.Contains(id));
    }

    /// <summary>
    /// P11. Whether an earlier lead carrying the same pair was linked by a person to this customer, with
    /// no earlier human decision on the pair for anybody else. The vendor code is compared in SQL as
    /// printed (trimmed, upper case) and the whole key in C#, because LooseKey cannot run in SQL; a code
    /// printed in another format simply does not vouch.
    /// </summary>
    private async Task<bool> EarlierDecisionCarriesPairAsync(
        long businessUnitId, Lead lead, long customerId, string pairKey, CancellationToken ct)
    {
        var account = lead.SupplierAccountRefOnDocument!.Trim().ToUpper();
        var earlier = await _db.Leads.AsNoTracking().IgnoreQueryFilters()
            .Where(l => l.BusinessUnitId == businessUnitId
                        && l.Id != lead.Id
                        && l.CustomerId != null
                        && l.CustomerPortalNameExtracted != null
                        && l.SupplierAccountRefOnDocument != null
                        && l.SupplierAccountRefOnDocument.Trim().ToUpper() == account)
            .OrderByDescending(l => l.Id)
            .Select(l => new
            {
                CustomerId = l.CustomerId!.Value, l.CustomerMatchStatus,
                l.CustomerPortalNameExtracted, l.SupplierAccountRefOnDocument
            })
            .Take(MaximumDomainEvidenceRead)
            .ToListAsync(ct);
        var decided = earlier
            .Where(row => LeadCustomerMatchStatuses.IsHumanDecided(row.CustomerMatchStatus))
            .Where(row => string.Equals(
                PortalAccountKey(row.CustomerPortalNameExtracted, row.SupplierAccountRefOnDocument), pairKey, StringComparison.Ordinal))
            .ToList();
        return decided.Count > 0 && decided.All(row => row.CustomerId == customerId);
    }

    private static string? ResolveSender(Lead lead)
        => !string.IsNullOrWhiteSpace(lead.EmailIngests?.FromEmail)
            ? lead.EmailIngests!.FromEmail
            : lead.Clientemail;

    private async Task<HashSet<string>> LoadTenantSelfNameKeysAsync(
        long businessUnitId, Lead lead, CancellationToken ct)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var businessUnitName = await _db.BusinessUnits.AsNoTracking().IgnoreQueryFilters()
            .Where(b => b.Id == businessUnitId)
            .Select(b => b.BusinessUnitName)
            .SingleOrDefaultAsync(ct);
        Add(businessUnitName);
        // The document's own vendor/Vendname block names US, never the buyer.
        Add(lead.SupplierNameOnDocument);
        return keys;

        void Add(string? value)
        {
            var key = CustomerNameNormalizer.LooseKey(value);
            if (key.Length > 0) keys.Add(key);
        }
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max];

    /// <param name="Source">
    /// Which shelf the row is filed on. Defaults to the trusted
    /// <see cref="CustomerIdentifierSources.LeadReviewLearned"/>; a proposal the sanity gates
    /// demoted passes <see cref="UnverifiedAliasSource"/> instead, so it is reviewable rather
    /// than authoritative. Source and IsVerified are set together and deliberately belt-and-
    /// braces: the resolver's S3 tier checks BOTH, so either one alone already stops an
    /// auto-link.
    /// </param>
    private sealed record Proposal(
        CustomerIdentifierType Type,
        string NormalizedValue,
        string DisplayValue,
        bool IsVerified,
        decimal Confidence,
        string Source = CustomerIdentifierSources.LeadReviewLearned);
}
