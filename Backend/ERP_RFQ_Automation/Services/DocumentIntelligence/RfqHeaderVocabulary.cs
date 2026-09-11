using System.Globalization;
using System.Text;

namespace ERP_RFQ_Automation.Services.DocumentIntelligence;

/// <summary>
/// The one list of spellings a buyer uses for each RFQ field.
///
/// <para><b>Why one list.</b> There were three: the spreadsheet column mapper knew six spellings
/// of the closing date, the Word header-block reader knew twelve, and the legacy CSV path knew
/// two. "Response By" was therefore read from a Word document and dropped from a workbook, and
/// "BCD" — how one of the largest buyers in the Kingdom heads that column — was read by none of
/// them. Every customer's export is the same every time, so a spelling that misses once misses on
/// every file that customer ever sends, and the rep types the date in by hand each time.</para>
///
/// <para><b>Two views of the same vocabulary.</b> <see cref="ColumnAliases"/> is what a column
/// header or a form label may say; <see cref="LabelAliases"/> is what a document-level
/// "Label: value" line may say. The inquiry-level fields (RFQ number, buyer, dates, delivery,
/// agreement) carry the same spellings in both, plus a few that only make sense as a document
/// label ("Reference:", "Company:", "Date:") and would be reckless as a column heading.</para>
///
/// <para><b>Learned spellings.</b> A tenant's reviewers teach the system the labels its customers
/// use (see <see cref="WithLearned"/>); those are consulted alongside the built-in list and can
/// never override it — a learned spelling that collides with a built-in one is ignored, so a
/// single mistaken confirmation cannot re-route a field the parser already reads correctly.</para>
///
/// <para>Matching is exact after <see cref="Normalize"/> (lower-case, letters and digits only).
/// Substring matching would let a "Total Price" column capture "Price" — that rule predates this
/// class and stands.</para>
/// </summary>
public sealed class RfqHeaderVocabulary
{
    /// <summary>Fields that describe the inquiry rather than one of its lines.</summary>
    public static readonly IReadOnlySet<string> InquiryLevelFields = new HashSet<string>(StringComparer.Ordinal)
    {
        RfqSpreadsheetFields.RfqNo,
        RfqSpreadsheetFields.BuyerName,
        RfqSpreadsheetFields.ReceivedDate,
        RfqSpreadsheetFields.BidClosingDate,
        RfqSpreadsheetFields.RequiredDeliveryDate,
        RfqSpreadsheetFields.DeliveryLocation,
        RfqSpreadsheetFields.AgreementReference,
    };

    private static readonly string[] ClosingDateSpellings =
    {
        // As found in the three lists this class replaced.
        "bidclosingdate", "closingdate", "bidduedate", "duedate", "deadline", "submissiondate",
        "submissiondeadline", "quotationdue", "quotedue", "responseby", "offerdue", "tenderclosingdate",
        // The spellings customers actually write. "BCD" is the abbreviation on Saudi bid lists;
        // "Bid Close" is what an Aramco sheet says; "Response Date" is SAP Ariba; "Last Date for
        // Submission" is the government-tender phrasing.
        "bcd", "bidclosedate", "bidclose", "closedate", "closingon", "dueon", "biddue", "bidduedatetime",
        "responsedate", "responseduedate", "responsedeadline", "respondby", "replyby", "submitby",
        "quotationduedate", "quoteduedate", "quotationdeadline", "quotedeadline", "quotationby",
        "biddeadline", "bidsubmissiondate", "bidsubmissiondeadline", "offersubmissiondate",
        "tenderduedate", "tenderdeadline", "tenderclosing", "tendercloses",
        "rfqduedate", "rfqdeadline", "rfqclosingdate", "rfqclosedate", "rfpduedate", "rfpclosingdate",
        "lastdateforsubmission", "lastdateofsubmission", "lastsubmissiondate", "submissionduedate",
        "closingdateandtime", "bidclosingdateandtime", "duedateandtime", "closingdatetime", "closingtime",
        "quotationsubmissiondate", "quotesubmissiondate", "offerduedate", "offerdeadline",
    };

    private static readonly string[] ManufacturerSpellings =
    {
        "manufacturername", "manufacturer", "make", "brand", "mfr", "mfg",
        "oem", "oemname", "maker", "mfrname", "mfgname", "makebrand", "brandmake", "manufacturerbrand",
        "brandname", "manufacturermake", "makemanufacturer", "producer", "vendorbrand",
    };

