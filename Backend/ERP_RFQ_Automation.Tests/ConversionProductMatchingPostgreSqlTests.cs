using ERP_RFQ_Automation.CommercialCases.Lifecycle;
using ERP_RFQ_Automation.Intelligence.Conversion;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The exact-code rung of the conversion match ladder, which nothing exercised before this file.
///
/// <para><b>The defect.</b> Lead 479 — a genuine Saudi Aramco RFQ — produced 32 RFQ lines with a
/// product auto-assigned on ZERO of them, and an earlier 21-line lead did the same. That shape is
/// diagnostic in itself: the similarity band is <c>0.40 + 0.45 * ratio</c> with <c>ratio &lt;= 1</c>,
/// so it tops out at 0.85 against a floor of 0.90 and can NEVER auto-assign. Once a catalogue
/// number fails to be recognised as one, the outcome is not "a few matches" — it is necessarily
/// none.</para>
///
/// <para><b>What was actually wrong.</b> Measured against this fixture on the pre-fix code, an
/// exact code in <c>ItemMaterialCode</c> scored 1.00 and in <c>ManufacturerPartNumber</c> 0.95 —
/// those two rungs worked. Every other placement of the SAME code failed: punctuated spellings
/// ("A2A-50006470" against a catalogue "A2A50006470") fell to 0.85, <c>AlternatePartNumber</c> was
/// never compared and fell to 0.85, and a code sitting in <c>ProductShortName</c> or
/// <c>ProductShortDescription</c> scored 0.00 — no candidate was fetched at all, because the ILIKE
/// candidate query searches ProductName and Description and never the catalogue's own number
/// columns. Six of the eight placements below are true red-to-green.</para>
///
/// <para><b>The floor is not touched.</b> <c>ConfidenceFloor</c> is still 0.90 and every rung
/// asserted here is exact equality against a catalogue number. The two negative controls at the
/// bottom are what prove the bar was widened rather than lowered: prose in a description still
/// refuses to auto-assign, and a code that matches nothing still refuses.</para>
///
/// <para><b>Why PostgreSQL.</b> <c>ResolveLinesAsync</c> issues ILIKE and set-based product
/// queries the SQLite provider cannot translate, so the resolver only ever runs against a real
/// database. Asserting it on the SQLite lane would assert against a path production never takes.
/// </para>
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class ConversionProductMatchingPostgreSqlTests
{
    private const long Tenant = 947_301;
    private const long CustomerId = 947_311;
    private static readonly DateTime Now = new(2026, 1, 5, 9, 0, 0, DateTimeKind.Utc);

    private readonly PostgreSqlTestDatabase _database;

    public ConversionProductMatchingPostgreSqlTests(PostgreSqlTestDatabase database) => _database = database;

    // ------------------------------------------------------------------ fixture

    private async Task SeedTenantAsync()
    {
        await using var owner = _database.ContextFor(null);
        if (await owner.BusinessUnits.AnyAsync(b => b.Id == Tenant)) return;
        var businessUnit = Seed.BusinessUnit(owner, Tenant);
        owner.SetupMasters.AddRange(LifecycleStatusCatalog.CreateFor(businessUnit, "tests"));
        Seed.Customer(owner, CustomerId, Tenant, "Saudi Aramco");
        await owner.SaveChangesAsync();
    }

    /// <summary>
    /// A catalogue row shaped the way the live tenant's is: keyed on the BUYER's material number,
    /// which is what "A2A50006470" is — an Aramco number, not a manufacturer's.
    /// </summary>
    private async Task<long> CatalogProductAsync(string partNo, string? modelNo = null,
        string name = "Gasket, spiral wound, 2IN CL300")
    {
        await SeedTenantAsync();
        await using var owner = _database.ContextFor(null);
        var existing = await owner.Products.FirstOrDefaultAsync(p => p.Buid == Tenant && p.PartNo == partNo);
        if (existing is not null) return existing.Id;
        var product = new Product
        {
            Buid = Tenant, PartNo = partNo, ModelNo = modelNo, ProductName = name,
            QtyOnHand = 0m, ReorderPoint = 0m, IsActive = true, CreatedBy = "qa", CreatedOn = Now
        };
        owner.Products.Add(product);
        await owner.SaveChangesAsync();
        return product.Id;
    }

    /// <summary>A lead that clears every gate except product matching, so nothing else is under test.</summary>
    private async Task<long> QualifiedLeadAsync(LeadItem item)
    {
        await SeedTenantAsync();
        await using var owner = _database.ContextFor(null);
        var qualifiedId = await LifecycleStatusCatalog.ResolveIdAsync(owner, Tenant, "Lead", "QUALIFIED");
        var lead = new Lead
        {
            BuyersName = "Aramco Bid Desk",
            RecDate = DateTime.UtcNow,
            BidClosingDate = DateTime.UtcNow.AddDays(14),
            LeadSource = "IntegrationTest",
            CreatedBy = "tests",
            CreatedDate = DateTime.UtcNow,
            BusinessUnitId = Tenant,
            LeadStatusId = qualifiedId,
            NoOfLineItems = 1
        };
        lead.LeadItems.Add(item);
        owner.Leads.Add(lead);
        await owner.SaveChangesAsync();
        lead.ResolveCommercialIdentity(CustomerId, null, "CUSTOMER_CONFIRMED");
        await owner.SaveChangesAsync();
        return lead.Id;
    }

    /// <summary>One quotable line, complete apart from wherever the caller puts the code.</summary>
    private static LeadItem Line() => new()
    {
        LineItemNo = "00010",
        Quantity = 4,
        UnitOfMeasure = "EA",
        Currency = "SAR"
    };

    private static LeadItem LineCarrying(string field, string value)
    {
        var item = Line();
        switch (field)
        {
            case "ItemMaterialCode":
                item.ItemMaterialCode = value;
                item.ProductShortDescription = "GASKET:SPIRAL WOUND,2 IN,CL300";
                break;
            case "ManufacturerPartNumber":
                item.ManufacturerPartNumber = value;
                item.ProductShortDescription = "GASKET:SPIRAL WOUND,2 IN,CL300";
                break;
            case "AlternatePartNumber":
                item.AlternatePartNumber = value;
                item.ProductShortDescription = "GASKET:SPIRAL WOUND,2 IN,CL300";
                break;
            // The two placements that produce a lead 479: a document whose code column had no
            // heading the parser recognised, so the number arrived as the line's own text.
            case "ProductShortName": item.ProductShortName = value; break;
            case "ProductShortDescription": item.ProductShortDescription = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(field), field, "unknown line field");
        }
        return item;
    }

    private async Task<ConversionPreviewItem> PreviewLineAsync(long leadId)
    {
        await using var ctx = _database.TenantContextWithRls(Tenant);
        var preview = await new LeadConversionIntelligence(ctx).PreviewAsync(leadId, Tenant, default);
        return preview.Items.Single();
    }

    /// <summary>Converts through the entry point the "Create RFQ" button reaches, and reads back
    /// the product actually written onto the RFQ line — not the preview's opinion of it.</summary>
    private async Task<long?> ConvertAndReadProductIdAsync(long leadId)
    {
        long rfqId;
        await using (var ctx = _database.TenantContextWithRls(Tenant))
            rfqId = await new LeadConversionIntelligence(ctx).ConvertAsync(
                leadId, Tenant, new ConvertRequest { ActingUser = "sara@nexora.sa" }, default);

        await using var owner = _database.ContextFor(null);
        return await owner.Rfqitems.AsNoTracking()
            .Where(i => i.Rfqid == rfqId)
            .Select(i => i.ProductId)
            .SingleAsync();
    }

    // ------------------------------------------------------------------ the ladder

    /// <summary>
    /// GUARD-PIN, not a regression: this rung already worked before the fix and is asserted so it
    /// keeps working. It is also the control that proves the fixture itself can match — without it
    /// a red result below could just mean the seed is wrong.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task An_exact_buyer_material_code_scores_one_and_auto_assigns()
    {
        var productId = await CatalogProductAsync("A2A50006470");
        var leadId = await QualifiedLeadAsync(LineCarrying("ItemMaterialCode", "A2A50006470"));

        var line = await PreviewLineAsync(leadId);
        Assert.Equal(1.00m, line.Confidence);
        Assert.Equal("Matched by material code", line.Matches[0].Reason);
        Assert.Equal(productId, line.BestMatchProductId);
        Assert.Equal(productId, await ConvertAndReadProductIdAsync(leadId));
    }

    /// <summary>
    /// The defect proper. The same buyer material number, written the way five different ingestion
    /// doors leave it, must reach the same catalogue row every time. Each case seeds its own
    /// catalogue number so the cases cannot mask one another.
    /// </summary>
    [Theory]
    [Trait("Category", "PostgreSQL")]
    // pre-fix: 1.00 — the one placement that worked
    [InlineData("ItemMaterialCode", "A2A50006471", "A2A50006471")]
    // pre-fix: 0.85 — buyer wrote the dashes the catalogue does not carry
    [InlineData("ItemMaterialCode", "A2A50006472", "A2A-50006472")]
    // pre-fix: 0.85 — buyer spaced it
    [InlineData("ItemMaterialCode", "A2A50006473", "A2A 50006473")]
    // pre-fix: 0.95 — the spreadsheet door's landing field, and it worked
    [InlineData("ManufacturerPartNumber", "A2A50006474", "A2A50006474")]
    // pre-fix: 0.85 — same field, punctuated
    [InlineData("ManufacturerPartNumber", "A2A50006475", "A2A-50006475")]
    // pre-fix: 0.85 — a part-number field that was never compared to the catalogue at all
    [InlineData("AlternatePartNumber", "A2A50006476", "A2A50006476")]
    // pre-fix: 0.00 — no candidate even fetched
    [InlineData("ProductShortName", "A2A50006477", "A2A50006477")]
    // pre-fix: 0.00 — no candidate even fetched
    [InlineData("ProductShortDescription", "A2A50006478", "A2A50006478")]
    public async Task A_catalog_number_is_recognised_from_whichever_field_carried_it(
        string field, string catalogPartNo, string asWrittenOnTheLine)
    {
        var productId = await CatalogProductAsync(catalogPartNo);
        var leadId = await QualifiedLeadAsync(LineCarrying(field, asWrittenOnTheLine));

        var line = await PreviewLineAsync(leadId);

        Assert.Equal(productId, line.BestMatchProductId);
        // >= the floor is the whole point: below it the line needs a human click before it can be
        // quoted, which is the symptom this closes.
        Assert.True(line.Confidence >= 0.90m,
            $"{field} carrying \"{asWrittenOnTheLine}\" against catalogue \"{catalogPartNo}\" " +
            $"scored {line.Confidence}, below the 0.90 auto-assign floor.");
        Assert.DoesNotContain("Name similarity", line.Matches[0].Reason);
    }

    /// <summary>
    /// The wiring, not the unit. A matched line must also (a) not trip the warning governance gate,
    /// which would refuse the conversion outright and force an operator to type a batch
    /// acknowledgement, and (b) land its ProductId on the persisted RFQ line.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_recognised_code_converts_without_an_acknowledgement_and_reaches_the_rfq_line()
    {
        var productId = await CatalogProductAsync("A2A50006480");
        // The hardest live shape: punctuated, and sitting in the description because the document's
        // code column had no heading the parser knew.
        var leadId = await QualifiedLeadAsync(LineCarrying("ProductShortDescription", "A2A-50006480"));

        var line = await PreviewLineAsync(leadId);
        Assert.False(line.NeedsAttention, $"line still flagged: {line.AttentionReason}");

        Assert.Equal(productId, await ConvertAndReadProductIdAsync(leadId));
    }

    // ------------------------------------------------------------------ negative controls
    //
    // These are what make the ladder above evidence rather than decoration: they fail if the fix
    // widened the match by lowering the bar instead of by reading more fields.

    /// <summary>
    /// Prose is not a code. A description that happens to share tokens with a catalogue product
    /// must stay in the similarity band, below the floor, and the conversion must still refuse
    /// without an acknowledgement.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Prose_in_the_description_never_counts_as_a_catalog_number()
    {
        await CatalogProductAsync("A2A50006481", name: "Gasket, spiral wound, 2IN CL300");
        var leadId = await QualifiedLeadAsync(
            LineCarrying("ProductShortDescription", "GASKET SPIRAL WOUND 2IN CL300 SUPPLY"));

        var line = await PreviewLineAsync(leadId);
        Assert.True(line.Confidence < 0.90m,
            $"prose scored {line.Confidence} and would have auto-assigned.");
        Assert.True(line.NeedsAttention);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConvertAndReadProductIdAsync(leadId));
        Assert.Contains("acknowledged", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A code is only folded, never guessed. "A2A-99999999" shares a prefix and a shape with
    /// "A2A50006482" and must match nothing.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_code_that_matches_no_catalog_row_still_refuses_to_auto_assign()
    {
        await CatalogProductAsync("A2A50006482");
        var leadId = await QualifiedLeadAsync(LineCarrying("ItemMaterialCode", "A2A-99999999"));

        var line = await PreviewLineAsync(leadId);
        Assert.True(line.Confidence < 0.90m,
            $"an unmatched code scored {line.Confidence} and would have auto-assigned.");
    }

    /// <summary>
    /// A short token is not a code. Folding "2 IN" to "2IN" must not let a size become an
    /// identifier that binds a catalogue row.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_short_token_is_not_treated_as_a_catalog_number()
    {
        await CatalogProductAsync("2IN", name: "Two inch nominal");
        var leadId = await QualifiedLeadAsync(LineCarrying("ProductShortName", "2IN"));

        var line = await PreviewLineAsync(leadId);
        Assert.True(line.Confidence < 0.90m,
            $"a four-character token scored {line.Confidence} and would have auto-assigned.");
    }
}
