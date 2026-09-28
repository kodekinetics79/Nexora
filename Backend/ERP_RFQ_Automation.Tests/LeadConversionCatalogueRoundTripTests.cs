using System.Data.Common;
using System.Globalization;
using ERP_RFQ_Automation.CommercialCases.Participation;
using ERP_RFQ_Automation.Intelligence.Conversion;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Sla;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// PERF-01 (pilot audit 2026-09-28). Decide on the 1,500-line Aramco request (lead 15) took
/// 110–148 s under load and ~3 s quiet: the catalogue fallback in
/// <see cref="LeadConversionIntelligence"/> ran ONE ILIKE query per distinct line name — 1,295 of
/// the 1,326 SQL commands one workbench read issued. The catalogue is now read once per call and
/// the same per-name candidate rule (two longest tokens, first 40 products by id) is applied in
/// memory.
///
/// <para>Two things are pinned. The matches are the SAME as the per-name queries produced: the
/// golden table below was captured from the pre-change implementation (base commit 6d8bf4c0) on
/// this fixture, which exercises the exact-code rungs, the folded rung, name equality, the
/// 40-candidate bound, inactive/NULL-active/foreign catalogue rows and currency noise. And the number of
/// database round trips no longer grows with the number of lines.</para>
/// </summary>
public sealed class LeadConversionCatalogueRoundTripTests
{
    private const long Tenant = 7_301;
    private const long Foreign = 7_302;
    private static readonly DateTime Now = new(2026, 9, 28, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Catalogue_matches_are_identical_to_the_per_name_query_implementation()
    {
        using var db = new TestDb();
        var leadId = await SeedFixtureAsync(db);

        await using var context = db.ContextFor(Tenant);
        var preview = await new LeadConversionIntelligence(context).PreviewAsync(leadId, Tenant, default);
        var partNoById = await context.Products.IgnoreQueryFilters().AsNoTracking()
            .ToDictionaryAsync(p => p.Id, p => p.PartNo);

        var actual = preview.Items.Select((item, index) => Describe(index, item, partNoById)).ToArray();

        Assert.Equal(Golden, actual);
    }

    [Fact]
    public async Task Database_round_trips_do_not_grow_with_the_number_of_lines()
    {
        using var db = new TestDb();
        await SeedCatalogueAsync(db);
        var fewLines = await SeedLeadAsync(db, 9_001, DistinctNamedLines(5));
        var manyLines = await SeedLeadAsync(db, 9_002, DistinctNamedLines(60));

        var few = await CountPreviewCommandsAsync(db, fewLines);
        var many = await CountPreviewCommandsAsync(db, manyLines);

        // Before: 5 + k versus 60 + k (one ILIKE per distinct line name).
        Assert.Equal(few, many);
        Assert.True(many <= 6, $"a preview issued {many} commands; the catalogue should be read once.");
    }

    /// <summary>
    /// The number the owner asked for: SQL commands for ONE decision-workbench read of a
    /// 1,500-line request. Before the change the fallback alone issued one query per distinct
    /// line name (1,500 here), exactly the lead-15 shape in the audit log (1,326 commands).
    /// </summary>
    [Fact]
    public async Task A_1500_line_workbench_read_issues_a_constant_number_of_commands()
    {
        using var db = new TestDb();
        await SeedCatalogueAsync(db);
        var small = await ReconciledLeadAsync(db, 6_901, 10);
        var large = await ReconciledLeadAsync(db, 6_902, 1_500);

        var smallCommands = await CountWorkbenchCommandsAsync(db, small);
        var largeCommands = await CountWorkbenchCommandsAsync(db, large);

        Console.WriteLine($"decision-workbench SQL commands: 10 lines = {smallCommands}, 1,500 lines = {largeCommands}");
        Assert.Equal(smallCommands, largeCommands);
        Assert.True(largeCommands < 60, $"a 1,500-line workbench read issued {largeCommands} commands.");
    }

    // ------------------------------------------------------------------ golden

    /// <summary>
    /// Captured from the per-name-query implementation. Format:
    /// line | confidence | needs attention | reason | top matches (catalogue PartNo@score:reason).
    /// </summary>
    private static readonly string[] Golden =
    [
        "1 | 1.00 | False |  | best=A2A50006470 | A2A50006470@1.00:Matched by material code; WID-1@0.51:Name similarity 25%",
        "2 | 0.93 | False |  | best=VLV-100 | VLV-100@0.93:Matched by manufacturer part number",
        "3 | 0.90 | False |  | best=VLV-100 | VLV-100@0.90:Exact product name match",
        "4 | 0.85 | True | Low-confidence match (85%) | best=BOLT-012 | BOLT-012@0.85:Name similarity 100%; BOLT-001@0.74:Name similarity 75%; BOLT-002@0.74:Name similarity 75%",
        "5 | 0.85 | True | Low-confidence match (85%) | best=BOLT-012 | BOLT-012@0.85:Name similarity 100%; BOLT-001@0.74:Name similarity 75%; BOLT-002@0.74:Name similarity 75%",
        "6 | 0.85 | True | Low-confidence match (85%) | best=GAUGE-10 | GAUGE-10@0.85:Name similarity 100%",
        "7 | 0.55 | True | Low-confidence match (55%) | best=BOLT-020 | BOLT-020@0.55:Name similarity 33%; TIE-NULL@0.55:Name similarity 33%",
        "8 | 0.70 | True | Low-confidence match (70%) | best=TIE-NULL | TIE-NULL@0.70:Name similarity 67%",
        "9 | 0.85 | True | Low-confidence match (85%) | best=WID-1 | WID-1@0.85:Name similarity 100%",
        "10 | 0.00 | True | No catalog match found | best=- | ",
        "11 | 0.00 | True | No catalog match found | best=- | ",
        "12 | 0.76 | True | Low-confidence match (76%) | best=RLY-1 | RLY-1@0.76:Name similarity 80%",
        "13 | 0.85 | True | Low-confidence match (85%) | best=GAUGE-10 | GAUGE-10@0.85:Name similarity 100%",
        "14 | 0.85 | True | Low-confidence match (85%) | best=WID-1 | WID-1@0.85:Name similarity 100%; A2A50006470@0.62:Name similarity 50%",
        "15 | 0.90 | False |  | best=A2A50006470 | A2A50006470@0.90:Matched by catalog number in the line text",
        "16 | 0.93 | False |  | best=VLV-100 | VLV-100@0.93:Matched by alternate part number",
        "17 | 0.85 | True | Quantity missing; Unit of measure missing; Low-confidence match (85%) | best=BOLT-001 | BOLT-001@0.85:Name similarity 100%; BOLT-002@0.85:Name similarity 100%; BOLT-003@0.85:Name similarity 100%",
        "18 | 0.76 | True | Low-confidence match (76%); Unit of measure \"PACK\" needs review — packaging unit — confirm how many items it contains before quoting | best=WID-1 | WID-1@0.76:Name similarity 80%",
        "19 | 0.74 | True | Low-confidence match (74%) | best=BOLT-001 | BOLT-001@0.74:Name similarity 75%; BOLT-002@0.74:Name similarity 75%; BOLT-003@0.74:Name similarity 75%",
    ];

    private static string Describe(int index, ConversionPreviewItem item, IReadOnlyDictionary<long, string> partNoById)
    {
        var matches = string.Join("; ", item.Matches.Select(m =>
            $"{partNoById[m.ProductId]}@{m.Score.ToString("0.00", CultureInfo.InvariantCulture)}:{m.Reason}"));
        var best = item.BestMatchProductId is { } id ? partNoById[id] : "-";
        return $"{index + 1} | {item.Confidence.ToString("0.00", CultureInfo.InvariantCulture)} | "
               + $"{item.NeedsAttention} | {item.AttentionReason} | best={best} | {matches}";
    }

    // ------------------------------------------------------------------ fixture

    private static async Task<long> SeedFixtureAsync(TestDb db)
    {
        await SeedCatalogueAsync(db);
        return await SeedLeadAsync(db, 9_000,
        [
            Line(itemMaterialCode: "A2A50006470", description: "GASKET:SPIRAL WOUND,2 IN,CL300"),
            Line(mpn: "BV100SS", description: "BALL VALVE"),
            Line(name: "Ball valve stainless 1in"),
            Line(name: "HEX BOLT GALVANISED M12"),
            Line(name: "hex bolt, galvanised m12"),
            Line(description: "Pressure gauge glycerine"),
            Line(name: "cable gland M20"),
            Line(name: "Cable tie 300"),
            Line(name: "Flange kit"),
            Line(name: "zz"),
            Line(),
            Line(name: "Relay module"),
            Line(name: "gauge"),
            Line(name: "USD 45 Gasket kit $"),
            Line(name: "A2A-50006470"),
            Line(alternate: "VLV-100", description: "valve"),
            Line(name: "Bolt", quantity: null, unit: null),
            Line(name: "Widget spares", unit: "PACK"),
            // Its own product is the 45th "bolt"/"galvanised" row: outside the first 40 by id, so
            // the per-name bound never fetched it and the line matched no exact name.
            Line(name: "Hex bolt M45 galvanised"),
        ]);
    }

    private static async Task SeedCatalogueAsync(TestDb db)
    {
        await using var owner = db.ContextFor(null);
        Seed.BusinessUnit(owner, Tenant);
        Seed.BusinessUnit(owner, Foreign);
        await owner.SaveChangesAsync();

        var products = new List<Product>
        {
            Product("A2A50006470", "Gasket, spiral wound, 2IN CL300"),
            Product("VLV-100", "Ball valve stainless 1in", modelNo: "BV100-SS"),
            Product("GAUGE-10", "Pressure gauge 0-10 bar", description: "Bourdon tube gauge glycerine filled"),
            Product("RLY-1", "Relay"),
            Product("WID-1", "Widget", description: "Flange gasket kit"),
            Product("GLAND-INACTIVE", "Cable gland nylon M20", active: false),
            Product("GLAND-FOREIGN", "Cable gland stainless M20", buid: Foreign),
        };
        // More products carrying the same tokens than one name may pull in (40), so the bound
        // and its id order are part of what the golden table pins.
        for (var i = 1; i <= 45; i++)
            products.Add(Product($"BOLT-{i:000}", $"Hex bolt M{i} galvanised"));
        owner.Products.AddRange(products);
        await owner.SaveChangesAsync();

        // A legacy row shape EF will not write itself: IsActive NULL (EF would send the column
        // default instead). It is read as active.
        await owner.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"Products\" (\"BUID\", \"PartNo\", \"ProductName\", \"QtyOnHand\", \"ReorderPoint\", \"IsActive\", \"CreatedBy\", \"CreatedOn\") "
            + "VALUES ({1}, 'TIE-NULL', 'Cable tie black 300mm', 0, 0, NULL, 'qa', {0})", Now, Tenant);
    }

