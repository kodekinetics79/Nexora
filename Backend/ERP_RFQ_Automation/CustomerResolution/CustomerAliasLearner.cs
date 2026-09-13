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
///   P2 synthetic      — never learn Nexora's ingestion placeholders; never a portal-relay address
///                       and never a free-mail DOMAIN. A free-mail buyer's exact address becomes an
///                       Email only once people have linked it to one customer
///                       <see cref="CustomerResolutionPolicy.FreeMailAddressConfirmationsRequired"/> times and to nobody else
///   P3 exclusivity    — Email/ErpAccount/TaxRegistration are exclusive; on conflict SKIP, never steal
///   P4 multi-owner    — Alias/Domain/PortalAccount may point at several customers; the
///                       resolver then returns AMBIGUOUS, which is an outcome, not an error
///   P5 reversal       — a later review that CHANGES the customer takes back every learned row for
///                       the rejected customer that this document's own evidence carries, whichever
///                       lead first taught it, and writes that through before anything is checked
///                       against it. A fact other decisions confirmed more than once is demoted to
///                       unverified instead of expired, so one mis-click cannot wipe it
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
///                       document names the chosen customer, or when this document names no other
///                       customer and an earlier human decision on the same pair, made on a document
///                       that named no other customer either, ties it; otherwise it is filed
///                       unverified, and a pair another customer already holds is not filed at all
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
    /// The printed company name is written in another script than the customer's name, so it cannot be compared, and
    /// neither the page nor enough earlier decisions corroborate it yet. It was recorded for review. See LF08 in
    /// <c>ForeignScriptNameIsTheCustomersAsync</c>.
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
    /// A consumer-mailbox address people have linked to more than one customer: a freight agent or a
    /// personal account forwarding for several buyers. Nothing was learned, and whatever this class had
    /// learned for the address, for any customer, was expired. See P2.
    /// </summary>
    public const string SkipFreeMailAddressConfirmedForAnotherCustomer = "freeMailAddressConfirmedForAnotherCustomer";

    /// <summary>
    /// A relink contradicted a fact that other human decisions had confirmed more than once, so it was
    /// demoted to unverified rather than expired. The skip reasons reach the review's correction metric,
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
    /// How many of the tenant's other customers the alias gate reads to find one that owns the
    /// printed name more closely. A review is human-paced and the read is names only, so it is
    /// set well above any realistic customer book. A tenant past it cannot be checked, and an
    /// alias that cannot be checked is recorded for review rather than trusted.
    /// </summary>
    public const int MaximumCustomersCompared = 20_000;

    /// <summary>Contacts, identifiers and earlier leads read per mailbox domain when deciding whose domain it is.</summary>
    public const int MaximumDomainEvidenceRead = 200;

    /// <summary>Earlier documents read in full when asking whether an earlier decision on a portal pair vouches for it (P11).</summary>
    public const int MaximumPairVouchersRead = 20;

    /// <summary>Lines read per earlier document for its storage locations and labelled columns (P11).</summary>
    private const int MaximumItemsReadPerEarlierDocument = 300;

    /// <summary>
    /// The confidence every row filed on the unverified shelf is written at. A row on that shelf above it was
    /// trusted once and demoted by a relink (P5), because re-proposing an unverified value never raises it.
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

    /// <summary>The rows this class writes. Only these may be expired by a reviewer's correction (P5).</summary>
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
        // Every row this review took away from a customer, expired or demoted. None of them may still
        // claim a domain or a portal pair below.
        var setAside = new HashSet<long>();
        if (previousCustomerId.HasValue && previousCustomerId.Value != customerId
            && await ExpireContradictedAsync(
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
            // The names that make a DOMAIN ours are the tenant's configured names, and the vendor block only where
            // it spells one of them (TenantSelfIdentity.DomainSelfNames), as the resolver, the corpus loader and
            // routing ask. The raw vendor block was passed here: a Marafiq print whose vendor field was misread as
            // "MARAFIQ" made marafiq.com.sa ours to the learner alone, and a person's confirmation taught nothing.
            if (TenantSelfIdentity.IsOurs(domain, selfDomains, domainSelfNames))
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
            //
            // A RELAY'S ADDRESS NAMES NOBODY, however often it is confirmed: noreply@ariba.com carries
            // every Ariba buyer's RFQ.
            if (SyntheticIdentityGuard.IsPortalRelayDomain(domain))
            {
                skips.Add(SkipPersonalOrRelayAddress);
                continue;
            }
            // A CONSUMER MAILBOX NAMES A PERSON, and a sole trader on gmail is a buyer like any other. The
            // fix for the live.com incident refused the address outright, so a gmail buyer that reps linked
            // to the same customer again and again was only ever offered at 0.65 (the base learner had
            // written the address on the first confirmation). One confirmation still writes nothing. The
            // ADDRESS, never the domain, is learned once people have linked it to one customer
            // FreeMailAddressConfirmationsRequired times and to nobody else; the first decision for a second
            // customer marks it an agent's, and what was learned for it is taken back.
            if (SyntheticIdentityGuard.IsFreeMailDomain(domain))
            {
                var reading = await ReadAddressDecisionsAsync(
                    businessUnitId, lead.Id, customerId, customerName, address, documentNamesSomebodyElse,
                    selfNameKeys, expiredIds, setAside, now, takeBackWhatItTaught: true, ct);
                if (reading == FreeMailAddressReading.Corroborated)
                {
                    proposals.Add(new Proposal(CustomerIdentifierType.Email, address, address, true, 1.00m));
                }
                else
                {
                    skips.Add(SkipPersonalOrRelayAddress);
                    if (reading == FreeMailAddressReading.ConfirmedForAnotherCustomer)
                        skips.Add(SkipFreeMailAddressConfirmedForAnotherCustomer);
                }
                continue;
            }
            if (domain is null || !IdentityDomainGuard.IsOrganisationDomain(domain, selfDomains))
            {
                skips.Add(SkipSynthetic);
                continue;
            }

            if (!ties.TryGetValue(domain, out var tie))
                ties[domain] = tie = await TieDomainToCustomerAsync(
                    businessUnitId, lead.Id, customerId, customerName, domain, setAside,
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
            // A SYSTEM MAILBOX IS NEVER MINTED. no-reply@ or ordersender@ on a host nobody listed as a relay carries
            // every buyer's mail on that system. The resolver and routing refuse such a row unless a person entered
            // it (IdentityDomainGuard.MayMatchExactAddress), so writing one only left a row that claimed the domain
            // for this customer in every "who else writes from here" read. The domain is still weighed below.
            else if (!tie.CustomerAddresses.Contains(address) && IdentityDomainGuard.IsSystemMailbox(address))
            {
                skips.Add(SkipPersonalOrRelayAddress);
            }
            else if (tie.CustomerAddresses.Contains(address)
                     || (tie.TiedToCustomer && !tie.ClaimedByAnotherCustomer(exceptAddress: address)))
            {
                proposals.Add(new Proposal(CustomerIdentifierType.Email, address, address, true, 1.00m));
            }
            // A BUYER'S OWN MAILBOX CONFIRMED AGAIN AND AGAIN IS THAT BUYER'S ADDRESS (LG01). Only a printed buyer
            // address ties a domain, so "K Lee <k.lee@hdec.com>" confirmed for Hyundai twice, with nothing printed,
            // wrote nothing, and the third message was a 0.65 suggestion; base wrote the address at 1.00 on the first
            // confirmation. A gmail buyer confirmed twice for one customer is learned, and a corporate buyer must not
            // be worse off. The same rule as a consumer mailbox, and only for the ADDRESS: people linked it to this
            // customer FreeMailAddressConfirmationsRequired times, to nobody else, never on a page naming another
            // organisation, and nobody else holds the domain. The domain itself stays unverified until something
            // ties it (P10), so the next person on the domain is not linked by it.
            else if (!tie.ClaimedByAnotherCustomer(exceptAddress: address)
                     && await ReadAddressDecisionsAsync(
                         businessUnitId, lead.Id, customerId, customerName, address, documentNamesSomebodyElse,
                         selfNameKeys, expiredIds, setAside, now, takeBackWhatItTaught: false, ct)
                        == FreeMailAddressReading.Corroborated)
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
            else if (IsWrittenInAnotherScript(companyDisplay, customerName))
            {
                // A NAME IN ANOTHER SCRIPT CANNOT BE COMPARED, WHICH IS NOT THE SAME AS UNLIKE (LF08). "الشركة السعودية
                // للكهرباء" is Saudi Electricity Company's own name in Arabic. The resemblance tiers compare letters,
                // so it read as unlike SEC and was filed unverified however many people confirmed it, and the next
                // Arabic print resolved to nothing where base linked it at 0.90. Where the letters cannot be compared
                // the page and the people decide: trusted when this page names the customer in a header or an address
                // and names no other active customer, or once people have linked prints carrying this name to this
                // customer as often as a consumer mailbox needs, to nobody else, on no page naming another customer.
                // Another customer whose own record reads as the printed name keeps it unverified.
                if (await ForeignScriptNameIsTheCustomersAsync(businessUnitId, lead, customerId, customerName, companyDisplay, aliasKey, selfNameKeys, ct))
                {
                    proposals.Add(new Proposal(CustomerIdentifierType.Alias, aliasKey, companyDisplay, true, 0.90m));
                }
                else
                {
                    skips.Add(SkipAliasInAnotherScriptNotYetCorroborated);
                    proposals.Add(new Proposal(
                        CustomerIdentifierType.Alias, aliasKey, companyDisplay, false, 0.50m, UnverifiedAliasSource));
                    _log?.LogInformation(
                        "Client alias learning recorded \"{Alias}\" for customer {CustomerId} as unverified on lead {LeadId}: it is written in another script and nothing yet corroborates it.",
                        companyDisplay, customerId, lead.Id);
                }
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
                // An earlier decision vouches only on a document that named no other customer, and never for
                // a document that names another customer itself: a rep repeating a wrong pick on a pair-only
                // print promoted the pair to 0.92, and lead 680's address was never read again.
                if (await PortalPairHeldByAnotherCustomerAsync(businessUnitId, customerId, pair.Key, setAside, ct))
                {
                    skips.Add(SkipPortalAccountClaimedByAnotherCustomer);
                }
                else if (DocumentNamesCustomer(lead, customerId, customerName, selfNameKeys)
                         || await EarlierDecisionCarriesPairAsync(
                             businessUnitId, lead, customerId, customerName, pair.Key, selfNameKeys, ct))
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
    /// LeadReviewUnverified) and whose value this document carries is taken back, whoever taught it.
    /// A row a person typed on the rejected customer's profile is left alone: P3 still refuses to
    /// take it, because the reviewer contradicted the machine, not the profile.
    ///
    /// ONE RELINK IS NOT FIFTY CONFIRMATIONS. Expiring whatever the document carried meant a single
    /// mis-click relink wiped SEC's Email, Domain, alias and portal pair that people had confirmed fifty
    /// times. A trusted row that OTHER decisions confirmed more than once (ObservationCount above one, not
    /// taught by this lead) is therefore demoted: IsVerified false and the unverified shelf, so it leaves
    /// every auto-link tier at once and stays on the recognition table, and the next confirmation for its
    /// customer promotes it back through the ordinary shelf move. A row seen once, a row this lead taught,
    /// and a row that was never trusted are expired as before; demoting those would change nothing but
    /// keep a claim alive. Demotions are reported as <see cref="SkipContradictedFactDemoted"/>.
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
    /// <returns>How many rows were demoted rather than expired.</returns>
    private async Task<int> ExpireContradictedAsync(
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
                        && i.EffectiveTo == null)
            .Where(i => i.LearnedFromLeadId == leadId
                        || (LearnedSources.Contains(i.Source)
                            && ((i.IdentifierType == CustomerIdentifierType.Email && emails.Contains(i.NormalizedValue))
                                || (i.IdentifierType == CustomerIdentifierType.Domain && domains.Contains(i.NormalizedValue))
                                || (i.IdentifierType == CustomerIdentifierType.Alias && aliases.Contains(i.NormalizedValue))
                                || (i.IdentifierType == CustomerIdentifierType.PortalAccount && portals.Contains(i.NormalizedValue)))))
            .ToListAsync(ct);
        if (contradicted.Count == 0) return 0;

        var expired = new List<long>();
        var demoted = new List<long>();
        foreach (var identifier in contradicted)
        {
            if (ConfirmedByOtherDecisions(identifier, leadId))
            {
                identifier.IsVerified = false;
                identifier.Source = UnverifiedAliasSource;
                demoted.Add(identifier.Id);
            }
            else
            {
                identifier.EffectiveTo = now;
                expired.Add(identifier.Id);
                expiredIds.Add(identifier.Id);
            }
            setAside.Add(identifier.Id);
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
                "Client alias learning demoted identifiers {IdentifierIds} of customer {CustomerId} to unverified on lead {LeadId}: the relink contradicts facts other decisions confirmed more than once.",
                string.Join(",", ids), previousCustomerId, leadId);
        }
        return demoted.Count;
    }

    /// <summary>
    /// P5. A trusted row that decisions other than this lead's confirmed more than once. Such a row is
    /// demoted by a relink, never expired by it.
    /// </summary>
    private static bool ConfirmedByOtherDecisions(CustomerIdentifier identifier, long leadId)
        => identifier.ObservationCount > 1
           && identifier.LearnedFromLeadId != leadId
           && identifier.IsVerified
           && string.Equals(identifier.Source, CustomerIdentifierSources.LeadReviewLearned, StringComparison.Ordinal);

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
            // An Email row the resolver's S1 and routing refuse (a system mailbox or relay address nobody entered)
            // is no fact about who writes from the domain, here as in the resolver's own domain-tie read.
            if (fact.IdentifierType == CustomerIdentifierType.Email
                && !IdentityDomainGuard.MayMatchExactAddress(fact.NormalizedValue, fact.Source)) continue;
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

        // A FACT A RELINK DEMOTED IS STILL THIS CUSTOMER'S RECORD (T25). One mis-click relink demotes an Email or a
        // Domain that other decisions confirmed many times, so that it stops auto-linking at once, and "the next
        // confirmation for its customer promotes it back". For a portal pair or an alias it did. For the mailbox it
        // could not: this read skipped the demoted rows, the relink's own decision for the other customer then
        // vetoed the domain, and two SEC confirmations from 57322@se.com.sa left SEC's fifty-times-confirmed
        // address and domain on the unverified shelf, so SEC's mail went from 1.00 to a 0.65 tie with Aramco. A row
        // this class demoted keeps the confidence it was trusted at (a row filed unverified never rises above
        // UnverifiedFilingConfidence) and was confirmed more than once, and it ties the domain back to its own
        // customer. Another customer's verified hold still refuses it (ClaimedByAnotherCustomer).
        if (!tie.TiedToCustomer)
        {
            var demoted = await _db.Set<CustomerIdentifier>().AsNoTracking().IgnoreQueryFilters()
                .Where(i => i.BusinessUnitId == businessUnitId
                            && i.CustomerId == customerId
                            && i.EffectiveTo == null
                            && !i.IsVerified
                            && i.Source == UnverifiedAliasSource
                            && i.ObservationCount > 1
                            && i.Confidence > UnverifiedFilingConfidence
                            && ((i.IdentifierType == CustomerIdentifierType.Domain && i.NormalizedValue == domain)
                                || (i.IdentifierType == CustomerIdentifierType.Email && i.NormalizedValue.EndsWith(atDomain))))
                .OrderBy(i => i.Id)
                .Select(i => i.Id)
                .Take(1)
                .ToListAsync(ct);
            if (demoted.Count > 0) tie.TiedToCustomer = true;
        }

        if (!tie.TiedToCustomer && DomainLabelSpellsCustomerName(domain, customerName))
            tie.TiedToCustomer = true;

        if (!tie.TiedToCustomer)
        {
            // Clientemail may still carry a display name ("Ali <ali@se.com.sa>"), hence the ">" form.
            var bracketed = atDomain + ">";
            // Human statuses are filtered in SQL, BEFORE the cap (the T08 defect, here in the learner's own read).
            // Filtered after it, 205 newer machine links from the domain pushed a person's older decision for
            // ANOTHER customer out of the window: its veto was lost, the domain was written to this customer at
            // 0.95, and the next job from the domain for that other customer linked here without asking anyone.
            var earlier = await _db.Leads.AsNoTracking().IgnoreQueryFilters()
                .Where(l => l.BusinessUnitId == businessUnitId
                            && l.Id != leadId
                            && l.CustomerId != null
                            && HumanDecidedStatuses.Contains(l.CustomerMatchStatus)
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
            // A DECISION A LATER CORRECTION TOOK BACK DOES NOT VETO. Leads mis-linked to Aramco and then
            // converted can never be relinked, so their decisions stood against SEC's domain for ever: every
            // later correct SEC confirmation left se.com.sa unverified and wrote no Email. When a person's
            // relink has since expired that customer's own learned row for the domain, and it holds nothing
            // on the domain now (no row of any kind, no contact), its old decisions are not counted.
            var otherCustomers = decided.Select(row => row.CustomerId).Where(id => id != customerId).Distinct().ToArray();
            if (otherCustomers.Length > 0)
            {
                var withdrawn = await CustomersACorrectionTookTheValueFromAsync(
                    businessUnitId, otherCustomers, CustomerIdentifierType.Domain, domain, ct);
                withdrawn.ExceptWith(contacts.Select(contact => contact.CustomerId));
                if (withdrawn.Count > 0)
                    decided = decided.Where(row => !withdrawn.Contains(row.CustomerId)).ToList();
            }
            // A PICK AGAINST ITS OWN PAGE IS A MIS-CLICK, NOT AN INTERMEDIARY (T26). One SEC print from 57322@se.com.sa,
            // its company-name field "Saudi Electricity Company", was linked to Saudi Aramco and converted, so it can
            // never be relinked and no correction ever takes its decision back. That one decision vetoed se.com.sa for
            // SEC for ever: every later correct SEC confirmation left the domain unverified and wrote no address, and
            // SEC's next mail was UNRESOLVED or a 0.65 tie with Aramco. A decision for another customer whose own page
            // printed THIS customer as the buying organisation, and not the customer picked, is a wrong pick rather
            // than a sign the domain carries several buyers' mail. It stops vetoing once this customer has at least
            // the corroboration a consumer mailbox needs (FreeMailAddressConfirmationsRequired decisions, this one
            // included, none on a page naming another organisation) and more of them than there are such picks. A
            // decision on a page that names nobody, or names its own customer, still vetoes: that is the EPC case.
            var againstOwnPage = decided
                .Where(row => row.CustomerId != customerId && ResemblesCustomerName(row.CustomerCompanyNameExtracted, customerName))
                .ToList();
            if (againstOwnPage.Count > 0 && !documentNamesSomebodyElse)
            {
                var pickedIds = againstOwnPage.Select(row => row.CustomerId).Distinct().ToArray();
                var pickedNames = await _db.Customers.AsNoTracking().IgnoreQueryFilters()
                    .Where(c => c.Buid == businessUnitId && pickedIds.Contains(c.Id))
                    .Select(c => new { c.Id, c.Name })
                    .ToListAsync(ct);
                againstOwnPage = againstOwnPage
                    .Where(row => !ResemblesCustomerName(
                        row.CustomerCompanyNameExtracted, pickedNames.FirstOrDefault(c => c.Id == row.CustomerId)?.Name))
                    .ToList();
                var confirmations = 1 + decided.Count(row =>
                    row.CustomerId == customerId
                    && !NamesAnOrganisationOtherThan(row.CustomerCompanyNameExtracted, customerName, selfNameKeys));
                if (againstOwnPage.Count > 0
                    && confirmations >= _policy.FreeMailAddressConfirmationsRequired
                    && confirmations > againstOwnPage.Count)
                    decided = decided.Where(row => !againstOwnPage.Contains(row)).ToList();
            }
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
    ///
    /// THIS IS THE PERMISSIVE READING, kept for the tenant's OWN names
    /// (<see cref="TenantSelfIdentity.IsOurs(string?, IEnumerable{string}?, IEnumerable{string?}?)"/>), where
    /// the rule deliberately errs towards "ours". Whether a domain spells a CUSTOMER is
    /// <see cref="DomainLabelSpellsCustomerName"/>.
    /// </summary>
    public static bool DomainLabelSpellsName(string? domainOrAddress, string? name)
        => LabelSpellsName(domainOrAddress, name, strict: false);

    /// <summary>
    /// Whether the domain's own name spells a CUSTOMER's name, which ties the domain to that customer on the
    /// first confirmation (P10). The rules of <see cref="DomainLabelSpellsName"/>, except that ONE word of
    /// the name is not the name.
    ///
    /// Any single distinctive word of four letters used to be enough, so pipes.com spelled Arabian Pipes
    /// Company, bank.com Al Rajhi Bank and jubail.com the Marafiq legal name. One confirmation from
    /// desk@pipes.com, a pipe marketplace, wrote the address at 1.00 and the domain at 0.95, and the next
    /// trader on that marketplace was linked to Arabian Pipes whatever the page said. A single-word match
    /// now counts only when the word is the name's whole key ("Marafiq"), its bracketed trade name
    /// ("... (Marafiq)", "(SATORP)"), or its only distinctive word ("Saudi Aramco"), and never when the word
    /// is a sector or place word (<see cref="SectorAndPlaceWords"/>), which is what "Arabian Pipes" and
    /// "National Water" reduce to. The whole tight key, all the distinctive words run together and the
    /// first two of them still tie ("alrajhibank", "saudiaramco", "fultoncounty").
    /// </summary>
    public static bool DomainLabelSpellsCustomerName(string? domainOrAddress, string? name)
        => LabelSpellsName(domainOrAddress, name, strict: true);

    private static bool LabelSpellsName(string? domainOrAddress, string? name, bool strict)
    {
        var label = RegistrableLabel(IdentityDomainGuard.DomainOf(domainOrAddress));
        if (label is null) return false;
        var tokens = CustomerNameDistinctiveness.DistinctiveTokens(name).Where(IsLatinWord).ToList();
        if (tokens.Count == 0) return false;
        var nameKey = CustomerNameNormalizer.LooseKey(name);
        var tightKey = CustomerNameNormalizer.TightKey(name);
        var oneWordName = !nameKey.Contains(' ');
        var tradeName = strict ? BracketedTradeName(name) : null;

        foreach (var spelling in LabelSpellings(label))
        {
            // "Al Dammam Trading Co." keys to the one word DAMMAM: a city, not a company, however whole.
            if (string.Equals(spelling, tightKey, StringComparison.Ordinal)
                && !(strict && oneWordName && SectorAndPlaceWords.Contains(spelling)))
                return true;
            if (tokens.Count >= 2)
            {
                if (string.Equals(spelling, string.Concat(tokens), StringComparison.Ordinal)) return true;
                if (spelling.StartsWith(tokens[0] + tokens[1], StringComparison.Ordinal)) return true;
            }
            foreach (var token in tokens)
            {
                if (!string.Equals(spelling, token, StringComparison.Ordinal)) continue;
                if (token.Length < 4 && !string.Equals(nameKey, token, StringComparison.Ordinal)) continue;
                if (!strict) return true;
                if (SectorAndPlaceWords.Contains(token)) continue;
                if (string.Equals(nameKey, token, StringComparison.Ordinal)
                    || string.Equals(tradeName, token, StringComparison.Ordinal)
                    || tokens.Count == 1)
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The one word in brackets that closes a customer's registered name ("Power and Water Utility Company
    /// for Jubail and Yanbu (Marafiq)", "... Petrochemical Co. (SATORP)"): Latin letters, at least four,
    /// and a word that names one company. Null when there is none. Read off the customer's own record, where
    /// a bracket is the trade name the customer registered, not off a printed address.
    /// </summary>
    private static string? BracketedTradeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var trimmed = name.Trim();
        if (!trimmed.EndsWith(')')) return null;
        var open = trimmed.LastIndexOf('(');
        if (open <= 0) return null;
        var key = CustomerNameNormalizer.LooseKey(trimmed[(open + 1)..^1]);
        return key.Length >= 4 && IsLatinWord(key) && CustomerNameDistinctiveness.HasDistinctiveToken(key) ? key : null;
    }

    /// <summary>
    /// Words that name a trade, a utility or a place rather than one company, as they appear in a domain
    /// label. Used ONLY to stop one such word tying a domain to a customer; it never touches a name match.
    /// A word missing from this list keeps the single-word tie it always had, so the list can only make the
    /// learner more careful: an unlisted word costs what it cost before, a listed one costs one suggestion.
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
    ///
    /// AN EARLIER DECISION IS ONLY AS GOOD AS ITS PAGE. Any earlier human decision on the pair used to vouch,
    /// so a rep who picked Aramco on an SEC e-bidding print (whose delivery address says Saudi Electricity
    /// Company) and picked Aramco again on the next, pair-only, print promoted the pair to 0.92, and from then
    /// on every SEC print linked to Aramco before its address was read. Now:
    ///   * this document must not name another active customer (a page naming SEC cannot teach Aramco's pair);
    ///   * a vouching document must name this customer, or name no active customer at all;
    ///   * human statuses are filtered in SQL before the read is capped;
    ///   * a customer whose own learned row for the pair a person's later relink expired, and who holds no
    ///     row for the pair now, does not veto with its old decisions.
    /// A tenant too large to read its customer book cannot be shown to name nobody else, and does not vouch.
    /// </summary>
    private async Task<bool> EarlierDecisionCarriesPairAsync(
        long businessUnitId, Lead lead, long customerId, string? customerName, string pairKey,
        IReadOnlySet<string> selfNameKeys, CancellationToken ct)
    {
        var customers = await LoadActiveCustomerNamesAsync(businessUnitId, ct);
        if (customers is null || DocumentNamesAnotherCustomer(lead, customerId, customers, selfNameKeys)) return false;

        var account = lead.SupplierAccountRefOnDocument!.Trim().ToUpper();
        var earlier = await _db.Leads.AsNoTracking().IgnoreQueryFilters()
            .Where(l => l.BusinessUnitId == businessUnitId
                        && l.Id != lead.Id
                        && l.CustomerId != null
                        && HumanDecidedStatuses.Contains(l.CustomerMatchStatus)
                        && l.CustomerPortalNameExtracted != null
                        && l.SupplierAccountRefOnDocument != null
                        && l.SupplierAccountRefOnDocument.Trim().ToUpper() == account)
            .OrderByDescending(l => l.Id)
            .Select(l => new
            {
                l.Id, CustomerId = l.CustomerId!.Value, l.CustomerMatchStatus,
                l.CustomerPortalNameExtracted, l.SupplierAccountRefOnDocument
            })
            .Take(MaximumDomainEvidenceRead)
            .ToListAsync(ct);
        var decided = earlier
            .Where(row => LeadCustomerMatchStatuses.IsHumanDecided(row.CustomerMatchStatus))
            .Where(row => string.Equals(
                PortalAccountKey(row.CustomerPortalNameExtracted, row.SupplierAccountRefOnDocument), pairKey, StringComparison.Ordinal))
            .ToList();

        var otherCustomers = decided.Select(row => row.CustomerId).Where(id => id != customerId).Distinct().ToArray();
        if (otherCustomers.Length > 0)
        {
            var withdrawn = await CustomersACorrectionTookTheValueFromAsync(
                businessUnitId, otherCustomers, CustomerIdentifierType.PortalAccount, pairKey, ct);
            if (withdrawn.Count > 0)
                decided = decided.Where(row => !withdrawn.Contains(row.CustomerId)).ToList();
        }
        if (decided.Count == 0 || decided.Any(row => row.CustomerId != customerId)) return false;

        var documents = await LoadEarlierDocumentsAsync(
            businessUnitId, decided.Select(row => row.Id).Take(MaximumPairVouchersRead).ToArray(), ct);
        // AN EARLIER PICK AGAINST ITS OWN PAGE VETOES, IT DOES NOT MERELY FAIL TO VOUCH (T24). A rep who picked
        // Aramco on an SEC e-bidding print whose address says Saudi Electricity Company, and then twice more on
        // pair-only prints, promoted the pair on the third: the pair-only prints vouched for each other, the page
        // that named SEC was simply not counted, and from then on the learned-portal tier linked every SEC print
        // to Aramco at 0.92 before its address was read. A decision for this customer taken on a page that names
        // another active customer, and not this one, is evidence the pick was wrong, so the pair stays unverified.
        if (documents.Any(document => !DocumentNamesCustomer(document, customerId, customerName, selfNameKeys)
                                      && DocumentNamesAnotherCustomer(document, customerId, customers, selfNameKeys)))
            return false;
        return documents.Any(document => DocumentNamesCustomer(document, customerId, customerName, selfNameKeys)
                                         || !DocumentNamesAnotherCustomer(document, customerId, customers, selfNameKeys));
    }

    /// <summary>
    /// P11. Whether the page names an active customer other than the one picked, read as the resolver reads a
    /// page: headers, delivery addresses and labelled columns, never item text. Any customer the name scan
    /// finds in it counts, even one only offered.
    /// </summary>
    private static bool DocumentNamesAnotherCustomer(
        Lead document, long customerId, IReadOnlyList<CustomerNameSnapshot> customers, IEnumerable<string> selfNameKeys)
    {
        var statements = LeadCustomerResolutionService.Passages(document)
            .Where(passage => passage.Role != PassageRole.ItemText)
            .ToList();
        if (statements.Count == 0) return false;
        var outcome = CustomerIdentityResolver.Resolve(
            new LeadClientEvidence
            {
                BusinessUnitId = document.BusinessUnitId, LeadId = document.Id,
                SupplierNameOnDocument = document.SupplierNameOnDocument,
                TenantSelfNameKeys = selfNameKeys.ToList(),
                Passages = statements
            },
            new ClientResolutionCorpus { Customers = customers.ToList() },
            DocumentReadingPolicy);
        return outcome.Candidates.Any(candidate =>
            candidate.CustomerId != customerId
            && string.Equals(candidate.ReasonCode, CustomerMatchReasonCodes.NameInDocument, StringComparison.Ordinal));
    }

    /// <summary>
    /// Whether the printed name and the customer's name are written in different scripts, so their letters cannot be
    /// compared: every letter of one is Latin and no letter of the other is ("الشركة السعودية للكهرباء" against
    /// "Saudi Electricity Company", or the other way round).
    /// </summary>
    private static bool IsWrittenInAnotherScript(string? printed, string? customerName)
    {
        if (string.IsNullOrWhiteSpace(printed) || string.IsNullOrWhiteSpace(customerName)) return false;
        return (OnlyNonLatinLetters(printed) && OnlyLatinLetters(customerName))
               || (OnlyLatinLetters(printed) && OnlyNonLatinLetters(customerName));

        static bool IsLatin(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
        static bool OnlyLatinLetters(string value) => value.Any(char.IsLetter) && value.Where(char.IsLetter).All(IsLatin);
        static bool OnlyNonLatinLetters(string value) => value.Any(char.IsLetter) && !value.Any(IsLatin);
    }

    /// <summary>
    /// LF08. Whether a company name printed in another script than the chosen customer's name may be trusted as that
    /// customer's alias. Never while another active customer's own name reads as it, or this page names another active
    /// customer. Then either this page names the chosen customer in a header or an address, or people linked earlier
    /// prints carrying the same name to this customer, and to nobody else, often enough that this decision makes
    /// <see cref="CustomerResolutionPolicy.FreeMailAddressConfirmationsRequired"/>, none of those pages naming another
    /// active customer. A tenant too large to read its customer book cannot be checked and does not trust.
    /// </summary>
    private async Task<bool> ForeignScriptNameIsTheCustomersAsync(
        long businessUnitId, Lead lead, long customerId, string? customerName, string printed, string aliasKey,
        IReadOnlySet<string> selfNameKeys, CancellationToken ct)
    {
        var customers = await LoadActiveCustomerNamesAsync(businessUnitId, ct);
        if (customers is null) return false;
        if (customers.Any(customer => customer.CustomerId != customerId
                                      && (string.Equals(CustomerNameNormalizer.LooseKey(customer.Name), aliasKey, StringComparison.Ordinal)
                                          || ResemblesCustomerName(printed, customer.Name))))
            return false;
        if (DocumentNamesAnotherCustomer(lead, customerId, customers, selfNameKeys)) return false;
        if (DocumentNamesCustomer(lead, customerId, customerName, selfNameKeys)) return true;

        // The printed text is compared as written in SQL and by key in C#: LooseKey cannot run in SQL, and a print
        // written another way simply does not count towards the corroboration.
        var written = printed.Trim();
        var earlier = await _db.Leads.AsNoTracking().IgnoreQueryFilters()
            .Where(l => l.BusinessUnitId == businessUnitId
                        && l.Id != lead.Id
                        && l.CustomerId != null
                        && HumanDecidedStatuses.Contains(l.CustomerMatchStatus)
                        && l.CustomerCompanyNameExtracted != null
                        && l.CustomerCompanyNameExtracted.Trim() == written)
            .OrderByDescending(l => l.Id)
            .Select(l => new { l.Id, CustomerId = l.CustomerId!.Value, l.CustomerMatchStatus, l.CustomerCompanyNameExtracted })
            .Take(MaximumDomainEvidenceRead)
            .ToListAsync(ct);
        var decided = earlier
            .Where(row => LeadCustomerMatchStatuses.IsHumanDecided(row.CustomerMatchStatus))
            .Where(row => string.Equals(CustomerNameNormalizer.LooseKey(row.CustomerCompanyNameExtracted), aliasKey, StringComparison.Ordinal))
            .ToList();
        if (decided.Count == 0 || decided.Any(row => row.CustomerId != customerId)) return false;

        var documents = await LoadEarlierDocumentsAsync(
            businessUnitId, decided.Select(row => row.Id).Take(MaximumPairVouchersRead).ToArray(), ct);
        if (documents.Any(document => DocumentNamesAnotherCustomer(document, customerId, customers, selfNameKeys))) return false;
        return 1 + documents.Count >= _policy.FreeMailAddressConfirmationsRequired;
    }

    /// <summary>The tenant's active customers by name; null past <see cref="MaximumCustomersCompared"/>, which answers "cannot tell".</summary>
    private async Task<IReadOnlyList<CustomerNameSnapshot>?> LoadActiveCustomerNamesAsync(long businessUnitId, CancellationToken ct)
    {
        var rows = await _db.Customers.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.Buid == businessUnitId && c.IsActive != false && c.Name != null)
            .OrderBy(c => c.Id)
            .Select(c => new { c.Id, c.Name })
            .Take(MaximumCustomersCompared + 1)
            .ToListAsync(ct);
        return rows.Count > MaximumCustomersCompared
            ? null
            : rows.Select(row => new CustomerNameSnapshot(row.Id, row.Name!)).ToList();
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

    /// <summary>
    /// Customers, among <paramref name="customerIds"/>, whose claim on a value a person's later correction
    /// took back: this class once learned the value for them and P5 has expired that row, and they hold no
    /// active row on it now, of any source or grade (for a Domain, no Email at the domain either). Their old
    /// decisions no longer veto the value for another customer. A customer re-confirmed since then holds a
    /// row again, and its decisions count again.
    /// </summary>
    private async Task<HashSet<long>> CustomersACorrectionTookTheValueFromAsync(
        long businessUnitId, long[] customerIds, CustomerIdentifierType type, string value, CancellationToken ct)
    {
        var corrected = await _db.Set<CustomerIdentifier>().AsNoTracking().IgnoreQueryFilters()
            .Where(i => i.BusinessUnitId == businessUnitId
                        && customerIds.Contains(i.CustomerId)
                        && i.EffectiveTo != null
                        && LearnedSources.Contains(i.Source)
                        && i.IdentifierType == type
                        && i.NormalizedValue == value)
            .Select(i => i.CustomerId)
            .Distinct()
            .ToListAsync(ct);
        if (corrected.Count == 0) return new HashSet<long>();

        var atDomain = "@" + value;
        var isDomain = type == CustomerIdentifierType.Domain;
        var stillHeld = await _db.Set<CustomerIdentifier>().AsNoTracking().IgnoreQueryFilters()
            .Where(i => i.BusinessUnitId == businessUnitId
                        && corrected.Contains(i.CustomerId)
                        && i.EffectiveTo == null
                        && ((i.IdentifierType == type && i.NormalizedValue == value)
                            || (isDomain && i.IdentifierType == CustomerIdentifierType.Email && i.NormalizedValue.EndsWith(atDomain))))
            .Select(i => i.CustomerId)
            .Distinct()
            .ToListAsync(ct);
        return corrected.Where(id => !stillHeld.Contains(id)).ToHashSet();
    }

    private enum FreeMailAddressReading { NotYetCorroborated, Corroborated, ConfirmedForAnotherCustomer }

    /// <summary>
    /// P2. What people have said about one consumer-mailbox address: the earlier leads a person linked whose
    /// envelope sender, sender column or printed buyer address is exactly this address, human statuses
    /// filtered in SQL before the read is capped.
    ///   * Any decision for a customer other than this one: the address is an agent's. Nothing is learned, and
    ///     every row this class learned for the address, on any customer, is expired and written through.
    ///   * Otherwise: corroborated once this decision and the earlier ones for this customer reach
    ///     <see cref="CustomerResolutionPolicy.FreeMailAddressConfirmationsRequired"/>. A decision on a page whose company-name field
    ///     names another organisation does not count towards it, and this page naming one blocks it, exactly
    ///     as neither vouches for a domain (P10).
    /// </summary>
    /// <param name="takeBackWhatItTaught">
    /// True for a consumer mailbox, whose first decision for a second customer marks it an agent's and expires what
    /// was learned for it. False for an organisation's own mailbox (LG01), read only to ask whether it is
    /// corroborated: its learned rows answer to P3 and P5 like any other organisation's, and a relink's demotion of
    /// a fact confirmed many times must not be undone by a read.
    /// </param>
    private async Task<FreeMailAddressReading> ReadAddressDecisionsAsync(
        long businessUnitId, long leadId, long customerId, string? customerName, string address,
        bool documentNamesSomebodyElse, IReadOnlySet<string> selfNameKeys,
        HashSet<long> expiredIds, HashSet<long> setAside, DateTime now, bool takeBackWhatItTaught, CancellationToken ct)
    {
        var earlier = await _db.Leads.AsNoTracking().IgnoreQueryFilters()
            .Where(l => l.BusinessUnitId == businessUnitId
                        && l.Id != leadId
                        && l.CustomerId != null
                        && HumanDecidedStatuses.Contains(l.CustomerMatchStatus)
                        && ((l.Clientemail != null && l.Clientemail.ToLower().Contains(address))
                            || (l.CustomerBuyerEmailExtracted != null && l.CustomerBuyerEmailExtracted.ToLower().Contains(address))
                            || (l.EmailIngests != null && l.EmailIngests.FromEmail.ToLower().Contains(address))))
            .OrderByDescending(l => l.Id)
            .Select(l => new
            {
                CustomerId = l.CustomerId!.Value, l.CustomerMatchStatus, l.Clientemail, l.CustomerBuyerEmailExtracted,
                FromEmail = l.EmailIngests != null ? l.EmailIngests.FromEmail : null,
                l.CustomerCompanyNameExtracted
            })
            .Take(MaximumDomainEvidenceRead)
            .ToListAsync(ct);
        var decided = earlier
            .Where(row => LeadCustomerMatchStatuses.IsHumanDecided(row.CustomerMatchStatus))
            .Where(row => IsThisAddress(row.FromEmail) || IsThisAddress(row.Clientemail) || IsThisAddress(row.CustomerBuyerEmailExtracted))
            .ToList();

        if (decided.Any(row => row.CustomerId != customerId))
        {
            if (!takeBackWhatItTaught) return FreeMailAddressReading.ConfirmedForAnotherCustomer;
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
                    "Client alias learning expired {Count} learned Email row(s) for a consumer mailbox on lead {LeadId}: people have linked that address to more than one customer.",
                    ids.Length, leadId);
            }
            return FreeMailAddressReading.ConfirmedForAnotherCustomer;
        }

        if (documentNamesSomebodyElse) return FreeMailAddressReading.NotYetCorroborated;
        var confirmations = 1 + decided.Count(row =>
            !NamesAnOrganisationOtherThan(row.CustomerCompanyNameExtracted, customerName, selfNameKeys));
        return confirmations >= _policy.FreeMailAddressConfirmationsRequired
            ? FreeMailAddressReading.Corroborated
            : FreeMailAddressReading.NotYetCorroborated;

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
