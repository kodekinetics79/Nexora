using System.Text;
using ERP_RFQ_Automation.DTOs.DocumentIntelligence;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Services.DocumentIntelligence;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Pilot audit HT-01 / XS-01 / CP-01 / UX-01 / HT-05 / XS-02: SEC and Aramco SAP Ariba prints
/// write their dates month-first ("Due date 9/6/2026 5:00 PM" is 6 September, 5 PM), and Nexora
/// read them day-first and dropped the time — "Quote due 09 Jun 2026 · Closed 111 days ago" for a
/// tender that closed on 6 September, and every SEC request "received" in March.
/// </summary>
public sealed class PortalPrintDateOrderTests
{
    /// <summary>The upload day of the audit: every SEC print was read three weeks after it was published.</summary>
    private static readonly DateTime Arrived = new(2026, 9, 28);

    /// <summary>The shape of an SEC SAP Ariba print (.doc = HTML), trimmed to the header and one line.</summary>
    private static string SecPrint(string publish, string due, bool withBanner = true) => $$"""
        <!-- class: ariba.sourcing.rfxui.PrintRFXEncode -->
        <html><head><meta content="text/html; charset=UTF-8" http-equiv="Content-Type"/></head>
        <body>
        <table><tr><th colspan="2" class="sectionHead">Introduction</th></tr>
        <tr><td>{{(withBanner
            ? "<p>This is a print version of the event. It contains a summary of the event and its contents.</p><p>This file was downloaded at: [Sunday, September 6, 2026 at 11:28 AM]</p>"
            : "<p>Summary</p>")}}</td></tr></table>
        <table><tr><th colspan="2">Overview</th></tr>
        <tr><td class="fieldLabel">Owner</td><td class="fieldValue">Labib A. Nogali</td></tr>
        <tr><td class="fieldLabel">Event Type</td><td class="fieldValue">RFP</td></tr>
        <tr><td class="fieldLabel">Currency</td><td class="fieldValue">Saudi Riyal</td></tr></table>
        <table><tr><th colspan="2">Timing Rules</th></tr>
        <tr><td class="fieldLabel">Publish time</td><td class="fieldValue">{{publish}}</td></tr>
        <tr><td class="fieldLabel">Due date</td><td class="fieldValue">{{due}}</td></tr></table>
        <table><tr><th colspan="3">Content</th></tr>
        <tr><th>Name</th><th>Alternative</th><th>Value</th></tr>
        <tr><td colspan="2">10 909101154 BATTERY,STORAGE,MAX VOLT 1.5 V,830AH</td><td></td></tr>
        <tr><td>Price</td><td></td><td></td></tr>
        <tr><td>Quantity</td><td></td><td>92 each</td></tr>
        <tr><td>Material Code</td><td></td><td>909101154</td></tr>
        </table>
        </body></html>
        """;

    private static CanonicalRfqDocument ReadPrint(string html, string fileName)
    {
        var reading = HtmlTableGrids.Read(Encoding.UTF8.GetBytes(html));
        var rows = new DocxTableParser(new NativeSpreadsheetParser()).ParseGrids(reading.Grids, reading.Paragraphs, fileName);
        Assert.NotEmpty(rows);
        return Assert.Single(new CanonicalRfqNormalizer().NormalizeSpreadsheetRows(rows, 1, Arrived).Documents);
    }

    private static void AssertSettled(CanonicalValue<DateTime> value)
    {
        Assert.Equal(ValidationStatus.Valid, value.ValidationStatus);
        Assert.DoesNotContain("ambiguous_day_month", value.Transformations);
    }

    [Fact]
    public void SEC_C001701152_publish_and_due_are_read_month_first_with_the_closing_time()
    {
        // "Publish time 9/3/2026 10:37 AM", "Due date 9/6/2026 5:00 PM": stored as 9 March and
        // 9 June 00:00 before the fix.
        var document = ReadPrint(SecPrint("9/3/2026 10:37 AM", "9/6/2026 5:00 PM"), "SE  RFP C001701152.doc");

        Assert.Equal(new DateTime(2026, 9, 6, 17, 0, 0), document.BidClosingDate.Value);
        Assert.Equal(new DateTime(2026, 9, 3, 10, 37, 0), document.ReceivedDate.Value);
        AssertSettled(document.BidClosingDate);
        AssertSettled(document.ReceivedDate);
        Assert.Contains(document.BidClosingDate.Transformations, t => t.Contains("SAP Ariba", StringComparison.Ordinal));
        Assert.DoesNotContain(document.Issues, i => i.Code is "BID_CLOSING_DATE" or "RECEIVED_DATE");
        Assert.Equal("C001701152", document.RfqNo.Value);
    }

