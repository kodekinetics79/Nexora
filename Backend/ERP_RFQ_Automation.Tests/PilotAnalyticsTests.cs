using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The three pilot analytics. Each is judged on the same standard: does it state a fact
/// with its denominator attached, and does it disclose what it left out?
/// </summary>
public class PilotAnalyticsTests
{
    private const long Bu = 1;

    // ───────────────────────────────────────────── brand demand normalisation

    [Theory]
    [InlineData("EATON", "EATON")]
    [InlineData("  eaton  ", "EATON")]
    [InlineData("Eaton Corp.", "EATON")]
    [InlineData("EATON CORPORATION", "EATON")]
    [InlineData("Eaton, Inc", "EATON")]
    [InlineData("SIEMENS AG", "SIEMENS")]
    [InlineData("Schneider Electric GmbH", "SCHNEIDER ELECTRIC")]
    [InlineData("crouse-hinds/eaton", "CROUSE HINDS EATON")]
    public void Spelling_variants_of_one_brand_collapse_to_one_key(string raw, string expected)
        => Assert.Equal(expected, BrandDemandRepository.Normalize(raw));

    [Fact]
    public void Different_manufacturers_are_never_merged_by_the_normaliser()
    {
        // "CROUSE HINDS/EATON" may well BE Eaton, but deciding that is a judgement about
        // the customer's business. A normaliser that quietly made it would produce a
        // concentration figure nobody could check.
        Assert.NotEqual(
            BrandDemandRepository.Normalize("EATON"),
            BrandDemandRepository.Normalize("CROUSE HINDS/EATON"));
        Assert.NotEqual(
            BrandDemandRepository.Normalize("ABB"),
            BrandDemandRepository.Normalize("ABB CONTROL"));
    }

    [Fact]
    public void A_name_made_only_of_corporate_suffixes_keeps_its_tokens()
    {
        // Stripping to the empty string would silently drop the line from the analysis.
        Assert.Equal("CO", BrandDemandRepository.Normalize("Co."));
        Assert.NotEqual("", BrandDemandRepository.Normalize("Ltd"));
    }

    [Fact]
    public void Blank_manufacturers_normalise_away_rather_than_forming_a_group()
    {
        Assert.Equal("", BrandDemandRepository.Normalize(null));
        Assert.Equal("", BrandDemandRepository.Normalize("   "));
        Assert.Equal("", BrandDemandRepository.Normalize("-/-"));
    }

    [Fact]
    public async Task Brand_share_is_taken_over_all_lines_including_the_unbranded_ones()
    {
        // Dividing by the branded subset would let a brand on 2 of 2 branded lines read as
        // 100% of the book when half the book names no brand at all.
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.Lead(seed, 400, Bu, items: new[]
            {
                Line(1, "EATON"), Line(2, "Eaton Corp."), Line(3, null), Line(4, "  ")
            });
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(Bu);
        var report = await new BrandDemandRepository(ctx).GetAsync(Bu, null, null);

        Assert.Equal(4, report.TotalLines);
        Assert.Equal(2, report.LinesWithManufacturer);
        Assert.Equal(2, report.LinesWithoutManufacturer);
        Assert.Equal(1, report.DistinctManufacturers);

        var eaton = Assert.Single(report.Rows);
        Assert.Equal(2, eaton.Lines);
        Assert.Equal(2, eaton.Variants);       // two spellings folded, and the fold is visible
        Assert.Equal(1, eaton.Documents);
        Assert.Equal(50.0m, eaton.LineSharePercent);
    }

    [Fact]
    public async Task Brand_demand_counts_documents_not_only_lines()
    {
        // 143 lines from one spreadsheet is a different fact from 143 lines across 40
        // enquiries, and only the second is evidence of demand.
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.Lead(seed, 401, Bu, items: new[] { Line(1, "LEDVANCE"), Line(2, "LEDVANCE") });
            Seed.Lead(seed, 402, Bu, items: new[] { Line(3, "LEDVANCE") });
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(Bu);
        var report = await new BrandDemandRepository(ctx).GetAsync(Bu, null, null);

        var row = Assert.Single(report.Rows);
        Assert.Equal(3, row.Lines);
        Assert.Equal(2, row.Documents);
        Assert.Contains("mixed units of measure", report.QuantityCaveat);
    }

    // ───────────────────────────────────────────── deadline board

