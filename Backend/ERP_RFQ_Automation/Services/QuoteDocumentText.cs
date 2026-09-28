using System.Globalization;
using System.Text.RegularExpressions;
using ERP_RFQ_Automation.Models;

namespace ERP_RFQ_Automation.Services;

/// <summary>
/// Facts about a quote that live outside the quote row but belong on what the buyer receives:
/// who signs it, and which earlier quotation it replaces.
/// </summary>
public sealed record QuoteDocumentFacts(
    string? SignerName,
    string? SignerTitle,
    string? SignerEmail,
    string? ContactPhone,
    string? SupersedesQuoteNo,
    DateTime? SupersedesDate)
{
    public static readonly QuoteDocumentFacts None = new(null, null, null, null, null, null);
}

/// <summary>
/// Every sentence the buyer reads on the quote PDF and in the quote e-mail that is built from the
/// quote's own facts. Pure, so each rule is testable without rendering a PDF, and shared by both
/// documents so the PDF and the e-mail cannot say two different things.
///
/// <para><b>Rule.</b> The buyer's copy never contradicts itself. The PDF used to print the tenant's
/// stock terms verbatim — "Prices are valid for 30 days" beside "Valid Until: Oct 31, 2026", and
/// "taxes are not included" beside a VAT 15% line — so a tender evaluator logged each as a
/// commercial exception (pilot audit CB-01 / UX-02 / D-12). The terms are now generated from the
/// quote; the tenant's own text prints after them as "Additional terms".</para>
/// </summary>
public static class QuoteDocumentText
{
    /// <summary>What the PDF prints when the buyer's RFQ number is not known (never our own number).</summary>
    public const string UnknownReference = "—";

    /// <summary>
    /// The seven clauses every tenant was seeded with (TenantBaselineSeeder before 2026-09-29, and
    /// QuoteService's own fallback). Three of them contradict the quote they are printed on.
    /// Stored text equal to this is treated as "no additional terms".
    /// </summary>
    public const string LegacySeededTerms =
        "1. Prices are valid for 30 days from the date of the quote.\n" +
        "2. Payment terms: Net 30 days from invoice date.\n" +
        "3. Delivery dates are estimates and subject to confirmation.\n" +
        "4. All products remain the property of the seller until fully paid.\n" +
        "5. Any applicable taxes or duties are not included unless specified.\n" +
        "6. Warranty and liability are as per the manufacturer's standard terms.\n" +
        "7. This quote is confidential and intended solely for the recipient.";

    /// <summary>
    /// The internal marker a draft prepared from an RFQ is born with. It describes Nexora's
    /// review state, not the offer, so it never reaches the buyer.
    /// </summary>
    public const string DraftReviewPlaceholder =
        "Commercial Review Required: pricing, inventory, lead time, tax, freight and validity remain pending.";

    private const string DateFormat = "MMM dd, yyyy";

    public static string Date(DateTime value) => value.ToString(DateFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// The buyer's OWN number for the enquiry: the reference promoted onto the RFQ, else the number
    /// read from their document. Never <c>Rfq.Rfqno</c>, which is a Nexora serial when the buyer
    /// gave none — printing it as "Your RFQ Reference" told the buyer our number was theirs (CB-14).
    /// </summary>
    public static string? BuyerRfqReference(Quote quote)
    {
        var reference = quote.Rfq?.CustomerRfqReference;
        if (string.IsNullOrWhiteSpace(reference)) reference = quote.Rfq?.Lead?.Rfqno;
        return string.IsNullOrWhiteSpace(reference) ? null : reference.Trim();
    }

    /// <summary>The tenant's own terms, or null when there are none or they are the legacy seed.</summary>
    public static string? AdditionalTerms(string? tenantTerms)
    {
        if (string.IsNullOrWhiteSpace(tenantTerms)) return null;
        return Normalize(tenantTerms) == Normalize(LegacySeededTerms) ? null : tenantTerms.Trim();
    }

    /// <summary>The rep's notes for the buyer, or null when empty or the internal draft marker.</summary>
    public static string? BuyerNotes(string? headerRemarks)
    {
        if (string.IsNullOrWhiteSpace(headerRemarks)) return null;
        return Normalize(headerRemarks) == Normalize(DraftReviewPlaceholder) ? null : headerRemarks.Trim();
    }

    private static string Normalize(string text) =>
        Regex.Replace(text.Replace("\r\n", "\n").Replace('\r', '\n'), @"[ \t]+", " ")
            .Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0)
            .Aggregate(string.Empty, (all, line) => all + line + "\n");

