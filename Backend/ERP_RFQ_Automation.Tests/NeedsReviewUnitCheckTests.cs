using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Services.Uom;
using ERP_RFQ_Automation.Tests.Support;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// "Documents to check" must say when a line's unit is a word the business has never transacted
/// in. A line with unit "BANANAS" passed as verified because the only question asked was whether
/// the cell was blank; the queue's reason column said nothing about it either.
/// </summary>
public sealed class NeedsReviewUnitCheckTests
{
    private const long Bu = 7_350;

    private static void Unit(ErpRfqAutomationContext ctx, int id, string code, string name) =>
        ctx.SetUoms.Add(new SetUom
        {
            UomId = id, BusinessUnitId = Bu, UomCode = code, UomName = name, IsActive = true, CreatedBy = "seed",
        });

    [Fact]
    public async Task The_queue_names_a_unit_that_is_neither_a_tenant_unit_nor_a_known_spelling()
    {
        using var db = new TestDb();
        await using var ctx = db.ContextFor(Bu);
        Seed.BusinessUnit(ctx, Bu);
        Unit(ctx, 7_351, "EA", "Each");
        // Extraction flagged the document for an unrelated reason; the unit reason must join it, not replace it.
        Seed.Lead(ctx, 7_352, Bu, parseStatus: "NeedsReview", headerRemarks: "[NEEDS REVIEW] Bid closing date could not be read",
            items:
            [
                Seed.LeadItem(7_353, "1", 5),
                Seed.LeadItem(7_354, "2", 12),
                Seed.LeadItem(7_355, "3", 1),
            ]);
        await ctx.SaveChangesAsync();
        var items = ctx.LeadItems.Where(i => i.LeadId == 7_352).OrderBy(i => i.Id).ToList();
        items[0].UnitOfMeasure = "EA";
        items[1].UnitOfMeasure = "BANANAS";
        items[2].UnitOfMeasure = "Nos"; // a spelling the canonicaliser maps to EA
        await ctx.SaveChangesAsync();

        var (rows, _) = await new LeadRepository(ctx).GetNeedsReviewLeadsAsync(1, 50, Bu);

        var row = Assert.Single(rows, r => r.Id == 7_352);
        Assert.Equal("Bid closing date could not be read; Unit 'BANANAS' is not one of your units — pick one", row.ReviewReason);
        Assert.DoesNotContain("EA", row.ReviewReason!.Replace("BANANAS", ""), StringComparison.Ordinal);
        Assert.DoesNotContain("Nos", row.ReviewReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_unit_only_this_tenant_uses_is_theirs_and_is_not_questioned()
    {
        using var db = new TestDb();
        await using var ctx = db.ContextFor(Bu);
        Seed.BusinessUnit(ctx, Bu);
        Unit(ctx, 7_361, "DRM", "Drum of 200 litres");
        Seed.Lead(ctx, 7_362, Bu, parseStatus: "NeedsReview", items: [Seed.LeadItem(7_363, "1", 4)]);
        await ctx.SaveChangesAsync();
        ctx.LeadItems.Single(i => i.Id == 7_363).UnitOfMeasure = "drm";
        await ctx.SaveChangesAsync();

        var (rows, _) = await new LeadRepository(ctx).GetNeedsReviewLeadsAsync(1, 50, Bu);

        var row = Assert.Single(rows, r => r.Id == 7_362);
        Assert.Null(row.ReviewReason);
    }

    [Fact]
    public void Unit_reasons_are_stated_once_per_unit_per_lead_in_the_canonicalisers_words()
    {
        var vocabulary = SetUomVocabulary.From([new SetUom { UomId = 1, BusinessUnitId = Bu, UomCode = "EA", UomName = "Each", IsActive = true, CreatedBy = "seed" }]);

        var reasons = LeadRepository.UnitReviewReasons(
            [(1, "BANANAS"), (1, "bananas"), (1, "Pallet"), (1, "EA"), (2, "Mtr")],
            vocabulary);

        Assert.Equal("Unit 'BANANAS' is not one of your units — pick one; Unit 'Pallet': packaging unit — confirm how many items it contains before quoting", reasons[1]);
        Assert.False(reasons.ContainsKey(2));
    }
}
