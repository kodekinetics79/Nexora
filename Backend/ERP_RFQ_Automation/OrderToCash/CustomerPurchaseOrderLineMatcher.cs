using ERP_RFQ_Automation.Deduplication;

namespace ERP_RFQ_Automation.OrderToCash;

/// <summary>The identity key that carried a proposal, recorded so a reviewer can see WHY.</summary>
public static class QuoteLineMatchKeys
{
    public const string ManufacturerPartNumber = "MANUFACTURER_PART_NUMBER";
    public const string CataloguePartNumber = "CATALOGUE_PART_NUMBER";
    public const string ManufacturerAndItemCode = "MANUFACTURER_AND_ITEM_CODE";
    public const string ItemCode = "ITEM_CODE";
    public const string Description = "DESCRIPTION";
}

public static class QuoteLineMatchConfidence
{
    public const string Exact = "EXACT";
    public const string High = "HIGH";
    public const string Medium = "MEDIUM";
    public const string Low = "LOW";
    public const string None = "NONE";
}

public static class QuoteLineMatchStatuses
{
    /// <summary>One identity key resolved to exactly one quote line. Still requires confirmation.</summary>
    public const string Proposed = "PROPOSED";

    /// <summary>The winning key resolved to more than one quote line. Nothing is proposed.</summary>
    public const string Ambiguous = "AMBIGUOUS";

    /// <summary>No identity key resolved. Nothing is proposed; candidates are listed for a human.</summary>
    public const string ReviewRequired = "REVIEW_REQUIRED";
}

/// <summary>The three FR-COM-02 keys exactly as the BUYER printed them on one purchase-order line.</summary>
public sealed record PurchaseOrderLineKeys(
    string ExternalLineReference,
    string? Description,
    string? CustomerItemCode,
    string? ManufacturerName,
    string? ManufacturerPartNumber,
    long? CustomerPurchaseOrderLineId = null);

/// <summary>The same three keys as OUR quotation carries them, plus the balances a reviewer needs.</summary>
public sealed record QuoteLineKeys(
    long QuoteItemId,
    string Description,
    string? ItemCode,
    string? ManufacturerName,
    string? ManufacturerPartNumber,
    IReadOnlyList<string?> CataloguePartNumbers,
    decimal QuotedQuantity,
    decimal RemainingQuantity,
    decimal UnitPrice);

public sealed record QuoteLineMatchCandidate(
    long QuoteItemId,
    string QuoteDescription,
    decimal QuotedQuantity,
    decimal RemainingQuantity,
    decimal QuotedUnitPrice,
    string? MatchedKey,
    string Confidence,
    string Reason);

public sealed record PurchaseOrderLineMatchProposal(
    long? CustomerPurchaseOrderLineId,
    string ExternalLineReference,
    string Status,
    long? ProposedQuoteItemId,
    string? MatchedKey,
    string Confidence,
    string Reason,
    IReadOnlyList<QuoteLineMatchCandidate> Candidates);

/// <summary>
/// FR-COM-02. "Match PO lines to the originating RFQ and quotation using item code, manufacturer
/// and part number."
///
/// <para>A pure, deterministic, explainable rule — no I/O, no model, no randomness, and no
/// dependency on the caller's own quoted figures. Given the buyer's keys and the quotation's keys
/// it returns, for every purchase-order line, either ONE proposed quote line together with the key
/// that carried it, or nothing at all together with the candidates a human should choose
/// between.</para>
///
/// <para>The BRD is explicit that completely touchless matching is out of scope and that exceptions
/// retain human review, so this type deliberately cannot commit anything: it produces proposals.
/// <see cref="CustomerAwardApplicationService.CreateAwardAsync"/> remains the only writer and still
/// takes its allocations as an explicit input.</para>
///
/// <para>Normalisation is <see cref="DuplicateRules.Normalize"/> — casefold and strip everything
/// that is not a-z/0-9 — reused rather than re-invented so "MFR-1234/A" on the buyer's PO and
/// "mfr1234a" in our catalogue are the same key here and in duplicate detection.</para>
/// </summary>
public static class CustomerPurchaseOrderLineMatcher
{
    /// <summary>Share of the smaller token set two descriptions must share before it is worth showing.</summary>
    public const double DescriptionOverlapThreshold = DuplicateRules.ItemOverlapThreshold;

    private sealed record Tier(string Key, string Confidence, bool Proposable,
        Func<PurchaseOrderLineKeys, QuoteLineKeys, string?> Match);

