using ERP_RFQ_Automation.CommercialCases.Participation;
using ERP_RFQ_Automation.Intelligence.Decision;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Reporting;
using ERP_RFQ_Automation.Tests.Support;

namespace ERP_RFQ_Automation.Tests.Intelligence.Decision;

public sealed class LeadDecisionServiceTests
{
    private const long TenantId = 11;
    private const long OtherTenantId = 12;

    [Fact]
    public async Task Canonical_customer_wins_over_duplicate_names_and_cross_tenant_candidates()
    {
        using var database = new TestDb();
        await using (var seed = database.ContextFor(null))
        {
            var canonical = Seed.Customer(seed, 101, TenantId, "Duplicate Buyer");
            Seed.Customer(seed, 102, TenantId, "Duplicate Buyer");
            Seed.Customer(seed, 201, OtherTenantId, "Duplicate Buyer");
            var lead = Seed.Lead(seed, 1, TenantId, buyersName: "Duplicate Buyer");
            lead.ResolveCommercialIdentity(canonical.Id, null, "VERIFIED");
            await seed.SaveChangesAsync();
        }

        await using var context = database.ContextFor(TenantId);
        var brief = await new LeadDecisionService(context, new GrossMarginService(context)).GetBriefAsync(1, TenantId, default);

        Assert.Equal(101, brief.Customer.CustomerId);
        Assert.Equal(CustomerIdentityEvidence.Canonical, brief.Customer.IdentityEvidence);
        Assert.True(brief.Customer.IsDecisionGradeIdentity);
        Assert.True(brief.Customer.IsExistingCustomer);
    }

    [Fact]
    public async Task Customer_history_uses_recent_sent_quotes_and_only_their_recent_orders()
    {
        using var database = new TestDb();
        var now = DateTime.UtcNow;
        await using (var seed = database.ContextFor(null))
        {
            var customer = Seed.Customer(seed, 101, TenantId, "Measured Buyer");
            var lead = Seed.Lead(seed, 1, TenantId, buyersName: "Measured Buyer");
            lead.ResolveCommercialIdentity(customer.Id, null, "VERIFIED");
            seed.SetupMasters.Add(new SetupMaster
            {
                SetupId = 701,
                SetupType = "OrderStatus",
                SetupValue = "Open",
                BusinessUnitId = TenantId,
                IsActive = true,
                CreatedBy = "test",
                CreatedOn = now
            });
            seed.Currencies.Add(new Currency
            {
                Id = 702,
                Code = "USD",
                CurrencyName = "US Dollar",
                BusinessUnitId = TenantId,
                IsActive = true,
                CreatedBy = "test",
                CreatedOn = now
            });
            seed.Quotes.AddRange(
                new Quote
                {
                    Id = 703,
                    QuoteNo = "QT-RECENT",
                    CustomerId = customer.Id,
                    BusinessUnitId = TenantId,
                    QuoteDate = now.AddDays(-11),
                    SentOn = now.AddDays(-10),
                    CreatedBy = "test",
                    CreatedDate = now.AddDays(-11)
                },
                new Quote
                {
                    Id = 704,
                    QuoteNo = "QT-OLD",
                    CustomerId = customer.Id,
                    BusinessUnitId = TenantId,
                    QuoteDate = now.AddMonths(-25),
                    SentOn = now.AddMonths(-25),
                    CreatedBy = "test",
                    CreatedDate = now.AddMonths(-25)
                },
                new Quote
                {
                    Id = 708,
                    QuoteNo = "QT-RECENT-OLD-ORDER",
                    CustomerId = customer.Id,
                    BusinessUnitId = TenantId,
                    QuoteDate = now.AddDays(-20),
                    SentOn = now.AddDays(-19),
                    CreatedBy = "test",
                    CreatedDate = now.AddDays(-20)
                });
            seed.Orders.AddRange(
                new Order
                {
                    Id = 705,
                    OrderNo = "SO-ELIGIBLE",
                    QuoteId = 703,
                    SourceType = "LEGACY_QUOTE",
                    CustomerId = customer.Id,
                    BusinessUnitId = TenantId,
                    StatusId = 701,
                    CurrencyId = 702,
                    OrderDate = now.AddDays(-5),
                    TotalAmount = 250m,
                    CreatedBy = "test",
                    CreatedOn = now,
                    IsActive = true
                },
                new Order
                {
                    Id = 706,
                    OrderNo = "SO-OLD-QUOTE",
                    QuoteId = 704,
                    SourceType = "LEGACY_QUOTE",
                    CustomerId = customer.Id,
                    BusinessUnitId = TenantId,
                    StatusId = 701,
                    CurrencyId = 702,
                    OrderDate = now.AddDays(-4),
                    TotalAmount = 900m,
                    CreatedBy = "test",
                    CreatedOn = now,
                    IsActive = true
                },
                new Order
                {
                    Id = 707,
                    OrderNo = "SO-OLD-ORDER",
                    QuoteId = 708,
                    SourceType = "LEGACY_QUOTE",
                    CustomerId = customer.Id,
                    BusinessUnitId = TenantId,
                    StatusId = 701,
                    CurrencyId = 702,
                    OrderDate = now.AddMonths(-25),
                    TotalAmount = 700m,
                    CreatedBy = "test",
                    CreatedOn = now,
                    IsActive = true
                });
            await seed.SaveChangesAsync();
        }

        await using var context = database.ContextFor(TenantId);
        var brief = await new LeadDecisionService(context, new GrossMarginService(context)).GetBriefAsync(1, TenantId, default);

        Assert.Equal(2, brief.Customer.Quotes);
        Assert.Equal(1, brief.Customer.Orders);
        Assert.Equal(250m, brief.Customer.TotalOrderValue);
        Assert.Equal("USD", brief.Customer.TotalOrderCurrency);
        Assert.Equal(now.AddDays(-5), brief.Customer.EvidenceAsOfUtc);
    }

