using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.InboundLogistics;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Procurement;
using ERP_RFQ_Automation.Sla;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// FR-MAS-04 — "alert buyer and sales staff when a shipment exceeds the committed ship date or puts
/// the customer's required delivery date at risk".
///
/// <para>Both sweeps are deliberately threshold-free, and that is the point these tests pin.
/// Gate 4's standing lesson is that a new SLA policy column backfilled with zero makes every
/// existing row instantly overdue and mails the whole order book out on the first sweep, after
/// which the channel is filtered and every real alert is lost with it. "The committed ship date has
/// passed and nothing left the factory" and "the derived availability date is after the date the
/// customer asked for" are facts, so no tenant-configured tolerance was introduced and there is no
/// column to backfill wrongly.</para>
/// </summary>
public sealed class Gate5InboundShipmentSlaTests
{
    private const long Bu = 4_300;
    private const long RfqId = 4_310;
    private const long RfqItemId = 4_311;
    private const long SupplierId = 4_320;
    private const long CurrencyId = 4_330;
    private const long WarehouseId = 4_335;
    private const long ProductId = 4_336;
    private const long ManagerRoleId = 4_340;
    private const long MemberRoleId = 4_341;
    private const long BuyerId = 4_350;
    private const long SupervisorId = 4_351;

    private const string BuyerEmail = "buyer@tenant.test";
    private const string SupervisorEmail = "supervisor@tenant.test";

    private static readonly DateTime Anchor = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

    // ---------------------------------------------------------------- ship-date breach

    [Fact]
    public async Task A_committed_ship_date_that_has_passed_with_nothing_departed_alerts_the_buyer()
    {
        using var host = new SweepHost();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await host.SeedAsync(ctx =>
        {
            var order = Order(ctx, 1, "PO-LATE", committedShipDate: today.AddDays(-2));
            Line(ctx, 1, order.Id, ordered: 10m);
            // Ready at factory only. Paperwork exists; nothing has physically moved.
            Shipment(ctx, 1, order.Id, "PO-LATE-S1", InboundShipmentMilestones.ReadyAtFactory);
            ShipmentLine(ctx, 1, 1, 1, 10m);
        });

        await host.CreateWorker().SweepOnceAsync(default);

        var alert = Assert.Single(host.Notifications.Sent);
        Assert.Equal(BuyerEmail, alert.ToEmail);
        Assert.Equal("overdue", alert.Level);
        Assert.Equal("Purchase order PO-LATE", alert.EntityLabel);
    }

    [Fact]
    public async Task An_order_fully_departed_before_the_committed_date_is_not_alerted()
    {
        using var host = new SweepHost();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await host.SeedAsync(ctx =>
        {
            var order = Order(ctx, 1, "PO-SHIPPED", committedShipDate: today.AddDays(-2));
            Line(ctx, 1, order.Id, ordered: 10m);
            Shipment(ctx, 1, order.Id, "PO-SHIPPED-S1", InboundShipmentMilestones.InTransit,
                departedOn: today.AddDays(-3));
            ShipmentLine(ctx, 1, 1, 1, 10m);
        });

        await host.CreateWorker().SweepOnceAsync(default);

        Assert.Empty(host.Notifications.Sent);
    }

    [Fact]
    public async Task A_partially_departed_order_is_still_a_breach_for_the_quantity_left_behind()
    {
        using var host = new SweepHost();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await host.SeedAsync(ctx =>
        {
            var order = Order(ctx, 1, "PO-PART", committedShipDate: today.AddDays(-1));
            Line(ctx, 1, order.Id, ordered: 10m);
            Shipment(ctx, 1, order.Id, "PO-PART-S1", InboundShipmentMilestones.InTransit,
                departedOn: today.AddDays(-2));
            ShipmentLine(ctx, 1, 1, 1, 4m);
        });

        await host.CreateWorker().SweepOnceAsync(default);

        Assert.Single(host.Notifications.Sent);
    }

