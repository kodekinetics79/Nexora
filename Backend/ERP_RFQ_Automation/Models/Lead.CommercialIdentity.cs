namespace ERP_RFQ_Automation.Models;

/// <summary>
/// The vocabulary of <c>Leads."CustomerMatchStatus"</c>.
///
/// INVARIANT (enforced by the DB CHECK constraint <c>CK_Leads_CustomerIdentityStatus</c>):
/// an unresolved-shaped status (<see cref="Unresolved"/> / <see cref="Suggested"/> /
/// <see cref="Ambiguous"/>) MUST carry a NULL CustomerID, and every other status MUST carry
/// a customer. A suggestion that quietly wrote a customer would be indistinguishable from a
/// confirmed link, and a wrong client on a lead is worse than an unresolved one.
/// </summary>
public static class LeadCustomerMatchStatuses
{
    /// <summary>No usable evidence, or evidence that matched nothing. CustomerID is null.</summary>
    public const string Unresolved = "UNRESOLVED";

    /// <summary>1..5 ranked machine candidates were persisted; nothing was linked.</summary>
    public const string Suggested = "SUGGESTED";

    /// <summary>Two or more equally strong hits at the same tier; nothing was linked.</summary>
    public const string Ambiguous = "AMBIGUOUS";

    /// <summary>Machine-linked customer AND contact.</summary>
    public const string AutoMatched = "AUTO_MATCHED";

    /// <summary>Machine-linked customer; the buyer contact could not be identified.</summary>
    public const string AutoMatchedContactUnresolved = "AUTO_MATCHED_CONTACT_UNRESOLVED";

    /// <summary>Human picked customer + contact in extraction review.</summary>
    public const string Confirmed = "CONFIRMED";

    /// <summary>Human picked the customer only.</summary>
    public const string CustomerConfirmedContactUnresolved = "CUSTOMER_CONFIRMED_CONTACT_UNRESOLVED";

    /// <summary>Legacy backfill (migration 20260724223932). Machine-grade; never rewritten.</summary>
    public const string VerifiedEmail = "VERIFIED_EMAIL";

    /// <summary>Statuses that must NOT carry a customer.</summary>
    public static readonly string[] WithoutCustomer = [Unresolved, Suggested, Ambiguous];

    /// <summary>A human decided this; the machine must never overwrite it.</summary>
    public static bool IsHumanDecided(string? status) => status is
        Confirmed or CustomerConfirmedContactUnresolved or "CUSTOMER_CONFIRMED" or "VERIFIED";

    public static bool CarriesCustomer(string? status) =>
        !string.IsNullOrWhiteSpace(status) &&
        !WithoutCustomer.Contains(status.Trim().ToUpperInvariant(), StringComparer.Ordinal);
}

public partial class Lead
{
    public long? CustomerId { get; private set; }
    public long? ContactId { get; private set; }
    public string CustomerMatchStatus { get; private set; } = LeadCustomerMatchStatuses.Unresolved;

    // ── Why the machine decided what it decided ──────────────────────────────
    // Written only by the three governed mutators below, so the reason can never
    // drift from the link it explains.

    /// <summary>One of <c>CustomerMatchReasonCodes</c>. Null on leads never resolved.</summary>
    public string? CustomerMatchReasonCode { get; private set; }

    /// <summary>Strength of the signal that produced the link/suggestion, 0..1.</summary>
    public decimal? CustomerMatchConfidence { get; private set; }

    /// <summary>Human-readable basis ("Matched on sender domain se.com.sa").</summary>
    public string? CustomerMatchExplanation { get; private set; }

    public DateTime? CustomerMatchedOn { get; private set; }

    // ── Raw client evidence extracted from the document ──────────────────────
    // Plain setters: this is transcription of what the document said, not a decision.
    // It is what turns "Unknown client" from a dead end into a five-second decision,
    // and it is the seed corpus the learning loop draws on.

    /// <summary>The BUYING organisation as printed. Never the vendor/Vendname block.</summary>
    public string? CustomerCompanyNameExtracted { get; set; }

    /// <summary>≤120-character verbatim snippet that names the buying organisation.</summary>
    public string? CustomerCompanyEvidence { get; set; }

    /// <summary>CR / VAT / commercial registration of the BUYER, verbatim.</summary>
    public string? CustomerCompanyRegistrationId { get; set; }

