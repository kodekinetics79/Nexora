using System;
using System.Text.RegularExpressions;

namespace ERP_RFQ_Automation.Extraction;

/// <summary>
/// The one question a rep must answer when a document's closing date could be read two ways and
/// nothing on the document said which: "Closes 8 Sep or 9 Aug?".
/// </summary>
/// <param name="DocumentText">The date exactly as the document printed it ("9/8/2026 5:00 PM").</param>
/// <param name="CurrentReading">What the lead holds now — the reading Nexora guessed.</param>
/// <param name="OtherReading">The same token read the other way round, same time of day.</param>
public sealed record ClosingDateQuestionDTO(string DocumentText, DateTime CurrentReading, DateTime OtherReading);

/// <summary>
/// Reads the question back from the note extraction leaves on the lead, and removes it once a
/// person has answered.
///
/// <para><b>Why the note and not a new column.</b> The normaliser already records the guess in
/// the lead's header remarks, in one fixed sentence ("\"9/8/2026\" is ambiguous — both parts of
/// the bid closing date are 12 or lower … confirm it."), and the rep never saw it: the sentence
/// reached only the Leads Excel "Remarks" column. The sentence carries the token, the lead
/// carries the reading, so the question needs no schema; answering it removes the sentence,
/// which is how the question stays answered.</para>
/// </summary>
public static class ClosingDateQuestion
{
    // The sentence CanonicalRfqNormalizer.AmbiguityMessage writes. Bounded, no nested quantifiers.
    private static readonly Regex Sentence = new(
        "\"(?<raw>[^\"]{1,80})\" is ambiguous — both parts of the (?<field>bid closing date|received date|requested delivery date) "
        + "are 12 or lower, so it could be either day/month or month/day\\. It has been read [^;\"]{1,160}; confirm it\\.",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The question for this lead, or null when there is none: no guessed closing date in the
    /// note, or the closing date has since been set to something that is neither reading.
    /// </summary>
    public static ClosingDateQuestionDTO? From(string? headerRemarks, DateTime? bidClosingDate)
    {
        if (string.IsNullOrWhiteSpace(headerRemarks) || bidClosingDate is not { } current) return null;

        foreach (Match match in Sentence.Matches(headerRemarks))
        {
            if (match.Groups["field"].Value != "bid closing date") continue;
            var raw = match.Groups["raw"].Value.Trim();
            var reading = RfqDateParser.Read(raw);
            if (reading.Value is not { } dayFirst || RfqDateParser.SwapDayAndMonth(dayFirst) is not { } monthFirst)
                return null;

            if (current.Date == dayFirst.Date)
                return new ClosingDateQuestionDTO(raw, current, monthFirst.Date + current.TimeOfDay);
            if (current.Date == monthFirst.Date)
                return new ClosingDateQuestionDTO(raw, current, dayFirst.Date + current.TimeOfDay);
            return null;
        }
        return null;
    }

    /// <summary>
    /// The received date read in the same order as the answered closing date, when the note says
    /// the received date was ambiguous too; otherwise null (nothing to realign).
    /// </summary>
    public static DateTime? ReceivedDateInOrder(string? headerRemarks, DateTime receivedDate, bool monthFirst)
    {
        if (string.IsNullOrWhiteSpace(headerRemarks)) return null;
        foreach (Match match in Sentence.Matches(headerRemarks))
        {
            if (match.Groups["field"].Value != "received date") continue;
            var reading = RfqDateParser.Read(match.Groups["raw"].Value.Trim());
            if (reading.Value is not { } dayFirst) return null;
            var wanted = monthFirst ? RfqDateParser.SwapDayAndMonth(dayFirst) : dayFirst;
            if (wanted is not { } value) return null;
            // Only a received date still holding one of the two readings is realigned; a date a
            // person has already corrected is theirs.
            var other = RfqDateParser.SwapDayAndMonth(dayFirst);
            return receivedDate.Date == dayFirst.Date || (other.HasValue && receivedDate.Date == other.Value.Date)
                ? value
                : null;
        }
        return null;
    }

    /// <summary>The note without its date questions — what an answer leaves behind.</summary>
    public static string? RemoveDateQuestions(string? headerRemarks)
    {
        if (string.IsNullOrWhiteSpace(headerRemarks)) return headerRemarks;
        var cleaned = Sentence.Replace(headerRemarks, string.Empty);
        cleaned = Regex.Replace(cleaned, @"[ \t]{2,}", " ").Trim();
        return cleaned.Length == 0 || cleaned.Equals("[NEEDS REVIEW]", StringComparison.OrdinalIgnoreCase)
            ? null
            : cleaned;
    }

    /// <summary>True when the note still asks about the closing date.</summary>
    public static bool AsksAboutClosingDate(string? headerRemarks)
        => !string.IsNullOrWhiteSpace(headerRemarks)
           && Sentence.IsMatch(headerRemarks)
           && headerRemarks.Contains("of the bid closing date", StringComparison.Ordinal);
}
