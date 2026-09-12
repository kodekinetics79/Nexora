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
    /// Sources trusted enough for the learned-alias auto-link tier (S3). An administrator's own
    /// entry on the setup screen belongs here: a portal vendor code or an "also known as" typed
    /// in deliberately is at least as reliable as a reviewer's confirmation — and until it was
    /// listed, a rule entered there could route a lead but never link its customer.
    /// </summary>
    public static readonly string[] TrustedForAutoLink =
        [LeadReviewLearned, "CustomerProfile", "CustomerImport", MasterData];
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
    /// </summary>
    private static readonly HashSet<string> FreeMailFirstLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "gmail", "googlemail", "hotmail", "outlook", "yahoo", "ymail", "icloud", "me",
        "aol", "protonmail", "proton", "live", "msn", "qq", "163", "126", "gmx",
        "mail", "yandex", "zoho", "rediffmail"
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
    /// </summary>
    private static readonly HashSet<string> PortalRelayDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "ariba.com", "ansmtp.ariba.com", "eusmtp.ariba.com", "sap.com", "sapariba.com",
        "etimad.sa", "tenders.gov.sa", "jaggaer.com", "coupahost.com", "tejari.com",
        "bidnet.com", "bidnetdirect.com", "demandstar.com", "bonfirehub.com"
    };

    public static bool IsFreeMailDomain(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return false;
        var first = domain.Trim().Split('.', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
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
    /// The portal names as a person writes them. Public so a test can state the whole list,
    /// and so the reason any one portal sits on it can be argued about in review.
    /// </summary>
    public static readonly IReadOnlyList<string> Names =
    [
        "ARIBA", "SAP ARIBA", "SAP ARIBA NETWORK", "SAP BUSINESS NETWORK", "ARIBA NETWORK",
        "ETIMAD", "JAGGAER", "COUPA", "TENDERBOARD", "TEJARI", "ORACLE SUPPLIER NETWORK",
        "PROCUREPORT"
    ];

    /// <summary>
    /// Compared on <see cref="CustomerNameNormalizer.LooseKey"/> — the same key the portal
    /// name is matched under everywhere else — so "SAP Ariba", "sap ariba network" and
    /// "ARIBA" all land on the same entry and no amount of punctuation, casing or a stray
    /// legal-form token can smuggle one past.
    /// </summary>
    private static readonly HashSet<string> Keys =
        Names.Select(CustomerNameNormalizer.LooseKey)
             .Where(key => key.Length > 0)
             .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// True when a supplier number on this portal was issued by the network rather than by
    /// the buyer, so the "portal|our-vendor-code" pair identifies NOBODY and must neither be
    /// learned nor matched.
    /// </summary>
    public static bool IsShared(string? portalName)
    {
        var key = CustomerNameNormalizer.LooseKey(portalName);
        return key.Length > 0 && Keys.Contains(key);
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
