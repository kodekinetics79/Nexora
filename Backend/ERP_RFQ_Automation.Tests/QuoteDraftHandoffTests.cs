using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Intelligence.Conversion;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using System.Reflection;
using UglyToad.PdfPig;

namespace ERP_RFQ_Automation.Tests;

public sealed class QuoteDraftHandoffTests
{
    [Fact]
    public async Task PrepareDraftFromRfq_IsIdempotentAndPreservesCommercialIdentityWithoutInventingValues()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(9401);
        var lead = Seed.Lead(context, 94011, 9401, items: new[] { CompleteLine(94012) });
        Seed.Customer(context, 94013, 9401, "Draft Customer");
        Seed.Contact(context, 94014, 9401, 94013);
        context.SetupMasters.Add(Status(94015, 9401, "QuoteStatus", "DRAFT"));
        await context.SaveChangesAsync();
        lead.ResolveCommercialIdentity(94013, 94014, "CONFIRMED");
        var rfq = RfqFrom(lead, 94016);
        context.Rfqs.Add(rfq);
        await context.SaveChangesAsync();

        var service = new QuoteService(context, null!, null!);
        var first = await service.PrepareDraftFromRfqAsync(rfq.Id, 9401, "owner@example.com");
        var retry = await service.PrepareDraftFromRfqAsync(rfq.Id, 9401, "owner@example.com");

        context.ChangeTracker.Clear();
        var quote = await context.Quotes.Include(item => item.QuoteItems).SingleAsync();
        Assert.Equal(first.Id, retry.Id);
        Assert.Equal(rfq.Id, quote.Rfqid);
        Assert.Equal(lead.CommercialCaseReference, quote.NexoraSerial);
        Assert.Equal(lead.CustomerId, quote.CustomerId);
        Assert.Equal(lead.ContactId, quote.ContactId);
        Assert.Equal(lead.CurrentRevisionNumber, quote.SourceLeadRevision);
        Assert.Equal(rfq.LifecycleVersion, quote.SourceRfqRevision);
        Assert.Null(quote.CurrencyId);
        Assert.Null(quote.ValidUntil);
        Assert.Equal(0m, quote.TotalAmount);
        Assert.All(quote.QuoteItems, item =>
        {
            Assert.Equal(0m, item.UnitPrice);
            // The legacy column has a database default of zero; the draft's
            // Commercial Review Required state means it is not an approved tax decision.
            Assert.Equal(0m, item.TaxAmount);
            Assert.Null(item.DeliveryLeadTime);
        });
        Assert.Single(await context.Quotes.ToListAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GenerateQuotePdfAsync(quote.Id, 9401));