    /// <summary>
    /// Precedence, strongest first. Exact-on-normalised identity keys are exhausted before anything
    /// fuzzier is considered, and the FIRST tier that produces any hit at all decides the outcome —
    /// a weaker tier can never overrule or supplement a stronger one.
    /// </summary>
    private static readonly Tier[] Tiers =
    [
        new(QuoteLineMatchKeys.ManufacturerPartNumber, QuoteLineMatchConfidence.Exact, true, MatchManufacturerPartNumber),
        new(QuoteLineMatchKeys.CataloguePartNumber, QuoteLineMatchConfidence.High, true, MatchCataloguePartNumber),
        new(QuoteLineMatchKeys.ManufacturerAndItemCode, QuoteLineMatchConfidence.High, true, MatchManufacturerAndItemCode),
        new(QuoteLineMatchKeys.ItemCode, QuoteLineMatchConfidence.Medium, true, MatchItemCode),
        new(QuoteLineMatchKeys.Description, QuoteLineMatchConfidence.Low, false, MatchDescription),
    ];

    public static IReadOnlyList<PurchaseOrderLineMatchProposal> Propose(
        IEnumerable<PurchaseOrderLineKeys> purchaseOrderLines, IReadOnlyList<QuoteLineKeys> quoteLines)
    {
        ArgumentNullException.ThrowIfNull(purchaseOrderLines);
        ArgumentNullException.ThrowIfNull(quoteLines);
        var proposals = purchaseOrderLines.Select(line => ProposeOne(line, quoteLines)).ToList();
        return FlagContention(proposals);
    }

    private static PurchaseOrderLineMatchProposal ProposeOne(PurchaseOrderLineKeys line,
        IReadOnlyList<QuoteLineKeys> quoteLines)
    {
        foreach (var tier in Tiers)
        {
            var hits = quoteLines
                .Select(quoteLine => (quoteLine, reason: tier.Match(line, quoteLine)))
                .Where(hit => hit.reason is not null)
                .Select(hit => Candidate(hit.quoteLine, tier.Key, tier.Confidence, hit.reason!))
                .ToList();
            if (hits.Count == 0) continue;

            if (hits.Count == 1 && tier.Proposable)
            {
                var only = hits[0];
                return new PurchaseOrderLineMatchProposal(line.CustomerPurchaseOrderLineId,
                    line.ExternalLineReference, QuoteLineMatchStatuses.Proposed, only.QuoteItemId,
                    tier.Key, tier.Confidence, only.Reason, hits);
            }

            if (hits.Count > 1)
                return new PurchaseOrderLineMatchProposal(line.CustomerPurchaseOrderLineId,
                    line.ExternalLineReference, QuoteLineMatchStatuses.Ambiguous, null, tier.Key,
                    QuoteLineMatchConfidence.None,
                    $"{hits.Count} quote lines share this line's {KeyLabel(tier.Key)}. Choose the intended line.",
                    hits);

            // A single hit from a tier that is not strong enough to propose on its own.
            return new PurchaseOrderLineMatchProposal(line.CustomerPurchaseOrderLineId,
                line.ExternalLineReference, QuoteLineMatchStatuses.ReviewRequired, null, tier.Key,
                QuoteLineMatchConfidence.Low,
                "No item code, manufacturer or part number matched. One quote line reads similarly; confirm it yourself.",
                hits);
        }

        return new PurchaseOrderLineMatchProposal(line.CustomerPurchaseOrderLineId,
            line.ExternalLineReference, QuoteLineMatchStatuses.ReviewRequired, null, null,
            QuoteLineMatchConfidence.None,
            HasAnyKey(line)
                ? "This line's item code, manufacturer and part number match no quote line. Choose the intended line or treat it as unquoted."
                : "This line carries no item code, manufacturer or part number, so it cannot be matched. Choose the intended line.",
            quoteLines.Select(quoteLine => Candidate(quoteLine, null, QuoteLineMatchConfidence.None,
                "Listed for review only: no identity key on this PO line matched it.")).ToList());
    }

    /// <summary>
    /// One quote line proposed for several PO lines is legitimate — a buyer may split a quoted line
    /// across delivery dates — but the reviewer must be told, because confirming both consumes the
    /// quoted quantity twice.
    /// </summary>
    private static IReadOnlyList<PurchaseOrderLineMatchProposal> FlagContention(
        List<PurchaseOrderLineMatchProposal> proposals)
    {
        var contended = proposals
            .Where(x => x.ProposedQuoteItemId.HasValue)
            .GroupBy(x => x.ProposedQuoteItemId!.Value)
            .Where(x => x.Count() > 1)
            .Select(x => x.Key)
            .ToHashSet();
        if (contended.Count == 0) return proposals;
        return proposals.Select(proposal => proposal.ProposedQuoteItemId.HasValue
            && contended.Contains(proposal.ProposedQuoteItemId.Value)
                ? proposal with
                {
                    Reason = proposal.Reason
                        + " This quote line is proposed for more than one PO line; confirm the split quantities."
                }
                : proposal).ToList();
    }

