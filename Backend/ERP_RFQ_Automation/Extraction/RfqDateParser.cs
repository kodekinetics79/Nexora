using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ERP_RFQ_Automation.Extraction;

/// <summary>
/// How one date token was read, including what could not be established with certainty.
/// </summary>
/// <param name="Value">Normalized value, or null when the token is not a usable date.</param>
/// <param name="RawToken">The caller's input, trimmed — kept so a reviewer can check the reading.</param>
/// <param name="HasExplicitTime">The source stated a time of day, not just a date.</param>
/// <param name="IsDayMonthAmbiguous">
/// The source used a numeric day/month form in which both components are 12 or lower, so
/// "03/04/2026" could be 3 April or 4 March. A day-first reading is returned because that is the
/// prevailing convention in the Gulf, but the ambiguity is reported rather than hidden.
/// </param>
public readonly record struct RfqDateReading(
    DateTime? Value,
    string? RawToken,
    bool HasExplicitTime,
    bool IsDayMonthAmbiguous)
{
    public static readonly RfqDateReading None = new(null, null, false, false);
    public bool HasValue => Value.HasValue;
}

/// <summary>
/// The one place an RFQ date is turned into a <see cref="DateTime"/>.
///
/// <para><b>Why this exists.</b> Every ingestion door — email, manual upload, watched folder,
/// spreadsheet uploader, the extraction worker — previously carried its own private copy of a
/// date parser, and the copies disagreed. One accepted "15 Mar 2026" and the others returned
/// null for it; one accepted <c>yyyy/MM/dd</c> and another did not; two applied a sentinel-year
/// guard and three did not. The same tender therefore produced a different closing date, or no
/// closing date at all, depending on which door it arrived through. For a bid business a missed
/// closing date is a lost bid, so the parser is now shared and the behaviour is one behaviour.</para>
///
/// <para><b>What changed for existing inputs: nothing.</b> Every format the previous parsers
/// accepted is still accepted, and day-first ordering is preserved. The additions are formats
/// that previously produced <c>null</c> — times of day, ISO 8601, spelled months, and
/// Arabic-Indic digits — plus a uniform sanity guard.</para>
///
/// <para><b>Times of day.</b> A tender closing "2026-09-01 14:00" previously failed to parse
/// outright, because no accepted format carried a time: the deadline was lost, not merely
/// truncated. Times are now read, and a token whose time portion is unreadable degrades to the
/// date rather than to nothing.</para>
///
/// <para><b>Time zone is deliberately not applied here.</b> Values are returned with
/// <see cref="DateTimeKind.Unspecified"/>, exactly as the previous parsers did, so persistence
/// semantics are unchanged. Whether a stated closing time is Arabia Standard Time and how it
/// should be stored is an open product decision, not something this parser should assume.</para>
/// </summary>
public static class RfqDateParser
{
    /// <summary>Below this year a value is OCR noise or a sentinel (0001-01-01), not a date.</summary>
    private const int MinimumPlausibleYear = 2000;

    /// <summary>Above this year a value is digit noise rather than a deadline.</summary>
    private const int MaximumPlausibleYear = 2100;

    // Ordered deliberately. Day-first precedes month-first so an ambiguous numeric token reads
    // the way Gulf correspondence writes it; the ambiguity is reported separately.
    private static readonly string[] DateTimeFormats =
    {
        "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm",
        "dd/MM/yyyy HH:mm:ss", "dd/MM/yyyy HH:mm", "dd/MM/yyyy h:mm tt",
        "MM/dd/yyyy HH:mm:ss", "MM/dd/yyyy HH:mm", "MM/dd/yyyy h:mm tt",
        "dd-MM-yyyy HH:mm", "yyyy/MM/dd HH:mm",
        "dd MMM yyyy HH:mm", "d MMM yyyy HH:mm",
    };

