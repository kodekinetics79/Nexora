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
/// OWNER DECISION 2026-09-13, POLICY A ("learn slowly, never guess"). People's confirmations earn trust for one thing
/// only: a buyer's exact address, confirmed for one customer and never for anyone else. Every rule that let decisions
/// vouch for more is deleted, not patched: a domain tied by earlier decisions, a contact or the domain's own spelling;
/// a printed-address voucher; a demoted row recovering itself; a pick set aside as "against its own page"; a
/// "withdrawn" decision; an earlier decision vouching for a portal pair. Every repair round found a new wrong 0.95 link
/// inside them. The cost the owner accepted: a new buyer is recognised by email from their third message, not their
/// second.
///
/// Poisoning safeguards, all enforced here:
///   P1 self-identity  — never learn the tenant's own name / domains / the document's vendor block.
///                       "Our domains" are the intake mailboxes AND the tenant's active users
///                       (<see cref="TenantSelfIdentity"/>), any host under them, and any domain
///                       whose own name spells ours
///   P2 address        — a buyer's exact address, on a consumer provider or an organisation's own domain alike, becomes a
///                       verified Email only once people have linked it to ONE customer
///                       <see cref="CustomerResolutionPolicy.FreeMailAddressConfirmationsRequired"/> times, this decision
///                       included, whatever customer their pages name. The first decision for another customer makes the
///                       address nobody's and takes back whatever was learned for it, and the resolver and routing refuse the
///                       address from the moment that decision stands (<see cref="HumanAddressDecisions"/>). A relay or a
///                       system mailbox is never learned, and nothing at all is written before the count is reached
///   P3 exclusivity    — Email/ErpAccount/TaxRegistration are exclusive; on conflict SKIP, never steal
///   P4 multi-owner    — Alias/PortalAccount may point at several customers; the resolver then
///                       returns AMBIGUOUS, which is an outcome, not an error
///   P5 reversal       — a later review that CHANGES the customer takes back, before anything else is read, every learned
///                       row of the rejected customer that this lead taught or whose value this document carries: an
///                       address and a numbering shape are expired; a domain, a name or a portal pair is demoted to
///                       unverified with its counts kept. Nothing is promoted for the new customer by the relink itself
///   P6 approval gate  — only "approve" + an explicitly supplied customer; NEVER a machine
///                       AUTO_MATCHED result (that is the path by which one machine mistake
///                       would bootstrap itself into an authoritative alias)
///   P7 single level   — identifiers point DIRECTLY at a CustomerId; no alias -> alias
///                       indirection, no supersession chain, so cycles are structurally
///                       impossible. If customer merge/supersession is ever added, port the
///                       `visited` HashSet cycle detector from ProductIdentityResolver
///                       (Inventory/Commercial/CommercialInventoryServices.cs:36-56) FIRST.
///   P8 resemblance    — a printed name is trusted as the customer's alias only when it carries a word that names ONE
///                       company, reads as the customer, no other active customer owns it at least as closely, and the
///                       document itself names that customer, read with the names a person entered and never a taught
///                       one. Anything else is recorded for review, never trusted
///   P9 shared network — never learn a portal pair whose supplier number was issued by the
///                       NETWORK rather than by the buyer (SharedSupplierNetworks)
///   P10 no domain     — never a Domain row, verified or unverified, from any address. A customer's domain comes only from
///                       a customer contact or an admin entry (<see cref="CustomerIdentifierSources.EnteredByAPerson"/>),
///                       and the resolver and routing read no other Domain row
///   P11 portal pair   — a buyer portal's "portal|our-vendor-code" pair is trusted only when the document names the chosen
///                       customer; otherwise it is filed unverified, and a pair another customer already holds is not filed
///                       at all. No earlier decision vouches for a pair
///   P12 reinforcement — a re-confirmation counts on every matching row but promotes only a row on this class's own
///                       shelves. A row a person entered keeps its grade, its confidence and its source: a person's "not
///                       verified" is never overridden by a reviewer's click
///
/// ASSUMPTION, stated because the owner did not answer it and this is the recommendation he was given: an Arabic-only
/// company name is trusted after two confirmations for the same customer with none for another, or at once when the
/// document's header or address names that customer (<c>ArabicOnlyNameIsTheCustomersAsync</c>).
/// </summary>
/// <remarks>
/// OBSERVATIONCOUNT IS HISTORY ONLY. Reinforcement increments it, a relink's demotion or expiry leaves it alone, and no
/// rule reads it. Policy A counts the human-decided LEADS that carry a value, never a row's counter: a counter cannot say
/// who decided, whether that decision still stands, or what the page it was taken on said.
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
    /// The printed company name is written only in Arabic letters and the customer's record in none, so its letters cannot
    /// be compared, and neither the page nor enough earlier decisions corroborate it yet. It was recorded for review.
    /// ASSUMPTION: the owner's policy A decision (2026-09-13) left this open, and the recommendation he was given is what is
    /// built: an Arabic-only company name is trusted after two confirmations for the same customer with none for another,
    /// or at once when the document's header or address names that customer. See <c>ArabicOnlyNameIsTheCustomersAsync</c>.
    /// </summary>
    public const string SkipAliasInAnotherScriptNotYetCorroborated = "aliasInAnotherScriptNotYetCorroborated";

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
    /// larger share of its words), or the tenant's customer book is too large to read, so it was
    /// recorded for review instead of being trusted. See <see cref="NamesAnotherCustomerAtLeastAsClosely"/>.
    /// </summary>
    public const string SkipAliasNamesAnotherCustomer = "aliasNamesAnotherCustomer";

    /// <summary>
    /// The printed name resembles the chosen customer and no other, but the document itself does not name that customer:
    /// read as the resolver reads a page (headers and delivery addresses, the whole customer book, only the names a person
    /// entered), it links nobody or somebody else. It was recorded for review. Owner decision 2026-09-13, policy A: a company
    /// name is learned only when the document itself names that customer. An alias the page cannot show ("MARAFIQ" alone
    /// for Marafiq Power &amp; Water Utility Company) is entered on the customer's profile instead.
    /// </summary>
    public const string SkipAliasNotNamedByDocument = "aliasNotNamedByDocument";

    /// <summary>The portal issues OUR supplier number itself, so the pair names nobody.</summary>
    public const string SkipSharedSupplierNetwork = "sharedSupplierNetwork";

    /// <summary>
    /// The document does not name the chosen customer, so the portal + vendor-code pair was filed unverified. No earlier
    /// decision vouches for a pair (owner decision 2026-09-13, policy A). See P11.
    /// </summary>
    public const string SkipPortalAccountNotTiedToCustomer = "portalAccountNotTiedToCustomer";

    /// <summary>
    /// Another active customer already holds the same portal + vendor-code pair as a fact, so it
    /// was not filed against this one. See P11.
    /// </summary>
    public const string SkipPortalAccountClaimedByAnotherCustomer = "portalAccountClaimedByAnotherCustomer";

    /// <summary>A procurement portal's relay, or a system mailbox nobody reads on any host: evidence about a POSTMAN, never learned.</summary>
    public const string SkipPersonalOrRelayAddress = "personalOrRelayAddress";

    /// <summary>
    /// People have linked this exact address to more than one customer: a freight agent, an EPC contractor's buyer working
    /// for several site owners, or a wrong pick that still stands. Nothing was learned for it, and every Email row this class
    /// had learned for it, on any customer, was expired and written through. Owner decision 2026-09-13, policy A: a buyer's
    /// address is learned "never for anyone else". The remedy for a standing wrong pick is a contact on the right customer.
    /// </summary>
    public const string SkipAddressConfirmedForAnotherCustomer = "addressConfirmedForAnotherCustomer";

    /// <summary>
    /// Fewer than <see cref="CustomerResolutionPolicy.FreeMailAddressConfirmationsRequired"/> human decisions for this
    /// customer carry the address, this one included. Nothing was written, not even a suggestion: an Email row of any grade
    /// blocks the address for every other customer, and no approved command removes one.
    /// </summary>
    public const string SkipAddressNotYetConfirmed = "addressNotYetConfirmed";

    /// <summary>
    /// A relink took the trust off a domain, a company name or a portal pair of the previous customer: the row was demoted to
    /// unverified, its counts and confidence kept, rather than expired. The skip reasons reach the review's correction metric,
    /// which is the only record a learned row's change has (the row carries no reason column). See P5.
    /// </summary>
    public const string SkipContradictedFactDemoted = "contradictedFactDemoted";

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
    /// How many of the tenant's customers, and how many names a person entered on them, a page reading and the alias gate
    /// read. A review is human-paced and the read is names only, so it is set well above any realistic customer book. A
    /// tenant past it cannot be checked: a name or a portal pair that cannot be checked is recorded for review, and an
    /// address is not learned.
    /// </summary>
    public const int MaximumCustomersCompared = 20_000;

    /// <summary>
    /// Rows read by the portal-pair check here, and by the resolution service's own identity-evidence reads. Never a cap on
    /// the decisions that veto an address or an Arabic-only name: a capped veto is a veto a flood of newer decisions hides.
    /// </summary>
    public const int MaximumDomainEvidenceRead = 200;

    /// <summary>
    /// Earlier decisions for the chosen customer read when counting the confirmations of an address, and earlier documents read
    /// in full, and again as pages, when counting those of an Arabic-only company name.
    /// </summary>
    public const int MaximumEarlierPrintsRead = 20;

    /// <summary>
    /// Human decisions that carry a printed company name, read newest first to find every earlier pick of an Arabic-only name
    /// by its name key (<see cref="CustomerNameNormalizer.LooseKey"/> cannot run in SQL). A tenant past it cannot be checked,
    /// and the name is recorded for review. A human-paced read, on the one rare branch of a review that asks it.
    /// </summary>
    public const int MaximumPrintedNameDecisionsRead = 20_000;

    /// <summary>Lines read per earlier document for its storage locations and labelled columns.</summary>
    private const int MaximumItemsReadPerEarlierDocument = 300;

    /// <summary>
    /// The confidence every row this class files on the unverified shelf, and every numbering shape, is written at. A relink's
    /// demotion keeps a row's own confidence, so a demoted row can sit above it.
    /// </summary>
    private const decimal UnverifiedFilingConfidence = 0.50m;

    /// <summary>
    /// Exactly the statuses <see cref="LeadCustomerMatchStatuses.IsHumanDecided"/> accepts, so a read of
    /// earlier decisions can filter in SQL BEFORE it is capped. Filtered after the cap, a flood of newer
    /// machine links pushed every human decision out of the window. The resolution service's list, not a
    /// copy of it: two lists would drift apart the day one of them changed. A test pins it to the model.
    /// </summary>
    internal static string[] HumanDecidedStatuses => LeadCustomerResolutionService.HumanDecidedStatuses;

    /// <summary>Reads a page for names only; enough candidates that no named customer is dropped from the answer.</summary>
    private static readonly CustomerResolutionPolicy DocumentReadingPolicy = new() { MaximumCandidates = 50 };

    /// <summary>A word must be at least this long before one wrong letter is read as a typing slip rather than another word.</summary>
    private const int MinimumSlipWordLength = 6;

    /// <summary>The rows this class writes. Only these are taken back by a relink (P5) or promoted by a re-confirmation (P12).</summary>
    private static readonly string[] LearnedSources =
        [CustomerIdentifierSources.LeadReviewLearned, CustomerIdentifierSources.LeadReviewUnverified];

    private readonly ErpRfqAutomationContext _db;
    private readonly ILogger<CustomerAliasLearner>? _log;
    private readonly CustomerResolutionPolicy _policy;

    /// <param name="policy">
    /// The tenant-wide identity policy the host registers. The learner reads
    /// <see cref="CustomerResolutionPolicy.FreeMailAddressConfirmationsRequired"/> from it: that number lived
    /// here as a constant while the policy carried a setting of the same name nothing read, so retuning the
    /// policy changed nothing.
    /// </param>
    public CustomerAliasLearner(
        ErpRfqAutomationContext db, ILogger<CustomerAliasLearner>? log = null, CustomerResolutionPolicy? policy = null)
    {
        _db = db;
        _log = log;
        _policy = policy ?? new CustomerResolutionPolicy();
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
        // sender or a relay to the wrong client is exactly what a correction must take back, even
        // though nothing about that sender would be learned today.
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

        // P5: the reviewer moved this lead to a different client. What that contradicts is taken back and written
        // through before anything below reads the store.
        var expiredIds = new HashSet<long>();
        // Every row this review took away from a customer, expired or demoted. None of them may still claim a portal
        // pair below.
        var setAside = new HashSet<long>();
        if (previousCustomerId.HasValue && previousCustomerId.Value != customerId
            && await TakeBackWhatARelinkContradictsAsync(
                businessUnitId, lead.Id, previousCustomerId.Value, addresses, aliasKey, portalPair?.Key,
                expiredIds, setAside, now, ct) > 0)
            skips.Add(SkipContradictedFactDemoted);

        var (selfNameKeys, domainSelfNames) = await LoadTenantSelfNamesAsync(businessUnitId, lead, ct);
        var selfDomains = await TenantSelfIdentity.LoadSelfDomainsAsync(_db, businessUnitId, ct);
        // The name of the customer the reviewer actually picked. Without it there is nothing to
        // compare the printed name against, and "learn whatever is printed" is exactly the bug
        // (P8). Tenant-scoped on purpose: a customer id from another tenant reads back as no
        // name at all, and a nameless customer demotes rather than trusts.
        var customerName = await _db.Customers.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.Id == customerId && c.Buid == businessUnitId)
            .Select(c => c.Name)
            .FirstOrDefaultAsync(ct);

        // Every rule below that asks what a page says asks it here: the tenant's whole customer book with the names a
        // person entered, loaded at most once for this review and only when a rule needs it, and each page read once.
        var pages = new PageReader(this, businessUnitId, selfNameKeys);

        var proposals = new List<Proposal>();

        // 1. Real sender + the buyer address printed on the document. For a folder-ingested
        //    or scanned bid the printed buyer address is the ONLY place the buying
        //    organisation's real address appears, so both are weighed identically.
        foreach (var address in addresses)
        {
            var domain = RoutingValueNormalizer.DomainFromEmail(address);
            if (SyntheticIdentityGuard.IsSyntheticDomain(domain)) { skips.Add(SkipSynthetic); continue; }

            // P1. The mailbox list alone missed every colleague who forwards from a staff domain
            // that is not an intake mailbox: rfq@alquraishi.com is the mailbox, the salesman is
            // ahmed@alquraishi.com.sa, and one confirmed forward taught that domain as SEC's.
            // Active users count (TenantSelfIdentity), and so does a domain whose own name spells
            // ours, which covers the colleague who has no Nexora login at all. One predicate, shared
            // with the resolver and routing. The names that make a DOMAIN ours are the tenant's
            // configured names, and the vendor block only where it spells one of them
            // (TenantSelfIdentity.DomainSelfNames): a Marafiq print whose vendor field was misread as
            // "MARAFIQ" must not make marafiq.com.sa ours.
            if (TenantSelfIdentity.IsOurs(domain, selfDomains, domainSelfNames))
            {
                skips.Add(SkipSelfIdentity);
                continue;
            }

            // A RELAY'S ADDRESS NAMES NOBODY, however often it is confirmed: noreply@ariba.com carries
            // every Ariba buyer's RFQ, and learning it pins every later Ariba RFQ to whichever buyer was
            // confirmed first.
            if (SyntheticIdentityGuard.IsPortalRelayDomain(domain))
            {
                skips.Add(SkipPersonalOrRelayAddress);
                continue;
            }
            // A SYSTEM MAILBOX IS NEVER MINTED, on any host. no-reply@ or ordersender@ on a host nobody listed as a
            // relay carries every buyer's mail on that system, and the resolver and routing refuse such a row unless a
            // person entered it (IdentityDomainGuard.MayMatchExactAddress).
            if (IdentityDomainGuard.IsSystemMailbox(address))
            {
                skips.Add(SkipPersonalOrRelayAddress);
                continue;
            }
            if (domain is null || !domain.Contains('.') || !domain.Any(char.IsLetter))
            {
                skips.Add(SkipSynthetic);
                continue;
            }

            // P2, OWNER DECISION 2026-09-13, POLICY A: A BUYER'S EXACT ADDRESS IS LEARNED ONCE REPS CONFIRM IT FOR THE
            // SAME CUSTOMER TWICE, AND NEVER FOR ANYONE ELSE. An Email identifier is the strongest thing this engine can
            // write: verified, 1.00, exclusive to one customer for the whole tenant. On the live tenant "Saudi Aramco"
            // came to own personal addresses at live.com and bidnet.com from ONE confirmation each. A gmail sole trader
            // and an organisation's own mailbox go through the same rule, FreeMailAddressConfirmationsRequired decisions
            // for one customer, whatever customer their pages name; nothing is filed before that (SkipAddressNotYetConfirmed).
            //
            // P10, THE SAME DECISION: A WHOLE EMAIL DOMAIN IS NEVER LEARNED FROM CONFIRMATIONS. Hyundai E&C buying for a
            // Ras Tanura job prints buyer@hdec.com; minting hdec.com as Aramco's Domain linked every later Hyundai enquiry
            // to Aramco at S2 before its page was read, and every rule that tried to tell a buyer's domain from an
            // intermediary's by the decisions people took on it found a new wrong 0.95 link. A domain comes only from a
            // customer contact or an admin entry.
            switch (await ReadAddressDecisionsAsync(businessUnitId, lead, customerId, address, expiredIds, setAside, now, ct))
            {
                case AddressReading.Confirmed:
                    proposals.Add(new Proposal(CustomerIdentifierType.Email, address, address, true, 1.00m));
                    break;
                case AddressReading.ConfirmedForAnotherCustomer:
                    skips.Add(SkipAddressConfirmedForAnotherCustomer);
                    break;
                default:
                    skips.Add(SkipAddressNotYetConfirmed);
                    break;
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
            else if (IsArabicOnlyNameOfANonArabicRecord(companyDisplay, customerName))
            {
                // AN ARABIC-ONLY NAME CANNOT BE COMPARED, WHICH IS NOT THE SAME AS UNLIKE (LF08). "الشركة السعودية للكهرباء"
                // is Saudi Electricity Company's own name in Arabic; the resemblance tiers compare letters, so it read as
                // unlike SEC however many people confirmed it. ASSUMPTION (owner decision 2026-09-13 left it open; this is
                // the recommendation he was given): trusted after two confirmations for this customer with none for
                // another, or at once when the page's header or address names this customer. Only this direction: a
                // Latin print of a customer recorded in Arabic is compared like any other print below.
                if (await ArabicOnlyNameIsTheCustomersAsync(businessUnitId, lead, customerId, companyDisplay, aliasKey, pages, ct))
                    proposals.Add(new Proposal(CustomerIdentifierType.Alias, aliasKey, companyDisplay, true, 0.90m));
                else
                    FileAlias(SkipAliasInAnotherScriptNotYetCorroborated, "it is written only in Arabic and nothing yet corroborates it");
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
                FileAlias(SkipAliasUnlikeCustomer, "it does not resemble the customer's own name");
            }
            else
            {
                // Resembling the chosen customer is not enough when another customer is at least
                // as good a reading of the same words. "SCC" is Saudi Cable's initials AND Saudi
                // Ceramics'; the resolver refuses to derive initials two customers share, but a
                // taught alias is never suppressed, so confirming "SCC" once for Saudi Cable made
                // every Saudi Ceramics delivery address reading "SCC Riyadh Plant 2" link to Saudi
                // Cable at 0.88.
                var book = await pages.BookAsync(ct);
                if (book is null
                    || NamesAnotherCustomerAtLeastAsClosely(
                        companyDisplay, customerName,
                        book.Customers.Where(customer => customer.CustomerId != customerId).Select(customer => (string?)customer.Name)))
                {
                    FileAlias(SkipAliasNamesAnotherCustomer, "another customer owns that name at least as closely, or the customer book is too large to read");
                }
                // OWNER DECISION 2026-09-13, POLICY A: A COMPANY NAME IS LEARNED ONLY WHEN THE DOCUMENT ITSELF NAMES THAT
                // CUSTOMER. A resembling print used to be trusted on one confirmation, so "ARAMCO" printed on a job whose
                // delivery address says Saudi Electricity Company, picked as Aramco, became Aramco's alias at 0.90. The
                // page is read with the names a person entered and never a taught alias, so nothing a reviewer taught
                // can vouch for what it taught.
                else if (!pages.DocumentNamesChosenCustomer(lead, customerId, book))
                {
                    FileAlias(SkipAliasNotNamedByDocument, "the document does not name that customer");
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
                // P11. The pair was written verified at 0.92 from one confirmation with no look at the document, and the
                // learned-portal tier decides before the passage tier, so one mis-click on an SEC e-bidding print linked
                // every later SEC print to Saudi Aramco at 0.92 and lead 680's delivery address never got a say. Earlier
                // decisions were then allowed to vouch for a pair, and a wrong pick repeated on pair-only prints promoted
                // it all the same. Owner decision 2026-09-13, policy A: a portal account is learned only when THIS document
                // names the customer picked; SEC's own prints name SEC in the delivery address.
                if (await PortalPairHeldByAnotherCustomerAsync(businessUnitId, customerId, pair.Key, setAside, ct))
                {
                    skips.Add(SkipPortalAccountClaimedByAnotherCustomer);
                }
                else if (pages.DocumentNamesChosenCustomer(lead, customerId, await pages.BookAsync(ct)))
                {
                    proposals.Add(new Proposal(CustomerIdentifierType.PortalAccount, pair.Key, pair.Display, true, 0.92m));
                }
                else
                {
                    skips.Add(SkipPortalAccountNotTiedToCustomer);
                    proposals.Add(new Proposal(
                        CustomerIdentifierType.PortalAccount, pair.Key, pair.Display, false, UnverifiedFilingConfidence, UnverifiedAliasSource));
                    _log?.LogInformation(
                        "Client alias learning filed portal pair {Pair} for customer {CustomerId} as unverified on lead {LeadId}: the document does not name that customer.",
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
                CustomerIdentifierType.RfqNumberPattern, pattern, lead.Rfqno!.Trim(), false, UnverifiedFilingConfidence));

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
                // P12. A human confirmed it again: the counter moves on the matching row, whoever wrote it.
                current.ObservationCount += 1;
                current.LastObservedOn = now;
                reinforced++;
                // Only a row on this class's own shelves is promoted, and only by a proposal the rules above trusted; an
                // RFQ-number SHAPE stays unverified by design. The shelf moves with the flag, because the resolver checks
                // Source as well as IsVerified. A row a person entered (or a backfilled one) keeps the grade, confidence
                // and source it has: a person who marked a detail "not verified" is never overridden by a reviewer's click.
                if (proposal.IsVerified
                    && proposal.Type != CustomerIdentifierType.RfqNumberPattern
                    && LearnedSources.Contains(current.Source, StringComparer.Ordinal))
                {
                    current.IsVerified = true;
                    if (current.Confidence < proposal.Confidence) current.Confidence = proposal.Confidence;
                    current.Source = CustomerIdentifierSources.LeadReviewLearned;
                }
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

        // A printed company name recorded for review: the unverified shelf, never trusted, with the reason it was not.
        void FileAlias(string reason, string why)
        {
            skips.Add(reason);
            proposals.Add(new Proposal(
                CustomerIdentifierType.Alias, aliasKey, companyDisplay!, false, UnverifiedFilingConfidence, UnverifiedAliasSource));
            _log?.LogInformation(
                "Client alias learning recorded \"{Alias}\" for customer {CustomerId} as unverified on lead {LeadId}: {Why}.",
                companyDisplay, customerId, lead.Id, why);
        }
    }

    /// <summary>
    /// P5. Takes back what the reviewer's correction contradicts and writes it through at once.
    ///
    /// It used to reach only rows whose LearnedFromLeadId was THIS lead. A reinforced row keeps
    /// the id of the first lead that taught it, and that lead can no longer change client once it
    /// has an RFQ. So when lead A (an SEC print from 57322@se.com.sa) was linked to Aramco and
    /// converted, lead B from the same sender auto-linked to Aramco at 1.00, and the rep relinked
    /// B to SEC, nothing was taken back and lead C from the same sender still linked to Aramco.
    ///
    /// The rows: every active row of the rejected customer on this class's own shelves (LeadReviewLearned,
    /// LeadReviewUnverified) that this lead taught, or whose value this document carries: its raw addresses
    /// (Email), their domains (Domain), the printed company name (Alias) or the portal pair (PortalAccount).
    /// Raw means before any gate, so a legacy row on a relay or a consumer mailbox is reached too. A row a
    /// person typed on the rejected customer's profile is never touched: the reviewer contradicted the
    /// machine, not the profile, and P3 still refuses to take it.
    ///
    /// OWNER DECISION 2026-09-13, POLICY A:
    ///   * An Email and an RFQ-number shape are EXPIRED. An Email row of any grade blocks its address for every
    ///     other customer (UX_customer_identifiers_authoritative and EnsureAuthoritativeValuesAvailableAsync ignore
    ///     IsVerified and Source), so a demoted address would stop the right customer's contact being saved. A shape
    ///     is already unverified, so demoting it would change nothing and would keep the numbering suggestion alive,
    ///     which is the shape of the 55% incident.
    ///   * A Domain, an alias and a portal pair are DEMOTED: IsVerified false and the unverified shelf, with the
    ///     row's ObservationCount, Confidence, LastObservedOn and LearnedFromLeadId kept. One mis-click relink must
    ///     not wipe what fifty confirmations built; the next confirmation for its own customer re-verifies an alias
    ///     or a pair only where that page names that customer (P8, P11, P12). A demoted Domain stays unverified for
    ///     ever, because this class never proposes a Domain (P10).
    ///   * Nothing is promoted for the new customer here: it gets only what the ordinary rules give this lead.
    ///
    /// WHY IT IS WRITTEN THROUGH: EnsureAuthoritativeValuesAvailableAsync and this class's own reads query the
    /// database without the change tracker, so a staged expiry is invisible to them and the correction was refused
    /// against the very row it had just expired. Flushing with SaveChanges would also flush whatever else the caller
    /// has staged, so only these rows are updated, by id. The entities stay Modified with the same values, so the
    /// caller's SaveChanges agrees with the database and a failure path that detaches changed entries leaves nothing
    /// stale behind.
    /// </summary>
    /// <returns>How many demoted rows were trusted before the relink (verified, or on the LeadReviewLearned shelf).</returns>
    private async Task<int> TakeBackWhatARelinkContradictsAsync(
        long businessUnitId,
        long leadId,
        long previousCustomerId,
        IReadOnlyCollection<string> addresses,
        string aliasKey,
        string? portalKey,
        HashSet<long> expiredIds,
        HashSet<long> setAside,
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
                        && i.EffectiveTo == null
                        && LearnedSources.Contains(i.Source))
            .Where(i => i.LearnedFromLeadId == leadId
                        || (i.IdentifierType == CustomerIdentifierType.Email && emails.Contains(i.NormalizedValue))
                        || (i.IdentifierType == CustomerIdentifierType.Domain && domains.Contains(i.NormalizedValue))
                        || (i.IdentifierType == CustomerIdentifierType.Alias && aliases.Contains(i.NormalizedValue))
                        || (i.IdentifierType == CustomerIdentifierType.PortalAccount && portals.Contains(i.NormalizedValue)))
            .ToListAsync(ct);
        if (contradicted.Count == 0) return 0;

        var expired = new List<long>();
        var demoted = new List<long>();
        var trustTaken = 0;
        foreach (var identifier in contradicted)
        {
            setAside.Add(identifier.Id);
            if (identifier.IdentifierType is CustomerIdentifierType.Email or CustomerIdentifierType.RfqNumberPattern)
            {
                identifier.EffectiveTo = now;
                expired.Add(identifier.Id);
                expiredIds.Add(identifier.Id);
                continue;
            }
            if (identifier.IsVerified
                || string.Equals(identifier.Source, CustomerIdentifierSources.LeadReviewLearned, StringComparison.Ordinal))
                trustTaken++;
            identifier.IsVerified = false;
            identifier.Source = UnverifiedAliasSource;
            demoted.Add(identifier.Id);
        }

        if (expired.Count > 0)
        {
            var ids = expired.ToArray();
            await _db.Set<CustomerIdentifier>().IgnoreQueryFilters()
                .Where(i => i.BusinessUnitId == businessUnitId && ids.Contains(i.Id))
                .ExecuteUpdateAsync(set => set.SetProperty(i => i.EffectiveTo, (DateTime?)now), ct);
        }
        if (demoted.Count > 0)
        {
            var ids = demoted.ToArray();
            await _db.Set<CustomerIdentifier>().IgnoreQueryFilters()
                .Where(i => i.BusinessUnitId == businessUnitId && ids.Contains(i.Id))
                .ExecuteUpdateAsync(set => set
                    .SetProperty(i => i.IsVerified, false)
                    .SetProperty(i => i.Source, UnverifiedAliasSource), ct);
            _log?.LogInformation(
                "Client alias learning demoted identifiers {IdentifierIds} of customer {CustomerId} to unverified on lead {LeadId}: a relink contradicts them.",
                string.Join(",", ids), previousCustomerId, leadId);
        }
        return trustTaken;
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
    ///
    /// Used ONLY for the tenant's OWN names
    /// (<see cref="TenantSelfIdentity.IsOurs(string?, IEnumerable{string}?, IEnumerable{string?}?)"/>), where the rule
    /// deliberately errs towards "ours": a colleague with no Nexora login on a domain that spells one of our distinctive
    /// words is us, and the error costs a suggestion. Nothing asks whether a domain spells a CUSTOMER any more: since the
    /// owner's policy A decision (2026-09-13) a customer's domain comes only from a contact or an admin entry.
    /// </summary>
    public static bool DomainLabelSpellsName(string? domainOrAddress, string? name)
        => LabelSpellsName(domainOrAddress, name);

    private static bool LabelSpellsName(string? domainOrAddress, string? name)
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
            {
                if (!string.Equals(spelling, token, StringComparison.Ordinal)) continue;
                if (token.Length < 4 && !string.Equals(nameKey, token, StringComparison.Ordinal)) continue;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Words that name a trade, a utility or a place rather than one company. The resolver reads this one list so that
    /// such a word alone is not taken for a company: a one-word name that is a place, or a mailbox signed with a trade.
    /// A word missing from this list keeps the reading it always had, so the list can only make a reading more careful.
    /// Nothing in this class reads it since the owner's policy A decision (2026-09-13) deleted the domain-spelling tie.
    /// </summary>
    internal static readonly HashSet<string> SectorAndPlaceWords = new(StringComparer.Ordinal)
    {
        // trades and utilities
        "PIPE", "PIPES", "PIPING", "PIPELINE", "PIPELINES", "TUBE", "TUBES", "WATER", "WATERS", "POWER",
        "ENERGY", "ELECTRIC", "ELECTRICAL", "ELECTRICITY", "ELECTRONICS", "UTILITY", "UTILITIES", "GRID",
        "SOLAR", "WIND", "NUCLEAR", "OIL", "OILS", "OILFIELD", "GAS", "GASES", "PETROLEUM", "PETROCHEMICAL",
        "PETROCHEMICALS", "CHEMICAL", "CHEMICALS", "REFINING", "REFINERY", "DRILLING", "OFFSHORE", "ONSHORE",
        "STEEL", "STEELS", "METAL", "METALS", "IRON", "ALUMINIUM", "ALUMINUM", "COPPER", "CABLE", "CABLES",
        "WIRE", "WIRES", "CEMENT", "CERAMIC", "CERAMICS", "GLASS", "PLASTIC", "PLASTICS", "POLYMERS", "RUBBER",
        "PAPER", "TEXTILE", "TEXTILES", "FERTILIZER", "FERTILIZERS", "MINING", "MINES", "MINERALS",
        "DESALINATION", "VALVE", "VALVES", "PUMP", "PUMPS", "EQUIPMENT", "MACHINERY", "TOOLS", "TANKS",
        "BANK", "BANKS", "BANKING", "FINANCE", "FINANCIAL", "INSURANCE", "CAPITAL", "INVESTMENT", "INVESTMENTS",
        "TELECOM", "TELECOMS", "TELECOMMUNICATION", "TELECOMMUNICATIONS", "COMMUNICATIONS", "NETWORK", "NETWORKS",
        "CONSTRUCTION", "CONTRACTING", "CONTRACTORS", "ENGINEERING", "ENGINEERS", "CONSULTANTS", "CONSULTING",
        "SERVICE", "SERVICES", "SUPPLY", "SUPPLIES", "SUPPLIER", "SUPPLIERS", "INDUSTRY", "INDUSTRIES",
        "INDUSTRIAL", "MANUFACTURING", "FACTORY", "FACTORIES", "TECHNOLOGY", "TECHNOLOGIES", "TECH", "SOLUTIONS",
        "SYSTEM", "SYSTEMS", "AUTOMATION", "CONTROLS", "INSTRUMENTS", "INSTRUMENTATION", "LIGHTING", "SAFETY",
        "SECURITY", "FIRE", "PAINTS", "COATINGS", "FASTENERS", "BEARINGS", "LOGISTICS", "SHIPPING", "TRANSPORT",
        "TRANSPORTATION", "MARINE", "AVIATION", "AIRLINES", "RAILWAY", "RAILWAYS", "PORTS", "STORAGE",
        "WAREHOUSE", "HEALTH", "HEALTHCARE", "MEDICAL", "HOSPITAL", "PHARMA", "PHARMACEUTICAL",
        "PHARMACEUTICALS", "FOOD", "FOODS", "AGRICULTURE", "AGRICULTURAL", "MARKET", "MARKETS", "MARKETING",
        "TRADERS", "PROJECTS", "DEVELOPMENT", "DEVELOPMENTS", "PROPERTIES", "HOUSING", "ENVIRONMENT",
        "ENVIRONMENTAL", "AUTHORITY", "MINISTRY", "GOVERNMENT", "MUNICIPALITY", "COUNCIL", "UNIVERSITY", "COLLEGE",
        // places
        "RIYADH", "JEDDAH", "JIDDAH", "MAKKAH", "MECCA", "MADINAH", "MEDINA", "DAMMAM", "KHOBAR", "ALKHOBAR",
        "DHAHRAN", "JUBAIL", "YANBU", "TABUK", "ABHA", "JIZAN", "JAZAN", "GIZAN", "NAJRAN", "QASSIM", "BURAIDAH",
        "BURAYDAH", "TAIF", "HOFUF", "AHSA", "HASA", "QATIF", "RABIGH", "TANURA", "RASTANURA", "ABQAIQ", "KHAFJI",
        "SAKAKA", "JOUF", "KHARJ", "SUDAIR", "NEOM", "DUBAI", "ABUDHABI", "SHARJAH", "AJMAN", "FUJAIRAH", "DOHA",
        "QATAR", "KUWAIT", "BAHRAIN", "MANAMA", "OMAN", "MUSCAT", "SOHAR", "CAIRO", "EGYPT", "JORDAN", "AMMAN",
        "YEMEN", "IRAQ", "BASRA"
    };

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
        /// <summary>
        /// Shapes already worked out, by the exact name. A shape is a pure function of the name, and the resolver asks
        /// for every customer's shape on every lead that carries a taught name or a signature (NameReadings).
        /// </summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, NameShape> Remembered = new(StringComparer.Ordinal);

        private const int MaximumRemembered = 200_000;

        public static NameShape Of(string? name)
        {
            if (name is null) return Compute(null);
            if (Remembered.TryGetValue(name, out var known)) return known;
            var shape = Compute(name);
            if (Remembered.Count >= MaximumRemembered) Remembered.Clear();
            Remembered.TryAdd(name, shape);
            return shape;
        }

        private static NameShape Compute(string? name) => new(
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
    /// The tenant's customer book as a page is read: every active customer with a name, and the verified Alias and
    /// CustomerName rows a PERSON entered (<see cref="CustomerIdentifierSources.EnteredByAPerson"/>) on an active customer.
    /// No contacts and no earlier senders: a page is read for names only. Never a learned row, so nothing a reviewer
    /// taught vouches for what the next page says (owner decision 2026-09-13, policy A). Null when either list passes
    /// <see cref="MaximumCustomersCompared"/>, which answers "cannot tell".
    /// </summary>
    private async Task<ClientResolutionCorpus?> LoadDocumentReadingBookAsync(long businessUnitId, CancellationToken ct)
    {
        var customers = await _db.Customers.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.Buid == businessUnitId && c.IsActive != false && c.Name != null)
            .OrderBy(c => c.Id)
            .Select(c => new { c.Id, c.Name })
            .Take(MaximumCustomersCompared + 1)
            .ToListAsync(ct);
        if (customers.Count > MaximumCustomersCompared) return null;

        // EF cannot translate a method call, so the SQL filter uses the one array IsEnteredByAPerson reads.
        var enteredByAPerson = CustomerIdentifierSources.EnteredByAPerson;
        var names = await _db.Set<CustomerIdentifier>().AsNoTracking().IgnoreQueryFilters()
            .Where(i => i.BusinessUnitId == businessUnitId
                        && i.EffectiveTo == null
                        && i.IsVerified
                        && (i.IdentifierType == CustomerIdentifierType.Alias || i.IdentifierType == CustomerIdentifierType.CustomerName)
                        && enteredByAPerson.Contains(i.Source))
            .Where(i => _db.Customers.Any(c => c.Buid == businessUnitId && c.Id == i.CustomerId && c.IsActive != false))
            .OrderBy(i => i.Id)
            .Take(MaximumCustomersCompared + 1)
            .Select(i => new { i.Id, i.CustomerId, i.IdentifierType, i.NormalizedValue, i.IsVerified, i.Confidence, i.Source })
            .ToListAsync(ct);
        if (names.Count > MaximumCustomersCompared) return null;

        return new ClientResolutionCorpus
        {
            Customers = customers.Select(row => new CustomerNameSnapshot(row.Id, row.Name!)).ToList(),
            Identifiers = names
                .Select(row => new CustomerIdentifierSnapshot(
                    row.Id, row.CustomerId, row.IdentifierType, row.NormalizedValue, row.IsVerified, row.Confidence, row.Source))
                .ToList()
        };
    }

    /// <summary>
    /// Reads pages for ONE review. "The document names the customer" means what the resolver, reading only the page's
    /// headers and delivery addresses against the whole customer book and the names a person entered, links at the
    /// auto-link line or above: every rule a passage obeys applies (one word in an address is only offered, a name that
    /// runs on into a longer company name is only offered, shared initials name nobody, bracketed site codes, contractor
    /// and consignee demotions). Item text never counts: "SEC-specified barcode" is a specification, not a buyer.
    ///
    /// The page is on trial, not the mailbox: no sender, printed buyer address, account, registration, portal, supplier
    /// account, RFQ number, buyer person, sender organisation or self domain is given to the resolver. The book is loaded
    /// at most once and only when a rule asks, and each page is read once.
    /// </summary>
    private sealed class PageReader(CustomerAliasLearner learner, long businessUnitId, IReadOnlyCollection<string> selfNameKeys)
    {
        private bool _bookLoaded;
        private ClientResolutionCorpus? _book;
        private readonly Dictionary<long, long?> _pageLinks = new();
        private readonly Dictionary<long, bool> _namesAnother = new();

        /// <summary>The customer book (<see cref="LoadDocumentReadingBookAsync"/>); null when it is too large to read.</summary>
        public async Task<ClientResolutionCorpus?> BookAsync(CancellationToken ct)
        {
            if (!_bookLoaded)
            {
                _book = await learner.LoadDocumentReadingBookAsync(businessUnitId, ct);
                _bookLoaded = true;
            }
            return _book;
        }

        /// <summary>The customer the resolver links this page to, or null when it links nobody.</summary>
        public long? ReadPage(Lead document, ClientResolutionCorpus book)
        {
            if (_pageLinks.TryGetValue(document.Id, out var known)) return known;
            var outcome = CustomerIdentityResolver.Resolve(
                new LeadClientEvidence
                {
                    BusinessUnitId = document.BusinessUnitId,
                    LeadId = document.Id,
                    CustomerCompanyName = document.CustomerCompanyNameExtracted,
                    SupplierNameOnDocument = document.SupplierNameOnDocument,
                    TenantSelfNameKeys = selfNameKeys.ToList(),
                    Passages = Statements(document)
                },
                book,
                DocumentReadingPolicy,
                tenantSharedAcronyms: null);
            _pageLinks[document.Id] = outcome.CustomerId;
            return outcome.CustomerId;
        }

        /// <summary>The page links the chosen customer. False when the book cannot be read.</summary>
        public bool DocumentNamesChosenCustomer(Lead document, long customerId, ClientResolutionCorpus? book)
            => book is not null && ReadPage(document, book) == customerId;

        /// <summary>
        /// Whether the page names any active customer other than the one picked, even one it only offers: a candidate for
        /// somebody else found by the name scan (NAME_IN_DOCUMENT) or by a name a person entered (LEARNED_ALIAS).
        /// </summary>
        public bool DocumentNamesAnotherCustomer(Lead document, long customerId, ClientResolutionCorpus book)
        {
            if (_namesAnother.TryGetValue(document.Id, out var known)) return known;
            var statements = Statements(document);
            var names = false;
            if (statements.Count > 0)
            {
                var outcome = CustomerIdentityResolver.Resolve(
                    new LeadClientEvidence
                    {
                        BusinessUnitId = document.BusinessUnitId,
                        LeadId = document.Id,
                        SupplierNameOnDocument = document.SupplierNameOnDocument,
                        TenantSelfNameKeys = selfNameKeys.ToList(),
                        Passages = statements
                    },
                    book,
                    DocumentReadingPolicy,
                    tenantSharedAcronyms: null);
                names = outcome.Candidates.Any(candidate =>
                    candidate.CustomerId != customerId
                    && (string.Equals(candidate.ReasonCode, CustomerMatchReasonCodes.NameInDocument, StringComparison.Ordinal)
                        || string.Equals(candidate.ReasonCode, CustomerMatchReasonCodes.LearnedAlias, StringComparison.Ordinal)));
            }
            _namesAnother[document.Id] = names;
            return names;
        }

        private static List<DocumentPassage> Statements(Lead document)
            => LeadCustomerResolutionService.Passages(document)
                .Where(passage => passage.Role != PassageRole.ItemText)
                .ToList();
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
    /// Whether the printed company name is written only in Arabic letters while the customer's record has letters and
    /// none of them Arabic ("الشركة السعودية للكهرباء" printed for "Saudi Electricity Company"). Only this direction. The
    /// rule it replaces worked both ways, so two confirmations trusted a Latin "AL FAISAL ESTABLISHMENT" as the alias of a
    /// customer recorded as "مؤسسة الفيصل"; a Latin print, or any other pair of scripts, is now compared by the resemblance
    /// rules like any other print and recorded for review when unlike (owner decision 2026-09-13, policy A).
    /// </summary>
    private static bool IsArabicOnlyNameOfANonArabicRecord(string? printed, string? customerName)
    {
        if (string.IsNullOrWhiteSpace(printed) || string.IsNullOrWhiteSpace(customerName)) return false;
        var printedLetters = printed.Where(char.IsLetter).ToList();
        var recordLetters = customerName.Where(char.IsLetter).ToList();
        return printedLetters.Count > 0 && printedLetters.All(IsArabicLetter)
               && recordLetters.Count > 0 && !recordLetters.Any(IsArabicLetter);

        static bool IsArabicLetter(char c)
            => c is >= '\u0600' and <= '\u06FF'
                or >= '\u0750' and <= '\u077F'
                or >= '\u08A0' and <= '\u08FF'
                or >= '\uFB50' and <= '\uFDFF'
                or >= '\uFE70' and <= '\uFEFF';
    }

    /// <summary>
    /// LF08, THE STATED ASSUMPTION. The owner's policy A decision (2026-09-13) did not answer how an Arabic-only company
    /// name is trusted; this is the recommendation he was given: after two confirmations for the same customer with none
    /// for another, or at once when the document's header or address names that customer. In order:
    ///   1. the customer book cannot be read: not trusted;
    ///   2. another active customer's own record keys to the printed name or resembles it: not trusted;
    ///   3. this page names another customer (<see cref="PageReader.DocumentNamesAnotherCustomer"/>): not trusted;
    ///   4. this page names the chosen customer: trusted;
    ///   5. otherwise the earlier human decisions on prints whose company name has this name's key: none, any for another
    ///      customer, or any of those pages naming another customer is not trusted; else trusted once they and this decision
    ///      reach <see cref="CustomerResolutionPolicy.FreeMailAddressConfirmationsRequired"/>.
    /// BY THE NAME KEY, NEVER THE TEXT AS WRITTEN. Earlier picks were found by their exact text, so a pick for Aramco written
    /// with a double space, a trailing full stop or a tatweel was invisible, while LooseKey, under which the alias is stored
    /// and matched, reads them all as this name: the alias was verified on SEC and linked the Aramco pick's own spelling to SEC
    /// at 0.90 (the conformance round's C1 to C3). LooseKey cannot run in SQL, and no text filter in SQL is a safe stand-in (it
    /// also drops diacritics and folds presentation forms), so every human decision carrying a company name is read, newest
    /// first, up to <see cref="MaximumPrintedNameDecisionsRead"/>; a tenant past that cannot be checked, and nothing is trusted.
    /// </summary>
    private async Task<bool> ArabicOnlyNameIsTheCustomersAsync(
        long businessUnitId, Lead lead, long customerId, string printed, string aliasKey, PageReader pages, CancellationToken ct)
    {
        var book = await pages.BookAsync(ct);
        if (book is null) return false;
        if (book.Customers.Any(customer => customer.CustomerId != customerId
                                           && (string.Equals(CustomerNameNormalizer.LooseKey(customer.Name), aliasKey, StringComparison.Ordinal)
                                               || ResemblesCustomerName(printed, customer.Name))))
            return false;
        if (pages.DocumentNamesAnotherCustomer(lead, customerId, book)) return false;
        if (pages.DocumentNamesChosenCustomer(lead, customerId, book)) return true;

        var earlier = await _db.Leads.AsNoTracking().IgnoreQueryFilters()
            .Where(l => l.BusinessUnitId == businessUnitId
                        && l.Id != lead.Id
                        && l.CustomerId != null
                        && HumanDecidedStatuses.Contains(l.CustomerMatchStatus)
                        && l.CustomerCompanyNameExtracted != null)
            .OrderByDescending(l => l.Id)
            .Select(l => new { l.Id, CustomerId = l.CustomerId!.Value, l.CustomerCompanyNameExtracted })
            .Take(MaximumPrintedNameDecisionsRead + 1)
            .ToListAsync(ct);
        if (earlier.Count > MaximumPrintedNameDecisionsRead) return false;
        var decided = earlier
            .Where(row => string.Equals(CustomerNameNormalizer.LooseKey(row.CustomerCompanyNameExtracted), aliasKey, StringComparison.Ordinal))
            .ToList();
        if (decided.Count == 0 || decided.Any(row => row.CustomerId != customerId)) return false;

        var documents = await LoadEarlierDocumentsAsync(
            businessUnitId, decided.Select(row => row.Id).Take(MaximumEarlierPrintsRead).ToArray(), ct);
        if (documents.Any(document => pages.DocumentNamesAnotherCustomer(document, customerId, book))) return false;
        return 1 + documents.Count >= _policy.FreeMailAddressConfirmationsRequired;
    }

    /// <summary>
    /// The parts of earlier documents a page is read by: company-name field, buyer sentence, delivery address,
    /// vendor block, and each line's storage location and labelled columns. Never tracked.
    /// </summary>
    private async Task<List<Lead>> LoadEarlierDocumentsAsync(long businessUnitId, long[] leadIds, CancellationToken ct)
    {
        if (leadIds.Length == 0) return [];
        var rows = await _db.Leads.AsNoTracking().IgnoreQueryFilters()
            .Where(l => l.BusinessUnitId == businessUnitId && leadIds.Contains(l.Id))
            .Select(l => new
            {
                l.Id, l.DeliveryLocation, l.CustomerCompanyNameExtracted, l.CustomerCompanyEvidence, l.SupplierNameOnDocument,
                Lines = l.LeadItems
                    .OrderBy(item => item.Id)
                    .Take(MaximumItemsReadPerEarlierDocument)
                    .Select(item => new { item.StorageLocation, item.ExtraFields })
                    .ToList()
            })
            .ToListAsync(ct);
        return rows.Select(row => new Lead
        {
            Id = row.Id,
            BusinessUnitId = businessUnitId,
            DeliveryLocation = row.DeliveryLocation,
            CustomerCompanyNameExtracted = row.CustomerCompanyNameExtracted,
            CustomerCompanyEvidence = row.CustomerCompanyEvidence,
            SupplierNameOnDocument = row.SupplierNameOnDocument,
            LeadItems = row.Lines
                .Select(line => new LeadItem { StorageLocation = line.StorageLocation, ExtraFields = line.ExtraFields })
                .ToList()
        }).ToList();
    }

    private enum AddressReading { NotYetConfirmed, Confirmed, ConfirmedForAnotherCustomer }

    /// <summary>
    /// P2, OWNER DECISION 2026-09-13, POLICY A: "A buyer's exact email address is learned once reps confirm it for the same
    /// customer twice, and never for anyone else." What people have said about one exact address, and nothing else, read
    /// through <see cref="HumanAddressDecisions"/>: the leads whose envelope sender, sender column or printed buyer address is
    /// the address itself or ends in "&lt;address&gt;", under a human status, each counted for the customer it is linked to NOW.
    ///   E1 any earlier decision for another customer: nothing is learned, and every row this class learned for the address, on
    ///      any customer, is expired and written through. Its own EXISTS, filtered on the customer in SQL and never capped, so
    ///      no number of newer decisions, and no longer address containing this one, can hide it (the conformance round's V5b
    ///      and V5c). The remedy for a wrong pick that stands is a contact on the right customer.
    ///   E2 otherwise confirmed once this decision and the earlier decisions for this customer reach
    ///      <see cref="CustomerResolutionPolicy.FreeMailAddressConfirmationsRequired"/>, whatever customer their pages name.
    /// No longer read, because each let confirmations vouch for more than the address: contacts of this or any other customer,
    /// other customers' holdings on the domain, whether the domain spells a name, printed-versus-envelope distinctions, "against
    /// its own page" set-asides, "withdrawn" decisions and demoted rows. Nor whether this page or an earlier one names another
    /// customer: that veto was never approved, and under it a trader buying for an SEC job, or an EPC contractor delivering to
    /// an Aramco plant, was never recognised by email however often reps confirmed them, while the site owner kept being
    /// auto-linked (the regression round's ADV5.16, ADV5.17, ADV5.20). ITS COST, stated for the owner: two picks of Aramco on
    /// SEC's own prints from 57322@se.com.sa, with no decision for SEC on that address, make it Aramco's at 1.00 (ADV5.05a),
    /// until the first decision for SEC, on any screen, makes it nobody's.
    /// </summary>
    private async Task<AddressReading> ReadAddressDecisionsAsync(
        long businessUnitId, Lead lead, long customerId, string address,
        HashSet<long> expiredIds, HashSet<long> setAside, DateTime now, CancellationToken ct)
    {
        if (await HumanAddressDecisions.DecidedForAnotherCustomerAsync(_db, businessUnitId, address, customerId, lead.Id, ct))
        {
            var taught = await _db.Set<CustomerIdentifier>().IgnoreQueryFilters()
                .Where(i => i.BusinessUnitId == businessUnitId
                            && i.EffectiveTo == null
                            && i.IdentifierType == CustomerIdentifierType.Email
                            && i.NormalizedValue == address
                            && LearnedSources.Contains(i.Source))
                .ToListAsync(ct);
            if (taught.Count > 0)
            {
                foreach (var row in taught)
                {
                    row.EffectiveTo = now;
                    expiredIds.Add(row.Id);
                    setAside.Add(row.Id);
                }
                var ids = taught.Select(row => row.Id).ToArray();
                await _db.Set<CustomerIdentifier>().IgnoreQueryFilters()
                    .Where(i => i.BusinessUnitId == businessUnitId && ids.Contains(i.Id))
                    .ExecuteUpdateAsync(set => set.SetProperty(i => i.EffectiveTo, (DateTime?)now), ct);
                _log?.LogInformation(
                    "Client alias learning expired {Count} learned Email row(s) on lead {LeadId}: people have linked that address to more than one customer.",
                    ids.Length, lead.Id);
            }
            return AddressReading.ConfirmedForAnotherCustomer;
        }

        var earlier = await HumanAddressDecisions
            .Carrying(_db.Leads.AsNoTracking().IgnoreQueryFilters(), businessUnitId, address)
            .Where(l => l.Id != lead.Id && l.CustomerId == customerId)
            .OrderByDescending(l => l.Id)
            .Select(l => new
            {
                l.Clientemail, l.CustomerBuyerEmailExtracted,
                FromEmail = l.EmailIngests != null ? l.EmailIngests.FromEmail : null
            })
            .Take(MaximumEarlierPrintsRead)
            .ToListAsync(ct);
        var confirmations = 1 + earlier.Count(row =>
            IsThisAddress(row.FromEmail) || IsThisAddress(row.Clientemail) || IsThisAddress(row.CustomerBuyerEmailExtracted));
        return confirmations >= _policy.FreeMailAddressConfirmationsRequired
            ? AddressReading.Confirmed
            : AddressReading.NotYetConfirmed;

        bool IsThisAddress(string? raw)
            => string.Equals(LeadCustomerResolutionService.ParseAddress(raw), address, StringComparison.Ordinal);
    }

    private static string? ResolveSender(Lead lead)
        => !string.IsNullOrWhiteSpace(lead.EmailIngests?.FromEmail)
            ? lead.EmailIngests!.FromEmail
            : lead.Clientemail;

    /// <summary>
    /// Who "we" are, as two lists (T09a/T09c). NameKeys suppress a NAME: the tenant's configured name and the
    /// document's vendor block, which prints our name. DomainNames make a MAILBOX DOMAIN ours: the configured
    /// name, and the vendor block only where it is a spelling of it, because an extractor can misread the
    /// buyer's name into that field (<see cref="TenantSelfIdentity.DomainSelfNames"/>).
    /// </summary>
    private async Task<(HashSet<string> NameKeys, IReadOnlyList<string> DomainNames)> LoadTenantSelfNamesAsync(
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
        return (keys, TenantSelfIdentity.DomainSelfNames([businessUnitName], lead.SupplierNameOnDocument));

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