    [Fact]
    public async Task A_cancelled_shipment_does_not_count_as_departed()
    {
        using var host = new SweepHost();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await host.SeedAsync(ctx =>
        {
            var order = Order(ctx, 1, "PO-CANCELLED-SHIP", committedShipDate: today.AddDays(-1));
            Line(ctx, 1, order.Id, ordered: 10m);
            var shipment = Shipment(ctx, 1, order.Id, "PO-CANCELLED-SHIP-S1",
                InboundShipmentMilestones.Cancelled, departedOn: today.AddDays(-5));
            shipment.CancelledOn = today.AddDays(-3);
            shipment.CancellationReason = "Booking released.";
            ShipmentLine(ctx, 1, 1, 1, 10m);
        });

        await host.CreateWorker().SweepOnceAsync(default);

        Assert.Single(host.Notifications.Sent);
    }

    [Fact]
    public async Task The_breach_alert_is_sent_once_per_committed_date()
    {
        using var host = new SweepHost();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await host.SeedAsync(ctx =>
        {
            var order = Order(ctx, 1, "PO-ONCE", committedShipDate: today.AddDays(-2));
            Line(ctx, 1, order.Id, ordered: 5m);
        });

        var worker = host.CreateWorker();
        await worker.SweepOnceAsync(default);
        await worker.SweepOnceAsync(default);

        Assert.Single(host.Notifications.Sent);
        using var verify = host.UnscopedContext();
        Assert.Equal(1, await verify.Set<SlaEvent>().IgnoreQueryFilters()
            .CountAsync(e => e.EntityType == "inbound-shipment-late"));
    }

    [Fact]
    public async Task A_rejected_or_settled_order_is_never_chased()
    {
        using var host = new SweepHost();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await host.SeedAsync(ctx =>
        {
            var rejected = Order(ctx, 1, "PO-REJECTED", committedShipDate: today.AddDays(-2),
                ackStatus: SupplierAcknowledgementStatuses.Rejected);
            Line(ctx, 1, rejected.Id, ordered: 5m);
            var received = Order(ctx, 2, "PO-RECEIVED", committedShipDate: today.AddDays(-2),
                status: SupplierPurchaseOrderStatuses.Received);
            Line(ctx, 2, received.Id, ordered: 5m);
        });

        await host.CreateWorker().SweepOnceAsync(default);

        Assert.Empty(host.Notifications.Sent);
    }

    // ---------------------------------------------------------------- customer delivery risk

    [Fact]
    public async Task A_material_available_date_after_the_customer_date_alerts_buyer_and_sales()
    {
        using var host = new SweepHost();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await host.SeedAsync(ctx =>
        {
            var order = Order(ctx, 1, "PO-RISK", committedShipDate: today.AddDays(20));
            Line(ctx, 1, order.Id, ordered: 5m);
            var shipment = Shipment(ctx, 1, order.Id, "PO-RISK-S1", InboundShipmentMilestones.InTransit,
                departedOn: today.AddDays(-1));
            // Required 20 August; available 30 August. Ten days late, and only sales can renegotiate
            // the customer date — which is why the escalation reaches managers too.
            shipment.MaterialAvailableDate = new DateOnly(2026, 8, 30);
            shipment.MaterialAvailableBasisKind = MaterialAvailableBasisKinds.Eta;
            ShipmentLine(ctx, 1, 1, 1, 5m);
        }, requiredDesiredDate: new DateTime(2026, 8, 20, 0, 0, 0, DateTimeKind.Utc));

        await host.CreateWorker().SweepOnceAsync(default);

        var risk = host.Notifications.Sent.Where(x => x.Level == "critical").ToList();
        Assert.Equal(2, risk.Count);
        Assert.Contains(risk, x => x.ToEmail == BuyerEmail);
        Assert.Contains(risk, x => x.ToEmail == SupervisorEmail);
        Assert.All(risk, x => Assert.Equal("Shipment PO-RISK-S1", x.EntityLabel));
    }