    private static readonly string[] PartNumberSpellings =
    {
        "manufacturerpartnumber", "mpn", "partnumber", "partno", "partcode", "modelno", "modelnumber",
        "materialcode", "itemcode", "materialno", "materialnumber", "stockcode", "stockno", "stocknumber",
        "skucode", "productcode", "catalogno", "catalognumber", "catalogueno", "cataloguenumber", "catno",
        "articleno", "articlenumber", "sku", "pn", "mfrpn", "mfgpn", "manufacturerpn", "mfrpartno", "mfgpartno",
        "mfrpartnumber", "mfgpartnumber", "oempartno", "oempartnumber", "oemno", "oemnumber", "oemcode",
        "manufacturerpartno", "manufacturercode", "manufacturerref", "partref", "partreference",
        "materialid", "itemid", "productid", "model", "typenumber", "typeno", "refnumber", "vendorpartno",
        // The buyer's OWN catalogue number travels on the same field, by design (see
        // LeadConversionIntelligence): an SAP export writes it far more often than a maker's number.
        "sapmaterial", "sapmaterialno", "sapmaterialnumber", "sapcode", "sapno", "customerpartno", "customerpartnumber",
        "buyerpartno", "buyerpartnumber", "customermaterialno", "customermaterialnumber", "customeritemcode",
        "buyermaterialno", "clientpartno", "clientpartnumber", "clientmaterialno",
    };

    /// <summary>Spellings a column header or a form label may use, per field.</summary>
    private static readonly Dictionary<string, string[]> BuiltinColumnAliases = new(StringComparer.Ordinal)
    {
        [RfqSpreadsheetFields.RfqNo] = new[]
        {
            "rfqno", "rfq", "rfqnumber", "rfqref", "rfqreference", "enquiryno", "enquirynumber", "inquiryno",
            "inquirynumber", "tenderno", "tendernumber", "bidno", "bidnumber", "rfpno", "rfpnumber",
            "eventno", "eventnumber", "eventid", "docno", "documentno", "documentnumber", "prno", "prnumber",
            "purchaserequisition", "purchaserequisitionno", "quotationrequestno", "requestno", "requestnumber",
            "customerrfqno", "customerrfqnumber", "customerrfqreference", "customerrfqref", "customerreference",
            "buyerrfqno", "buyerrfqnumber", "buyerreference", "rfqreferenceno", "rfqrefno", "enquiryref", "enquiryreference",
        },
        [RfqSpreadsheetFields.BuyerName] = new[]
        {
            "buyername", "buyer", "customer", "customername", "client", "clientname", "endcustomer", "enduser",
            "purchaser", "buyingcompany", "customercompany", "clientcompany",
        },
        [RfqSpreadsheetFields.ReceivedDate] = new[]
        {
            "receiveddate", "datereceived", "rfqdate", "enquirydate", "inquirydate", "issuedate", "dateissued",
            "issuedon", "rfpdate", "tenderdate", "documentdate", "datedon",
            // A sourcing portal's "Publish time" is when the buyer issued the event: the RFQ date.
            "publishtime", "publishdate", "publisheddate", "publishedon", "publishedat", "datepublished", "eventpublished",
            "releasedate", "dateofissue", "issuedatetime",
        },
        [RfqSpreadsheetFields.BidClosingDate] = ClosingDateSpellings,
        // "item" is deliberately absent — it is ambiguous and resolved by the column mapper.
        [RfqSpreadsheetFields.ProductName] = new[]
        {
            "productname", "product", "description", "itemdescription", "materialdescription", "materialname",
            "particulars", "shortdescription", "shorttext", "longtext", "itemname", "descriptionofgoods",
            "descriptionofitem", "materialdesc", "itemdesc", "goodsdescription",
        },
        [RfqSpreadsheetFields.Quantity] = new[]
        {
            "quantity", "qty", "qtyrequired", "quantityrequired", "reqqty", "requiredqty", "requiredquantity",
            "orderqty", "orderquantity", "demandqty", "reqquantity", "bidqty", "totalqty", "noofunits",
        },
        [RfqSpreadsheetFields.UnitOfMeasure] = new[]
        {
            "unitofmeasure", "uom", "unit", "um", "units", "measure", "unitofmeasurement", "unitmeasure",
            "unitofmeasures", "measurementunit", "unitofissue", "uoi", "orderunit",
        },
        [RfqSpreadsheetFields.UnitPrice] = new[] { "unitprice", "price", "rate", "unitrate", "priceperunit", "unitcost" },
        [RfqSpreadsheetFields.Currency] = new[] { "currency", "curr", "ccy", "currencycode" },
        [RfqSpreadsheetFields.ManufacturerName] = ManufacturerSpellings,
        // Bare "material" is deliberately absent: on a fabrication enquiry that column holds the
        // material of CONSTRUCTION ("SS316", "Carbon Steel"), not an identifier. Bare "itemno" is
        // absent too: it is the line ordinal (1, 2, 3) far more often than a code.
        [RfqSpreadsheetFields.ManufacturerPartNumber] = PartNumberSpellings,
        // "delivery" is deliberately NOT here. A column headed exactly "Delivery" holds a date far
        // more often than a number of days; it maps to the buyer's requirement below.
        [RfqSpreadsheetFields.LeadTimeDays] = new[]
        {
            "leadtimedays", "leadtime", "deliverytime", "deliveryperiod", "deliveryleadtime", "leadtimeweeks",
            "leadtimeindays", "leadtimeinweeks", "deliveryleadtimedays",
        },
        [RfqSpreadsheetFields.ItemText] = new[]
        {
            "notes", "note", "remarks", "remark", "comments", "comment", "itemtext", "specification", "spec",
            "specifications", "technicalspecification", "additionalinformation", "additionalinfo",
        },
        [RfqSpreadsheetFields.DeliveryLocation] = new[]
        {
            "deliverylocation", "deliveryto", "shipto", "destination", "deliveryaddress", "deliverypoint",
            "site", "plant", "deliverysite", "shiptolocation", "deliverat", "deliveryplace", "placeofdelivery",
        },
        [RfqSpreadsheetFields.RequiredDeliveryDate] = new[]
        {
            "requireddeliverydate", "requesteddeliverydate", "deliverydate", "delivery", "requiredby", "neededby",
            "wanteddate", "requesteddelivery", "deliveryrequired", "requireddelivery", "deliveryby",
            "expecteddeliverydate", "expecteddelivery", "requireddate", "needdate", "needbydate",
        },
        [RfqSpreadsheetFields.AgreementReference] = new[]
        {
            "agreementreference", "agreementno", "contractno", "contractreference", "framecontract", "agreement",
            "agreementnumber", "contractnumber", "frameworkagreement", "frameworkagreementno", "contract",
            "blanketorderno", "outlineagreement", "outlineagreementno",
        },
    };

