using System.Text.RegularExpressions;
using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.Models;

namespace ERP_RFQ_Automation.CustomerResolution;

/// <summary>
/// Why a lead is (or is not) linked to a client organisation. Persisted on the lead and
/// surfaced verbatim to the rep, so "Nexora picked this client" is never unexplained.
/// </summary>
public static class CustomerMatchReasonCodes
{
    public const string SenderEmailExact = "SENDER_EMAIL_EXACT";
    public const string SenderDomain = "SENDER_DOMAIN";
    public const string ErpAccountExact = "ERP_ACCOUNT_EXACT";
    public const string TaxRegExact = "TAX_REG_EXACT";
    public const string LearnedAlias = "LEARNED_ALIAS";
    public const string LearnedPortalAccount = "LEARNED_PORTAL_ACCOUNT";
    public const string NameExactUnverified = "NAME_EXACT_UNVERIFIED";
    public const string NameInDocument = "NAME_IN_DOCUMENT";
    public const string NameFuzzy = "NAME_FUZZY";
    public const string RfqPattern = "RFQ_PATTERN";
    public const string PriorSender = "PRIOR_SENDER";
    public const string ContactPerson = "CONTACT_PERSON";
    public const string NoEvidence = "NO_EVIDENCE";
    public const string NoMatch = "NO_MATCH";
    public const string Ambiguous = "AMBIGUOUS";
    /// <summary>A human already decided; the machine deliberately did not touch the lead.</summary>
    public const string HumanResolved = "HUMAN_RESOLVED";
}

/// <summary>Source values written to <c>customer_identifiers."Source"</c> by this module.</summary>
public static class CustomerIdentifierSources
{
    /// <summary>
    /// Learned from a HUMAN correction in extraction review. Deliberately absent from
    /// <c>CustomerIdentityMaintenance.ManagedSources</c> so a later customer-profile sync
    /// cannot expire what a person taught the platform.
    /// </summary>
    public const string LeadReviewLearned = "LeadReviewLearned";

    /// <summary>What the Setup → Routing rules screen writes: a rule an administrator entered on purpose.</summary>
    public const string MasterData = "MasterData";

    /// <summary>
    /// What a reviewer confirmed that the rules would not trust, and what a correction took back. Since the
    /// owner's policy A decision (2026-09-13) the learner writes exactly two kinds of row here: a printed
    /// company name or a portal account the document does not tie to the chosen customer, filed at 0.50; and a
    /// Domain, alias or portal pair a relink demoted, which keeps its counts and confidence. It never writes an
    /// Email or a Domain here any more; a mailbox row on this shelf predates policy A. The row is kept so a
    /// person can look at it; it is NOT a fact. It is deliberately absent from
    /// <see cref="TrustedForAutoLink"/> and from <c>CustomerIdentityMaintenance.ManagedSources</c>.
    ///
    /// It lives here, beside the trusted sources, because several readers must skip it and they
    /// were about to spell it several times: the learner writes it; the resolver's S1, S3 and passage scan,
    /// the corpus loader and routing refuse it. S1 and S2 once never looked at Source at all, so a demoted
    /// row still linked at 1.00 or 0.95, and a misspelt copy of this string in any one reader would quietly
    /// reopen that hole. <c>CustomerAliasLearner.UnverifiedAliasSource</c> must stay equal to this value; a
    /// contract test pins it.
    /// </summary>
    public const string LeadReviewUnverified = "LeadReviewUnverified";

    /// <summary>
    /// Sources trusted enough for the learned-alias auto-link tier (S3). An administrator's own
    /// entry on the setup screen belongs here: a portal vendor code or an "also known as" typed
    /// in deliberately is at least as reliable as a reviewer's confirmation — and until it was
    /// listed, a rule entered there could route a lead but never link its customer.
    /// </summary>
    public static readonly string[] TrustedForAutoLink =
        [LeadReviewLearned, "CustomerProfile", "CustomerImport", MasterData];

    /// <summary>
    /// Sources a PERSON typed or imported on purpose: the setup screen, the customer profile and its
    /// contacts, a customer import. What the platform inferred is not here: a reviewer's confirmation
    /// the learner turned into a row, or the migration backfill. Read where a value is trusted only if
    /// somebody said so: a procurement relay's or a system mailbox's own sending address
    /// (<see cref="IdentityDomainGuard.MayMatchExactAddress"/>), and whether a customer is on record
    /// with a staff login's domain (<see cref="TenantSelfIdentity.LoadSelfDomainsAsync"/>).
    ///
    /// OWNER DECISION 2026-09-13, policy A ("learn slowly, never guess"): a whole email domain is never
    /// learned from confirmations. It comes only from a customer contact or an admin entry, so the Domain
    /// tier in the resolver, the loader and routing trust a Domain row only when its source is on this
    /// list. A LeadReviewLearned or MigrationBackfill Domain row, however old, links and routes nothing.
    /// A buyer's exact address is learned once reps confirm it for the same customer twice and never for
    /// anyone else; a company name or portal account only when the document itself names that customer.
    /// ASSUMPTION (sub-decision the owner did not answer, defaulted to the recommendation he was given):
    /// an Arabic-only company name is trusted after two confirmations for the same customer with none for
    /// another, or at once when the document's header or address names that customer.
    ///
    /// EF Core reads this array inside queries (<c>EnteredByAPerson.Contains(i.Source)</c>); in memory,
    /// use <see cref="IsEnteredByAPerson"/> so every reader compares the same way.
    /// </summary>
    public static readonly string[] EnteredByAPerson = [MasterData, "CustomerProfile", "CustomerContact", "CustomerImport"];

    /// <summary>
    /// True only when <paramref name="source"/> is one of <see cref="EnteredByAPerson"/>, compared
    /// ordinally: "masterdata" is not "MasterData", and null, empty and anything unlisted
    /// ("LeadReviewLearned", "LeadReviewUnverified", "MigrationBackfill") are false. One predicate
    /// for the resolver, the loader, routing and the exact-address guard (policy A, 2026-09-13).
    /// </summary>
    public static bool IsEnteredByAPerson(string? source)
        => source is not null && EnteredByAPerson.Contains(source, StringComparer.Ordinal);
}

