using System.Text.RegularExpressions;

namespace ERP_RFQ_Automation.Extraction.Templates;

/// <summary>
/// Reads an SAP "Material PO text" — the long text a buyer's material master carries and prints
/// on every request line. SEC's prints all have the one shape:
///
/// <code>
/// ;Material PO text:
/// GUARD,BULK NOUN:                       ← the noun and its attributes: THE SPECIFICATION
/// SIZE: 272 MM WD X 215 MM LG; MATERIAL: STL;
/// ADDITIONAL DATA:
/// GENERAL ELECTRIC (GE) (LM/FM):         ← the maker the buyer references, then their number
/// P/N#221B4034G005
/// * FOR THIS ITEM YOU ARE REQUIRED TO AFFIX SEC SPECIFIED BARCODE …   ← standing instruction
/// </code>
///
/// <para>Three different things live in one cell, and a quote needs them apart: the
/// specification is what to quote against, the maker and part number are WHOSE part (the
/// buyer's own material number is not a part number), and the barcode boilerplate is a
/// packing condition that is the same on every line. Nothing here is inferred: a maker is
/// read only from the "NAME: / P/N#" pairs the text states, and a placeholder maker
/// ("REFERENCE ONLY/TEMPORARY/UNKNOWN") is discarded.</para>
/// </summary>
public static partial class SapMaterialPoText
{
    public sealed record Maker(string Name, string? PartNumber, string? Model);

    /// <param name="Specification">The technical text, boilerplate removed; empty when the cell was only boilerplate.</param>
    /// <param name="Makers">Makers named with a part or model number, document order, placeholders removed.</param>
    /// <param name="Instructions">The buyer's standing instruction (labelling, packing), verbatim; null when none.</param>
    public sealed record Reading(string Specification, IReadOnlyList<Maker> Makers, string? Instructions)
    {
        public bool IsEmpty => Specification.Length == 0 && Makers.Count == 0 && Instructions is null;
    }

    private static readonly string[] InstructionOpeners =
    [
        "* FOR THIS ITEM", "*FOR THIS ITEM", "EXTERNAL MEMO TEXT", "REFERANCE DOCUMENT", "REFERENCE DOCUMENT",
        "OTHER MATERIALS:-", "PLEASE VISIT", "SUPPLIER IS REQUIRED TO PRINT", "* THE BARCODE", "*THE BARCODE",
    ];

    private static readonly string[] PlaceholderMakers =
    [
        "REFERENCE ONLY", "TEMPORARY", "UNKNOWN", "REFERENCE ONLY/TEMPORARY/UNKNOWN",
    ];

