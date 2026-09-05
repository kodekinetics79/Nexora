using System.Reflection;
using ERP_RFQ_Automation.DTOs;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// <c>POST /api/Order</c> used to mint a priced customer document with no commercial case at all.
/// <see cref="Rfq"/> and <see cref="Quote"/> guarded identity with private setters and an
/// inheritance invariant; <see cref="Order"/> had three public setters, no guard, and a manual
/// creation path that set none of them — so the authoritative Sales Order, the one document
/// FR-COM-07 makes the single source of truth, was the one that could exist outside the spine.
///
/// <para>There is deliberately no "allocate a case for a walk-in" path. A commercial case is the
/// one-to-one principal of a Lead, so minting one for a counter sale would manufacture a phantom
/// inquiry — and BRD v3.0 §2 starts the Phase 1 spine at an inquiry, with no counter-sale
/// requirement to serve. An order with no preceding inquiry is refused.</para>
/// </summary>
public sealed class OrderCommercialIdentityTests
{
    private const long Tenant = 97_501;
    private static readonly DateTime Now = new(2026, 8, 9, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The guard has to live on the entity, not in the controller: a controller check is bypassed
    /// by the next service that constructs an <see cref="Order"/>. Assignment is a compile error
    /// now, so this asserts the shape a compiler cannot assert for callers outside this assembly.
    /// </summary>
    [Theory]
    [InlineData(nameof(Order.CommercialCaseId))]
    [InlineData(nameof(Order.NexoraSerial))]
    [InlineData(nameof(Order.ContactId))]
    public void The_commercial_identity_of_an_order_has_no_public_setter(string propertyName)
    {
        var property = typeof(Order).GetProperty(propertyName)!;
        var setter = property.SetMethod;

        Assert.NotNull(setter);
        Assert.False(setter!.IsPublic,
            $"Order.{propertyName} has a public setter, so an order can be given (or robbed of) a " +
            "commercial case by any caller. Rfq and Quote keep theirs private for exactly this reason.");
    }

    [Fact]
    public async Task A_manual_order_naming_no_originating_document_is_refused()
    {
        using var db = new TestDb();
        var fixture = await ArrangeAsync(db);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.CreateManualOrderAsync(Request(fixture, leadId: null, rfqId: null), Tenant));

        Assert.Contains("commercial case", failure.Message, StringComparison.OrdinalIgnoreCase);
        await using var verify = db.ContextFor(Tenant);
        Assert.Empty(await verify.Orders.ToListAsync());
    }

    [Fact]
    public async Task A_manual_order_raised_against_an_rfq_inherits_that_rfqs_case()
    {
        using var db = new TestDb();
        var fixture = await ArrangeAsync(db);

        var created = await fixture.Service.CreateManualOrderAsync(
            Request(fixture, leadId: null, rfqId: fixture.RfqId), Tenant);

        Assert.Equal(fixture.CaseId, created.CommercialCaseId);
        Assert.Equal(fixture.Serial, created.NexoraSerial);
    }

    [Fact]
    public async Task A_manual_order_raised_against_a_lead_inherits_that_leads_case()
    {
        using var db = new TestDb();
        var fixture = await ArrangeAsync(db);

        var created = await fixture.Service.CreateManualOrderAsync(
            Request(fixture, leadId: fixture.LeadId, rfqId: null), Tenant);

        Assert.Equal(fixture.CaseId, created.CommercialCaseId);
        Assert.Equal(fixture.Serial, created.NexoraSerial);
    }

    /// <summary>
    /// The originating document is re-read inside the caller's tenant. Naming another tenant's RFQ
    /// must not borrow its case — that would print a foreign master reference on this tenant's
    /// paperwork.
    /// </summary>
    [Fact]
    public async Task A_manual_order_cannot_borrow_a_case_from_another_tenants_document()
    {
        using var db = new TestDb();
        var fixture = await ArrangeAsync(db);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.CreateManualOrderAsync(
                Request(fixture, leadId: null, rfqId: fixture.ForeignRfqId), Tenant));

