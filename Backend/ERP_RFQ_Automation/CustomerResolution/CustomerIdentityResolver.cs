using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.Models;

namespace ERP_RFQ_Automation.CustomerResolution;

/// <summary>
/// Deterministic client-organisation resolution. NO database, NO LLM, NO embeddings —
/// given the same evidence and the same corpus this returns the same answer forever, which
/// is what makes "why is this lead linked to that customer" answerable.
///
/// Tiers run in order and the FIRST decisive tier wins:
///   S0 guard   — direction of trade, tenant self-identity, synthetic + free-mail domains
///   S1 exact   — sender/buyer address, ERP account, tax registration      -> AUTO 1.00
///   S2 domain  — corporate sender domain                                  -> AUTO 0.95
///   S3 learned — human-taught alias / portal+vendor-code pair             -> AUTO 0.90/0.92
///   S4..S6     — read-only scans that only ever SUGGEST
///   S7         — UNRESOLVED
///
/// Two or more equally strong hits in S1..S3 produce AMBIGUOUS, never a coin toss.
///
/// SINGLE LEVEL BY CONSTRUCTION (P7): a CustomerIdentifier points DIRECTLY at a CustomerId.
/// There is no alias -> alias indirection and no supersession chain, so cycles are
/// structurally impossible and no cycle detector is needed here. If customer merge or
/// customer supersession is ever added, whoever adds it MUST port the `visited` HashSet
/// cycle detector from ProductIdentityResolver
/// (Inventory/Commercial/CommercialInventoryServices.cs:36-56) before the first chain edge
/// is written.
/// </summary>
public static class CustomerIdentityResolver
{
    public static ClientResolutionOutcome Resolve(
        LeadClientEvidence evidence,
        ClientResolutionCorpus corpus,
        CustomerResolutionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(corpus);
        policy ??= new CustomerResolutionPolicy();

        var guarded = Guard(evidence);
        if (guarded.IsEmpty)
            return ClientResolutionOutcome.Unresolved(
                CustomerMatchReasonCodes.NoEvidence,
                "No usable client evidence: the document names no buying organisation and the sender is an internal placeholder.");

        var names = corpus.Customers
            .GroupBy(c => c.CustomerId)
            .ToDictionary(g => g.Key, g => g.First().Name, EqualityComparer<long>.Default);

        // ── S1 AUTHORITATIVE EXACT ────────────────────────────────────────────
        var authoritative = new List<Hit>();
        foreach (var identifier in corpus.Identifiers)
        {
            switch (identifier.IdentifierType)
            {
                case CustomerIdentifierType.Email when guarded.Addresses.Contains(identifier.NormalizedValue):
                    authoritative.Add(new Hit(identifier.CustomerId, policy.AuthoritativeConfidence,
                        CustomerMatchReasonCodes.SenderEmailExact,
                        $"Sender address {identifier.NormalizedValue} is registered to this client."));
                    break;
                // A four-character account number is a company code, not an identity. On an SAP
                // print a line's company reference is 1000 / 2000 / SA01, shared across every
                // affiliate of a group, and this is the strongest tier in the engine.
                case CustomerIdentifierType.ErpAccount
                    when identifier.NormalizedValue.Length >= policy.MinimumErpAccountLength
                         && guarded.AccountReferences.Contains(identifier.NormalizedValue):
                    authoritative.Add(new Hit(identifier.CustomerId, policy.AuthoritativeConfidence,
                        CustomerMatchReasonCodes.ErpAccountExact,
                        $"Portal/ERP account {identifier.NormalizedValue} belongs to this client."));
                    break;
                case CustomerIdentifierType.TaxRegistration when guarded.TaxRegistrations.Contains(identifier.NormalizedValue):
                    authoritative.Add(new Hit(identifier.CustomerId, policy.AuthoritativeConfidence,
                        CustomerMatchReasonCodes.TaxRegExact,
                        $"Registration number {identifier.NormalizedValue} belongs to this client."));
                    break;
            }
        }
        if (Decide(authoritative, names, policy, out var authoritativeOutcome))
            return WithContact(authoritativeOutcome!, guarded, corpus);

        // ── S2 CORPORATE SENDER DOMAIN ────────────────────────────────────────
        var domainHits = corpus.Identifiers
            .Where(i => i.IdentifierType == CustomerIdentifierType.Domain
                        && guarded.Domains.Contains(i.NormalizedValue))
            .Select(i => new Hit(i.CustomerId, policy.DomainConfidence,
                CustomerMatchReasonCodes.SenderDomain,
                $"Shares the corporate sender domain {i.NormalizedValue}."))
            .ToList();
        if (Decide(domainHits, names, policy, out var domainOutcome))
            return WithContact(domainOutcome!, guarded, corpus);

        // ── S3 HUMAN-TAUGHT ALIAS / PORTAL ACCOUNT ────────────────────────────
        // Only verified identifiers from trusted sources. A machine AUTO_MATCH never
        // becomes an alias (see CustomerAliasLearner P6), so this tier cannot bootstrap
        // its own mistakes.
        var learnedHits = new List<Hit>();
        // Portal pairs that are real evidence but name nobody on their own. Collected here and
        // offered further down, never decided on. See the SharedSupplierNetworks branch below.
        var sharedNetworkHits = new List<Hit>();
        foreach (var identifier in corpus.Identifiers)
        {
            if (!identifier.IsVerified) continue;
            if (!CustomerIdentifierSources.TrustedForAutoLink.Contains(identifier.Source, StringComparer.Ordinal))
                continue;

            var isNameType = identifier.IdentifierType is CustomerIdentifierType.Alias
                or CustomerIdentifierType.CustomerName;
            if (isNameType && guarded.NameKey.Length > 0 &&
                string.Equals(identifier.NormalizedValue, guarded.NameKey, StringComparison.Ordinal))
            {
                learnedHits.Add(new Hit(identifier.CustomerId, policy.LearnedAliasConfidence,
                    CustomerMatchReasonCodes.LearnedAlias,
                    $"\"{evidence.CustomerCompanyName}\" was confirmed as this client by a reviewer."));
            }
            else if (identifier.IdentifierType == CustomerIdentifierType.PortalAccount &&
                     guarded.PortalAccountKey is { Length: > 0 } &&
                     string.Equals(identifier.NormalizedValue, guarded.PortalAccountKey, StringComparison.Ordinal))
            {
                // WHOSE NUMBER IS IT? This tier keys on "portal|our-vendor-code" and links at
                // 0.92, which is honest only while the BUYER issued the code. Saudi Electricity
                // Company issued our vendor code 2004414; it means nothing anywhere else, so it
                // names SEC. An Ariba Network ID, or an Etimad supplier number, is ONE number
                // issued by the NETWORK that identifies US to every buyer on it. Learned against
                // the first Ariba buyer, it would auto-link every later Ariba RFQ — from any
                // buyer at all — to that one customer at 0.92, which links without asking
                // anybody. Teaching a second buyer makes it worse, not better: the pair then
                // matches two customers and every Ariba document is permanently AMBIGUOUS, a
                // state no further teaching can undo.
                //
                // The pair is still a true fact about a real document, so it is OFFERED at the
                // weakest suggestion confidence in the engine. It just never decides.
                if (SharedSupplierNetworks.IsShared(evidence.CustomerPortalName))
                {
                    sharedNetworkHits.Add(new Hit(identifier.CustomerId, policy.RfqPatternSuggestionConfidence,
                        CustomerMatchReasonCodes.LearnedPortalAccount,
                        $"Vendor code {evidence.SupplierAccountRefOnDocument} is OUR supplier number on " +
                        $"{evidence.CustomerPortalName}, a sourcing network shared by several of your customers, " +
                        "so it names no single buyer on its own."));
                    continue;
                }
                learnedHits.Add(new Hit(identifier.CustomerId, policy.LearnedPortalAccountConfidence,
                    CustomerMatchReasonCodes.LearnedPortalAccount,
                    $"Same portal vendor code {evidence.SupplierAccountRefOnDocument} on {evidence.CustomerPortalName}."));
            }
        }
        if (Decide(learnedHits, names, policy, out var learnedOutcome))
            return WithContact(learnedOutcome!, guarded, corpus);

        // ── S4..S6 THE NAME THE BUYER WROTE, THEN SUGGESTION-ONLY SCANS ───────
        // A bare company-name field never links: two distinct Gulf legal entities routinely
        // share a trade name, so a first-time collision is not evidence and a human-confirmed
        // one (S3) is. A name written inside a PASSAGE is different — "Deliver to: Saudi
        // Electricity Company-Jizan Area" names the customer whatever system printed the page —
        // and what that name is worth depends on what the passage is doing: a header states who
        // is buying, an address states where the goods go, item text may mention anyone
        // ("SEC-specified barcode"). A one-word customer name still never matches on its own
        // unless it is a real trade name: "Test" or "Alpha" is anybody's word.
        var namedInAddress = new List<Hit>();
        var namedInText = new List<Hit>();
        // Ship-to hits the page itself contradicts: kept, demoted, and offered to a person.
        var consigneeOnly = new List<Hit>();
        if (guarded.Passages.Count > 0)
        {
            var namesToFind = new List<(long CustomerId, string Key, string Display, bool Taught, bool Initials)>();
            // A DERIVED acronym that two customers share identifies neither: "Saudi Cable Company",
            // "Saudi Ceramics Company" and "Saudi Chemical Company" all derive SCC, and without this
            // every document carrying those three letters is a permanent stalemate. A TAUGHT alias
            // is a deliberate human statement about one customer and is never suppressed here.
            var derivedAcronyms = corpus.Customers
                .Select(c => (c.CustomerId, Key: CustomerNameNormalizer.AcronymKey(c.Name)))
                .Where(x => x.Key.Length > 0)
                .GroupBy(x => x.Key, StringComparer.Ordinal)
                .Where(g => g.Select(x => x.CustomerId).Distinct().Count() == 1)
                .ToDictionary(g => g.Key, g => g.First().CustomerId, StringComparer.Ordinal);
            foreach (var customer in corpus.Customers)
            {
                var key = CustomerNameNormalizer.LooseKey(customer.Name);
                // Two words and eight characters was written for "Saudi Electricity Company" and it
                // makes the commonest buyer names in the country invisible: SABIC, NEOM, Marafiq,
                // Sadara, SATORP, SAMREF, Ma'aden. A Saudi buyer's trade name IS one word, and a
                // one-word name of four characters or more that is not an ordinary word is as
                // distinctive as any two-word one — "Marafiq" names exactly one company.
                var oneWord = key.Split(' ').Length == 1;
                var scannable = oneWord
                    ? key.Length >= 4 && !CustomerNameNormalizer.IsAmbiguousAcronym(key)
                    : key.Length >= 8;
                if (scannable && !guarded.IsSelfName(key))
                    namesToFind.Add((customer.CustomerId, key, customer.Name, false, false));
                // "SEC Materials West Plant" names Saudi Electricity Company by its initials, and
                // nobody should have to teach the system that. Derived, never stored: it follows
                // the customer's name wherever the name goes.
                var initials = CustomerNameNormalizer.AcronymKey(customer.Name);
                if (initials.Length > 0 && derivedAcronyms.ContainsKey(initials) && !guarded.IsSelfName(initials))
                    namesToFind.Add((customer.CustomerId, initials, customer.Name, false, true));
            }
            foreach (var identifier in corpus.Identifiers)
            {
                if (!identifier.IsVerified) continue;
                if (identifier.IdentifierType is not (CustomerIdentifierType.Alias or CustomerIdentifierType.CustomerName)) continue;
                if (!CustomerIdentifierSources.TrustedForAutoLink.Contains(identifier.Source, StringComparer.Ordinal)) continue;
                var key = identifier.NormalizedValue;
                var taught = string.Equals(identifier.Source, CustomerIdentifierSources.LeadReviewLearned, StringComparison.Ordinal);
                // A taught alias may be one word ("SEC"); a profile name still needs two.
                if (key.Length < 3 || (!taught && (key.Length < 8 || key.Split(' ').Length < 2)) || guarded.IsSelfName(key)) continue;
                namesToFind.Add((identifier.CustomerId, key, identifier.NormalizedValue, taught, false));
            }

            var passageHits = new List<Hit>();
            for (var index = 0; index < guarded.Passages.Count; index++)
            {
                var passage = guarded.Passages[index];
                // Item text is the only role where a name may be incidental; a header and an
                // address are both statements about an organisation, and are scored alike.
                var aboutTheBuyer = passage.Role is not PassageRole.ItemText;
                foreach (var (customerId, key, display, _, initials) in namesToFind)
                {
                    if (!ContainsWholeWords(passage.Key, key)) continue;
                    var excerpt = passage.Text.Length <= 80 ? passage.Text : passage.Text[..80] + "…";
                    var confidence = initials
                        ? (aboutTheBuyer ? policy.NameAcronymInAddressConfidence : policy.NameAcronymInItemTextConfidence)
                        : (aboutTheBuyer ? policy.NameInAddressConfidence : policy.NameInItemTextConfidence);
                    passageHits.Add(new Hit(customerId, confidence,
                        CustomerMatchReasonCodes.NameInDocument,
                        initials
                            ? $"\"{key}\", the initials of \"{display}\", appears in the {passage.Where}: \"{excerpt}\"."
                            : $"\"{display}\" appears in the {passage.Where}: \"{excerpt}\".",
                        key, index));
                }
            }

            // WHAT THE PASSAGE IS DOING ON THE PAGE decides what its hit is allowed to do.
            // A header names the buyer and links. Item text may mention anyone and only
            // suggests. A delivery address names the CONSIGNEE, which is the buyer on an SEC
            // portal print and is the site owner on an EPC contractor's enquiry — so it links
            // only while nothing else on the page names a different organisation.
            //
            // The page's own claims are read ONCE. A 1,500-line print yields up to 200 passages
            // and a tenant can carry thousands of customers; re-deriving "whose name is in the
            // company-name field" for every hit would re-key the whole customer list per hit.
            var claims = PageIdentityClaims.Read(evidence, guarded, corpus);
            foreach (var hit in SuppressNamesInsideLongerNames(passageHits))
            {
                var role = guarded.Passages[hit.PassageIndex].Role;
                if (role is PassageRole.ItemText)
                {
                    namedInText.Add(hit);
                    continue;
                }
                if (role is PassageRole.BuyerHeader)
                {
                    namedInAddress.Add(hit);
                    continue;
                }

                var customerName = names.TryGetValue(hit.CustomerId, out var known)
                    ? known
                    : $"Customer #{hit.CustomerId}";
                var competitor = claims.CompetesWith(hit.CustomerId, customerName, domainHits);
                if (competitor is null)
                    namedInAddress.Add(hit);
                else
                    consigneeOnly.Add(hit with
                    {
                        Confidence = policy.ShipToDemotedConfidence,
                        Explanation = $"{hit.Explanation} {competitor}"
                    });
            }
        }
        if (Decide(namedInAddress, names, policy, out var namedOutcome))
            return WithContact(namedOutcome!, guarded, corpus);

        var suggestions = new List<Hit>();
        // A name in item text and a contradicted delivery address are both "offer it, do not
        // decide it", so they compete for one row per customer and the stronger statement wins.
        suggestions.AddRange(namedInText.Concat(consigneeOnly)
            .GroupBy(h => h.CustomerId)
            .Select(g => g.OrderByDescending(h => h.Confidence).First()));
        suggestions.AddRange(sharedNetworkHits);

        if (guarded.NameKey.Length > 0)
        {
            foreach (var customer in corpus.Customers)
            {
                if (string.Equals(CustomerNameNormalizer.LooseKey(customer.Name), guarded.NameKey, StringComparison.Ordinal))
                    suggestions.Add(new Hit(customer.CustomerId, policy.ExactNameSuggestionConfidence,
                        CustomerMatchReasonCodes.NameExactUnverified,
                        $"Document names \"{evidence.CustomerCompanyName}\", which matches this client's name."));
            }

            if (suggestions.Count == 0 && guarded.TightNameKey.Length > 2)
            {
                foreach (var customer in corpus.Customers)
                {
                    var tight = CustomerNameNormalizer.TightKey(customer.Name);
                    if (tight.Length == 0) continue;
                    var score = CustomerNameNormalizer.JaroWinkler(guarded.TightNameKey, tight);
                    if (score < policy.FuzzyNameThreshold) continue;
                    var confidence = Math.Min((decimal)score, policy.FuzzyMaximumConfidence);
                    suggestions.Add(new Hit(customer.CustomerId, confidence,
                        CustomerMatchReasonCodes.NameFuzzy,
                        $"\"{evidence.CustomerCompanyName}\" closely resembles this client's name ({score:P0} similar)."));
                }
            }
        }

        foreach (var prior in corpus.PriorSenderResolutions)
        {
            if (guarded.Addresses.Contains(prior.SenderEmail))
                suggestions.Add(new Hit(prior.CustomerId, policy.PriorSenderSuggestionConfidence,
                    CustomerMatchReasonCodes.PriorSender,
                    $"An earlier lead from {prior.SenderEmail} was resolved to this client."));
        }

        foreach (var contact in corpus.Contacts)
        {
            var byAddress = !string.IsNullOrWhiteSpace(contact.Email)
                            && guarded.Addresses.Contains(contact.Email!.Trim().ToLowerInvariant());
            var byPerson = guarded.BuyerPersonKey.Length > 0 && MatchesPerson(contact, guarded.BuyerPersonKey);
            if (!byAddress && !byPerson) continue;
            var who = $"{contact.FirstName} {contact.LastName}".Trim();
            suggestions.Add(new Hit(contact.CustomerId, policy.ContactPersonSuggestionConfidence,
                CustomerMatchReasonCodes.ContactPerson,
                $"Buyer contact {who} is registered against this client."));
        }

        if (!string.IsNullOrWhiteSpace(evidence.RfqNumber))
        {
            // A numbering shape means something only while exactly one customer uses it. Two
            // customers taught the same shape produced two 0.55 suggestions and the ranking then
            // broke the tie on the database id, so the rep was shown the lower primary key dressed
            // as evidence. Production, 2026-09-12: an SEC bid was offered to Saudi Aramco at 55%
            // because Aramco had been taught C-plus-nine-digits from an earlier confirmation.
            var patternHits = new List<Hit>();
            foreach (var identifier in corpus.Identifiers)
            {
                if (identifier.IdentifierType != CustomerIdentifierType.RfqNumberPattern) continue;
                if (!RfqNumberPattern.Matches(identifier.NormalizedValue, evidence.RfqNumber)) continue;
                patternHits.Add(new Hit(identifier.CustomerId, policy.RfqPatternSuggestionConfidence,
                    CustomerMatchReasonCodes.RfqPattern,
                    $"RFQ number {evidence.RfqNumber} follows this client's numbering."));
            }
            if (patternHits.Select(hit => hit.CustomerId).Distinct().Count() == 1)
                suggestions.AddRange(patternHits);
        }

        var candidates = Rank(suggestions, names, policy);
        if (candidates.Count == 0)
            return ClientResolutionOutcome.Unresolved(
                CustomerMatchReasonCodes.NoMatch,
                "Client evidence was found but matched no customer in this tenant.");

        var top = candidates[0];
        return new ClientResolutionOutcome(
            LeadCustomerMatchStatuses.Suggested, null, null,
            top.Confidence, top.ReasonCode, top.Explanation, candidates);
    }