    [Fact]
    public async Task Open_leads_are_bucketed_by_days_to_close_with_their_line_counts()
    {
        using var db = new TestDb();
        var today = DateTime.UtcNow.Date;
        using (var seed = db.ContextFor(null))
        {
            Seed.Lead(seed, 500, Bu, items: new[] { Line(1, null), Line(2, null) })
                .BidClosingDate = today.AddDays(-2);
            Seed.Lead(seed, 501, Bu, items: new[] { Line(3, null) }).BidClosingDate = today;
            Seed.Lead(seed, 502, Bu, items: new[] { Line(4, null) }).BidClosingDate = today.AddDays(2);
            Seed.Lead(seed, 503, Bu, items: new[] { Line(5, null) }).BidClosingDate = today.AddDays(60);
            Seed.Lead(seed, 504, Bu, items: new[] { Line(6, null) }); // no closing date
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(Bu);
        var board = await new DashboardRepository(ctx).GetDeadlineBoardAsync(Bu);

        Assert.Equal(5, board.OpenLeads);
        Assert.Equal(6, board.OpenLineItems);
        Assert.Equal(1, Bucket(board, "overdue"));
        Assert.Equal(1, Bucket(board, "today"));
        Assert.Equal(1, Bucket(board, "days_1_3"));
        Assert.Equal(1, Bucket(board, "later"));
        Assert.Equal(1, Bucket(board, "unknown"));

        // The overdue bucket's WORK, not just its count.
        Assert.Equal(2, board.Buckets.Single(b => b.Key == "overdue").LineItems);
    }

    [Fact]
    public async Task A_lead_with_no_closing_date_is_counted_separately_never_hidden()
    {
        // A missing deadline is a data gap the reviewer can close by asking the buyer.
        // Folding it into a comfortable bucket makes 27 silent gaps look under control.
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.Lead(seed, 510, Bu, items: new[] { Line(1, null) });
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(Bu);
        var board = await new DashboardRepository(ctx).GetDeadlineBoardAsync(Bu);

        Assert.Equal(1, board.LeadsWithoutClosingDate);
        Assert.Equal(1, Bucket(board, "unknown"));
        Assert.Equal(0, Bucket(board, "later"));
        Assert.Contains(board.Leads, l => l.LeadId == 510 && l.DaysLeft is null);
    }

    [Fact]
    public async Task Sentinel_closing_dates_are_unknown_rather_than_catastrophically_overdue()
    {
        // Extraction writes sentinel dates. Year 1900 read literally is 45,000 days
        // overdue, which would dominate the board.
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.Lead(seed, 520, Bu, items: new[] { Line(1, null) })
                .BidClosingDate = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(Bu);
        var board = await new DashboardRepository(ctx).GetDeadlineBoardAsync(Bu);

        Assert.Equal(0, Bucket(board, "overdue"));
        Assert.Equal(1, Bucket(board, "unknown"));
    }

    [Fact]
    public async Task The_board_shows_only_this_tenants_enquiries()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.Lead(seed, 530, Bu, items: new[] { Line(1, null) });
            Seed.Lead(seed, 531, 2, items: new[] { Line(2, null) });
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(Bu);
        var board = await new DashboardRepository(ctx).GetDeadlineBoardAsync(Bu);

        Assert.Equal(1, board.OpenLeads);
        Assert.All(board.Leads, l => Assert.Equal(530, l.LeadId));
    }

    // ───────────────────────────────────────────── the withdrawn series

    [Fact]
    public async Task The_dashboard_no_longer_computes_an_AI_accuracy_score()
    {
        // The radar averaged Lead.Aiconfidence — a literal 1.0/0.2 from the normalizer, or
        // the model's own self-report — multiplied it by 100 and labelled it "AI Accuracy".
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            var lead = Seed.Lead(seed, 540, Bu, items: new[] { Line(1, null) });
            lead.Aiconfidence = 1.0m;
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(Bu);
        var data = await new DashboardRepository(ctx).GetDashboardDataAsync(Bu);

        Assert.Empty(data.OperationalHealth!);
        Assert.DoesNotContain(data.OperationalHealth!, r => r.Subject == "AI Accuracy");
        Assert.Empty(data.ResponseIntegrity!);
        Assert.Empty(data.EfficiencyVelocity!);
    }

    // ───────────────────────────────────────────── helpers

    private static int Bucket(ERP_RFQ_Automation.DTOs.Dashboard.DeadlineBoardDTO board, string key)
        => board.Buckets.Single(b => b.Key == key).Leads;

    private static LeadItem Line(long id, string? manufacturer) => new()
    {
        Id = id,
        LineItemNo = $"L{id}",
        ProductShortName = "Widget",
        Quantity = 1,
        ManufacturerName = manufacturer
    };
}
