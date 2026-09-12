using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ERP_RFQ_Automation.ProductIntelligence.ManufacturerKnowledge;

/// <summary>What the inference concluded and why, in a form the evidence ledger can keep.</summary>
/// <param name="Manufacturer">The maker's display name, as the tenant's own reviewed lines spelled it.</param>
/// <param name="Reason">
/// A stable transformation token — <c>inferred_from_description:&lt;name&gt;</c> or
/// <c>inferred_from_part_number_pattern:&lt;pattern&gt;</c>. It is written into the canonical
/// value's <c>Transformations</c> and therefore into <c>field_evidence</c>, which is how the
/// learner later tells an inferred manufacturer from a stated one and refuses to learn from it.
/// The <c>inferred_from</c> prefix is the contract; do not rename it without the learner.
/// </param>
/// <param name="Confidence">0.85 when the maker's name was in the text; 0.80 when only its numbering shape was.</param>
public sealed record ManufacturerInferenceResult(string Manufacturer, string Reason, decimal Confidence);

/// <summary>
/// Fills in a manufacturer the buyer left out, from evidence the tenant already has — and only
/// from that. Pure and synchronous: every input is handed in, so the rules can be tested without
/// a database and the callers (structured and unstructured extraction) apply exactly the same
/// judgement.
///
/// <para><b>The two sources, in order.</b> First the line's own text: if a maker the tenant
/// already knows is written in the description ("Siemens 3RT2015 contactor"), the document said
/// it and merely put it in the wrong column. Second the part number's shape: if reviewed leads
/// have repeatedly paired a prefix with one maker, a bare "X7-M5" is that maker's. The text wins
/// because it is the document's own statement; the pattern is an inference from history.</para>
///
/// <para><b>What it refuses.</b> Two different known makers in one description, or one pattern
/// that the tenant's history attributes to two makers, produce <c>null</c>, not a guess — and an
/// ambiguous longer pattern is NOT rescued by a shorter, vaguer one, because the shorter prefix is
/// by construction less specific than the evidence that already disagreed. A single observation
/// never asserts anything: one reviewer's one line is an anecdote until a second line agrees. And
/// a line that states its manufacturer is never touched, whatever the history says — the document
/// outranks the platform's memory of other documents.</para>
/// </summary>
public static partial class ManufacturerInference
{
    public const decimal DescriptionConfidence = 0.85m;
    public const decimal PatternConfidence = 0.80m;

    /// <summary>The prefix every reason token starts with; the learner's contract.</summary>
    public const string ReasonPrefix = "inferred_from";
    public const string DescriptionReasonPrefix = "inferred_from_description:";
    public const string PatternReasonPrefix = "inferred_from_part_number_pattern:";

    /// <summary>
    /// Observations a pattern needs before it is believed. Two, not one: a single reviewed line is
    /// how one typo becomes an authoritative maker for every future document with that prefix.
    /// </summary>
    public const int MinimumObservations = 2;

    /// <summary>Longest pattern the store keeps; anything longer is a description, not a number.</summary>
    public const int MaxPatternLength = 64;

    private const int MinimumCandidateLength = 2;

    /// <summary>Separators that delimit the FIRST segment of a part number.</summary>
    private static readonly char[] SegmentSeparators = ['-', '/', '.', ' ', '_'];

