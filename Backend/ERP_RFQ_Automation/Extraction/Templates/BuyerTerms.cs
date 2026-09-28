using System.Globalization;
using System.Text.RegularExpressions;
using ERP_RFQ_Automation.Services.DocumentIntelligence;

namespace ERP_RFQ_Automation.Extraction.Templates;

/// <summary>
/// The commercial terms a buyer states in an RFQ document, read back out: how to deliver, where
/// to, in which currency, for how long the price must hold, what must be accepted and attached.
///
/// <para><b>Why this exists.</b> A portal event print (SAP Ariba / ASMO) states its terms in the
/// rows above the line items — "4.5 1- Required Incoterms - … AMC/SAC. (submit alternative bid
/// for (VDD/VTC). 2- Required Currency - … USD or SAR. … 6- contract duration – Option 1: offer
/// for two-year agreement …" — and the line reader, correctly, reads none of it as a line. The
/// terms were therefore discarded, and a quote built from the lead ignored the delivery terms,
/// the currency rule, the validity floor and the agreement length the buyer set. A quote that
/// breaks them is rejected.</para>
///
/// <para><b>Why deterministic.</b> Every term is found by the buyer's own wording and returned
/// with the sentence it came from, so the rep reads the value and the proof side by side. A
/// term the document does not state is simply absent — nothing is inferred.</para>
/// </summary>
public static class BuyerTerms
{
    /// <summary>One term: a fixed key, a short label, the value in plain words, and the buyer's sentence.</summary>
    public sealed record Term(string Key, string Label, string Value, string Quote);

    /// <summary>The order terms are shown in: what decides the bid first.</summary>
    private static readonly string[] Order =
    {
        "closes", "package", "delivery_terms", "deliver_to", "agreement", "quote_currency", "exchange_rate",
        "validity", "payment", "vat", "alternatives", "quantities", "documents", "technical_offer",
        "part_numbers", "packing", "local_content", "warranty",
    };

    /// <summary>Rows read from the top of each table; a print states its terms above the items.</summary>
    public const int LeadingRows = 200;

    private const int MaxQuoteChars = 400;

