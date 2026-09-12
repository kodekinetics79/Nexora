using System.Globalization;
using System.Text.RegularExpressions;

namespace ERP_RFQ_Automation.Services.DocumentIntelligence;

/// <summary>
/// Reads a Word RFP that states each line item DOWN the page instead of across it.
///
/// <para><b>The shape.</b> Aramco/ASMO e-bidding exports put every line in a two-column form —
/// a label beside its value — repeated once per item inside one enormous table:</para>
/// <code>
/// 8 BATTERY: LEAD ACID, 12 V, 6 CELLS      (title, unique to this item)
/// BATTERY: LEAD ACID, 12 V, 6 CELLS...     (description)
/// Price                    |               (blank: the SUPPLIER fills this in)
/// Quantity                 | 1 each
/// Requested Delivery Date  | Fri, 1 Jan, 2027
/// Material Number          | 000000002000008534
/// ... thirty more labels ...
/// 9 BEARING Shell two halves for DE and NDE (next item begins)
/// </code>
///
/// <para><b>Why it needed its own reader.</b> <see cref="DocxTableParser"/> looks for a grid — one
/// header row, one row per item — and correctly refuses this: the header here is
/// "Name | Alternative | Value", which names no commercial field, so the "this is not a
/// line-item table" guard rejects it. The document then went to prose, where the extractor
/// counted each of 51,496 TABLE ROWS as a line item, arrived at ~103,000 items, and was refused
/// by the cost ceiling. A real RFP of 1,514 lines was turned away, and the count that turned it
/// away was wrong by a factor of thirty-four.</para>
///
/// <para><b>How blocks are found.</b> Not by matching known labels — by shape. A LABEL is a
/// left-hand value that recurs down the table; a TITLE is unique to its item. So the item
/// boundary is simply the point where a label we have already seen in this block comes round
/// again. That works whatever the labels are called, in any language, and needs no list of
/// spellings to be maintained. The labels are only consulted afterwards, to decide which field
/// each one means, and that uses the SAME vocabulary a column header does
/// (<see cref="RfqHeaderVocabulary.FieldForColumn"/>).</para>
///
/// <para><b>Deliberately narrow.</b> Refuses anything that does not look like a repeated form:
/// too few blocks, too few labels, or a table whose left column is mostly unique. A refusal
/// costs nothing — the document keeps the behaviour it has today.</para>
/// </summary>
public sealed class DocxFormBlockParser
{
    /// <summary>A left-hand value must recur at least this often to be read as a label.</summary>
    private const int MinimumLabelOccurrences = 3;

    /// <summary>Fewer repeated blocks than this and it is a form, not a line-item list.</summary>
    private const int MinimumBlocks = 1;

    /// <summary>A block must resolve at least this many real fields to be emitted.</summary>
    private const int MinimumResolvedFields = 1;

    /// <summary>
    /// "1 each", "25 EA", "10.5 M" — a count and its unit sharing one cell.
    ///
    /// <para>The unit is anything after the number that does not itself start with a digit, and
    /// it deliberately allows spaces and punctuation. A single alphabetic word is not enough:
    /// one line of a real 1,514-item RFP read "1 square meter/second", failed to split, then
    /// failed to parse as a number, and the item was dropped without a word. One silent loss in
    /// fifteen hundred is exactly the kind that reaches a customer as a missing line.</para>
    /// </summary>
    private static readonly Regex QuantityWithUnit =
        new(@"^\s*([0-9][0-9.,]*)\s+([^\d\s][^\r\n]*?)\s*$", RegexOptions.Compiled);

    private readonly RfqHeaderVocabulary _vocabulary;

    public DocxFormBlockParser() : this(null) { }

    public DocxFormBlockParser(RfqHeaderVocabulary? vocabulary)
        => _vocabulary = vocabulary ?? RfqHeaderVocabulary.Builtin;