    // ORDER IS LOAD BEARING, and it is the one place a silent month-off error can enter.
    // Unambiguous year-first forms are tried first, then EVERY day-first form, then month-first
    // as a fallback that can only win when a day-first reading is impossible ("12/25/2026").
    // The watched-folder parser previously listed "M/d/yyyy" ahead of "d/M/yyyy" and therefore
    // read "5/3/2026" as 3 May while every other door read it as 5 March.
    private static readonly string[] DateOnlyFormats =
    {
        "yyyy-MM-dd", "yyyy/MM/dd", "yyyy.MM.dd",
        "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy", "dd.MM.yyyy",
        "MM/dd/yyyy", "M/d/yyyy", "M/dd/yyyy", "MM/d/yyyy",
        "dd MMM yyyy", "d MMM yyyy", "MMM d, yyyy", "MMM dd, yyyy",
        "dd MMMM yyyy", "d MMMM yyyy", "MMMM d, yyyy", "MMMM dd, yyyy",
    };

    /// <summary>A numeric day/month/year token, used only to test for day-month ambiguity.</summary>
    private static readonly Regex NumericDayMonth =
        new(@"^\s*(\d{1,2})\s*[/.\-]\s*(\d{1,2})\s*[/.\-]\s*\d{4}", RegexOptions.Compiled);

    /// <summary>Pulls a date-looking run out of surrounding prose ("Closing: 15/03/2026 (Riyadh)").</summary>
    private static readonly Regex EmbeddedDate = new(
        @"(\d{4}[/.\-]\d{1,2}[/.\-]\d{1,2}(?:[T ]\d{1,2}:\d{2}(?::\d{2})?)?)" +
        @"|(\d{1,2}[/.\-]\d{1,2}[/.\-]\d{4}(?:\s+\d{1,2}:\d{2}(?::\d{2})?(?:\s*[APap][Mm])?)?)" +
        @"|(\d{1,2}\s+[A-Za-z]{3,9},?\s+\d{4})",
        RegexOptions.Compiled);

    /// <summary>
    /// Drop-in replacement for the per-service parsers. Returns null when the token is not a
    /// usable date.
    /// </summary>
    public static DateTime? Parse(string? raw) => Read(raw).Value;

    /// <summary>Full reading, including whether a time was stated and whether the token is ambiguous.</summary>
    public static RfqDateReading Read(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return RfqDateReading.None;

        var trimmed = raw.Trim();
        var normalized = NormalizeDigits(trimmed).Replace(' ', ' ').Trim();

        // Excel and some exporters hand over a real DateTime already rendered by the current
        // culture; and some documents wrap the date in prose. Try the token as given first, then
        // the first date-looking run inside it.
        var reading = ReadExact(normalized, trimmed);
        if (reading.HasValue) return reading;

        var embedded = EmbeddedDate.Match(normalized);
        return embedded.Success ? ReadExact(embedded.Value.Trim(), trimmed) : RfqDateReading.None;
    }

    private static RfqDateReading ReadExact(string candidate, string rawToken)
    {
        if (DateTime.TryParseExact(candidate, DateTimeFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var withTime))
            return Sanitized(withTime, rawToken, hasTime: true, IsAmbiguous(candidate));

        if (DateTime.TryParseExact(candidate, DateOnlyFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var dateOnly))
            return Sanitized(dateOnly, rawToken, hasTime: false, IsAmbiguous(candidate));

        // A token that states a time we cannot read must still yield its date. Losing the whole
        // deadline because the time portion was odd is the worst possible outcome.
        var firstSpace = candidate.IndexOf(' ');
        if (firstSpace > 0 && DateTime.TryParseExact(candidate[..firstSpace], DateOnlyFormats,
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var degraded))
            return Sanitized(degraded, rawToken, hasTime: false, IsAmbiguous(candidate));

        return RfqDateReading.None;
    }

    /// <summary>Hijri years a real tender could plausibly carry. 1400 AH ≈ 1979, 1500 AH ≈ 2076.</summary>
    private const int MinimumPlausibleHijriYear = 1400;
    private const int MaximumPlausibleHijriYear = 1500;