    /// <summary>Buyer e-mail printed on the document (e.g. 57322@se.com.sa).</summary>
    public string? CustomerBuyerEmailExtracted { get; set; }

    /// <summary>Buying portal / template name ("MATERIALS E-BIDDING SYSTEM", "Ariba").</summary>
    public string? CustomerPortalNameExtracted { get; set; }

    /// <summary>OUR name in the document's Vendor block — captured to be EXCLUDED, never matched.</summary>
    public string? SupplierNameOnDocument { get; set; }

    /// <summary>OUR vendor code AT the customer (e.g. SEC vendor code 2004414).</summary>
    public string? SupplierAccountRefOnDocument { get; set; }

    /// <summary>
    /// Human resolution (extraction review). Kept for the existing call sites; the status
    /// string is normalised and must satisfy the CustomerID⇔status invariant.
    /// </summary>
    public void ResolveCommercialIdentity(long customerId, long? contactId, string matchStatus)
    {
        if (customerId <= 0) throw new ArgumentOutOfRangeException(nameof(customerId));
        if (string.IsNullOrWhiteSpace(matchStatus)) throw new ArgumentException("Match status is required.", nameof(matchStatus));

        var normalized = matchStatus.Trim().ToUpperInvariant();
        if (!LeadCustomerMatchStatuses.CarriesCustomer(normalized))
            throw new ArgumentException(
                $"Match status '{normalized}' may not carry a customer.", nameof(matchStatus));

        CustomerId = customerId;
        ContactId = contactId;
        CustomerMatchStatus = normalized;
    }

    /// <summary>
    /// Machine resolution at ingestion. Records WHY, always — a rep must be able to see the
    /// signal and its strength behind any automatic link.
    /// </summary>
    public void AutoResolveCommercialIdentity(
        long customerId, long? contactId, string reasonCode, decimal confidence,
        string explanation, DateTime matchedOn)
    {
        if (customerId <= 0) throw new ArgumentOutOfRangeException(nameof(customerId));
        if (string.IsNullOrWhiteSpace(reasonCode))
            throw new ArgumentException("A match reason code is required.", nameof(reasonCode));

        CustomerId = customerId;
        ContactId = contactId;
        CustomerMatchStatus = contactId.HasValue
            ? LeadCustomerMatchStatuses.AutoMatched
            : LeadCustomerMatchStatuses.AutoMatchedContactUnresolved;
        CustomerMatchReasonCode = reasonCode.Trim().ToUpperInvariant();
        CustomerMatchConfidence = Math.Clamp(confidence, 0m, 1m);
        CustomerMatchExplanation = Truncate(explanation, 500);
        CustomerMatchedOn = matchedOn;
    }

    /// <summary>
    /// Candidates exist but none is strong enough to link. The customer stays NULL by
    /// construction — an unresolved lead is honest, a guessed one is not.
    /// </summary>
    public void SuggestCommercialIdentity(
        string reasonCode, decimal confidence, string explanation, bool ambiguous, DateTime evaluatedOn)
    {
        if (string.IsNullOrWhiteSpace(reasonCode))
            throw new ArgumentException("A match reason code is required.", nameof(reasonCode));

        CustomerId = null;
        ContactId = null;
        CustomerMatchStatus = ambiguous
            ? LeadCustomerMatchStatuses.Ambiguous
            : LeadCustomerMatchStatuses.Suggested;
        CustomerMatchReasonCode = reasonCode.Trim().ToUpperInvariant();
        CustomerMatchConfidence = Math.Clamp(confidence, 0m, 1m);
        CustomerMatchExplanation = Truncate(explanation, 500);
        CustomerMatchedOn = evaluatedOn;
    }

    /// <summary>No usable evidence, or evidence that matched nothing.</summary>
    public void ClearCommercialIdentity(string reasonCode, string explanation, DateTime evaluatedOn)
    {
        CustomerId = null;
        ContactId = null;
        CustomerMatchStatus = LeadCustomerMatchStatuses.Unresolved;
        CustomerMatchReasonCode = string.IsNullOrWhiteSpace(reasonCode)
            ? null : reasonCode.Trim().ToUpperInvariant();
        CustomerMatchConfidence = 0m;
        CustomerMatchExplanation = Truncate(explanation, 500);
        CustomerMatchedOn = evaluatedOn;
    }

    private static string? Truncate(string? value, int max)
        => string.IsNullOrWhiteSpace(value) ? null : (value.Length <= max ? value : value[..max]);
}
