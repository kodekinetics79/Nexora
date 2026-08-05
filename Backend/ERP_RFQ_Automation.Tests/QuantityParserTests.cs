using ERP_RFQ_Automation.Extraction.Quantities;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Regression for the highest-value extraction defect found in production: a customer
/// RFQ line reading "1,000" was quoted to the customer as 1.
///
/// The old expression, repeated at four ingestion doors, was:
///     Quantity = int.TryParse(cell.Text, out var qty) ? qty : 1
/// int.TryParse with default NumberStyles rejects thousands separators, any decimal
/// point, and any trailing unit — so "1,000", "2,500 PCS", "12.00" and "2.5" all
/// silently became the number 1. In production 875 of 2,966 extracted lines carried
/// quantity 1, and RfqController.ApproveAsync mails the resulting quote PDF to the
/// customer in the same request, with no screen in between that displays a quantity.
///
/// The contract these tests pin down: read it correctly, or return null and send the
/// line to a human. Never substitute a number.
/// </summary>
public class QuantityParserTests
{
    // ---------------------------------------------------------------------
    // The exact production failures. Each of these used to yield 1.
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("1,000", 1000)]      // was 1 — a 1000x under-quote
    [InlineData("2,500 PCS", 2500)]  // was 1
    [InlineData("12.00", 12)]        // was 1
    [InlineData("1 000", 1000)]      // was 1 (space as thousands separator)
    [InlineData("500 EA", 500)]      // was 1
    [InlineData("1'000", 1000)]      // was 1 (Swiss convention)
    [InlineData("40 nos", 40)]       // was 1
    [InlineData("1,234,567", 1234567)]
    public void ReadsQuantitiesThatUsedToSilentlyBecomeOne(string raw, int expected)
    {
        var reading = QuantityParser.Parse(raw);

        Assert.True(reading.HasValue, $"'{raw}' must be read, not defaulted.");
        Assert.Equal(expected, reading.Value!.Value);
        Assert.False(reading.RequiresReview);
    }

    [Fact]
    public void KeepsTheUnitTokenItStripped()
    {
        var reading = QuantityParser.Parse("500 EA");

        Assert.Equal(500m, reading.Value);
        Assert.Equal("EA", reading.UnitToken);
        Assert.Equal(QuantityOrigin.ParsedWithUnitSuffix, reading.Origin);
    }

    // ---------------------------------------------------------------------
    // The core contract: never invent a number.
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("TBD")]
    [InlineData("as required")]
    [InlineData("see attached")]
    [InlineData("-")]
    [InlineData("N/A")]
    public void UnreadableTextYieldsNullNotOne(string raw)
    {
        var reading = QuantityParser.Parse(raw);

        Assert.Null(reading.Value);
        Assert.True(reading.RequiresReview);
        Assert.Equal(QuantityOrigin.Unreadable, reading.Origin);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AbsentSourceYieldsNullNotOne(string? raw)
    {
        var reading = QuantityParser.Parse(raw);

        Assert.Null(reading.Value);
        Assert.True(reading.RequiresReview);
        Assert.Equal(QuantityOrigin.Absent, reading.Origin);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("−5")]  // unicode minus, as emitted by some ERP exports
    public void NonPositiveQuantityIsHeldForReviewRatherThanClamped(string raw)
    {
        var reading = QuantityParser.Parse(raw);

        Assert.Null(reading.Value);
        Assert.True(reading.RequiresReview);
    }

    [Fact]
    public void DoesNotHarvestANumberOutOfProse()
    {
        // "Part 4032 gasket" must NOT yield quantity 4032. Prose quantities are the
        // conversational extractor's job; it carries a verifiable source span.
        var reading = QuantityParser.Parse("Part 4032 gasket");

        Assert.Null(reading.Value);
        Assert.True(reading.RequiresReview);
    }

    // ---------------------------------------------------------------------
    // Separator interpretation — where a wrong guess is a 1000x error.
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("2.5", 2.5)]
    [InlineData("10.75", 10.75)]
    [InlineData("0.500", 0.5)]        // leading zero settles it as decimal
    [InlineData("1.234.567", 1234567)] // repeated separator can only be grouping
    [InlineData("1.234,56", 1234.56)]  // both present, rightmost is the decimal
    [InlineData("1,234.56", 1234.56)]
    public void InterpretsSeparatorsThatAreDecidable(string raw, decimal expected)
    {
        var reading = QuantityParser.Parse(raw);

        Assert.True(reading.HasValue, $"'{raw}' should be decidable.");
        Assert.Equal(expected, reading.Value!.Value);
    }

    [Fact]
    public void RefusesTheGenuinelyAmbiguousCaseRatherThanGuessing()
    {
        // "1.234" is one thousand two hundred thirty-four in Germany and 1.234 in the
        // US. The two readings differ by 1000x. Guessing is how you ship the exact bug
        // this class was written to eliminate.
        var reading = QuantityParser.Parse("1.234");

        Assert.Null(reading.Value);
        Assert.Equal(QuantityOrigin.Ambiguous, reading.Origin);
        Assert.True(reading.RequiresReview);
    }

    // ---------------------------------------------------------------------
    // Fractional handling for the int-typed LeadItem.Quantity column.
    // ---------------------------------------------------------------------

    [Fact]
    public void FractionalIsHeldForReviewRatherThanTruncatedWhenIntegersAreRequired()
    {
        // Truncating 2.5 to 2 is a silent 20% under-quote. Callers writing to the
        // int-typed LeadItem.Quantity pass allowFractional: false and get a review flag.
        var reading = QuantityParser.Parse("2.5", allowFractional: false);

        Assert.Null(reading.Value);
        Assert.True(reading.RequiresReview);
    }

    [Fact]
    public void FractionalIsReadWhenTheCallerCanStoreIt()
    {
        var reading = QuantityParser.Parse("2.5", allowFractional: true);

        Assert.Equal(2.5m, reading.Value);
    }

    [Fact]
    public void WholeNumberWrittenWithDecimalsIsAcceptedByIntegerCallers()
    {
        // "12.00" is an integer quantity typed by a spreadsheet with 2dp formatting.
        var reading = QuantityParser.Parse("12.00", allowFractional: false);

        Assert.Equal(12m, reading.Value);
    }

    // ---------------------------------------------------------------------
    // Provenance is preserved so the review screen can explain itself.
    // ---------------------------------------------------------------------

    [Fact]
    public void PreservesTheSourceTextForTheReviewScreen()
    {
        var reading = QuantityParser.Parse("  2,500 PCS  ");

        Assert.Equal(2500m, reading.Value);
        Assert.Equal("  2,500 PCS  ", reading.SourceText);
    }
}