    private static async Task<long> SeedLeadAsync(TestDb db, long leadId, IEnumerable<LeadItem> items)
    {
        await using var owner = db.ContextFor(null);
        Seed.Lead(owner, leadId, Tenant, items: items);
        await owner.SaveChangesAsync();
        return leadId;
    }

    private static IEnumerable<LeadItem> DistinctNamedLines(int count) =>
        Enumerable.Range(1, count).Select(i => Line(name: $"Distinct item {Words(i)} widget"));

    /// <summary>A number spelled as letters, so every line name has its own longest token.</summary>
    private static string Words(int value)
    {
        var letters = new char[6];
        for (var i = letters.Length - 1; i >= 0; i--) { letters[i] = (char)('a' + value % 26); value /= 26; }
        return new string(letters);
    }

    private static LeadItem Line(string? name = null, string? description = null, string? itemMaterialCode = null,
        string? mpn = null, string? alternate = null, decimal? quantity = 2, string? unit = "EA") => new()
    {
        ProductShortName = name,
        ProductShortDescription = description,
        ItemMaterialCode = itemMaterialCode,
        ManufacturerPartNumber = mpn,
        AlternatePartNumber = alternate,
        Quantity = quantity,
        UnitOfMeasure = unit,
    };

    private static Product Product(string partNo, string name, string? modelNo = null, string? description = null,
        long buid = Tenant, bool active = true) => new()
    {
        Buid = buid,
        PartNo = partNo,
        ModelNo = modelNo,
        ProductName = name,
        Description = description,
        QtyOnHand = 0m,
        ReorderPoint = 0m,
        IsActive = active,
        CreatedBy = "qa",
        CreatedOn = Now,
    };