    // ── S0 ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The direction-of-trade and self-identity firewall. The only company name printed on
    /// an SEC bid is OUR OWN ("Vendname: ALI ZAID AL-QURAISHI&amp;PARTNERS", "Vendor Code
    /// 2004414"). Anything that normalises to the tenant's own identity — or to the vendor
    /// block on the document itself — is discarded here and can never reach a tier.
    /// </summary>
    private static GuardedEvidence Guard(LeadClientEvidence evidence)
    {
        var selfNames = evidence.TenantSelfNameKeys
            .Select(CustomerNameNormalizer.LooseKey)
            .Where(key => key.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var supplierKey = CustomerNameNormalizer.LooseKey(evidence.SupplierNameOnDocument);
        if (supplierKey.Length > 0) selfNames.Add(supplierKey);

        var selfDomains = evidence.TenantSelfDomains
            .Select(RoutingValueNormalizer.DomainFromEmail)
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => d!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in evidence.TenantSelfDomains)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (!raw.Contains('@')) selfDomains.Add(raw.Trim().ToLowerInvariant());
        }

        var addresses = new HashSet<string>(StringComparer.Ordinal);
        var domains = new HashSet<string>(StringComparer.Ordinal);
        AddAddress(evidence.SenderEmail);
        AddAddress(evidence.DocumentBuyerEmail);

