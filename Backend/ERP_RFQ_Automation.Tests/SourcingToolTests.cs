using System.Text.Json;
using ERP_RFQ_Automation.Agent;
using ERP_RFQ_Automation.Agent.Guardrails;
using ERP_RFQ_Automation.Agent.Models;
using ERP_RFQ_Automation.Agent.Tools;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Procurement;
using ERP_RFQ_Automation.SupplierEvaluation;
using ERP_RFQ_Automation.Tests.Support;

namespace ERP_RFQ_Automation.Tests;

public sealed class SourcingToolTests
{
    private const long Bu1 = 1;
    private const long RfqId = 700;
    private const long SupplierId = 500;
    private static readonly AgentToolContext AgentContext = new()
        { BusinessUnitId = Bu1, UserId = 42, UserName = "tester" };

    [Fact]
    public async Task SendRfqToSuppliers_FailsClosed_AndRequiresExplicitSourcingCaseApproval()
    {
        using var db = new TestDb();
        SeedGraph(db, includeSolicitation: false);
        var service = new RecordingProcurementService();

        using var context = db.ContextFor(Bu1);
        var tool = new SendRfqToSuppliersTool(context, service);
        var result = await tool.ExecuteAsync(AgentSeed.Json(
            "{\"rfqId\":700,\"supplierIds\":[500],\"message\":\"First wording\"}"), AgentContext, default);

        Assert.False(result.Success);
        Assert.Contains("explicitly approve", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(service.Solicitations);
    }

    [Fact]
    public async Task CaptureSupplierQuote_AlwaysFailsClosedWithoutCallingService()
    {
        using var db = new TestDb();
        SeedGraph(db, includeSolicitation: true);
        var service = new RecordingProcurementService();

        using var context = db.ContextFor(Bu1);
        var tool = new CaptureSupplierQuoteTool(context, service);
        const string forgedApproval = "{\"rfqId\":700,\"supplierId\":500," +
            "\"supplierQuoteReference\":\"SUP-Q-9\",\"evidenceReference\":" +
            "\"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"," +
            "\"humanAuthorized\":true,\"revision\":2,\"validUntil\":\"2030-01-01T00:00:00Z\"," +
            "\"lines\":[{\"rfqItemId\":7001,\"unitPrice\":12.5,\"quantity\":20,\"leadTimeDays\":14," +
            "\"currency\":1,\"availableQuantity\":20,\"reliabilitySnapshot\":95}]}";

        var forged = await tool.ExecuteAsync(AgentSeed.Json(forgedApproval), AgentContext, default);
        var empty = await tool.ExecuteAsync(AgentSeed.Json("{}"), AgentContext, default);

        Assert.False(forged.Success);
        Assert.False(empty.Success);
        Assert.Contains("disabled", forged.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("non-forgeable", forged.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(service.Quotes);
    }

    [Fact]
    public async Task CompareSupplierQuotes_UsesExplicitTenantRfqAndLineLineage()
    {
        using var db = new TestDb();
        SeedGraph(db, includeSolicitation: true, includeQuote: true);

        using var context = db.ContextFor(Bu1);
        var tool = new CompareSupplierQuotesTool(context);
        var result = await tool.ExecuteAsync(AgentSeed.Json("{\"rfqId\":700}"), AgentContext, default);

        Assert.True(result.Success, result.Error);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result.Data));
        Assert.Equal(1, document.RootElement.GetProperty("lineCount").GetInt32());
        var line = Assert.Single(document.RootElement.GetProperty("lines").EnumerateArray());
        Assert.Equal(7001, line.GetProperty("rfqItemId").GetInt64());

        // The lineage this test is about: one line, one bid, from the seeded supplier.
        var bid = Assert.Single(line.GetProperty("bids").EnumerateArray());
        Assert.Equal(SupplierId, bid.GetProperty("SupplierId").GetInt64());
        Assert.Equal(2m, bid.GetProperty("LandedUnitCost").GetDecimal());

        // …and it is NOT crowned. A weighted score ranks offers against each other, and there is
        // only one offer here: min-max normalisation over a set of one gave it the full weight of
        // every criterion and named it "best" on a perfect score it was never compared for. It is
        // reported, it is quoted, and a human may still award it — it simply is not ranked.
        Assert.Equal(JsonValueKind.Null, line.GetProperty("bestSupplierId").ValueKind);
        Assert.Contains("one comparable offer",
            line.GetProperty("bestUnavailableReason").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CompareSupplierQuotes_ExcludesExpiredQuotes_AndRejectsMixedCurrencies()
    {
        using var db = new TestDb();
        SeedGraph(db, includeSolicitation: true, includeQuote: true);
        using (var seed = db.ContextFor(null))
        {
            var quote = seed.SupplierQuotedItems.Single(row => row.Id == 9001);
            quote.ValidUntil = DateTime.UtcNow.AddDays(-1);
            await seed.SaveChangesAsync();
        }

        using (var context = db.ContextFor(Bu1))
        {
            var expired = await new CompareSupplierQuotesTool(context).ExecuteAsync(
                AgentSeed.Json("{\"rfqId\":700}"), AgentContext, default);
            Assert.False(expired.Success);
            Assert.Contains("eligible", expired.Error, StringComparison.OrdinalIgnoreCase);
        }

        using (var seed = db.ContextFor(null))
        {
            var quote = seed.SupplierQuotedItems.Single(row => row.Id == 9001);
            quote.ValidUntil = DateTime.UtcNow.AddDays(1);
            seed.Currencies.Add(new Currency
            {
                Id = 2, BusinessUnitId = Bu1, Code = "EUR", CurrencyName = "Euro",
                IsActive = true, CreatedBy = "seed", CreatedOn = AgentSeed.Now
            });
            seed.SupplierQuotedItems.Add(new SupplierQuotedItem
            {
                Id = 9002, BusinessUnitId = Bu1, SupplierId = SupplierId,
                SupplierSolicitationId = 1, RfqId = RfqId, RfqItemId = 7001,
                Quantity = 25, UnitPrice = 1, LandedUnitCost = 1, CurrencyId = 2,
                AvailableQuantity = 25, LeadTimeDays = 3,
                QuoteReference = "SUP-Q-2", ValidUntil = DateTime.UtcNow.AddDays(1),
                ResponseIdempotencyKey = "seed-quote:9002", RequestHash = new string('1', 64),
                QuoteRevision = 1, Version = 1, IsActive = true,
                CreatedBy = "seed", CreatedDate = AgentSeed.Now
            });
            await seed.SaveChangesAsync();
        }

        using (var context = db.ContextFor(Bu1))
        {
            var mixed = await new CompareSupplierQuotesTool(context).ExecuteAsync(
                AgentSeed.Json("{\"rfqId\":700}"), AgentContext, default);
            Assert.False(mixed.Success);
            Assert.Contains("currency", mixed.Error, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task CompareSupplierQuotes_RanksOnlyQuotesMeetingQuantityMoqAndLeadTimeRequirements()
    {
        using var db = new TestDb();
        SeedGraph(db, includeSolicitation: true, includeQuote: true);
        using (var seed = db.ContextFor(null))
        {
            seed.SupplierQuotedItems.AddRange(
                Quote(9002, available: 24, moq: null, leadTimeDays: 1),
                Quote(9003, available: 25, moq: 30, leadTimeDays: 1),
                Quote(9004, available: 25, moq: null, leadTimeDays: null),
                Quote(9005, available: 25, moq: null, leadTimeDays: 1,
                    validUntil: DateTime.UtcNow.AddMinutes(-1)));
            await seed.SaveChangesAsync();
        }

        using var context = db.ContextFor(Bu1);
        var result = await new CompareSupplierQuotesTool(context).ExecuteAsync(
            AgentSeed.Json("{\"rfqId\":700}"), AgentContext, default);

        Assert.True(result.Success, result.Error);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result.Data));
        var line = Assert.Single(document.RootElement.GetProperty("lines").EnumerateArray());
        var bid = Assert.Single(line.GetProperty("bids").EnumerateArray());
        Assert.Equal(SupplierId, bid.GetProperty("SupplierId").GetInt64());
        Assert.Equal(5, bid.GetProperty("leadTimeDays").GetInt32());
        Assert.Equal(50m, bid.GetProperty("lineTotal").GetDecimal());
    }

    [Fact]
    public async Task CompareSupplierQuotes_RejectsMissingCurrency()
    {
        using var db = new TestDb();
        SeedGraph(db, includeSolicitation: true, includeQuote: true);
        using (var seed = db.ContextFor(null))
        {
            seed.SupplierQuotedItems.Single(row => row.Id == 9001).CurrencyId = null;
            await seed.SaveChangesAsync();
        }

        using var context = db.ContextFor(Bu1);
        var result = await new CompareSupplierQuotesTool(context).ExecuteAsync(
            AgentSeed.Json("{\"rfqId\":700}"), AgentContext, default);

        Assert.False(result.Success);
        Assert.Contains("currency", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gate 2 collapsed two recommenders into one. This holds them collapsed on the criterion that
    /// could most easily split them again: the agent tool used to hand the scorer a hardcoded null
    /// for warranty, so the moment a tenant gave warranty a non-zero weight the workbench ranked the
    /// line and the agent refused to — and told the operator to capture a number that was already
    /// captured on the canonical quote line.
    ///
    /// <para>Same offers, same tenant weights, same winner, and the same arithmetic behind it.</para>
    /// </summary>
    [Fact]
    public async Task CompareSupplierQuotes_NamesTheSameWinnerAsTheGovernedComparison_WhenWarrantyCarriesWeight()
    {
        using var fixture = new ProcurementScenario();
        await AddEligibleSupplierAsync(fixture, LongWarrantySupplierId, "Long Warranty Supplier");
        await SetWeightsAsync(fixture, price: 30, leadTime: 10, warranty: 60, paymentTerms: 0,
            key: "agent-warranty-weighted");

        // Cheaper AND faster, but one year of warranty against five. Only the warranty weight can
        // move the award off it, which is exactly what a tenant that weights warranty is asking for.
        var cheapShortWarranty = await QuoteAsync(fixture, ProcurementTestData.Supplier, "cheap",
            unitPrice: 12m, leadTimeDays: 5, warrantyMonths: 12);
        var dearLongWarranty = await QuoteAsync(fixture, LongWarrantySupplierId, "durable",
            unitPrice: 20m, leadTimeDays: 10, warrantyMonths: 60);

        var governed = await fixture.Execute(service =>
            service.CompareQuotesAsync(fixture.BusinessUnitId, fixture.RfqItemId));
        Assert.Equal(dearLongWarranty, governed.RecommendedSupplierQuotedItemId);
        var governedWinner = governed.Lines
            .Single(x => x.SupplierQuotedItemId == dearLongWarranty);
        var governedLoser = governed.Lines
            .Single(x => x.SupplierQuotedItemId == cheapShortWarranty);

        var line = await CompareLineAsync(fixture);

        // The agent names the supplier the workbench named, not the cheapest one.
        Assert.Equal(governedWinner.SupplierId, line.GetProperty("bestSupplierId").GetInt64());
        Assert.Equal(LongWarrantySupplierId, line.GetProperty("bestSupplierId").GetInt64());

        // …and it got there on the same numbers, not by coincidence of ordering: the warranty months
        // are the ones the operator typed, and the scores match the governed ones.
        var winnerBid = Bid(line, governedWinner.SupplierId);
        var loserBid = Bid(line, governedLoser.SupplierId);
        Assert.Equal(60, winnerBid.GetProperty("warrantyMonths").GetInt32());
        Assert.Equal(12, loserBid.GetProperty("warrantyMonths").GetInt32());
        Assert.Equal(governedWinner.WeightedScore!.Value, winnerBid.GetProperty("Score").GetDouble(), 2);
        Assert.Equal(governedLoser.WeightedScore!.Value, loserBid.GetProperty("Score").GetDouble(), 2);
    }

    /// <summary>
    /// Ruling R-F, held on the agent path too. An offer whose warranty nobody captured is not the
    /// offer with the worst warranty — it is an offer this tool cannot rank. Scoring it zero would
    /// have sorted the cheapest, fastest bid on the line last as though it had lost on the merits.
    /// </summary>
    [Fact]
    public async Task CompareSupplierQuotes_LeavesAnUncapturedWarrantyUnscored_RatherThanScoringItZero()
    {
        using var fixture = new ProcurementScenario();
        await AddEligibleSupplierAsync(fixture, LongWarrantySupplierId, "Second Supplier");
        await AddEligibleSupplierAsync(fixture, UncapturedWarrantySupplierId, "Uncaptured Warranty Supplier");
        await SetWeightsAsync(fixture, price: 40, leadTime: 40, warranty: 20, paymentTerms: 0,
            key: "agent-warranty-missing");

        await QuoteAsync(fixture, ProcurementTestData.Supplier, "scored",
            unitPrice: 12m, leadTimeDays: 5, warrantyMonths: 24);
        await QuoteAsync(fixture, LongWarrantySupplierId, "also-scored",
            unitPrice: 15m, leadTimeDays: 9, warrantyMonths: 12);
        // Deliberately the cheapest and the fastest offer on the line.
        await QuoteAsync(fixture, UncapturedWarrantySupplierId, "uncaptured",
            unitPrice: 8m, leadTimeDays: 3, warrantyMonths: null);

        var line = await CompareLineAsync(fixture);

        // The two captured offers are ranked, and the cheaper, faster one of them wins.
        Assert.Equal(ProcurementTestData.Supplier, line.GetProperty("bestSupplierId").GetInt64());
        Assert.Equal(JsonValueKind.Number, Bid(line, ProcurementTestData.Supplier).GetProperty("Score").ValueKind);
        Assert.Equal(JsonValueKind.Number, Bid(line, LongWarrantySupplierId).GetProperty("Score").ValueKind);

        // The uncaptured one carries no score and no zero, and says which criterion is missing.
        var uncaptured = Bid(line, UncapturedWarrantySupplierId);
        Assert.Equal(JsonValueKind.Null, uncaptured.GetProperty("Score").ValueKind);
        Assert.Equal(JsonValueKind.Null, uncaptured.GetProperty("warrantyMonths").ValueKind);
        var reason = uncaptured.GetProperty("scoreUnavailableReason").GetString()!;
        Assert.Contains("Cannot score", reason, StringComparison.Ordinal);
        Assert.Contains("warranty", reason, StringComparison.Ordinal);
        Assert.All(uncaptured.GetProperty("scoreBreakdown").EnumerateArray(), contribution =>
            Assert.Equal(JsonValueKind.Null, contribution.GetProperty("PointsEarned").ValueKind));

        // Still listed and still quoted — it is simply sorted below the offers that could be ranked
        // rather than mixed in among them.
        var bids = line.GetProperty("bids").EnumerateArray().ToArray();
        Assert.Equal(3, bids.Length);
        Assert.Equal(UncapturedWarrantySupplierId, bids[^1].GetProperty("SupplierId").GetInt64());
    }

    [Fact]
    public async Task AwardRfq_RequiresQuoteIdentity_AndRoutesSingleAward()
    {
        using var db = new TestDb();
        SeedGraph(db, includeSolicitation: true, includeQuote: true);
        var service = new RecordingProcurementService();

        using var context = db.ContextFor(Bu1);
        var tool = new AwardRfqTool(context, service);
        var result = await tool.ExecuteAsync(AgentSeed.Json(
            "{\"rfqId\":700,\"rationale\":\"Best landed cost\",\"awards\":[{" +
            "\"supplierQuotedItemId\":9001,\"expectedQuoteVersion\":1,\"quantity\":10}]}"), AgentContext, default);

        Assert.True(result.Success, result.Error);
        var command = Assert.Single(service.Awards);
        Assert.Equal(9001, command.SupplierQuotedItemId);
        Assert.Equal(1, command.ExpectedQuoteVersion);
        Assert.Equal(10m, command.Quantity);
        Assert.Equal(42, command.AwardedByUserId);
        Assert.Equal("Best landed cost", command.Rationale);
    }

    [Fact]
    public async Task AwardRfq_RejectsLegacyUnlinkedOrMultiAwardInput()
    {
        using var db = new TestDb();
        SeedGraph(db, includeSolicitation: true, includeQuote: true);
        var service = new RecordingProcurementService();

        using var context = db.ContextFor(Bu1);
        var tool = new AwardRfqTool(context, service);
        var legacy = await tool.ExecuteAsync(AgentSeed.Json(
            "{\"rfqId\":700,\"awards\":[{\"supplierId\":500,\"unitPrice\":2,\"quantity\":1}]}"), AgentContext, default);
        var multiple = await tool.ExecuteAsync(AgentSeed.Json(
            "{\"rfqId\":700,\"awards\":[{" +
            "\"supplierQuotedItemId\":9001,\"expectedQuoteVersion\":1,\"quantity\":1},{" +
            "\"supplierQuotedItemId\":9001,\"expectedQuoteVersion\":1,\"quantity\":1}]}"), AgentContext, default);

        Assert.False(legacy.Success);
        Assert.False(multiple.Success);
        Assert.Empty(service.Awards);
    }

    [Fact]
    public async Task AwardRfq_UsesPersistedLandedCostForTenantCap_NotCallerHints()
    {
        using var db = new TestDb();
        SeedGraph(db, includeSolicitation: true, includeQuote: true);
        using (var seed = db.ContextFor(null))
        {
            seed.Set<AgentPolicy>().Single().MaxAutoAwardValue = 10m;
            await seed.SaveChangesAsync();
        }
        var service = new RecordingProcurementService();

        using var context = db.ContextFor(Bu1);
        var result = await new AwardRfqTool(context, service).ExecuteAsync(AgentSeed.Json(
            "{\"rfqId\":700,\"totalValue\":0.01,\"awards\":[{" +
            "\"supplierQuotedItemId\":9001,\"expectedQuoteVersion\":1," +
            "\"unitPrice\":0.01,\"quantity\":6}]}"), AgentContext, default);

        Assert.False(result.Success);
        Assert.Contains("12", result.Error, StringComparison.Ordinal);
        Assert.Contains("cap", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(service.Awards);
    }

    [Theory]
    [InlineData(AgentAutonomyLevel.Act, true)]
    [InlineData(AgentAutonomyLevel.Suggest, false)]
    [InlineData(AgentAutonomyLevel.Observe, false)]
    public async Task AwardRfq_FailsClosedWheneverPolicyRequiresHumanApproval(
        AgentAutonomyLevel autonomyLevel,
        bool requireApproval)
    {
        using var db = new TestDb();
        SeedGraph(db, includeSolicitation: true, includeQuote: true);
        using (var seed = db.ContextFor(null))
        {
            var policy = seed.Set<AgentPolicy>().Single();
            policy.AutonomyLevel = autonomyLevel;
            policy.RequireApprovalForAwards = requireApproval;
            await seed.SaveChangesAsync();
        }
        var service = new RecordingProcurementService();

        using var context = db.ContextFor(Bu1);
        var result = await new AwardRfqTool(context, service).ExecuteAsync(AgentSeed.Json(
            "{\"rfqId\":700,\"awards\":[{" +
            "\"supplierQuotedItemId\":9001,\"expectedQuoteVersion\":1,\"quantity\":10}]}"),
            AgentContext, default);

        Assert.False(result.Success);
        Assert.Contains("human approval", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(service.Awards);
    }

    private const long LongWarrantySupplierId = ProcurementTestData.Supplier + 1;
    private const long UncapturedWarrantySupplierId = ProcurementTestData.Supplier + 2;

    /// <summary>
    /// Runs <see cref="CompareSupplierQuotesTool"/> over the fixture's RFQ and returns its single
    /// comparison line, detached from the document so the caller can read it after disposal.
    /// </summary>
    private static async Task<JsonElement> CompareLineAsync(ProcurementScenario fixture)
    {
        await using var context = fixture.Context();
        var result = await new CompareSupplierQuotesTool(context).ExecuteAsync(
            AgentSeed.Json($"{{\"rfqId\":{fixture.RfqId}}}"),
            new AgentToolContext { BusinessUnitId = fixture.BusinessUnitId, UserId = 42, UserName = "tester" },
            default);

        Assert.True(result.Success, result.Error);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result.Data));
        return Assert.Single(document.RootElement.GetProperty("lines").EnumerateArray()).Clone();
    }

    private static JsonElement Bid(JsonElement line, long supplierId) =>
        Assert.Single(line.GetProperty("bids").EnumerateArray(),
            bid => bid.GetProperty("SupplierId").GetInt64() == supplierId);

    private static async Task SetWeightsAsync(ProcurementScenario fixture,
        int price, int leadTime, int warranty, int paymentTerms, string key)
    {
        await using var context = fixture.Context();
        await new SupplierComparisonWeightsService(context).UpdateAsync(
            fixture.BusinessUnitId, 42, "qa", key,
            new UpdateSupplierComparisonWeightsCommand(price, leadTime, warranty, paymentTerms,
                "Agent and workbench must rank on one weight set"));
    }

    /// <summary>
    /// A second award-eligible supplier whose governance mirrors the fixture's own, so the governed
    /// comparison scores every offer and the two paths differ on nothing but the code under test.
    /// </summary>
    private static async Task AddEligibleSupplierAsync(ProcurementScenario fixture, long supplierId, string name)
    {
        await using var context = fixture.Context();
        var supplier = AgentSeed.Supplier(context, supplierId, fixture.BusinessUnitId, name,
            $"supplier-{supplierId}@example.test");
        supplier.GovernanceStatus = SupplierGovernanceStatuses.Approved;
        supplier.VerificationStatus = SupplierVerificationStatuses.Verified;
        supplier.ComplianceStatus = SupplierComplianceStatuses.Cleared;
        supplier.RiskStatus = SupplierRiskStatuses.Low;
        supplier.ReadinessStatus = SupplierReadinessStatuses.Ready;
        supplier.ConcurrencyToken = Guid.NewGuid();
        await context.SaveChangesAsync();
    }

    private static async Task<long> QuoteAsync(ProcurementScenario fixture, long supplierId, string key,
        decimal unitPrice, int leadTimeDays, int? warrantyMonths)
    {
        var solicitation = await fixture.Execute(service => service.CreateSolicitationAsync(
            fixture.Solicitation($"{key}-sol") with { SupplierId = supplierId }));
        await fixture.MarkSolicitationSentAsync(solicitation.Id);
        var quote = await fixture.Execute(service => service.CaptureSupplierQuoteAsync(
            fixture.Quote(solicitation.Id, $"{key}-quote") with
            {
                Lines = [fixture.QuoteLine() with
                {
                    UnitPrice = unitPrice,
                    LeadTimeDays = leadTimeDays,
                    WarrantyMonths = warrantyMonths
                }]
            }));
        return Assert.Single(quote.LineIds);
    }

    private static void SeedGraph(TestDb db, bool includeSolicitation, SolicitationStatus status = SolicitationStatus.Sent,
        bool includeQuote = false)
    {
        using var seed = db.ContextFor(null);
        // Credit days captured, so this supplier is scorable against the default weight set (payment
        // terms carries 10 of the 100). Without it the comparison tool correctly declines to rank —
        // a weighted criterion with no value is never imputed and never scored as zero — which is
        // its own test below rather than a precondition of every other one.
        AgentSeed.Supplier(seed, SupplierId, Bu1, "Bolt Traders", "sales@bolts.example").CreditDays = 45;
        // The cap is denominated in the same USD the quotes below are, so these tests exercise
        // cap ARITHMETIC without also exercising conversion. The currency is now mandatory: a
        // policy with none cannot auto-execute at all (see AgentSpendCapCurrencyTests).
        AgentSeed.Policy(seed, Bu1, AgentAutonomyLevel.Act, maxAutoAwardValue: 1_000m,
            requireApprovalForAwards: false, currencyId: 1);
        AgentSeed.Rfq(seed, RfqId, Bu1);
        seed.Currencies.Add(new Currency
        {
            Id = 1,
            BusinessUnitId = Bu1,
            Code = "USD",
            CurrencyName = "US Dollar",
            IsActive = true,
            CreatedBy = "seed",
            CreatedOn = AgentSeed.Now
        });
        AgentSeed.RfqItem(seed, 7001, RfqId, "Hex Bolt M8", 25);
        AgentSeed.RfqItem(seed, 7002, RfqId, "Gasket 40mm", 10);
        if (includeSolicitation)
            AgentSeed.Solicitation(seed, 1, Bu1, RfqId, SupplierId, status);
        if (includeQuote)
        {
            seed.SupplierQuotedItems.Add(new SupplierQuotedItem
            {
                Id = 9001,
                BusinessUnitId = Bu1,
                SupplierId = SupplierId,
                SupplierSolicitationId = 1,
                RfqId = RfqId,
                RfqItemId = 7001,
                Quantity = 25,
                UnitPrice = 2,
                CurrencyId = 1,
                QuoteReference = "SUP-Q-1",
                LeadTimeDays = 5,
                AvailableQuantity = 25,
                LandedUnitCost = 2,
                ValidUntil = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ResponseIdempotencyKey = "seed-quote:9001",
                RequestHash = new string('0', 64),
                QuoteRevision = 1,
                Version = 1,
                IsActive = true,
                CreatedBy = "seed",
                CreatedDate = AgentSeed.Now
            });
        }
        seed.SaveChanges();
    }

    private static SupplierQuotedItem Quote(
        long id,
        decimal? available,
        decimal? moq,
        int? leadTimeDays,
        DateTime? validUntil = null) => new()
    {
        Id = id,
        BusinessUnitId = Bu1,
        SupplierId = SupplierId,
        SupplierSolicitationId = 1,
        RfqId = RfqId,
        RfqItemId = 7001,
        Quantity = 25,
        UnitPrice = 1,
        LandedUnitCost = 1,
        CurrencyId = 1,
        QuoteReference = $"SUP-Q-{id}",
        LeadTimeDays = leadTimeDays,
        AvailableQuantity = available,
        MinimumOrderQuantity = moq,
        ValidUntil = validUntil ?? DateTime.UtcNow.AddDays(1),
        ResponseIdempotencyKey = $"seed-quote:{id}",
        RequestHash = id.ToString("x64"),
        QuoteRevision = 1,
        Version = 1,
        IsActive = true,
        CreatedBy = "seed",
        CreatedDate = AgentSeed.Now
    };

    private sealed class RecordingProcurementService : IProcurementApplicationService
    {
        public List<CreateSolicitationCommand> Solicitations { get; } = [];
        public List<CaptureSupplierQuoteCommand> Quotes { get; } = [];
        public List<ApproveAwardCommand> Awards { get; } = [];

        public Task<SolicitationResult> CreateSolicitationAsync(CreateSolicitationCommand command, CancellationToken ct = default)
        {
            Solicitations.Add(command);
            return Task.FromResult(new SolicitationResult(Solicitations.Count, "PendingDispatch", Solicitations.Count > 1));
        }

        public Task<SupplierQuoteResult> CaptureSupplierQuoteAsync(CaptureSupplierQuoteCommand command, CancellationToken ct = default)
        {
            Quotes.Add(command);
            return Task.FromResult(new SupplierQuoteResult([9000 + Quotes.Count], Quotes.Count > 1));
        }

        public Task<AwardResult> ApproveAwardAsync(ApproveAwardCommand command, CancellationToken ct = default)
        {
            Awards.Add(command);
            return Task.FromResult(new AwardResult(8000 + Awards.Count, "APPROVED", 2m, Awards.Count > 1));
        }

        public Task<ProcurementWorkbench> GetWorkbenchAsync(long businessUnitId, long rfqId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyCollection<SupplierPurchaseOrderSummary>> SearchPurchaseOrdersAsync(
            long businessUnitId, string? search, int limit, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<SolicitationResult> RetrySolicitationAsync(RetrySolicitationCommand command, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<QuoteComparisonResult> CompareQuotesAsync(long businessUnitId, long rfqItemId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<PurchaseOrderResult> CreatePurchaseOrderAsync(CreatePurchaseOrderCommand command, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<PurchaseOrderResult> IssuePurchaseOrderAsync(IssuePurchaseOrderCommand command, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<GoodsReceiptResult> PostGoodsReceiptAsync(PostGoodsReceiptCommand command, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<SupplierPurchaseOrderAcknowledgementResult> AcknowledgePurchaseOrderAsync(
            AcknowledgeSupplierPurchaseOrderCommand command, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<PurchaseOrderTradeTermsResult> AmendPurchaseOrderTradeTermsAsync(
            AmendPurchaseOrderTradeTermsCommand command, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