    public IReadOnlyList<RfqSpreadsheetRow> Parse(
        IReadOnlyList<IReadOnlyList<string?>> grid, string sourceDocumentName, string worksheetName)
    {
        if (grid.Count < 4) return Array.Empty<RfqSpreadsheetRow>();

        // The value column is the RIGHTMOST populated one. These exports carry an unused
        // "Alternative" column in the middle, and taking column 2 blindly would read every value
        // as blank while reporting confident success.
        var width = grid.Max(r => r.Count);
        if (width < 2) return Array.Empty<RfqSpreadsheetRow>();
        var valueColumn = ValueColumn(grid, width);
        if (valueColumn <= 0) return Array.Empty<RfqSpreadsheetRow>();

        var labels = RecurringLeftHandValues(grid);
        // A label is also anything the vocabulary already knows as a field ("Quantity",
        // "Manufacturer Part Number", "Item Text"). Recurrence alone needs three items before
        // it believes a document, so a one-item event print — the SEC portal sends those —
        // read as nothing at all and went to the model for a table we could read ourselves.
        labels.UnionWith(RecognisedLeftHandValues(grid));
        labels.ExceptWith(TitlesUnderNumberedHeadings(grid, valueColumn));
        if (labels.Count < 2) return Array.Empty<RfqSpreadsheetRow>();

        var blocks = SplitIntoBlocks(grid, valueColumn, labels);
        if (blocks.Count < MinimumBlocks) return Array.Empty<RfqSpreadsheetRow>();
        // One block is a line only when the document introduced it as one — a numbered item
        // heading above it. A lone "Quantity | 5" under no heading is a terms table, not an item.
        if (blocks.Count == 1 && !blocks[0].Titles.Any(title => NumberedHeading.IsMatch(title.Trim())))
            return Array.Empty<RfqSpreadsheetRow>();

        var rows = new List<RfqSpreadsheetRow>();
        foreach (var block in blocks)
        {
            var row = BuildRow(block, sourceDocumentName, worksheetName);
            if (row is not null) rows.Add(row);
        }
        return rows;
    }

    /// <summary>"8 MODULE ADAPT ESD" — a numbered section heading, whose number is the buyer's own line number.</summary>
    private static readonly Regex NumberedHeading = new(@"^(\d{1,6})\s+(\S.*)$", RegexOptions.Compiled);