    /// <summary>
    /// The generated "Commercial terms" block. Each sentence states a fact the quote carries, and
    /// a sentence whose fact is unknown is left out rather than guessed.
    /// </summary>
    public static IReadOnlyList<string> CommercialTerms(
        DateTime? validUntil,
        string currencyCode,
        IReadOnlyCollection<QuoteItem> lines,
        string? deliveryPlace,
        string? deliveryTerms = null,
        string? paymentTerms = null,
        string? supersedesQuoteNo = null,
        DateTime? supersedesDate = null)
    {
        var terms = new List<string>();
        if (validUntil is DateTime until)
            terms.Add($"Prices are valid until {Date(until)}.");

        var taxedRates = lines
            .Where(line => line.TaxRatePercentApplied is > 0m && !QuoteLinePricing.IsUnpricedByChoice(line.PricingStatus))
            .Select(line => line.TaxRatePercentApplied!.Value)
            .Distinct()
            .ToList();
        terms.Add(taxedRates.Count switch
        {
            0 => $"Prices are in {currencyCode} and exclude VAT.",
            1 => $"Prices are in {currencyCode} and exclude VAT. VAT {RateText(taxedRates[0])}% is shown separately.",
            _ => $"Prices are in {currencyCode} and exclude VAT. VAT is shown separately for each rate."
        });

        if (Sentence("Delivery terms", deliveryTerms) is { } delivery) terms.Add(delivery);
        if (Sentence("Delivery to", deliveryPlace) is { } place) terms.Add(place);
        if (Sentence("Payment terms", paymentTerms) is { } payment) terms.Add(payment);

        var priced = lines.Where(line => !QuoteLinePricing.IsUnpricedByChoice(line.PricingStatus)).ToList();
        if (priced.Any(line => line.DeliveryLeadTime is > 0))
            terms.Add("Delivery times are counted from the date we receive your purchase order.");
        if (priced.Any(line => line.DeliveryLeadTime == 0 || line.ExStockQuantity > 0))
            terms.Add("Items marked ex stock are offered subject to prior sale. Stock is held for you once we receive your purchase order.");

        if (Supersedes(supersedesQuoteNo, supersedesDate) is { } supersedes)
            terms.Add($"This quotation {supersedes}, which is withdrawn.");
        return terms;
    }

    private static string? Sentence(string label, string? value)
    {
        var text = value?.Trim().TrimEnd('.').Trim();
        return string.IsNullOrEmpty(text) ? null : $"{label}: {text}.";
    }

    /// <summary>"supersedes QT-0926-0004 dated Sep 17, 2026" (CB-12), or null on an original quote.</summary>
    public static string? Supersedes(string? quoteNo, DateTime? dated) =>
        string.IsNullOrWhiteSpace(quoteNo) ? null
        : dated is DateTime on ? $"supersedes {quoteNo} dated {Date(on)}"
        : $"supersedes {quoteNo}";