    /// <summary>
    /// Spellings that identify the inquiry only when they appear as a document-level label
    /// ("Reference: RFQ-1", "Company: Omega Oil", "Date: 2026-05-26"). As column headings they
    /// would be reckless — a "Reference" column on a line grid is the buyer's part reference.
    /// </summary>
    private static readonly Dictionary<string, string[]> LabelOnlySpellings = new(StringComparer.Ordinal)
    {
        [RfqSpreadsheetFields.RfqNo] = new[] { "reference", "refno", "ref", "ourref", "yourref", "referenceno", "referencenumber" },
        [RfqSpreadsheetFields.BuyerName] = new[] { "company", "companyname", "organisation", "organization", "buyerorganisation" },
        [RfqSpreadsheetFields.ReceivedDate] = new[] { "date" },
        [RfqSpreadsheetFields.Currency] = new[] { "bidcurrency", "eventcurrency", "quotationcurrency", "quotecurrency", "currencyofquotation", "biddingcurrency" },
    };

    /// <summary>
    /// Line fields a document may state ONCE for every line ("Currency: US Dollar" in an event's
    /// overview). Read from the header block and applied to each line that does not state its own.
    /// </summary>
    public static readonly IReadOnlySet<string> DocumentLevelLineDefaults = new HashSet<string>(StringComparer.Ordinal)
    {
        RfqSpreadsheetFields.Currency,
    };

    private static readonly Dictionary<string, string[]> BuiltinLabelAliases = BuildLabelAliases();

