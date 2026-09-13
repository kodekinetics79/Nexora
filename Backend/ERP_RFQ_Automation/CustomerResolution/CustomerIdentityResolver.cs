using System.Collections.Concurrent;
using System.Globalization;
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
    /// <param name="tenantSharedAcronyms">
    /// Initials that two or more of the tenant's active customers derive, counted over the WHOLE
    /// tenant (build it with <see cref="SharedDerivedAcronyms"/>). Null means "count them in the
    /// corpus", which is exact only while the corpus holds every customer. Above
    /// <see cref="CustomerResolutionPolicy.MaximumNameScanRows"/> the loader narrows the customer
    /// list by leading letters, and a customer that shared the initials but was filtered out no
    /// longer counted: "SCC Store, Sudair Industrial City" loaded Sudair Ceramics and not Saudi
    /// Cable, SCC looked unique, and a large tenant auto-linked Sudair Ceramics at 0.85 while a
    /// small tenant left the same document unresolved. The answer must not depend on how many
    /// customers a tenant has. What is supplied is added to what the corpus shows, never
    /// substituted for it, so a stale set can only make the engine more careful.
    /// </param>
    public static ClientResolutionOutcome Resolve(
        LeadClientEvidence evidence,
        ClientResolutionCorpus corpus,
        CustomerResolutionPolicy policy,
        IReadOnlySet<string>? tenantSharedAcronyms = null)
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

        // Initials two customers share, over the corpus and the tenant-wide set. Worked out on first use:
        // a lead decided at S1 or S2 never needs them.
        HashSet<string>? sharedAcronymsCache = null;
        HashSet<string> SharedAcronyms()
        {
            if (sharedAcronymsCache is not null) return sharedAcronymsCache;
            sharedAcronymsCache = new HashSet<string>(SharedDerivedAcronyms(corpus.Customers), StringComparer.Ordinal);
            if (tenantSharedAcronyms is not null) sharedAcronymsCache.UnionWith(tenantSharedAcronyms);
            return sharedAcronymsCache;
        }

        // The learner's name rules over the customers on the books today, also worked out on first use.
        CustomerAliasLearner.NameReadings? readingsCache = null;
        CustomerAliasLearner.NameReadings Readings() => readingsCache ??= new CustomerAliasLearner.NameReadings(corpus.Customers);

        // A TAUGHT NAME MUST STILL NAME ITS OWNER WHEN IT IS READ. The learner's gate runs only at the
        // moment of teaching, and every tier here trusted a taught alias for ever. Rows poisoned before the
        // gate existed ("SAUDI ELECTRICITY" put on Saudi Aramco by a mis-click), names taught before the
        // right customer was created, and initials taught while they were unique ("SCC" for Saudi Cable,
        // before Saudi Ceramics was added) all kept linking. So a taught name gives way when its initials
        // are now shared, or when another customer on the books reads it at least as closely as its owner
        // does, by the learner's own tiers. Poisoned rows become inert with no data job, and the answer no
        // longer depends on the order customers were created. Rows a person typed are not re-judged.
        var givesWay = new Dictionary<(long CustomerId, string Key), bool>();
        bool TaughtNameGivesWay(long customerId, string key)
        {
            if (givesWay.TryGetValue((customerId, key), out var known)) return known;
            var answer = SharedAcronyms().Contains(key) || Readings().AnotherCustomerReadsAtLeastAsClosely(key, customerId);
            givesWay[(customerId, key)] = answer;
            return answer;
        }

        // TWO LETTERS NAME A COMPANY ONLY WHERE THEY ARE ITS OWN INITIALS AND NOBODY ELSE'S. The exact
        // learned-alias tier took the passage scan's distinctiveness test, which wants a word of three
        // letters, so "GE" taught for General Electric Saudi Arabia stopped linking a company-name field
        // that says exactly "GE" (0.90 before). The field must be those two capitals and nothing else, the
        // letters must be the initials of the owner's first two name words, and no other customer on the
        // books may carry the same initials: two letters shared by two customers identify neither.
        Dictionary<string, int>? leadingInitialsCounts = null;
        bool IsOwnersOwnTwoLetterInitials(CustomerIdentifierSnapshot identifier)
        {
            var key = identifier.NormalizedValue;
            if (key.Length != 2 || !key.All(c => c is >= 'A' and <= 'Z')) return false;
            var printed = (evidence.CustomerCompanyName ?? string.Empty).Where(char.IsLetter).ToArray();
            if (printed.Length != 2 || !printed.All(c => c is >= 'A' and <= 'Z')) return false;
            if (!names.TryGetValue(identifier.CustomerId, out var ownerName)
                || !string.Equals(LeadingTwoWordInitials(ownerName), key, StringComparison.Ordinal)) return false;
            leadingInitialsCounts ??= corpus.Customers
                .GroupBy(customer => customer.CustomerId)
                .Select(group => LeadingTwoWordInitials(group.First().Name))
                .Where(initials => initials.Length == 2)
                .GroupBy(initials => initials, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            return leadingInitialsCounts.TryGetValue(key, out var owners) && owners == 1 && !SharedAcronyms().Contains(key);
        }

        // ── S1 AUTHORITATIVE EXACT ────────────────────────────────────────────
        var authoritative = new List<Hit>();
        foreach (var identifier in corpus.Identifiers)
        {
            // A ROW THE LEARNER REFUSED TO TRUST IS NOT A FACT. CustomerAliasLearner writes
            // LeadReviewUnverified for a mailbox on a domain nobody tied to the chosen customer, so
            // that a person can look at it. This tier never read Source at all, so the demoted row
            // still linked every later mail from that address at 1.00, and nothing on the page could
            // outvote it.
            //
            // NOR IS A ROW A PERSON MARKED UNVERIFIED. Unticking "verified" on the setup screen is the
            // one correction an administrator can make to a wrong fact today, and routing's engine
            // has always honoured it. This tier did not: 57322@se.com.sa unverified against Saudi
            // Aramco still linked SEC's mail to Aramco at 1.00 here, while routing refused the same
            // row one step later. Every writer of a real fact sets the flag (profile sync, contact
            // sync, the migration backfill, the setup screen's default, the learner's trusted rows),
            // so only a deliberate "not verified" is affected.
            if (!identifier.IsVerified || IsUnverifiedLearned(identifier)) continue;
            switch (identifier.IdentifierType)
            {
                // A RELAY'S OWN SENDING ADDRESS IS THE POSTMAN. ordersender-prod@ansmtp.ariba.com delivers
                // every Ariba buyer's RFQ, and the old learner minted it as the Email of whichever buyer
                // was confirmed first: a SABIC RFQ then linked to Saudi Aramco here at 1.00, however
                // plainly the page named SABIC. On a relay a row names one buyer only where a person put
                // it there; routing asks the same predicate.
                case CustomerIdentifierType.Email when guarded.Addresses.Contains(identifier.NormalizedValue)
                                                      && IdentityDomainGuard.MayMatchExactAddress(identifier.NormalizedValue, identifier.Source):
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
        // guarded.Domains holds only domains IdentityDomainGuard accepts as an organisation's own,
        // and a row the learner demoted to LeadReviewUnverified, or a person marked unverified, is
        // skipped for the same reasons as at S1: it links at 0.95, which asks nobody.
        //
        // A SYSTEM MAILBOX IS THE POSTMAN ON WHATEVER HOST IT SITS, FOR A DOMAIN AS FOR AN ADDRESS. S1 refuses a
        // learned no-reply@etimad.gov.sa row, but a learned Domain row etimad.gov.sa (or coupa.com, or sap.com
        // for SAP Ariba's no-reply@sap.com) still linked every SEC print the portal carried to Saudi Aramco here
        // at 0.95, because no list of relay hosts is ever complete. A domain that reaches this tier only through
        // a system mailbox (IdentityDomainGuard.IsSystemMailbox) is matched only by a row a person entered; a
        // person's own mailbox on the same host still matches every row, as before. Routing and the corpus
        // loader ask the same question.
        var domainHits = corpus.Identifiers
            .Where(i => i.IdentifierType == CustomerIdentifierType.Domain
                        && i.IsVerified
                        && !IsUnverifiedLearned(i)
                        && guarded.Domains.Contains(i.NormalizedValue)
                        && (guarded.PersonMailboxDomains.Contains(i.NormalizedValue)
                            || CustomerIdentifierSources.EnteredByAPerson.Contains(i.Source, StringComparer.Ordinal)))
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
            // The distinctiveness test the passage scan applies below holds here too. The extractor that
            // took "SAUDI ARABIA" off one address block takes it off the next buyer's print as well, and
            // the alias taught on the first lead linked a Saudi Kayan RFQ to Saudi Aramco at 0.90.
            if (isNameType && guarded.NameKey.Length > 0 &&
                string.Equals(identifier.NormalizedValue, guarded.NameKey, StringComparison.Ordinal) &&
                (CustomerNameDistinctiveness.HasDistinctiveToken(identifier.NormalizedValue)
                 || IsOwnersOwnTwoLetterInitials(identifier)) &&
                // "SCC" taught for Saudi Cable linked the company-name field "SCC" at 0.90 here long after
                // Saudi Ceramics, whose initials they also are, was added. See TaughtNameGivesWay.
                !(IsTaught(identifier) && TaughtNameGivesWay(identifier.CustomerId, identifier.NormalizedValue)))
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
                    $"Same portal vendor code {evidence.SupplierAccountRefOnDocument} on {evidence.CustomerPortalName}.",
                    Taught: IsTaught(identifier)));
            }
        }
        // A TAUGHT PORTAL PAIR IS WHAT A PERSON PICKED BEFORE, AND THE PAGE IN FRONT OF US MAY SAY OTHERWISE (T24,
        // the 55% incident). This tier decided before any passage was read, so a pair a rep taught onto Saudi Aramco
        // (two pair-only mis-clicks, or a row taught before the learner's portal guard existed) linked every later
        // SEC e-bidding print to Aramco at 0.92, over "Saudi Electricity Company-DAMMAM" in its address and over
        // "SEC Materials West Plant" in its storage location. A decision resting only on taught pairs waits for the
        // page: where a header or an address names a different customer at link strength, both are offered
        // instead (see the passage tier). A pair a person entered, a taught alias, or a pair the page agrees with
        // still decides here, and a page with no passages costs nothing.
        Hit? taughtPairAwaitingThePage = null;
        ClientResolutionOutcome? taughtPairOutcome = null;
        if (Decide(learnedHits, names, policy, out var learnedOutcome))
        {
            if (learnedOutcome!.CustomerId is long pairOwner
                && guarded.Passages.Count > 0
                && learnedHits.Where(hit => hit.CustomerId == pairOwner)
                    .All(hit => hit.Taught && hit.ReasonCode == CustomerMatchReasonCodes.LearnedPortalAccount))
            {
                taughtPairAwaitingThePage = learnedHits.First(hit => hit.CustomerId == pairOwner);
                taughtPairOutcome = learnedOutcome;
            }
            else
            {
                return WithContact(learnedOutcome!, guarded, corpus);
            }
        }

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
        // The organisation a contradicting sender domain belongs to, offered beside the consignee.
        var rivalOffers = new List<Hit>();
        if (guarded.Passages.Count > 0)
        {
            var namesToFind = new List<NameToFind>();
            var queued = new HashSet<(long, string, bool)>();
            void Find(long customerId, string key, string display, bool initials, bool oneWordName, bool taught = false,
                IReadOnlyList<string>? writtenWords = null)
            {
                if (queued.Add((customerId, key, initials)))
                {
                    namesToFind.Add(new NameToFind(customerId, key, display, initials, oneWordName, taught, writtenWords));
                }
                else if (taught)
                {
                    // The customer's own one-word name taught back as an alias ("MARAFIQ" for a customer recorded as
                    // Marafiq) was queued first as an untaught name and dropped here, so the taught rules never saw it.
                    var at = namesToFind.FindIndex(existing => existing.CustomerId == customerId && existing.Initials == initials
                                                               && string.Equals(existing.Key, key, StringComparison.Ordinal));
                    if (at >= 0 && !namesToFind[at].Taught) namesToFind[at] = namesToFind[at] with { Taught = true };
                }
            }

            // A DERIVED acronym that two customers share identifies neither: "Saudi Cable Company",
            // "Saudi Ceramics Company" and "Saudi Chemical Company" all derive SCC, and without this
            // every document carrying those three letters is a permanent stalemate. A TAUGHT alias is
            // not dropped here but checked again once it is found (TaughtNameGivesWay): taught while it
            // was unique, it is not unique for ever.
            var sharedAcronyms = SharedAcronyms();
            var derivedAcronyms = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var customer in corpus.Customers)
            {
                var acronym = FactsOf(customer.Name).Initials;
                if (acronym.Length > 0 && !sharedAcronyms.Contains(acronym))
                    derivedAcronyms.TryAdd(acronym, customer.CustomerId);
            }

            foreach (var customer in corpus.Customers)
            {
                // What the name yields is a pure function of the name, worked out once per name and remembered
                // (FactsOf): above the name-scan cap this loop runs over two thousand customers on every lead.
                var facts = FactsOf(customer.Name);
                foreach (var spelling in facts.Spellings)
                {
                    // Two words and eight characters was written for "Saudi Electricity Company" and it
                    // makes the commonest buyer names in the country invisible: SABIC, NEOM, Marafiq,
                    // Sadara, SATORP, SAMREF, Ma'aden. A Saudi buyer's trade name IS one word, and a
                    // one-word name of four characters or more that is not an ordinary word is as
                    // distinctive as any two-word one — "Marafiq" names exactly one company. What a
                    // one-word hit may DO is narrower than a full name; see the ship-to rule below.
                    // (Scannable is that rule, worked out in FactsOf.)
                    var key = spelling.Key;
                    var oneWord = !key.Contains(' ');
                    // A name made only of words half the country carries ("Arabian International
                    // Company" keys to ARABIAN) would be found in every "Arabian Pipes Company" and
                    // "Arabian Gulf Road" on the page. The same test the taught-alias scan applies.
                    if (!spelling.Scannable || guarded.IsSelfName(key)) continue;
                    if (spelling.Distinctive)
                        Find(customer.CustomerId, key, customer.Name, initials: false, oneWordName: oneWord);
                    // BUT A LONG NAME MADE OF GENERIC WORDS IS STILL A NAME WHEN IT IS WRITTEN OUT WHOLE. The
                    // distinctiveness filter made "Arabian Gulf International Company" invisible, so its own
                    // yard address stopped linking (0.88 before) and a trading house named after the city in
                    // that address was offered instead. Its key is only "ARABIAN GULF" (LooseKey drops
                    // INTERNATIONAL), which "Arabian Gulf Road" also carries, so such a name is heard only
                    // where the page writes every word of it, and a run into further words ("... Trading Co.")
                    // is another company. One- and two-word names and taught aliases keep the filter.
                    else if (spelling.WrittenWords is { } written)
                        Find(customer.CustomerId, key, customer.Name, initials: false, oneWordName: false, writtenWords: written);
                }
                // "SEC Materials West Plant" names Saudi Electricity Company by its initials, and
                // nobody should have to teach the system that. Derived, never stored: it follows
                // the customer's name wherever the name goes.
                var initials = facts.Initials;
                if (initials.Length > 0
                    && derivedAcronyms.TryGetValue(initials, out var initialsOwner)
                    && initialsOwner == customer.CustomerId
                    && !guarded.IsSelfName(initials))
                    Find(customer.CustomerId, initials, customer.Name, initials: true, oneWordName: false);
            }
            foreach (var identifier in corpus.Identifiers)
            {
                if (!identifier.IsVerified) continue;
                if (identifier.IdentifierType is not (CustomerIdentifierType.Alias or CustomerIdentifierType.CustomerName)) continue;
                if (!CustomerIdentifierSources.TrustedForAutoLink.Contains(identifier.Source, StringComparer.Ordinal)) continue;
                var key = identifier.NormalizedValue;
                var taught = string.Equals(identifier.Source, CustomerIdentifierSources.LeadReviewLearned, StringComparison.Ordinal);
                // A TAUGHT ALIAS MUST STILL NAME ONE COMPANY. This scan accepted any taught alias of
                // three characters, and the learner taught whatever the company-name field held: the
                // extractor took the country line of an address block, a reviewer rightly linked the
                // lead to Saudi Aramco, and "SAUDI ARABIA" became an Aramco alias. From then on every
                // delivery address and every buyer sentence that said "Saudi Arabia" linked to Aramco
                // at 0.88 — a Saudi Kayan RFQ included. The alias is kept; the scan simply does not
                // use a name with no word in it that belongs to one company.
                if (!CustomerNameDistinctiveness.HasDistinctiveToken(key)) continue;
                // A taught alias may be one word ("SEC"); a profile name still needs two.
                if ((!taught && (key.Length < 8 || key.Split(' ').Length < 2)) || guarded.IsSelfName(key)) continue;
                // The customer's own one-word name taught back as an alias is still that one word,
                // and gets exactly the same, narrower, rights as when it is read off the name.
                //
                // SO DOES ONE WORD OF THE NAME. The learner now trusts a print whose every distinctive
                // word is the customer's own, so "YANBU" confirmed once for Yanbu Cement Company is a
                // verified alias, and so is "ELECTRICITY" for Saudi Electricity Company. Read with full
                // rights, the first put Yanbu Cement beside SEC in "Saudi Electricity Company-YANBU"
                // and lead 680's shape went AMBIGUOUS again, the #1 defect back through learning; the
                // second pushed the one-word name Marafiq out of "Marafiq Power & Electricity Plant"
                // and linked the page to SEC. One word of a name in an address is a place or a sector
                // as often as a company. Initials are not this ("SEC" is a deliberate abbreviation),
                // and neither is a trade name that is no word of the legal name.
                var oneWordName = !key.Contains(' ')
                    && names.TryGetValue(identifier.CustomerId, out var ownName)
                    && (string.Equals(CustomerNameNormalizer.LooseKey(ownName), key, StringComparison.Ordinal)
                        || IsOneWordOfTheName(key, ownName));
                Find(identifier.CustomerId, key, identifier.NormalizedValue, initials: false, oneWordName, taught: IsTaught(identifier));
            }

            // A name is in a passage only if its first word is a whole word of that passage, so a name whose first word
            // is nowhere on the page is never scanned, and each passage key is padded once rather than once per name.
            // The same whole-word test as ContainsWholeWords. On the 1,500-line print with two thousand customers the
            // scan compared every name with every one of 120 passages and allocated a string for each comparison.
            var pageWords = new HashSet<string>(StringComparer.Ordinal);
            foreach (var guardedPassage in guarded.Passages)
                foreach (var word in guardedPassage.Key.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    pageWords.Add(word);
            var namesOnThePage = namesToFind.Where(name => pageWords.Contains(name.FirstWord)).ToList();

            var passageHits = new List<Hit>();
            for (var index = 0; index < guarded.Passages.Count; index++)
            {
                var passage = guarded.Passages[index];
                var paddedPassageKey = " " + passage.Key + " ";
                // Item text is the only role where a name may be incidental; a header and an
                // address are both statements about an organisation, and are scored alike.
                var aboutTheBuyer = passage.Role is not PassageRole.ItemText;
                var onTheMailbox = string.Equals(passage.Where, DocumentPassage.SenderDisplayNameWhere, StringComparison.Ordinal);
                foreach (var name in namesOnThePage)
                {
                    if (!paddedPassageKey.Contains(name.PaddedKey, StringComparison.Ordinal)) continue;
                    if (name.WrittenWords is not null && !WritesTheNameInFull(passage.Text, name.WrittenWords)) continue;
                    // ONE WORD OF A NAME ON A MAILBOX IS A PERSON OR A DEPARTMENT. "Al-Rashid Trading" keys to RASHID and
                    // "Al Dammam Trading Co." to DAMMAM, so "Rashid <rashid1987@gmail.com>" offered Al-Rashid Trading and
                    // SEC's own "Dammam Procurement <procurement@se.com.sa>" offered Al Dammam Trading at 0.70, ranked
                    // above SEC's own earlier confirmation of that very mailbox (0.65); a relay's "RASHID" even linked at
                    // 0.88. Base read nothing there. On the mailbox such a name is not heard where the display name is
                    // that one word alone, or the word is followed by a buying function ("Dammam Procurement", "Ahmed
                    // Rashid, Procurement"), unless the display name is the customer's whole written name ("SABIC",
                    // "Marafiq", "Al-Rashid Trading"). A person's fuller name ("Rashid Al-Otaibi") is still a mention.
                    if (onTheMailbox && name.OneWordName
                        && OneWordOnTheMailboxIsAPersonOrADepartment(
                            passage.Text, name.Key, names.TryGetValue(name.CustomerId, out var ownName) ? ownName : null))
                        continue;
                    var excerpt = passage.Text.Length <= 80 ? passage.Text : passage.Text[..80] + "…";
                    var confidence = name.Initials
                        ? (aboutTheBuyer ? policy.NameAcronymInAddressConfidence : policy.NameAcronymInItemTextConfidence)
                        : (aboutTheBuyer ? policy.NameInAddressConfidence : policy.NameInItemTextConfidence);
                    passageHits.Add(new Hit(name.CustomerId, confidence,
                        CustomerMatchReasonCodes.NameInDocument,
                        name.Initials
                            ? $"\"{name.Key}\", the initials of \"{name.Display}\", appears in the {passage.Where}: \"{excerpt}\"."
                            : $"\"{name.Display}\" appears in the {passage.Where}: \"{excerpt}\".",
                        name.Key, index, name.OneWordName, name.Taught, name.WrittenWords));
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
            // A taught name that gives way (see TaughtNameGivesWay) is offered, never applied, and takes
            // no part in deciding which other names in its passage are the longer one.
            var heard = new List<Hit>(passageHits.Count);
            foreach (var hit in passageHits)
            {
                if (hit.Taught && TaughtNameGivesWay(hit.CustomerId, hit.MatchedKey))
                {
                    consigneeOnly.Add(hit with
                    {
                        Confidence = Math.Min(hit.Confidence, policy.ShipToDemotedConfidence),
                        Explanation = $"{hit.Explanation} A reviewer taught that name, but another customer's own name " +
                                      "reads as it at least as closely, so it is offered rather than applied."
                    });
                    continue;
                }
                heard.Add(hit);
            }
            var kept = SuppressNamesInsideLongerNames(heard, guarded.Passages);
            var claims = PageIdentityClaims.Read(evidence, guarded, corpus, derivedAcronyms, names, TaughtNameGivesWay, Readings);

            // Headers first: what the page says about WHO IS BUYING is what a delivery address is
            // then checked against.
            var headerStatements = new List<Hit>();
            foreach (var hit in kept)
            {
                var passage = guarded.Passages[hit.PassageIndex];
                if (passage.Role is not PassageRole.BuyerHeader) continue;
                // ONE WORD IN A HEADER IS A PLACE OR A PERSON AS OFTEN AS IN AN ADDRESS. Headers were
                // exempted from the one-word rule below so that "MARAFIQ" in the company-name field kept
                // linking lead 682, and every one-word hit in any header went straight into the linking set.
                // But the buyer sentence, a relay's display name, a purchaser column and the company-name
                // field itself carry cities and people: "Dammam Area Materials Procurement invites bidders"
                // read DAMMAM for "Al Dammam Trading Co.", "Purchaser: Ahmed Al-Ghamdi" read GHAMDI, a city
                // line confirmed once for Marafiq read JUBAIL in "Royal Commission for Jubail and Yanbu".
                // Each stood beside SEC's delivery address and made lead 680's shape AMBIGUOUS, or linked a
                // buyer who is not a customer to a trading house named after its city. A one-word name
                // links from a header only where it IS the statement: the header is that word ("MARAFIQ"),
                // or a sentence opens with it ("MARAFIQ invites bidders"). Elsewhere it is offered.
                if (hit.OneWordName && !OneWordNameIsTheStatement(passage.Text, passage.Key, hit.MatchedKey))
                {
                    consigneeOnly.Add(hit with
                    {
                        Confidence = policy.ShipToDemotedConfidence,
                        Explanation = $"{hit.Explanation} One word in a header names a place or a person as often as a " +
                                      "company unless the header is that word or opens with it, so it is offered rather than applied."
                    });
                    continue;
                }
                var longer = LongerCompanyName(passage.Text, hit.MatchedKey, hit.WrittenWords);
                if (longer is not null)
                {
                    consigneeOnly.Add(RunsOnIntoAnotherName(hit, longer, policy));
                    continue;
                }
                namedInAddress.Add(hit);
                headerStatements.Add(hit);
            }

            foreach (var hit in kept)
            {
                var passage = guarded.Passages[hit.PassageIndex];
                if (passage.Role is PassageRole.BuyerHeader) continue;
                if (passage.Role is PassageRole.ItemText)
                {
                    namedInText.Add(hit);
                    continue;
                }

                // ONE WORD IN AN ADDRESS IS A PLACE AS OFTEN AS A COMPANY. The one-word scan turned
                // "Al Dammam Trading Co." into DAMMAM and "Jizan Establishment" into JIZAN, and every
                // SEC print says "Saudi Electricity Company-DAMMAM" or "-JIZAN AREA". Both names were
                // in the address, both counted as the consignee, and production lead 680's own shape
                // went AMBIGUOUS in any tenant with a trading house named after a city. A one-word
                // name still LINKS from a header, where a company names itself ("MARAFIQ invites
                // bidders"); in an address it is offered to a person, never applied for them.
                //
                // EXCEPT A NAME A REVIEWER TAUGHT, WRITTEN AS THE FIRST WORD OF THE ADDRESS. "ARAMCO Ras Tanura Refinery"
                // and "MARAFIQ Yanbu Warehouse" open with a one-word alias a person confirmed for exactly that
                // customer; base linked both at 0.88 and they fell to a 0.70 offer. That is the header rule
                // (OneWordNameIsTheStatement) for an address: the word must open it, must not be a place or trade word
                // (a taught "DAMMAM" or "YANBU" stays offered), and a taught name that gives way was already set aside
                // above. The ordinary checks still follow: a run into a longer company name, or a page naming someone
                // else, still only offers it. Untaught one-word names stay offered.
                if (hit.OneWordName && !(hit.Taught && TaughtOneWordNameOpensTheAddress(passage.Text, hit.MatchedKey)))
                {
                    consigneeOnly.Add(hit with
                    {
                        Confidence = policy.ShipToDemotedConfidence,
                        Explanation = $"{hit.Explanation} One word in an address names a place as often as a " +
                                      "company, so it is offered rather than applied."
                    });
                    continue;
                }

                var longer = LongerCompanyName(passage.Text, hit.MatchedKey, hit.WrittenWords);
                if (longer is not null)
                {
                    consigneeOnly.Add(RunsOnIntoAnotherName(hit, longer, policy));
                    continue;
                }

                var customerName = names.TryGetValue(hit.CustomerId, out var known)
                    ? known
                    : $"Customer #{hit.CustomerId}";
                var competition = claims.CompetesWith(hit.CustomerId, customerName, passage.Key, headerStatements, guarded.Passages, names, policy);
                if (competition is null)
                {
                    namedInAddress.Add(hit);
                    continue;
                }
                consigneeOnly.Add(hit with
                {
                    Confidence = policy.ShipToDemotedConfidence,
                    Explanation = $"{hit.Explanation} {competition.Sentence}"
                });
                rivalOffers.AddRange(competition.Offers);
            }
        }
        // THE TAUGHT PAIR MEETS THE PAGE (see S3). Where no header or address names a different customer at link
        // strength, the pair decides as it always did. Where one does, neither is applied: the named customer is
        // offered at the demoted ship-to strength and the pair's owner below it, because the pair is an earlier pick
        // and a rep shown the earlier pick first repeats it (the same order CompetesWith gives earlier decisions).
        Hit? taughtPairOffer = null;
        if (taughtPairAwaitingThePage is not null)
        {
            var pairOwner = taughtPairAwaitingThePage.CustomerId;
            var rival = namedInAddress.FirstOrDefault(hit => hit.CustomerId != pairOwner);
            if (rival is null)
                return WithContact(taughtPairOutcome!, guarded, corpus);

            var ownerName = names.TryGetValue(pairOwner, out var knownOwner) ? knownOwner : $"Customer #{pairOwner}";
            var rivalName = names.TryGetValue(rival.CustomerId, out var knownRival) ? knownRival : $"Customer #{rival.CustomerId}";
            foreach (var hit in namedInAddress)
                consigneeOnly.Add(hit with
                {
                    Confidence = policy.ShipToDemotedConfidence,
                    Explanation = hit.CustomerId == pairOwner
                        ? hit.Explanation
                        : $"{hit.Explanation} But a reviewer taught this document's portal account to {ownerName}, " +
                          "so it is offered rather than applied."
                });
            namedInAddress.Clear();
            taughtPairOffer = taughtPairAwaitingThePage with
            {
                Confidence = Math.Min(policy.PriorSenderSuggestionConfidence, policy.ShipToDemotedConfidence),
                Explanation = $"{taughtPairAwaitingThePage.Explanation} A reviewer taught that portal account to this " +
                              $"client, but the page names {rivalName}, so it is offered rather than applied."
            };
        }

        if (Decide(namedInAddress, names, policy, out var namedOutcome))
            return WithContact(namedOutcome!, guarded, corpus);

        var suggestions = new List<Hit>();
        if (taughtPairOffer is not null) suggestions.Add(taughtPairOffer);
        // A name in item text and a contradicted delivery address are both "offer it, do not
        // decide it", so they compete for one row per customer and the stronger statement wins.
        suggestions.AddRange(namedInText.Concat(consigneeOnly)
            .GroupBy(h => h.CustomerId)
            .Select(g => g.OrderByDescending(h => h.Confidence).First()));
        suggestions.AddRange(rivalOffers);
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

    /// <summary>
    /// The derived initials (<see cref="CustomerNameNormalizer.AcronymKey"/>) that two or more
    /// DIFFERENT customers in <paramref name="customers"/> share. Such initials identify neither
    /// customer, so the passage scan never uses them. Pass every active customer of the tenant to
    /// get the set <see cref="Resolve"/> needs above the name-scan cap; it is the same rule the
    /// resolver applies to the corpus, so the two can never disagree about what "shared" means.
    /// </summary>
    public static IReadOnlySet<string> SharedDerivedAcronyms(IEnumerable<CustomerNameSnapshot> customers)
    {
        ArgumentNullException.ThrowIfNull(customers);
        var firstOwner = new Dictionary<string, long>(StringComparer.Ordinal);
        var shared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var customer in customers)
        {
            // AcronymKey, remembered per name (FactsOf): the same initials, without folding every name again per lead.
            var acronym = FactsOf(customer.Name).Initials;
            if (acronym.Length == 0) continue;
            if (!firstOwner.TryAdd(acronym, customer.CustomerId) && firstOwner[acronym] != customer.CustomerId)
                shared.Add(acronym);
        }
        return shared;
    }

    private static bool IsUnverifiedLearned(CustomerIdentifierSnapshot identifier)
        => string.Equals(identifier.Source, CustomerIdentifierSources.LeadReviewUnverified, StringComparison.Ordinal);

    /// <summary>A row the learner wrote from a reviewer's confirmation, as opposed to one a person typed.</summary>
    private static bool IsTaught(CustomerIdentifierSnapshot identifier)
        => string.Equals(identifier.Source, CustomerIdentifierSources.LeadReviewLearned, StringComparison.Ordinal);

    /// <summary>
    /// A one-word key that is one of the customer's own distinctive name words ("YANBU" for "Yanbu
    /// Cement Company", "ALRAJHI" for "Al Rajhi Bank"), and not the customer's initials. The
    /// learner's alias gate accepts exactly this shape, so the word test is the one it uses
    /// (<see cref="CustomerNameDistinctiveness.SharesDistinctiveToken"/>).
    /// </summary>
    private static bool IsOneWordOfTheName(string key, string? customerName)
        => !string.Equals(CustomerNameNormalizer.AcronymKey(customerName), key, StringComparison.Ordinal)
           && CustomerNameDistinctiveness.SharesDistinctiveToken(key, customerName);

    // ── S0 ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The direction-of-trade and self-identity firewall. The only company name printed on
    /// an SEC bid is OUR OWN ("Vendname: ALI ZAID AL-QURAISHI&amp;PARTNERS", "Vendor Code
    /// 2004414"). Anything that normalises to the tenant's own identity — or to the vendor
    /// block on the document itself — is discarded here and can never reach a tier.
    /// </summary>
    private static GuardedEvidence Guard(LeadClientEvidence evidence)
    {
        var tenantNames = evidence.TenantSelfNameKeys
            .Select(CustomerNameNormalizer.LooseKey)
            .Where(key => key.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var selfNames = new HashSet<string>(tenantNames, StringComparer.Ordinal);
        var supplierKey = CustomerNameNormalizer.LooseKey(evidence.SupplierNameOnDocument);
        if (supplierKey.Length > 0) selfNames.Add(supplierKey);

        var addresses = new HashSet<string>(StringComparer.Ordinal);
        var domains = new HashSet<string>(StringComparer.Ordinal);
        var personMailboxDomains = new HashSet<string>(StringComparer.Ordinal);
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
            .Select(p => new GuardedPassage(p.Where, p.Text.Trim(), PassageKey(p.Text), p.Role))
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
            selfNames,
            personMailboxDomains);

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
            // Our own mail, including a host under a domain we mail from: a rep writing from
            // sales.alquraishi.com.sa is us as surely as rfq@alquraishi.com.sa is. The exact-match
            // set this replaced missed every such host, and a Domain row learned from one then
            // linked every later forward from our own staff to one customer at 0.95. A domain whose
            // own name spells ours ("alquraishi.com.sa") is ours too, even when no user or mailbox
            // writes from it: the learner already refused it, and a legacy row on it linked here.
            //
            // THE NAMES THAT MAKE A DOMAIN OURS ARE THE TENANT'S CONFIGURED NAMES, NOT THE VENDOR BLOCK
            // THIS DOCUMENT PRINTS. The vendor block is an extraction, and extractions misread: a Marafiq
            // RFQ whose vendor field came out as "MARAFIQ" made marafiq.com.sa "ours", threw away the
            // buyer's registered address and domain, and a lead that linked at 1.00 went UNRESOLVED. The
            // same with "Saudi Aramco" read into that field and aramco.com. A misread vendor block still
            // cannot become a customer name (selfNames keeps it for that); it just cannot delete a sender.
            //
            // AND THE VENDOR BLOCK WHERE IT IS A SPELLING OF THOSE NAMES, exactly as routing, the corpus loader and
            // the learner ask (TenantSelfIdentity.DomainSelfNames through the evidence overload). This Guard read
            // the configured names alone while routing added "ALI ZAID AL-QURAISHI & PARTNERS ESOSA": mail from
            // esosa.com linked SEC here at 0.95 on SEC's Domain row, and routing refused the same row as ours and
            // sent the lead to the unassigned queue. One question, one answer.
            if (TenantSelfIdentity.IsOurs(domain, evidence)) return;

            addresses.Add(address);
            // A shared consumer mailbox says nothing about an organisation, and neither does a
            // procurement network's relay: noreply@bidnet.com delivers every buyer's RFQ. A Domain
            // row for either, already in the store from before the learner refused them, would
            // auto-link every later sender on it — from any buyer — at S2's 0.95. Refusing the
            // domain HERE makes such rows inert without waiting for a data clean-up. The exact
            // address is still evidence (S1). One shared predicate decides, so this tier, the
            // learner and routing cannot disagree about which domains are an organisation's own.
            if (IdentityDomainGuard.IsOrganisationDomain(domain, evidence.TenantSelfDomains))
            {
                domains.Add(domain);
                // Whether a person's mailbox, and not only a system mailbox, speaks for the domain (see S2).
                if (!IdentityDomainGuard.IsSystemMailbox(address)) personMailboxDomains.Add(domain);
            }
        }
    }

    /// <summary>
    /// A passage's key. <see cref="CustomerNameNormalizer.LooseKey"/> folds initials written with dots ("S.E.C.")
    /// into single letters ("S E C"), which no name or initials key contains, so SEC's own company-name field
    /// stopped linking once the taught alias "S E C" was refused as naming nobody (0.90 at base, NO_MATCH after).
    /// A key made only of two or more single letters is those initials run together. LooseKey itself is not
    /// changed: stored keys depend on it.
    /// </summary>
    internal static string PassageKey(string? text)
    {
        var key = CustomerNameNormalizer.LooseKey(text);
        if (key.Length < 3) return key;
        var tokens = key.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length >= 2 && tokens.All(token => token.Length == 1 && token[0] is >= 'A' and <= 'Z')
            ? string.Concat(tokens)
            : key;
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
    /// <param name="OneWordName">
    /// For a passage hit: the key was the customer's own name reduced to ONE word ("DAMMAM" for
    /// "Al Dammam Trading Co."). Such a hit links only from a header. Initials and taught aliases
    /// are not this: "SEC" is a deliberate abbreviation, not a name that happens to be a city.
    /// </param>
    /// <param name="Taught">
    /// For a passage hit: the name came from a row the learner wrote, so it is checked again against
    /// the customers on the books before it may decide anything (see TaughtNameGivesWay in Resolve).
    /// </param>
    /// <param name="WrittenWords">
    /// For a passage hit on a customer name made only of generic words: every word of that name as it is
    /// written, legal forms aside ("ARABIAN GULF INTERNATIONAL"). The page had to write all of them for the
    /// hit to exist, and a run on into any other word is read as another company. Null for every other hit.
    /// </param>
    private sealed record Hit(
        long CustomerId,
        decimal Confidence,
        string ReasonCode,
        string Explanation,
        string MatchedKey = "",
        int PassageIndex = -1,
        bool OneWordName = false,
        bool Taught = false,
        IReadOnlyList<string>? WrittenWords = null);

    /// <summary>A name the passage scan looks for, and what kind of name it is.</summary>
    private sealed record NameToFind(
        long CustomerId, string Key, string Display, bool Initials, bool OneWordName, bool Taught,
        IReadOnlyList<string>? WrittenWords = null)
    {
        /// <summary>The key between spaces, built once, so matching it against each padded passage key allocates nothing.</summary>
        public string PaddedKey { get; } = " " + Key + " ";

        /// <summary>The key's first word, which must be a whole word of the page for the name to be anywhere on it.</summary>
        public string FirstWord { get; } = Key.Split(' ', 2)[0];
    }

    /// <summary>Fewest written words a generic-only customer name needs before the scan reads it.</summary>
    private const int MinimumWrittenGenericNameWords = 3;

    /// <summary>Shortest a generic-only customer name may be, written out, before the scan reads it.</summary>
    private const int MinimumWrittenGenericNameLength = 15;

    /// <summary>
    /// The words of a customer name made only of generic words, as it is written, with a leading article and
    /// the legal-form words taken off: "Arabian Gulf International Company" is ARABIAN GULF INTERNATIONAL.
    /// Null when that is fewer than <see cref="MinimumWrittenGenericNameWords"/> words or shorter than
    /// <see cref="MinimumWrittenGenericNameLength"/> characters, which is too little to be anybody's name.
    /// </summary>
    private static IReadOnlyList<string>? WrittenNameWords(string spelling)
    {
        var words = PassageWords(spelling).Select(word => word.Key).ToList();
        if (words.Count > 1 && LeadingArticleWords.Contains(words[0])) words.RemoveAt(0);
        words.RemoveAll(LegalFormWords.Contains);
        return words.Count >= MinimumWrittenGenericNameWords
               && string.Join(' ', words).Length >= MinimumWrittenGenericNameLength
            ? words
            : null;
    }

    /// <summary>Whether the passage writes these words one after another, inside one field.</summary>
    private static bool WritesTheNameInFull(string passageText, IReadOnlyList<string> writtenWords)
    {
        var words = PassageWords(passageText);
        for (var start = 0; start + writtenWords.Count <= words.Count; start++)
        {
            var matched = 0;
            while (matched < writtenWords.Count
                   && string.Equals(words[start + matched].Key, writtenWords[matched], StringComparison.Ordinal)
                   && (matched == 0 || !words[start + matched].BreakBefore))
                matched++;
            if (matched == writtenWords.Count) return true;
        }
        return false;
    }

    /// <summary>
    /// The initials of the first two words of a name, articles and joining words aside: "General Electric Saudi
    /// Arabia" is GE. Empty when the name has fewer than two such words.
    /// </summary>
    private static string LeadingTwoWordInitials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var words = PassageWords(name)
            .Select(word => word.Key)
            .Where(key => key.Length > 0 && key[0] is >= 'A' and <= 'Z')
            .ToList();
        if (words.Count > 1 && LeadingArticleWords.Contains(words[0])) words.RemoveAt(0);
        words.RemoveAll(InitialsJoiningWords.Contains);
        return words.Count < 2 ? string.Empty : string.Concat(words[0][0], words[1][0]);
    }

    /// <summary>Words that never give a letter to a name's initials.</summary>
    private static readonly HashSet<string> InitialsJoiningWords = new(StringComparer.Ordinal) { "AND", "OF", "THE", "FOR" };

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
        HashSet<string> SelfNameKeys,
        HashSet<string> PersonMailboxDomains)
    {
        public bool IsEmpty =>
            Addresses.Count == 0 && Domains.Count == 0 && AccountReferences.Count == 0 &&
            TaxRegistrations.Count == 0 && NameKey.Length == 0 &&
            string.IsNullOrEmpty(PortalAccountKey) && BuyerPersonKey.Length == 0 && Passages.Count == 0;

        public bool IsSelfName(string key) => SelfIdentityGuard.IsSelfName(key, SelfNameKeys);
    }

    /// <summary>
    /// The spellings of a customer's name the passage scan looks for: the name as recorded and, when
    /// it ends in a bracketed trade name, the name without it. "Saudi Aramco Total Refining &amp;
    /// Petrochemical Co. (SATORP)" keys to "... PETROCHEMICAL SATORP", which no document writes, so
    /// SATORP was never found in its own delivery address while the parent's "SAUDI ARAMCO" inside it
    /// was, and the lead linked to Aramco. The bracket alone is not scanned: "(Riyadh)" or "(Branch)"
    /// is as often a note as a trade name.
    /// </summary>
    /// <summary>What the passage tier reads off one spelling of a customer's name.</summary>
    /// <param name="Key">The spelling's <see cref="CustomerNameNormalizer.LooseKey"/>, never empty.</param>
    /// <param name="Scannable">One word of four characters or more that is not an ordinary word, or two words and eight characters.</param>
    /// <param name="Distinctive">Scannable, and at least one word names one company (<see cref="CustomerNameDistinctiveness"/>).</param>
    /// <param name="WrittenWords">For a scannable name of several generic words only: its words as written (<see cref="WrittenNameWords"/>).</param>
    private sealed record SpellingFacts(string Key, bool Scannable, bool Distinctive, IReadOnlyList<string>? WrittenWords);

    /// <summary>What the passage tier reads off one customer name: its spellings and its derived initials.</summary>
    private sealed record NameFacts(string Initials, IReadOnlyList<SpellingFacts> Spellings);

    /// <summary>
    /// Name facts already worked out, by the exact name as recorded. Every fact is a pure function of the name, so a
    /// remembered one is the same answer whichever tenant or lead asks. Above the name-scan cap the passage tier and the
    /// corpus loader read thousands of customer names on every lead, and folding each of them several times over was
    /// most of the time a resolution took (the PERF finding). Bounded, and simply emptied when full.
    /// </summary>
    private static readonly ConcurrentDictionary<string, NameFacts> RememberedNameFacts = new(StringComparer.Ordinal);

    private const int MaximumRememberedNames = 200_000;

    private static NameFacts FactsOf(string? name)
    {
        var raw = name ?? string.Empty;
        if (RememberedNameFacts.TryGetValue(raw, out var known)) return known;

        var spellings = new List<SpellingFacts>(2);
        foreach (var spelling in NameSpellings(raw))
        {
            var key = CustomerNameNormalizer.LooseKey(spelling);
            if (key.Length == 0) continue;
            var oneWord = !key.Contains(' ');
            var scannable = oneWord
                ? key.Length >= 4 && !CustomerNameNormalizer.IsAmbiguousAcronym(key)
                : key.Length >= 8;
            var distinctive = scannable && CustomerNameDistinctiveness.HasDistinctiveToken(key);
            var written = scannable && !distinctive && !oneWord ? WrittenNameWords(spelling) : null;
            spellings.Add(new SpellingFacts(key, scannable, distinctive, written));
        }
        var facts = new NameFacts(CustomerNameNormalizer.AcronymKey(raw), spellings);
        if (RememberedNameFacts.Count >= MaximumRememberedNames) RememberedNameFacts.Clear();
        RememberedNameFacts.TryAdd(raw, facts);
        return facts;
    }

    /// <summary>
    /// <see cref="CustomerNameNormalizer.AcronymKey"/> of a customer name, remembered per name. The corpus loader builds
    /// its tenant-wide initials index with this, so the loader and the resolver read initials the same way.
    /// </summary>
    internal static string DerivedInitials(string? name) => FactsOf(name).Initials;

    private static IEnumerable<string> NameSpellings(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) yield break;
        var trimmed = name.Trim();
        yield return trimmed;
        if (!trimmed.EndsWith(')')) yield break;
        var open = trimmed.LastIndexOf('(');
        if (open <= 0) yield break;
        var withoutTradeName = trimmed[..open].Trim();
        if (withoutTradeName.Length > 0) yield return withoutTradeName;
    }

    /// <summary>
    /// THE LONGEST NAME IN A PASSAGE IS THE ONE THE DOCUMENT MEANS.
    ///
    /// A delivery address reading "Saudi Aramco Total Refining and Petrochemical Company, Jubail"
    /// contains "SAUDI ARAMCO" as whole words, so with both companies on the books one address
    /// matched two customers and the lead went AMBIGUOUS. SATORP is a separate joint venture with
    /// its own vendor registration, its own payment terms and its own portal; an invoice sent to
    /// Aramco against a SATORP order is simply not paid. The same shape covers the whole family,
    /// because every one of them is a joint venture that put the parent's name in its own: SAMREF
    /// (Aramco and Mobil), YASREF (Aramco and Sinopec), Luberef, Sadara (Aramco and Dow). It settles
    /// "Royal Commission Jubail" against a bare "Royal Commission" the same way — the
    /// industrial-city authority and the parent body are different buyers with different budgets.
    /// (With only the parent on the books there is nothing longer to suppress it with; that case is
    /// <see cref="LongerCompanyName"/>.)
    ///
    /// A ONE-WORD NAME GIVES WAY TO ANY LONGER NAME IN THE SAME PASSAGE: a full name or a
    /// customer's initials. "Saudi Electricity Company Dammam" is SEC writing where it is, not SEC
    /// and "Al Dammam Trading" both buying; before the one-word scan existed the page was read that
    /// way, and it is read that way again.
    ///
    /// Applied only INSIDE ONE PASSAGE, because "the document wrote one name and we read two" is
    /// a statement about one sentence. Two names in two different passages are two statements and
    /// both deserve to be heard.
    /// </summary>
    private static List<Hit> SuppressNamesInsideLongerNames(List<Hit> passageHits, IReadOnlyList<GuardedPassage> passages)
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
                // A longer name that itself only begins a still longer company name cannot swallow the one
                // word: in "Saudi Aramco Total Refining &amp; Petrochemical (SATORP)" the parent's name runs
                // on into SATORP's, and the bracketed SATORP is the name the page means, not a word inside
                // Saudi Aramco's.
                var shadowed = inPassage.Any(other =>
                    (other.MatchedKey.Length > hit.MatchedKey.Length
                     && ContainsWholeWords(other.MatchedKey, hit.MatchedKey))
                    || (hit.OneWordName && !other.OneWordName && other.CustomerId != hit.CustomerId
                        && LongerCompanyName(passages[other.PassageIndex].Text, other.MatchedKey, other.WrittenWords) is null));
                if (!shadowed) kept.Add(hit);
            }
        }
        return kept;
    }

    private static Hit RunsOnIntoAnotherName(Hit hit, string longer, CustomerResolutionPolicy policy) => hit with
    {
        Confidence = policy.ShipToDemotedConfidence,
        Explanation = $"{hit.Explanation} But there the name runs on into \"{longer}\", which reads as a " +
                      "different company's name, so it is offered rather than applied."
    };

    /// <summary>
    /// A CUSTOMER'S NAME CAN BE THE FIRST HALF OF ANOTHER COMPANY'S NAME.
    ///
    /// "Saudi Aramco Total Refining and Petrochemical Company, Jubail" is SATORP's address, and with
    /// only Saudi Aramco on the books — or SATORP recorded as "SATORP" — the words "Saudi Aramco"
    /// linked it to Aramco at 0.88. The longest-name rule cannot help when the longer name belongs
    /// to nobody in the tenant. So: where a customer's name runs straight on, in the same field and
    /// without a break, into more name words and then a legal-form word (Company, Co, LLC, Ltd),
    /// the page has written a longer company name that merely begins with the customer's. The hit
    /// is offered instead of applied.
    ///
    /// The run is read narrowly on purpose, because lead 680's "Saudi Electricity Company-DAMMAM"
    /// and every "Saudi Aramco Ras Tanura Refinery" must keep linking. It stops at punctuation that
    /// separates fields (a comma, a bracket, a slash, " - "), at a lowercase word ("invites") or a
    /// connecting word (TO, FOR, AT, INVITES), and after six words. The customer's own legal words
    /// do not count ("Saudi Electricity Company Ltd" is still SEC). One plain mention of the name
    /// anywhere in the passage is a fair statement and the hit stands.
    ///
    /// Returns the longer name as the page wrote it, or null when the name stands on its own.
    /// </summary>
    /// <param name="ownWords">
    /// For a name made only of generic words: every word of it as written. Then a word LooseKey strips
    /// that is NOT one of these ("TRADING" after "Arabian Gulf International") is another name word too,
    /// because for such a name the stripped words are all that tell two companies apart.
    /// </param>
    internal static string? LongerCompanyName(string passageText, string nameKey, IReadOnlyList<string>? ownWords = null)
    {
        var name = nameKey.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (name.Length == 0 || string.IsNullOrWhiteSpace(passageText)) return null;
        var words = PassageWords(passageText);

        string? longer = null;
        for (var start = 0; start < words.Count; start++)
        {
            if (!string.Equals(words[start].Key, name[0], StringComparison.Ordinal)) continue;

            // The name's own words, allowing the legal words LooseKey dropped from the key to sit
            // between them ("SAUDI ELECTRICITY" against "Saudi Electricity Company").
            var next = start + 1;
            var matched = 1;
            while (matched < name.Length && next < words.Count && !words[next].BreakBefore)
            {
                if (string.Equals(words[next].Key, name[matched], StringComparison.Ordinal)) { matched++; next++; }
                else if (IsNoiseWord(words[next].Key)) next++;
                else break;
            }
            if (matched < name.Length) continue;

            string? runsOnInto = null;
            var sawAnotherNameWord = false;
            var crossedDash = false;
            // The words after the name, so a bracket can be asked whether it repeats one or closes a site's name.
            var runWords = new List<string>();
            for (var index = next; index < words.Count && index < next + MaximumNameRunWords; index++)
            {
                var word = words[index];
                if (word.BreakBefore)
                {
                    // A BRACKETED TRADE NAME ENDS A COMPANY NAME AS SURELY AS "COMPANY" DOES. "Saudi Aramco
                    // Total Refining & Petrochemical (SATORP), Jubail" and "Saudi Aramco Jubail Refinery
                    // (SASREF)" carry no legal-form word, so the run stopped at the bracket, "Saudi Aramco"
                    // stood alone and both linked to Aramco at 0.88. A run of other name words closed by one
                    // capitalised, distinctive word in brackets is a company writing its trade name. A short
                    // site code ("(RTR)") or the customer's own initials straight after its name ("Saudi
                    // Electricity Company (SEC)") is not.
                    if (word.BracketBefore && sawAnotherNameWord && !crossedDash
                        && IsBracketedTradeName(passageText, word, words[start], runWords, out var closesAt))
                    {
                        runsOnInto = passageText[words[start].Start..closesAt];
                        break;
                    }
                    // A DASH STRAIGHT AFTER A MULTI-WORD NAME MAY SET OFF ANOTHER COMPANY'S NAME. "SAUDI
                    // ARAMCO - TOTAL REFINING AND PETROCHEMICAL COMPANY, JUBAIL" is SATORP's legal name
                    // after the parent's, and the dash stopped the run. One dash is crossed, only directly
                    // after the name, and what follows still has to end in a legal-form word to count, so
                    // "Saudi Electricity Company - Riyadh PP9 Substation" and "Saudi Aramco - Ras Tanura
                    // Refinery" still link. A one-word name is never carried across: "MARAFIQ - Power &
                    // Water Utility Company..." is a trade name followed by its own legal name.
                    if (!word.DashBefore || crossedDash || sawAnotherNameWord || name.Length < 2) break;
                    crossedDash = true;
                }
                runWords.Add(word.Key);
                if (LegalFormWords.Contains(word.Key))
                {
                    if (!sawAnotherNameWord) continue;
                    runsOnInto = passageText[words[start].Start..word.End];
                    break;
                }
                if (word.StopsNameRun) break;
                if (IsNoiseWord(word.Key))
                {
                    if (ownWords is not null && !ownWords.Contains(word.Key, StringComparer.Ordinal)) sawAnotherNameWord = true;
                }
                // A SECTOR WORD SAYS WHAT THE CUSTOMER DOES, NOT WHO ELSE IS NAMED. "Saudi Aramco Oil Company -
                // Ras Tanura" and "Saudi Aramco Oil Co., Dhahran" are Aramco's own legal name written out, and
                // read as another company they dropped a correct 0.88 link to a 0.70 offer. Only these few:
                // CHEMICAL, PETROCHEMICAL and REFINING are real joint-venture name words (SATORP, SADAF).
                else if (!SectorWords.Contains(word.Key)) sawAnotherNameWord = true;
            }

            if (runsOnInto is null) return null;
            longer ??= runsOnInto;
        }
        return longer;
    }

    private const int MaximumNameRunWords = 6;

    /// <summary>Sector words that follow a customer's own name without making it another company's.</summary>
    private static readonly HashSet<string> SectorWords = new(StringComparer.Ordinal)
    {
        "OIL", "GAS", "ELECTRICITY", "ELECTRIC", "POWER", "WATER", "ENERGY"
    };

    /// <summary>Shortest bracketed word read as a trade name; "(RTR)" and "(KSA)" are site and country codes.</summary>
    private const int MinimumBracketedTradeNameLength = 4;

    /// <summary>
    /// Whether the word right after an opening bracket is a trade name closing a company name: capital
    /// letters only, at least <see cref="MinimumBracketedTradeNameLength"/> of them, not an ordinary
    /// address or form word ("(EAST)", "(AREA)"), and immediately closed by the bracket.
    ///
    /// AND IT MUST BEGIN WITH THE LETTER THE RUN BEGINS WITH. A trade name abbreviates the name it closes:
    /// SATORP, SASREF and SAMREF all start with the S of "Saudi". A city or a site code does not: "Saudi
    /// Electricity Company Main Store (DAMMAM)" and "Saudi Aramco Abqaiq Plants (ABQP)" are the customer's
    /// own site, and read as another company's trade name they fell from a 0.88 link to a 0.70 offer.
    /// (A stricter "its letters come in order from the run's initials" test would lose SASREF, which is
    /// Saudi Aramco Shell Refinery and not spelled by "Saudi Aramco Jubail Refinery".)
    /// </summary>
    /// <param name="runWords">The words between the customer's name and the bracket.</param>
    private static bool IsBracketedTradeName(
        string passageText, PassageWord word, PassageWord runStart, IReadOnlyList<string> runWords, out int closesAt)
    {
        closesAt = -1;
        var raw = passageText[word.Start..word.End];
        if (raw.Length < MinimumBracketedTradeNameLength || !raw.All(c => c is >= 'A' and <= 'Z')) return false;
        if (runStart.Key.Length == 0 || raw[0] != runStart.Key[0]) return false;
        if (CustomerNameNormalizer.IsAmbiguousAcronym(raw) || !CustomerNameDistinctiveness.HasDistinctiveToken(raw)) return false;
        // NOR A SITE'S OWN CODE THAT HAPPENS TO START WITH THE SAME LETTER. Saudi Aramco and SEC sites whose names
        // begin with S ("Saudi Aramco Shaybah (SHAYBAH)", "Saudi Aramco Shaybah Producing Department (SHYB)",
        // "Saudi Electricity Company Sudair Substation (SUDAIR)") read as a joint venture's trade name and fell from
        // an 0.88 link to a 0.70 offer. A bracket that repeats a word already in the run is that place written again,
        // and a run carrying a site or organisation-unit word (PLANT, DEPARTMENT, SUBSTATION ...) names a site, not a
        // company. The joint-venture runs this rule exists for ("Total Refining & Petrochemical", "Jubail Refinery",
        // "Mobil Refinery Company") carry neither.
        if (runWords.Contains(word.Key, StringComparer.Ordinal) || runWords.Any(SiteUnitWords.Contains)) return false;
        var after = word.End;
        while (after < passageText.Length && passageText[after] == ' ') after++;
        if (after >= passageText.Length || passageText[after] is not (')' or ']')) return false;
        closesAt = after + 1;
        return true;
    }

    /// <summary>Words that make a run of words after a customer's name a site or a unit of that customer.</summary>
    private static readonly HashSet<string> SiteUnitWords = new(StringComparer.Ordinal)
    {
        "PLANT", "PLANTS", "DEPARTMENT", "DEPT", "OPERATIONS", "OPERATION", "OPERATING", "AREA", "STORE", "STORES",
        "SUBSTATION", "STATION", "PRODUCING", "FACILITY", "FACILITIES", "SUPPLY", "NGL", "GAS", "POWER", "ONSHORE",
        "OFFSHORE", "WAREHOUSE", "TERMINAL", "PROCESSING", "BRANCH", "SITE", "YARD"
    };

    /// <summary>
    /// Whether one word of a customer's name, read off a mailbox display name, speaks only as a person or a department:
    /// the display name is that word alone once a leading article and buying-function words are taken off ("Rashid",
    /// "RASHID", "Dammam Procurement"), or the word is directly followed by a buying function ("Ahmed Rashid,
    /// Procurement"). Never where the display name is the customer's whole written name ("SABIC Procurement" for a
    /// customer recorded as SABIC). A fuller person's name ("Rashid Al-Otaibi") is neither, and stays a mention.
    /// </summary>
    private static bool OneWordOnTheMailboxIsAPersonOrADepartment(string displayName, string nameKey, string? customerName)
    {
        if (MailboxNameIsTheWholeName(displayName, customerName)) return false;
        var words = PassageWords(displayName).Select(word => word.Key).ToList();
        if (words.Count > 1 && LeadingArticleWords.Contains(words[0])) words.RemoveAt(0);
        if (words.Count(word => !LeadCustomerResolutionService.SignatureFunctionWords.Contains(word)) <= 1) return true;
        var at = words.IndexOf(nameKey);
        return at >= 0 && at + 1 < words.Count && LeadCustomerResolutionService.SignatureFunctionWords.Contains(words[at + 1]);
    }

    /// <summary>
    /// Whether a mailbox display name is a customer's whole written name: the same words, a leading article, legal
    /// forms and buying-function words aside ("SABIC Procurement" is SABIC; "Rashid" is not "Al-Rashid Trading").
    /// </summary>
    private static bool MailboxNameIsTheWholeName(string displayName, string? customerName)
    {
        if (string.IsNullOrWhiteSpace(customerName)) return false;
        var written = OwnWords(displayName, dropFunctionWords: true);
        return written.Count > 0 && written.SequenceEqual(OwnWords(customerName, dropFunctionWords: false), StringComparer.Ordinal);

        static List<string> OwnWords(string text, bool dropFunctionWords)
        {
            var words = PassageWords(text).Select(word => word.Key).ToList();
            if (words.Count > 1 && LeadingArticleWords.Contains(words[0])) words.RemoveAt(0);
            words.RemoveAll(LegalFormWords.Contains);
            if (dropFunctionWords) words.RemoveAll(LeadCustomerResolutionService.SignatureFunctionWords.Contains);
            return words;
        }
    }

    /// <summary>
    /// Whether a taught one-word name opens an address ("ARAMCO Ras Tanura Refinery"), after an article, and is not a
    /// place or trade word (<see cref="CustomerAliasLearner.SectorAndPlaceWords"/>), which names a site as often as a company.
    /// </summary>
    private static bool TaughtOneWordNameOpensTheAddress(string passageText, string nameKey)
    {
        if (CustomerAliasLearner.SectorAndPlaceWords.Contains(nameKey)) return false;
        var words = PassageWords(passageText);
        var index = 0;
        if (index < words.Count && LeadingArticleWords.Contains(words[index].Key)) index++;
        return index < words.Count && string.Equals(words[index].Key, nameKey, StringComparison.Ordinal);
    }

    /// <summary>Articles that can stand in front of a name at the start of a sentence ("Al Marafiq", "The SABIC").</summary>
    private static readonly HashSet<string> LeadingArticleWords = new(StringComparer.Ordinal) { "AL", "EL", "THE" };

    /// <summary>
    /// Whether a one-word name is the whole statement a header makes: the header's key IS the name
    /// ("MARAFIQ", "Marafiq Co."), or the header opens with the name (after an article, and with its own
    /// legal or trading words allowed) and the next word begins a sentence ("MARAFIQ invites bidders",
    /// "Al-Ghamdi Trading invites"). "Dammam Area Materials Procurement invites", "Royal Commission for
    /// Jubail and Yanbu" and "Ahmed Al-Ghamdi" are not.
    /// </summary>
    internal static bool OneWordNameIsTheStatement(string passageText, string passageKey, string nameKey)
    {
        if (string.Equals(passageKey, nameKey, StringComparison.Ordinal)) return true;
        var words = PassageWords(passageText);
        var index = 0;
        if (index < words.Count && LeadingArticleWords.Contains(words[index].Key)) index++;
        if (index >= words.Count || !string.Equals(words[index].Key, nameKey, StringComparison.Ordinal)) return false;
        index++;
        while (index < words.Count && !words[index].BreakBefore && IsNoiseWord(words[index].Key)) index++;
        return index < words.Count && !words[index].BreakBefore && words[index].StopsNameRun;
    }

    /// <summary>Words that make a run of words a company's registered name.</summary>
    private static readonly HashSet<string> LegalFormWords = new(StringComparer.Ordinal)
    {
        "CO", "COMPANY", "CORP", "CORPORATION", "INC", "LTD", "LIMITED", "LLC", "WLL", "PLC",
        "JSC", "PJSC", "PSC", "SPC", "SAOG", "SAOC", "KSC", "KSCP", "QSC", "QPSC", "BSC",
        "SARL", "GMBH", "FZE", "FZC", "FZCO", "EST", "ESTABLISHMENT"
    };

    /// <summary>
    /// Words that end a name and begin a sentence or a location. None of them sits inside the
    /// joint-venture names this rule exists for, and each one shows up straight after a buyer's
    /// name in a real buyer sentence ("SAUDI ARAMCO INVITES BIDDERS ... TO THE COMPANY").
    /// </summary>
    private static readonly HashSet<string> NameRunStopWords = new(StringComparer.Ordinal)
    {
        "AT", "VIA", "TO", "THE", "FOR", "BY", "IN", "ON", "OF", "WITH", "FROM", "IS", "ARE", "WAS",
        "WERE", "WILL", "SHALL", "INVITES", "INVITE", "INVITING", "REQUESTS", "REQUEST", "ATTN",
        "ATTENTION", "NEAR", "INSIDE", "OPPOSITE", "BEHIND"
    };

    /// <summary>Characters that separate one field or clause of a passage from the next.</summary>
    private static readonly HashSet<char> FieldBreaks = [',', ';', ':', '(', ')', '[', ']', '{', '}', '/', '\\', '|', '\n', '\r', '\t', '•'];

    /// <param name="Key">The word as <see cref="CustomerNameNormalizer.LooseKey"/> writes it, legal words kept.</param>
    /// <param name="Start">Where the word starts in the passage text.</param>
    /// <param name="End">Where it ends (exclusive).</param>
    /// <param name="BreakBefore">A field separator stands between this word and the one before.</param>
    /// <param name="StopsNameRun">A lowercase or connecting word, which no company name continues through.</param>
    /// <param name="DashBefore">The break before this word is a spaced dash (" - "), not a comma or a bracket.</param>
    /// <param name="BracketBefore">An opening bracket stands between this word and the one before.</param>
    private readonly record struct PassageWord(
        string Key, int Start, int End, bool BreakBefore, bool StopsNameRun, bool DashBefore = false, bool BracketBefore = false);

    /// <summary>
    /// The passage's words with their positions, keeping the legal-form words a passage key drops.
    /// Each word is folded by <see cref="CustomerNameNormalizer.LooseKey"/> ON ITS OWN, where it keeps
    /// every word, so "Company" stays COMPANY and "Ma'aden" folds exactly as it does in a name key.
    /// </summary>
    private static List<PassageWord> PassageWords(string text)
    {
        var words = new List<PassageWord>();
        var runStart = -1;
        bool gapBreak = false, gapSpace = false, gapDash = false, gapBracket = false;
        for (var index = 0; index < text.Length; index++)
        {
            var c = text[index];
            if (char.IsLetterOrDigit(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                if (runStart < 0) runStart = index;
                continue;
            }
            if (runStart >= 0)
            {
                Emit(runStart, index, null);
                runStart = -1;
            }
            if (c == '&') Emit(index, index + 1, "AND");
            else if (FieldBreaks.Contains(c))
            {
                gapBreak = true;
                if (c is '(' or '[') gapBracket = true;
            }
            else if (char.IsWhiteSpace(c)) gapSpace = true;
            else if (c is '-' or '–' or '—') gapDash = true;
        }
        if (runStart >= 0) Emit(runStart, text.Length, null);
        return words;

        void Emit(int start, int end, string? fixedKey)
        {
            var raw = text[start..end];
            var key = fixedKey ?? CustomerNameNormalizer.LooseKey(raw);
            // "Company-DAMMAM" is one field; "Company - Riyadh PP9 Substation" is two.
            var breakBefore = words.Count > 0 && (gapBreak || (gapSpace && gapDash));
            var dashBefore = breakBefore && !gapBreak;
            var bracketBefore = words.Count > 0 && gapBracket;
            gapBreak = gapSpace = gapDash = gapBracket = false;
            foreach (var part in key.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var stops = NameRunStopWords.Contains(part)
                            || (char.IsLower(raw[0]) && !string.Equals(part, "AND", StringComparison.Ordinal));
                words.Add(new PassageWord(part, start, end, breakBefore, stops, dashBefore, bracketBefore));
                breakBefore = dashBefore = bracketBefore = false;
            }
        }
    }

    /// <summary>
    /// Whether <see cref="CustomerNameNormalizer.LooseKey"/> strips this word from a name (a legal
    /// form, TRADING, a dangling AND). Asked of LooseKey itself, with a word it never strips in
    /// front, rather than of a second copy of its private list that would drift from it.
    /// </summary>
    private static bool IsNoiseWord(string word)
        => string.Equals(CustomerNameNormalizer.LooseKey("Q " + word), "Q", StringComparison.Ordinal);

    /// <summary>What on this page disagrees with linking a consignee, and whom that evidence names instead.</summary>
    private sealed record Competition(string Sentence, IReadOnlyList<Hit> Offers);

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
    /// ONLY A POSITIVE CLAIM FOR SOMEBODY ELSE COMPETES. As first written, anything on the page
    /// that was not demonstrably this customer counted as a rival buyer: every sender domain nobody
    /// had registered (sabic.com, satorp.com, apco-ksa.com, a rep's own group domain), and every
    /// company-name field written another way ("الشركة السعودية للكهرباء", "S.E.C.", a variant of our
    /// own vendor block). Each demoted a correct 0.88 link to a 0.70 suggestion with a false
    /// sentence, and a letter-guessing patch to spare "domains that spell the name" then treated
    /// Schneider Electric's se.com as Saudi Electricity's. Unknown is not evidence. So the page
    /// competes only where it names ANOTHER customer of this tenant: a header naming them, the
    /// company-name field holding their name, initials or verified alias, or a mail domain their
    /// own records write from. An EPC contractor the tenant has never recorded therefore no longer
    /// demotes the site owner; recording the contractor, or a contact at its domain, restores it.
    /// </summary>
    private sealed class PageIdentityClaims
    {
        private readonly string? _buyerNameOnDocument;
        private readonly HashSet<long> _nameOwners;
        private readonly IReadOnlyList<DomainTie> _domainTies;

        /// <summary>What kind of fact ties a mail domain to a customer, because they are not worth the same.</summary>
        private enum TieKind
        {
            /// <summary>A contact a person entered, or a verified address registered to the customer.</summary>
            Record,
            /// <summary>An earlier lead a person resolved, from the very address on this page.</summary>
            DecisionFromThisMailbox,
            /// <summary>An earlier lead a person resolved, from a different address on the same domain.</summary>
            DecisionFromAnotherMailbox,
            /// <summary>The organisation the sender's mailbox is signed with.</summary>
            Signature
        }

        /// <param name="Address">For a decision: the address the earlier lead came from or printed.</param>
        /// <param name="SignatureWords">For a signature: its distinctive words.</param>
        private sealed record DomainTie(
            string Domain, long CustomerId, string Why, TieKind Kind,
            string Address = "", IReadOnlyList<string>? SignatureWords = null)
        {
            public bool IsDecision => Kind is TieKind.DecisionFromThisMailbox or TieKind.DecisionFromAnotherMailbox;
        }

        /// <summary>
        /// Distinct addresses on one domain that must carry a person's decisions, all for the same other
        /// customer, before those decisions speak for the domain on a page from a different address.
        /// </summary>
        private const int MailboxesThatMakeADecisionTheDomains = 2;

        private PageIdentityClaims(string? buyerNameOnDocument, HashSet<long> nameOwners, IReadOnlyList<DomainTie> domainTies)
        {
            _buyerNameOnDocument = buyerNameOnDocument;
            _nameOwners = nameOwners;
            _domainTies = domainTies;
        }

        public static PageIdentityClaims Read(
            LeadClientEvidence evidence,
            GuardedEvidence guarded,
            ClientResolutionCorpus corpus,
            IReadOnlyDictionary<string, long> derivedAcronyms,
            IReadOnlyDictionary<long, string> names,
            Func<long, string, bool> taughtNameGivesWay,
            Func<CustomerAliasLearner.NameReadings> readings)
        {
            // The company-name field belongs to a customer when it IS their name, their initials
            // ("S.E.C." is SEC), or an alias a person verified against them.
            var owners = new HashSet<long>();
            if (guarded.NameKey.Length > 0)
            {
                foreach (var customer in corpus.Customers)
                    if (string.Equals(CustomerNameNormalizer.LooseKey(customer.Name), guarded.NameKey, StringComparison.Ordinal))
                        owners.Add(customer.CustomerId);
                if (derivedAcronyms.TryGetValue(guarded.TightNameKey, out var initialsOwner))
                    owners.Add(initialsOwner);
                foreach (var identifier in corpus.Identifiers)
                    if (identifier.IsVerified
                        && identifier.IdentifierType is CustomerIdentifierType.Alias or CustomerIdentifierType.CustomerName
                        && CustomerIdentifierSources.TrustedForAutoLink.Contains(identifier.Source, StringComparer.Ordinal)
                        && string.Equals(identifier.NormalizedValue, guarded.NameKey, StringComparison.Ordinal)
                        && CustomerNameDistinctiveness.HasDistinctiveToken(identifier.NormalizedValue)
                        // A taught name that gives way to another customer makes no claim for its owner.
                        && !(IsTaught(identifier) && taughtNameGivesWay(identifier.CustomerId, identifier.NormalizedValue)))
                        owners.Add(identifier.CustomerId);
            }

            // A mail domain belongs to a customer when that customer's own records write from it: a
            // contact a person entered, an earlier lead from it a person resolved, an address
            // registered to them. Facts somebody set, never a guess at what a domain's letters spell.
            // A Domain row is not read here: had one matched, S2 would already have decided.
            // Guard has already removed synthetic, self, free-mail and relay domains, so the postman
            // (ansmtp.ariba.com) can never be a rival. Ordered, so the sentence names the same
            // domain on every run.
            var ties = new List<DomainTie>();
            foreach (var domain in guarded.Domains.OrderBy(domain => domain, StringComparer.Ordinal))
            {
                foreach (var contact in corpus.Contacts)
                {
                    if (!SameDomain(contact.Email, domain)) continue;
                    var who = $"{contact.FirstName} {contact.LastName}".Trim();
                    if (who.Length == 0) who = contact.Email!.Trim();
                    ties.Add(new DomainTie(domain, contact.CustomerId, $"{who}, a contact on {NameOf(contact.CustomerId)}, writes from it", TieKind.Record));
                }
                // AN EARLIER DECISION IS A FACT ABOUT ONE MAILBOX. Read as a fact about the whole domain, one
                // person-linked lead from 59000@se.com.sa to Saudi Aramco demoted every later SEC print from
                // 57322@se.com.sa to 0.70 and ranked Aramco FIRST, so one mis-click steered the next rep into
                // repeating it. The kind is kept here; CompetesWith decides what a decision may say.
                //
                // NOT A SYSTEM MAILBOX'S. no-reply@etimad.gov.sa carries every buyer's tender on that portal, so a
                // person linking the Aramco job it carried says who that job was for, not who the mailbox is. Read
                // as "this mailbox's decision", one Aramco pick demoted the next SEC print through the same relay
                // to 0.70. The mailbox names nobody (IdentityDomainGuard.IsSystemMailbox), which is why S1 will not
                // match a learned row on it either.
                foreach (var prior in corpus.PriorSenderResolutions)
                    if (SameDomain(prior.SenderEmail, domain) && !IdentityDomainGuard.IsSystemMailbox(prior.SenderEmail))
                    {
                        var address = prior.SenderEmail.Trim().ToLowerInvariant();
                        ties.Add(new DomainTie(domain, prior.CustomerId,
                            $"an earlier lead from {prior.SenderEmail} was resolved to {NameOf(prior.CustomerId)} by a person",
                            guarded.Addresses.Contains(address) ? TieKind.DecisionFromThisMailbox : TieKind.DecisionFromAnotherMailbox,
                            address));
                    }
                foreach (var identifier in corpus.Identifiers)
                    // Only a fact ties: the same test S1 applies, and the one the learner uses for
                    // "another customer already writes from this domain".
                    // S1 also refuses a row nobody entered on a system mailbox (MayMatchExactAddress), and so must
                    // this: a learned no-reply@etimad.gov.sa on Aramco matched nothing at S1 and still demoted an SEC
                    // print through the same relay to 0.70, with Aramco ranked first.
                    if (identifier.IdentifierType == CustomerIdentifierType.Email
                        && identifier.IsVerified
                        && !IsUnverifiedLearned(identifier)
                        && IdentityDomainGuard.MayMatchExactAddress(identifier.NormalizedValue, identifier.Source)
                        && SameDomain(identifier.NormalizedValue, domain))
                        ties.Add(new DomainTie(domain, identifier.CustomerId,
                            $"{identifier.NormalizedValue} is registered to {NameOf(identifier.CustomerId)}", TieKind.Record));
            }

            // THE ORGANISATION THE SENDER'S MAILBOX IS SIGNED WITH (#14). Hyundai E&C mails from hdec.com
            // about an Aramco site. Hyundai is a customer, but with no contact or registered row at hdec.com
            // nothing here was a claim for it: Aramco linked on its site address at 0.88 and Hyundai was
            // never offered. The mailbox says "Hyundai E&C Procurement". When that signature, on the
            // sender's own organisation domain, reads as exactly one customer by the learner's name tiers,
            // it ties the domain to that customer like a contact would. It can only demote a consignee and
            // offer the writer. It is never the consignee's OWN tie (see CompetesWith): an EPC project mailbox
            // signed "Saudi Aramco Procurement" at hdec.com is still Hyundai's mailbox, and it must not
            // outvote Hyundai's real contact there.
            //
            // A SIGNATURE MADE ONLY OF PLACE OR TRADE WORDS IS A BRANCH OR A DEPARTMENT, NOT AN ORGANISATION (T27). SEC's
            // own "Jubail Procurement <procurement@se.com.sa>" signs "Jubail", which reads as "Jubail Trading Est", and
            // "Dammam Procurement" reads as "Al Dammam Trading Co.". The containment rule in CompetesWith spared such a
            // signature only where the address repeated its city, so with "Saudi Electricity Company - Riyadh PP9
            // Substation" in the address the trading house was ranked first and SEC's own site fell to 0.70 (base
            // linked it at 0.88). A signature whose every distinctive word is a place or trade word
            // (CustomerAliasLearner.SectorAndPlaceWords) ties nothing. "Hyundai E&C" still does.
            var signatureWords = CustomerNameDistinctiveness.DistinctiveTokens(evidence.SenderOrganisationName);
            if (signatureWords.Any(word => !CustomerAliasLearner.SectorAndPlaceWords.Contains(word)))
            {
                var senderDomain = RoutingValueNormalizer.DomainFromEmail(evidence.SenderEmail?.Trim().ToLowerInvariant());
                if (!string.IsNullOrWhiteSpace(senderDomain) && guarded.Domains.Contains(senderDomain)
                    && readings().ReadsAsExactlyOne(evidence.SenderOrganisationName!) is { } signer)
                    ties.Add(new DomainTie(senderDomain, signer,
                        $"the mailbox is signed \"{evidence.SenderOrganisationName!.Trim()}\", which reads as {NameOf(signer)}",
                        TieKind.Signature,
                        SignatureWords: CustomerNameDistinctiveness.DistinctiveTokens(evidence.SenderOrganisationName)));
            }

            return new PageIdentityClaims(
                guarded.NameKey.Length > 0 ? evidence.CustomerCompanyName?.Trim() : null,
                owners,
                ties);

            string NameOf(long customerId) => names.TryGetValue(customerId, out var name) ? name : $"Customer #{customerId}";

            static bool SameDomain(string? email, string domain)
                => string.Equals(RoutingValueNormalizer.DomainFromEmail(email?.Trim()), domain, StringComparison.Ordinal);
        }

        /// <summary>
        /// What on this page names a different customer as the buyer, in the words a rep would use,
        /// or null when nothing does.
        /// </summary>
        /// <param name="consigneePassageKey">The key of the passage that names this consignee.</param>
        public Competition? CompetesWith(
            long customerId,
            string customerName,
            string consigneePassageKey,
            IReadOnlyList<Hit> headerStatements,
            IReadOnlyList<GuardedPassage> passages,
            IReadOnlyDictionary<long, string> names,
            CustomerResolutionPolicy policy)
        {
            // A header naming another customer. Not a one-word name: a header built from a person's
            // name ("Requisitioner: Ahmed Al-Ghamdi") reads GHAMDI for "Al-Ghamdi Trading", and
            // letting that push "Saudi Electricity Company" out of the address would turn a lead a
            // person must decide into a lead linked to the wrong company.
            var rivalHeader = headerStatements.FirstOrDefault(h => h.CustomerId != customerId && !h.OneWordName);
            if (rivalHeader is not null)
                return new Competition(
                    $"But the {passages[rivalHeader.PassageIndex].Where} names {NameOf(rivalHeader.CustomerId)}, " +
                    $"which is not {customerName}.",
                    []);

            if (_buyerNameOnDocument is not null && _nameOwners.Count > 0 && !_nameOwners.Contains(customerId))
                return new Competition(
                    $"But the document names \"{_buyerNameOnDocument}\" as the buying organisation, which is not {customerName}.",
                    []);

            // A domain speaks against this consignee only when it is tied to somebody else and to
            // nobody on this customer's own records. A signature is never one of those records: it may
            // add a rival, it never shields the consignee (an EPC mailbox signed with the site owner's
            // name is still the contractor's mailbox).
            if (_domainTies.Any(tie => tie.CustomerId == customerId && tie.Kind != TieKind.Signature)) return null;
            var rivals = _domainTies.Where(tie => tie.CustomerId != customerId && Speaks(tie)).ToList();
            if (rivals.Count == 0) return null;
            var rival = rivals[0];
            // The organisation actually writing is offered too, ranked above the site owner: on an
            // EPC enquiry it is the buyer, and a rep shown only the consignee would pick the wrong one.
            // Not where earlier decisions are all that speak: they are what a person chose before, and a
            // rep shown the earlier pick first repeats it, so it is offered below the named consignee.
            var offers = rivals
                .GroupBy(tie => tie.CustomerId)
                .Select(group =>
                {
                    var onlyDecisions = group.All(tie => tie.IsDecision);
                    var strongest = group.FirstOrDefault(tie => !tie.IsDecision) ?? group.First();
                    return onlyDecisions
                        ? new Hit(group.Key, Math.Min(policy.PriorSenderSuggestionConfidence, policy.ShipToDemotedConfidence),
                            CustomerMatchReasonCodes.PriorSender,
                            $"This document is from {strongest.Domain}, and {WhyOf(strongest)}.")
                        : new Hit(group.Key, policy.ShipToDemotedConfidence,
                            CustomerMatchReasonCodes.SenderDomain,
                            $"This document is from {strongest.Domain}, and {WhyOf(strongest)}.");
                })
                .ToList();
            return new Competition($"But this document is from {rival.Domain}, which is not {customerName}: {WhyOf(rival)}.", offers);

            bool Speaks(DomainTie tie) => tie.Kind switch
            {
                // A MAILBOX SIGNED WITH A WORD THE ADDRESS ALREADY SAYS IS DESCRIBING THE SITE, NOT NAMING A
                // BUYER. SEC's own department mailbox "Dammam Procurement <procurement@se.com.sa>" is signed
                // "Dammam", which reads as "Al Dammam Trading Co.", and that tie demoted lead 680's own
                // "Saudi Electricity Company-DAMMAM" to 0.70 with the trading house ranked first. When every
                // distinctive word of the signature is already in the consignee's own passage, the signature
                // adds nothing the address did not say. "Hyundai E&C" is not in "Saudi Aramco Ras Tanura
                // Refinery", so the contractor's signature still speaks.
                TieKind.Signature => !(tie.SignatureWords ?? []).All(word => ContainsWholeWords(consigneePassageKey, word)),
                // Another mailbox's decision speaks for the domain only when at least two different addresses
                // on it carry decisions and every one of them chose this rival.
                TieKind.DecisionFromAnotherMailbox => DecisionsOn(tie.Domain) is var decisions
                                                      && decisions.All(decision => decision.CustomerId == tie.CustomerId)
                                                      && decisions.Select(decision => decision.Address).Distinct(StringComparer.Ordinal).Count()
                                                         >= MailboxesThatMakeADecisionTheDomains,
                _ => true
            };

            List<DomainTie> DecisionsOn(string domain)
                => _domainTies.Where(tie => tie.IsDecision && string.Equals(tie.Domain, domain, StringComparison.Ordinal)).ToList();

            string WhyOf(DomainTie tie)
            {
                if (tie.Kind != TieKind.DecisionFromAnotherMailbox) return tie.Why;
                var addresses = DecisionsOn(tie.Domain).Select(decision => decision.Address).Distinct(StringComparer.Ordinal).ToList();
                return $"earlier leads from {string.Join(" and ", addresses)} were each resolved to {NameOf(tie.CustomerId)} by a person";
            }

            string NameOf(long id) => names.TryGetValue(id, out var name) ? name : $"Customer #{id}";
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