    public static string RateText(decimal rate) =>
        decimal.Round(rate, 2).ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>
    /// "Material: 905750742 · Make: SAFT · Part no.: LS14500-AX" — what the buyer calls the line,
    /// printed under our description so their evaluator can match it (UX-03 / CB-08). Null when the
    /// line carries none of the three. A material number that is already the description is not
    /// printed twice.
    /// </summary>
    public static string? BuyerIdentityLine(string? materialCode, string? maker, string? partNumber, string? description)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(materialCode)
            && !string.Equals(materialCode.Trim(), description?.Trim(), StringComparison.OrdinalIgnoreCase))
            parts.Add($"Material: {materialCode.Trim()}");
        if (!string.IsNullOrWhiteSpace(maker)) parts.Add($"Make: {maker.Trim()}");
        if (!string.IsNullOrWhiteSpace(partNumber)) parts.Add($"Part no.: {partNumber.Trim()}");
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>2.5 stays 2.5 and 1500 reads 1,500 (CB-11): the page must say the quantity being priced.</summary>
    public static string QuantityText(decimal quantity) =>
        quantity.ToString("#,0.###", CultureInfo.InvariantCulture);

    /// <summary>The unit price as printed: the currency scale, the same value the line total is built on.</summary>
    public static string UnitPriceText(decimal unitPrice) =>
        QuoteService.RoundUnitPrice(unitPrice).ToString("N2", CultureInfo.InvariantCulture);

    /// <summary>
    /// SHIP TO: the delivery point the buyer named for THIS enquiry, then the customer's shipping
    /// address, then billing (HT-10). The buyer's RFQ says where the goods go; the customer master
    /// says where the company sits.
    /// </summary>
    public static IReadOnlyList<string> ShipToLines(string? deliveryLocation, Customer? customer)
    {
        if (!string.IsNullOrWhiteSpace(deliveryLocation)) return [deliveryLocation.Trim()];
        var hasShipping = !string.IsNullOrWhiteSpace(customer?.ShippingAddressLine1)
            || !string.IsNullOrWhiteSpace(customer?.ShippingCity)
            || !string.IsNullOrWhiteSpace(customer?.ShippingCountry);
        return hasShipping
            ? QuoteService.AddressLines(customer?.ShippingAddressLine1, customer?.ShippingAddressLine2,
                customer?.ShippingCity, customer?.ShippingCountry)
            : QuoteService.AddressLines(customer?.BillingAddressLine1, customer?.BillingAddressLine2,
                customer?.BillingCity, customer?.BillingCountry);
    }

    /// <summary>The delivery point on the enquiry: the RFQ's, else the one read on the lead.</summary>
    public static string? DeliveryPlace(Quote quote)
    {
        var place = quote.Rfq?.DeliveryLocation;
        if (string.IsNullOrWhiteSpace(place)) place = quote.Rfq?.Lead?.DeliveryLocation;
        return string.IsNullOrWhiteSpace(place) ? null : place.Trim();
    }

    /// <summary>The three money figures a buyer compares, ex-VAT first (CB-17).</summary>
    public static (decimal ExcludingVat, decimal Vat, decimal IncludingVat) Totals(Quote quote)
    {
        static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
        var total = quote.TotalAmount ?? 0m;
        if (quote.QuoteItems.Count == 0) return (Round(total), 0m, Round(total));
        var vat = quote.QuoteItems.Sum(line => Round(line.TaxAmount ?? 0m));
        var excluding = quote.QuoteItems.Sum(line => Round(line.TaxableBase));
        return (excluding, vat, Round(total));
    }

    /// <summary>The label of the VAT row, with the rate when every taxed line shares one.</summary>
    public static string VatLabel(IEnumerable<QuoteItem> lines)
    {
        var rates = lines.Where(line => line.TaxRatePercentApplied is > 0m)
            .Select(line => line.TaxRatePercentApplied!.Value).Distinct().ToList();
        return rates.Count == 1 ? $"VAT {RateText(rates[0])}%" : "VAT";
    }

    /// <summary>
    /// The default covering e-mail. Subject carries the BUYER's RFQ number first (Marafiq 1.1.2:
    /// "The RFQ Number shall be clearly indicated in the subject email", otherwise disqualified —
    /// CB-05), falling back to the old subject when the buyer gave no number. The body states the
    /// totals ex-VAT first, the rep's notes, what the quote replaces, and a named contact.
    /// </summary>
    public static (string Subject, string PlainBody) Email(Quote quote, QuoteDocumentFacts facts)
    {
        var seller = quote.BusinessUnit?.BusinessUnitName;
        var reference = BuyerRfqReference(quote);
        var subject = reference is null
            ? $"Quote #{quote.QuoteNo} from {seller}"
            : $"RFQ {reference} – Quotation {quote.QuoteNo} – {seller}";

        var greetingName = quote.Customer?.Name;
        var greeting = string.IsNullOrWhiteSpace(greetingName) ? "Dear Customer" : $"Dear {greetingName}";

        var facts1 = new List<string> { $"Please find attached our quotation #{quote.QuoteNo}." };
        if (reference is not null) facts1.Add($"Your RFQ reference: {reference}");
        if (Supersedes(facts.SupersedesQuoteNo, facts.SupersedesDate) is { } supersedes)
            facts1.Add($"This quotation {supersedes}, which is withdrawn.");
        var currency = quote.Currency?.Code?.Trim();
        if (quote.TotalAmount is not null && !string.IsNullOrWhiteSpace(currency))
        {
            var (excluding, vat, including) = Totals(quote);
            facts1.Add($"Total excl. VAT: {currency} {excluding.ToString("N2", CultureInfo.InvariantCulture)}");
            if (vat > 0m)
            {
                facts1.Add($"{VatLabel(quote.QuoteItems)}: {currency} {vat.ToString("N2", CultureInfo.InvariantCulture)}");
                facts1.Add($"Total incl. VAT: {currency} {including.ToString("N2", CultureInfo.InvariantCulture)}");
            }
        }
        if (quote.ValidUntil is DateTime validUntil)
            facts1.Add($"Valid until: {validUntil.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)}");

        var paragraphs = new List<string> { $"{greeting},", string.Join("\n", facts1) };
        if (BuyerNotes(quote.HeaderRemarks) is { } notes) paragraphs.Add($"Notes:\n{notes}");
        paragraphs.Add("If anything here needs revisiting, reply to this message and we will pick it up.");

        var signature = new List<string> { "Kind regards," };
        if (!string.IsNullOrWhiteSpace(facts.SignerName))
            signature.Add(string.IsNullOrWhiteSpace(facts.SignerTitle)
                ? facts.SignerName!
                : $"{facts.SignerName}, {facts.SignerTitle}");
        if (!string.IsNullOrWhiteSpace(seller)) signature.Add(seller!);
        if (!string.IsNullOrWhiteSpace(facts.SignerEmail)) signature.Add(facts.SignerEmail!);
        if (!string.IsNullOrWhiteSpace(facts.ContactPhone)) signature.Add(facts.ContactPhone!);
        paragraphs.Add(string.Join("\n", signature));

        return (subject, string.Join("\n\n", paragraphs));
    }
}