    /// <summary>
    /// The prefixes of a part number worth remembering, longest first, all separator-free.
    ///
    /// <para><b>The rule.</b> The number is first put through
    /// <see cref="ProductIdentityNormalizer.NormalizePartNumber"/> (upper-case, compatibility-
    /// normalised, punctuation collapsed). Three candidates are then derived and de-duplicated:
    /// <list type="number">
    /// <item>the whole number with every separator removed
    /// (<see cref="ProductIdentityNormalizer.FoldIdentifier"/>) — "3RT2015-1BB41" → "3RT20151BB41";</item>
    /// <item>the first separator-delimited segment (separators: hyphen, slash, dot, space,
    /// underscore) — "3RT2015";</item>
    /// <item>the maker's FAMILY prefix: the first segment cut where its first run of two or more
    /// consecutive digits begins — "3RT2015" → "3RT", "LC1D09BD" → "LC1D",
    /// "1SDA054523R1" → "1SDA".</item>
    /// </list>
    /// Separators never appear in a candidate, so "A2A-50006470", "A2A 50006470" and
    /// "A2A50006470" learn and match as one number; the display value is untouched because the
    /// candidate is a KEY, never something written back to a line.</para>
    ///
    /// <para><b>Why "first run of two or more digits" and not "first digit".</b> Makers embed a
    /// single digit inside the family code — Schneider's LC1D, ABB's 1SDA — and cutting at the
    /// first digit would reduce every Schneider contactor to "LC" and every ABB breaker to "".
    /// The multi-digit run is where the family ends and the rating or series begins on every
    /// numbering scheme in the pilot corpus.</para>
    ///
    /// <para><b>What is dropped.</b> Candidates shorter than two characters, and candidates that
    /// are purely numeric: a numeric prefix such as "60" identifies a quantity, a size, or nothing
    /// at all, and learning it would attribute every "60xxxx" line on every future document to
    /// whoever happened to be reviewed first. Anything longer than <see cref="MaxPatternLength"/>
    /// is not a part number.</para>
    /// </summary>
    public static IReadOnlyList<string> PatternCandidates(string? partNumber)
    {
        var normalized = ProductIdentityNormalizer.NormalizePartNumber(partNumber);
        if (normalized is null) return [];

        var candidates = new List<string>(3);

        var full = ProductIdentityNormalizer.FoldIdentifier(normalized);
        Add(full);

        var firstSegment = normalized.Split(SegmentSeparators, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        var segment = ProductIdentityNormalizer.FoldIdentifier(firstSegment);
        Add(segment);

        if (segment is not null)
        {
            var family = FamilyPrefix().Match(segment);
            if (family.Success) Add(family.Groups[1].Value);
        }

        return candidates;

        void Add(string? candidate)
        {
            if (candidate is null) return;
            if (candidate.Length < MinimumCandidateLength || candidate.Length > MaxPatternLength) return;
            if (candidate.All(char.IsDigit)) return;
            if (candidates.Contains(candidate, StringComparer.Ordinal)) return;
            candidates.Add(candidate);
        }
    }

    /// <summary>
    /// Everything up to, but not including, the first run of two or more digits. The lazy
    /// quantifier and the lookahead make the shortest such prefix win; a segment with no such
    /// run does not match at all, which leaves the segment itself as the shortest candidate.
    /// </summary>
    [GeneratedRegex(@"^(.+?)(?=\d{2})", RegexOptions.CultureInvariant)]
    private static partial Regex FamilyPrefix();

    /// <summary>
    /// The patterns a reviewed line with BOTH a stated manufacturer and a stated part number
    /// teaches; empty when either is blank. Same candidate rule as
    /// <see cref="PatternCandidates"/>, so what is learned is exactly what is later looked up.
    /// </summary>
    public static IReadOnlyList<string> ExtractStatedPairs(string? statedManufacturer, string? partNumber)
    {
        if (ProductIdentityNormalizer.NormalizeManufacturer(statedManufacturer) is null) return [];
        return PatternCandidates(partNumber);
    }

    /// <summary>
    /// Infers the maker of one line, or returns <c>null</c> when the evidence does not support
    /// exactly one answer.
    /// </summary>
    /// <param name="statedManufacturer">
    /// What the line itself says. Anything non-blank here short-circuits to <c>null</c>: the
    /// document's own statement is never overridden by history, however strong.
    /// </param>
    /// <param name="description">The line's free text — description, product name, item note — joined by the caller.</param>
    /// <param name="partNumber">The line's stated part number, if any.</param>
    /// <param name="tenantPatterns">This tenant's learned rows; rows of other tenants must never be passed.</param>
    /// <param name="knownManufacturers">
    /// The distinct display names this tenant's history knows. Only a name the tenant has
    /// already reviewed can be recognised in a description — a free-text scan for "any word that
    /// looks like a brand" is how "Standard" and "General" become manufacturers.
    /// </param>
    public static ManufacturerInferenceResult? Infer(
        string? statedManufacturer,
        string? description,
        string? partNumber,
        IReadOnlyList<ManufacturerPartPattern> tenantPatterns,
        IReadOnlySet<string> knownManufacturers)
    {
        ArgumentNullException.ThrowIfNull(tenantPatterns);
        ArgumentNullException.ThrowIfNull(knownManufacturers);

        if (!string.IsNullOrWhiteSpace(statedManufacturer)) return null;

        return InferFromDescription(description, knownManufacturers)
               ?? InferFromPattern(partNumber, tenantPatterns);
    }

    /// <summary>
    /// A known maker's name written inside the description, matched as a whole word or word
    /// sequence after folding case, accents and punctuation on both sides — so "Schneider
    /// Électric" finds "Schneider Electric", and "Siemens" does not fire on "Siemensstrasse".
    ///
    /// <para>When several known names match, the longest wins IF every other match is contained
    /// in it ("Schneider" inside "Schneider Electric" is the same maker written twice in the
    /// tenant's history). Two matches that do not contain each other are two makers, and a line
    /// naming two makers — "Siemens replacement for ABB 1SDA…" — is exactly the line a machine
    /// must not decide.</para>
    /// </summary>
    private static ManufacturerInferenceResult? InferFromDescription(
        string? description, IReadOnlySet<string> knownManufacturers)
    {
        var haystack = FoldText(description);
        if (haystack.Length == 0 || knownManufacturers.Count == 0) return null;

        var padded = " " + haystack + " ";
        var matches = new List<(string Display, string Folded)>();
        foreach (var name in knownManufacturers)
        {
            var folded = FoldText(name);
            if (folded.Length < MinimumCandidateLength) continue;
            if (!padded.Contains(" " + folded + " ", StringComparison.Ordinal)) continue;
            if (matches.Any(m => m.Folded == folded)) continue;
            matches.Add((name, folded));
        }

        if (matches.Count == 0) return null;

        var longest = matches.OrderByDescending(m => m.Folded.Length).ThenBy(m => m.Display, StringComparer.Ordinal).First();
        var ambiguous = matches.Any(m =>
            m.Folded != longest.Folded
            && !(" " + longest.Folded + " ").Contains(" " + m.Folded + " ", StringComparison.Ordinal));
        if (ambiguous) return null;

        return new ManufacturerInferenceResult(
            longest.Display.Trim(), DescriptionReasonPrefix + longest.Display.Trim(), DescriptionConfidence);
    }

    /// <summary>
    /// The part number's shape against the tenant's learned prefixes, most specific first.
    /// The first candidate with any rows decides: agreement with enough observations answers,
    /// disagreement refuses, and too few observations falls through to the next shorter prefix
    /// (insufficient evidence is not conflicting evidence).
    /// </summary>
    private static ManufacturerInferenceResult? InferFromPattern(
        string? partNumber, IReadOnlyList<ManufacturerPartPattern> tenantPatterns)
    {
        if (tenantPatterns.Count == 0) return null;

        foreach (var candidate in PatternCandidates(partNumber))
        {
            var rows = tenantPatterns.Where(p => string.Equals(p.Pattern, candidate, StringComparison.Ordinal)).ToList();
            if (rows.Count == 0) continue;

            var makers = rows.Select(r => r.NormalizedManufacturer).Distinct(StringComparer.Ordinal).ToList();
            if (makers.Count > 1) return null;

            if (rows.Sum(r => r.ObservationCount) < MinimumObservations) continue;

            var display = rows.OrderByDescending(r => r.ObservationCount).ThenBy(r => r.Id).First().Manufacturer.Trim();
            return new ManufacturerInferenceResult(display, PatternReasonPrefix + candidate, PatternConfidence);
        }

        return null;
    }

    /// <summary>
    /// Upper-case, accent-stripped, every run of punctuation or whitespace collapsed to one
    /// space. "Schneider-Électric" and "SCHNEIDER ELECTRIC" fold to the same string; used on
    /// both the haystack and the needle so the comparison is symmetric.
    /// </summary>
    internal static string FoldText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var decomposed = value.Normalize(NormalizationForm.FormKD).ToUpperInvariant();
        var builder = new StringBuilder(decomposed.Length);
        var pendingSpace = false;
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(character))
            {
                if (pendingSpace && builder.Length > 0) builder.Append(' ');
                builder.Append(character);
                pendingSpace = false;
                continue;
            }
            pendingSpace = true;
        }
        return builder.ToString();
    }
}