        var supplierAccountKey = string.IsNullOrWhiteSpace(evidence.SupplierAccountRefOnDocument)
            ? null
            : RoutingValueNormalizer.Normalize(CustomerIdentifierType.ErpAccount, evidence.SupplierAccountRefOnDocument);

        var accounts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in evidence.AccountReferences)
        {
            if (string.IsNullOrWhiteSpace(reference)) continue;
            var normalized = RoutingValueNormalizer.Normalize(CustomerIdentifierType.ErpAccount, reference);
            if (normalized.Length == 0) continue;
            // Our own vendor code at the customer identifies US, never them.
            if (supplierAccountKey is not null && string.Equals(normalized, supplierAccountKey, StringComparison.Ordinal))
                continue;
            accounts.Add(normalized);
        }

        var taxRegistrations = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(evidence.CustomerRegistrationId))
        {
            var normalized = RoutingValueNormalizer.Normalize(
                CustomerIdentifierType.TaxRegistration, evidence.CustomerRegistrationId);
            if (normalized.Length > 0) taxRegistrations.Add(normalized);
        }

        var nameKey = CustomerNameNormalizer.LooseKey(evidence.CustomerCompanyName);
        if (IsSelfName(nameKey)) nameKey = string.Empty;

        var passages = evidence.Passages
            .Where(p => !string.IsNullOrWhiteSpace(p.Text))
            .Select(p => new GuardedPassage(p.Where, p.Text.Trim(), CustomerNameNormalizer.LooseKey(p.Text), p.Role))
            .Where(p => p.Key.Length > 0)
            .Take(200)
            .ToList();

        string? portalAccountKey = null;
        var portalKey = CustomerNameNormalizer.LooseKey(evidence.CustomerPortalName);
        if (portalKey.Length > 0 && supplierAccountKey is { Length: > 0 })
            portalAccountKey = $"{portalKey}|{supplierAccountKey}";

        return new GuardedEvidence(
            addresses, domains, accounts, taxRegistrations, nameKey,
            CustomerNameNormalizer.TightKey(nameKey),
            portalAccountKey,
            CustomerNameNormalizer.LooseKey(evidence.BuyerPersonName),
            passages,
            selfNames);

        bool IsSelfName(string key) => SelfIdentityGuard.IsSelfName(key, selfNames);

        void AddAddress(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return;
            var address = candidate.Trim().ToLowerInvariant();
            var domain = RoutingValueNormalizer.DomainFromEmail(address);
            if (string.IsNullOrWhiteSpace(domain)) return;
            // Nexora's own ingestion labels (extraction@pipeline.local, sec@system.com,
            // manual@upload.com, system@excel.upload) are plumbing, not customers.
            if (SyntheticIdentityGuard.IsSyntheticDomain(domain)) return;
            if (selfDomains.Contains(domain)) return;

            addresses.Add(address);
            // A shared consumer mailbox says nothing about an organisation.
            if (!SyntheticIdentityGuard.IsFreeMailDomain(domain)) domains.Add(domain);
        }
    }

    private static bool MatchesPerson(CustomerContactSnapshot contact, string buyerPersonKey)
    {
        var contactKey = CustomerNameNormalizer.LooseKey($"{contact.FirstName} {contact.LastName}");
        if (contactKey.Length == 0) return false;
        if (string.Equals(contactKey, buyerPersonKey, StringComparison.Ordinal)) return true;

        // SEC prints a department prefix on the buyer ("3C2-AMER AL-DOSSARY"), so a
        // surname-plus-forename containment is the honest comparison. Both parts must
        // appear, and only ever as a SUGGESTION.
        var contactTokens = contactKey.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length > 2).ToArray();
        if (contactTokens.Length < 2) return false;
        var buyerTokens = buyerPersonKey.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        return contactTokens.All(buyerTokens.Contains);
    }

    // ── decision helpers ──────────────────────────────────────────────────────

    private static bool Decide(
        List<Hit> hits,
        IReadOnlyDictionary<long, string> names,
        CustomerResolutionPolicy policy,
        out ClientResolutionOutcome? outcome)
    {
        outcome = null;
        if (hits.Count == 0) return false;

        var distinct = hits.Select(h => h.CustomerId).Distinct().ToArray();
        if (distinct.Length == 1)
        {
            var best = hits.Where(h => h.CustomerId == distinct[0])
                .OrderByDescending(h => h.Confidence)
                .ThenBy(h => ReasonRank(h.ReasonCode))
                .First();

            // AN AUTO-LINK MUST CLEAR THE FLOOR. Every tier that reaches here was written to sit
            // above MinimumAutoLinkConfidence, so today this rejects nothing — which is exactly
            // why it is worth writing down. Until now "Nexora decides at 0.85 and above" existed
            // only as an arithmetic coincidence between the constants each tier happened to pick:
            // lowering one number in CustomerResolutionPolicy, or adding a tier that never read
            // this file, would have started auto-linking leads at 0.70 with nothing anywhere in
            // the engine to stop it. A wrong client on a lead is worse than an unresolved one, so
            // below the floor the same evidence is OFFERED to a person instead of applied for them.
            if (best.Confidence < policy.MinimumAutoLinkConfidence)
            {
                outcome = new ClientResolutionOutcome(
                    LeadCustomerMatchStatuses.Suggested, null, null,
                    best.Confidence, best.ReasonCode, best.Explanation,
                    Rank(hits, names, policy));
                return true;
            }

            outcome = new ClientResolutionOutcome(
                LeadCustomerMatchStatuses.AutoMatchedContactUnresolved,
                best.CustomerId, null, best.Confidence, best.ReasonCode, best.Explanation,
                Rank(hits, names, policy));
            return true;
        }

        var candidates = Rank(hits, names, policy);
        // Nothing was decided, so nothing is 95% certain. Reporting the winning candidate's
        // confidence on a refusal made two customers on one corporate domain read as
        // "AMBIGUOUS at 95%", and the sentence named neither of them nor the evidence.
        var tied = string.Join(" and ", candidates.Take(3).Select(candidate => candidate.CustomerName));
        outcome = new ClientResolutionOutcome(
            LeadCustomerMatchStatuses.Ambiguous, null, null,
            0m,
            CustomerMatchReasonCodes.Ambiguous,
            candidates.Count > 0
                ? $"{tied} match the same evidence ({candidates[0].Explanation}) Pick one."
                : $"{distinct.Length} clients share this evidence; a person must choose.",
            candidates);
        return true;
    }

    private static IReadOnlyList<ClientMatchCandidate> Rank(
        IEnumerable<Hit> hits,
        IReadOnlyDictionary<long, string> names,
        CustomerResolutionPolicy policy)
        => hits
            .GroupBy(h => h.CustomerId)
            .Select(group => group
                .OrderByDescending(h => h.Confidence)
                .ThenBy(h => ReasonRank(h.ReasonCode))
                .First())
            .OrderByDescending(h => h.Confidence)
            .ThenBy(h => ReasonRank(h.ReasonCode))
            .ThenBy(h => h.CustomerId)
            .Take(Math.Max(1, policy.MaximumCandidates))
            .Select((h, index) => new ClientMatchCandidate(
                index + 1, h.CustomerId,
                names.TryGetValue(h.CustomerId, out var name) ? name : $"Customer #{h.CustomerId}",
                h.Confidence, h.ReasonCode, h.Explanation))
            .ToList();

    /// <summary>
    /// A contact is resolved ONLY by an exact address match inside the chosen customer, and
    /// only when it is unambiguous. Nothing here touches Contacts.IsPrimary — the partial
    /// unique index on primary contacts is a human's decision to make.
    /// </summary>
    private static ClientResolutionOutcome WithContact(
        ClientResolutionOutcome outcome, GuardedEvidence guarded, ClientResolutionCorpus corpus)
    {
        if (!outcome.CustomerId.HasValue) return outcome;

        var matches = corpus.Contacts
            .Where(c => c.CustomerId == outcome.CustomerId!.Value
                        && !string.IsNullOrWhiteSpace(c.Email)
                        && guarded.Addresses.Contains(c.Email!.Trim().ToLowerInvariant()))
            .Select(c => c.ContactId)
            .Distinct()
            .ToArray();

        return matches.Length == 1
            ? outcome with { Status = LeadCustomerMatchStatuses.AutoMatched, ContactId = matches[0] }
            : outcome with { Status = LeadCustomerMatchStatuses.AutoMatchedContactUnresolved, ContactId = null };
    }

    private static int ReasonRank(string reasonCode) => reasonCode switch
    {
        CustomerMatchReasonCodes.SenderEmailExact => 0,
        CustomerMatchReasonCodes.ErpAccountExact => 1,
        CustomerMatchReasonCodes.TaxRegExact => 2,
        CustomerMatchReasonCodes.SenderDomain => 3,
        CustomerMatchReasonCodes.LearnedPortalAccount => 4,
        CustomerMatchReasonCodes.LearnedAlias => 5,
        CustomerMatchReasonCodes.NameInDocument => 6,
        CustomerMatchReasonCodes.NameExactUnverified => 7,
        CustomerMatchReasonCodes.PriorSender => 8,
        CustomerMatchReasonCodes.ContactPerson => 9,
        CustomerMatchReasonCodes.NameFuzzy => 10,
        CustomerMatchReasonCodes.RfqPattern => 11,
        _ => 99
    };

    /// <param name="MatchedKey">
    /// For a passage hit: the normalised name key that actually matched, so a longer name can be
    /// seen to swallow a shorter one. Empty for every other tier.
    /// </param>
    /// <param name="PassageIndex">
    /// For a passage hit: which passage it came from, so both the longest-name rule and the
    /// consignee rule can ask what that passage was DOING on the page. -1 for every other tier.
    /// </param>
    private sealed record Hit(
        long CustomerId,
        decimal Confidence,
        string ReasonCode,
        string Explanation,
        string MatchedKey = "",
        int PassageIndex = -1);

    /// <summary>
    /// One statement off the document, already normalised. It carries the ROLE and not the old
    /// NamesTheBuyer boolean: one flag was answering two different questions ("is this about the
    /// buyer" and "where do the goods go"), and keeping both copies around is how the two answers
    /// drift apart. DocumentPassage derives Role from the flag, so a caller that has not been
    /// updated behaves exactly as it did.
    /// </summary>
    private sealed record GuardedPassage(string Where, string Text, string Key, PassageRole Role);

    private sealed record GuardedEvidence(
        HashSet<string> Addresses,
        HashSet<string> Domains,
        HashSet<string> AccountReferences,
        HashSet<string> TaxRegistrations,
        string NameKey,
        string TightNameKey,
        string? PortalAccountKey,
        string BuyerPersonKey,
        IReadOnlyList<GuardedPassage> Passages,
        HashSet<string> SelfNameKeys)
    {
        public bool IsEmpty =>
            Addresses.Count == 0 && Domains.Count == 0 && AccountReferences.Count == 0 &&
            TaxRegistrations.Count == 0 && NameKey.Length == 0 &&
            string.IsNullOrEmpty(PortalAccountKey) && BuyerPersonKey.Length == 0 && Passages.Count == 0;

        public bool IsSelfName(string key) => SelfIdentityGuard.IsSelfName(key, SelfNameKeys);
    }

    /// <summary>
    /// THE LONGEST NAME IN A PASSAGE IS THE ONE THE DOCUMENT MEANS.
    ///
    /// A delivery address reading "Saudi Aramco Total Refining and Petrochemical Company, Jubail"
    /// contains "SAUDI ARAMCO" as whole words, so with both companies on the books one address
    /// matched two customers and the lead went AMBIGUOUS — and with only the parent on the books
    /// it linked to Aramco outright. SATORP is a separate joint venture with its own vendor
    /// registration, its own payment terms and its own portal; an invoice sent to Aramco against
    /// a SATORP order is simply not paid. The same shape covers the whole family, because every
    /// one of them is a joint venture that put the parent's name in its own: SAMREF (Aramco and
    /// Mobil), YASREF (Aramco and Sinopec), Luberef, Sadara (Aramco and Dow). It settles "Royal
    /// Commission Jubail" against a bare "Royal Commission" the same way — the industrial-city
    /// authority and the parent body are different buyers with different budgets.
    ///
    /// Applied only INSIDE ONE PASSAGE, because "the document wrote one name and we read two" is
    /// a statement about one sentence. Two names in two different passages are two statements and
    /// both deserve to be heard.
    /// </summary>
    private static List<Hit> SuppressNamesInsideLongerNames(List<Hit> passageHits)
    {
        if (passageHits.Count < 2) return passageHits;

        var kept = new List<Hit>(passageHits.Count);
        foreach (var group in passageHits.GroupBy(hit => hit.PassageIndex))
        {
            // Comparisons stay inside one passage, and a passage only ever holds the handful of
            // customer names that genuinely appear in it, so this never walks the whole corpus.
            var inPassage = group.ToList();
            foreach (var hit in inPassage)
            {
                // STRICTLY longer, and spelled out in whole words: two customers whose names
                // produce the same key are still ambiguous, and "SEC" is not swallowed by
                // "SAUDI ELECTRICITY" because the initials are not inside that name.
                var shadowed = inPassage.Any(other =>
                    other.MatchedKey.Length > hit.MatchedKey.Length
                    && ContainsWholeWords(other.MatchedKey, hit.MatchedKey));
                if (!shadowed) kept.Add(hit);
            }
        }
        return kept;
    }

    /// <summary>
    /// A DELIVERY ADDRESS NAMES THE CONSIGNEE, which is usually — not always — the buyer.
    ///
    /// On a Saudi Electricity Company portal print the delivery address is the ONLY place the
    /// buyer's name appears anywhere: production lead 680 carried "Saudi Electricity
    /// Company-DAMMAM" and nothing else — no sender domain, no company-name field, no portal
    /// name, no supplier block — and it has to link on that alone. On an EPC contractor's enquiry
    /// the identical field names the site owner: Hyundai Engineering and Construction sends from
    /// hdec.com, prints its own name at the top of the page and "Deliver to: Saudi Aramco Ras
    /// Tanura Refinery" in the address. Reading that consignee as the buyer links the lead to
    /// Aramco, who is buying nothing on this job, has no contract with us for it, and whose
    /// payment terms and quote would then be the ones the rep works to.
    ///
    /// So a consignee links only while the page names no COMPETING organisation. This is what
    /// the page itself claims about who is buying, read once per lead.
    /// </summary>
    private sealed record PageIdentityClaims(
        string? CompetingDomain,
        HashSet<long> NameKeyOwners,
        string? BuyerNameOnDocument)
    {
        public static PageIdentityClaims Read(
            LeadClientEvidence evidence, GuardedEvidence guarded, ClientResolutionCorpus corpus)
        {
            // A procurement portal relays mail on behalf of every buyer on its network, so its
            // domain names no organisation at all and cannot compete with one. Without this, an
            // ordinary SEC enquiry that happened to arrive through Ariba would be demoted to a
            // suggestion because "ansmtp.ariba.com is not Saudi Electricity Company" — true, and
            // beside the point: the postman is not a rival buyer.
            var competingDomain = guarded.Domains
                .FirstOrDefault(domain => !SyntheticIdentityGuard.IsPortalRelayDomain(domain));

            // The company-name field is a statement about who is buying. It belongs to a customer
            // when it IS their name, or an alias a person verified against them.
            var owners = new HashSet<long>();
            if (guarded.NameKey.Length > 0)
            {
                foreach (var customer in corpus.Customers)
                    if (string.Equals(CustomerNameNormalizer.LooseKey(customer.Name), guarded.NameKey, StringComparison.Ordinal))
                        owners.Add(customer.CustomerId);
                foreach (var identifier in corpus.Identifiers)
                    if (identifier.IsVerified
                        && identifier.IdentifierType is CustomerIdentifierType.Alias or CustomerIdentifierType.CustomerName
                        && string.Equals(identifier.NormalizedValue, guarded.NameKey, StringComparison.Ordinal))
                        owners.Add(identifier.CustomerId);
            }

            return new PageIdentityClaims(
                competingDomain,
                owners,
                guarded.NameKey.Length > 0 ? evidence.CustomerCompanyName?.Trim() : null);
        }

        /// <summary>
        /// The sentence explaining what on this page disagrees with linking to this customer, or
        /// null when nothing does. Both facts, in the words a rep would use.
        /// </summary>
        public string? CompetesWith(long customerId, string customerName, List<Hit> domainHits)
        {
            // domainHits is empty by the time the passage scan runs — S2 returns whenever a
            // sender domain matches anybody — but the question is "does this domain point at
            // THIS customer", and asking it of the hits keeps the answer right if the tier
            // order ever changes.
            if (CompetingDomain is not null && !domainHits.Any(hit => hit.CustomerId == customerId))
                return $"But this document is from {CompetingDomain}, which is not {customerName}.";

            if (BuyerNameOnDocument is not null && !NameKeyOwners.Contains(customerId))
                return $"But the document names \"{BuyerNameOnDocument}\" as the buying " +
                       $"organisation, which is not {customerName}.";

            return null;
        }
    }

    /// <summary>"SAUDI ELECTRICITY COMPANY" inside "SAUDI ELECTRICITY COMPANY JIZAN AREA", as whole words — never inside another word.</summary>
    internal static bool ContainsWholeWords(string passageKey, string nameKey)
    {
        if (nameKey.Length == 0 || passageKey.Length < nameKey.Length) return false;
        var padded = " " + passageKey + " ";
        return padded.Contains(" " + nameKey + " ", StringComparison.Ordinal);
    }
}