/// <summary>
/// Values that look like customer evidence but are Nexora's own plumbing. Learning any of
/// these, or matching on them, would bind an entire ingestion door to one customer with a
/// single mistake — see FolderService/ManualUploadService/LeadUploaderService, which inject
/// <c>sec@system.com</c>, <c>aramco@system.com</c>, <c>manual@upload.com</c>,
/// <c>system@excel.upload</c>, and the historic <c>extraction@pipeline.local</c>.
/// Note <c>sec@system.com</c> is NOT Saudi Electricity Company — SEC is <c>se.com.sa</c>.
/// </summary>
public static class SyntheticIdentityGuard
{
    private static readonly HashSet<string> SyntheticDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "pipeline.local", "system.com", "upload.com", "excel.upload", "rfq.com",
        "localhost", "localhost.localdomain", "invalid", "nexora.invalid", "example.com"
    };

    /// <summary>
    /// Consumer mailbox providers. A shared free-mail domain is evidence about a PERSON,
    /// never about an organisation, so it may still match as an exact ADDRESS (A1) but must
    /// never be learned or matched as a Domain identifier (B1).
    ///
    /// Compared on the FIRST label, so "yahoo.com.sa" and "hotmail.co.uk" are caught without
    /// listing every country. The list was closed at twenty-one names, and a provider missing
    /// from it counted as an organisation: one confirmation on one bid a freight agent forwarded
    /// from agent@fastmail.com minted fastmail.com as that customer's Domain at 0.95, and every
    /// later fastmail sender, for any buyer at all, linked to the same customer.
    ///
    /// KNOWN QUIRK, deliberately not widened: "mail" is here for mail.com and mail.ru, and a
    /// first-label rule cannot tell those from a company's own mail host, so
    /// "mail.alquraishi.com.sa" also reads as free mail. That errs towards refusing: such a host
    /// is never learned or matched as a Domain, which costs a suggestion, never a wrong link. It
    /// is also why the ordinary words below sit on the whole-domain list instead of this one.
    /// </summary>
    private static readonly HashSet<string> FreeMailFirstLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "gmail", "googlemail", "hotmail", "outlook", "yahoo", "ymail", "icloud", "me",
        "aol", "protonmail", "proton", "live", "msn", "qq", "163", "126", "gmx",
        "mail", "yandex", "zoho", "rediffmail",
        "fastmail", "rocketmail", "windowslive", "tutanota", "tutamail", "zohomail",
        "mailfence", "hushmail", "posteo", "runbox", "btinternet"
    };

    /// <summary>
    /// Consumer providers whose name is too ordinary a word to compare as a first label. "hey",
    /// "mac", "pm", "web" and "post" begin real company hosts, and as first labels they would
    /// quietly stop those companies' registered Domain rows from ever linking again — the same
    /// harm sap.com did on the relay list. So these match the WHOLE domain only.
    /// </summary>
    private static readonly HashSet<string> FreeMailDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "hey.com", "tuta.io", "tuta.com", "pm.me", "mac.com", "aim.com",
        "email.com", "usa.com", "post.com", "consultant.com", "engineer.com", "inbox.com",
        "web.de", "t-online.de", "freenet.de", "list.ru", "bk.ru", "inbox.ru", "rambler.ru",
        "att.net", "sbcglobal.net", "comcast.net", "verizon.net",
        "emirates.net.ae", "eim.ae", "naver.com", "hanmail.net", "daum.net", "sina.com", "sohu.com",
        // Still missing on 2026-09-12, and each an "organisation" to the learner until then: DuckDuckGo's
        // address relay, Saudi Telecom's consumer ISP, and national webmail elsewhere. This list will
        // never be complete. An unlisted provider cannot become a customer's domain through confirmations,
        // because the learner never learns a Domain and S2 and routing read only a Domain row a person
        // entered (owner decision 2026-09-13, policy A); what this list still stops is a contact saved at
        // such a provider writing its domain (CustomerIdentityMaintenance).
        "duck.com", "awalnet.net.sa", "mail2world.com", "sina.cn", "inbox.lv", "seznam.cz", "wp.pl",
        "o2.pl", "interia.pl", "onet.pl", "libero.it", "virgilio.it", "orange.fr", "wanadoo.fr",
        "laposte.net", "bigpond.com", "optonline.net", "cox.net", "charter.net", "earthlink.net",
        "juno.com", "rediff.com",
        // Sahara Net, a Saudi consumer ISP: still an "organisation" on 2026-09-12, so a contact saved at
        // agent@sahara.com wrote sahara.com as that customer's Domain and every other Sahara subscriber
        // linked to it at 0.95.
        "sahara.com"
    };

    public static bool IsSyntheticDomain(string? domain)
        => !string.IsNullOrWhiteSpace(domain) && SyntheticDomains.Contains(domain.Trim());

    /// <summary>
    /// Procurement portals and e-sourcing networks that DELIVER a buyer's request from their
    /// own mail servers. <c>noreply@ariba.com</c> and <c>sourcing@etimad.sa</c> are the
    /// postman, not the buyer: every RFQ that arrives through SAP Ariba carries that same
    /// sender domain whichever company issued it. Learning one as a Domain identifier would
    /// auto-link the NEXT portal-delivered RFQ — from a completely different buyer — to
    /// whichever customer was taught first, and it would do it at S2's 0.95 domain
    /// confidence, which links without asking anyone.
    ///
    /// ONLY hosts that send on behalf of many buyers belong here. sap.com was listed, and it is
    /// SAP's own company domain: SAP Arabia buys from trading houses like any other customer and
    /// mails from sap.com. Because the resolver drops every relay domain before the domain tier,
    /// a customer an administrator had registered with Domain sap.com stopped linking at 0.95
    /// and came back NO_MATCH, with nothing else on the page to catch it. SAP Ariba's relay mail
    /// comes from ariba.com's sending hosts and sapariba.com, which stay listed. A software
    /// vendor's company domain is a buyer's domain like any other.
    /// </summary>
    private static readonly HashSet<string> PortalRelayDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "ariba.com", "ansmtp.ariba.com", "eusmtp.ariba.com", "sapariba.com",
        "etimad.sa", "tenders.gov.sa", "jaggaer.com", "coupahost.com", "tejari.com",
        "bidnet.com", "bidnetdirect.com", "demandstar.com", "bonfirehub.com"
    };

    /// <summary>
    /// True for a consumer mailbox provider. Accepts a bare domain or a whole address, like
    /// <see cref="IsPortalRelayDomain"/>: callers hold both shapes, and a guard that silently
    /// answers false for one of them is a hole.
    /// </summary>
    public static bool IsFreeMailDomain(string? domain)
    {
        var value = IdentityDomainGuard.DomainOf(domain);
        if (value is null) return false;
        if (FreeMailDomains.Contains(value)) return true;
        var first = value.Split('.', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return first is not null && FreeMailFirstLabels.Contains(first);
    }

    /// <summary>
    /// True when this domain belongs to a procurement portal that relays mail on behalf of
    /// many buyers. Exactly like a free-mail domain, the ADDRESS may still be real evidence
    /// and may still match exactly (A1) — a human who confirms that one portal mailbox
    /// belongs to one buyer is making a statement about one mailbox, and that costs nothing.
    /// It must never be generalised to the DOMAIN (B1), because the domain is shared by every
    /// buyer on the network.
    ///
    /// Subdomains count: a portal mints new sending hosts without telling anybody
    /// ("s4.ansmtp.ariba.com"), so anything under a listed domain is a relay too. The
    /// boundary is a real label separator, so "notariba.com" is not ariba.com.
    /// </summary>
    public static bool IsPortalRelayDomain(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return false;
        var value = domain.Trim().TrimEnd('.').ToLowerInvariant();
        // Tolerate being handed a whole address: callers hold both shapes and a silent false
        // here would be a hole in a guard.
        var at = value.LastIndexOf('@');
        if (at >= 0) value = value[(at + 1)..];
        if (value.Length == 0) return false;
        if (PortalRelayDomains.Contains(value)) return true;
        foreach (var relay in PortalRelayDomains)
        {
            if (value.Length > relay.Length
                && value[value.Length - relay.Length - 1] == '.'
                && value.EndsWith(relay, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>True when the address itself must be discarded outright.</summary>
    public static bool IsSyntheticAddress(string? email)
        => IsSyntheticDomain(RoutingValueNormalizer.DomainFromEmail(email));
}

/// <summary>
/// Sourcing networks where OUR supplier number is issued by the NETWORK, not by the buyer.
///
/// The learned-portal-account tier (S3) keys on the pair "portal|our-vendor-code" and links
/// at 0.92. At Saudi Electricity Company that pair is honest: vendor code 2004414 was issued
/// by SEC, means nothing anywhere else, and therefore names SEC. On SAP Ariba our Ariba
/// Network ID is ONE number that identifies US to every buyer on the network, and an Etimad
/// supplier number is one number for the whole Saudi government. Learn that pair against the
/// first Ariba buyer and every later Ariba RFQ — from any buyer at all — auto-links to that
/// one customer. Teach a second buyer and it is worse, not better: the pair then matches two
/// customers and every Ariba document is permanently AMBIGUOUS, a state no amount of further
/// teaching can undo.
///
/// The test for membership is "who issued the number", not "is it a portal". SEC's own
/// "MATERIALS E-BIDDING SYSTEM" is buyer-operated — one buyer runs it and issues the codes in
/// it — so it is exactly the case the tier was built for and MUST stay learnable.
/// </summary>
public static class SharedSupplierNetworks
{
    /// <summary>
    /// The network's own mark: the word, or the run of words, that no spelling of that
    /// network's name leaves out. Public so a test can state the whole list, and so the reason
    /// any one portal sits on it can be argued about in review. The Arabic entry is Etimad as
    /// the Saudi government writes it ("منصة اعتماد").
    /// </summary>
    public static readonly IReadOnlyList<string> Names =
    [
        "ARIBA", "SAP BUSINESS NETWORK", "ORACLE SUPPLIER NETWORK",
        "ETIMAD", "ETIMAAD", "ITIMAD", "اعتماد",
        "COUPA", "JAGGAER", "TEJARI", "TENDERBOARD", "PROCUREPORT"
    ];

    /// <summary>
    /// Each mark as <see cref="CustomerNameNormalizer.LooseKey"/> words — the same key the
    /// portal name is matched under everywhere else, so punctuation, casing and diacritics
    /// (the hamza in "إعتماد") cannot smuggle a spelling past.
    /// </summary>
    private static readonly string[][] Marks =
        Names.Select(CustomerNameNormalizer.LooseKey)
             .Where(key => key.Length > 0)
             .Select(key => key.Split(' ', StringSplitOptions.RemoveEmptyEntries))
             .ToArray();

    /// <summary>
    /// True when a supplier number on this portal was issued by the network rather than by
    /// the buyer, so the "portal|our-vendor-code" pair identifies NOBODY and must neither be
    /// learned nor matched.
    ///
    /// A mark matches as WHOLE WORDS anywhere in the name, not as the whole name. The list was
    /// compared by exact key, and the extractor does not write a portal's name one way:
    /// "SAP Ariba Sourcing", "Ariba Discovery", "Coupa Supplier Portal", "Etimad Portal" each
    /// missed it, so the learner wrote the pair as a verified 0.92 fact for the first buyer and
    /// the next Ariba RFQ from any buyer linked to that customer. Whole words, not letters:
    /// "SARIBA" is not ARIBA and "COUPANG" is not COUPA. Erring towards "shared" costs a
    /// buyer-operated portal one learnable pair; erring the other way costs wrong links.
    /// </summary>
    public static bool IsShared(string? portalName)
    {
        var key = CustomerNameNormalizer.LooseKey(portalName);
        if (key.Length == 0) return false;
        var words = key.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var mark in Marks)
            if (ContainsRun(words, mark)) return true;

        // GLUED OR SPLIT. Whole words still let "SAPAriba", "AribaNetwork", "CoupaHost",
        // "SAP BusinessNetwork", "Tender Board", "e-Timad" and the Arabic "الاعتماد" through, and each
        // was learned as a verified 0.92 pair for the first buyer confirmed on it. So neighbouring
        // words are joined back up and compared with the mark written solid, allowing only the few
        // things that are glued to a network's name in practice (<see cref="GluedPrefixes"/>,
        // <see cref="GluedSuffixes"/>). Anything else glued on is a different word: "SARIBA" and
        // "COUPANG" stay buyers' own portals.
        for (var start = 0; start < words.Length; start++)
        {
            var joined = string.Empty;
            for (var end = start; end < words.Length && end < start + MaximumJoinedWords; end++)
            {
                joined += words[end];
                if (IsMarkWithAffixes(joined)) return true;
            }
        }
        return false;
    }

    /// <summary>How many neighbouring words are joined back up when looking for a split mark.</summary>
    private const int MaximumJoinedWords = 4;

    /// <summary>What may stand in front of a mark inside one word: nothing, SAP, or the Arabic article.</summary>
    private static readonly string[] GluedPrefixes = ["", "SAP", CustomerNameNormalizer.LooseKey("ال")];

    /// <summary>What may follow a mark inside one word: the words a network's own sites and products add.</summary>
    private static readonly string[] GluedSuffixes =
        ["", "NETWORK", "NET", "HOST", "PORTAL", "SOURCING", "DISCOVERY", "SUPPLIER", "SUPPLIERS", "PLATFORM"];

    private static readonly string[] GluedMarks =
        Marks.Select(mark => string.Concat(mark)).Distinct(StringComparer.Ordinal).ToArray();

    private static bool IsMarkWithAffixes(string joined)
    {
        foreach (var mark in GluedMarks)
        {
            for (var at = joined.IndexOf(mark, StringComparison.Ordinal);
                 at >= 0;
                 at = joined.IndexOf(mark, at + 1, StringComparison.Ordinal))
            {
                if (GluedPrefixes.Contains(joined[..at], StringComparer.Ordinal)
                    && GluedSuffixes.Contains(joined[(at + mark.Length)..], StringComparer.Ordinal))
                    return true;
            }
        }
        return false;
    }

    private static bool ContainsRun(string[] words, string[] run)
    {
        for (var start = 0; start + run.Length <= words.Length; start++)
        {
            var matched = true;
            for (var offset = 0; offset < run.Length && matched; offset++)
                matched = string.Equals(words[start + offset], run[offset], StringComparison.Ordinal);
            if (matched) return true;
        }
        return false;
    }
}

/// <summary>
/// The direction-of-trade firewall, shared by the resolver and the learner so they can never
/// disagree about what counts as "us".
///
/// On the production corpus the ONLY company name printed on a Saudi Electricity Company bid
/// is the trading house that RECEIVED it ("Vendname: ALI ZAID AL-QURAISHI&amp;PARTNERS EL",
/// "A.Z. ALQURAISHI &amp; PARTNERS ESOSA"). A naive customer-name field returns our own name,
/// and every SEC lead links to a customer record of ourselves.
///
/// This is a REJECTION rule, so it is deliberately generous where the MATCHING rules are
/// strict: an over-eager reject costs an unresolved lead, an under-eager one costs a lead
/// linked to the wrong company. Containment and a high similarity floor both count, because
/// real documents attach noise to our name that an exact key comparison sails past.
/// </summary>
public static class SelfIdentityGuard
{
    /// <summary>Shortest tight key distinctive enough to reject on.</summary>
    private const int MinimumDistinctiveLength = 6;

    public static bool IsSelfName(string? candidateKey, IEnumerable<string> selfNameKeys)
    {
        if (string.IsNullOrEmpty(candidateKey)) return false;
        var tight = CustomerNameNormalizer.TightKey(candidateKey);
        if (tight.Length == 0) return false;

        // The floor has to be SYMMETRIC, because the containment test is. It was applied only
        // to the tenant side, and then containment ran in both directions, so any short
        // candidate that happened to be spelled by a run of letters inside our own name was
        // deleted as "us". With the tenant "ALI ZAID AL-QURAISHI & PARTNERS"
        // (ALIZAIDALQURAISHI) that is AID, ZAI, RAI, SHI and a dozen more: a customer called
        // "Arabian Industrial Development" whose initials are AID could never be found in a
        // document, and because this is a rejection rule it happened permanently and wrote no
        // log — the customer simply never appeared again and nobody could say why.
        // Three letters are not distinctive enough to REJECT on, exactly as they are not
        // distinctive enough to MATCH on, so below the floor only the two comparisons that
        // need no distinctiveness are allowed to speak: exact key equality (a candidate that
        // IS our name, however short) and the high-similarity check.
        var candidateIsDistinctive = tight.Length >= MinimumDistinctiveLength;

        foreach (var self in selfNameKeys)
        {
            if (string.Equals(self, candidateKey, StringComparison.Ordinal)) return true;
            var selfTight = CustomerNameNormalizer.TightKey(self);
            if (selfTight.Length < MinimumDistinctiveLength) continue;
            if (candidateIsDistinctive)
            {
                if (tight.Contains(selfTight, StringComparison.Ordinal)) return true;
                if (selfTight.Contains(tight, StringComparison.Ordinal)) return true;
            }
            if (CustomerNameNormalizer.JaroWinkler(tight, selfTight) >= 0.90d) return true;
        }
        return false;
    }
}

/// <summary>
/// Which words of a company name actually say WHICH company. Shared by the learner's alias
/// gate and the resolver's taught-alias scan, so the two can never disagree about whether
/// "SAUDI ARABIA" is a name.
///
/// Two defects from one missing test. The learner accepted a printed name as a trusted alias
/// when Jaro-Winkler on the joined letters scored 0.75, and the first four letters carry a
/// bonus: every pair of names that both open with SAUDI, ARABIAN or NATIONAL clears it
/// ("SAUDI ELECTRICITY" against "Saudi Aramco" scores 0.79), so a reviewer's mis-click became
/// a verified alias for the wrong company. And a company-name field that held only the country
/// line ("SAUDI ARABIA") was taught as an alias, after which the passage scan linked every
/// document mentioning Saudi Arabia to that customer at 0.88. Both are the same question —
/// does this name have a word in it that belongs to one company — and it is answered here once.
/// </summary>
public static class CustomerNameDistinctiveness
{
    /// <summary>Words shorter than this are initials fragments or articles, not a name.</summary>
    public const int MinimumTokenLength = 3;

    /// <summary>
    /// Words that half the buyers in the Kingdom carry. Geography, the words every national or
    /// international company adds, and articles and conjunctions. Deliberately NOT sector words
    /// such as ELECTRICITY or WATER: then "Saudi Electricity Company" would have no distinctive
    /// word at all, and any caller that asks this before scanning would lose lead 680. City
    /// names are not here either; the list of them has no end, and a customer called "Al Dammam
    /// Trading" really is distinguished by DAMMAM. Arabic entries are folded through LooseKey
    /// below, exactly like the names they are compared with.
    /// </summary>
    private static readonly HashSet<string> GenericTokens = new[]
    {
        "SAUDI", "ARABIA", "ARABIAN", "ARAB", "ARABIC", "KSA", "KINGDOM", "NATIONAL",
        "GULF", "MIDDLE", "EAST", "WEST", "NORTH", "SOUTH", "EASTERN", "WESTERN", "NORTHERN",
        "SOUTHERN", "CENTRAL", "PROVINCE", "REGION", "REGIONAL", "GCC", "MENA", "EMIRATES",
        "INTERNATIONAL", "GLOBAL", "GENERAL", "UNITED", "WORLDWIDE", "OVERSEAS",
        "AL", "EL", "THE", "AND", "OF", "FOR",
        "شركة", "الشركة", "مؤسسة", "المؤسسة", "السعودية", "السعودي", "العربية", "العربي",
        "المملكة", "الوطنية", "الوطني", "الخليج", "المحدودة", "ذات", "مسؤولية", "المسؤولية",
        "للتجارة"
    }
    .Select(CustomerNameNormalizer.LooseKey)
    .Where(key => key.Length > 0)
    .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// A word <see cref="CustomerNameNormalizer.LooseKey"/> never strips. Prefixed to a key, it
    /// makes LooseKey report whether the key was made of nothing but legal-form words: LooseKey
    /// keeps "TRADING COMPANY" whole only because nothing else survives, and with the sentinel
    /// in front it strips them and returns the sentinel alone. That answers the question without
    /// a second copy of the private noise list, which would drift the first time either changed.
    /// </summary>
    private const string Sentinel = "Q";

    /// <summary>
    /// The words of the name that name one company, in order, without repeats: the LooseKey
    /// words minus legal-form words, generic geography and business words, words shorter than
    /// <see cref="MinimumTokenLength"/>, and words with no letter in them. Accepts a raw name
    /// or a key already produced by LooseKey.
    /// </summary>
    public static IReadOnlyList<string> DistinctiveTokens(string? name)
    {
        var key = CustomerNameNormalizer.LooseKey(name);
        if (key.Length == 0) return [];
        if (string.Equals(CustomerNameNormalizer.LooseKey($"{Sentinel} {key}"), Sentinel, StringComparison.Ordinal))
            return [];

        var tokens = new List<string>();
        foreach (var token in key.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length < MinimumTokenLength) continue;
            if (!token.Any(char.IsLetter)) continue;
            if (GenericTokens.Contains(token)) continue;
            if (!tokens.Contains(token, StringComparer.Ordinal)) tokens.Add(token);
        }
        return tokens;
    }

    /// <summary>True when at least one word of the name belongs to one company rather than to half the country.</summary>
    public static bool HasDistinctiveToken(string? name) => DistinctiveTokens(name).Count > 0;

    /// <summary>
    /// True when the two names share a distinctive word. "SAUDI ELECTRICITY" and "Saudi Aramco"
    /// share only SAUDI, so false. An article glued to the word counts as the same word
    /// ("ALRAJHI" and "Al Rajhi"), because that is how the same family name is printed both
    /// ways, and LooseKey drops a leading article from one spelling but not the other.
    ///
    /// This is a necessary condition for calling a printed name a resemblance of a customer, not
    /// a sufficient one: two customers can share a distinctive word too, and a caller that must
    /// know which customer a name is closer to has to ask that as well.
    /// </summary>
    public static bool SharesDistinctiveToken(string? left, string? right)
    {
        var leftTokens = DistinctiveTokens(left);
        if (leftTokens.Count == 0) return false;
        var rightTokens = DistinctiveTokens(right);
        foreach (var a in leftTokens)
            foreach (var b in rightTokens)
                if (SameWord(a, b)) return true;
        return false;
    }

    private static bool SameWord(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal)) return true;
        return GluedArticle(a, b) || GluedArticle(b, a);

        static bool GluedArticle(string glued, string bare) =>
            glued.Length == bare.Length + 2
            && (glued.StartsWith("AL", StringComparison.Ordinal) || glued.StartsWith("EL", StringComparison.Ordinal))
            && glued.EndsWith(bare, StringComparison.Ordinal);
    }
}

