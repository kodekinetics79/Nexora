using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.ProductIntelligence.ManufacturerKnowledge;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The learner is where one reviewed line becomes the platform's memory, so what it writes,
/// what it reinforces, what it skips, and what another tenant can see are all asserted
/// against the real relational model (SQLite lane, same pattern as CustomerAliasLearnerTests).
/// </summary>
public sealed class ManufacturerPatternLearnerTests
{
    private const long Tenant = 9100;
    private const long OtherTenant = 9200;

    [Fact]
    public async Task A_reviewed_line_stating_both_fields_teaches_every_candidate_pattern()
    {
        using var db = new TestDb();
        await SeedAsync(db, 9401, ("BMW", "X7-M5"), ("Siemens", "3RT2015-1BB41"), (null, "ZZ-1"), ("Nobody", "12345"));
        await using var context = db.ContextFor(Tenant);
        var lead = await LoadLeadAsync(context, 9401);

        var result = await new ManufacturerPatternLearner(context).LearnFromReviewAsync(Tenant, lead, 77);
        await context.SaveChangesAsync();

        Assert.Equal(5, result.Learned);
        Assert.Equal(0, result.Reinforced);
        Assert.Equal(ManufacturerPatternLearner.SkipUnusablePartNumber, result.SkippedReason);

        var rows = await context.Set<ManufacturerPartPattern>().OrderBy(p => p.Id).ToListAsync();
        Assert.Equal(["X7M5", "X7", "3RT20151BB41", "3RT2015", "3RT"], rows.Select(r => r.Pattern));
        Assert.All(rows, r =>
        {
            Assert.Equal(Tenant, r.BusinessUnitId);
            Assert.Equal(1, r.ObservationCount);
            Assert.Equal(9401, r.LearnedFromLeadId);
            Assert.Equal(77, r.LearnedFromReviewAuditId);
        });
        Assert.Equal("SIEMENS", rows.Single(r => r.Pattern == "3RT").NormalizedManufacturer);
    }

    [Fact]
    public async Task A_second_review_reinforces_and_only_then_does_the_pattern_answer()
    {
        using var db = new TestDb();
        await SeedAsync(db, 9401, ("BMW", "X7-M5"));
        await SeedAsync(db, 9402, ("bmw ", "X7-M5"));

        await using (var first = db.ContextFor(Tenant))
        {
            await new ManufacturerPatternLearner(first).LearnFromReviewAsync(Tenant, await LoadLeadAsync(first, 9401), 1);
            await first.SaveChangesAsync();

            // One observation: the reader hands the inference a snapshot it must not act on.
            var snapshot = await new EfManufacturerKnowledge(first).ForBusinessUnitAsync(Tenant);
            Assert.Null(ManufacturerInference.Infer(null, "Bracket", "X7-M5", snapshot.Patterns, snapshot.KnownManufacturers));
        }

        await using (var second = db.ContextFor(Tenant))
        {
            var result = await new ManufacturerPatternLearner(second).LearnFromReviewAsync(Tenant, await LoadLeadAsync(second, 9402), 2);
            await second.SaveChangesAsync();

            Assert.Equal(0, result.Learned);
            Assert.Equal(2, result.Reinforced);
        }

        await using (var reader = db.ContextFor(Tenant))
        {
            var rows = await reader.Set<ManufacturerPartPattern>().ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.All(rows, r => Assert.Equal(2, r.ObservationCount));
            Assert.All(rows, r => Assert.Equal(9401, r.LearnedFromLeadId));

            var snapshot = await new EfManufacturerKnowledge(reader).ForBusinessUnitAsync(Tenant);
            var inferred = ManufacturerInference.Infer(null, "Bracket", "X7-M5", snapshot.Patterns, snapshot.KnownManufacturers);
            Assert.Equal("BMW", inferred?.Manufacturer);
            Assert.Equal("inferred_from_part_number_pattern:X7M5", inferred?.Reason);
        }
    }

    [Fact]
    public async Task Another_tenant_sees_nothing_of_what_this_one_taught()
    {
        using var db = new TestDb();
        await SeedAsync(db, 9401, ("BMW", "X7-M5"));
        await using (var context = db.ContextFor(Tenant))
        {
            await new ManufacturerPatternLearner(context).LearnFromReviewAsync(Tenant, await LoadLeadAsync(context, 9401), null);
            await context.SaveChangesAsync();
        }

        await using var other = db.ContextFor(OtherTenant);
        Assert.True((await new EfManufacturerKnowledge(other).ForBusinessUnitAsync(OtherTenant)).IsEmpty);

        // Even an unfiltered (background-worker) context is bounded by the explicit tenant predicate.
        await using var background = db.ContextFor(null);
        Assert.True((await new EfManufacturerKnowledge(background).ForBusinessUnitAsync(OtherTenant)).IsEmpty);
        Assert.False((await new EfManufacturerKnowledge(background).ForBusinessUnitAsync(Tenant)).IsEmpty);
    }

    [Fact]
    public async Task A_lead_with_no_line_stating_both_fields_teaches_nothing()
    {
        using var db = new TestDb();
        await SeedAsync(db, 9401, (null, "X7-M5"), ("BMW", null));
        await using var context = db.ContextFor(Tenant);

        var result = await new ManufacturerPatternLearner(context).LearnFromReviewAsync(Tenant, await LoadLeadAsync(context, 9401), null);
        await context.SaveChangesAsync();

        Assert.Equal(0, result.Learned);
        Assert.Equal(ManufacturerPatternLearner.SkipNoEvidence, result.SkippedReason);
        Assert.Empty(await context.Set<ManufacturerPartPattern>().ToListAsync());
    }

    private static async Task SeedAsync(TestDb db, long leadId, params (string? Maker, string? Part)[] lines)
    {
        await using var seed = db.ContextFor(null);
        Seed.EnsureBusinessUnit(seed, Tenant);
        var items = lines.Select((line, index) =>
        {
            var item = Seed.LeadItem(leadId * 10 + index + 1, (index + 1).ToString(), 1, "Widget");
            item.ManufacturerName = line.Maker;
            item.ManufacturerPartNumber = line.Part;
            return item;
        }).ToList();
        Seed.Lead(seed, leadId, Tenant, items: items);
        await seed.SaveChangesAsync();
    }

    private static async Task<Lead> LoadLeadAsync(ErpRfqAutomationContext context, long leadId)
        => await context.Leads.Include(l => l.LeadItems).SingleAsync(l => l.Id == leadId);
}