        var sourceLeadRevision = quote.SourceLeadRevision;
        var sourceRfqRevision = quote.SourceRfqRevision;
        var persistedLead = await context.Leads.SingleAsync(item => item.Id == lead.Id);
        var persistedRfq = await context.Rfqs.SingleAsync(item => item.Id == rfq.Id);
        persistedLead.CurrentRevisionNumber++;
        persistedRfq.LifecycleVersion++;
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var unchangedQuote = await context.Quotes.SingleAsync();
        Assert.Equal(sourceLeadRevision, unchangedQuote.SourceLeadRevision);
        Assert.Equal(sourceRfqRevision, unchangedQuote.SourceRfqRevision);
    }

    [Fact]
    public void Convert_RequiresLeadAndRfqCreatePermissions()
    {
        var action = typeof(ConversionIntelligenceController)
            .GetMethod(nameof(ConversionIntelligenceController.Convert));

        var permissions = Assert.IsAssignableFrom<IEnumerable<RequireModulePermissionAttribute>>(
            action!.GetCustomAttributes<RequireModulePermissionAttribute>(true));
        Assert.Contains(permissions, item => item.Policy == "ModulePermission:Leads:Create");
        Assert.Contains(permissions, item => item.Policy == "ModulePermission:RFQ Management:Create");
    }

    [Fact]
    public async Task PrepareDraftFromRfq_DoesNotDiscloseAnotherTenantRfq()
    {
        using var db = new TestDb();
        await using var owner = db.ContextFor(9402);
        var lead = Seed.Lead(owner, 94021, 9402, items: new[] { CompleteLine(94022) });
        Seed.Customer(owner, 94023, 9402, "Tenant Customer");
        await owner.SaveChangesAsync();
        lead.ResolveCommercialIdentity(94023, null, "CONFIRMED");
        owner.Rfqs.Add(RfqFrom(lead, 94024));
        await owner.SaveChangesAsync();

        await using var intruder = db.ContextFor(9403);
        var service = new QuoteService(intruder, null!, null!);
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.PrepareDraftFromRfqAsync(94024, 9403, "intruder@example.com"));
        Assert.Empty(await intruder.Quotes.ToListAsync());
    }

    [Fact]
    public async Task PrepareDraftFromRfq_CarriesUnitAndBuyersLineReference_AndOrdersLinesByThatReference()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(9404);
        // Insertion order deliberately REVERSED against the buyer's numbering ("00020"
        // before "00010") to prove ordering follows the buyer's reference, not row order.
        var lead = Seed.Lead(context, 94041, 9404, items: new[]
        {
            Line(94042, "00020", "EA", "Gasket spiral wound"),
            Line(94043, "00010", "PACK", "Hex bolts M12")
        });
        Seed.Customer(context, 94044, 9404, "Ref Customer");
        context.SetupMasters.Add(Status(94045, 9404, "QuoteStatus", "DRAFT"));
        await context.SaveChangesAsync();
        lead.ResolveCommercialIdentity(94044, null, "CONFIRMED");
        var rfq = RfqFrom(lead, 94046);
        context.Rfqs.Add(rfq);
        await context.SaveChangesAsync();

        var service = new QuoteService(context, null!, null!);
        var draft = await service.PrepareDraftFromRfqAsync(rfq.Id, 9404, "owner@example.com");

        // Persisted lines carry the unit and the buyer's own reference from the RFQ line.
        context.ChangeTracker.Clear();
        var quote = await context.Quotes.Include(item => item.QuoteItems).SingleAsync();
        var byRef = quote.QuoteItems.ToDictionary(item => item.CustomerLineRef!);
        Assert.Equal("EA", byRef["00020"].UnitOfMeasure);
        Assert.Equal("PACK", byRef["00010"].UnitOfMeasure);

        // The DTO orders lines by the buyer's reference, numeric-aware ("00010" < "00020").
        Assert.Equal(new[] { "00010", "00020" }, draft.QuoteItems.Select(item => item.CustomerLineRef).ToArray());
        Assert.Equal(new[] { "PACK", "EA" }, draft.QuoteItems.Select(item => item.UnitOfMeasure).ToArray());
    }

    [Fact]
    public async Task GenerateQuotePdf_PrintsUnit_BuyersLineReference_AndTheCustomersRfqNumber()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(9405);
        var quoteId = SeedPrintableQuote(context, 9405, 94051,
            customerRfqNo: "SA-RFQ-2026-889",
            lines: new[]
            {
                (Ref: (string?)"00020", Uom: (string?)"EA", Description: "Gasket spiral wound"),
                (Ref: (string?)"00010", Uom: (string?)"PACK", Description: "Hex bolts M12")
            });

        var service = new QuoteService(context, null!, new NullQuoteConfigurationRepository());
        await Attest(context, quoteId, 9405);
        var pdf = await service.GenerateQuotePdfAsync(quoteId, 9405);

        var text = PdfText(pdf);
        // Header: the buyer files our quote under THEIR number.
        Assert.Contains("Your RFQ Reference", text);
        Assert.Contains("SA-RFQ-2026-889", text);
        // Line grid: unit column + the buyer's own line references.
        Assert.Contains("UOM", text);
        Assert.Contains("PACK", text);
        Assert.Contains("EA", text);
        Assert.Contains("00010", text);
        Assert.Contains("00020", text);
        // Deterministic, numeric-aware order: the buyer's "00010" prints before "00020".
        Assert.True(text.IndexOf("00010", StringComparison.Ordinal) < text.IndexOf("00020", StringComparison.Ordinal),
            "Quote lines must print in the buyer's line-reference order.");
    }

    [Fact]
    public async Task GenerateQuotePdf_LegacyLinesWithoutUnitOrReference_StillRenderWithSyntheticNumbering()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(9406);
        // Pre-existing production rows: both new columns are NULL and no RFQ is linked.
        var quoteId = SeedPrintableQuote(context, 9406, 94061,
            customerRfqNo: null,
            lines: new[]
            {
                (Ref: (string?)null, Uom: (string?)null, Description: "Legacy line one"),
                (Ref: (string?)null, Uom: (string?)null, Description: "Legacy line two")
            });

        var service = new QuoteService(context, null!, new NullQuoteConfigurationRepository());
        await Attest(context, quoteId, 9406);
        var pdf = await service.GenerateQuotePdfAsync(quoteId, 9406);

        Assert.NotEmpty(pdf);
        var text = PdfText(pdf);
        Assert.Contains("Legacy line one", text);
        Assert.Contains("Legacy line two", text);
        // No buyer reference exists, so no customer RFQ header line is invented.
        Assert.DoesNotContain("Your RFQ Reference", text);
    }

    /// <summary>
    /// R5: the PDF is the commercial document, so rendering one now requires a recorded
    /// price-provenance attestation covering the quote's current prices — the same gate the
    /// send path has always had. These tests are about what the document PRINTS, not about
    /// the gate, so they satisfy it explicitly. QuotePriceAttestationTests owns the gate.
    /// </summary>
    private static Task Attest(ErpRfqAutomationContext context, long quoteId, long tenant) =>
        new ERP_RFQ_Automation.Intelligence.Pricing.PriceAttestationService(context).AttestAsync(
            quoteId, tenant,
            ERP_RFQ_Automation.Intelligence.Pricing.PriceAttestationSources.SupplierQuote,
            "SQ-TEST", null, "tests", default);

    /// <summary>A complete, priced, TAXED, non-draft quote (optionally RFQ-linked) that passes
    /// every PDF export gate; returns the quote id.</summary>
    private static long SeedPrintableQuote(
        ErpRfqAutomationContext context, long tenant, long baseId, string? customerRfqNo,
        (string? Ref, string? Uom, string Description)[] lines)
    {
        Seed.EnsureBusinessUnit(context, tenant);
        // A printable quote is a sendable quote, so it carries a currency — the renderer now
        // refuses a currency-less document rather than printing a "USD" fallback over an unknown
        // unit (see IssuedQuoteCurrencyTests). Seeded once here so every print test states the
        // valid shape.
        var currencyId = baseId + 90;
        if (!context.Currencies.Any(x => x.Id == currencyId))
            context.Currencies.Add(new Currency
            {
                Id = currencyId, BusinessUnitId = tenant, Code = "SAR",
                CurrencyName = "Saudi Riyal", CreatedBy = "seed"
            });

        long? rfqId = null;
        if (customerRfqNo != null)
        {
            var lead = Seed.Lead(context, baseId + 1, tenant);
            lead.Rfqno = customerRfqNo;
            context.Rfqs.Add(new Rfq
            {
                Id = baseId + 2,
                Rfqno = customerRfqNo,
                LeadId = lead.Id,
                BusinessUnitId = tenant,
                CreatedBy = "seed",
                CreatedDate = DateTime.UtcNow
            });
            rfqId = baseId + 2;
        }

        var quote = new Quote
        {
            Id = baseId + 3,
            QuoteNo = $"QT-PRINT-{baseId}",
            Rfqid = rfqId,
            CurrencyId = currencyId,
            BusinessUnitId = tenant,
            QuoteDate = DateTime.UtcNow,
            ValidUntil = DateTime.UtcNow.AddDays(30),
            TotalAmount = 100m,
            CreatedBy = "seed",
            CreatedDate = DateTime.UtcNow
        };
        var lineId = baseId + 10;
        foreach (var line in lines)
        {
            quote.QuoteItems.Add(new QuoteItem
            {
                Id = lineId++,
                ItemDescription = line.Description,
                Quantity = 5m,
                UnitOfMeasure = line.Uom,
                CustomerLineRef = line.Ref,
                UnitPrice = 10m,
                // R17: the PDF gate refuses a line whose output tax was never derived, the same
                // way the send gate does — the document IS the commercial thing, and a quotation
                // with no VAT stated on it is deemed VAT-inclusive. 5 x 10.00 = 50.00 net, 7.50
                // VAT at the KSA standard rate, 57.50 gross.
                TaxAmount = 7.50m,
                TaxCategory = ERP_RFQ_Automation.OrderToCash.QuoteLineTaxCategories.Standard,
                TaxRatePercentApplied = 15m,
                TotalAmount = 57.50m,
                CreatedBy = "seed",
                CreatedDate = DateTime.UtcNow
            });
        }
        context.Quotes.Add(quote);
        context.SaveChanges();
        return quote.Id;
    }

    private static string PdfText(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        return string.Join("\n", document.GetPages().Select(page => page.Text));
    }

    private static LeadItem Line(long id, string lineItemNo, string unitOfMeasure, string description) => new()
    {
        Id = id,
        LineItemNo = lineItemNo,
        ItemMaterialCode = $"PART-{id}",
        ProductShortDescription = description,
        Quantity = 2,
        UnitOfMeasure = unitOfMeasure,
        Currency = "USD"
    };

    /// <summary>PDF export path only needs the lookup; "no configuration row" exercises
    /// every built-in default (colors, terms, company block).</summary>
    /// <summary>
    /// A CONFIGURED seller, because the PDF now refuses to render for a business unit whose own
    /// records cannot say who is sending the quotation. These tests are about line rendering, not
    /// about identity, so the fixture supplies the identity a real tenant has and lets them go on
    /// asserting what they were written to assert. The refusal itself is pinned separately by
    /// QuoteIssuerIdentityTests.
    /// </summary>
    private sealed class NullQuoteConfigurationRepository : IQuoteConfigurationRepository
    {
        public Task<QuoteConfiguration?> GetByBusinessUnitIdAsync(long businessUnitId)
            => Task.FromResult<QuoteConfiguration?>(new QuoteConfiguration
            {
                BusinessUnitId = businessUnitId,
                CompanyAddress = "King Fahd Road, Al Khobar 34423",
                CompanyPhone = "+966 13 800 0000",
                CompanyEmail = "sales@noorandsons.example"
            });
        public Task AddAsync(QuoteConfiguration config) => Task.CompletedTask;
        public Task UpdateAsync(QuoteConfiguration config) => Task.CompletedTask;
    }

    /// <param name="markLinesForQuote">
    /// Preparing a Quote Draft now requires an explicit per-line decision, so these fixtures
    /// state one. The tests that use the default are asserting carry-forward and idempotency,
    /// not the gate; the gate itself is asserted by
    /// <see cref="PrepareDraftFromRfq_RefusesWhenNoLineIsMarkedForQuote"/> and its siblings,
    /// which pass <c>false</c>.
    /// </param>
    private static Rfq RfqFrom(Lead lead, long id, bool markLinesForQuote = true)
    {
        var rfq = new Rfq
        {
            Id = id,
            Rfqno = $"RFQ-{id}",
            RecDate = lead.RecDate,
            BidClosingDate = lead.BidClosingDate,
            LeadId = lead.Id,
            BusinessUnitId = lead.BusinessUnitId,
            CustomerId = lead.CustomerId,
            CreatedBy = "seed",
            CreatedDate = DateTime.UtcNow,
            Rfqitems = lead.LeadItems.Select((line, index) => new Rfqitem
            {
                Id = id + 1 + index,
                LineItemNo = line.LineItemNo,
                ItemMaterialCode = line.ItemMaterialCode,
                ProductShortDescription = line.ProductShortDescription,
                // LeadItem.Quantity is nullable so an unread quantity is distinguishable from a
                // real zero; Rfqitem.Quantity is NOT NULL and positive-checked. Production
                // RFQ Promotion refuses an unquantified line rather than
                // coalescing it, so the fixture refuses too instead of silently seeding a 0.
                Quantity = line.Quantity ?? throw new InvalidOperationException(
                    $"Fixture lead line '{line.LineItemNo}' states no quantity; an RFQ line cannot be built from it."),
                UnitOfMeasure = line.UnitOfMeasure,
                CreatedBy = "seed",
                CreatedDate = DateTime.UtcNow
            }).ToList()
        };
        if (markLinesForQuote)
            foreach (var line in rfq.Rfqitems)
                line.DecideParticipation(Rfqitem.ParticipationQuote, null, "seed@example.com", DateTime.UtcNow);
        rfq.InheritCommercialIdentity(lead);
        return rfq;
    }

    private static LeadItem CompleteLine(long id) => new()
    {
        Id = id,
        LineItemNo = "1",
        ItemMaterialCode = "PART-001",
        ProductShortDescription = "Verified requested part",
        Quantity = 2,
        UnitOfMeasure = "EA",
        Currency = "USD"
    };

    private static SetupMaster Status(long id, long businessUnitId, string type, string code) => new()
    {
        SetupId = id,
        BusinessUnitId = businessUnitId,
        SetupType = type,
        SetupCode = code,
        SetupValue = "Draft",
        IsActive = true,
        CreatedBy = "seed",
        CreatedOn = DateTime.UtcNow
    };
}