    [GeneratedRegex(@"^\s*;?\s*Material\s+PO\s+text\s*:\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Preamble();

    /// <summary>"GENERAL ELECTRIC (GE) (LM/FM):" — a maker line: a name, optional SAP role markers, a colon, nothing else.</summary>
    [GeneratedRegex(@"^(?<name>[A-Za-z][A-Za-z0-9&.,'/ \-]{1,80}?)(?:\s*\((?:GE|LM/FM|FM|LM|OEM)\))*\s*:\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex MakerLine();

    /// <summary>"P/N#221B4034G005", "P/N# 112-2093", "Model#VEGADIS81(AXIKIMACX)".</summary>
    [GeneratedRegex(@"^\s*(?<kind>P/N|PN|PART\s*NO\.?|MODEL)\s*#?\s*:?\s*(?<value>\S(?:.*\S)?)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NumberLine();

    /// <summary>"… DRAWING NO : 141A5547 GENERAL ELECTRIC (GE) (LM/FM) :" — a maker at the END of a prose line.</summary>
    [GeneratedRegex(@"(?<=^|\s)(?<name>[A-Z]{2,}[A-Z0-9&.,'/ \-]{0,60}?)(?:\s*\((?:GE|LM/FM|FM|LM|OEM)\))+\s*:\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex MakerAtLineEnd();

    /// <summary>"LBE830P-1" straight under "ALCAD BATTERIES:" — a bare number with no P/N# prefix: one token, carrying a digit.</summary>
    [GeneratedRegex(@"^[A-Z0-9][A-Z0-9\-./#()]{1,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex BareNumber();

    public static bool Recognises(string? text)
        => !string.IsNullOrWhiteSpace(text)
           && (Preamble().IsMatch(text) || text.Contains("P/N#", StringComparison.OrdinalIgnoreCase)
               || text.Contains("ADDITIONAL DATA", StringComparison.OrdinalIgnoreCase));

    public static Reading Read(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new Reading(string.Empty, [], null);

        var body = Preamble().Replace(text.Replace("\r\n", "\n").Replace('\r', '\n'), string.Empty);
        var lines = body.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

        // The standing instruction starts at the first boilerplate opener and runs to the end.
        var cut = lines.FindIndex(l => InstructionOpeners.Any(o => l.StartsWith(o, StringComparison.OrdinalIgnoreCase)));
        var technical = cut < 0 ? lines : lines.Take(cut).ToList();
        var instructions = cut < 0 ? null : string.Join('\n', lines.Skip(cut));

        var makers = new List<Maker>();
        string? pendingMaker = null;
        string? heldLine = null;          // the line that MAY be a maker: it is one only if a number follows
        var specLines = new List<string>();
        foreach (var line in technical)
        {
            var number = NumberLine().Match(line);
            // Some prints drop the "P/N#" and put the bare number under the maker's name.
            var bare = !number.Success && pendingMaker is not null && heldLine is not null
                       && BareNumber().IsMatch(line) && line.Any(char.IsDigit);
            if ((number.Success || bare) && pendingMaker is not null)
            {
                heldLine = null;
                if (bare)
                {
                    var existingBare = makers.FindIndex(m => string.Equals(m.Name, pendingMaker, StringComparison.OrdinalIgnoreCase));
                    if (existingBare >= 0) makers[existingBare] = makers[existingBare] with { PartNumber = makers[existingBare].PartNumber ?? line };
                    else makers.Add(new Maker(pendingMaker, line, null));
                    continue;
                }
                var isModel = number.Groups["kind"].Value.StartsWith("MODEL", StringComparison.OrdinalIgnoreCase);
                var value = number.Groups["value"].Value.Trim();
                var existing = makers.FindIndex(m => string.Equals(m.Name, pendingMaker, StringComparison.OrdinalIgnoreCase));
                if (existing >= 0)
                    makers[existing] = isModel
                        ? makers[existing] with { Model = makers[existing].Model ?? value }
                        : makers[existing] with { PartNumber = makers[existing].PartNumber ?? value };
                else
                    makers.Add(new Maker(pendingMaker, isModel ? null : value, isModel ? value : null));
                continue;
            }

            // A line that was held as a possible maker but was not followed by a number is
            // specification after all ("GUARD,BULK NOUN:" is a noun, not a company).
            if (heldLine is not null) { specLines.Add(heldLine); heldLine = null; }

            var maker = MakerLine().Match(line);
            if (maker.Success && LooksLikeMakerName(maker.Groups["name"].Value))
            {
                pendingMaker = CleanMakerName(maker.Groups["name"].Value);
                heldLine = line;
                continue;
            }

            // A maker can close a prose line: "DRAWING NO : 141A5547 GENERAL ELECTRIC (GE) (LM/FM) :"
            var tail = MakerAtLineEnd().Match(line);
            if (tail.Success && LooksLikeMakerName(tail.Groups["name"].Value))
            {
                pendingMaker = CleanMakerName(tail.Groups["name"].Value);
                var prose = line[..tail.Index].Trim();
                if (prose.Length > 0) specLines.Add(prose);
                heldLine = null;
                continue;
            }

            pendingMaker = null;
            specLines.Add(line);
        }
        if (heldLine is not null) specLines.Add(heldLine);

        makers.RemoveAll(m => PlaceholderMakers.Any(p => m.Name.Contains(p, StringComparison.OrdinalIgnoreCase)));
        return new Reading(string.Join('\n', specLines).Trim(), makers, instructions);
    }

    /// <summary>An attribute label ("SIZE:", "STANDARD/SPECIFICATION:", "ADDITIONAL DATA:") is not a maker.</summary>
    private static bool LooksLikeMakerName(string candidate)
    {
        var name = candidate.Trim();
        if (name.Length < 3) return false;
        var upper = name.ToUpperInvariant();
        return !AttributeLabels.Contains(upper) && upper.Any(char.IsLetter);
    }

    private static readonly HashSet<string> AttributeLabels = new(StringComparer.Ordinal)
    {
        "SIZE", "MATERIAL", "TYPE", "RATING", "DESIGN", "FUNCTION", "MOUNT", "ACTION", "OPERATED", "SERVICE",
        "CAPACITY", "POWER", "READOUT", "CONNECTION", "VOLTAGE", "PRESSURE RATING", "ELECTRICAL RATING",
        "STANDARD/SPECIFICATION", "STANDARD", "SPECIFICATION", "ADDITIONAL DATA", "PARENT EQUIPMENT/FUNCTION",
        "INTERNAL MATERIAL/S", "INFORMATION PRESENTATION", "INPUT PRESSURE", "OUTPUT PRESSURE", "HOUSING",
        "SIZE/CONNECTION", "ITEM ADDITIONAL DESCRIPTION", "SYSTEM PARENT EQUIPMENT INFORMATION", "MESH SIZE",
        "WIRE SIZE", "AMP HOURS", "GROUP SIZE", "EQUIPMENT DEVICE/EQUIPMENT APLICATION", "BULK NOUN", "DC",
    };

    private static string CleanMakerName(string name)
    {
        var cleaned = Regex.Replace(name.Trim(), @"\s*\((?:GE|LM/FM|FM|LM|OEM)\)", string.Empty).Trim().TrimEnd(':').Trim();
        return cleaned;
    }
}
