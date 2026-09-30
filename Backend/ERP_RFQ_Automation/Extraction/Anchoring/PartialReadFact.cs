using System.Globalization;
using System.Text.RegularExpressions;

namespace ERP_RFQ_Automation.Extraction.Anchoring;

/// <summary>
/// "Nexora read only part of this document." One fact, written in one sentence and read back
/// from it.
///
/// <para>A 42-line Aramco print came back as 3 clean-looking lines: 47 of 48 model calls were cut
/// off at the output ceiling, the job reported Succeeded, and the only trace was a free-text
/// "[NEEDS REVIEW] 28 chunk(s) failed" in a remark Decide never showed. A rep would have quoted 3
/// of 42 lines. The sentence below travels as the extraction's review reason — through e-mail
/// assembly, which joins review reasons with "; " — and is persisted as a PARTIAL_READ finding on
/// the extraction run, from where the decision screen says it in one line.</para>
/// </summary>
public sealed partial record PartialReadFact(int LinesRead, int? LinesExpected, int PartsUnread)
{
    /// <summary>The ledger code the fact is stored under.</summary>
    public const string FindingCode = "PARTIAL_READ";

    /// <summary>The sentence. Machine-readable by <see cref="TryParse"/>, and plain enough for a person.</summary>
    public string Describe()
    {
        var read = LinesExpected is { } expected && expected > LinesRead
            ? $"Read {LinesRead} of about {expected} line{(expected == 1 ? "" : "s")}"
            : $"Read {LinesRead} line{(LinesRead == 1 ? "" : "s")}";
        var unread = PartsUnread > 0
            ? $"; {PartsUnread} part(s) of the document could not be read"
            : "; some lines the document prints were not returned";
        return $"{read}{unread}. Check the document before quoting.";
    }

    /// <summary>Finds the fact inside a (possibly joined) review reason.</summary>
    public static bool TryParse(string? text, out PartialReadFact fact)
    {
        fact = new PartialReadFact(0, null, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        var match = Sentence().Match(text);
        if (!match.Success) return false;
        var read = int.Parse(match.Groups["read"].Value, CultureInfo.InvariantCulture);
        int? expected = match.Groups["expected"].Success
            ? int.Parse(match.Groups["expected"].Value, CultureInfo.InvariantCulture)
            : null;
        var parts = match.Groups["parts"].Success
            ? int.Parse(match.Groups["parts"].Value, CultureInfo.InvariantCulture)
            : 0;
        fact = new PartialReadFact(read, expected, parts);
        return true;
    }

    [GeneratedRegex(
        @"Read (?<read>\d{1,7})(?: of about (?<expected>\d{1,7}))? lines?; (?:(?<parts>\d{1,7}) part\(s\) of the document could not be read|some lines the document prints were not returned)\.",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex Sentence();
}
