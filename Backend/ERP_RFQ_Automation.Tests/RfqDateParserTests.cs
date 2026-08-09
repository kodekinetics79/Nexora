using ERP_RFQ_Automation.Extraction;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// A closing date is the one field where being wrong costs a bid, so this suite is deliberately
/// heavy on the cases the previous per-service parsers got wrong or dropped.
/// </summary>
public sealed class RfqDateParserTests
{
    // ---------------------------------------------------------------- regression protection
    // Every format at least one ingestion door accepted before the parsers were merged. If any
    // of these stops parsing, a door that used to read a deadline has gone blind.

    [Theory]
    [InlineData("2026-03-15", 15)]      // ISO — every door
    [InlineData("15/03/2026", 15)]      // day-first slash — every door
    [InlineData("15-03-2026", 15)]      // day-first dash — every door
    [InlineData("2026/03/15", 15)]      // ISO slash — every door except manual upload
    [InlineData("5/3/2026", 5)]         // single-digit day-first
    [InlineData("15 Mar 2026", 15)]     // spelled month — watched folder only
    [InlineData("5 Mar 2026", 5)]
    [InlineData("Mar 15, 2026", 15)]    // month-first spelled — watched folder only
    [InlineData("15 March 2026", 15)]   // full month name — no door accepted this before
    public void Legacy_formats_still_parse(string raw, int expectedDay)
        => Assert.Equal(new DateTime(2026, 3, expectedDay), RfqDateParser.Parse(raw));

    [Fact]
    public void Day_first_ordering_is_preserved_for_ambiguous_tokens()
    {
        // 03/04/2026 stays 3 April, as before the merge. Behaviour is unchanged; what is new is
        // that the reading also reports it could have been 4 March.
        var parsed = RfqDateParser.Parse("03/04/2026");
        Assert.Equal(new DateTime(2026, 4, 3), parsed);
    }

    // ------------------------------------------------------------------------ times of day
    // A tender closing at 14:00 previously failed to parse ENTIRELY — the deadline was lost,
    // not truncated, because no accepted format carried a time.

    [Theory]
    [InlineData("2026-03-15 14:30")]
    [InlineData("2026-03-15T14:30")]
    [InlineData("2026-03-15T14:30:00")]
    [InlineData("15/03/2026 14:30")]
    public void Closing_time_is_read_and_kept(string raw)
    {
        var reading = RfqDateParser.Read(raw);
        Assert.True(reading.HasValue);
        Assert.True(reading.HasExplicitTime);
        Assert.Equal(new DateTime(2026, 3, 15, 14, 30, 0), reading.Value);
    }

    [Fact]
    public void Twelve_hour_clock_is_read()
    {
        var reading = RfqDateParser.Read("15/03/2026 2:30 PM");
        Assert.Equal(new DateTime(2026, 3, 15, 14, 30, 0), reading.Value);
        Assert.True(reading.HasExplicitTime);
    }

    [Fact]
    public void Unreadable_time_degrades_to_the_date_rather_than_losing_the_deadline()
    {
        var reading = RfqDateParser.Read("2026-03-15 at noon");
        Assert.Equal(new DateTime(2026, 3, 15), reading.Value);
        Assert.False(reading.HasExplicitTime);
    }

    [Fact]
    public void A_date_without_a_time_is_not_reported_as_having_one()
    {
        Assert.False(RfqDateParser.Read("2026-03-15").HasExplicitTime);
    }

    // ------------------------------------------------------------------------- ambiguity
    // AGENTS.md: critical uncertain values must be flagged, never guessed silently.

    [Theory]
    [InlineData("03/04/2026")]
    [InlineData("3/4/2026")]
    [InlineData("05-06-2026")]
    public void Numeric_tokens_that_could_swap_day_and_month_are_flagged(string raw)
        => Assert.True(RfqDateParser.Read(raw).IsDayMonthAmbiguous);