    [Theory]
    [InlineData("9/3/2026 5:21 PM", "9/8/2026 1:45 AM", 2026, 9, 3, 17, 21, 2026, 9, 8, 1, 45)]   // C001832162
    [InlineData("9/3/2026 11:10 AM", "9/8/2026 5:00 PM", 2026, 9, 3, 11, 10, 2026, 9, 8, 17, 0)]  // C001817585
    [InlineData("9/3/2026 3:36 PM", "9/13/2026 1:45 AM", 2026, 9, 3, 15, 36, 2026, 9, 13, 1, 45)] // C001832136
    [InlineData("9/3/2026 3:59 PM", "9/14/2026 1:45 AM", 2026, 9, 3, 15, 59, 2026, 9, 14, 1, 45)] // C001832155
    public void SEC_9_8_2026_is_8_September_and_every_print_keeps_its_time(
        string publish, string due,
        int py, int pm, int pd, int ph, int pmin,
        int dy, int dm, int dd, int dh, int dmin)
    {
        var document = ReadPrint(SecPrint(publish, due), "SE  RFP-C001832162.doc");

        Assert.Equal(new DateTime(dy, dm, dd, dh, dmin, 0), document.BidClosingDate.Value);
        Assert.Equal(new DateTime(py, pm, pd, ph, pmin, 0), document.ReceivedDate.Value);
        AssertSettled(document.BidClosingDate);
    }

    [Fact]
    public void The_Ariba_timing_rules_alone_are_enough_when_the_banner_is_missing()
    {
        var document = ReadPrint(SecPrint("9/3/2026 10:37 AM", "9/6/2026 5:00 PM", withBanner: false), "SE  RFP C001757361.doc");
        Assert.Equal(new DateTime(2026, 9, 6, 17, 0, 0), document.BidClosingDate.Value);
        Assert.Equal(new DateTime(2026, 9, 3), document.ReceivedDate.Value.Date);
    }

    // ------------------------------------------------------------------ the time of day

    [Theory]
    [InlineData("10/8/2026 3:00 PM", 15, 0)]   // Aramco 6000000028: day and month of one and two digits
    [InlineData("9/2/2026 2:15 PM", 14, 15)]   // Aramco publish time: both single digit
    [InlineData("9/6/2026 5:00 PM", 17, 0)]
    [InlineData("9/13/2026 1:45 AM", 1, 45)]
    [InlineData("9/6/2026 17:00", 17, 0)]
    [InlineData("16.09.2026 10:00:00", 10, 0)] // SABIC
    public void A_single_digit_day_or_month_keeps_the_time_of_day(string raw, int hour, int minute)
    {
        var reading = RfqDateParser.Read(raw);
        Assert.True(reading.HasExplicitTime, $"\"{raw}\" lost its time");
        Assert.Equal(new TimeSpan(hour, minute, 0), reading.Value!.Value.TimeOfDay);
    }

    [Fact]
    public void Aramco_due_date_3_PM_is_stored_with_its_time()
    {
        // "Due date 10/8/2026 3:00 PM" became 2026-10-08 00:00: the deadline turned OVERDUE at
        // 03:00 Riyadh, twelve hours before the portal closed.
        var document = ReadPrint(SecPrint("9/2/2026 2:15 PM", "10/8/2026 3:00 PM"), "RFP - 6000000099 - Switchgear Package 2026 - 1 of 3.doc");
        Assert.Equal(new DateTime(2026, 10, 8, 15, 0, 0), document.BidClosingDate.Value);
        Assert.Equal(new DateTime(2026, 9, 2, 14, 15, 0), document.ReceivedDate.Value);
    }

    // --------------------------------------------------- one unambiguous date settles the rest

    private static RfqSpreadsheetRow Row(string? received, string? closing) => new()
    {
        RowNumber = 2,
        RfqNo = "C001817585",
        BuyerName = "SEC buyer",
        ProductName = "Relay",
        Quantity = "1",
        UnitOfMeasure = "each",
        ReceivedDate = received,
        BidClosingDate = closing,
    };

