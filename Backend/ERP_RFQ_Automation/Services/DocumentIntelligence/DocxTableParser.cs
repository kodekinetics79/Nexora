using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace ERP_RFQ_Automation.Services.DocumentIntelligence;

/// <summary>
/// Reads a Word RFQ that states its line items in a table.
///
/// <para><b>Why this exists.</b> A .docx used to be flattened to prose and handed to the language
/// model, which is the most expensive, slowest and least certain way to read a document that
/// already has a grid in it — and in the current deployment the external model is refused
/// outright, so those documents simply dead-lettered. A table is structured data; it should be
/// read as structured data.</para>
///
/// <para>Column mapping is delegated to <see cref="NativeSpreadsheetParser.ParseGrid"/>, so a
/// Word table and a workbook share one set of header spellings and one header-location rule.
/// The header block above the table — "RFQ Number: …", "Customer: …" — is read separately,
/// because in a workbook those are columns and in a Word document they are paragraphs.</para>
/// </summary>
public sealed class DocxTableParser
{
    /// <summary>Paragraphs scanned above the first table for header-block labels.</summary>
    private const int HeaderBlockParagraphLimit = 40;

    /// <summary>Labels that identify the inquiry itself rather than one of its lines.</summary>
    private static readonly Dictionary<string, string[]> HeaderBlockAliases = new(StringComparer.Ordinal)
    {
        [RfqSpreadsheetFields.RfqNo] = new[] { "rfqnumber", "rfqno", "rfq", "enquiryno", "enquirynumber", "inquiryno", "tenderno", "bidno", "reference", "refno" },
        [RfqSpreadsheetFields.BuyerName] = new[] { "customer", "customername", "buyer", "buyername", "client", "clientname", "company" },
        [RfqSpreadsheetFields.ReceivedDate] = new[] { "rfqdate", "date", "datereceived", "receiveddate", "enquirydate" },
        [RfqSpreadsheetFields.BidClosingDate] = new[] { "closingdate", "bidclosingdate", "duedate", "deadline", "submissiondate", "quotationdue", "responseby" },
        // "Requested Delivery" now has a correct home. It is what the BUYER is asking for, so it
        // maps to RequiredDeliveryDate and never to a supplier lead time — that conflation put a
        // lead time of zero, meaning "deliver immediately", on every line of every document.
        // It is frequently prose ("9 weeks") rather than a date; an optional date that cannot be
        // parsed now yields NeedsReview and a null value, so an unreadable one costs nothing.
        [RfqSpreadsheetFields.RequiredDeliveryDate] = new[] { "requesteddelivery", "deliveryrequired", "requireddelivery", "deliveryby", "requiredby", "neededby" },
        [RfqSpreadsheetFields.DeliveryLocation] = new[] { "deliverylocation", "deliveryto", "shipto", "destination", "deliveryaddress", "site" },
        [RfqSpreadsheetFields.AgreementReference] = new[] { "agreementreference", "agreementno", "contractno", "contractreference", "framecontract" },
    };

    private readonly NativeSpreadsheetParser _grid;

    public DocxTableParser(NativeSpreadsheetParser grid) => _grid = grid;

    /// <summary>
    /// Returns one row per table line, or an empty list when the document states no table this
    /// parser can map — in which case the caller falls back to the unstructured text path
    /// exactly as before.
    /// </summary>
    public IReadOnlyList<RfqSpreadsheetRow> Parse(byte[] bytes, string sourceDocumentName)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var document = WordprocessingDocument.Open(stream, isEditable: false);

        var body = document.MainDocumentPart?.Document?.Body;
        if (body is null)
            return Array.Empty<RfqSpreadsheetRow>();

        var headerBlock = ReadHeaderBlock(body);

        var results = new List<RfqSpreadsheetRow>();
        var tableOrdinal = 0;
        foreach (var table in body.Descendants<Table>())
        {
            tableOrdinal++;
            var grid = table.Elements<TableRow>()
                .Select(row => (IReadOnlyList<string?>)row.Elements<TableCell>()
                    .Select(cell => CellText(cell))
                    .ToList())
                .ToList();

            var rows = _grid.ParseGrid(grid, sourceDocumentName, $"Table {tableOrdinal}");
            foreach (var row in rows)
            {
                ApplyHeaderBlock(row, headerBlock);
                results.Add(row);
            }
        }