    [Theory]
    [InlineData("15/03/2026")]   // 15 cannot be a month
    [InlineData("2026-03-15")]   // ISO is unambiguous by construction
    [InlineData("15 Mar 2026")]  // spelled month is unambiguous
    [InlineData("04/04/2026")]   // both readings give the same real date
    public void Unambiguous_tokens_are_not_flagged(string raw)
        => Assert.False(RfqDateParser.Read(raw).IsDayMonthAmbiguous);

    // --------------------------------------------------------------------- sentinel guard
    // Applied unevenly before the merge: the email and folder doors had no guard at all, so
    // an extracted placeholder reached the database as a real closing date.

    [Theory]
    [InlineData("0001-01-01")]
    [InlineData("1900-01-01")]
    [InlineData("1999-12-31")]
    [InlineData("9999-12-31")]
    public void Sentinel_and_noise_years_are_treated_as_no_date(string raw)
        => Assert.Null(RfqDateParser.Parse(raw));

    [Fact]
    public void The_year_2000_boundary_is_inclusive()
        => Assert.Equal(new DateTime(2000, 1, 1), RfqDateParser.Parse("2000-01-01"));

    // ------------------------------------------------------------------- real-document noise

    [Theory]
    [InlineData("Closing: 15/03/2026 (Riyadh time)")]
    [InlineData("Bid closing date 2026-03-15")]
    [InlineData("  15/03/2026  ")]
    public void A_date_embedded_in_prose_is_found(string raw)
        => Assert.Equal(new DateTime(2026, 3, 15), RfqDateParser.Parse(raw));

    [Fact]
    public void Arabic_indic_digits_are_read()
    {
        // Saudi tender documents carry these routinely. Reading them is a data concern and is
        // independent of the interface remaining English-only.
        Assert.Equal(new DateTime(2026, 3, 15), RfqDateParser.Parse("٢٠٢٦-٠٣-١٥"));
        Assert.Equal(new DateTime(2026, 3, 15), RfqDateParser.Parse("۱۵/۰۳/۲۰۲۶"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("next week")]
    [InlineData("TBD")]
    [InlineData("15/13/2026")]   // month 13 is not a date
    [InlineData("32/03/2026")]   // day 32 is not a date
    public void Unusable_tokens_return_no_date(string? raw)
        => Assert.Null(RfqDateParser.Parse(raw));

    // ------------------------------------------------------------------------------ Hijri
    // Saudi government tenders publish closing dates in Hijri. Reading one as Gregorian loses
    // the bid, which is why this is in scope even though the Arabic interface is deferred.

    [Theory]
    [InlineData("2026-03-15", "1447-09-26")]
    [InlineData("2026-01-01", "1447-07-12")]
    public void A_closing_date_is_also_rendered_in_the_umm_al_qura_calendar(string gregorian, string expectedHijri)
        => Assert.Equal(expectedHijri, RfqDateParser.ToHijri(RfqDateParser.Parse(gregorian)));

    [Fact]
    public void No_date_means_no_hijri_date()
        => Assert.Null(RfqDateParser.ToHijri(null));

    [Fact]
    public void A_date_outside_the_umm_al_qura_range_yields_null_rather_than_a_wrong_date()
        => Assert.Null(RfqDateParser.ToHijri(new DateTime(1800, 1, 1)));

    [Fact]
    public void The_gregorian_value_is_unchanged_by_rendering_a_hijri_one()
    {
        var gregorian = RfqDateParser.Parse("2026-03-15");
        RfqDateParser.ToHijri(gregorian);
        Assert.Equal(new DateTime(2026, 3, 15), gregorian);
    }

    [Fact]
    public void The_raw_token_is_preserved_for_the_reviewer()
    {
        var reading = RfqDateParser.Read("  Closing: 15/03/2026  ");
        Assert.Equal("Closing: 15/03/2026", reading.RawToken);
    }
}