    private static string? MatchManufacturerPartNumber(PurchaseOrderLineKeys line, QuoteLineKeys quoteLine)
    {
        var buyer = Key(line.ManufacturerPartNumber);
        if (buyer is null || Key(quoteLine.ManufacturerPartNumber) != buyer) return null;
        return $"Manufacturer part number {Display(line.ManufacturerPartNumber)} is the part number quoted on this line.";
    }

    private static string? MatchCataloguePartNumber(PurchaseOrderLineKeys line, QuoteLineKeys quoteLine)
    {
        var buyer = Key(line.ManufacturerPartNumber);
        if (buyer is null) return null;
        var hit = quoteLine.CataloguePartNumbers.FirstOrDefault(part => Key(part) == buyer);
        return hit is null
            ? null
            : $"Manufacturer part number {Display(line.ManufacturerPartNumber)} matches the part number {Display(hit)} on the quoted product.";
    }

    private static string? MatchManufacturerAndItemCode(PurchaseOrderLineKeys line, QuoteLineKeys quoteLine)
    {
        var code = Key(line.CustomerItemCode);
        var manufacturer = Key(line.ManufacturerName);
        if (code is null || manufacturer is null) return null;
        if (Key(quoteLine.ItemCode) != code || Key(quoteLine.ManufacturerName) != manufacturer) return null;
        return $"Manufacturer {Display(line.ManufacturerName)} and item code {Display(line.CustomerItemCode)} both match this quote line.";
    }

    private static string? MatchItemCode(PurchaseOrderLineKeys line, QuoteLineKeys quoteLine)
    {
        var code = Key(line.CustomerItemCode);
        if (code is null || Key(quoteLine.ItemCode) != code) return null;

        // Two manufacturers that are both known and different are a contradiction, not a match.
        var buyerManufacturer = Key(line.ManufacturerName);
        var quotedManufacturer = Key(quoteLine.ManufacturerName);
        if (buyerManufacturer is not null && quotedManufacturer is not null && buyerManufacturer != quotedManufacturer)
            return null;

        return $"Item code {Display(line.CustomerItemCode)} matches this quote line"
            + (buyerManufacturer is null && quotedManufacturer is null
                ? "; neither document names a manufacturer."
                : "; the manufacturer is named on only one of the two documents.");
    }

    private static string? MatchDescription(PurchaseOrderLineKeys line, QuoteLineKeys quoteLine)
    {
        var buyer = Tokens(line.Description);
        if (buyer.Count == 0) return null;
        var overlap = DuplicateRules.Overlap(buyer, Tokens(quoteLine.Description));
        return overlap >= DescriptionOverlapThreshold
            ? $"Descriptions share {overlap:P0} of their wording. Wording is not an identity key."
            : null;
    }

    private static QuoteLineMatchCandidate Candidate(QuoteLineKeys quoteLine, string? key, string confidence, string reason)
        => new(quoteLine.QuoteItemId, quoteLine.Description, quoteLine.QuotedQuantity,
            quoteLine.RemainingQuantity, quoteLine.UnitPrice, key, confidence, reason);

    private static bool HasAnyKey(PurchaseOrderLineKeys line)
        => Key(line.CustomerItemCode) is not null || Key(line.ManufacturerName) is not null
            || Key(line.ManufacturerPartNumber) is not null;

    private static string KeyLabel(string key) => key switch
    {
        QuoteLineMatchKeys.ManufacturerPartNumber or QuoteLineMatchKeys.CataloguePartNumber => "part number",
        QuoteLineMatchKeys.ManufacturerAndItemCode => "manufacturer and item code",
        QuoteLineMatchKeys.ItemCode => "item code",
        _ => "wording",
    };

    private static string? Key(string? raw) => DuplicateRules.Normalize(raw);

    private static string Display(string? raw) => raw?.Trim() ?? string.Empty;

    private static HashSet<string> Tokens(string? raw)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(raw)) return tokens;
        foreach (var part in raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            if (DuplicateRules.Normalize(part) is { } token) tokens.Add(token);
        return tokens;
    }
}