        return results;
    }

    /// <summary>
    /// Header-block values fill only what the table itself did not state. A column always wins
    /// over a document-level label: if a line names its own closing date, that is the line's
    /// date, not the document's.
    /// </summary>
    private static void ApplyHeaderBlock(RfqSpreadsheetRow row, IReadOnlyDictionary<string, string> block)
    {
        row.RfqNo ??= Value(block, RfqSpreadsheetFields.RfqNo);
        row.BuyerName ??= Value(block, RfqSpreadsheetFields.BuyerName);
        row.ReceivedDate ??= Value(block, RfqSpreadsheetFields.ReceivedDate);
        row.BidClosingDate ??= Value(block, RfqSpreadsheetFields.BidClosingDate);
        row.RequiredDeliveryDate ??= Value(block, RfqSpreadsheetFields.RequiredDeliveryDate);
        row.DeliveryLocation ??= Value(block, RfqSpreadsheetFields.DeliveryLocation);
        row.AgreementReference ??= Value(block, RfqSpreadsheetFields.AgreementReference);
    }

    private static string? Value(IReadOnlyDictionary<string, string> block, string field)
        => block.TryGetValue(field, out var value) ? value : null;

    /// <summary>
    /// Reads "Label: value" pairs from the paragraphs above the first table. Word frequently
    /// splits one visual line across several runs and several paragraphs, so each paragraph is
    /// scanned for every known label rather than assuming one pair per line.
    /// </summary>
    private static Dictionary<string, string> ReadHeaderBlock(Body body)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        var scanned = 0;

        foreach (var paragraph in body.Descendants<Paragraph>())
        {
            if (paragraph.Ancestors<Table>().Any())
                continue;
            if (++scanned > HeaderBlockParagraphLimit)
                break;

            var text = paragraph.InnerText;
            if (string.IsNullOrWhiteSpace(text) || !text.Contains(':', StringComparison.Ordinal))
                continue;

            foreach (var (field, value) in ExtractPairs(text))
                found.TryAdd(field, value);
        }

        return found;
    }

    /// <summary>
    /// Pulls every recognised "Label: value" pair out of one line. Values run to the next known
    /// label rather than to the end of the line, because these documents routinely concatenate
    /// several pairs into a single paragraph with no separator
    /// ("RFQ Number: RFQ-260011Customer: Omega OilRFQ Date: 2026-05-26").
    /// </summary>
    private static IEnumerable<(string Field, string Value)> ExtractPairs(string line)
    {
        var marks = new List<(int Index, int LabelLength, string Field)>();

        foreach (var (field, aliases) in HeaderBlockAliases)
        {
            foreach (var alias in aliases)
            {
                var at = IndexOfLabel(line, alias);
                if (at.Start >= 0)
                {
                    marks.Add((at.Start, at.Length, field));
                    break;
                }
            }
        }

        if (marks.Count == 0)
            yield break;

        marks.Sort((a, b) => a.Index.CompareTo(b.Index));
        for (var i = 0; i < marks.Count; i++)
        {
            var start = marks[i].Index + marks[i].LabelLength;
            var end = i + 1 < marks.Count ? marks[i + 1].Index : line.Length;
            if (end <= start)
                continue;

            var value = line[start..end].Trim().Trim('-', '–', ' ').Trim();
            if (value.Length > 0)
                yield return (marks[i].Field, value);
        }
    }

    /// <summary>
    /// Locates a label followed by a colon, ignoring case, spacing and punctuation inside the
    /// label itself, so "RFQ Number:", "RFQ No.:" and "rfq_number :" all match.
    /// </summary>
    private static (int Start, int Length) IndexOfLabel(string line, string normalizedAlias)
    {
        for (var i = 0; i < line.Length; i++)
        {
            var compact = 0;
            var j = i;
            while (j < line.Length && compact < normalizedAlias.Length)
            {
                var c = line[j];
                if (char.IsLetterOrDigit(c))
                {
                    if (char.ToLowerInvariant(c) != normalizedAlias[compact])
                        break;
                    compact++;
                }
                else if (c != ' ' && c != '_' && c != '.' && c != '-' && c != ' ')
                {
                    break;
                }
                j++;
            }

            if (compact != normalizedAlias.Length)
                continue;

            // The label must be followed by a colon, allowing whitespace before it.
            var k = j;
            while (k < line.Length && (line[k] == ' ' || line[k] == ' ')) k++;
            // Deliberately NOT rejecting a label that begins mid-token. These documents run
            // several labelled values together with no separator at all —
            // "RFQ Number: RFQ-260011Customer: Omega OilRFQ Date: 2026-05-26" — so "Customer"
            // follows a digit and "RFQ Date" follows a letter. A preceding-character guard
            // rejects both of those real labels to avoid a hypothetical "Sub-Customer:", and
            // costs far more than it saves.
            if (k < line.Length && line[k] == ':')
                return (i, k - i + 1);
        }

        return (-1, 0);
    }

    private static string? CellText(TableCell cell)
    {
        var text = string.Join(" ", cell.Descendants<Paragraph>().Select(p => p.InnerText))
            .Replace(' ', ' ')
            .Trim();
        return text.Length == 0 ? null : text;
    }
}