    [Fact]
    public async Task Heuristic_customer_matching_is_tenant_scoped_and_explicitly_weaker()
    {
        using var database = new TestDb();
        await using (var seed = database.ContextFor(null))
        {
            Seed.Customer(seed, 101, TenantId, "Known Buyer");
            Seed.Customer(seed, 201, OtherTenantId, "Known Buyer");
            Seed.Lead(seed, 1, TenantId, buyersName: "Known Buyer");
            await seed.SaveChangesAsync();
        }

        await using var context = database.ContextFor(TenantId);
        var brief = await new LeadDecisionService(context, new GrossMarginService(context)).GetBriefAsync(1, TenantId, default);

        Assert.Equal(101, brief.Customer.CustomerId);
        Assert.Equal(CustomerIdentityEvidence.HeuristicName, brief.Customer.IdentityEvidence);
        Assert.False(brief.Customer.IsDecisionGradeIdentity);
        Assert.Contains(brief.Reasons, reason => reason.Contains("weaker name/email match", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Duplicate_name_customer_candidates_remain_ambiguous()
    {
        using var database = new TestDb();
        await using (var seed = database.ContextFor(null))
        {
            Seed.Customer(seed, 101, TenantId, "Ambiguous Buyer");
            Seed.Customer(seed, 102, TenantId, "Ambiguous Buyer");
            Seed.Lead(seed, 1, TenantId, buyersName: "Ambiguous Buyer");
            await seed.SaveChangesAsync();
        }

        await using var context = database.ContextFor(TenantId);
        var brief = await new LeadDecisionService(context, new GrossMarginService(context)).GetBriefAsync(1, TenantId, default);

        Assert.Null(brief.Customer.CustomerId);
        Assert.False(brief.Customer.IsExistingCustomer);
        Assert.False(brief.Customer.IsDecisionGradeIdentity);
        Assert.Equal(CustomerIdentityEvidence.HeuristicAmbiguous, brief.Customer.IdentityEvidence);
    }

    [Fact]
    public async Task Name_only_product_candidates_contribute_no_commercial_signal()
    {
        using var database = new TestDb();
        await using (var seed = database.ContextFor(null))
        {
            var lead = Seed.Lead(seed, 1, TenantId, buyersName: null);
            lead.LeadItems.Add(Item(1001, null, null, "Precision hydraulic pump", 10, null, "USD"));
            seed.Products.AddRange(
                Product(501, "PUMP-A", "Precision hydraulic pump assembly", 20m, 25m, 50m),
                Product(502, "PUMP-B", "Precision hydraulic pump kit", 30m, 30m, 60m));
            await seed.SaveChangesAsync();
        }

        await using var context = database.ContextFor(TenantId);
        var brief = await new LeadDecisionService(context, new GrossMarginService(context)).GetBriefAsync(1, TenantId, default);

        var item = Assert.Single(brief.Coverage.Items);
        Assert.False(item.Matched);
        Assert.Null(item.ProductId);
        Assert.Null(item.UnitPrice);
        Assert.Null(item.CatalogQtyOnHand);
        Assert.Equal(0, brief.Coverage.CoveredItems);
        Assert.Equal(0, brief.Coverage.CatalogOnHandItems);
        Assert.Null(brief.EstimatedValue);
        Assert.Null(brief.MarginPotentialPct);
        Assert.Equal(LeadDecisionRecommendations.Skip, brief.Recommendation);
    }

    [Fact]
    public async Task Mixed_currency_lines_have_no_aggregate_value_or_margin_without_fx()
    {
        using var database = new TestDb();
        await using (var seed = database.ContextFor(null))
        {
            var lead = Seed.Lead(seed, 1, TenantId, buyersName: null);
            lead.LeadItems.Add(Item(1001, "PART-USD", null, "USD line", 2, 100m, "USD"));
            lead.LeadItems.Add(Item(1002, "PART-EUR", null, "EUR line", 3, 80m, "EUR"));
            seed.Products.Add(Product(501, "PART-USD", "USD product", 5m, 50m, 100m));
            seed.Products.Add(Product(502, "PART-EUR", "EUR product", 5m, 40m, 80m));
            await seed.SaveChangesAsync();
        }

        await using var context = database.ContextFor(TenantId);
        var brief = await new LeadDecisionService(context, new GrossMarginService(context)).GetBriefAsync(1, TenantId, default);

        Assert.Equal(100m, brief.Coverage.CoveragePct);
        Assert.Null(brief.Currency);
        Assert.Null(brief.EstimatedValue);
        Assert.Null(brief.MarginPotentialPct);
        Assert.Equal("unknown", brief.ValueConfidence);
    }

    [Fact]
    public async Task Product_cost_without_currency_never_creates_margin()
    {
        using var database = new TestDb();
        await using (var seed = database.ContextFor(null))
        {
            var lead = Seed.Lead(seed, 1, TenantId, buyersName: null);
            lead.LeadItems.Add(Item(1001, "HIGH", null, "High value", 1, 100m, "USD"));
            lead.LeadItems.Add(Item(1002, "VOLUME", null, "Volume", 9, 10m, "USD"));
            seed.Products.Add(Product(501, "HIGH", "High value", 1m, 50m, 100m));
            seed.Products.Add(Product(502, "VOLUME", "Volume", 1m, 9m, 10m));
            // Stock now comes from the Inventory rows, never the product master column: the
            // decision brief must quote a number the availability engine agrees with.
            Stock(seed, warehouseId: 601, inventoryId: 611, productId: 501, onHand: 1m);
            Stock(seed, warehouseId: 601, inventoryId: 612, productId: 502, onHand: 1m);
            await seed.SaveChangesAsync();
        }

        await using var context = database.ContextFor(TenantId);
        var brief = await new LeadDecisionService(context, new GrossMarginService(context)).GetBriefAsync(1, TenantId, default);

        Assert.Equal("USD", brief.Currency);
        Assert.Equal(190m, brief.EstimatedValue);
        Assert.Null(brief.MarginPotentialPct);
        Assert.Equal(0, brief.MarginCostedItems);
        Assert.False(brief.IsMarginComplete);
        Assert.Equal(2, brief.Coverage.CatalogOnHandItems);
        Assert.Equal(2m, brief.Coverage.CatalogOnHandQuantity);
        Assert.Contains(brief.Reasons, reason => reason.Contains("available to promise", StringComparison.Ordinal));
        Assert.DoesNotContain(brief.Reasons, reason => reason.Contains("We stock", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Duplicate_exact_product_identifiers_fail_closed()
    {
        using var database = new TestDb();
        await using (var seed = database.ContextFor(null))
        {
            var lead = Seed.Lead(seed, 1, TenantId, buyersName: null);
            lead.LeadItems.Add(Item(1001, null, "DUPLICATE", "Ambiguous product", 1, 100m, "USD"));
            var first = Product(501, "FIRST", "First", 1m, 50m, 100m);
            var second = Product(502, "SECOND", "Second", 1m, 55m, 100m);
            first.ModelNo = "DUPLICATE";
            second.ModelNo = "DUPLICATE";
            seed.Products.AddRange(first, second);
            await seed.SaveChangesAsync();
        }

        await using var context = database.ContextFor(TenantId);
        var brief = await new LeadDecisionService(context, new GrossMarginService(context)).GetBriefAsync(1, TenantId, default);

        var item = Assert.Single(brief.Coverage.Items);
        Assert.False(item.Matched);
        Assert.Null(item.ProductId);
        Assert.Equal(0, brief.Coverage.CoveredItems);
        Assert.Null(brief.MarginPotentialPct);
    }

    [Fact]
    public async Task Conflicting_cross_identifier_product_matches_fail_closed()
    {
        using var database = new TestDb();
        await using (var seed = database.ContextFor(null))
        {
            var lead = Seed.Lead(seed, 1, TenantId, buyersName: null);
            lead.LeadItems.Add(Item(1001, "PART-A", "PART-B", "Conflicting identifiers", 1, 100m, "USD"));
            var first = Product(501, "PART-A", "First", 1m, 50m, 100m);
            first.ModelNo = "MODEL-A";
            var second = Product(502, "PART-B", "Second", 1m, 55m, 100m);
            second.ModelNo = "PART-B";
            seed.Products.AddRange(first, second);
            await seed.SaveChangesAsync();
        }

        await using var context = database.ContextFor(TenantId);
        var brief = await new LeadDecisionService(context, new GrossMarginService(context)).GetBriefAsync(1, TenantId, default);

        var item = Assert.Single(brief.Coverage.Items);
        Assert.False(item.Matched);
        Assert.Null(item.ProductId);
        Assert.Equal(0, brief.Coverage.CoveredItems);
    }

    [Fact]
    public async Task Summary_is_versioned_partial_and_never_emits_actionable_bid()
    {
        using var database = new TestDb();
        await using (var seed = database.ContextFor(null))
        {
            var lead = Seed.Lead(seed, 1, TenantId, buyersName: null);
            lead.LeadItems.Add(Item(1001, "KNOWN", null, "Known part", 2, 25m, "USD"));
            seed.Products.Add(Product(501, "KNOWN", "Known part", 10m, 10m, 25m));
            await seed.SaveChangesAsync();
        }

        await using var context = database.ContextFor(TenantId);
        var summaries = await new LeadDecisionService(context, new GrossMarginService(context)).GetSummariesAsync([1], TenantId, default);

        var summary = Assert.Single(summaries).Value;
        Assert.Equal(100m, summary.CoveragePct);
        Assert.Equal(50m, summary.EstimatedValue);
        Assert.Equal(LeadDecisionRecommendations.Review, summary.Recommendation);
        Assert.Equal(LeadDecisionPolicy.Version, summary.PolicyVersion);
        Assert.Equal(LeadDecisionCompleteness.Partial, summary.Completeness);
        Assert.False(summary.IsActionable);
    }

    /// <summary>
    /// The leads grid and the Decision Brief must agree about catalogue coverage. Every
    /// spreadsheet-ingested lead arrives with ItemMaterialCode null and the buyer's material
    /// number in ManufacturerPartNumber — NativeSpreadsheetParser.FieldAliases routes every
    /// material-code heading there by design. The brief reads both fields; the grid used to read
    /// only ItemMaterialCode, so it printed "We stock ~0%" and a Skip chip beside a lead whose own
    /// brief reported full coverage. Both surfaces are asserted here so the divergence itself,
    /// not one half of it, is the subject.
    /// </summary>
    [Fact]
    public async Task Spreadsheet_ingested_part_number_covers_the_grid_exactly_as_it_covers_the_brief()
    {
        using var database = new TestDb();
        await using (var seed = database.ContextFor(null))
        {
            var lead = Seed.Lead(seed, 1, TenantId, buyersName: null);
            // Exactly what the spreadsheet door produces: no material code, number in the mpn.
            lead.LeadItems.Add(Item(1001, null, "MAT-88001", "Spreadsheet line", 2, 25m, "USD"));
            seed.Products.Add(Product(501, "MAT-88001", "Stocked part", 10m, 10m, 25m));
            await seed.SaveChangesAsync();
        }

        await using var context = database.ContextFor(TenantId);
        var service = new LeadDecisionService(context, new GrossMarginService(context));

        var brief = await service.GetBriefAsync(1, TenantId, default);
        Assert.Equal(1, brief.Coverage.CoveredItems);
        Assert.Equal(100m, brief.Coverage.CoveragePct);

        var summary = Assert.Single(await service.GetSummariesAsync([1], TenantId, default)).Value;
        Assert.Equal(100m, summary.CoveragePct);
        Assert.NotEqual(LeadDecisionRecommendations.Skip, summary.Recommendation);
    }

    /// <summary>
    /// The grid must fail closed on ambiguity exactly where the brief does. Two products sharing
    /// a ModelNo the lead's part number hits identify nothing, so both surfaces report zero —
    /// otherwise the same contradiction reappears mirrored, with the grid claiming coverage the
    /// brief denies.
    /// </summary>
    [Fact]
    public async Task Ambiguous_part_number_covers_neither_the_grid_nor_the_brief()
    {
        using var database = new TestDb();
        await using (var seed = database.ContextFor(null))
        {
            var lead = Seed.Lead(seed, 1, TenantId, buyersName: null);
            lead.LeadItems.Add(Item(1001, null, "DUPLICATE", "Ambiguous product", 1, 100m, "USD"));
            var first = Product(501, "FIRST", "First", 1m, 50m, 100m);
            var second = Product(502, "SECOND", "Second", 1m, 55m, 100m);
            first.ModelNo = "DUPLICATE";
            second.ModelNo = "DUPLICATE";
            seed.Products.AddRange(first, second);
            await seed.SaveChangesAsync();
        }

        await using var context = database.ContextFor(TenantId);
        var service = new LeadDecisionService(context, new GrossMarginService(context));

        var brief = await service.GetBriefAsync(1, TenantId, default);
        Assert.Equal(0, brief.Coverage.CoveredItems);

        var summary = Assert.Single(await service.GetSummariesAsync([1], TenantId, default)).Value;
        Assert.Equal(0m, summary.CoveragePct);
    }

    // ---------------------------------------------------------------- D5: what "Nexora's read" says about value

    [Fact]
    public async Task Lines_that_all_state_one_currency_but_no_price_are_reported_as_unpriced_not_as_missing_currency()
    {
        using var database = new TestDb();
        await using (var seed = database.ContextFor(null))
        {
            var lead = Seed.Lead(seed, 1, TenantId, buyersName: null);
            lead.LeadItems.Add(Item(1001, "P-1", null, "Gate valve", 4, null, "SAR"));
            lead.LeadItems.Add(Item(1002, "P-2", null, "Globe valve", 6, null, " sar "));
            await seed.SaveChangesAsync();
        }

        await using var context = database.ContextFor(TenantId);
        var brief = await new LeadDecisionService(context, new GrossMarginService(context)).GetBriefAsync(1, TenantId, default);

        Assert.Equal("SAR", brief.Currency);
        Assert.Null(brief.EstimatedValue);
        Assert.Contains(brief.Reasons, reason => reason.StartsWith("No price information on any line", StringComparison.Ordinal)
                                                 && reason.Contains("SAR", StringComparison.Ordinal));
        Assert.DoesNotContain(brief.Reasons, reason => reason.Contains("one known currency", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Priced_lines_in_two_currencies_are_told_apart_from_lines_with_no_currency()
    {
        using var database = new TestDb();
        await using (var seed = database.ContextFor(null))
        {
            var mixed = Seed.Lead(seed, 1, TenantId, buyersName: null);
            mixed.LeadItems.Add(Item(1001, "P-1", null, "USD line", 2, 100m, "USD"));
            mixed.LeadItems.Add(Item(1002, "P-2", null, "EUR line", 3, 80m, "EUR"));
            var blank = Seed.Lead(seed, 2, TenantId, buyersName: null);
            blank.LeadItems.Add(Item(2001, "P-3", null, "Priced in SAR", 1, 100m, "SAR"));
            blank.LeadItems.Add(Item(2002, "P-4", null, "Priced, no currency", 1, 50m, null));
            await seed.SaveChangesAsync();
        }

        await using var context = database.ContextFor(TenantId);
        var service = new LeadDecisionService(context, new GrossMarginService(context));

        var mixedBrief = await service.GetBriefAsync(1, TenantId, default);
        Assert.Null(mixedBrief.Currency);
        Assert.Null(mixedBrief.EstimatedValue);
        Assert.Contains(mixedBrief.Reasons, reason =>
            reason.Contains("more than one currency (EUR, USD)", StringComparison.Ordinal));

        var blankBrief = await service.GetBriefAsync(2, TenantId, default);
        Assert.Null(blankBrief.EstimatedValue);
        Assert.Contains(blankBrief.Reasons, reason =>
            reason.Contains("1 of 2 priced lines state no currency", StringComparison.Ordinal));
        Assert.DoesNotContain(blankBrief.Reasons, reason => reason.Contains("one known currency", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- D5: coverage counts what a person resolved

    [Fact]
    public async Task A_line_a_person_bound_to_a_catalogue_product_counts_as_covered_and_lends_its_currency()
    {
        using var database = new TestDb();
        await using (var seed = database.ContextFor(null))
        {
            var lead = Seed.Lead(seed, 1, TenantId, buyersName: null);
            // Line 1 matches by exact code. Line 2's code is unknown to the matcher — the rep
            // bound it to product 502 on the Decide screen and gave it a currency there.
            lead.LeadItems.Add(Item(1001, "KNOWN-1", null, "Known part", 2, 10m, "SAR"));
            lead.LeadItems.Add(Item(1002, "GOLD-QUOTE-0004", null, "Resolved by hand", 2, 10m, null));
            seed.Products.Add(Product(501, "KNOWN-1", "Known product", 5m, 8m, 10m));
            seed.Products.Add(Product(502, "CATALOGUE-502", "Hand-resolved product", 5m, 8m, 10m));
            await seed.SaveChangesAsync();
            await SettleLineAsync(seed, lead, leadItemId: 1002, productId: 502, currency: "SAR");
        }

        await using var context = database.ContextFor(TenantId);
        var brief = await new LeadDecisionService(context, new GrossMarginService(context)).GetBriefAsync(1, TenantId, default);

        Assert.Equal(2, brief.Coverage.CoveredItems);
        Assert.Equal(100m, brief.Coverage.CoveragePct);
        var resolved = Assert.Single(brief.Coverage.Items, item => item.LeadItemId == 1002);
        Assert.True(resolved.Matched);
        Assert.Equal(502, resolved.ProductId);
        Assert.Equal("resolved", resolved.MatchType);
        Assert.Contains(brief.Reasons, reason => reason.Contains("2 of 2 items", StringComparison.Ordinal));
        // The currency the rep set on the line is the lead's currency; the total follows.
        Assert.Equal("SAR", brief.Currency);
        Assert.Equal(40m, brief.EstimatedValue);
    }

    /// <summary>
    /// Records what a rep settled on the Decide screen for one line: the current revision's
    /// participation decision carrying the chosen product and currency for that lead item.
    /// </summary>
    private static async Task SettleLineAsync(ErpRfqAutomationContext seed, Lead lead, long leadItemId,
        long productId, string currency)
    {
        var now = DateTimeOffset.UtcNow;
        var batch = new LeadIngestionBatch
        {
            Id = Guid.NewGuid(), BusinessUnitId = TenantId, SourceChannel = "DecisionTests",
            CreatedBy = "tests", CreatedAtUtc = now, UpdatedAtUtc = now
        };
        var occurrence = new LeadIngestionOccurrence
        {
            Id = 9_001, BusinessUnitId = TenantId, Batch = batch, Lead = lead, SourceChannel = "DecisionTests",
            IdempotencyKey = "decision-occurrence", LogicalInquiryFingerprint = new string('a', 64),
            Classification = LeadOccurrenceClassification.New, Confidence = 1m,
            ProcessingPath = LeadProcessingPath.Deterministic, IngestedAtUtc = now, CreatedAtUtc = now,
            ActorType = "TestFixture", ActorId = "tests", CorrelationId = "decision-fixture"
        };
        seed.Add(occurrence);
        await seed.SaveChangesAsync();
        var revision = new LeadRevision
        {
            Id = 9_002, BusinessUnitId = TenantId, Lead = lead, RevisionNumber = 1,
            EstablishedByOccurrence = occurrence, LogicalInquiryFingerprint = new string('b', 64),
            SnapshotJson = "{}", CreatedAtUtc = now, CreatedBy = "tests", ProcessingPath = LeadProcessingPath.Deterministic
        };
        seed.Add(revision);
        await seed.SaveChangesAsync();
        var revisionLine = new LeadItemRevision
        {
            Id = 9_003, BusinessUnitId = TenantId, LeadId = lead.Id, LeadRevisionId = revision.Id,
            LeadItemId = leadItemId, LineNumber = 2, LineFingerprint = new string('c', 64), SnapshotJson = "{}"
        };
        seed.Add(revisionLine);
        await seed.SaveChangesAsync();
        lead.CurrentRevisionId = revision.Id;
        lead.CurrentRevisionNumber = revision.RevisionNumber;
        var fit = new LeadFitAssessment
        {
            Id = 9_004, BusinessUnitId = TenantId, LeadId = lead.Id, LeadRevisionId = revision.Id, Sequence = 1,
            PolicyVersion = "decision-tests/v1", Recommendation = "FIT", IsActionable = true, AssessmentJson = "{}",
            IdempotencyKey = "decision-fit", RequestHash = new string('d', 64), AssessedBy = "tests", AssessedAtUtc = now
        };
        seed.Add(fit);
        await seed.SaveChangesAsync();
        var decision = new LeadParticipationDecision
        {
            Id = 9_005, BusinessUnitId = TenantId, LeadId = lead.Id, LeadRevisionId = revision.Id,
            FitAssessmentId = fit.Id, Sequence = 1, IsCommitted = false, Outcome = LeadParticipationOutcome.FullBid,
            IdempotencyKey = "decision-draft", RequestHash = new string('e', 64), DecidedBy = "tests", DecidedAtUtc = now
        };
        decision.Lines.Add(new LeadLineParticipationDecision
        {
            Id = 9_006, BusinessUnitId = TenantId, LeadId = lead.Id, LeadRevisionId = revision.Id,
            LeadItemRevisionId = revisionLine.Id, DecisionIsCommitted = false, Choice = LeadLineParticipationChoice.Bid,
            ProductId = productId, Quantity = 2, UnitOfMeasure = "EA", Currency = currency,
            CatalogPolicyVersion = "decision-tests/v1", WarningSnapshotJson = "{}"
        });
        seed.Add(decision);
        await seed.SaveChangesAsync();
    }

    private static LeadItem Item(
        long id,
        string? code,
        string? mpn,
        string name,
        int quantity,
        decimal? unitPrice,
        string? currency) => new()
    {
        Id = id,
        ItemMaterialCode = code,
        ManufacturerPartNumber = mpn,
        ProductShortName = name,
        Quantity = quantity,
        UnitPrice = unitPrice,
        Currency = currency
    };

    /// <summary>An authoritative per-warehouse stock row; the warehouse is created on first use.</summary>
    private static void Stock(ErpRfqAutomationContext ctx, long warehouseId, long inventoryId,
        long productId, decimal onHand)
    {
        if (!ctx.Warehouses.Local.Any(x => x.Id == warehouseId) && ctx.Warehouses.Find(warehouseId) is null)
            ctx.Warehouses.Add(new Warehouse
            {
                Id = warehouseId,
                BusinessUnitId = TenantId,
                WarehouseCode = $"WH-{warehouseId}",
                WarehouseName = $"Warehouse {warehouseId}",
                IsActive = true,
                CreatedBy = "decision-tests",
                CreatedOn = new DateTime(2026, 7, 29, 0, 0, 0, DateTimeKind.Utc)
            });
        ctx.Set<ERP_RFQ_Automation.Models.Inventory>().Add(new ERP_RFQ_Automation.Models.Inventory
        {
            Id = inventoryId,
            Buid = TenantId,
            ProductId = productId,
            WarehouseId = warehouseId,
            PartNo = $"INV-{inventoryId}",
            QtyOnHand = onHand,
            ReorderPoint = 0m,
            CreatedBy = "decision-tests",
            CreatedOn = new DateTime(2026, 7, 29, 0, 0, 0, DateTimeKind.Utc)
        });
    }

    private static Product Product(
        long id,
        string partNo,
        string name,
        decimal qtyOnHand,
        decimal unitCost,
        decimal sellingPrice) => new()
    {
        Id = id,
        Buid = TenantId,
        PartNo = partNo,
        ProductName = name,
        QtyOnHand = qtyOnHand,
        UnitCost = unitCost,
        SellingPrice = sellingPrice,
        IsActive = true,
        CreatedBy = "decision-tests",
        CreatedOn = new DateTime(2026, 7, 29, 0, 0, 0, DateTimeKind.Utc)
    };
}