    private static Dictionary<string, string[]> BuildLabelAliases()
    {
        var labels = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var field in InquiryLevelFields.Concat(DocumentLevelLineDefaults))
        {
            var spellings = BuiltinColumnAliases[field].ToList();
            if (LabelOnlySpellings.TryGetValue(field, out var extra))
                spellings.AddRange(extra);
            labels[field] = spellings.Distinct(StringComparer.Ordinal).ToArray();
        }
        return labels;
    }

    /// <summary>The vocabulary every tenant starts with.</summary>
    public static RfqHeaderVocabulary Builtin { get; } = new(
        Freeze(BuiltinColumnAliases), Freeze(BuiltinLabelAliases), Array.Empty<LearnedHeaderSpelling>());

    private readonly Dictionary<string, string> _columnIndex;
    private readonly Dictionary<string, string> _labelIndex;

    private RfqHeaderVocabulary(
        IReadOnlyDictionary<string, IReadOnlyList<string>> columnAliases,
        IReadOnlyDictionary<string, IReadOnlyList<string>> labelAliases,
        IReadOnlyList<LearnedHeaderSpelling> learned)
    {
        ColumnAliases = columnAliases;
        LabelAliases = labelAliases;
        Learned = learned;
        _columnIndex = Index(columnAliases);
        _labelIndex = Index(labelAliases);
    }

    /// <summary>Spellings a column header or a form label may use, per field.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ColumnAliases { get; }

    /// <summary>Spellings a document-level "Label: value" line may use, per inquiry-level field.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> LabelAliases { get; }

    /// <summary>The tenant-taught spellings this vocabulary carries beyond the built-in list.</summary>
    public IReadOnlyList<LearnedHeaderSpelling> Learned { get; }

    /// <summary>
    /// Lower-case, letters and digits only, accents folded — "Bid Closing Date", "BID_CLOSING-DATE"
    /// and "bidclosingdate" are one spelling, and so are "Fecha límite" and "fecha limite". Arabic
    /// and other scripts keep their letters, so a tenant can teach a label in any language.
    /// </summary>
    public static string Normalize(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return string.Empty;
        var sb = new StringBuilder(header.Length);
        foreach (var c in header.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>The field a column header or form label names, or null when it names none.</summary>
    public string? FieldForColumn(string? header)
    {
        var normalized = Normalize(header);
        return normalized.Length > 0 && _columnIndex.TryGetValue(normalized, out var field) ? field : null;
    }

    /// <summary>The field a document-level label names, or null when it names none.</summary>
    public string? FieldForLabel(string? label)
    {
        var normalized = Normalize(label);
        return normalized.Length > 0 && _labelIndex.TryGetValue(normalized, out var field) ? field : null;
    }

    /// <summary>True when the built-in list already knows this spelling, whichever field it names.</summary>
    public static bool IsBuiltin(string? spelling)
    {
        var normalized = Normalize(spelling);
        return normalized.Length > 0
               && (Builtin._columnIndex.ContainsKey(normalized) || Builtin._labelIndex.ContainsKey(normalized));
    }

    /// <summary>
    /// This vocabulary plus the spellings a tenant's reviewers have confirmed. A learned spelling
    /// the built-in list already knows is dropped, whatever field it claims; a learned spelling
    /// for an unknown field is dropped; the rest are appended to both views for their field.
    /// </summary>
    public RfqHeaderVocabulary WithLearned(IEnumerable<LearnedHeaderSpelling> learned)
    {
        var accepted = new List<LearnedHeaderSpelling>(Learned);
        var columns = ColumnAliases.ToDictionary(p => p.Key, p => p.Value.ToList(), StringComparer.Ordinal);
        var labels = LabelAliases.ToDictionary(p => p.Key, p => p.Value.ToList(), StringComparer.Ordinal);

        foreach (var spelling in learned)
        {
            var normalized = Normalize(spelling.Spelling);
            if (normalized.Length == 0 || IsBuiltin(normalized) || !columns.ContainsKey(spelling.Field))
                continue;
            if (_columnIndex.ContainsKey(normalized) || _labelIndex.ContainsKey(normalized))
                continue; // already learned, possibly for another field: first confirmation wins

            columns[spelling.Field].Add(normalized);
            if (labels.TryGetValue(spelling.Field, out var labelList))
                labelList.Add(normalized);
            accepted.Add(spelling with { Spelling = normalized });
        }

        return new RfqHeaderVocabulary(
            Freeze(columns.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.Ordinal)),
            Freeze(labels.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.Ordinal)),
            accepted);
    }

    private static Dictionary<string, string> Index(IReadOnlyDictionary<string, IReadOnlyList<string>> aliases)
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        // Ordinal field order so a duplicate spelling resolves the same way on every machine.
        foreach (var field in aliases.Keys.OrderBy(k => k, StringComparer.Ordinal))
            foreach (var spelling in aliases[field])
                index.TryAdd(spelling, field);
        return index;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Freeze(Dictionary<string, string[]> source)
        => source.ToDictionary(p => p.Key, p => (IReadOnlyList<string>)p.Value, StringComparer.Ordinal);
}

/// <summary>One spelling a tenant's reviewer confirmed names one field.</summary>
public sealed record LearnedHeaderSpelling(string Spelling, string Field);
