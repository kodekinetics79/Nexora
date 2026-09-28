using System.Globalization;
using ERP_RFQ_Automation.DTOs.QuoteDTOs;
using ERP_RFQ_Automation.Models;

namespace ERP_RFQ_Automation.Services.QuoteTerms;

/// <summary>
/// The send warnings that come from the buyer's terms and the quote's own lines. Pure, so each
/// rule is tested on its own. None of these blocks a send (owner rule 2026-09-26: inform, don't
/// obstruct); each names the problem in one sentence.
/// </summary>
public static class QuoteSendWarnings
{
    public const string ValidityBelowBuyerMinimum = "VALIDITY_BELOW_BUYER_MINIMUM";
    public const string BidClosed = "BID_CLOSED";
    public const string LinesNotFirm = "LINES_NOT_FIRM";
    public const string LeadTimeMissing = "LEAD_TIME_MISSING";
    public const string CurrencyNotAllowed = "CURRENCY_NOT_ALLOWED";
    public const string BuyerRevisionNewer = "BUYER_REVISION_NEWER";

    private static string Day(DateTime value) => value.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    public static List<QuoteSendWarningDTO> Evaluate(
        DateTime today, DateTime? validUntil, DateTime? bidClosing, string? currencyCode,
        IEnumerable<QuoteItem> lines, BuyerQuoteTerms terms)
    {
        var warnings = new List<QuoteSendWarningDTO>();
        var items = lines.ToList();

        // The buyer's validity floor. SEC: "valid for 90 days from bid due date"; Aramco: "at
        // least sixty (60) days from the bid closing date". A short quote is non-responsive.
        if (validUntil is { } until
            && BuyerQuoteTermRules.RequiredValidUntil(today, bidClosing, terms.Validity) is { } required
            && until.Date < required.Date)
        {
            var basis = terms.Validity!.Basis switch
            {
                BuyerValidityRule.FromClosing => " from closing",
                BuyerValidityRule.FromSubmission => " from submission",
                _ => string.Empty,
            };
            warnings.Add(new QuoteSendWarningDTO
            {
                Code = ValidityBelowBuyerMinimum,
                Message = $"Buyer asks for prices valid {terms.Validity.Days} days{basis} (until {Day(required)}). "
                    + $"This quote is valid until {Day(until)}.",
                SuggestedValidUntil = DateTime.SpecifyKind(required.Date, DateTimeKind.Unspecified),
            });
        }

        if (bidClosing is { } closing && closing.Date < today.Date)
            warnings.Add(new QuoteSendWarningDTO
            {
                Code = BidClosed,
                Message = $"The bid closed on {Day(closing)}.",
            });

        // CB-07: tender buyers (SEC, Marafiq) want fixed and firm prices.
        var toFollow = items.Count(i => i.PricingStatus == QuoteLinePricing.ToFollow);
        var estimates = items.Count(i => i.PricingStatus == QuoteLinePricing.Estimate);
        if (toFollow + estimates > 0)
        {
            var parts = new List<string>();
            if (toFollow > 0) parts.Add(toFollow == 1 ? "1 line is \"Price to follow\"" : $"{toFollow} lines are \"Price to follow\"");
            if (estimates > 0) parts.Add(estimates == 1 ? "1 line is an estimate" : $"{estimates} lines are estimates");
            warnings.Add(new QuoteSendWarningDTO
            {
                Code = LinesNotFirm,
                Message = $"{string.Join(" and ", parts)}. Tender buyers treat these as no bid.",
            });
        }

        // A priced line with no delivery time prints nothing where the buyer looks for it.
        // 0 is "ex stock" and is a real answer.
        var noLeadTime = items.Count(i => i.UnitPrice > 0m
            && !QuoteLinePricing.IsUnpricedByChoice(i.PricingStatus)
            && i.DeliveryLeadTime is null);
        if (noLeadTime > 0)
            warnings.Add(new QuoteSendWarningDTO
            {
                Code = LeadTimeMissing,
                Message = noLeadTime == 1 ? "1 priced line has no delivery time." : $"{noLeadTime} priced lines have no delivery time.",
            });

        if (terms.AllowedCurrencies.Count > 0 && !string.IsNullOrWhiteSpace(currencyCode)
            && !terms.AllowedCurrencies.Contains(currencyCode.Trim(), StringComparer.OrdinalIgnoreCase))
            warnings.Add(new QuoteSendWarningDTO
            {
                Code = CurrencyNotAllowed,
                Message = $"Buyer asks for prices in {string.Join(" or ", terms.AllowedCurrencies)}. This quote is in {currencyCode.Trim().ToUpperInvariant()}.",
            });

        return warnings;
    }

    /// <summary>
    /// "The buyer sent a newer version (rev 4): 2 lines changed." Null when the quote already
    /// reflects the buyer's latest revision.
    /// </summary>
    public static QuoteSendWarningDTO? NewerBuyerRevision(int reflectedRevision, int currentRevision,
        IReadOnlyList<QuoteRevisionLineChangeDTO> changes, bool canApply)
    {
        if (currentRevision <= reflectedRevision) return null;
        var lines = changes.Select(c => c.Line).Where(l => !string.IsNullOrEmpty(l)).Distinct(StringComparer.Ordinal).Count();
        var what = lines switch
        {
            0 => "",
            1 => ": 1 line changed",
            _ => $": {lines} lines changed",
        };
        var detail = changes.Where(c => c.Field == "quantity").Take(3)
            .Select(c => $"line {c.Line} {c.From} → {c.To}").ToList();
        return new QuoteSendWarningDTO
        {
            Code = BuyerRevisionNewer,
            Message = $"The buyer sent a newer version (rev {currentRevision}){what}."
                + (detail.Count > 0 ? $" Quantities: {string.Join(", ", detail)}." : string.Empty),
            Revision = new QuoteRevisionImpactDTO
            {
                ImpactType = "BUYER_REVISION_NEWER",
                FromRevision = reflectedRevision,
                ToRevision = currentRevision,
                Changes = changes.ToList(),
            },
            CanApply = canApply,
        };
    }
}
