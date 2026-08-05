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
                case CustomerIdentifierType.ErpAccount when guarded.AccountReferences.Contains(identifier.NormalizedValue):
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
                learnedHits.Add(new Hit(identifier.CustomerId, policy.LearnedPortalAccountConfidence,
                    CustomerMatchReasonCodes.LearnedPortalAccount,
                    $"Same portal vendor code {evidence.SupplierAccountRefOnDocument} on {evidence.CustomerPortalName}."));
            }
        }
        if (Decide(learnedHits, names, policy, out var learnedOutcome))
            return WithContact(learnedOutcome!, guarded, corpus);

        // ── S4..S6 READ-ONLY SUGGESTION SCANS ─────────────────────────────────
        // Never link. Two distinct Gulf legal entities routinely share a trade name; a
        // first-time collision is not evidence, a human-confirmed one (S3) is.
        var suggestions = new List<Hit>();

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
            foreach (var identifier in corpus.Identifiers)
            {
                if (identifier.IdentifierType != CustomerIdentifierType.RfqNumberPattern) continue;
                if (!RfqNumberPattern.Matches(identifier.NormalizedValue, evidence.RfqNumber)) continue;
                suggestions.Add(new Hit(identifier.CustomerId, policy.RfqPatternSuggestionConfidence,
                    CustomerMatchReasonCodes.RfqPattern,
                    $"RFQ number {evidence.RfqNumber} follows this client's numbering."));
            }
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

        string? portalAccountKey = null;
        var portalKey = CustomerNameNormalizer.LooseKey(evidence.CustomerPortalName);
        if (portalKey.Length > 0 && supplierAccountKey is { Length: > 0 })
            portalAccountKey = $"{portalKey}|{supplierAccountKey}";

        return new GuardedEvidence(
            addresses, domains, accounts, taxRegistrations, nameKey,
            CustomerNameNormalizer.TightKey(nameKey),
            portalAccountKey,
            CustomerNameNormalizer.LooseKey(evidence.BuyerPersonName));

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
            outcome = new ClientResolutionOutcome(
                LeadCustomerMatchStatuses.AutoMatchedContactUnresolved,
                best.CustomerId, null, best.Confidence, best.ReasonCode, best.Explanation,
                Rank(hits, names, policy));
            return true;
        }

        var candidates = Rank(hits, names, policy);
        outcome = new ClientResolutionOutcome(
            LeadCustomerMatchStatuses.Ambiguous, null, null,
            candidates.Count > 0 ? candidates[0].Confidence : 0m,
            CustomerMatchReasonCodes.Ambiguous,
            $"{distinct.Length} clients share this evidence; a person must choose.",
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
        CustomerMatchReasonCodes.NameExactUnverified => 6,
        CustomerMatchReasonCodes.PriorSender => 7,
        CustomerMatchReasonCodes.ContactPerson => 8,
        CustomerMatchReasonCodes.NameFuzzy => 9,
        CustomerMatchReasonCodes.RfqPattern => 10,
        _ => 99
    };

    private sealed record Hit(long CustomerId, decimal Confidence, string ReasonCode, string Explanation);

    private sealed record GuardedEvidence(
        HashSet<string> Addresses,
        HashSet<string> Domains,
        HashSet<string> AccountReferences,
        HashSet<string> TaxRegistrations,
        string NameKey,
        string TightNameKey,
        string? PortalAccountKey,
        string BuyerPersonKey)
    {
        public bool IsEmpty =>
            Addresses.Count == 0 && Domains.Count == 0 && AccountReferences.Count == 0 &&
            TaxRegistrations.Count == 0 && NameKey.Length == 0 &&
            string.IsNullOrEmpty(PortalAccountKey) && BuyerPersonKey.Length == 0;
    }
}
