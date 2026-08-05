using System.Security.Claims;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

public sealed class CustomerContextControllerFocusedTests
{
    [Fact]
    public async Task Context_UsesCanonicalOutcomeEvidenceAndReturnsCommercialDrilldowns()
    {
        using var fixture = new CustomerAwardTestFixture();
        var purchaseOrder = await fixture.CreatePurchaseOrderAsync("customer-context-po", 10m);
        var award = await fixture.CreateAwardAsync(purchaseOrder, "customer-context-award", 10m);
        var confirmed = await fixture.Service.ConfirmAwardAsync(fixture.BusinessUnitId, award.Id,
            "customer-context-confirm", "customer-context-confirm", new(award.Version), "tests");
        await fixture.Service.ConvertToOrderAsync(fixture.BusinessUnitId, award.Id,
            "customer-context-order", "customer-context-order", new(confirmed.Version), "tests");

        var customerId = await fixture.Context.Customers.Select(customer => customer.Id).SingleAsync();
        var rfq = await fixture.Context.Rfqs.SingleAsync();
        var accepted = Setup(881_001, fixture.BusinessUnitId, "ACCEPTED");
        var rejected = Setup(881_002, fixture.BusinessUnitId, "REJECTED");
        fixture.Context.SetupMasters.AddRange(accepted, rejected);
        fixture.Context.Rfqitems.Add(new Rfqitem
        {
            Id = 881_003,
            Rfqid = rfq.Id,
            ManufacturerPartNumber = "CTX-PART-1",
            ProductShortDescription = "Context test part",
            Quantity = 12,
            CreatedBy = "tests",
            CreatedDate = DateTime.UtcNow
        });
        fixture.Context.Quotes.AddRange(
            Quote(881_004, fixture.BusinessUnitId, customerId, accepted.SetupId, null),
            Quote(881_005, fixture.BusinessUnitId, customerId, rejected.SetupId, DateTime.UtcNow));
        await fixture.Context.SaveChangesAsync();

        var controller = Controller(fixture.Context, fixture.BusinessUnitId);
        var action = await controller.GetContext(customerId, default);
        var response = Assert.IsType<OkObjectResult>(action.Result);
        var context = Assert.IsType<CustomerContextDTO>(response.Value);

        Assert.Equal(3, context.TotalQuotes);
        Assert.Equal(1, context.WonQuotes);
        Assert.Equal(1, context.LostQuotes);
        Assert.Equal(50m, context.WinRatePct);
        Assert.Equal("open", context.RecentQuotes.Single(quote => quote.QuoteId == 881_004).Outcome);
        Assert.Equal("lost", context.RecentQuotes.Single(quote => quote.QuoteId == 881_005).Outcome);
        Assert.Single(context.RecentOrders);
        Assert.Single(context.RecentRfqs);
        var demand = Assert.Single(context.DemandProfile);
        Assert.Equal("CTX-PART-1", demand.PartNumber);
        Assert.Equal(12, demand.RequestedQuantity);
    }

    [Fact]
    public async Task Context_DoesNotExposeCustomerFromAnotherTenant()
    {
        using var fixture = new CustomerAwardTestFixture();
        var customerId = await fixture.Context.Customers.Select(customer => customer.Id).SingleAsync();
        const long otherTenant = 889_999;
        await using var otherContext = fixture.Database.ContextFor(otherTenant);
        var controller = Controller(otherContext, otherTenant);

        var action = await controller.GetContext(customerId, default);
        Assert.IsType<NotFoundObjectResult>(action.Result);
    }