    private static async Task<long> CountPreviewCommandsAsync(TestDb db, long leadId)
    {
        var counter = new CommandCounter();
        await using var context = db.ContextFor(Tenant, counter);
        await new LeadConversionIntelligence(context).PreviewAsync(leadId, Tenant, default);
        return counter.Commands;
    }

    private static async Task<long> CountWorkbenchCommandsAsync(TestDb db, long leadId)
    {
        var counter = new CommandCounter();
        await using var context = db.ContextFor(Tenant, counter);
        var workbench = await new LeadDecisionWorkbenchService(context, new LeadOutcomeReasons(context))
            .GetAsync(Tenant, leadId);
        Assert.NotEmpty(workbench.Lines);
        return counter.Commands;
    }

    private static async Task<long> ReconciledLeadAsync(TestDb db, long ingestId, int lines)
    {
        await using var context = db.ContextFor(Tenant);
        Seed.EmailConfig(context, ingestId + 50_000, Tenant);
        Seed.EmailIngest(context, ingestId, ingestId + 50_000, "NeedsReview");
        await context.SaveChangesAsync();
        var lead = new Lead
        {
            Rfqno = $"RFQ-{ingestId}", BuyersName = "Buyer", RecDate = DateTime.UtcNow, LeadSource = "ManualUpload",
            CreatedBy = "test", CreatedDate = DateTime.UtcNow, BusinessUnitId = Tenant, EmailIngestsId = ingestId,
            Clientemail = $"buyer{ingestId}@example.test", RequiresCommercialReview = true
        };
        for (var i = 1; i <= lines; i++)
            lead.LeadItems.Add(new LeadItem
            {
                LineItemNo = i.ToString(CultureInfo.InvariantCulture),
                ProductShortDescription = $"Line {Words(i)} spare part {i}",
                Quantity = 1,
                UnitOfMeasure = "EA",
            });
        var created = await new LeadIdentityApplicationService(context).ReconcileAsync(lead, new LeadIntakeDescriptor(
            Guid.NewGuid(), "ManualUpload", $"perf-{ingestId}", null, null, "test", $"buyer{ingestId}@example.test",
            "RFQ", $"perf-{ingestId}.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", 100,
            $"perf-hash-{ingestId}".PadRight(64, '0')[..64], null, null, null, DateTimeOffset.UtcNow,
            LeadProcessingPath.Deterministic, false, 0, "User", "tester", $"test:perf-{ingestId}"));
        return created.LeadId;
    }

    /// <summary>Counts every command EF sends to the database, on this context only.</summary>
    private sealed class CommandCounter : DbCommandInterceptor
    {
        private long _commands;

        public long Commands => Interlocked.Read(ref _commands);

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        { Interlocked.Increment(ref _commands); return result; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref _commands); return ValueTask.FromResult(result); }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        { Interlocked.Increment(ref _commands); return result; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref _commands); return ValueTask.FromResult(result); }

        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        { Interlocked.Increment(ref _commands); return result; }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref _commands); return ValueTask.FromResult(result); }
    }
}
