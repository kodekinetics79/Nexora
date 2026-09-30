using System.Text.RegularExpressions;
using ERP_RFQ_Automation.Extraction.Templates;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services.Uom;

namespace ERP_RFQ_Automation.LeadIdentity;

/// <summary>
/// Stops a POORER READING of the same RFQ from silently replacing a better one.
///
/// <para><b>What happened.</b> Aramco's RFP 6000000003 was uploaded as .docx (870 makers, 949 part
/// numbers, 640 approved-maker lists on 1,514 lines) and then as .doc, the HTML print of the same
/// event. The .doc reader found no makers at all. Its lines hashed differently (the part number
/// is part of a line's identity), so the upload became revision 2 "as if the buyer had changed the
/// RFP", and the lead's current lines went to 1 maker / 0 part numbers / 0 lists. Nothing said so.</para>
///
/// <para><b>The rule.</b> Incoming lines are paired with the current revision's lines on everything
/// EXCEPT the maker facts (line number, buyer material code, description, quantity, unit). A copy
/// is poorer when every one of its lines pairs with a current line, it states no maker fact the
/// current revision lacks or states differently, and it either lost maker facts or lost lines.
/// Then:</para>
/// <list type="bullet">
/// <item>same lines, same header terms, only maker facts lost → it adds nothing: kept as a
/// duplicate of the current revision, with the reason;</item>
/// <item>lines missing → a person decides (possible match), because a buyer may really have
/// dropped lines and a reader may really have missed them, and only a person can tell;</item>
/// <item>anything else is a genuine revision, and on lines that are otherwise identical the maker
/// facts the copy did not state are carried forward from the current revision, with the reason.</item>
/// </list>
/// </summary>
internal static class PoorerCopyGuard
{
    /// <summary>The extra fields that carry maker facts, alongside the manufacturer and part-number columns.</summary>
    internal static readonly string[] MakerFactFields =
    {
        ManufacturingPartText.ApprovedManufacturersField, "Manufacturer part numbers", "Superseded part numbers",
    };

    internal sealed record Assessment(
        int CurrentLines,
        int IncomingLines,
        bool EveryIncomingLinePaired,
        bool AnyMakerFactGainedOrChanged,
        int LinesThatLostMakerFacts,
        IReadOnlyList<(LeadItem Incoming, LeadItem Current)> CarryCandidates)
    {
        /// <summary>The copy states nothing the current revision lacks, and lost something.</summary>
        public bool IsPoorer => EveryIncomingLinePaired && !AnyMakerFactGainedOrChanged
                                && (LinesThatLostMakerFacts > 0 || IncomingLines < CurrentLines);

        public bool LostLinesOnly => IsPoorer && IncomingLines < CurrentLines;
    }

    internal static Assessment Assess(IReadOnlyCollection<LeadItem> current, IReadOnlyCollection<LeadItem> incoming)
    {
        var pool = current
            .GroupBy(LineKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => new Queue<LeadItem>(g), StringComparer.Ordinal);

        var allPaired = incoming.Count > 0;
        var anyChanged = false;
        var lostLines = 0;
        var carry = new List<(LeadItem, LeadItem)>();
        foreach (var line in incoming)
        {
            if (!pool.TryGetValue(LineKey(line), out var queue) || queue.Count == 0)
            {
                allPaired = false;
                continue;
            }
            var match = queue.Dequeue();
            var (lost, changed) = Compare(Facts(match), Facts(line));
            if (changed) anyChanged = true;
            else if (lost)
            {
                lostLines++;
                carry.Add((line, match));
            }
        }

        return new Assessment(current.Count, incoming.Count, allPaired, anyChanged, lostLines, carry);
    }

    /// <summary>
    /// Copies the maker facts the incoming line did not state from the current line it pairs
    /// with. Only lines that lost facts and changed none are ever candidates.
    /// </summary>
    internal static int CarryForward(Assessment assessment)
    {
        foreach (var (incoming, current) in assessment.CarryCandidates)
        {
            if (string.IsNullOrWhiteSpace(incoming.ManufacturerName)) incoming.ManufacturerName = current.ManufacturerName;
            if (string.IsNullOrWhiteSpace(incoming.ManufacturerPartNumber)) incoming.ManufacturerPartNumber = current.ManufacturerPartNumber;

            var currentExtras = ExtraFieldsJson.Deserialize(current.ExtraFields);
            if (currentExtras is null) continue;
            var incomingExtras = ExtraFieldsJson.Deserialize(incoming.ExtraFields) ?? new Dictionary<string, string>();
            var merged = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var key in MakerFactFields)
            {
                if (incomingExtras.TryGetValue(key, out var own) && !string.IsNullOrWhiteSpace(own)) merged[key] = own;
                else if (currentExtras.TryGetValue(key, out var kept) && !string.IsNullOrWhiteSpace(kept)) merged[key] = kept;
            }
            foreach (var (key, value) in incomingExtras) merged.TryAdd(key, value);
            incoming.ExtraFields = ExtraFieldsJson.Serialize(merged);
        }
        return assessment.CarryCandidates.Count;
    }

    private sealed record MakerFacts(string? Maker, string? PartNumber, IReadOnlyDictionary<string, string?> Extras);

    private static MakerFacts Facts(LeadItem item)
    {
        var extras = ExtraFieldsJson.Deserialize(item.ExtraFields);
        return new MakerFacts(Normalize(item.ManufacturerName), Normalize(item.ManufacturerPartNumber),
            MakerFactFields.ToDictionary(key => key,
                key => extras is not null && extras.TryGetValue(key, out var value) ? Normalize(value) : null,
                StringComparer.Ordinal));
    }

    /// <summary>Lost: the current line states it and the incoming one does not. Changed: the incoming one states something else.</summary>
    private static (bool Lost, bool Changed) Compare(MakerFacts current, MakerFacts incoming)
    {
        var lost = false;
        var changed = false;
        Check(current.Maker, incoming.Maker);
        Check(current.PartNumber, incoming.PartNumber);
        foreach (var key in MakerFactFields) Check(current.Extras[key], incoming.Extras[key]);
        return (lost, changed);

        void Check(string? was, string? now)
        {
            if (now is null) { if (was is not null) lost = true; }
            else if (!string.Equals(was, now, StringComparison.Ordinal)) changed = true;
        }
    }

    /// <summary>A line's identity WITHOUT its maker facts.</summary>
    private static string LineKey(LeadItem item) => string.Join("|",
        Normalize(item.LineItemNo), Normalize(item.ItemMaterialCode),
        Normalize(item.ProductShortDescription ?? item.ItemText ?? item.ProductShortName),
        item.Quantity?.ToString("0.############", System.Globalization.CultureInfo.InvariantCulture),
        Normalize(UomCanonicalizer.EquivalenceKey(item.UnitOfMeasure)));

    private static readonly Regex NonWord = new("[^a-z0-9]+", RegexOptions.Compiled);

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var folded = NonWord.Replace(value.Trim().ToLowerInvariant(), "");
        return folded.Length == 0 ? null : folded;
    }
}