    /// <summary>
    /// Names that appear as the row directly under a numbered heading that repeats them
    /// ("40 OUTLET, SOCKET…" then "OUTLET, SOCKET…"). Such a name is an item's TITLE, whatever
    /// its frequency: an export that asks for the same socket three times repeats the title
    /// three times, and by repetition alone it looked like a label. Every one of those items
    /// then lost its title and was named by its heading number instead.
    /// </summary>
    private static HashSet<string> TitlesUnderNumberedHeadings(IReadOnlyList<IReadOnlyList<string?>> grid, int valueColumn)
    {
        // A name that ever carries a value is a label, however it also appears under a heading
        // ("3 Delivery" then "Delivery" as a section, and "Delivery | 8 weeks" on every item).
        var carriesValue = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in grid)
        {
            if (row.Count == 0) continue;
            var name = (row[0] ?? string.Empty).Trim();
            var value = valueColumn < row.Count ? (row[valueColumn] ?? string.Empty).Trim() : string.Empty;
            if (name.Length > 0 && value.Length > 0) carriesValue.Add(name);
        }
        var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index + 1 < grid.Count; index++)
        {
            var heading = grid[index].Count > 0 ? (grid[index][0] ?? string.Empty).Trim() : string.Empty;
            var next = grid[index + 1].Count > 0 ? (grid[index + 1][0] ?? string.Empty).Trim() : string.Empty;
            var match = NumberedHeading.Match(heading);
            if (match.Success && next.Length > 0
                && string.Equals(match.Groups[2].Value.Trim(), next, StringComparison.OrdinalIgnoreCase)
                && !carriesValue.Contains(next))
                titles.Add(next);
        }
        return titles;
    }

    private sealed class Block
    {
        public int StartRow;
        public readonly List<string> Titles = new();
        /// <summary>label -> (value, row number it was read from)</summary>
        public readonly List<(string Label, string Value, int Row)> Entries = new();
    }

    /// <summary>
    /// The rightmost column that actually carries values. Chosen by count rather than position so
    /// an unused middle column cannot silently become the answer.
    /// </summary>
    private static int ValueColumn(IReadOnlyList<IReadOnlyList<string?>> grid, int width)
    {
        var best = -1;
        var bestCount = 0;
        for (var column = 1; column < width; column++)
        {
            var count = grid.Count(r => column < r.Count && !string.IsNullOrWhiteSpace(r[column]));
            if (count >= bestCount) { bestCount = count; best = column; }
        }
        return bestCount == 0 ? -1 : best;
    }

    /// <summary>
    /// Left-hand values that recur are labels; values unique to one item are its title. Deciding
    /// this from repetition rather than from a list of known words is what lets the reader handle
    /// a layout, or a language, nobody anticipated.
    /// </summary>
    private HashSet<string> RecognisedLeftHandValues(IReadOnlyList<IReadOnlyList<string?>> grid)
    {
        var recognised = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in grid)
        {
            if (row.Count == 0) continue;
            var name = (row[0] ?? string.Empty).Trim();
            if (name.Length > 0 && _vocabulary.FieldForColumn(name) is not null) recognised.Add(name);
        }
        return recognised;
    }

    private static HashSet<string> RecurringLeftHandValues(IReadOnlyList<IReadOnlyList<string?>> grid)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in grid)
        {
            if (row.Count == 0) continue;
            var name = (row[0] ?? string.Empty).Trim();
            if (name.Length == 0) continue;
            counts[name] = counts.GetValueOrDefault(name) + 1;
        }
        return counts.Where(p => p.Value >= MinimumLabelOccurrences)
                     .Select(p => p.Key)
                     .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static List<Block> SplitIntoBlocks(
        IReadOnlyList<IReadOnlyList<string?>> grid, int valueColumn, HashSet<string> labels)
    {
        var blocks = new List<Block>();
        var pendingTitles = new List<string>();
        Block? current = null;

        for (var index = 0; index < grid.Count; index++)
        {
            var row = grid[index];
            if (row.Count == 0) continue;
            var name = (row[0] ?? string.Empty).Trim();
            if (name.Length == 0) continue;
            var value = valueColumn < row.Count ? (row[valueColumn] ?? string.Empty).Trim() : string.Empty;

            // Inside an item, a short label-shaped row the vocabulary does not know ("Extended
            // Price", "Copper Factor (%)") is one of the item's own fields, not the next item's
            // title. Only a NUMBERED heading opens the next item. Without this, every unknown
            // label ended the block, and a two-item print read each item as Price and Quantity
            // alone — the material number two rows further down belonged to a block nobody kept.
            var isLabel = labels.Contains(name)
                || (current is not null && current.Entries.Count > 0
                    && !NumberedHeading.IsMatch(name) && LooksLikeLabel(name));

            if (isLabel)
            {
                // The boundary. A label coming round again means the previous item ended, even
                // when no title separated them.
                if (current is not null && current.Entries.Any(e =>
                        string.Equals(e.Label, name, StringComparison.OrdinalIgnoreCase)))
                {
                    blocks.Add(current);
                    current = null;
                }

                current ??= new Block { StartRow = index + 1, Titles = { } };
                if (pendingTitles.Count > 0)
                {
                    current.Titles.AddRange(pendingTitles);
                    pendingTitles.Clear();
                }
                current.Entries.Add((name, value, index + 1));
                continue;
            }

            // A unique left-hand value. Inside a populated block it announces the next item;
            // before one it is that item's title.
            if (current is not null && current.Entries.Count > 0)
            {
                blocks.Add(current);
                current = null;
            }
            pendingTitles.Add(name);
            if (pendingTitles.Count > 2) pendingTitles.RemoveAt(0);
        }

        if (current is not null && current.Entries.Count > 0) blocks.Add(current);
        return blocks;
    }

    /// <summary>
    /// "8 MODULE ADAPT ESD" introduces "MODULE ADAPT ESD"; so does "8 10 909101154 BATTERY,STORAGE,MAX VOLT ..."
    /// introduce "10 909101154 BATTERY,STORAGE,MAX VOLT 1.5 V,830AH" — the portal cuts a long heading
    /// short with an ellipsis, and what remains is the start of the title.
    /// </summary>
    private static bool HeadingIntroduces(string headingText, string title)
    {
        var heading = headingText.Trim();
        var name = title.Trim();
        if (string.Equals(heading, name, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var ellipsis in new[] { "...", "…" })
        {
            if (!heading.EndsWith(ellipsis, StringComparison.Ordinal)) continue;
            var stem = heading[..^ellipsis.Length].TrimEnd();
            if (stem.Length >= 8 && name.StartsWith(stem, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>A title without the number a heading opens with, for comparing against repeated text.</summary>
    private static string TitleText(string title)
    {
        var trimmed = title.Trim();
        var numbered = NumberedHeading.Match(trimmed);
        var text = numbered.Success ? numbered.Groups[2].Value.Trim() : trimmed;
        foreach (var ellipsis in new[] { "...", "…" })
            if (text.EndsWith(ellipsis, StringComparison.Ordinal)) text = text[..^ellipsis.Length].TrimEnd();
        return text.Length >= 8 ? text : string.Empty;
    }

    /// <summary>A field name, not a sentence: short, few words, no closing full stop.</summary>
    private static bool LooksLikeLabel(string name)
        => name.Length <= 80
           && !name.EndsWith('.')
           && name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 8;

    private RfqSpreadsheetRow? BuildRow(Block block, string sourceDocumentName, string worksheetName)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var addresses = new Dictionary<string, string>(StringComparer.Ordinal);
        var headers = new Dictionary<int, string>();
        var fieldRows = new Dictionary<string, int>(StringComparer.Ordinal);

        var ordinal = 0;
        var unmapped = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (label, value, rowNumber) in block.Entries)
        {
            ordinal++;
            headers[ordinal] = label;
            var field = _vocabulary.FieldForColumn(label);
            if (field is null && !string.IsNullOrWhiteSpace(value) && !string.IsNullOrWhiteSpace(label)
                && unmapped.Count < NativeSpreadsheetParser.MaxUnmappedColumns)
                unmapped.TryAdd(label.Trim(), value.Trim());
            // A blank Price is the norm, not a defect: the buyer leaves it for us to quote. Only
            // populated labels become values, so an empty one never overwrites a real reading.
            if (field is null || string.IsNullOrWhiteSpace(value) || values.ContainsKey(field)) continue;
            values[field] = value.Trim();
            fieldRows[field] = rowNumber;
            addresses[field] = $"'{worksheetName.Replace("'", "''", StringComparison.Ordinal)}'!R{rowNumber}";
        }

        // "1 each" is a count and a unit in one cell. Split only when the unit slot is otherwise
        // empty, so a document that states its own unit column always wins.
        // The SEC portal prints the material cell as "909101154 10 909101154 BATTERY,STORAGE…":
        // the code, then the item's own heading again. A code is one token; when what follows
        // it is the item's title, the title is not part of the code.
        foreach (var codeField in new[] { RfqSpreadsheetFields.CustomerMaterialCode, RfqSpreadsheetFields.ManufacturerPartNumber })
        {
            if (!values.TryGetValue(codeField, out var code)) continue;
            var space = code.IndexOf(' ', StringComparison.Ordinal);
            if (space <= 0) continue;
            var rest = code[(space + 1)..].Trim();
            if (block.Titles.Any(title => TitleText(title).Length > 0
                    && rest.Contains(TitleText(title), StringComparison.OrdinalIgnoreCase)))
                values[codeField] = code[..space];
        }

        if (values.TryGetValue(RfqSpreadsheetFields.Quantity, out var quantity))
        {
            var match = QuantityWithUnit.Match(quantity);
            if (match.Success)
            {
                values[RfqSpreadsheetFields.Quantity] = match.Groups[1].Value;
                if (!values.ContainsKey(RfqSpreadsheetFields.UnitOfMeasure))
                {
                    values[RfqSpreadsheetFields.UnitOfMeasure] = match.Groups[2].Value;
                    if (fieldRows.TryGetValue(RfqSpreadsheetFields.Quantity, out var qRow))
                        addresses[RfqSpreadsheetFields.UnitOfMeasure] =
                            $"'{worksheetName.Replace("'", "''", StringComparison.Ordinal)}'!R{qRow}";
                }
            }
        }

        // The title carries the item's name, and the NEAREST preceding line is the right one —
        // not the longest. These exports repeat the description under a numbered heading
        // ("8 BATTERY: …" then "BATTERY: …"), so the nearest line is the clean description while
        // the one before it carries the section number. Picking the longest instead reached back
        // past both into the preamble and named the first item after a paragraph of bidding
        // instructions.
        string? customerLineNumber = null;
        if (!values.ContainsKey(RfqSpreadsheetFields.ProductName))
        {
            var title = block.Titles.LastOrDefault();
            if (!string.IsNullOrWhiteSpace(title))
            {
                // "8 MODULE ADAPT ESD" directly above "MODULE ADAPT ESD": the number is the
                // buyer's own line number, and the title is the line without it.
                var heading = block.Titles.Count >= 2 ? block.Titles[^2] : null;
                var numbered = heading is null ? Match.Empty : NumberedHeading.Match(heading);
                if (numbered.Success && HeadingIntroduces(numbered.Groups[2].Value, title))
                {
                    customerLineNumber = numbered.Groups[1].Value;
                    // "8 10 909101154 BATTERY…" above "10 909101154 BATTERY…": the outer number
                    // is the print's own section index; the buyer's line number and material are
                    // the two tokens the title opens with. Read that way ONLY when the second
                    // token is the material code the block states — "10 KV CABLE" stays a name.
                    var inner = NumberedHeading.Match(title.Trim());
                    var statedCode = values.GetValueOrDefault(RfqSpreadsheetFields.CustomerMaterialCode)
                        ?? values.GetValueOrDefault(RfqSpreadsheetFields.ManufacturerPartNumber);
                    if (inner.Success && !string.IsNullOrWhiteSpace(statedCode)
                        && inner.Groups[2].Value.Trim().StartsWith(statedCode + " ", StringComparison.Ordinal))
                    {
                        customerLineNumber = inner.Groups[1].Value;
                        title = inner.Groups[2].Value.Trim()[(statedCode.Length + 1)..];
                    }
                }
                else
                {
                    // Only a numbered heading was left ("40 OUTLET, SOCKET…"): the number is still
                    // the buyer's, and the title is what follows it.
                    var own = NumberedHeading.Match(title.Trim());
                    if (own.Success && block.Titles.Count == 1)
                    {
                        customerLineNumber = own.Groups[1].Value;
                        title = own.Groups[2].Value;
                    }
                }
                values[RfqSpreadsheetFields.ProductName] = title.Trim();
                addresses[RfqSpreadsheetFields.ProductName] =
                    $"'{worksheetName.Replace("'", "''", StringComparison.Ordinal)}'!R{block.StartRow}";
            }
        }

        if (values.Count < MinimumResolvedFields) return null;

        // A QUOTABLE LINE STATES A COUNT. This is what separates the line-item table from the
        // scoring table that sits beside it in these exports — same labels, same 1,514 blocks,
        // but its values are weightings ("Price | 0%", "Quantity | 0%"). Without this rule the
        // reader returned 3,028 rows for a 1,514-line document and stamped "0%" onto unit price.
        // Read prose as a quantity and we would quote against a number nobody wrote.
        if (!HasCountableQuantity(values)) return null;

        // Same materiality rule the column path uses: something commercial has to be present.
        var row = new RfqSpreadsheetRow
        {
            RowNumber = block.StartRow,
            SourceDocumentName = sourceDocumentName,
            WorksheetName = worksheetName,
            HeaderRowNumber = block.StartRow,
            HeadersByColumn = headers,
            FieldColumnNumbers = fieldRows.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
            FieldSourceAddresses = addresses,
            UnmappedColumns = unmapped,
            CustomerLineNumber = customerLineNumber,
            RfqNo = Get(values, RfqSpreadsheetFields.RfqNo),
            BuyerName = Get(values, RfqSpreadsheetFields.BuyerName),
            ReceivedDate = Get(values, RfqSpreadsheetFields.ReceivedDate),
            BidClosingDate = Get(values, RfqSpreadsheetFields.BidClosingDate),
            ProductName = Get(values, RfqSpreadsheetFields.ProductName),
            Quantity = Get(values, RfqSpreadsheetFields.Quantity),
            UnitOfMeasure = Get(values, RfqSpreadsheetFields.UnitOfMeasure),
            UnitPrice = Get(values, RfqSpreadsheetFields.UnitPrice),
            Currency = Get(values, RfqSpreadsheetFields.Currency),
            ManufacturerName = Get(values, RfqSpreadsheetFields.ManufacturerName),
            ManufacturerPartNumber = Get(values, RfqSpreadsheetFields.ManufacturerPartNumber),
            LeadTimeDays = Get(values, RfqSpreadsheetFields.LeadTimeDays),
            ItemText = Get(values, RfqSpreadsheetFields.ItemText),
            CustomerMaterialCode = Get(values, RfqSpreadsheetFields.CustomerMaterialCode),
            MaterialPoText = Get(values, RfqSpreadsheetFields.MaterialPoText),
            DeliveryLocation = Get(values, RfqSpreadsheetFields.DeliveryLocation),
            RequiredDeliveryDate = Get(values, RfqSpreadsheetFields.RequiredDeliveryDate),
            AgreementReference = Get(values, RfqSpreadsheetFields.AgreementReference)
        };

        var material = row.ProductName ?? row.Quantity ?? row.ManufacturerPartNumber;
        return string.IsNullOrWhiteSpace(material) ? null : row;
    }

    /// <summary>True when the quantity reads as a positive number, once any unit is split off.</summary>
    private static bool HasCountableQuantity(IReadOnlyDictionary<string, string> values)
        => values.TryGetValue(RfqSpreadsheetFields.Quantity, out var quantity)
           && decimal.TryParse(quantity.Replace(",", string.Empty, StringComparison.Ordinal).Trim(),
               NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
           && number > 0;

    private static string? Get(IReadOnlyDictionary<string, string> values, string field)
        => values.TryGetValue(field, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
}