        Assert.Contains("not found", failure.Message, StringComparison.OrdinalIgnoreCase);
        await using var verify = db.ContextFor(Tenant);
        Assert.Empty(await verify.Orders.ToListAsync());
    }

    /// <summary>
    /// Finance refuses to invoice an order that names no currency, and the manual order screen
    /// was the one door that let such an order in. The refusal now sits at the door, worded for
    /// the person keying the order, and the currency has to be one of this tenant's own.
    /// </summary>
    [Fact]
    public async Task A_manual_order_naming_no_currency_is_refused_where_it_is_keyed()
    {
        using var db = new TestDb();
        var fixture = await ArrangeAsync(db);

        var failure = await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Service.CreateManualOrderAsync(
                Request(fixture, leadId: fixture.LeadId, rfqId: null, currencyId: null), Tenant));

        Assert.Contains("currency", failure.Message, StringComparison.OrdinalIgnoreCase);
        await using var verify = db.ContextFor(Tenant);
        Assert.Empty(await verify.Orders.ToListAsync());
    }

    [Fact]
    public async Task A_manual_order_cannot_be_denominated_in_another_tenants_currency()
    {
        using var db = new TestDb();
        var fixture = await ArrangeAsync(db);

        var failure = await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Service.CreateManualOrderAsync(
                Request(fixture, leadId: fixture.LeadId, rfqId: null, currencyId: ForeignCurrencyId), Tenant));

        Assert.Contains("currency", failure.Message, StringComparison.OrdinalIgnoreCase);
        await using var verify = db.ContextFor(Tenant);
        Assert.Empty(await verify.Orders.ToListAsync());
    }

    [Fact]
    public async Task A_manual_order_keeps_the_currency_it_was_raised_in()
    {
        using var db = new TestDb();
        var fixture = await ArrangeAsync(db);

        var created = await fixture.Service.CreateManualOrderAsync(
            Request(fixture, leadId: fixture.LeadId, rfqId: null), Tenant);

        await using var verify = db.ContextFor(Tenant);
        var stored = await verify.Orders.SingleAsync(o => o.Id == created.Id);
        Assert.Equal(OwnCurrencyId, stored.CurrencyId);
    }

    /// <summary>
    /// The other door that minted currency-less orders: raising one straight from an RFQ. The RFQ
    /// has no currency of its own, its lines do, so the order inherits the one currency every
    /// line agrees on and is refused otherwise.
    /// </summary>
    [Fact]
    public async Task An_order_raised_from_an_rfq_inherits_the_currency_its_lines_agree_on()
    {
        using var db = new TestDb();
        var fixture = await ArrangeAsync(db);
        await PriceRfqLinesAsync(db, fixture, OwnCurrencyId, OwnCurrencyId);

        var created = await fixture.Service.CreateOrderFromRfqAsync(fixture.RfqId, Tenant);

        await using var verify = db.ContextFor(Tenant);
        var stored = await verify.Orders.SingleAsync(o => o.Id == created.Id);
        Assert.Equal(OwnCurrencyId, stored.CurrencyId);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(OwnCurrencyId, ForeignCurrencyId)]
    public async Task An_order_is_not_raised_from_an_rfq_whose_lines_do_not_agree_on_a_currency(long? first, long? second)
    {
        using var db = new TestDb();
        var fixture = await ArrangeAsync(db);
        await PriceRfqLinesAsync(db, fixture, first, second);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.CreateOrderFromRfqAsync(fixture.RfqId, Tenant));

        Assert.Contains("currency", failure.Message, StringComparison.OrdinalIgnoreCase);
        await using var verify = db.ContextFor(Tenant);
        Assert.Empty(await verify.Orders.ToListAsync());
    }

    private static async Task PriceRfqLinesAsync(TestDb db, Fixture fixture, long? firstCurrency, long? secondCurrency)
    {
        await using var seed = db.ContextFor(null);
        var rfq = await seed.Rfqs.IgnoreQueryFilters().SingleAsync(r => r.Id == fixture.RfqId);
        rfq.CustomerId = fixture.CustomerId;
        var one = AgentSeed.RfqItem(seed, 97_581, fixture.RfqId, "Spine line one", 2);
        one.UnitPrice = 25m; one.CurrencyId = firstCurrency; one.ProductId = fixture.ProductId;
        var two = AgentSeed.RfqItem(seed, 97_582, fixture.RfqId, "Spine line two", 1);
        two.UnitPrice = 40m; two.CurrencyId = secondCurrency; two.ProductId = fixture.ProductId;
        await seed.SaveChangesAsync();
    }

    [Fact]
    public void An_order_refuses_a_source_document_from_another_tenant()
    {
        var order = new Order { BusinessUnitId = 97_501 };
        var lead = new Lead { BusinessUnitId = 97_502 };

        var failure = Assert.Throws<InvalidOperationException>(() => order.InheritCommercialIdentity(lead));

        Assert.Contains("tenant", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(order.CommercialCaseId);
    }

    [Fact]
    public void An_order_refuses_a_source_document_that_has_no_case_of_its_own()
    {
        var order = new Order { BusinessUnitId = Tenant };
        var rfq = new Rfq { BusinessUnitId = Tenant };

        var failure = Assert.Throws<InvalidOperationException>(() => order.InheritCommercialIdentity(rfq));

        Assert.Contains("Nexora Serial", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(order.HasCommercialIdentity);
    }

    /// <summary>
    /// Identity is permanent, exactly as on <see cref="Rfq"/> and <see cref="Quote"/>. Moving a
    /// priced order between cases would rewrite history on both.
    /// </summary>
    [Fact]
    public void An_order_commercial_identity_cannot_be_replaced()
    {
        var first = new Rfq { Id = 1, BusinessUnitId = Tenant };
        var second = new Rfq { Id = 2, BusinessUnitId = Tenant };
        SetRfqIdentity(first, 97_520, "NXR-QA-97501-000001");
        SetRfqIdentity(second, 97_521, "NXR-QA-97501-000002");
        var order = new Order { BusinessUnitId = Tenant };
        order.InheritCommercialIdentity(first);

        var failure = Assert.Throws<InvalidOperationException>(() => order.InheritCommercialIdentity(second));

        Assert.Contains("cannot be replaced", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(97_520, order.CommercialCaseId);
    }

    // ---- fixture ---------------------------------------------------------------------------

    private sealed record Fixture(
        OrderService Service, long LeadId, long RfqId, long ForeignRfqId,
        long CaseId, string Serial, long CustomerId, long ProductId);

    private static async Task<Fixture> ArrangeAsync(TestDb db)
    {
        const long foreignTenant = 97_502;
        long caseId;
        string serial;

        await using (var seed = db.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, Tenant);
            Seed.EnsureBusinessUnit(seed, foreignTenant);
            Seed.Customer(seed, Tenant, Tenant, "Spine customer");
            seed.SetupMasters.AddRange(
                Status(97_531, "OrderStatus", "DRAFT", Tenant),
                Status(97_532, "PaymentStatus", "UNPAID", Tenant));
            seed.Currencies.AddRange(
                new Currency
                {
                    Id = OwnCurrencyId, BusinessUnitId = Tenant, Code = "SAR", CurrencyName = "Saudi riyal",
                    IsBaseCurrency = true, IsActive = true, CreatedBy = "qa", CreatedOn = Now
                },
                new Currency
                {
                    Id = ForeignCurrencyId, BusinessUnitId = foreignTenant, Code = "AED", CurrencyName = "UAE dirham",
                    IsBaseCurrency = true, IsActive = true, CreatedBy = "qa", CreatedOn = Now
                });
            seed.Warehouses.Add(new Warehouse
            {
                Id = 97_541, BusinessUnitId = Tenant, WarehouseCode = "WH-SPINE",
                WarehouseName = "Spine warehouse", IsActive = true, CreatedBy = "qa", CreatedOn = Now
            });
            seed.Products.Add(new Product
            {
                Id = 97_542, Buid = Tenant, PartNo = "PART-SPINE", ProductName = "Spine product",
                WarehouseId = 97_541, QtyOnHand = 10m, ReorderPoint = 0m, IsActive = true,
                CreatedBy = "qa", CreatedOn = Now
            });

            var lead = Seed.Lead(seed, 97_551, Tenant, buyersName: "Spine buyer");
            var foreignLead = Seed.Lead(seed, 97_552, foreignTenant, buyersName: "Foreign buyer");
            await seed.SaveChangesAsync();
            caseId = lead.CommercialCaseId;
            serial = lead.CommercialCaseReference;

            var rfq = AgentSeed.Rfq(seed, 97_561, Tenant, "RFQ-SPINE");
            rfq.LeadId = lead.Id;
            rfq.InheritCommercialIdentity(lead);
            var foreignRfq = AgentSeed.Rfq(seed, 97_562, foreignTenant, "RFQ-FOREIGN");
            foreignRfq.LeadId = foreignLead.Id;
            foreignRfq.InheritCommercialIdentity(foreignLead);
            await seed.SaveChangesAsync();
        }

        var context = db.ContextFor(Tenant);
        var service = new OrderService(new OrderRepository(context), context);
        return new Fixture(service, 97_551, 97_561, 97_562, caseId, serial, Tenant, 97_542);
    }

    private const long OwnCurrencyId = 97_571;
    private const long ForeignCurrencyId = 97_572;

    private static CreateOrderDto Request(Fixture fixture, long? leadId, long? rfqId,
        long? currencyId = OwnCurrencyId) => new()
    {
        LeadId = leadId,
        RfqId = rfqId,
        CurrencyId = currencyId,
        CustomerId = fixture.CustomerId,
        BusinessUnitId = Tenant,
        OrderDate = Now,
        Items =
        [
            new CreateOrderItemDto
            {
                ProductId = fixture.ProductId, Description = "Spine line",
                Quantity = 2m, UnitPrice = 25m, Discount = 0m, TaxAmount = 0m
            }
        ]
    };

    /// <summary>
    /// Rfq guards its identity the same way Order now does, so the fixture reaches through
    /// reflection rather than reopening a setter to build an arrange step.
    /// </summary>
    private static void SetRfqIdentity(Rfq rfq, long commercialCaseId, string nexoraSerial)
    {
        typeof(Rfq).GetProperty(nameof(Rfq.CommercialCaseId), BindingFlags.Public | BindingFlags.Instance)!
            .SetValue(rfq, (long?)commercialCaseId);
        typeof(Rfq).GetProperty(nameof(Rfq.NexoraSerial), BindingFlags.Public | BindingFlags.Instance)!
            .SetValue(rfq, nexoraSerial);
    }

    private static SetupMaster Status(long setupId, string type, string code, long businessUnitId) => new()
    {
        SetupId = setupId, SetupType = type, SetupCode = code, SetupValue = code,
        BusinessUnitId = businessUnitId, IsActive = true, CreatedBy = "qa", CreatedOn = Now
    };
}