    /// <summary>
    /// True when a retained document is one this reader can reach: a Word file, or the HTML page
    /// an Ariba event print is when saved as .doc (every SEC print). SEC leads used to show no
    /// "Buyer requires" strip at all although the print states "Local vendors MUST bid in SAR only",
    /// "Quotation Validity (minimum 90 days)" and "without VAT" — only .docx was ever read.
    /// </summary>
    public static bool CanRead(string? fileName, string? mediaType)
    {
        var name = fileName ?? string.Empty;
        var type = mediaType ?? string.Empty;
        return name.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".doc", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".htm", StringComparison.OrdinalIgnoreCase)
               || type.Contains("wordprocessingml", StringComparison.OrdinalIgnoreCase)
               || type.Contains("text/html", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The terms a retained document states, from its bytes: an HTML page (an Ariba print named
    /// .doc) is read through its tables exactly as the line reader reads it; a Word file through
    /// its leading table rows. A file neither reader can open (a Word 97 binary) states none.
    /// </summary>
    public static IReadOnlyList<Term> ReadDocument(byte[] bytes, string? fileName)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (HtmlDocumentTextExtractor.HasHtmlSignature(bytes))
        {
            var grids = HtmlTableGrids.Read(bytes).Grids
                .Select(grid => (IReadOnlyList<IReadOnlyList<string?>>)grid.Take(LeadingRows).ToList())
                .ToList();
            return Read(grids, fileName);
        }
        try
        {
            return Read(DocxTableParser.ReadLeadingGrids(bytes, LeadingRows), fileName);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Not a Word package (a Word 97 binary, a damaged file): no terms this reader can reach.
            return [];
        }
    }

    /// <param name="grids">The document's tables, leading rows are enough (terms sit above the items).</param>
    /// <param name="fileName">The document's file name, for a split package ("1 of 3").</param>
    public static IReadOnlyList<Term> Read(IReadOnlyList<IReadOnlyList<IReadOnlyList<string?>>> grids, string? fileName)
    {
        var found = new Dictionary<string, Term>(StringComparer.Ordinal);
        void Add(string key, string label, string value, string quote)
        {
            if (found.ContainsKey(key) || string.IsNullOrWhiteSpace(value)) return;
            found[key] = new Term(key, label, value.Trim(), Clip(quote));
        }

        var pairs = new List<(string Label, string Value)>();
        var sentences = new List<string>();
        var rates = new List<(string From, string To, string Rate)>();

        foreach (var grid in grids)
        {
            var title = FirstCell(grid.FirstOrDefault());
            var isRateTable = title is not null && Regex.IsMatch(title, @"^\s*exchange\s+rates?\s*$", RegexOptions.IgnoreCase);
            foreach (var row in grid)
            {
                var cells = row.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => Clean(c!)).ToList();
                if (cells.Count == 0) continue;
                if (isRateTable && cells.Count == 3 && decimal.TryParse(cells[2], NumberStyles.Number, CultureInfo.InvariantCulture, out _))
                    rates.Add((cells[0], cells[1], cells[2]));
                else if (cells.Count == 2 && cells[0].Length <= 80)
                    pairs.Add((cells[0], cells[1]));
                else if (cells.Count == 1)
                    sentences.Add(cells[0]);
            }
        }

        // ---- label | value rows (any buyer's terms table, and the event print's rule tables)
        string? eventCurrency = null, dueText = null, overtime = null, currencyChoice = null;
        foreach (var (label, value) in pairs)
        {
            var key = RfqHeaderVocabulary.Normalize(label);
            switch (key)
            {
                case "duedate": dueText ??= value; break;
                case "allowbiddingovertime": overtime ??= value; break;
                case "allowparticipantstoselectbiddingcurrency": currencyChoice ??= value; break;
                case "currency" or "eventcurrency" or "biddingcurrency": eventCurrency ??= value; break;
                case "incoterms" or "incoterm" or "deliveryterms" or "incotermsvalue":
                    Add("delivery_terms", "Delivery terms", value, $"{label}: {value}"); break;
                case "paymentterms" or "payment" or "termsofpayment":
                    Add("payment", "Payment", value, $"{label}: {value}"); break;
                case "bidvalidity" or "offervalidity" or "quotevalidity" or "validity" or "validityofoffer":
                    Add("validity", "Quote valid for", value, $"{label}: {value}"); break;
                case "warranty" or "warrantyperiod" or "guarantee":
                    Add("warranty", "Warranty", value, $"{label}: {value}"); break;
                case "deliveryto" or "deliverypoint" or "shipto" or "placeofdelivery":
                    Add("deliver_to", "Deliver to", value, $"{label}: {value}"); break;
                default:
                    // A clause printed as "label | text" — the SEC print's "5 Header Text | Important
                    // points …: 1. … 6. Quotation must be valid for 90 days …" — is still the buyer's
                    // wording, and its clauses are read like any other.
                    sentences.Add($"{label} {value}");
                    break;
            }
        }

        // A numbered list run together in one cell ("… without VAT.5.Any change …") is one clause
        // per row, so a quoted sentence ends where the buyer's clause does.
        for (var i = 0; i < sentences.Count; i++)
            sentences[i] = Regex.Replace(sentences[i], @"(?<=[\w):]\.|:)(?=\d{1,2}\.\s?[A-Z])", "\n");

        // ---- the buyer's clauses, in the buyer's words
        // One row per line, so a quoted sentence never runs into the next clause row.
        var text = string.Join("\n", sentences);

        // Closing: the exact time the portal shuts, which the lead's date field does not carry.
        if (dueText is not null)
        {
            var when = FormatDue(dueText);
            var noOvertime = overtime is not null && overtime.Trim().StartsWith("n", StringComparison.OrdinalIgnoreCase);
            Add("closes", "Closes", noOvertime ? $"{when}, no extension" : when,
                $"Due date: {dueText}" + (overtime is null ? "" : $"; Allow bidding overtime: {overtime}"));
        }

        if (fileName is not null && Regex.Match(fileName, @"\b(\d{1,2})\s+of\s+(\d{1,2})\b", RegexOptions.IgnoreCase) is { Success: true } part
            && int.Parse(part.Groups[1].Value, CultureInfo.InvariantCulture) <= int.Parse(part.Groups[2].Value, CultureInfo.InvariantCulture))
            Add("package", "Package", $"Part {part.Groups[1].Value} of {part.Groups[2].Value}", fileName);

        if (Find(text, @"preferred\s+Incoterms[^.]*?\bare\s+([A-Z]{3}(?:\s*/\s*[A-Z]{3})*)") is { } inco)
        {
            var alternative = Find(text, @"alternative\s+bid\s+for\s*\(?\s*([A-Z]{3}(?:\s*/\s*[A-Z]{3})*)");
            Add("delivery_terms", "Delivery terms",
                alternative is null ? inco.Value : $"{inco.Value}; also price {alternative.Value} as an alternative",
                Sentence(text, inco.Index));
        }

        if (Find(text, @"delivery\s+of\s+Goods\s+to\s+([^.]{3,160}?)\s*\.") is { } to)
            Add("deliver_to", "Deliver to", to.Value, Sentence(text, to.Index));
        // SEC: "For local vendor, the delivery of material must be deliver to SaudiEnergy warehouse or user location."
        if (Find(text, @"delivery\s+of\s+material\s+must\s+be\s+deliver(?:ed)?\s+to\s+([^.]{3,160}?)\s*\.") is { } material)
            Add("deliver_to", "Deliver to", material.Value, Sentence(text, material.Index));

        if (Find(text, @"Option\s*1\s*:\s*offer\s+for\s+(?:a\s+)?([\w-]+?)[\s-]+year\s+agreement") is { } years)
        {
            var alt = Find(text, @"alternative\s+offer\s+for\s+(?:a\s+)?([\w-]+?)[\s-]+year\s+agreement");
            var first = Years(years.Value);
            Add("agreement", "Agreement",
                alt is null ? $"{first} agreement" : $"{first} agreement; also offer {Years(alt.Value)} as an alternative",
                Sentence(text, years.Index));
        }
        else if (Find(text, @"\bPurchase\s+Agreement\s*\(PA\)") is { } pa)
            Add("agreement", "Agreement", "Purchase agreement (length not stated)", Sentence(text, pa.Index));

        // SEC: "Local vendors MUST bid in SAR only" — decides the currency for a Saudi seller,
        // whatever the event allows participants to choose.
        if (Find(text, @"must\s+bid\s+in\s+([A-Z]{3})\s+only") is { } only)
            Add("quote_currency", "Quote in", $"{only.Value.ToUpperInvariant()} only (local vendors)", Sentence(text, only.Index));
        if (Find(text, @"submitted\s+in\s+either\s+([A-Z]{3}\s+or\s+[A-Z]{3})") is { } either)
            Add("quote_currency", "Quote in", either.Value, Sentence(text, either.Index));
        else if (currencyChoice is not null && currencyChoice.StartsWith("y", StringComparison.OrdinalIgnoreCase))
            Add("quote_currency", "Quote in", eventCurrency is null ? "Your choice of currency" : $"{eventCurrency} or your own currency",
                $"Allow participants to select bidding currency: {currencyChoice}");
        else if (eventCurrency is not null)
            Add("quote_currency", "Quote in", eventCurrency, $"Currency: {eventCurrency}");

        if (rates.Count > 0)
        {
            var shown = rates.Select(r => $"1 {Code(r.From)} = {r.Rate} {Code(r.To)}").ToList();
            // The rate a Saudi seller converts with first.
            var usdSar = rates.FindIndex(r => Code(r.From) == "USD" && Code(r.To) == "SAR");
            if (usdSar > 0) { var s = shown[usdSar]; shown.RemoveAt(usdSar); shown.Insert(0, s); }
            Add("exchange_rate", "Exchange rate", string.Join(" · ", shown),
                "Exchange Rates: " + string.Join("; ", rates.Select(r => $"{r.From} → {r.To} {r.Rate}")));
        }

        if (Find(text, @"validity\s+of\s+at\s+least\s+(?:[a-z-]+\s+)?\(?(\d{1,3})\)?\s+days\s+from\s+the\s+bid\s+closing\s+date") is { } validity)
            Add("validity", "Quote valid for", $"At least {validity.Value} days after closing", Sentence(text, validity.Index));
        // SEC: "Quotation must be valid for 90 days from bid due date", "Quotation Validity (minimum 90 days)".
        if (Find(text, @"valid\s+for\s+(\d{1,3})\s+days\s+from\s+(?:the\s+)?bid\s+(?:due|closing)\s+date") is { } validFor)
            Add("validity", "Quote valid for", $"{validFor.Value} days after closing", Sentence(text, validFor.Index));
        if (Find(text, @"validity\s*\(\s*minimum\s+(\d{1,3})\s+days\s*\)") is { } minimum)
            Add("validity", "Quote valid for", $"At least {minimum.Value} days", Sentence(text, minimum.Index));

        if (Find(text, @"(?:do\s+not\s+include|exclusive\s+of|without)\s+(VAT)") is { } vat)
            Add("vat", "VAT",
                Regex.IsMatch(text, @"separately\s+identify[^.]*VAT", RegexOptions.IgnoreCase)
                    ? "Prices without VAT; show VAT separately"
                    : "Prices without VAT",
                Sentence(text, vat.Index));

        if (Find(text, @"may\s+submit\s+(alternative)\s+offers\s+for\s+each\s+item") is { } alternatives)
            Add("alternatives", "Alternative offers", "Allowed for each item", Sentence(text, alternatives.Index));

        if (Find(text, @"quantities\s+stated[^.]*?(estimates?)") is { } estimates)
            Add("quantities", "Quantities", "Estimates only, not a commitment", Sentence(text, estimates.Index));

        // One attachment per clause row: "4.4 ASMO - Terms and Conditions.pdf ASMO - Terms and Conditions.pdf".
        var documents = sentences
            .Select(row => Regex.Match(row, @"^\d+(?:\.\d+)*\s+(.{3,120}?\.pdf)\b", RegexOptions.IgnoreCase))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (documents.Count > 0)
            Add("documents", "Buyer's documents", string.Join("; ", documents.Select(d => Regex.Replace(d, @"\.pdf$", "", RegexOptions.IgnoreCase))),
                "Attached to the event: " + string.Join("; ", documents));

        if (Find(text, @"(DO\s+NOT\s+ATTACH\s+COMMERCIAL\s+DETAILS)") is { } technical)
            Add("technical_offer", "Technical offer", "Technical documents without prices, or the bid is disqualified", Sentence(text, technical.Index));

        if (Find(text, @"(Part\s+Number\s+Revision\s*/\s*Obsolescence)") is { } parts)
            Add("part_numbers", "Part numbers", "Confirm each part number, or state the substitute", Sentence(text, parts.Index));

        if (Find(text, @"complying\s+with\s+(Saudi\s+Aramco'?s\s+packing[^?.]*?requirements)") is { } packing)
            Add("packing", "Packing", "Saudi Aramco packing, labelling and marking, or state your own", Sentence(text, packing.Index));

        if (Find(text, @"\b(IKTVA)\b") is { } iktva)
            Add("local_content", "Local content", "IKTVA values requested", Sentence(text, iktva.Index));

        return Order.Where(found.ContainsKey).Select(k => found[k]).ToList();
    }

