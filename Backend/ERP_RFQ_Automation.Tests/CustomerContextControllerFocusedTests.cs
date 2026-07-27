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
