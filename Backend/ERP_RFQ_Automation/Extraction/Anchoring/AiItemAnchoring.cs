using System.Globalization;
using System.Text.RegularExpressions;
using ERP_RFQ_Automation.Extraction.Quantities;
using ERP_RFQ_Automation.Services.Interfaces;
using ERP_RFQ_Automation.Services.Uom;

namespace ERP_RFQ_Automation.Extraction.Anchoring;

/// <summary>What the page text said about one model-read line.</summary>
public enum AnchorDisposition
{
    /// <summary>The line's code, part number or description was found in the text it was read from.</summary>
    Anchored,

    /// <summary>Nothing identifying the line was found. It is kept, its quote-critical values cleared.</summary>
    NotFound,

    /// <summary>
    /// Found only in the header context every call carries, never in the call's own pages: the
    /// same line read again from the reference block. It is dropped, never saved twice.
    /// </summary>
    HeaderEcho
}

/// <param name="Item">The line as it will be saved: checked values, document evidence, and nothing the page does not say.</param>
/// <param name="Cleared">Fields the model returned that the page does not state; now null, for a person to fill.</param>
/// <param name="Corrected">Fields whose model value disagreed with the one value the page states; the page value was kept.</param>
public sealed record AnchoredItem(
    LeadItemData Item,
    AnchorDisposition Disposition,
    IReadOnlyList<string> Cleared,
    IReadOnlyList<string> Corrected);

/// <summary>
/// Checks every quote-critical value a model read from a document against the text it read it
/// from, and writes the exact source of each value it finds.
///
/// <para><b>Why.</b> The model read "CIRCUIT: TYP CONTROL BOARD 6 SAR … 978596 EA 1" as quantity
/// 1 (the 1 is the price-per-unit column), "2 Assembley" as 1, glued the item number to the
/// material number ("00010201195514") and composed a material code out of the item category and
/// the short text ("MATLFLDLGHT-ELCTRC"), every time at a self-reported confidence of 0.95–1.00.
/// Nothing checked the answer, so a wrong quantity went to the quote. And because no value carried
/// a source, every model-read line was MISSING_SOURCE on Decide and could not be quoted at all
/// without the review override.</para>
///
/// <para><b>The rule.</b> A value is kept only when the line's own text states it. Quantity and
/// unit must appear as a quantity does — "Quantity 1 each", a "2 Assembley" line, the SABIC
/// order-quantity column — in the stretch of text that belongs to the line; a buyer material code
/// and a line number must appear as whole tokens. A value the page does not state is cleared and
/// the line goes to a person; when the page states exactly one different value, the page wins and
/// the correction is recorded. Every kept value carries its exact text and "Page N, line M" as
/// evidence, so the line reads NEEDS_CHECK — checkable — instead of MISSING_SOURCE. Nothing is
/// ever invented: evidence is a substring of the page, and a value with no substring has none.</para>
/// </summary>
public static partial class AiItemAnchoring
{
    /// <summary>Longest stretch of text one line may claim after its start.</summary>
    private const int MaxWindowCharacters = 6_000;

    /// <summary>Lines above a line's first match searched for its item number ("1 Material" above "CIRCUIT: …").</summary>
    private const int LinesAboveForLineNumber = 3;

    /// <summary>Shortest description prefix (letters and digits) trusted to locate a line.</summary>
    private const int MinimumNameAnchor = 6;

    /// <summary>Longest description prefix used to locate a line; descriptions are often cut or reworded later on.</summary>
    private const int MaximumNameAnchor = 40;

    private const decimal EvidenceConfidence = 1.0m;

    private static readonly HashSet<string> Currencies = new(StringComparer.Ordinal)
    {
        "SAR", "USD", "EUR", "GBP", "AED", "KWD", "QAR", "BHD", "OMR", "JPY", "CNY", "INR", "CHF", "EGP", "JOD"
    };

    private static readonly HashSet<string> AssemblySpellings = new(StringComparer.OrdinalIgnoreCase)
    {
        "ASSEMBLY", "ASSEMBLEY", "ASSEMBLIES", "ASSY", "ASSYS", "ASM"
    };

    /// <summary>
    /// Checks the lines one extraction call returned against that call's own text.
    /// <paramref name="headerContext"/> is the reference block sent alongside it; a line found only
    /// there is a <see cref="AnchorDisposition.HeaderEcho"/>.
    /// </summary>
    public static IReadOnlyList<AnchoredItem> AnchorChunk(
        IReadOnlyList<LeadItemData> items, AnchorText own, string? headerContext)
    {
        if (items.Count == 0) return Array.Empty<AnchoredItem>();
        try
        {
            return AnchorChunkCore(items, own, headerContext);
        }
        catch (RegexMatchTimeoutException)
        {
            // A check that cannot finish proves nothing. Every line is kept, unchecked: its
            // quote-critical values cleared for a person, exactly as for a line not found.
            return items.Select(NotFound).ToList();
        }
    }