    [Fact]
    public async Task A_shipment_landing_in_time_is_not_alerted()
    {
        using var host = new SweepHost();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await host.SeedAsync(ctx =>
        {
            var order = Order(ctx, 1, "PO-OK", committedShipDate: today.AddDays(20));
            Line(ctx, 1, order.Id, ordered: 5m);
            var shipment = Shipment(ctx, 1, order.Id, "PO-OK-S1", InboundShipmentMilestones.InTransit,
                departedOn: today.AddDays(-1));
            shipment.MaterialAvailableDate = new DateOnly(2026, 8, 18);
            ShipmentLine(ctx, 1, 1, 1, 5m);
        }, requiredDesiredDate: new DateTime(2026, 8, 20, 0, 0, 0, DateTimeKind.Utc));

        await host.CreateWorker().SweepOnceAsync(default);

        Assert.Empty(host.Notifications.Sent.Where(x => x.Level == "critical"));
    }

    [Fact]
    public async Task A_shipment_with_no_derivable_date_is_not_alerted_on()
    {
        // It IS a problem — usually an unset lead-time policy — but a different one with a different
        // fix. Alerting here would fire on every shipment in a tenant that has not configured its
        // policy yet, which is how a channel gets muted.
        using var host = new SweepHost();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await host.SeedAsync(ctx =>
        {
            var order = Order(ctx, 1, "PO-NODATE", committedShipDate: today.AddDays(20));
            Line(ctx, 1, order.Id, ordered: 5m);
            Shipment(ctx, 1, order.Id, "PO-NODATE-S1", InboundShipmentMilestones.InTransit,
                departedOn: today.AddDays(-1));
            ShipmentLine(ctx, 1, 1, 1, 5m);
        }, requiredDesiredDate: new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));

        await host.CreateWorker().SweepOnceAsync(default);

