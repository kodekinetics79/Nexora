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
///   P1 self-identity  — never learn the tenant's own name / domains / the document's vendor block
///   P2 synthetic      — never learn Nexora's ingestion placeholders; never a free-mail or
///                       portal-relay address AT ALL (neither Email nor Domain)
///   P3 exclusivity    — Email/ErpAccount/TaxRegistration are exclusive; on conflict SKIP, never steal
///   P4 multi-owner    — Alias/Domain/PortalAccount may point at several customers; the
///                       resolver then returns AMBIGUOUS, which is an outcome, not an error
///   P5 reversal       — a later review that CHANGES the customer expires everything this
///                       lead taught about the rejected one before teaching afresh
///   P6 approval gate  — only "approve" + an explicitly supplied customer; NEVER a machine
///                       AUTO_MATCHED result (that is the path by which one machine mistake
///                       would bootstrap itself into an authoritative alias)
///   P7 single level   — identifiers point DIRECTLY at a CustomerId; no alias -> alias
///                       indirection, no supersession chain, so cycles are structurally
///                       impossible. If customer merge/supersession is ever added, port the
///                       `visited` HashSet cycle detector from ProductIdentityResolver
///                       (Inventory/Commercial/CommercialInventoryServices.cs:36-56) FIRST.
///   P8 resemblance    — a name that looks NOTHING like the customer the reviewer picked is
///                       not that customer's alias; it is recorded for review, never as
///                       authoritative knowledge
///   P9 shared network — never learn a portal pair whose supplier number was issued by the
///                       NETWORK rather than by the buyer (SharedSupplierNetworks)
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

    /// <summary>The portal issues OUR supplier number itself, so the pair names nobody.</summary>
    public const string SkipSharedSupplierNetwork = "sharedSupplierNetwork";

    /// <summary>A consumer mailbox or a procurement portal's relay: evidence about a PERSON or a POSTMAN.</summary>
    public const string SkipPersonalOrRelayAddress = "personalOrRelayAddress";

    /// <summary>
    /// Where a demoted alias is filed. Deliberately NOT in
    /// <see cref="CustomerIdentifierSources.TrustedForAutoLink"/>, so the resolver's S3 tier
    /// will not link on it however many times it is seen — the row exists so a person can look
    /// at it and say "yes, that really is them", not so the machine can act on it. It is also
    /// absent from <c>CustomerIdentityMaintenance.ManagedSources</c>, exactly like
    /// <see cref="CustomerIdentifierSources.LeadReviewLearned"/>, so a profile sync cannot
    /// silently delete the evidence of what a reviewer once confirmed.
    /// </summary>
    public const string UnverifiedAliasSource = "LeadReviewUnverified";

    /// <summary>
    /// How alike two company names must be, on Jaro-Winkler over
    /// <see cref="CustomerNameNormalizer.TightKey"/>, before a printed name is accepted as the
    /// chosen customer's alias without any other corroboration. Deliberately generous — it is
    /// there to reject "FULTON COUNTY GOVERNMENT" for "Saudi Aramco" (0.43), not to adjudicate
    /// spelling: "SAUDI ELECTRICITY" against "Saudi Aramco" scores 0.79 and is still accepted,
    /// because a reviewer looking at the document is better placed to judge that than a metric.
    /// </summary>
    public const double AliasResemblanceThreshold = 0.75d;

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
        var expired = 0;

        // P5: the reviewer moved this lead to a different client. Everything this lead
        // previously taught about the rejected client is now known to be wrong.
        if (previousCustomerId.HasValue && previousCustomerId.Value != customerId)
        {
            var contradicted = await _db.Set<CustomerIdentifier>().IgnoreQueryFilters()
                .Where(i => i.BusinessUnitId == businessUnitId
                            && i.CustomerId == previousCustomerId.Value
                            && i.LearnedFromLeadId == lead.Id
                            && i.EffectiveTo == null)
                .ToListAsync(ct);
            foreach (var identifier in contradicted)
            {
                identifier.EffectiveTo = now;
                expired++;
            }
        }

        var selfNameKeys = await LoadTenantSelfNameKeysAsync(businessUnitId, lead, ct);
        var selfDomains = await LoadTenantSelfDomainsAsync(businessUnitId, ct);
        // The name of the customer the reviewer actually picked. Without it there is nothing to
        // compare the printed name against, and "learn whatever is printed" is exactly the bug
        // (P8). Tenant-scoped on purpose: a customer id from another tenant reads back as no
        // name at all, and a nameless customer demotes rather than trusts.
        var customerName = await _db.Customers.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.Id == customerId && c.Buid == businessUnitId)
            .Select(c => c.Name)
            .FirstOrDefaultAsync(ct);

        var proposals = new List<Proposal>();

        // 1. Real sender + the buyer address printed on the document. For a folder-ingested
        //    or scanned bid the printed buyer address is the ONLY place the buying
        //    organisation's real domain appears, and the reviewer has just confirmed whose
        //    document it is — so both are learned identically.
        foreach (var raw in new[] { ResolveSender(lead), lead.CustomerBuyerEmailExtracted })
        {
            var address = LeadCustomerResolutionService.ParseAddress(raw);
            if (string.IsNullOrEmpty(address)) continue;
            var domain = RoutingValueNormalizer.DomainFromEmail(address);
            if (SyntheticIdentityGuard.IsSyntheticDomain(domain)) { skips.Add(SkipSynthetic); continue; }
            if (domain is not null && selfDomains.Contains(domain)) { skips.Add(SkipSelfIdentity); continue; }

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

            proposals.Add(new Proposal(CustomerIdentifierType.Email, address, address, true, 1.00m));
            // The Domain rule is unchanged in substance: a shared mailbox never becomes a Domain.
            // It now simply falls out of the guard above, which also closes the relay hole — until
            // now ariba.com was not free-mail, so it WAS learned as a Domain at 0.95.
            if (domain is { Length: > 0 })
                proposals.Add(new Proposal(CustomerIdentifierType.Domain, domain, domain, true, 0.95m));
        }

        // 2. The buying organisation's name, as printed.
        if (!string.IsNullOrWhiteSpace(lead.CustomerCompanyNameExtracted))
        {
            var display = lead.CustomerCompanyNameExtracted!.Trim();
            var key = CustomerNameNormalizer.LooseKey(display);
            if (key.Length == 0 || SelfIdentityGuard.IsSelfName(key, selfNameKeys))
            {
                skips.Add(SkipSelfIdentity);
            }
            else if (ResemblesCustomerName(display, customerName))
            {
                proposals.Add(new Proposal(CustomerIdentifierType.Alias, key, display, true, 0.90m));
            }
            else
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
                    CustomerIdentifierType.Alias, key, display, false, 0.50m, UnverifiedAliasSource));
                _log?.LogInformation(
                    "Client alias learning recorded \"{Alias}\" for customer {CustomerId} as unverified on lead {LeadId}: it does not resemble the customer's own name.",
                    display, customerId, lead.Id);
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
            {
                skips.Add(SkipSharedSupplierNetwork);
            }
            else
            {
                var portalKey = CustomerNameNormalizer.LooseKey(lead.CustomerPortalNameExtracted);
                var accountKey = RoutingValueNormalizer.Normalize(
                    CustomerIdentifierType.ErpAccount, lead.SupplierAccountRefOnDocument!);
                if (portalKey.Length > 0 && accountKey.Length > 0)
                    proposals.Add(new Proposal(
                        CustomerIdentifierType.PortalAccount,
                        $"{portalKey}|{accountKey}",
                        $"{lead.CustomerPortalNameExtracted!.Trim()} / {lead.SupplierAccountRefOnDocument!.Trim()}",
                        true, 0.92m));
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
            return new CustomerAliasLearningResult(0, 0, expired, skips);
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

        return new CustomerAliasLearningResult(learned, reinforced, expired, skips);
    }

    /// <summary>
    /// Does the company name printed on the document look like the customer a reviewer just
    /// picked? Any ONE of three agreements is enough, because real documents say a customer's
    /// name in three different ways:
    ///   * spelled roughly the same ("SAUDI ELECTRICITY CO." for "Saudi Electricity Company"),
    ///   * written as the initials the buyer uses for itself ("SEC"),
    ///   * longer or shorter by whole words ("Saudi Aramco" printed as
    ///     "SAUDI ARABIAN OIL COMPANY - SAUDI ARAMCO", or the reverse).
    ///
    /// It is a SANITY gate, not an identity test — the reviewer has already decided identity,
    /// and this only asks whether the words on the page are evidence OF that decision or of
    /// something else entirely. A false reject costs one unverified row somebody can promote;
    /// a false accept costs an authoritative alias that quietly mis-links documents forever.
    ///
    /// Fails closed on a customer with no readable name: with nothing to compare against,
    /// "accept whatever is printed" is the bug, not the fallback.
    /// </summary>
    public static bool ResemblesCustomerName(string? extracted, string? customerName)
    {
        var extractedKey = CustomerNameNormalizer.LooseKey(extracted);
        var customerKey = CustomerNameNormalizer.LooseKey(customerName);
        if (extractedKey.Length == 0 || customerKey.Length == 0) return false;

        if (string.Equals(extractedKey, customerKey, StringComparison.Ordinal)) return true;

        if (CustomerNameNormalizer.JaroWinkler(
                CustomerNameNormalizer.TightKey(extracted),
                CustomerNameNormalizer.TightKey(customerName)) >= AliasResemblanceThreshold)
            return true;

        // AcronymKey is empty for a name too short to have distinctive initials and for the
        // ordinary words listed in CustomerNameNormalizer.AmbiguousAcronyms, so an empty
        // acronym must never be allowed to "equal" anything.
        var acronym = CustomerNameNormalizer.AcronymKey(customerName);
        if (acronym.Length > 0 && string.Equals(extractedKey, acronym, StringComparison.Ordinal))
            return true;

        return IsWholeWordSubsequence(extractedKey, customerKey)
               || IsWholeWordSubsequence(customerKey, extractedKey);
    }

    /// <summary>
    /// Every word of <paramref name="candidateKey"/> appears in <paramref name="containerKey"/>,
    /// in the same order, as WHOLE words. Whole words on purpose: a substring test would call
    /// "ARCENE" a match for "MARAFIQ SERVICES" the moment any fragment lined up, and letters
    /// inside a word are not a name.
    /// </summary>
    private static bool IsWholeWordSubsequence(string candidateKey, string containerKey)
    {
        var needle = candidateKey.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var haystack = containerKey.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (needle.Length == 0 || haystack.Length == 0 || needle.Length > haystack.Length) return false;

        var next = 0;
        foreach (var token in haystack)
        {
            if (next < needle.Length && string.Equals(token, needle[next], StringComparison.Ordinal))
                next++;
        }
        return next == needle.Length;
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

    private async Task<HashSet<string>> LoadTenantSelfDomainsAsync(long businessUnitId, CancellationToken ct)
    {
        var mailboxes = await _db.EmailConfigurations.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.BusinessUnitId == businessUnitId && c.EmailAddress != null)
            .Select(c => c.EmailAddress!)
            .Distinct()
            .Take(50)
            .ToListAsync(ct);
        return mailboxes
            .Select(RoutingValueNormalizer.DomainFromEmail)
            .Where(domain => !string.IsNullOrWhiteSpace(domain))
            .Select(domain => domain!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
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