    [Fact]
    public async Task Context_SeparatesCurrenciesAndUsesAwardOrderDateWithContactLineage()
    {
        using var fixture = new CustomerAwardTestFixture();
        var customerId = await fixture.Context.Customers.Select(customer => customer.Id).SingleAsync();
        var lead = await fixture.Context.Leads.SingleAsync();
        var rfq = await fixture.Context.Rfqs.SingleAsync();
        var quote = await fixture.Context.Quotes.SingleAsync();
        var contact = new Contact
        {
            Id = 881_100,
            BusinessUnitId = fixture.BusinessUnitId,
            CustomerId = customerId,
            FirstName = "Maya",
            LastName = "Chen",
            Email = "maya.chen@customer.test",
            IsActive = true,
            IsPrimary = true,
            CreatedBy = "tests",
            CreatedOn = DateTime.UtcNow
        };
        fixture.Context.Contacts.Add(contact);
        lead.ResolveCommercialIdentity(customerId, contact.Id, "VERIFIED");
        rfq.InheritCommercialIdentity(lead);
        quote.InheritCommercialIdentity(rfq);
        await fixture.Context.SaveChangesAsync();

        var purchaseOrder = await fixture.CreatePurchaseOrderAsync("context-currency-po", 10m);
        var award = await fixture.CreateAwardAsync(purchaseOrder, "context-currency-award", 10m);
        var confirmed = await fixture.Service.ConfirmAwardAsync(fixture.BusinessUnitId, award.Id,
            "context-currency-confirm", "context-currency-confirm", new(award.Version), "tests");
        var converted = await fixture.Service.ConvertToOrderAsync(fixture.BusinessUnitId, award.Id,
            "context-currency-order", "context-currency-order", new(confirmed.Version), "tests");

        var soldOn = new DateTime(2026, 6, 15, 9, 30, 0, DateTimeKind.Utc);
        var awardedOrder = await fixture.Context.Orders.SingleAsync(order => order.Id == converted.Id);
        awardedOrder.OrderDate = soldOn;
        var eur = new Currency
        {
            Id = 881_101,
            BusinessUnitId = fixture.BusinessUnitId,
            Code = "EUR",
            CurrencyName = "Euro",
            IsActive = true,
            CreatedBy = "tests",
            CreatedOn = DateTime.UtcNow
        };
        fixture.Context.Currencies.Add(eur);
        fixture.Context.Quotes.Add(new Quote
        {
            Id = 881_102,
            QuoteNo = "CTX-EUR-Q",
            BusinessUnitId = fixture.BusinessUnitId,
            CustomerId = customerId,
            CurrencyId = eur.Id,
            QuoteDate = soldOn.AddDays(-5),
            TotalAmount = 300m,
            CreatedBy = "tests",
            CreatedDate = DateTime.UtcNow
        });
        fixture.Context.Orders.Add(new Order
        {
            Id = 881_103,
            OrderNo = "CTX-EUR-O",
            BusinessUnitId = fixture.BusinessUnitId,
            CustomerId = customerId,
            ContactId = contact.Id,
            CurrencyId = eur.Id,
            StatusId = awardedOrder.StatusId,
            OrderDate = soldOn.AddDays(-10),
            TotalAmount = 200m,
            PaidAmount = 0m,
            IsActive = true,
            SourceType = OrderSourceTypes.Manual,
            CreatedBy = "tests",
            CreatedOn = DateTime.UtcNow
        });
        await fixture.Context.SaveChangesAsync();

        var action = await Controller(fixture.Context, fixture.BusinessUnitId).GetContext(customerId, default);
        var response = Assert.IsType<OkObjectResult>(action.Result);
        var context = Assert.IsType<CustomerContextDTO>(response.Value);

        Assert.Null(context.OrderValueLast24Months);
        Assert.Equal("mixed_currency", context.OrderValueStatus);
        Assert.Equal(["EUR", "USD"], context.OrderValueByCurrency.Select(group => group.CurrencyCode));
        Assert.Null(context.AvgQuoteTotal);
        Assert.Equal("mixed_currency", context.AvgQuoteTotalStatus);

        var sold = Assert.Single(context.RecentItemPrices);
        Assert.Equal(soldOn, sold.SoldOn);
        Assert.Equal(soldOn, sold.QuoteDate);
        Assert.Equal(awardedOrder.CurrencyId, sold.CurrencyId);
        Assert.Equal("USD", sold.CurrencyCode);
        Assert.Equal(converted.Id, sold.AwardOrderId);

        Assert.Equal(contact.Id, context.RecentRfqs.Single().ContactId);
        Assert.Equal("Maya Chen", context.RecentRfqs.Single().ContactName);
        Assert.Equal(contact.Id, context.RecentQuotes.Single(row => row.QuoteId == quote.Id).ContactId);
        Assert.Equal("Maya Chen", context.RecentQuotes.Single(row => row.QuoteId == quote.Id).ContactName);
        Assert.Equal(contact.Id, context.RecentOrders.Single(row => row.OrderId == converted.Id).ContactId);
        Assert.Equal("Maya Chen", context.RecentOrders.Single(row => row.OrderId == converted.Id).ContactName);
    }

    [Fact]
    public async Task Context_BoundsDemandCohortAndUsesOverflowSafeQuantity()
    {
        using var fixture = new CustomerAwardTestFixture();
        var customerId = await fixture.Context.Customers.Select(customer => customer.Id).SingleAsync();
        var rfq = await fixture.Context.Rfqs.SingleAsync();
        rfq.RecDate = DateTime.UtcNow;
        fixture.Context.Rfqitems.AddRange(Enumerable.Range(0, 1_001).Select(index => new Rfqitem
        {
            Id = 882_000 + index,
            Rfqid = rfq.Id,
            ManufacturerPartNumber = "HIGH-VOLUME-PART",
            ProductShortDescription = "High volume demand",
            Quantity = int.MaxValue,
            CreatedBy = "tests",
            CreatedDate = DateTime.UtcNow
        }));
        await fixture.Context.SaveChangesAsync();

        var action = await Controller(fixture.Context, fixture.BusinessUnitId).GetContext(customerId, default);
        var response = Assert.IsType<OkObjectResult>(action.Result);
        var context = Assert.IsType<CustomerContextDTO>(response.Value);

        Assert.Equal(24, context.Completeness.DemandLookbackMonths);
        Assert.Equal(1_000, context.Completeness.DemandLineLimit);
        Assert.Equal(1_000, context.Completeness.DemandLinesEvaluated);
        Assert.True(context.Completeness.DemandLinesTruncated);
        var demand = Assert.Single(context.DemandProfile,
            row => row.PartNumber == "HIGH-VOLUME-PART");
        Assert.Equal(1_000L * int.MaxValue, demand.RequestedQuantity);
        Assert.Equal([rfq.Id], demand.SourceRfqIds);
        Assert.False(demand.SourceRfqsTruncated);
    }

    private static CustomerContextController Controller(ErpRfqAutomationContext context, long tenant) => new(context)
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim("businessUnitId", tenant.ToString())], "focused-test"))
            }
        }
    };

    private static SetupMaster Setup(long id, long tenant, string code) => new()
    {
        SetupId = id,
        BusinessUnitId = tenant,
        SetupType = "QuoteStatus",
        SetupCode = code,
        SetupValue = code,
        IsActive = true,
        CreatedBy = "tests",
        CreatedOn = DateTime.UtcNow
    };

    private static Quote Quote(long id, long tenant, long customerId, long statusId, DateTime? outcomeOn) => new()
    {
        Id = id,
        QuoteNo = $"CTX-Q-{id}",
        BusinessUnitId = tenant,
        CustomerId = customerId,
        QuoteDate = DateTime.UtcNow,
        StatusId = statusId,
        OutcomeOn = outcomeOn,
        TotalAmount = 100m,
        CreatedBy = "tests",
        CreatedDate = DateTime.UtcNow
    };
}