    private static IReadOnlyList<AnchoredItem> AnchorChunkCore(
        IReadOnlyList<LeadItemData> items, AnchorText own, string? headerContext)
    {
        var sabic = SabicBidLayout.Recognises(own.Text) || SabicBidLayout.Recognises(headerContext);

        // 1. Where each line starts in the text. Searching from the previous line's start keeps a
        //    description repeated further down (Aramco prints it three times) from pulling a line
        //    onto another line's block.
        var located = new Located?[items.Count];
        var searchFrom = 0;
        for (var i = 0; i < items.Count; i++)
        {
            located[i] = Locate(items[i], own, searchFrom) ?? (searchFrom > 0 ? Locate(items[i], own, 0) : null);
            if (located[i] is { } found) searchFrom = found.Start;
        }

        // 2. Each located line owns the text from its start to the next line's start. The first
        //    line also owns what precedes it: on a SABIC print the quantity sits on the description
        //    line but the line number two lines above it.
        var starts = located.Where(l => l is not null).Select(l => l!.Start).Distinct().OrderBy(s => s).ToArray();

        var output = new List<AnchoredItem>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (located[i] is not { } found)
            {
                var echo = !string.IsNullOrWhiteSpace(headerContext) && AppearsIn(item, AnchorText.Build([headerContext!]));
                output.Add(echo
                    ? new AnchoredItem(item, AnchorDisposition.HeaderEcho, [], [])
                    : NotFound(item));
                continue;
            }

            var index = Array.IndexOf(starts, found.Start);
            var windowStart = index == 0 ? 0 : found.Start;
            var windowEnd = index + 1 < starts.Length ? starts[index + 1] : own.Text.Length;
            windowEnd = Math.Min(windowEnd, Math.Max(found.Start, windowStart) + MaxWindowCharacters);
            output.Add(Check(item, found, own, windowStart, windowEnd, sabic));
        }
        return output;
    }

    /// <summary>
    /// How many lines this text visibly holds: one per printed quantity ("Quantity 1 each", a
    /// "2 Assembley" line, a SABIC order-quantity cell). A floor, not a count — a document that
    /// prints no quantities contributes nothing — used only to say "read 3 of about 42".
    /// </summary>
    public static int EstimateLines(AnchorText text)
    {
        try
        {
            var sabic = SabicBidLayout.Recognises(text.Text);
            return QuantityCandidates(text.Text, 0, text.Text.Length, sabic, strongOnly: true).Count;
        }
        catch (RegexMatchTimeoutException)
        {
            return 0; // no estimate: the read is judged on failed calls alone
        }
    }

    // ---- locating a line -------------------------------------------------------------------

    private sealed record Located(int Start, TokenMatch? Material, TokenMatch? Part, TokenMatch? Name, SplitCode? Split);

    private sealed record TokenMatch(int Offset, int Length, string Raw);

    /// <summary>An item number glued to a material number: "00010" + "201195514" read as one value.</summary>
    private sealed record SplitCode(TokenMatch ItemNumber, TokenMatch Material);

    private static Located? Locate(LeadItemData item, AnchorText own, int from)
    {
        TokenMatch? material = null;
        SplitCode? split = null;
        if (Clean(item.ItemMaterialCode) is { } code)
        {
            material = FindToken(own.Text, code, from);
            if (material is null && IsDigits(code) && code.Length >= 8)
                split = FindSplitCode(own.Text, code, from);
        }

        TokenMatch? part = Clean(item.ManufacturerPartNumber) is { } mpn
            ? FindToken(own.Text, mpn, from) ?? FindSpaced(own.Text, mpn, from)
            : null;

        var name = FindNamePrefix(own, Clean(item.ProductShortName) ?? Clean(item.ProductShortDescription), from);

        var anchors = new[] { material?.Offset, split?.ItemNumber.Offset, part?.Offset, name?.Offset }
            .Where(o => o.HasValue).Select(o => o!.Value).ToArray();
        if (anchors.Length == 0) return null;
        var start = own.LineStart(own.LineIndexOf(anchors.Min()));
        return new Located(start, material, part, name, split);
    }

    private static bool AppearsIn(LeadItemData item, AnchorText text)
        => (Clean(item.ItemMaterialCode) is { } code
                && (FindToken(text.Text, code, 0) is not null || FindSplitCode(text.Text, code, 0) is not null))
           || (Clean(item.ManufacturerPartNumber) is { } mpn
                && (FindToken(text.Text, mpn, 0) ?? FindSpaced(text.Text, mpn, 0)) is not null)
           || FindNamePrefix(text, Clean(item.ProductShortName) ?? Clean(item.ProductShortDescription), 0) is not null;

    private static AnchoredItem NotFound(LeadItemData item)
    {
        var cleared = new List<string>();
        if (item.Quantity is not null) cleared.Add("Quantity");
        if (Clean(item.UnitOfMeasure) is not null) cleared.Add("UnitOfMeasure");
        if (Clean(item.ItemMaterialCode) is not null) cleared.Add("ItemMaterialCode");
        if (Clean(item.LineItemNo) is not null) cleared.Add("LineItemNo");
        return new AnchoredItem(item with
        {
            Quantity = null,
            UnitOfMeasure = null,
            ItemMaterialCode = null,
            LineItemNo = null,
            ExtraFields = null,
            VerifiedEvidence = null,
            EvidenceFromDocumentCheck = true
        }, AnchorDisposition.NotFound, cleared, []);
    }

    // ---- checking one line -----------------------------------------------------------------

    private static AnchoredItem Check(
        LeadItemData item, Located found, AnchorText own, int windowStart, int windowEnd, bool sabic)
    {
        var evidence = new List<LeadItemEvidenceData>();
        var cleared = new List<string>();
        var corrected = new List<string>();
        var text = own.Text;
        bool InWindow(TokenMatch m) => m.Offset >= windowStart && m.Offset < windowEnd;
        void Cite(string field, TokenMatch m, string? normalized)
            => evidence.Add(new LeadItemEvidenceData(field, m.Raw, normalized, own.Address(m.Offset), EvidenceConfidence));

        // -- buyer material code: a whole token, never a composition --------------------------
        var materialCode = Clean(item.ItemMaterialCode);
        TokenMatch? splitItemNumber = null;
        if (materialCode is not null)
        {
            if (found.Material is { } material && InWindow(material) && material.Raw.Any(char.IsDigit))
                Cite("ItemMaterialCode", material, materialCode);
            else if (found.Split is { } split && InWindow(split.Material))
            {
                materialCode = split.Material.Raw;
                splitItemNumber = split.ItemNumber;
                corrected.Add("ItemMaterialCode");
                Cite("ItemMaterialCode", split.Material, materialCode);
            }
            else
            {
                materialCode = null;
                cleared.Add("ItemMaterialCode");
            }
        }
        if (materialCode is null && sabic && SabicBidLayout.ProductNumber(text, windowStart, windowEnd) is { } productNumber)
        {
            // A SABIC BID prints the buyer's product number in its own column, followed by the
            // unit: "1637334 EA 1". It is the material code the model missed or composed.
            materialCode = productNumber.Raw;
            cleared.Remove("ItemMaterialCode");
            corrected.Add("ItemMaterialCode");
            Cite("ItemMaterialCode", productNumber, materialCode);
        }

        // -- maker's part number: checked, cited, never cleared (not in the brief's four) ------
        var mpn = Clean(item.ManufacturerPartNumber);
        if (mpn is not null)
        {
            var part = found.Part is { } p && InWindow(p) ? p
                : FindToken(text, mpn, windowStart, windowEnd) ?? FindSpaced(text, mpn, windowStart, windowEnd);
            if (part is not null) Cite("ManufacturerPartNumber", part, mpn);
        }

        // -- the buyer's line number: as printed, never a position ---------------------------
        var lineNumber = Clean(item.LineItemNo);
        var startLine = own.LineIndexOf(found.Start);
        var sabicItemNumber = sabic ? SabicBidLayout.ItemNumber(text, windowStart, windowEnd) : null;
        if (lineNumber is not null)
        {
            var printed = FindLineNumber(own, lineNumber, startLine, windowStart, windowEnd)
                ?? (splitItemNumber is not null && SameNumber(splitItemNumber.Raw, lineNumber) ? splitItemNumber : null)
                ?? (sabicItemNumber is not null && SameNumber(sabicItemNumber.Raw, lineNumber) ? sabicItemNumber : null);
            if (printed is not null)
                Cite("LineItemNo", printed, lineNumber);
            else
            {
                // The model's number is not printed here: typically its own count (1, 2, 3 for the
                // buyer's 8, 9, 35) or the glued code. Take the number the line is printed under,
                // when there is exactly that; otherwise leave it empty — a position is never a
                // buyer's line number.
                var recovered = splitItemNumber ?? sabicItemNumber ?? LeadingLineNumber(own, startLine, windowStart);
                if (recovered is not null)
                {
                    lineNumber = recovered.Raw;
                    corrected.Add("LineItemNo");
                    Cite("LineItemNo", recovered, lineNumber);
                }
                else
                {
                    lineNumber = null;
                    cleared.Add("LineItemNo");
                }
            }
        }
        else if (splitItemNumber is not null)
        {
            lineNumber = splitItemNumber.Raw;
            Cite("LineItemNo", splitItemNumber, lineNumber);
        }

        // -- quantity and unit: as a quantity is printed, inside the line's own text ---------
        var quantity = item.Quantity;
        var unit = Clean(item.UnitOfMeasure);
        var anchorTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (materialCode is not null) anchorTokens.Add(materialCode);
        if (mpn is not null) anchorTokens.Add(mpn);
        var strong = QuantityCandidates(text, windowStart, windowEnd, sabic, strongOnly: true);
        var candidates = strong.Count > 0
            ? strong
            : QuantityCandidates(text, windowStart, windowEnd, sabic, strongOnly: false)
                .Where(c => !anchorTokens.Contains(c.Quantity.Raw)).ToList();

        var agreeing = quantity is { } modelQuantity
            ? candidates.FirstOrDefault(c => c.Value == modelQuantity && UnitsAgree(c.Unit?.Raw, unit))
            : null;
        QuantityCandidate? chosen = agreeing;
        if (chosen is null && strong.Count > 0 && strong.Select(c => c.Value).Distinct().Count() == 1
            && strong.Where(c => c.Unit is not null).Select(c => UomCanonicalizer.EquivalenceKey(c.Unit!.Raw)).Distinct().Count() <= 1)
        {
            // The page states one quantity for this line and the model said something else — or
            // nothing. The page wins; the correction is recorded and the line goes to a person.
            chosen = strong.FirstOrDefault(c => c.Unit is not null) ?? strong[0];
            if (quantity is not null) corrected.Add("Quantity");
        }

        if (chosen is not null)
        {
            quantity = chosen.Value;
            Cite("Quantity", chosen.Quantity, chosen.Value.ToString(CultureInfo.InvariantCulture));
            if (chosen.Unit is { } printedUnit)
            {
                if (unit is null || !UnitsAgree(printedUnit.Raw, unit))
                {
                    if (unit is not null) corrected.Add("UnitOfMeasure");
                    unit = printedUnit.Raw;
                }
                Cite("UnitOfMeasure", printedUnit, UomCanonicalizer.CanonicalizeForStorage(unit));
            }
            else if (unit is not null)
                unit = CheckUnitAlone(unit, text, windowStart, windowEnd, cleared, Cite);
        }
        else
        {
            if (quantity is not null) cleared.Add("Quantity");
            quantity = null;
            if (unit is not null)
                unit = CheckUnitAlone(unit, text, windowStart, windowEnd, cleared, Cite);
        }

        // -- description and maker: cited when printed, never cleared -----------------------
        var name = Clean(item.ProductShortName);
        if (name is not null && FindWhole(own, name, windowStart, windowEnd) is { } nameMatch)
            Cite("ProductShortName", nameMatch, name);
        var description = Clean(item.ProductShortDescription);
        if (description is not null && FindWhole(own, description, windowStart, windowEnd) is { } descriptionMatch)
            Cite("ProductShortDescription", descriptionMatch, description);
        var maker = Clean(item.ManufacturerName);
        if (maker is not null && AnchorText.CanonicalOf(maker).Length >= 3
            && FindWhole(own, maker, windowStart, windowEnd) is { } makerMatch)
            Cite("ManufacturerName", makerMatch, maker);

        var extras = CheckedExtraFields(item.ExtraFields, own, windowStart, windowEnd);

        var checkedItem = item with
        {
            ItemMaterialCode = materialCode,
            LineItemNo = lineNumber,
            Quantity = quantity,
            UnitOfMeasure = unit,
            ExtraFields = extras,
            VerifiedEvidence = evidence.Count == 0 ? null : evidence,
            EvidenceFromDocumentCheck = true
        };
        return new AnchoredItem(checkedItem, AnchorDisposition.Anchored, cleared, corrected);
    }

    private static string? CheckUnitAlone(
        string unit, string text, int windowStart, int windowEnd, List<string> cleared,
        Action<string, TokenMatch, string?> cite)
    {
        if (FindToken(text, unit, windowStart, windowEnd) is { } printed)
        {
            cite("UnitOfMeasure", printed, UomCanonicalizer.CanonicalizeForStorage(unit));
            return unit;
        }
        cleared.Add("UnitOfMeasure");
        return null;
    }

    private static bool UnitsAgree(string? printed, string? model)
        => printed is null || model is null
           || string.Equals(UomCanonicalizer.EquivalenceKey(printed), UomCanonicalizer.EquivalenceKey(model),
               StringComparison.OrdinalIgnoreCase)
           || (AssemblySpellings.Contains(printed.Trim('.')) && AssemblySpellings.Contains(model.Trim('.')));

    // ---- quantity candidates ---------------------------------------------------------------

    private sealed record QuantityCandidate(decimal Value, TokenMatch Quantity, TokenMatch? Unit);

    /// <summary>
    /// Quantities as documents print them. STRONG shapes are unambiguous: a "Quantity"/"Qty" label
    /// with its number; a line that is nothing but a number and a unit ("2     Assembley"); the SABIC
    /// order-quantity cell (a number followed by the currency code). The WEAK shape — a number
    /// followed by a count unit anywhere in a line — is consulted only when no strong shape exists,
    /// and only to confirm the model's own value, never to replace it: "40 KVA", "150 W" and
    /// "2 M" of cable are specifications, not demand.
    /// </summary>
    private static List<QuantityCandidate> QuantityCandidates(
        string text, int start, int end, bool sabic, bool strongOnly)
    {
        var results = new List<QuantityCandidate>();
        if (end <= start) return results;
        var window = text[start..end];

        if (strongOnly)
        {
            foreach (Match m in LabelledQuantity().Matches(window))
                if (Candidate(m, start, requireKnownUnit: false) is { } c) results.Add(c);
            foreach (Match m in StandaloneQuantityLine().Matches(window))
                if (Candidate(m, start, requireKnownUnit: true) is { } c) results.Add(c);
            if (sabic)
            {
                var unit = SabicBidLayout.Unit(text, start, end);
                foreach (Match m in SabicBidLayout.QuantityBeforeCurrency().Matches(window))
                {
                    if (!Currencies.Contains(m.Groups["cur"].Value)) continue;
                    var q = m.Groups["q"];
                    var reading = QuantityParser.Parse(q.Value);
                    if (reading.Value is not { } value) continue;
                    results.Add(new QuantityCandidate(value, new TokenMatch(start + q.Index, q.Length, q.Value), unit));
                }
            }
            return results.GroupBy(c => c.Quantity.Offset).Select(g => g.First()).OrderBy(c => c.Quantity.Offset).ToList();
        }

        foreach (Match m in InlineQuantity().Matches(window))
        {
            var u = m.Groups["u"].Value;
            if (!IsCountUnit(u)) continue;
            if (Candidate(m, start, requireKnownUnit: true) is { } c) results.Add(c);
        }
        return results;
    }

    private static QuantityCandidate? Candidate(Match m, int offset, bool requireKnownUnit)
    {
        var q = m.Groups["q"];
        var reading = QuantityParser.Parse(q.Value);
        if (reading.Value is not { } value) return null;
        TokenMatch? unit = null;
        var u = m.Groups["u"];
        if (u.Success && u.Length > 0)
        {
            var raw = u.Value.TrimEnd('.');
            if (IsKnownUnit(raw)) unit = new TokenMatch(offset + u.Index, raw.Length, raw);
            else if (requireKnownUnit) return null;
        }
        else if (requireKnownUnit) return null;
        return new QuantityCandidate(value, new TokenMatch(offset + q.Index, q.Length, q.Value), unit);
    }

    /// <summary>A word a quantity column holds: any unit the canonicaliser knows, maps or deliberately refuses.</summary>
    internal static bool IsKnownUnit(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        if (AssemblySpellings.Contains(token.Trim('.'))) return true;
        var reading = UomCanonicalizer.Canonicalize(token);
        return reading.Resolution == UomResolution.Canonical
               || reading.ReviewReason is UomReviewReason.Packaging or UomReviewReason.FormFactor;
    }

    /// <summary>A unit that counts things. Length, mass and volume after a number are usually specifications.</summary>
    private static bool IsCountUnit(string token)
    {
        if (AssemblySpellings.Contains(token.Trim('.'))) return true;
        var reading = UomCanonicalizer.Canonicalize(token);
        return reading.CanonicalCode is "EA" or "SET" or "PR" or "DZ" or "LOT" or "AU"
               || reading.ReviewReason == UomReviewReason.Packaging;
    }

    // ---- line numbers ----------------------------------------------------------------------

    private static TokenMatch? FindLineNumber(AnchorText own, string lineNumber, int startLine, int windowStart, int windowEnd)
    {
        for (var line = startLine; line >= Math.Max(0, startLine - LinesAboveForLineNumber); line--)
        {
            var text = own.Line(line);
            var m = LeadingToken().Match(text);
            if (m.Success && SameNumber(m.Groups[1].Value, lineNumber))
                return new TokenMatch(own.LineStart(line) + m.Groups[1].Index, m.Groups[1].Length, m.Groups[1].Value);
        }
        var window = own.Text[windowStart..windowEnd];
        foreach (Match m in LabelledItemNumber().Matches(window))
            if (SameNumber(m.Groups["n"].Value, lineNumber))
                return new TokenMatch(windowStart + m.Groups["n"].Index, m.Groups["n"].Length, m.Groups["n"].Value);
        return null;
    }

    /// <summary>"8 BASE, LAMP…", "1 Material": the number a line is printed under, from its own first lines.</summary>
    private static TokenMatch? LeadingLineNumber(AnchorText own, int startLine, int windowStart)
    {
        for (var line = startLine; line >= Math.Max(0, startLine - LinesAboveForLineNumber); line--)
        {
            if (own.LineStart(line) < windowStart && line != startLine) break;
            var m = NumberedLine().Match(own.Line(line));
            if (m.Success)
                return new TokenMatch(own.LineStart(line) + m.Groups[1].Index, m.Groups[1].Length, m.Groups[1].Value);
        }
        return null;
    }

    private static bool SameNumber(string printed, string model)
    {
        if (string.Equals(printed.Trim(), model.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
        return IsDigits(printed) && IsDigits(model)
               && string.Equals(printed.TrimStart('0'), model.TrimStart('0'), StringComparison.Ordinal)
               && printed.TrimStart('0').Length > 0;
    }

    // ---- extra columns ---------------------------------------------------------------------

    /// <summary>
    /// A buyer column the schema does not map is kept only when its value is printed in the line's
    /// text. A quantity or unit "column" is never kept: those ARE schema fields, and the model
    /// invented "RFQ Quantity": "1" on a line printed as "2 Assembley".
    /// </summary>
    private static Dictionary<string, string>? CheckedExtraFields(
        Dictionary<string, string>? extras, AnchorText own, int windowStart, int windowEnd)
    {
        if (extras is not { Count: > 0 }) return extras;
        var (canonical, map) = own.Canonical();
        var from = LowerBound(map, windowStart);
        var to = LowerBound(map, windowEnd);
        var windowCanonical = canonical[from..to];
        var kept = new Dictionary<string, string>();
        foreach (var (key, value) in extras)
        {
            if (QuantityOrUnitKey().IsMatch(key.Trim())) continue;
            var needle = AnchorText.CanonicalOf(value);
            if (needle.Length == 0) continue;
            var printed = needle.Length >= 3
                ? windowCanonical.Contains(needle, StringComparison.Ordinal)
                : FindToken(own.Text, value.Trim(), windowStart, windowEnd) is not null;
            if (printed) kept[key] = value;
        }
        return kept.Count == 0 ? null : kept;
    }

    // ---- text search -----------------------------------------------------------------------

    private static TokenMatch? FindToken(string text, string value, int from, int to = -1)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var end = to < 0 ? text.Length : Math.Min(to, text.Length);
        var index = Math.Max(0, from);
        while (index < end)
        {
            var found = text.IndexOf(value, index, StringComparison.OrdinalIgnoreCase);
            if (found < 0 || found >= end) return null;
            if (Bounded(text, found, value.Length))
                return new TokenMatch(found, value.Length, text.Substring(found, value.Length));
            index = found + 1;
        }
        return null;
    }

    /// <summary>
    /// A token boundary on both sides. Letters and digits continue a token, and so do the joiners
    /// codes carry (<c>- _ / # .</c>) when another letter or digit follows them — so "IP65." at the
    /// end of a sentence is the token IP65, while "IP65.2" is not.
    /// </summary>
    private static bool Bounded(string text, int start, int length)
    {
        if (start > 0)
        {
            var before = text[start - 1];
            if (char.IsLetterOrDigit(before)) return false;
            if (IsJoiner(before) && start > 1 && char.IsLetterOrDigit(text[start - 2])) return false;
        }
        var end = start + length;
        if (end < text.Length)
        {
            var after = text[end];
            if (char.IsLetterOrDigit(after)) return false;
            if (IsJoiner(after) && end + 1 < text.Length && char.IsLetterOrDigit(text[end + 1])) return false;
        }
        return true;
    }

    private static bool IsJoiner(char c) => c is '-' or '_' or '/' or '#' or '.';

    /// <summary>A part number printed with spaces the model left out: "559 0204MD 98683" for "5590204MD98683".</summary>
    private static TokenMatch? FindSpaced(string text, string value, int from, int to = -1)
    {
        var characters = value.Where(c => !char.IsWhiteSpace(c)).ToArray();
        if (characters.Length < 4) return null;
        var pattern = string.Join(@"[ \t]*", characters.Select(c => Regex.Escape(c.ToString())));
        var end = to < 0 ? text.Length : Math.Min(to, text.Length);
        if (end <= from) return null;
        var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(2000));
        try
        {
            for (var m = regex.Match(text, Math.Max(0, from), end - Math.Max(0, from)); m.Success; m = m.NextMatch())
                if (Bounded(text, m.Index, m.Length))
                    return new TokenMatch(m.Index, m.Length, m.Value);
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
        return null;
    }

    /// <summary>
    /// "00010 201195514" printed, "00010201195514" returned: the SAP item number (1–6 digits) glued
    /// to the material number (5+ digits). Only this exact two-token digit shape is split; anything
    /// else that merely concatenates to the model's value ("MATL" + "FLDLGHT-ELCTRC") is refused.
    /// </summary>
    private static SplitCode? FindSplitCode(string text, string code, int from)
    {
        if (!IsDigits(code)) return null;
        var spaced = FindSpaced(text, code, from);
        if (spaced is null) return null;
        var parts = spaced.Raw.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || parts[0].Length is < 1 or > 6 || parts[1].Length < 5) return null;
        var second = spaced.Raw.IndexOf(parts[1], parts[0].Length, StringComparison.Ordinal);
        return new SplitCode(
            new TokenMatch(spaced.Offset, parts[0].Length, parts[0]),
            new TokenMatch(spaced.Offset + second, parts[1].Length, parts[1]));
    }

    /// <summary>Where a line's description begins, on letters and digits only, from its first 40 of them.</summary>
    private static TokenMatch? FindNamePrefix(AnchorText own, string? name, int from)
    {
        var needle = AnchorText.CanonicalOf(name);
        if (needle.Length < MinimumNameAnchor) return null;
        if (needle.Length > MaximumNameAnchor) needle = needle[..MaximumNameAnchor];
        var (canonical, map) = own.Canonical();
        var index = canonical.IndexOf(needle, LowerBound(map, from), StringComparison.Ordinal);
        if (index < 0) return null;
        var start = map[index];
        var end = map[index + needle.Length - 1] + 1;
        return new TokenMatch(start, end - start, own.Text[start..end]);
    }

    /// <summary>The whole of <paramref name="value"/>, on letters and digits only, inside the window first, then anywhere.</summary>
    private static TokenMatch? FindWhole(AnchorText own, string value, int windowStart, int windowEnd)
    {
        var needle = AnchorText.CanonicalOf(value);
        if (needle.Length == 0) return null;
        var (canonical, map) = own.Canonical();
        var index = canonical.IndexOf(needle, LowerBound(map, windowStart), StringComparison.Ordinal);
        if (index < 0 || map[index] >= windowEnd) index = canonical.IndexOf(needle, StringComparison.Ordinal);
        if (index < 0) return null;
        var start = map[index];
        var end = map[index + needle.Length - 1] + 1;
        return new TokenMatch(start, end - start, own.Text[start..end]);
    }

    private static int LowerBound(int[] map, int offset)
    {
        var index = Array.BinarySearch(map, offset);
        return index >= 0 ? index : ~index;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsDigits(string value) => value.Length > 0 && value.All(char.IsDigit);

    // ---- patterns --------------------------------------------------------------------------

    /// <summary>"Quantity   1 each", "Qty: 12 PCS", "Order Quantity 6" — same line only.</summary>
    [GeneratedRegex(
        @"(?im)\b(?:(?:RFQ|Order|Req(?:uired|uested|\.)?|Requested)[ \t]+)?(?:Quantity|Qty)\b\.?[ \t]*[:=]?[ \t]*(?<q>\d{1,3}(?:,\d{3})+(?:\.\d{1,6})?|\d{1,9}(?:[.,]\d{1,6})?)(?![\d.,]*\d)(?:[ \t]*(?<u>[A-Za-z][A-Za-z.]{0,15}))?",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex LabelledQuantity();

    /// <summary>A line that is nothing but a quantity and its unit: "2     Assembley", "1      each".</summary>
    [GeneratedRegex(
        @"(?m)^[ \t]*(?<q>\d{1,3}(?:,\d{3})+(?:\.\d{1,6})?|\d{1,9}(?:[.,]\d{1,6})?)[ \t]+(?<u>[A-Za-z][A-Za-z.]{0,15})[ \t]*$",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex StandaloneQuantityLine();

    /// <summary>A number followed by a unit word anywhere in a line. Weak: confirmation only.</summary>
    [GeneratedRegex(
        @"(?<![\p{L}\p{N}.,/-])(?<q>\d{1,7}(?:[.,]\d{1,6})?)[ \t]*(?<u>[A-Za-z]{1,10})(?![\p{L}\p{N}])",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex InlineQuantity();

    [GeneratedRegex(@"^\s*([A-Za-z]?\d{1,9}[A-Za-z]?)(?=\s|$|[.):])", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex LeadingToken();

    /// <summary>A line printed under its number: "8 BASE, LAMP…", "1 Material". The number is 1–6 digits and a word follows.</summary>
    [GeneratedRegex(@"^\s*(\d{1,6})[ \t]+[A-Za-z(]", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex NumberedLine();

    [GeneratedRegex(@"(?i)\b(?:item|line|pos(?:ition)?)[ \t]*(?:no\.?|number|#)?[ \t]*[:.#-]?[ \t]*(?<n>\d{1,9})\b",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex LabelledItemNumber();

    [GeneratedRegex(
        @"^(?:(?:rfq|order|req(?:uested|uired)?\.?)\s+)?(?:qty|quantity|uom|u/?m|unit|unit of measure|units?)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex QuantityOrUnitKey();

    /// <summary>
    /// The SABIC "RFQ details" print: "Item Number | Item category | Product description | Order
    /// Quantity | Currency | Net Value | Delivery Date | … | Product Number. | UOM | Price per Unit".
    /// Read line by line the order quantity lands in front of the currency code ("… BOARD 6 SAR")
    /// and the product number in front of the unit and the unit price ("978596 EA 1") — which is
    /// where the model's "1" came from.
    /// </summary>
    private static partial class SabicBidLayout
    {
        public static bool Recognises(string? text)
            => !string.IsNullOrEmpty(text)
               && text.Contains("Price per Unit", StringComparison.OrdinalIgnoreCase)
               && OrderQuantityHeading().IsMatch(text)
               && ProductNumberHeading().IsMatch(text)
               && text.Contains("Currency", StringComparison.OrdinalIgnoreCase);

        /// <summary>"1637334 EA 1": the first line that is a product number followed by a unit (not "32233 AL JUBAIL").</summary>
        public static TokenMatch? ProductNumber(string text, int start, int end)
        {
            var m = ProductLineWithUnit(text, start, end);
            if (m is null) return null;
            var g = m.Groups["code"];
            return new TokenMatch(start + g.Index, g.Length, g.Value);
        }

        private static Match? ProductLineWithUnit(string text, int start, int end)
        {
            foreach (Match m in ProductLine().Matches(text[start..end]))
                if (IsKnownUnit(m.Groups["u"].Value)) return m;
            return null;
        }

        /// <summary>"1 Material" / "2 Service": the item number, printed above the item category column.</summary>
        public static TokenMatch? ItemNumber(string text, int start, int end)
        {
            var m = ItemLine().Match(text[start..end]);
            if (!m.Success) return null;
            var g = m.Groups["n"];
            return new TokenMatch(start + g.Index, g.Length, g.Value);
        }

        public static TokenMatch? Unit(string text, int start, int end)
        {
            var m = ProductLineWithUnit(text, start, end);
            if (m is null) return null;
            var g = m.Groups["u"];
            return new TokenMatch(start + g.Index, g.Length, g.Value);
        }

        [GeneratedRegex(@"(?m)^(?:[^\n]*?[ \t])?(?<q>\d{1,9}(?:[.,]\d{1,6})?)[ \t]+(?<cur>[A-Z]{3})[ \t]*$",
            RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
        public static partial Regex QuantityBeforeCurrency();

        [GeneratedRegex(@"(?m)^[ \t]*(?<code>\d{4,18})[ \t]+(?<u>[A-Za-z]{1,6})\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
        private static partial Regex ProductLine();

        [GeneratedRegex(@"(?m)^[ \t]*(?<n>\d{1,6})[ \t]+(?:Material|Service)s?[ \t]*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
        private static partial Regex ItemLine();

        [GeneratedRegex(@"Order\s+Quantity", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
        private static partial Regex OrderQuantityHeading();

        [GeneratedRegex(@"Product\s+Number", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
        private static partial Regex ProductNumberHeading();
    }
}
