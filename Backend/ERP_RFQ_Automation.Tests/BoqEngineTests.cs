using ERP_RFQ_Automation.Boq;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services.Interfaces;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ERP_RFQ_Automation.AI;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// WP-BOQ engine invariants over the real relational model (TestDb / SQLite):
///   * RecalcTotals prices qty×rate lines only — TBD lines are excluded from every
///     total and counted separately (the "never fake a number" rule).
///   * Assembly explosion replaces the item with library components (quantities
///     multiplied by the parent quantity, rates from the tenant library,
///     Source = "assembly") and refuses to explode a TBD item.
///   * The starter-assembly seed is lazy and idempotent.
///   * BOQ documents obey the same fail-closed tenant isolation as every
///     commercial document (ADR-0005).
///   * A drawing draft without a vision model degrades to an honest TBD skeleton.
/// </summary>
public class BoqEngineTests
{
    private const long Bu1 = 1;
    private const long Bu2 = 2;

    // ---- helpers -----------------------------------------------------------

    /// <summary>LLM stub: returns a canned draft (or null = "model unavailable").</summary>
    private sealed class StubLlm : ILLMService
    {
        public BoqDraftResult? BoqResult { get; set; }
        public Task<LeadExtractionResult?> ExtractLeadDataAsync(
            string fullText, AiCallContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult<LeadExtractionResult?>(null);
        public Task<BoqDraftResult?> DraftServiceBoqAsync(
            string scopeText, AiCallContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(BoqResult);
    }

    private static BoqBuilderService Engine(ErpRfqAutomationContext ctx, StubLlm? llm = null) =>
        new(ctx, llm ?? new StubLlm(), new NotConfiguredVisionReader(), NullLogger<BoqBuilderService>.Instance);

    private static BoqDocument SeedDocument(ErpRfqAutomationContext ctx, long bu, out BoqItem pricedItem, out BoqItem tbdItem)
    {
        var now = DateTime.UtcNow;
        var doc = new BoqDocument
        {
            BusinessUnitId = bu,
            Title = "Pump overhaul BOQ",
            ServiceCategory = "mechanical",
            Status = BoqStatus.Draft,
            CreatedOn = now,
            UpdatedOn = now
        };
        var section = new BoqSection { BusinessUnitId = bu, Seq = 1, Title = "Works" };
        pricedItem = new BoqItem
        {
            BusinessUnitId = bu,
            Seq = 1,
            Description = "Mechanic — strip & rebuild",
            Unit = "hr",
            Quantity = 10m,
            ItemType = BoqItemType.Labor,
            UnitRate = 50m,
            Source = BoqItemSource.Manual
        };
        tbdItem = new BoqItem
        {
            BusinessUnitId = bu,
            Seq = 2,
            Description = "Replacement seals — size not stated",
            Unit = "set",
            Quantity = 0m,
            ItemType = BoqItemType.Material,
            UnitRate = 120m, // even with a rate, a TBD line must not be priced
            Source = BoqItemSource.Extracted,
            IsTbd = true,
            EvidenceNote = "Seal sizes not stated — quantity TBD"
        };
        section.Items.Add(pricedItem);
        section.Items.Add(tbdItem);
        doc.Sections.Add(section);
        ctx.Add(doc);
        ctx.SaveChanges();
        return doc;
    }

    // ---- RecalcTotals ------------------------------------------------------

    [Fact]
    public async Task RecalcTotals_PricesOnlyNonTbdLines_AndCountsTbdSeparately()
    {
        using var db = new TestDb();
        long docId;
        using (var seed = db.ContextFor(Bu1))
        {
            var doc = SeedDocument(seed, Bu1, out _, out _);
            docId = doc.Id;
        }

        using (var ctx = db.ContextFor(Bu1))
        {
            await Engine(ctx).RecalcTotalsAsync(docId, Bu1, CancellationToken.None);
        }

        using var assert = db.ContextFor(Bu1);
        var stored = assert.Set<BoqDocument>()
            .Include(d => d.Sections).ThenInclude(s => s.Items)
            .Single(d => d.Id == docId);

        var priced = stored.Sections.Single().Items.Single(i => !i.IsTbd);
        var tbd = stored.Sections.Single().Items.Single(i => i.IsTbd);

        Assert.Equal(500m, priced.TotalAmount);         // 10 hr × 50
        Assert.Null(tbd.TotalAmount);                   // TBD never contributes a number
        Assert.Equal(500m, stored.Sections.Single().TotalAmount);
        Assert.Equal(500m, stored.TotalAmount);
        Assert.Equal(1, stored.TbdCount);
    }

    [Fact]
    public async Task RecalcTotals_ItemWithoutRate_ContributesNothingButIsNotTbd()
    {
        using var db = new TestDb();
        long docId;
        using (var seed = db.ContextFor(Bu1))
        {
            var doc = SeedDocument(seed, Bu1, out var priced, out _);
            priced.UnitRate = null; // quantity known, rate not yet set
            seed.SaveChanges();
            docId = doc.Id;
        }

        using (var ctx = db.ContextFor(Bu1))
        {
            await Engine(ctx).RecalcTotalsAsync(docId, Bu1, CancellationToken.None);
        }

        using var assert = db.ContextFor(Bu1);
        var stored = assert.Set<BoqDocument>().Include(d => d.Sections).ThenInclude(s => s.Items)
            .Single(d => d.Id == docId);
        Assert.Equal(0m, stored.TotalAmount);
        Assert.Equal(1, stored.TbdCount); // only the seeded TBD line — an unrated line is not TBD
    }

    // ---- Assembly explosion ------------------------------------------------

    [Fact]
    public async Task ExplodeAssembly_ReplacesItemWithComponents_MultipliedByParentQty()
    {
        using var db = new TestDb();
        long itemId, docId;
        using (var seed = db.ContextFor(Bu1))
        {
            var doc = SeedDocument(seed, Bu1, out var priced, out _);
            priced.Description = "Lighting points as per schedule";
            priced.Unit = "EA";
            priced.Quantity = 4m;
            priced.UnitRate = null;
            priced.AssemblyCode = "LIGHT-POINT";
            seed.SaveChanges();
            itemId = priced.Id;
            docId = doc.Id;
        }

        BoqDocumentDto result;
        using (var ctx = db.ContextFor(Bu1))
        {
            result = await Engine(ctx).ExplodeAssemblyAsync(itemId, Bu1, null, CancellationToken.None);
        }

        var section = result.Sections.Single();
        var exploded = section.Items.Where(i => i.Source == BoqItemSource.Assembly).ToList();

        // Starter LIGHT-POINT has 3 components; original item is gone.
        Assert.Equal(3, exploded.Count);
        Assert.DoesNotContain(section.Items, i => i.Id == itemId);

        // Component quantities are parent qty × QtyPer, rates come from the library.
        var luminaire = exploded.Single(i => i.Description.Contains("luminaire", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(4m, luminaire.Quantity);       // 4 × 1
        Assert.Equal(65m, luminaire.UnitRate);
        Assert.Equal(260m, luminaire.TotalAmount);

        var electrician = exploded.Single(i => i.ItemType == BoqItemType.Labor);
        Assert.Equal(6m, electrician.Quantity);     // 4 × 1.5 hr

        Assert.All(exploded, i => Assert.Equal("LIGHT-POINT", i.AssemblyCode));
        Assert.All(exploded, i => Assert.False(i.IsTbd));

        // Totals rolled up: 4×65 + 4×25 + 6×45 = 630, plus untouched TBD tracking.
        Assert.Equal(630m, section.TotalAmount);
        Assert.Equal(630m, result.TotalAmount);
        Assert.Equal(1, result.TbdCount); // the seeded TBD line survived untouched

        // Seq stays contiguous after the in-place replacement.
        var seqs = section.Items.OrderBy(i => i.Seq).Select(i => i.Seq).ToList();
        Assert.Equal(Enumerable.Range(1, section.Items.Count).ToList(), seqs);
        _ = docId;
    }

    [Fact]
    public async Task ExplodeAssembly_TbdItem_IsRefused()
    {
        using var db = new TestDb();
        long tbdId;
        using (var seed = db.ContextFor(Bu1))
        {
            SeedDocument(seed, Bu1, out _, out var tbd);
            tbd.AssemblyCode = "LIGHT-POINT";
            seed.SaveChanges();
            tbdId = tbd.Id;
        }

        using var ctx = db.ContextFor(Bu1);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Engine(ctx).ExplodeAssemblyAsync(tbdId, Bu1, null, CancellationToken.None));
        Assert.Contains("quantity", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExplodeAssembly_UnknownCode_IsRefused()
    {
        using var db = new TestDb();
        long itemId;
        using (var seed = db.ContextFor(Bu1))
        {
            SeedDocument(seed, Bu1, out var priced, out _);
            itemId = priced.Id;
        }

        using var ctx = db.ContextFor(Bu1);
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            Engine(ctx).ExplodeAssemblyAsync(itemId, Bu1, "NO-SUCH-ASSEMBLY", CancellationToken.None));
    }

    // ---- Starter seed ------------------------------------------------------

    [Fact]
    public async Task StarterAssemblySeed_IsLazyAndIdempotent()
    {
        using var db = new TestDb();

        using (var ctx = db.ContextFor(Bu1))
        {
            var first = await Engine(ctx).GetAssembliesAsync(Bu1, CancellationToken.None);
            Assert.Equal(BoqStarterAssemblies.All.Count, first.Count);
            Assert.All(first, a => Assert.True(a.IsStarter));
            Assert.All(first, a => Assert.NotEmpty(a.Components));
        }

        using (var ctx = db.ContextFor(Bu1))
        {
            // Second call must not duplicate anything.
            var second = await Engine(ctx).GetAssembliesAsync(Bu1, CancellationToken.None);
            Assert.Equal(BoqStarterAssemblies.All.Count, second.Count);
        }

        // Seeding is per business unit.
        using (var ctx2 = db.ContextFor(Bu2))
        {
            var other = await Engine(ctx2).GetAssembliesAsync(Bu2, CancellationToken.None);
            Assert.Equal(BoqStarterAssemblies.All.Count, other.Count);
        }

        using var worker = db.ContextFor(null);
        Assert.Equal(BoqStarterAssemblies.All.Count * 2, worker.Set<BoqAssembly>().Count());
    }

    // ---- Tenant isolation --------------------------------------------------

    [Fact]
    public void BoqDocuments_ScopedContext_SeesOnlyOwnBusinessUnit()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            SeedDocument(seed, Bu1, out _, out _);
            SeedDocument(seed, Bu2, out _, out _);
        }

        using var bu1 = db.ContextFor(Bu1);
        var visible = bu1.Set<BoqDocument>().ToList();
        Assert.Single(visible);
        Assert.Equal(Bu1, visible[0].BusinessUnitId);

        // Items are filtered too — a point lookup cannot cross the boundary.
        Assert.All(bu1.Set<BoqItem>().ToList(), i => Assert.Equal(Bu1, i.BusinessUnitId));
    }

    [Fact]
    public async Task Get_OtherTenantsDocument_ReturnsNull()
    {
        using var db = new TestDb();
        long foreignId;
        using (var seed = db.ContextFor(Bu2))
        {
            foreignId = SeedDocument(seed, Bu2, out _, out _).Id;
        }

        using var ctx = db.ContextFor(Bu1);
        var dto = await Engine(ctx).GetAsync(foreignId, Bu1, CancellationToken.None);
        Assert.Null(dto);
    }

    // ---- Drawing fallback (vision seam) ------------------------------------

    [Fact]
    public async Task Draft_DrawingWithoutVisionModel_ProducesHonestTbdSkeleton()
    {
        using var db = new TestDb();
        using var ctx = db.ContextFor(Bu1);

        var dto = await Engine(ctx).DraftFromTextAsync(new BoqDraftRequest
        {
            Title = "Substation SLD",
            FileName = "substation-sld.dwg",
            MimeType = "application/acad"
        }, Bu1, CancellationToken.None);

        Assert.Equal(BoqStatus.Draft, dto.Status);
        Assert.NotNull(dto.Notes);
        Assert.Contains("vision", dto.Notes!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0m, dto.TotalAmount);
        Assert.True(dto.TbdCount > 0);
        Assert.All(dto.Sections.SelectMany(s => s.Items), i => Assert.True(i.IsTbd));
        Assert.Equal(3, dto.Sections.Count); // Supply / Installation / T&C skeleton
    }

    // ---- LLM draft mapping -------------------------------------------------

    [Fact]
    public async Task Draft_UnstatedQuantity_BecomesTbd_NeverInvented()
    {
        using var db = new TestDb();
        using var ctx = db.ContextFor(Bu1);

        var llm = new StubLlm
        {
            BoqResult = new BoqDraftResult(
                "electrical", 0.8,
                new List<BoqDraftSection>
                {
                    new("Installation", new List<BoqDraftItem>
                    {
                        new("Install 250A distribution panel", "EA", 1m, "Labor", 0.9),
                        new("LV cable runs — sizes not stated", "m", null, "Material", 0.4,
                            Tbd: true, TbdReason: "Cable sizes not stated — quantity TBD"),
                    })
                },
                new List<string> { "Site access available during working hours" })
        };

        var dto = await Engine(ctx, llm).DraftFromTextAsync(new BoqDraftRequest
        {
            Title = "Panel install",
            Text = "Install one 250A distribution panel and associated LV cabling."
        }, Bu1, CancellationToken.None);

        var items = dto.Sections.Single().Items;
        Assert.Equal(2, items.Count);

        var stated = items.Single(i => !i.IsTbd);
        Assert.Equal(1m, stated.Quantity);
        Assert.Equal(BoqItemSource.Extracted, stated.Source);

        var tbd = items.Single(i => i.IsTbd);
        Assert.Equal(0m, tbd.Quantity);
        Assert.Contains("not stated", tbd.EvidenceNote!, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(1, dto.TbdCount);
        Assert.Single(dto.Assumptions);
        Assert.Equal("electrical", dto.ServiceCategory);
    }
}
