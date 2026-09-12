using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services.DocumentIntelligence;
using ERP_RFQ_Automation.Services.DocumentIntelligence.Learning;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// A reviewer who fills in the closing date that a "Cut-off" column carried has taught the
/// tenant what "Cut-off" means; the next file from that customer reads straight through.
/// </summary>
public sealed class HeaderSpellingLearnerTests
{
    private const long Tenant = 9100;
    private const long OtherTenant = 9200;

    private static async Task<ErpRfqAutomationContext> SeedAsync(TestDb db, string? extraFields)
    {
        await using (var seed = db.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, Tenant);
            var item = Seed.LeadItem(9401, "1", 4, "Relay module");
            item.ExtraFields = extraFields;
            Seed.Lead(seed, 9401, Tenant, items: new[] { item });
            await seed.SaveChangesAsync();
        }
        return db.ContextFor(Tenant);
    }

    private static Task<Lead> LoadAsync(ErpRfqAutomationContext context)
        => context.Leads.Include(l => l.LeadItems).SingleAsync(l => l.Id == 9401);

    [Fact]
    public async Task A_closing_date_the_reviewer_added_teaches_the_column_that_carried_it()
    {
        using var db = new TestDb();
        await using var context = await SeedAsync(db, """{"Cut-off":"10/08/2026","Zone":"East"}""");
        var lead = await LoadAsync(context);
        lead.BidClosingDate = new DateTime(2026, 8, 10);

        var result = await new HeaderSpellingLearner(context)
            .LearnFromReviewAsync(Tenant, lead, 77);
        await context.SaveChangesAsync();

        Assert.Equal(1, result.Learned);
        var learned = Assert.Single(await context.Set<HeaderSpelling>().ToListAsync());
        Assert.Equal("cutoff", learned.Spelling);
        Assert.Equal("Cut-off", learned.OriginalLabel);
        Assert.Equal(RfqSpreadsheetFields.BidClosingDate, learned.Field);
        Assert.Equal(9401, learned.LearnedFromLeadId);
        Assert.Equal(77, learned.LearnedFromReviewAuditId);

        // The tenant's documents are now read with the spelling; another tenant's are not.
        var taught = await new TenantHeaderVocabulary(context).ForBusinessUnitAsync(Tenant);
        Assert.Equal(RfqSpreadsheetFields.BidClosingDate, taught.FieldForColumn("CUT OFF"));
        await using var other = db.ContextFor(OtherTenant);
        Assert.Null((await new TenantHeaderVocabulary(other).ForBusinessUnitAsync(OtherTenant)).FieldForColumn("Cut-off"));
    }

    [Fact]
    public async Task A_second_confirmation_reinforces_and_a_conflicting_one_is_refused()
    {
        using var db = new TestDb();
        await using var context = await SeedAsync(db, """{"Cut-off":"2026-08-10"}""");
        var lead = await LoadAsync(context);
        lead.BidClosingDate = new DateTime(2026, 8, 10);
        var learner = new HeaderSpellingLearner(context);

        await learner.LearnFromReviewAsync(Tenant, lead, 1);
        await context.SaveChangesAsync();
        var again = await learner.LearnFromReviewAsync(Tenant, lead, 2);
        await context.SaveChangesAsync();

        Assert.Equal(1, again.Reinforced);
        Assert.Equal(2, Assert.Single(await context.Set<HeaderSpelling>().ToListAsync()).ObservationCount);

        // A later review reads the same label as the RFQ number: first confirmation wins.
        lead.Rfqno = "2026-08-10";
        lead.BidClosingDate = new DateTime(2026, 9, 1);
        var conflict = await learner.LearnFromReviewAsync(Tenant, lead, 3);
        Assert.Equal(0, conflict.Learned);
        Assert.Contains(HeaderSpellingLearner.SkipConflict, conflict.SkipReasons);
    }

    [Fact]
    public async Task A_value_the_reviewer_typed_from_elsewhere_teaches_nothing()
    {
        using var db = new TestDb();
        await using var context = await SeedAsync(db, """{"Cut-off":"10/08/2026"}""");
        var lead = await LoadAsync(context);
        lead.BidClosingDate = new DateTime(2026, 9, 1); // not what the column said

        var result = await new HeaderSpellingLearner(context).LearnFromReviewAsync(Tenant, lead, 5);

        Assert.Equal(0, result.Learned);
        Assert.Empty(await context.Set<HeaderSpelling>().ToListAsync());
    }

    [Fact]
    public async Task Two_labels_carrying_the_same_value_teach_nothing()
    {
        // "Cut-off" and "Issued" both say 10/08/2026: which of them meant the closing date
        // cannot be told from this document, so neither is learned.
        using var db = new TestDb();
        await using var context = await SeedAsync(db, """{"Cut-off":"10/08/2026","Issued":"10/08/2026"}""");
        var lead = await LoadAsync(context);
        lead.BidClosingDate = new DateTime(2026, 8, 10);

        var result = await new HeaderSpellingLearner(context).LearnFromReviewAsync(Tenant, lead, 5);

        Assert.Equal(0, result.Learned);
        Assert.Empty(await context.Set<HeaderSpelling>().ToListAsync());
    }

    [Fact]
    public async Task A_lead_with_no_unrecognised_labels_is_skipped_with_a_reason()
    {
        using var db = new TestDb();
        await using var context = await SeedAsync(db, null);
        var lead = await LoadAsync(context);
        lead.BidClosingDate = new DateTime(2026, 8, 10);

        var result = await new HeaderSpellingLearner(context).LearnFromReviewAsync(Tenant, lead, 5);

        Assert.Contains(HeaderSpellingLearner.SkipNoCandidates, result.SkipReasons);
    }
}