/// <summary>
/// Whether an e-mail domain may stand for an ORGANISATION in identity work: learned as a
/// customer's Domain, matched by the domain tier, or read by routing. One predicate, because
/// the learner, the resolver and routing each carried their own subset of the checks, and the
/// gap between them was the defect: routing had none of them, so a legacy bidnet.com Domain row
/// the resolver refused still wrote a customer onto the lead through routing.
/// </summary>
public static class IdentityDomainGuard
{
    /// <summary>
    /// The bare, lower-case domain of a domain or an address, in any shape callers hold:
    /// "se.com.sa", "57322@se.com.sa", "Ali Nasser &lt;ali@se.com.sa&gt;", "SE.COM.SA.". Null when
    /// there is no domain. Normalised by <see cref="RoutingValueNormalizer"/>, the rule the stored
    /// Domain rows were written with, so a lookup and a stored value cannot differ by a trailing
    /// dot or a "www.".
    /// </summary>
    public static string? DomainOf(string? domainOrAddress)
    {
        if (string.IsNullOrWhiteSpace(domainOrAddress)) return null;
        var value = domainOrAddress.Trim();
        var at = value.LastIndexOf('@');
        if (at >= 0) value = value[(at + 1)..];
        value = value.Trim().TrimEnd('>').Trim();
        if (value.Length == 0) return null;
        var domain = RoutingValueNormalizer.Normalize(CustomerIdentifierType.Domain, value);
        return domain.Length == 0 || domain.Any(char.IsWhiteSpace) ? null : domain;
    }