    [Fact]
    public void A_date_that_can_only_be_month_first_settles_the_other_dates_on_the_document()
    {
        // No portal signature: "9/13/2026" has no 13th month, so "9/3/2026" beside it is 3 Sep.
        var document = Assert.Single(new CanonicalRfqNormalizer()
            .NormalizeSpreadsheetRows(new[] { Row("9/3/2026 3:36 PM", "9/13/2026 1:45 AM") }, 1, Arrived).Documents);

        Assert.Equal(new DateTime(2026, 9, 3, 15, 36, 0), document.ReceivedDate.Value);
        Assert.Equal(new DateTime(2026, 9, 13, 1, 45, 0), document.BidClosingDate.Value);
        AssertSettled(document.ReceivedDate);
    }

    [Fact]
    public void A_date_that_can_only_be_day_first_settles_the_other_dates_day_first()
    {
        var document = Assert.Single(new CanonicalRfqNormalizer()
            .NormalizeSpreadsheetRows(new[] { Row("25/08/2026", "9/8/2026") }, 1, Arrived).Documents);

        Assert.Equal(new DateTime(2026, 8, 9), document.BidClosingDate.Value.Date);
        AssertSettled(document.BidClosingDate);
        Assert.Contains(document.BidClosingDate.Transformations, t => t.StartsWith("read_day_first", StringComparison.Ordinal));
    }

    [Fact]
    public void With_no_evidence_either_way_the_date_stays_flagged_for_the_rep()
    {
        var document = Assert.Single(new CanonicalRfqNormalizer()
            .NormalizeSpreadsheetRows(new[] { Row("2026-06-20", "9/8/2026 5:00 PM"), Row("2026-06-20", "9/8/2026 5:00 PM") }, 1, new DateTime(2026, 7, 1)).Documents);

        Assert.Equal(ValidationStatus.NeedsReview, document.BidClosingDate.ValidationStatus);
        var issue = Assert.Single(document.Issues, i => i.Code == "BID_CLOSING_DATE");
        Assert.Contains("\"9/8/2026 5:00 PM\" is ambiguous", issue.Message);
        // Header facts lead the review note, ahead of the duplicate-line warnings, so the
        // question survives the note's three-reason cut.
        Assert.Contains(document.Issues, i => i.Code == "DUPLICATE_LINE");
        Assert.Equal("BID_CLOSING_DATE", document.Issues[0].Code);
    }

    [Fact]
    public void Dates_that_contradict_each_other_prove_no_order()
        => Assert.Null(DocumentDateOrder.FromUnambiguousDates(new[] { "13/08/2026", "9/13/2026" }));

    // ------------------------------------------------------------- the rep's question

    private const string DayFirstNote =
        "[NEEDS REVIEW] \"9/8/2026 5:00 PM\" is ambiguous — both parts of the bid closing date are 12 or lower, so it could be "
        + "either day/month or month/day. It has been read day-first; confirm it. "
        + "\"9/3/2026\" is ambiguous — both parts of the received date are 12 or lower, so it could be "
        + "either day/month or month/day. It has been read day-first; confirm it.";

    [Fact]
    public void A_guessed_closing_date_becomes_the_question_closes_8_Sep_or_9_Aug()
    {
        var question = ClosingDateQuestion.From(DayFirstNote, new DateTime(2026, 8, 9, 17, 0, 0));

        Assert.NotNull(question);
        Assert.Equal("9/8/2026 5:00 PM", question!.DocumentText);
        Assert.Equal(new DateTime(2026, 8, 9, 17, 0, 0), question.CurrentReading);
        Assert.Equal(new DateTime(2026, 9, 8, 17, 0, 0), question.OtherReading);
    }

    [Fact]
    public void A_closing_date_a_person_already_changed_asks_nothing()
        => Assert.Null(ClosingDateQuestion.From(DayFirstNote, new DateTime(2026, 9, 30)));

    [Fact]
    public void Answering_month_first_reads_the_received_date_month_first_and_removes_the_question()
    {
        Assert.Equal(new DateTime(2026, 9, 3),
            ClosingDateQuestion.ReceivedDateInOrder(DayFirstNote, new DateTime(2026, 3, 9), monthFirst: true));

        var cleaned = ClosingDateQuestion.RemoveDateQuestions(DayFirstNote);
        Assert.Null(cleaned);
        Assert.Null(ClosingDateQuestion.From(cleaned, new DateTime(2026, 8, 9)));
    }

    [Fact]
    public void Removing_the_question_keeps_the_rest_of_the_note()
    {
        var note = DayFirstNote + " 3 of 40 line(s) have a field the document states elsewhere but not here.";
        Assert.Equal("[NEEDS REVIEW] 3 of 40 line(s) have a field the document states elsewhere but not here.",
            ClosingDateQuestion.RemoveDateQuestions(note));
    }
}
