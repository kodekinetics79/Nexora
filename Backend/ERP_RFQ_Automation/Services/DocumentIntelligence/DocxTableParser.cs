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
        [RfqSpreadsheetFields.BidClosingDate] = new[] { "bidclosingdate", "closingdate", "bidduedate", "duedate", "deadline", "submissiondate", "submissiondeadline", "quotationdue", "quotedue", "responseby", "offerdue", "tenderclosingdate" },
        // "Requested Delivery" now has a correct home. It is what the BUYER is asking for, so it
        // maps to RequiredDeliveryDate and never to a supplier lead time — that conflation put a
        // lead time of zero, meaning "deliver immediately", on every line of every document.
        // It is frequently prose ("9 weeks") rather than a date; an optional date that cannot be
        // parsed now yields NeedsReview and a null value, so an unreadable one costs nothing.
        //
        // The "…deliverydate" spellings are the SAME ones the column mapper already recognises
        // (NativeSpreadsheetParser.FieldAliases). They were missing here, so a paragraph reading
        // "Required Delivery Date: 2026-10-01" matched no delivery label at all and the bare
        // "date" alias took the value onto ReceivedDate instead.
        [RfqSpreadsheetFields.RequiredDeliveryDate] = new[] { "requireddeliverydate", "requesteddeliverydate", "deliverydate", "requesteddelivery", "deliveryrequired", "requireddelivery", "deliveryby", "requiredby", "neededby" },
        [RfqSpreadsheetFields.DeliveryLocation] = new[] { "deliverylocation", "deliveryto", "shipto", "destination", "deliveryaddress", "site" },
        [RfqSpreadsheetFields.AgreementReference] = new[] { "agreementreference", "agreementno", "contractno", "contractreference", "framecontract" },
    };

    /// <summary>
    /// Aliases short and generic enough to be the TAIL of a longer label, which may therefore only
    /// match as the first word of their label.
    ///
    /// <para><b>Why.</b> "date" is a suffix of "Bid Closing Date", "Due Date", "Submission Date"
    /// and "Required Delivery Date". Matching it inside those swallowed the outer label: the
    /// closing date of a tender was written into the received-date field, the closing date came
    /// out missing, and the reviewer was told to supply a date the document stated plainly. A bid
    /// closed and nobody knew there was a bid. The longest-match scan below already prevents that
    /// for every label spelling we know; this guard also covers the ones we do not — "Award Date",
    /// "Validity Date", "Effective Date" — where reading nothing is a visible gap and reading the
    /// wrong thing is a silent error.</para>
    /// </summary>
    private static readonly HashSet<string> FirstWordOnlyAliases = new(StringComparer.Ordinal) { "date" };

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

        // TOP-LEVEL tables only. Descendants<Table>() also returns every table NESTED inside a
        // cell of another table, so a layout table wrapping the line grid was read twice and every
        // line was counted twice — while Lead.NoOfLineItems reported the inflated number as if it
        // were a conservation guarantee.
        foreach (var table in body.Elements<Table>())
        {
            tableOrdinal++;
            var grid = BuildGrid(table);

            var rows = _grid.ParseGrid(grid, sourceDocumentName, $"Table {tableOrdinal}");

            // A commercial-terms table ("Payment Terms | 30 days", "Incoterms | DDP Dammam") maps
            // its left column to `description` and nothing else, and the header locator falls back
            // to row 1 when it recognises too little. Every row of it then became a phantom line
            // item named "Payment Terms" with no quantity. One mapped column is not a line-item
            // table; it is a table that happens to contain a word we recognise.
            if (rows.Count > 0 && rows[0].FieldColumnNumbers.Count < MinimumMappedFieldsForLineItems)
                continue;

            foreach (var row in rows)
            {
                ApplyHeaderBlock(row, headerBlock);
                results.Add(row);
            }
        }

        return results;
    }

    /// <summary>
    /// Mapped columns a table must recognise before its rows are believed to be line items.
    /// Matches <c>NativeSpreadsheetParser</c>'s own header-confidence threshold.
    /// </summary>
    private const int MinimumMappedFieldsForLineItems = 2;

    /// <summary>
    /// Flattens a Word table into the positional grid <see cref="NativeSpreadsheetParser.ParseGrid"/>
    /// indexes by column number.
    ///
    /// <para><b>Why this is not just <c>row.Elements&lt;TableCell&gt;()</c>.</b> Word states a
    /// horizontally merged cell as ONE element carrying <c>w:gridSpan</c>, and a vertically merged
    /// cell as a full cell followed by EMPTY continuation cells carrying <c>w:vMerge</c>. Reading
    /// the elements positionally therefore shifted every column to the right of a merge by one or
    /// more places — a quantity landed in the unit-of-measure column, failed to parse, and (before
    /// the guard in ChunkedExtractionService) persisted as the number 0 — and a value spanning
    /// three rows populated the first and was silently null on the other two.</para>
    /// </summary>
    private static List<IReadOnlyList<string?>> BuildGrid(Table table)
    {
        var grid = new List<IReadOnlyList<string?>>();

        // The effective value of each grid column on the previous row, so a vMerge continuation
        // can carry its originating cell's value down instead of reading as blank.
        var carried = new List<string?>();

        foreach (var tableRow in table.Elements<TableRow>())
        {
            var cells = new List<string?>();

            foreach (var cell in tableRow.Elements<TableCell>())
            {
                var column = cells.Count;
                var text = IsVerticalMergeContinuation(cell)
                    ? (column < carried.Count ? carried[column] : null)
                    : CellText(cell);

                var span = GridSpanOf(cell);
                for (var offset = 0; offset < span; offset++)
                {
                    // The text belongs to the first grid column the merged cell occupies; the
                    // remainder are padding so every column after it keeps its own index.
                    cells.Add(offset == 0 ? text : null);
                }
            }

            grid.Add(cells);

            carried.Clear();
            carried.AddRange(cells);
        }

        return grid;
    }

    /// <summary>How many grid columns one cell element occupies. Absent or unreadable means one.</summary>
    private static int GridSpanOf(TableCell cell)
    {
        var span = cell.TableCellProperties?.GridSpan?.Val;
        if (span is null || !span.HasValue) return 1;
        return span.Value < 1 ? 1 : span.Value;
    }

    /// <summary>
    /// True for the empty continuation cells of a vertical merge. Word writes
    /// <c>&lt;w:vMerge/&gt;</c> with no value, or <c>w:val="continue"</c>; the cell that OWNS the
    /// value writes <c>w:val="restart"</c>.
    /// </summary>
    private static bool IsVerticalMergeContinuation(TableCell cell)
    {
        var merge = cell.TableCellProperties?.VerticalMerge;
        if (merge is null) return false;

        var value = merge.Val;
        if (value is null || !value.HasValue) return true;
        return value.Value == MergedCellValues.Continue;
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
        var marks = FindMarks(line);

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
    /// Scans the line left to right and records every label it recognises, taking the LONGEST
    /// label that starts at each position and then resuming after that label's colon.
    ///
    /// <para><b>What this replaces, and why it mattered.</b> The previous scan took the FIRST
    /// alias each field matched ANYWHERE in the line, sorted the results by position, and ended
    /// each value at the next mark's start — dropping any pair whose value ended before it began.
    /// On "Bid Closing Date: 2026-06-15" the bare "date" alias matched INSIDE the closing label,
    /// at a later position, so the inner mark swallowed the outer one: the closing date was
    /// written into ReceivedDate, BidClosingDate came out missing, and the reviewer was told the
    /// bid closing date needed review on a document that stated it plainly. A bid closes and
    /// nobody knew there was a bid.</para>
    ///
    /// <para>Longest-match-at-position makes an inner label unreachable, and resuming after the
    /// colon makes the marks NON-OVERLAPPING, so a value can no longer end before it starts and
    /// no pair is dropped for that reason.</para>
    ///
    /// <para>Marks come back in position order, which is the order they are found.</para>
    /// </summary>
    private static List<(int Index, int LabelLength, string Field)> FindMarks(string line)
    {
        var marks = new List<(int Index, int LabelLength, string Field)>();
        var position = 0;

        while (position < line.Length)
        {
            var match = LongestLabelAt(line, position);
            if (match is null)
            {
                position++;
                continue;
            }

            marks.Add((position, match.Value.Length, match.Value.Field));
            position += match.Value.Length;
        }

        return marks;
    }

    /// <summary>
    /// The longest recognised label starting exactly at <paramref name="index"/>, or null. A tie
    /// on length is broken on the field's name, so the reading of a document never depends on
    /// dictionary enumeration order.
    /// </summary>
    private static (int Length, string Field)? LongestLabelAt(string line, int index)
    {
        (int Length, string Field)? best = null;

        foreach (var (field, aliases) in HeaderBlockAliases)
        {
            foreach (var alias in aliases)
            {
                if (FirstWordOnlyAliases.Contains(alias) && !StartsItsOwnLabel(line, index))
                    continue;

                var length = MatchLabelAt(line, index, alias);
                if (length < 0)
                    continue;

                if (best is null
                    || length > best.Value.Length
                    || (length == best.Value.Length && string.CompareOrdinal(field, best.Value.Field) < 0))
                {
                    best = (length, field);
                }
            }
        }

        return best;
    }

    /// <summary>
    /// True when the token at <paramref name="index"/> is the FIRST word of its label — nothing
    /// but the line start, punctuation or a digit run precedes it. See
    /// <see cref="FirstWordOnlyAliases"/>.
    /// </summary>
    private static bool StartsItsOwnLabel(string line, int index)
    {
        var back = index - 1;
        while (back >= 0 && (line[back] == ' ' || line[back] == ' '))
            back--;

        return back < 0 || !char.IsLetter(line[back]);
    }

    /// <summary>
    /// Length of the label starting exactly at <paramref name="index"/>, colon included, or -1.
    /// Case, spacing and punctuation inside the label are ignored, so "RFQ Number:", "RFQ No.:"
    /// and "rfq_number :" all match.
    /// </summary>
    private static int MatchLabelAt(string line, int index, string normalizedAlias)
    {
        var compact = 0;
        var j = index;
        while (j < line.Length && compact < normalizedAlias.Length)
        {
            var c = line[j];
            if (char.IsLetterOrDigit(c))
            {
                if (char.ToLowerInvariant(c) != normalizedAlias[compact])
                    return -1;
                compact++;
            }
            else if (c != ' ' && c != '_' && c != '.' && c != '-' && c != ' ')
            {
                return -1;
            }
            j++;
        }

        if (compact != normalizedAlias.Length)
            return -1;

        // The label must be followed by a colon, allowing whitespace before it.
        var k = j;
        while (k < line.Length && (line[k] == ' ' || line[k] == ' ')) k++;
        // Deliberately NOT rejecting a label that begins mid-token. These documents run
        // several labelled values together with no separator at all —
        // "RFQ Number: RFQ-260011Customer: Omega OilRFQ Date: 2026-05-26" — so "Customer"
        // follows a digit and "RFQ Date" follows a letter. A preceding-character guard
        // rejects both of those real labels to avoid a hypothetical "Sub-Customer:", and
        // costs far more than it saves. The one alias generic enough to be another label's
        // TAIL is guarded individually, by FirstWordOnlyAliases.
        return k < line.Length && line[k] == ':' ? k - index + 1 : -1;
    }

    /// <summary>
    /// The cell's OWN text. Paragraphs belonging to a table NESTED inside this cell are excluded:
    /// flattening them into the outer cell folded a nested table's entire contents into one
    /// product name, and the nested table's rows are read as their own table anyway.
    /// </summary>
    private static string? CellText(TableCell cell)
    {
        var text = string.Join(" ", cell.Descendants<Paragraph>()
                .Where(p => !p.Ancestors<TableCell>().Any(ancestor => ancestor != cell))
                .Select(p => p.InnerText))
            .Replace(' ', ' ')
            .Trim();
        return text.Length == 0 ? null : text;
    }
}
