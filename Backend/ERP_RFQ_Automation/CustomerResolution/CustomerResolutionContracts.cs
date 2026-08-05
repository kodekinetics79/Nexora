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

    /// <summary>Sources trusted enough for the learned-alias auto-link tier (S3).</summary>
    public static readonly string[] TrustedForAutoLink =
        [LeadReviewLearned, "CustomerProfile", "CustomerImport"];
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

    public static bool IsFreeMailDomain(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return false;
        var first = domain.Trim().Split('.', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return first is not null && FreeMailFirstLabels.Contains(first);
    }

    /// <summary>True when the address itself must be discarded outright.</summary>
    public static bool IsSyntheticAddress(string? email)
        => IsSyntheticDomain(RoutingValueNormalizer.DomainFromEmail(email));
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

        foreach (var self in selfNameKeys)
        {
            if (string.Equals(self, candidateKey, StringComparison.Ordinal)) return true;
            var selfTight = CustomerNameNormalizer.TightKey(self);
            if (selfTight.Length < MinimumDistinctiveLength) continue;
            if (tight.Contains(selfTight, StringComparison.Ordinal)) return true;
            if (selfTight.Contains(tight, StringComparison.Ordinal)) return true;
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

    /// <summary>LooseKeys of the tenant's own trading names — nothing may ever match these.</summary>
    public IReadOnlyCollection<string> TenantSelfNameKeys { get; init; } = [];

    /// <summary>The tenant's own mail domains — nothing may ever match these.</summary>
    public IReadOnlyCollection<string> TenantSelfDomains { get; init; } = [];
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

    public decimal FuzzyMaximumConfidence { get; init; } = 0.85m;
    public decimal ExactNameSuggestionConfidence { get; init; } = 0.75m;
    public decimal PriorSenderSuggestionConfidence { get; init; } = 0.65m;
    public decimal ContactPersonSuggestionConfidence { get; init; } = 0.60m;
    public decimal RfqPatternSuggestionConfidence { get; init; } = 0.55m;

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
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(50);

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
            return Regex.IsMatch(rfqNumber.Trim(), pattern, RegexOptions.CultureInvariant, MatchTimeout);
        }
        catch (RegexMatchTimeoutException) { return false; }
        catch (ArgumentException) { return false; }
    }
}