    private sealed record Hit(string Value, int Index);

    private static Hit? Find(string text, string pattern)
    {
        var m = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return m.Success ? new Hit(Regex.Replace(m.Groups[1].Value, @"\s*/\s*", "/").Trim(), m.Index) : null;
    }

    /// <summary>The buyer's sentence around a match: from the previous full stop (or clause number) to the next.</summary>
    private static string Sentence(string text, int index)
    {
        var start = index;
        while (start > 0 && text[start - 1] != '\n'
               && !(text[start - 1] == '.' && start < text.Length && char.IsWhiteSpace(text[start])) && index - start < 200) start--;
        var rowEnd = text.IndexOf('\n', index);
        if (rowEnd < 0) rowEnd = text.Length;
        var end = text.IndexOf(". ", index, StringComparison.Ordinal);
        end = end < 0 || end >= rowEnd ? rowEnd : end + 1;
        return text[start..end].Trim();
    }

    private static readonly Dictionary<string, int> NumberWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5,
    };

    private static string Years(string word)
    {
        var n = int.TryParse(word, out var digits) ? digits : NumberWords.GetValueOrDefault(word.Trim('-'), 0);
        return n switch { 0 => $"{word}-year", 1 => "1-year", _ => $"{n}-year" };
    }

    private static readonly Dictionary<string, string> CurrencyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["US Dollar"] = "USD", ["Saudi Riyal"] = "SAR", ["British Pound"] = "GBP", ["European Union Euro"] = "EUR",
        ["Euro"] = "EUR", ["UAE Dirham"] = "AED", ["Japanese Yen"] = "JPY", ["Chinese Yuan"] = "CNY",
    };

    private static string Code(string name) => CurrencyNames.TryGetValue(name.Trim(), out var code) ? code : name.Trim();

    /// <summary>"10/8/2026 3:00 PM" as "8 Oct 2026, 3:00 PM": a portal print is month-first.</summary>
    private static string FormatDue(string due)
    {
        var formats = new[] { "M/d/yyyy h:mm tt", "M/d/yyyy H:mm", "M/d/yyyy" };
        return DateTime.TryParseExact(due.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
            ? at.TimeOfDay == TimeSpan.Zero
                ? at.ToString("d MMM yyyy", CultureInfo.InvariantCulture)
                : at.ToString("d MMM yyyy, h:mm tt", CultureInfo.InvariantCulture)
            : due.Trim();
    }

    private static string? FirstCell(IReadOnlyList<string?>? row) => row?.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))?.Trim();

    private static string Clean(string cell) => Regex.Replace(cell.Replace(' ', ' '), @"\s+", " ").Trim();

    private static string Clip(string quote) => quote.Length <= MaxQuoteChars ? quote : quote[..MaxQuoteChars].TrimEnd() + " …";
}