    /// <summary>
    /// True when the domain is one of the tenant's own, or a host under one of them
    /// ("sales.alquraishi.com.sa" under "alquraishi.com.sa"). The boundary is a real label
    /// separator, so "notalquraishi.com.sa" is somebody else. Entries may be domains or whole
    /// addresses, because the evidence carries mailbox addresses.
    /// </summary>
    public static bool IsSelfDomain(string? domainOrAddress, IEnumerable<string>? selfDomains)
    {
        var domain = DomainOf(domainOrAddress);
        if (domain is null || selfDomains is null) return false;
        foreach (var entry in selfDomains)
        {
            var own = DomainOf(entry);
            // A dotless entry would make every host under that label "ours".
            if (own is null || !own.Contains('.')) continue;
            if (string.Equals(domain, own, StringComparison.Ordinal)) return true;
            if (domain.Length > own.Length + 1
                && domain[domain.Length - own.Length - 1] == '.'
                && domain.EndsWith(own, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Whether an Email identifier may match an address exactly. A row a person entered
    /// (<see cref="CustomerIdentifierSources.EnteredByAPerson"/>) always may. Any other row may not
    /// when the address is a procurement network's relay host, or when it is a system mailbox
    /// (<see cref="IsSystemMailbox"/>) on any host at all.
    ///
    /// ordersender-prod@ansmtp.ariba.com delivers every Ariba buyer's RFQ, and the old learner minted it
    /// as the Email of whichever buyer was confirmed first: a SABIC RFQ then linked to Saudi Aramco at
    /// 1.00 in the resolver, and routing handed it to Aramco's owner, however plainly the page named
    /// SABIC. Refusing learned rows on LISTED relay hosts closed that for Ariba and left it open
    /// everywhere else: a learned no-reply@etimad.gov.sa or do_not_reply@coupa.com row still decided
    /// at 1.00, because neither host is on the relay list and no list of hosts will ever be complete.
    /// A mailbox nobody answers is the postman on whatever host it sits, so the local part decides
    /// too. Rows the learner or the backfill wrote on such mailboxes become inert with no data job.
    /// One predicate for the resolver and routing.
    /// </summary>
    public static bool MayMatchExactAddress(string? address, string? source)
    {
        if (CustomerIdentifierSources.IsEnteredByAPerson(source))
            return true;
        return !SyntheticIdentityGuard.IsPortalRelayDomain(DomainOf(address)) && !IsSystemMailbox(address);
    }

    /// <summary>
    /// The local parts of a mailbox that no person reads: a platform's or a portal's sending address.
    /// Compared as the WHOLE local part, case-insensitively, never as a substring, so a buyer's own
    /// "noreply.desk@" or "procurement.notifications@" is somebody's mailbox and stays evidence.
    /// </summary>
    private static readonly HashSet<string> SystemMailboxLocalParts = new(StringComparer.OrdinalIgnoreCase)
    {
        "noreply", "no-reply", "no_reply", "donotreply", "do-not-reply", "do_not_reply",
        "notification", "notifications", "mailer-daemon"
    };

    /// <summary>A local-part prefix every SAP Ariba sending address carries ("ordersender-prod").</summary>
    private const string OrderSenderPrefix = "ordersender";

    /// <summary>
    /// True when the address's whole local part is a system mailbox (<see cref="SystemMailboxLocalParts"/>)
    /// or begins with "ordersender". Accepts a bare address or "Name &lt;address&gt;". False for anything
    /// with no local part.
    /// </summary>
    public static bool IsSystemMailbox(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return false;
        var value = address.Trim();
        var at = value.LastIndexOf('@');
        if (at <= 0) return false;
        var local = value[..at];
        var open = local.LastIndexOf('<');
        if (open >= 0) local = local[(open + 1)..];
        local = local.Trim().Trim('"', '\'').Trim();
        if (local.Length == 0) return false;
        return SystemMailboxLocalParts.Contains(local)
               || local.StartsWith(OrderSenderPrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True only for a domain that can belong to one organisation that is not us. False for:
    /// nothing at all or a dotless or letterless host; Nexora's own ingestion placeholders
    /// (pipeline.local, system.com); a consumer mailbox provider (gmail.com, fastmail.com); a
    /// procurement network's relay (ariba.com, etimad.sa); and the tenant's own domains, passed
    /// as <paramref name="selfDomains"/> (domains or addresses; see
    /// <see cref="TenantSelfIdentity.LoadSelfDomainsAsync"/>).
    ///
    /// True does NOT mean the domain belongs to the customer a reviewer picked. An EPC
    /// contractor's hdec.com is an organisation domain and is not Saudi Aramco's; whether a
    /// domain is tied to a particular customer is a separate question the caller must ask.
    /// </summary>
    public static bool IsOrganisationDomain(string? domainOrAddress, IEnumerable<string>? selfDomains = null)
    {
        var domain = DomainOf(domainOrAddress);
        if (domain is null || !domain.Contains('.') || !domain.Any(char.IsLetter)) return false;
        if (SyntheticIdentityGuard.IsSyntheticDomain(domain)) return false;
        if (SyntheticIdentityGuard.IsFreeMailDomain(domain)) return false;
        if (SyntheticIdentityGuard.IsPortalRelayDomain(domain)) return false;
        return !IsSelfDomain(domain, selfDomains);
    }
}

/// <summary>
/// Everything the platform knows about WHO sent this lead, as raw values. The resolver —
/// not the caller — applies the direction-of-trade and synthetic guards, so those guards
/// are unit-testable without a database.
/// </summary>
public sealed record LeadClientEvidence
{
    public long BusinessUnitId { get; init; }
    public long LeadId { get; init; }

    /// <summary>RFC-parsed sender address (<c>EmailIngests."FromEmail"</c> may be
    /// <c>"Ali Nasser &lt;ali@se.com.sa&gt;"</c>; a raw string compare never matches).</summary>
    public string? SenderEmail { get; init; }

    /// <summary>
    /// Buyer e-mail printed ON the document (SEC bids carry <c>57322@se.com.sa</c>). For a
    /// folder-ingested or scanned bid this is the ONLY place the buying organisation's real
    /// domain appears, so it is treated as address/domain evidence exactly like the sender.
    /// </summary>
    public string? DocumentBuyerEmail { get; init; }

    /// <summary>Portal / ERP account references printed per line (CompanyRef, CustomerAccountPortalId).</summary>
    public IReadOnlyList<string> AccountReferences { get; init; } = [];

    /// <summary>CR / VAT / commercial-registration number of the BUYER, verbatim.</summary>
    public string? CustomerRegistrationId { get; init; }

    /// <summary>The BUYING organisation as printed on the document.</summary>
    public string? CustomerCompanyName { get; init; }

    /// <summary>Buying portal / template ("MATERIALS E-BIDDING SYSTEM", "Ariba", "Etimad").</summary>
    public string? CustomerPortalName { get; init; }

    /// <summary>OUR vendor code at the customer (e.g. SEC vendor code 2004414).</summary>
    public string? SupplierAccountRefOnDocument { get; init; }

    /// <summary>OUR name as printed in the document's Vendor/Vendname block. Never a customer.</summary>
    public string? SupplierNameOnDocument { get; init; }

    public string? RfqNumber { get; init; }

    /// <summary>The buyer PERSON (Leads.BuyersName). Never an organisation.</summary>
    public string? BuyerPersonName { get; init; }

    /// <summary>
    /// The organisation the sender's own mailbox is signed with, with the buying-function words
    /// taken off ("Hyundai E&amp;C" out of "Hyundai E&amp;C Procurement &lt;procurement@hdec.com&gt;").
    /// Set only for an organisation's own domain, never for a person's name, a consumer mailbox, a
    /// relay or our own mail. See <c>LeadCustomerResolutionService.SenderOrganisationName</c>.
    /// </summary>
    public string? SenderOrganisationName { get; init; }

    /// <summary>
    /// Text the document states that may carry the buyer's own name — the delivery address,
    /// a storage location, an item's text. A customer writes its name on its own documents
    /// whatever system printed them, so this is the evidence that survives a portal change.
    /// </summary>
    public IReadOnlyList<DocumentPassage> Passages { get; init; } = [];

    /// <summary>LooseKeys of the tenant's own trading names — nothing may ever match these.</summary>
    public IReadOnlyCollection<string> TenantSelfNameKeys { get; init; } = [];

    /// <summary>The tenant's own mail domains — nothing may ever match these.</summary>
    public IReadOnlyCollection<string> TenantSelfDomains { get; init; } = [];
}

/// <summary>
/// What a passage is DOING on the page.
///
/// One boolean was carrying two different statements and they are not the same statement.
/// "This company is buying" and "the goods go to this address" agree on most Saudi bids,
/// because a buyer ships to its own plant — and they part company on exactly the deals worth
/// the most money. An EPC contractor buying on behalf of Saudi Aramco prints its own name in
/// the header and "Deliver to: Saudi Aramco, Ras Tanura" in the address, and it IS NOT
/// Aramco. Reading the consignee as the buyer links that lead to Aramco at full
/// name-in-address confidence, and the real customer — named at the top of the page — never
/// appears at all.
/// </summary>
public enum PassageRole
{
    /// <summary>
    /// A statement about WHO IS BUYING: the company-name field, the letterhead, the sentence
    /// the extractor captured because it names the buying organisation ("MARAFIQ invites
    /// bidders in accordance with our Request for Quotation"). The strongest name evidence a
    /// document carries about identity.
    /// </summary>
    BuyerHeader,

    /// <summary>
    /// WHERE THE GOODS GO: a delivery address, a storage location, a site or plant field.
    /// It names the CONSIGNEE. Usually that is the buyer writing its own address — on an SEC
    /// portal print it is the ONLY place the buyer's name appears anywhere (lead 680 carried
    /// nothing but "Saudi Electricity Company-DAMMAM") — but a consignee is a fact about
    /// delivery, not about who is paying.
    /// </summary>
    ShipTo,

    /// <summary>
    /// Incidental text: an item description, a material long text. A company name here may
    /// belong to anyone ("AFFIX SEC SPECIFIED BARCODE" is a specification, not a buyer).
    /// Suggestion-grade at best.
    /// </summary>
    ItemText
}

/// <param name="Where">Where the text sits, in the words a person would use: "delivery address", "storage location", "item text".</param>
/// <param name="Text">The text as the document states it.</param>
/// <param name="NamesTheBuyer">True where the passage is ABOUT the buyer (an address, a site); false for item text, where a name may be incidental.</param>
public sealed record DocumentPassage(string Where, string Text, bool NamesTheBuyer)
{
    /// <summary>
    /// The <see cref="Where"/> of the passage made from the sender's mailbox display name. The resolver asks for
    /// it by this value, because one word of a customer's name on a mailbox ("Rashid", "Dammam Procurement") is a
    /// person or a department, not that customer, and must not be read as one.
    /// </summary>
    public const string SenderDisplayNameWhere = "the name on the sender's mailbox";

    /// <summary>
    /// What this passage is doing on the page. Derived from <see cref="NamesTheBuyer"/> so
    /// every existing caller compiles and behaves exactly as before: a passage that "names
    /// the buyer" has always in practice meant a delivery address or a site, which is
    /// <see cref="PassageRole.ShipTo"/>. A caller that KNOWS it is reading a header says so
    /// with an object initializer — <c>new DocumentPassage(..., true) { Role =
    /// PassageRole.BuyerHeader }</c>.
    ///
    /// Record copy semantics: <c>passage with { NamesTheBuyer = false }</c> copies the Role
    /// that is already there rather than re-deriving it, so set Role explicitly whenever you
    /// flip the flag.
    /// </summary>
    public PassageRole Role { get; init; } = NamesTheBuyer ? PassageRole.ShipTo : PassageRole.ItemText;
}

public sealed record CustomerIdentifierSnapshot(
    long Id,
    long CustomerId,
    CustomerIdentifierType IdentifierType,
    string NormalizedValue,
    bool IsVerified,
    decimal Confidence,
    string Source);

public sealed record CustomerNameSnapshot(long CustomerId, string Name);

public sealed record CustomerContactSnapshot(
    long ContactId, long CustomerId, string? Email, string? FirstName, string? LastName);

public sealed record PriorSenderResolution(string SenderEmail, long CustomerId);

/// <summary>The tenant-scoped read model the pure resolver runs against.</summary>
public sealed record ClientResolutionCorpus
{
    public IReadOnlyList<CustomerIdentifierSnapshot> Identifiers { get; init; } = [];
    public IReadOnlyList<CustomerNameSnapshot> Customers { get; init; } = [];
    public IReadOnlyList<CustomerContactSnapshot> Contacts { get; init; } = [];
    public IReadOnlyList<PriorSenderResolution> PriorSenderResolutions { get; init; } = [];
}

public sealed record ClientMatchCandidate(
    int Rank,
    long CustomerId,
    string CustomerName,
    decimal Confidence,
    string ReasonCode,
    string Explanation);

public sealed record ClientResolutionOutcome(
    string Status,
    long? CustomerId,
    long? ContactId,
    decimal Confidence,
    string ReasonCode,
    string Explanation,
    IReadOnlyList<ClientMatchCandidate> Candidates)
{
    public bool Linked => CustomerId.HasValue;

    public static ClientResolutionOutcome Unresolved(string reasonCode, string explanation) =>
        new(LeadCustomerMatchStatuses.Unresolved, null, null, 0m, reasonCode, explanation, []);
}

/// <summary>Tunable thresholds. Registered as a singleton so a wrong rule is one edit away.</summary>
public sealed record CustomerResolutionPolicy
{
    /// <summary>Largest customer set the fuzzy stage will scan in memory before pre-filtering in SQL.</summary>
    public int MaximumNameScanRows { get; init; } = 2_000;

    /// <summary>Jaro-Winkler floor on TightKey for a fuzzy SUGGESTION. Never auto-links.</summary>
    public double FuzzyNameThreshold { get; init; } = 0.90d;

    /// <summary>
    /// The line between "Nexora decided" and "a rep decides". Every confidence below is
    /// chosen against it: 0.88 for a customer's full name in the delivery address links a
    /// lead on its own, 0.75 for an unverified name match only suggests one. It protects the
    /// governing rule of this module — a WRONG customer on a lead is worse than an unresolved
    /// one — by making the auto-link threshold one number a person can read, instead of a
    /// property of whichever tier happened to fire.
    /// </summary>
    public decimal MinimumAutoLinkConfidence { get; init; } = 0.85m;

    /// <summary>
    /// How many human decisions naming ONE customer, the current review included, make a buyer's exact
    /// address that customer's Email identifier, provided no decision names another customer. Since the
    /// owner's policy A decision (2026-09-13) this covers EVERY learnable address, corporate
    /// (57322@se.com.sa) as well as consumer mailbox (buyer.person@gmail.com); the name is historic.
    /// Nothing is written before this count is reached, and the address is written verified when it is
    /// (CustomerAliasLearner reads this setting). One is not enough: that is how Saudi Aramco came to own
    /// personal addresses at live.com from a single confirmation each. And never a Domain, at any count:
    /// a domain comes only from a customer contact or an admin entry
    /// (<see cref="CustomerIdentifierSources.EnteredByAPerson"/>). A buyer confirmed this many times is
    /// linked on the next message, which is the third: the accepted cost of policy A.
    /// </summary>
    public int FreeMailAddressConfirmationsRequired { get; init; } = 2;

    public decimal FuzzyMaximumConfidence { get; init; } = 0.85m;
    public decimal ExactNameSuggestionConfidence { get; init; } = 0.75m;
    /// <summary>A known customer's full name written in the document's delivery address or site: links.</summary>
    public decimal NameInAddressConfidence { get; init; } = 0.88m;
    /// <summary>
    /// What a name found ONLY in a ship-to passage is worth once the page names a DIFFERENT
    /// company as the buyer. It protects the contractor case: an EPC contractor's RFQ carries
    /// its own name in the header and "deliver to Saudi Aramco" in the address, and without a
    /// demotion the address wins at 0.88 and the lead is linked to the site owner, who is not
    /// buying anything. Deliberately below <see cref="MinimumAutoLinkConfidence"/>, so the
    /// consignee becomes a suggestion a rep can accept rather than a decision made for them.
    /// </summary>
    public decimal ShipToDemotedConfidence { get; init; } = 0.70m;
    /// <summary>The same name, or a taught alias, inside an item's text: a suggestion, the name may be incidental.</summary>
    public decimal NameInItemTextConfidence { get; init; } = 0.70m;
    /// <summary>
    /// The customer's initials ("SEC") found as a whole word in a passage about the buyer.
    /// Weaker than the full name, because three letters can belong to more than one company,
    /// but written by the buyer about themselves — strong enough to link when only one
    /// customer's initials fit. Two customers' initials in the same address stay AMBIGUOUS.
    /// </summary>
    public decimal NameAcronymInAddressConfidence { get; init; } = 0.85m;
    /// <summary>The initials inside item text: a suggestion only.</summary>
    public decimal NameAcronymInItemTextConfidence { get; init; } = 0.65m;
    public decimal PriorSenderSuggestionConfidence { get; init; } = 0.65m;
    public decimal ContactPersonSuggestionConfidence { get; init; } = 0.60m;
    public decimal RfqPatternSuggestionConfidence { get; init; } = 0.55m;
    /// <summary>
    /// How many characters an ERP/portal account number must carry before it is allowed to link a
    /// lead by itself. An SAP company code is four characters and is shared by every affiliate of a
    /// group, so matching one at authoritative confidence claims eleven companies at once.
    /// </summary>
    public int MinimumErpAccountLength { get; init; } = 5;

    public decimal AuthoritativeConfidence { get; init; } = 1.00m;
    public decimal DomainConfidence { get; init; } = 0.95m;
    public decimal LearnedPortalAccountConfidence { get; init; } = 0.92m;
    public decimal LearnedAliasConfidence { get; init; } = 0.90m;

    public int MaximumCandidates { get; init; } = 5;

    /// <summary>How many candidates the list projection carries per row.</summary>
    public int ListCandidateCount { get; init; } = 3;
}

/// <summary>
/// Shapes an RFQ number into a learnable pattern: digit runs become <c>\d{n}</c>, letter
/// runs stay literal. "C001046556" -&gt; "^C\d{9}$". Patterns are generated here and never
/// accepted from input, so matching them back is bounded and safe.
/// </summary>
public static partial class RfqNumberPattern
{
    /// <summary>
    /// Every pattern matched here was produced by <see cref="Derive"/>: anchored at both ends,
    /// literal letters, counted digit runs, a single negated class — no alternation, no
    /// backreference, no lookaround, nothing that can backtrack. The non-backtracking engine
    /// runs them in time linear in the input, which is what lets the match timeout go.
    ///
    /// The timeout was not merely unnecessary, it was a correctness bug: a
    /// RegexMatchTimeoutException was caught and reported as "does not match", so the same RFQ
    /// number could match on an idle machine and fail to match on a loaded one. This module's
    /// whole promise is that the same evidence gives the same answer forever; an answer that
    /// depends on CPU load breaks that silently, and nobody can reproduce it afterwards.
    /// </summary>
    private const RegexOptions MatchOptions = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;

    public static string? Derive(string? rfqNumber)
    {
        var trimmed = rfqNumber?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length is < 4 or > 64) return null;

        var builder = new System.Text.StringBuilder("^");
        var index = 0;
        var hasDigitRun = false;
        while (index < trimmed.Length)
        {
            var c = trimmed[index];
            if (char.IsDigit(c))
            {
                var run = 0;
                while (index < trimmed.Length && char.IsDigit(trimmed[index])) { run++; index++; }
                builder.Append("\\d{").Append(run).Append('}');
                hasDigitRun = true;
            }
            else if (char.IsLetter(c))
            {
                while (index < trimmed.Length && char.IsLetter(trimmed[index]))
                {
                    builder.Append(char.ToUpperInvariant(trimmed[index]));
                    index++;
                }
            }
            else
            {
                // Any separator generalises: "-" and "/" are formatting, not identity.
                builder.Append("[^A-Za-z0-9]");
                index++;
            }
        }
        builder.Append('$');

        // A pattern with no digit run is just the literal string; it identifies nothing.
        if (!hasDigitRun) return null;
        var pattern = builder.ToString();
        return pattern.Length <= 300 ? pattern : null;
    }

    public static bool Matches(string? pattern, string? rfqNumber)
    {
        if (string.IsNullOrWhiteSpace(pattern) || string.IsNullOrWhiteSpace(rfqNumber)) return false;
        try
        {
            return Regex.IsMatch(rfqNumber.Trim(), pattern, MatchOptions);
        }
        // A stored pattern predating Derive's current shape, or one edited by hand in the
        // database, can be malformed (ArgumentException) or can use a construct the
        // non-backtracking engine refuses, such as a lookaround (NotSupportedException).
        // Neither is evidence about a customer, so both mean "no match" — and, unlike the
        // timeout this replaced, they mean it for the same stored value every single time.
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
    }
}