        Assert.Empty(host.Notifications.Sent.Where(x => x.Level == "critical"));
    }

    [Fact]
    public async Task A_slipped_availability_date_earns_exactly_one_further_alert()
    {
        using var host = new SweepHost();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await host.SeedAsync(ctx =>
        {
            var order = Order(ctx, 1, "PO-SLIP", committedShipDate: today.AddDays(20));
            Line(ctx, 1, order.Id, ordered: 5m);
            var shipment = Shipment(ctx, 1, order.Id, "PO-SLIP-S1", InboundShipmentMilestones.InTransit,
                departedOn: today.AddDays(-1));
            shipment.MaterialAvailableDate = new DateOnly(2026, 8, 25);
            ShipmentLine(ctx, 1, 1, 1, 5m);
        }, requiredDesiredDate: new DateTime(2026, 8, 20, 0, 0, 0, DateTimeKind.Utc));

        var worker = host.CreateWorker();
        await worker.SweepOnceAsync(default);
        await worker.SweepOnceAsync(default);
        var afterFirstDate = host.Notifications.Sent.Count(x => x.Level == "critical");

        // The ETA slips again. Without the derived date in the dedup key the second slip — the one
        // that actually loses the order — would be silent.
        await using (var slip = host.UnscopedContext())
        {
            var shipment = await slip.SupplierShipments.IgnoreQueryFilters().SingleAsync();
            shipment.MaterialAvailableDate = new DateOnly(2026, 9, 10);
            await slip.SaveChangesAsync();
        }
        await worker.SweepOnceAsync(default);

        Assert.Equal(2, afterFirstDate);
        Assert.Equal(4, host.Notifications.Sent.Count(x => x.Level == "critical"));
    }

    // ---------------------------------------------------------------- harness

    private static SupplierPurchaseOrder Order(
        ErpRfqAutomationContext ctx, long id, string number, DateOnly? committedShipDate,
        string status = SupplierPurchaseOrderStatuses.Issued, string? ackStatus = null)
    {
        var order = new SupplierPurchaseOrder
        {
            Id = id,
            BusinessUnitId = Bu,
            RfqId = RfqId,
            DemandSource = SupplierPurchaseOrderDemandSources.Stock,
            SupplierId = SupplierId,
            CurrencyId = CurrencyId,
            PurchaseOrderNumber = number,
            Status = status,
            TotalValue = 100m,
            ApprovedByUserId = BuyerId,
            ApprovedBy = $"user-{BuyerId}",
            ApprovedOn = Anchor,
            SentToSupplierOn = Anchor,
            AcknowledgementStatus = ackStatus,
            AcknowledgedOn = ackStatus is null ? null : Anchor,
            AcknowledgedBy = ackStatus is null ? null : "Supplier contact",
            AcknowledgementNote = ackStatus is null ? null : "Cannot supply.",
            CommittedShipDate = committedShipDate,
            IdempotencyKey = $"mas-po-{id}",
            RequestHash = new string('a', 64),
            Version = 1,
            CreatedOn = Anchor,
            CreatedBy = "seed"
        };
        ctx.SupplierPurchaseOrders.Add(order);
        return order;
    }

    /// <summary>
    /// A purchase order line, with the two upstream rows its foreign keys demand. A PO line cannot
    /// exist without the award and the supplier quote it was raised from — that lineage is enforced
    /// by the schema, and standing it down for a test would be testing a shape production never has.
    /// </summary>
    private static void Line(ErpRfqAutomationContext ctx, long id, long orderId, decimal ordered)
    {
        AgentSeed.Award(ctx, id, Bu, RfqId, SupplierId, unitPrice: 10m, quantity: ordered);
        ctx.SupplierQuotedItems.Add(new SupplierQuotedItem
        {
            Id = id,
            BusinessUnitId = Bu,
            SupplierId = SupplierId,
            RfqId = RfqId,
            RfqItemId = RfqItemId,
            ProductId = ProductId,
            CurrencyId = CurrencyId,
            Quantity = ordered,
            UnitPrice = 10m,
            LandedUnitCost = 11m,
            QuoteRevision = 1,
            IsActive = true,
            CreatedBy = "seed",
            CreatedDate = Anchor,
            Version = 1
        });
        ctx.SupplierPurchaseOrderLines.Add(new SupplierPurchaseOrderLine
        {
            Id = id,
            BusinessUnitId = Bu,
            SupplierPurchaseOrderId = orderId,
            SourcingAwardId = id,
            SupplierQuotedItemId = id,
            RfqId = RfqId,
            RfqItemId = RfqItemId,
            ProductId = ProductId,
            WarehouseId = WarehouseId,
            OrderedQuantity = ordered,
            ReceivedQuantity = 0m,
            ShippedQuantity = 0m,
            UnitCost = 10m,
            LandedUnitCost = 11m,
            Version = 1
        });
    }

    private static SupplierShipment Shipment(
        ErpRfqAutomationContext ctx, long id, long orderId, string number, string milestone,
        DateOnly? departedOn = null)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var shipment = new SupplierShipment
        {
            Id = id,
            BusinessUnitId = Bu,
            SupplierPurchaseOrderId = orderId,
            ShipmentNumber = number,
            Milestone = milestone,
            MilestoneOccurredOn = departedOn ?? today,
            ReadyAtFactoryOn = today.AddDays(-10),
            DepartedOriginOn = departedOn,
            TrackingSource = InboundShipmentTrackingSources.Manual,
            IdempotencyKey = $"mas-ship-{id}",
            RequestHash = new string('b', 64),
            Version = 1,
            CreatedOn = Anchor,
            CreatedBy = "seed"
        };
        ctx.SupplierShipments.Add(shipment);
        return shipment;
    }

    private static void ShipmentLine(
        ErpRfqAutomationContext ctx, long id, long shipmentId, long orderLineId, decimal quantity)
        => ctx.SupplierShipmentLines.Add(new SupplierShipmentLine
        {
            Id = id,
            BusinessUnitId = Bu,
            SupplierShipmentId = shipmentId,
            SupplierPurchaseOrderLineId = orderLineId,
            ProductId = ProductId,
            ShippedQuantity = quantity,
            Version = 1
        });

    private sealed record SentAlert(string ToEmail, string Level, string EntityLabel, long BusinessUnitId);

    private sealed class CapturingNotifications : ISlaNotifications
    {
        private readonly object _gate = new();
        public List<SentAlert> Sent { get; } = new();

        public Task<SlaSendResult> SendDeadlineAlertAsync(
            string toEmail, string? toName, string level, string entityLabel,
            string headline, string detail, long businessUnitId, CancellationToken ct = default)
        {
            lock (_gate) Sent.Add(new SentAlert(toEmail, level, entityLabel, businessUnitId));
            return Task.FromResult(new SlaSendResult(SlaSendOutcome.Sent, "test-transport", "accepted"));
        }

        public Task<SlaSendResult> SendStaleQuotesDigestAsync(
            string toEmail, string? toName, IReadOnlyList<StaleQuoteDigestLine> lines,
            long businessUnitId, CancellationToken ct = default)
        {
            lock (_gate) Sent.Add(new SentAlert(toEmail, "stale", "digest", businessUnitId));
            return Task.FromResult(new SlaSendResult(SlaSendOutcome.Sent, "test-transport", "accepted"));
        }
    }

    private sealed class NoOpOutcomes : IQuoteOutcomeService
    {
        public Task<ERP_RFQ_Automation.DTOs.QuoteDTOs.QuoteResponseDTO> SetOutcomeAsync(
            long quoteId, long businessUnitId, string actorEmail, string outcome,
            string? reasonCode = null, string? note = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> ExpireAsync(long quoteId, string reasonCode = "AUTO_EXPIRED", CancellationToken ct = default)
            => Task.FromResult(false);

        public Task MarkRespondedAsync(long quoteId, long businessUnitId, string actorEmail, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<OutcomeReasonDto>> GetOutcomeReasonsAsync(
            long businessUnitId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<OutcomeReasonDto>>(Array.Empty<OutcomeReasonDto>());
    }

    private sealed class SweepHost : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _provider;
        private readonly DbContextOptions<ErpRfqAutomationContext> _rawOptions;

        public CapturingNotifications Notifications { get; } = new();

        public SweepHost()
        {
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
            _rawOptions = new DbContextOptionsBuilder<ErpRfqAutomationContext>()
                .UseSqlite(_connection)
                .Options;
            using (var create = new ErpRfqAutomationContext(_rawOptions, new StubTenant(null)))
            {
                create.Database.EnsureCreated();
                // SQLite stores decimals as TEXT, so its numeric CHECK comparisons diverge from
                // PostgreSQL. The seed here writes rows the application would never write (a
                // shipment with a pre-set milestone), so the constraints are stood down exactly as
                // the procurement fixture does.
                create.Database.ExecuteSqlRaw("PRAGMA ignore_check_constraints = ON");
            }

            var services = new ServiceCollection();
            services.AddSingleton<ITenantScopeAccessor, TenantScopeAccessor>();
            services.AddScoped<ITenantContext>(sp =>
                new StubTenant(sp.GetRequiredService<ITenantScopeAccessor>().BusinessUnitId));
            services.AddDbContext<ErpRfqAutomationContext>(
                o => o.UseSqlite(_connection), ServiceLifetime.Scoped);
            services.AddSingleton<ISlaNotifications>(Notifications);
            services.AddScoped<IQuoteOutcomeService, NoOpOutcomes>();
            _provider = services.BuildServiceProvider();
        }

        public SlaSweepWorker CreateWorker() => new(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            _provider.GetRequiredService<ITenantScopeAccessor>(),
            NullLogger<SlaSweepWorker>.Instance);

        public ErpRfqAutomationContext UnscopedContext() => new(_rawOptions, new StubTenant(null));

        public async Task SeedAsync(Action<ErpRfqAutomationContext> addRows, DateTime? requiredDesiredDate = null)
        {
            await using var seed = UnscopedContext();
            seed.Database.ExecuteSqlRaw("PRAGMA ignore_check_constraints = ON");

            Seed.EnsureBusinessUnit(seed, Bu);
            await seed.SaveChangesAsync();

            seed.SetupMasters.Add(Role(ManagerRoleId, "MANAGER", "Manager", RoleRanks.Manager));
            seed.SetupMasters.Add(Role(MemberRoleId, "MEMBER", "Member", RoleRanks.Member));
            AgentSeed.Rfq(seed, RfqId, Bu);
            var rfqLine = AgentSeed.RfqItem(seed, RfqItemId, RfqId, "QA Product", 10);
            rfqLine.RequiredDesiredDate = requiredDesiredDate;
            AgentSeed.Supplier(seed, SupplierId, Bu, "QA Supplier", "supplier@example.test");
            seed.Currencies.Add(new Currency
            {
                Id = CurrencyId, BusinessUnitId = Bu, Code = "SAR", CurrencyName = "Saudi Riyal",
                ExchangeRate = 1m, IsBaseCurrency = true, IsActive = true,
                CreatedBy = "seed", CreatedOn = Anchor
            });
            seed.Warehouses.Add(new Warehouse
            {
                Id = WarehouseId, BusinessUnitId = Bu, WarehouseCode = "QA-MAS",
                WarehouseName = "QA Warehouse", IsActive = true, CreatedBy = "seed", CreatedOn = Anchor
            });
            seed.Products.Add(new Product
            {
                Id = ProductId, Buid = Bu, PartNo = "QA-MAS-PART", ProductName = "QA Product",
                WarehouseId = WarehouseId, QtyOnHand = 0m, ReorderPoint = 0, IsActive = true,
                CreatedBy = "seed", CreatedOn = Anchor
            });
            // Ship-date reminder and acknowledgement escalation are switched OFF so these tests
            // observe only the two FR-MAS-04 sweeps. A non-positive value means "not configured",
            // which is the very rule Gate 4 established.
            seed.Set<SlaPolicy>().Add(new SlaPolicy
            {
                BusinessUnitId = Bu,
                SupplierShipDateReminderDays = 0,
                SupplierAckEscalationHours = 0,
                UnassignedHours = 0,
                WarnDaysBeforeClose = 0,
                CriticalDaysBeforeClose = 0,
                StaleQuoteDays = 3650,
                QuoteNoResponseExpiryDays = 3650,
                ApprovalEscalationHours = 0,
                CreatedOn = Anchor,
                UpdatedOn = Anchor
            });
            await seed.SaveChangesAsync();

            seed.Users.Add(User(SupervisorId, SupervisorEmail, "Sam", ManagerRoleId, managerId: null));
            await seed.SaveChangesAsync();
            seed.Users.Add(User(BuyerId, BuyerEmail, "Bea", MemberRoleId, managerId: SupervisorId));
            await seed.SaveChangesAsync();

            addRows(seed);
            await seed.SaveChangesAsync();
        }

        private static SetupMaster Role(long setupId, string code, string value, short rank) => new()
        {
            SetupId = setupId, SetupType = "Role", SetupCode = code, SetupValue = value,
            BusinessUnitId = Bu, RoleRank = rank, IsActive = true,
            CreatedBy = "seed", CreatedOn = Anchor
        };

        private static User User(long id, string email, string firstName, long roleId, long? managerId) => new()
        {
            Id = id, FirstName = firstName, LastName = "Tester", Email = email,
            PasswordHash = "x", ImageUrl = "n/a", Buid = Bu, RoleId = roleId, ManagerId = managerId,
            IsActive = true, CreatedBy = "seed", CreatedOn = Anchor
        };

        public void Dispose()
        {
            _provider.Dispose();
            _connection.Dispose();
        }
    }
}
