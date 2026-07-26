using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Intelligence.Conversion;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using System.Reflection;

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

    private static Rfq RfqFrom(Lead lead, long id)
    {
        var line = lead.LeadItems.Single();
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
            Rfqitems = new List<Rfqitem>
            {
                new()
                {
                    Id = id + 1,
                    LineItemNo = line.LineItemNo,
                    ItemMaterialCode = line.ItemMaterialCode,
                    ProductShortDescription = line.ProductShortDescription,
                    Quantity = line.Quantity,
                    UnitOfMeasure = line.UnitOfMeasure,
                    CreatedBy = "seed",
                    CreatedDate = DateTime.UtcNow
                }
            }
        };
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