    private static RfqDateReading Sanitized(DateTime value, string rawToken, bool hasTime, bool ambiguous)
    {
        if (value.Year is >= MinimumPlausibleYear and <= MaximumPlausibleYear)
            return new RfqDateReading(value, rawToken, hasTime, ambiguous);

        // A Saudi government tender routinely states its closing date in Hijri — "15/03/1447".
        // Every component parses, so the token reaches here looking like a Gregorian date in the
        // year 1447, where the plausible-year guard discards it and the deadline is lost in
        // silence. That is the single most damaging way to misread an Etimad pack: the bid closes
        // and nobody knew there was a bid.
        //
        // A year in the Hijri band cannot be a Gregorian tender date — 1447 CE is not a date any
        // buyer means — so the reading is unambiguous, and the value is converted rather than
        // rejected. The stored value stays Gregorian: one calendar is authoritative everywhere,
        // and ToHijri renders the buyer's own form beside it.
        if (value.Year is >= MinimumPlausibleHijriYear and <= MaximumPlausibleHijriYear
            && FromHijri(value.Year, value.Month, value.Day) is { } gregorian)
        {
            var withTime = gregorian.Add(value.TimeOfDay);
            return withTime.Year is >= MinimumPlausibleYear and <= MaximumPlausibleYear
                ? new RfqDateReading(withTime, rawToken, hasTime, IsDayMonthAmbiguous: false)
                : RfqDateReading.None;
        }

        return RfqDateReading.None;
    }

    /// <summary>
    /// Converts an Umm al-Qura date to its Gregorian equivalent, or null when the components are
    /// not a real Hijri date (month 13, day 31, or outside the calendar's supported range).
    /// </summary>
    public static DateTime? FromHijri(int year, int month, int day)
    {
        var calendar = new UmAlQuraCalendar();
        if (month is < 1 or > 12 || day < 1) return null;

        try
        {
            if (day > calendar.GetDaysInMonth(year, month)) return null;
            return calendar.ToDateTime(year, month, day, 0, 0, 0, 0);
        }
        catch (ArgumentOutOfRangeException)
        {
            // Outside Umm al-Qura's supported range. Unreadable is the honest answer.
            return null;
        }
    }

    /// <summary>
    /// True when a numeric token's first two components are both 12 or lower and differ, so
    /// day-first and month-first readings give different real dates.
    /// </summary>
    private static bool IsAmbiguous(string candidate)
    {
        var m = NumericDayMonth.Match(candidate);
        if (!m.Success) return false;
        return int.TryParse(m.Groups[1].Value, out var a)
            && int.TryParse(m.Groups[2].Value, out var b)
            && a <= 12 && b <= 12 && a != b;
    }

    /// <summary>
    /// Renders a Gregorian date in the Umm al-Qura calendar as <c>yyyy-MM-dd</c>, or null when
    /// the date falls outside the range that calendar supports.
    ///
    /// <para>Saudi government tenders publish closing dates in Hijri, so a system that only ever
    /// holds the Gregorian value cannot show a buyer their own deadline in the form they wrote
    /// it. Both are kept: the Gregorian value stays authoritative for every comparison and
    /// deadline calculation, and this is the rendering beside it. Umm al-Qura is the civil
    /// calendar of Saudi Arabia and is what Etimad publishes against.</para>
    /// </summary>
    public static string? ToHijri(DateTime? gregorian)
    {
        if (gregorian is not { } value) return null;

        var calendar = new UmAlQuraCalendar();
        if (value < calendar.MinSupportedDateTime || value > calendar.MaxSupportedDateTime)
            return null;

        return string.Create(CultureInfo.InvariantCulture, $"{calendar.GetYear(value):0000}-{calendar.GetMonth(value):00}-{calendar.GetDayOfMonth(value):00}");
    }

    /// <summary>
    /// Maps Arabic-Indic (٠-٩) and Eastern Arabic-Indic (۰-۹) digits onto ASCII. Saudi tender
    /// documents carry these routinely, and a scanned deadline in Arabic numerals is a data
    /// problem rather than a user-interface one — it must be read even while the interface is
    /// English only.
    /// </summary>
    public static string NormalizeDigits(string value)
    {
        if (!value.Any(c => c is >= '٠' and <= '٩' or >= '۰' and <= '۹'))
            return value;

        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            sb.Append(c switch
            {
                >= '٠' and <= '٩' => (char)('0' + (c - '٠')),
                >= '۰' and <= '۹' => (char)('0' + (c - '۰')),
                _ => c,
            });
        }
        return sb.ToString();
    }
}
