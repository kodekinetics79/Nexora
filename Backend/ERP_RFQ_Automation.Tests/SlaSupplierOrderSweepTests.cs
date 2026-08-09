using ERP_RFQ_Automation.Authorization;
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
/// FR-SPO-07 — "remind buyers/suppliers ahead of committed ship dates and escalate overdue
/// acknowledgements".
///
/// <para>Before this, <c>SlaSweepWorker</c> contained ZERO references to supplier purchase
/// orders: its five sweeps covered lead deadlines, unassigned leads, quote expiry, stale
/// quotes and copilot approvals. An order could sit with a supplier unanswered forever and
/// nothing in the platform would notice, and a committed ship date could arrive with nobody
/// told. These tests pin both new sweeps, their send-once behaviour, and — the part the rest
/// of the engine still gets wrong — that the clocks count WORKING time with a Saudi
/// Friday–Saturday weekend rather than raw calendar time.</para>
/// </summary>
public sealed class SlaSupplierOrderSweepTests
{
    private const long Bu = 4_100;
    private const long RfqId = 4_110;
    private const long SupplierId = 4_120;
    private const long CurrencyId = 4_130;
    private const long ManagerRoleId = 4_140;
    private const long MemberRoleId = 4_141;
    private const long BuyerId = 4_150;
    private const long SupervisorId = 4_151;
    private const long LonerId = 4_152;

    private const string BuyerEmail = "buyer@tenant.test";
    private const string SupervisorEmail = "supervisor@tenant.test";

    private static readonly DateTime Anchor = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

    // ---------------------------------------------------------------- business calendar

    [Fact]
    public void The_weekend_is_friday_and_saturday()
    {
        // Aug 3 2026 is a Monday (the same anchor SlaSweepWorkerScaleOutTests uses).
        var monday = new DateOnly(2026, 8, 3);

        Assert.False(BusinessCalendar.IsWeekend(monday));                 // Mon
        Assert.False(BusinessCalendar.IsWeekend(monday.AddDays(3)));      // Thu
        Assert.True(BusinessCalendar.IsWeekend(monday.AddDays(4)));       // Fri
        Assert.True(BusinessCalendar.IsWeekend(monday.AddDays(5)));       // Sat
        Assert.False(BusinessCalendar.IsWeekend(monday.AddDays(6)));      // Sun — a working day here
    }

    [Fact]
    public void Business_days_step_over_the_weekend()
    {
        var wednesday = new DateOnly(2026, 8, 5);

        // Thu, (skip Fri+Sat), Sun, Mon.
        Assert.Equal(new DateOnly(2026, 8, 10), BusinessCalendar.AddBusinessDays(wednesday, 3));
        // A calendar implementation would have said Saturday.
        Assert.NotEqual(wednesday.AddDays(3), BusinessCalendar.AddBusinessDays(wednesday, 3));
        Assert.Equal(wednesday, BusinessCalendar.AddBusinessDays(wednesday, 0));

        Assert.Equal(3, BusinessCalendar.BusinessDaysUntil(wednesday, new DateOnly(2026, 8, 10)));
        Assert.Equal(0, BusinessCalendar.BusinessDaysUntil(wednesday, wednesday));
        Assert.Equal(0, BusinessCalendar.BusinessDaysUntil(wednesday, wednesday.AddDays(-1)));
    }

    [Fact]
    public void A_48_hour_window_opened_on_thursday_evening_expires_on_monday_not_saturday()
    {
        var thursdayEvening = new DateTime(2026, 8, 6, 20, 0, 0, DateTimeKind.Utc);

        var due = BusinessCalendar.AddBusinessTime(thursdayEvening, TimeSpan.FromHours(48));

        // 4h Thursday + 24h Sunday + 20h Monday. The calendar answer — Saturday 20:00 — would
        // have escalated into a closed office and charged the supplier a weekend they could
        // not have worked.
        Assert.Equal(new DateTime(2026, 8, 10, 20, 0, 0, DateTimeKind.Utc), due);
        Assert.NotEqual(thursdayEvening.AddHours(48), due);
    }

    [Fact]
    public void A_clock_started_inside_the_weekend_does_not_tick_until_the_office_reopens()
    {
        var fridayNoon = new DateTime(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc);

        // Accrual starts Sunday 00:00; 8 working hours later is Sunday 08:00.
        Assert.Equal(new DateTime(2026, 8, 9, 8, 0, 0, DateTimeKind.Utc),
            BusinessCalendar.AddBusinessTime(fridayNoon, TimeSpan.FromHours(8)));
    }

    // ---------------------------------------------------------------- ship-date reminder

    [Fact]
    public async Task A_reminder_fires_once_before_the_committed_ship_date()
    {
        using var host = new SweepHost();
        var shipDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1);
        await host.SeedAsync(ctx => Order(ctx, 1, "PO-SHIP-1",
            SupplierPurchaseOrderStatuses.Sent,
            approvedOn: DateTime.UtcNow.AddDays(-1), approverId: BuyerId,
            committedShipDate: shipDate));

        await host.CreateWorker().SweepOnceAsync(default);

        var alert = Assert.Single(host.Notifications.Sent);
        Assert.Equal(BuyerEmail, alert.ToEmail);
        Assert.Equal("warn", alert.Level);
        Assert.Equal("Purchase order PO-SHIP-1", alert.EntityLabel);
        Assert.Equal(Bu, alert.BusinessUnitId);
    }

    [Fact]
    public async Task The_same_reminder_does_not_re_fire_on_a_second_sweep()
    {
        using var host = new SweepHost();
        await host.SeedAsync(ctx => Order(ctx, 1, "PO-SHIP-1",
            SupplierPurchaseOrderStatuses.Sent,
            approvedOn: DateTime.UtcNow.AddDays(-1), approverId: BuyerId,
            committedShipDate: DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1)));

        var worker = host.CreateWorker();
        await worker.SweepOnceAsync(default);
        await worker.SweepOnceAsync(default);

        Assert.Single(host.Notifications.Sent);
        using var verify = host.UnscopedContext();
        Assert.Equal(1, await verify.Set<SlaEvent>().IgnoreQueryFilters()
            .CountAsync(e => e.EntityType == "supplier-order-ship"));
    }

    [Fact]
    public async Task Two_instances_sweeping_together_send_the_reminder_once()
    {
        using var host = new SweepHost();
        await host.SeedAsync(ctx => Order(ctx, 1, "PO-SHIP-1",
            SupplierPurchaseOrderStatuses.Sent,
            approvedOn: DateTime.UtcNow.AddDays(-1), approverId: BuyerId,
            committedShipDate: DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1)));

        await Task.WhenAll(
            host.CreateWorker().SweepOnceAsync(default),
            host.CreateWorker().SweepOnceAsync(default));

        Assert.Single(host.Notifications.Sent);
    }

    [Fact]
    public async Task A_ship_date_beyond_the_window_or_already_past_is_not_reminded()
    {
        using var host = new SweepHost();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await host.SeedAsync(ctx =>
        {
            Order(ctx, 1, "PO-FAR", SupplierPurchaseOrderStatuses.Sent,
                approvedOn: DateTime.UtcNow.AddDays(-1), approverId: BuyerId,
                committedShipDate: today.AddDays(30));
            Order(ctx, 2, "PO-PAST", SupplierPurchaseOrderStatuses.Sent,
                approvedOn: DateTime.UtcNow.AddDays(-1), approverId: BuyerId,
                committedShipDate: today.AddDays(-1));
        });

        await host.CreateWorker().SweepOnceAsync(default);

        Assert.Empty(host.Notifications.Sent);
    }

    [Fact]
    public async Task The_reminder_window_is_measured_in_working_days_not_calendar_days()
    {
        // The policy asks for 3 days' notice. The boundary is therefore the date three WORKING
        // days out — which on a Tuesday/Wednesday/Thursday is five calendar days away, because
        // the Friday–Saturday weekend does not count. A calendar implementation would fall
        // silent on exactly those orders.
        using var host = new SweepHost();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var lastDayInside = BusinessCalendar.AddBusinessDays(today, 3);

        await host.SeedAsync(ctx =>
        {
            Order(ctx, 1, "PO-EDGE-IN", SupplierPurchaseOrderStatuses.Sent,
                approvedOn: DateTime.UtcNow.AddDays(-1), approverId: BuyerId,
                committedShipDate: lastDayInside);
            Order(ctx, 2, "PO-EDGE-OUT", SupplierPurchaseOrderStatuses.Sent,
                approvedOn: DateTime.UtcNow.AddDays(-1), approverId: BuyerId,
                committedShipDate: lastDayInside.AddDays(1));
        });

        await host.CreateWorker().SweepOnceAsync(default);

        var alert = Assert.Single(host.Notifications.Sent);
        Assert.Equal("Purchase order PO-EDGE-IN", alert.EntityLabel);
    }

    [Fact]
    public async Task A_supplier_who_moves_the_committed_date_earns_a_fresh_reminder()
    {
        // The dedup key carries the DATE. "Once ever per order" would mean a counter-offered
        // ship date the buyer was never warned about.
        using var host = new SweepHost();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await host.SeedAsync(ctx => Order(ctx, 1, "PO-SHIP-1",
            SupplierPurchaseOrderStatuses.Sent,
            approvedOn: DateTime.UtcNow.AddDays(-1), approverId: BuyerId,
            committedShipDate: today.AddDays(1)));

        var worker = host.CreateWorker();
        await worker.SweepOnceAsync(default);

        await using (var move = host.UnscopedContext())
        {
            var order = await move.SupplierPurchaseOrders.IgnoreQueryFilters().SingleAsync(o => o.Id == 1);
            order.CommittedShipDate = today.AddDays(2);
            await move.SaveChangesAsync();
        }
        await worker.SweepOnceAsync(default);

        Assert.Equal(2, host.Notifications.Sent.Count);
    }

    [Fact]
    public async Task An_order_the_supplier_rejected_or_already_shipped_is_not_reminded()
    {
        using var host = new SweepHost();
        var shipDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1);
        await host.SeedAsync(ctx =>
        {
            Order(ctx, 1, "PO-REJECTED", SupplierPurchaseOrderStatuses.Sent,
                approvedOn: DateTime.UtcNow.AddDays(-1), approverId: BuyerId,
                committedShipDate: shipDate,
                ackStatus: SupplierAcknowledgementStatuses.Rejected, ackOn: DateTime.UtcNow.AddHours(-1));
            Order(ctx, 2, "PO-SHIPPED", SupplierPurchaseOrderStatuses.Shipped,
                approvedOn: DateTime.UtcNow.AddDays(-1), approverId: BuyerId,
                committedShipDate: shipDate,
                ackStatus: SupplierAcknowledgementStatuses.Accepted, ackOn: DateTime.UtcNow.AddHours(-1));
        });

        await host.CreateWorker().SweepOnceAsync(default);

        Assert.Empty(host.Notifications.Sent);
    }

    // ------------------------------------------------------- acknowledgement escalation

    [Fact]
    public async Task An_unacknowledged_sent_order_escalates_after_the_window()
    {
        using var host = new SweepHost();
        await host.SeedAsync(ctx => Order(ctx, 1, "PO-ACK-1",
            SupplierPurchaseOrderStatuses.Sent,
            approvedOn: DateTime.UtcNow.AddDays(-30), approverId: BuyerId));

        await host.CreateWorker().SweepOnceAsync(default);

        var alert = Assert.Single(host.Notifications.Sent);
        Assert.Equal(SupervisorEmail, alert.ToEmail);          // the buyer's own manager
        Assert.Equal("escalated", alert.Level);
        Assert.Equal("Purchase order PO-ACK-1", alert.EntityLabel);
    }

    [Fact]
    public async Task An_acknowledged_order_never_escalates()
    {
        using var host = new SweepHost();
        await host.SeedAsync(ctx =>
        {
            Order(ctx, 1, "PO-ACCEPTED", SupplierPurchaseOrderStatuses.Acknowledged,
                approvedOn: DateTime.UtcNow.AddDays(-30), approverId: BuyerId,
                ackStatus: SupplierAcknowledgementStatuses.Accepted, ackOn: DateTime.UtcNow.AddDays(-29));
            // Countered still counts as answered: the buyer has something to act on.
            Order(ctx, 2, "PO-COUNTERED", SupplierPurchaseOrderStatuses.Sent,
                approvedOn: DateTime.UtcNow.AddDays(-30), approverId: BuyerId,
                ackStatus: SupplierAcknowledgementStatuses.Countered, ackOn: DateTime.UtcNow.AddDays(-29));
            // Never dispatched: nothing has been asked of the supplier yet.
            Order(ctx, 3, "PO-APPROVED-ONLY", SupplierPurchaseOrderStatuses.Approved,
                approvedOn: DateTime.UtcNow.AddDays(-30), approverId: BuyerId);
            // Dead order.
            Order(ctx, 4, "PO-CANCELLED", SupplierPurchaseOrderStatuses.Cancelled,
                approvedOn: DateTime.UtcNow.AddDays(-30), approverId: BuyerId);
        });

        await host.CreateWorker().SweepOnceAsync(default);

        Assert.Empty(host.Notifications.Sent);
    }

    [Fact]
    public async Task The_escalation_is_sent_once_and_not_repeated_by_the_next_sweep()
    {
        using var host = new SweepHost();
        await host.SeedAsync(ctx => Order(ctx, 1, "PO-ACK-1",
            SupplierPurchaseOrderStatuses.Sent,
            approvedOn: DateTime.UtcNow.AddDays(-30), approverId: BuyerId));

        var worker = host.CreateWorker();
        await worker.SweepOnceAsync(default);
        await worker.SweepOnceAsync(default);

        Assert.Single(host.Notifications.Sent);
        using var verify = host.UnscopedContext();
        Assert.Equal(1, await verify.Set<SlaEvent>().IgnoreQueryFilters()
            .CountAsync(e => e.EntityType == "supplier-order-ack" && e.Level == "escalated"));
    }

    [Fact]
    public async Task With_no_line_manager_the_escalation_falls_back_to_role_rank_supervisors()
    {
        // The same ladder SweepPendingApprovalsAsync uses: Users.ManagerId first, then the
        // tenant's managers/admins selected by SetupMaster.RoleRank — never by role NAME.
        using var host = new SweepHost();
        await host.SeedAsync(ctx => Order(ctx, 1, "PO-ACK-ORPHAN",
            SupplierPurchaseOrderStatuses.Sent,
            approvedOn: DateTime.UtcNow.AddDays(-30), approverId: LonerId));

        await host.CreateWorker().SweepOnceAsync(default);

        var alert = Assert.Single(host.Notifications.Sent);
        Assert.Equal(SupervisorEmail, alert.ToEmail);
        Assert.Equal("escalated", alert.Level);
    }

    [Fact]
    public async Task The_escalation_window_is_measured_in_working_hours_not_calendar_hours()
    {
        // Eight calendar days have passed against a seven-working-day (168 working hour)
        // window. Any eight consecutive days contain a whole Friday and a whole Saturday, so
        // at most 144 working hours can have elapsed — whatever weekday the test runs on.
        // A calendar clock would have escalated; the business clock must not.
        using var host = new SweepHost(ackEscalationHours: 168);
        await host.SeedAsync(ctx => Order(ctx, 1, "PO-ACK-WEEKEND",
            SupplierPurchaseOrderStatuses.Sent,
            approvedOn: DateTime.UtcNow.AddDays(-8), approverId: BuyerId));

        await host.CreateWorker().SweepOnceAsync(default);

        Assert.Empty(host.Notifications.Sent);
    }

    [Fact]
    public async Task Legacy_ISSUED_orders_are_chased_too()
    {
        // ISSUED is the pre-split word that conflated Approved and Sent. Skipping it would
        // exempt every order raised before the vocabulary changed.
        using var host = new SweepHost();
        await host.SeedAsync(ctx => Order(ctx, 1, "PO-LEGACY",
            SupplierPurchaseOrderStatuses.Issued,
            approvedOn: DateTime.UtcNow.AddDays(-30), approverId: BuyerId));

        await host.CreateWorker().SweepOnceAsync(default);

        Assert.Single(host.Notifications.Sent);
    }

    // ---------------------------------------------------------------- tenant isolation

    [Fact]
    public async Task A_supplier_order_alone_is_enough_activity_for_the_tenant_to_be_swept()
    {
        // The tenant list used to be "distinct BUs on Leads or Quotes". A procurement-only
        // tenant would never have been enumerated, so neither sweep would ever have run.
        using var host = new SweepHost();
        await host.SeedAsync(ctx => Order(ctx, 1, "PO-ONLY",
            SupplierPurchaseOrderStatuses.Sent,
            approvedOn: DateTime.UtcNow.AddDays(-30), approverId: BuyerId));

        var swept = await host.CreateWorker().SweepOnceAsync(default);

        Assert.Equal(1, swept);
        Assert.Single(host.Notifications.Sent);
    }

    // ---------------------------------------------------------------- seeding helpers

    private static SupplierPurchaseOrder Order(
        ErpRfqAutomationContext ctx, long id, string number, string status,
        DateTime? approvedOn = null, long? approverId = null,
        DateOnly? committedShipDate = null, string? ackStatus = null, DateTime? ackOn = null)
    {
        var order = new SupplierPurchaseOrder
        {
            Id = id,
            BusinessUnitId = Bu,
            RfqId = RfqId,
            // STOCK keeps the customer chain legitimately empty — this suite is about SLA
            // clocks, not about the demand discriminator.
            DemandSource = SupplierPurchaseOrderDemandSources.Stock,
            SupplierId = SupplierId,
            CurrencyId = CurrencyId,
            PurchaseOrderNumber = number,
            Status = status,
            TotalValue = 100m,
            // FR-SPO-01's constraint: approver id, name and date are all-or-nothing.
            ApprovedByUserId = approverId,
            ApprovedBy = approverId is null ? null : $"user-{approverId}",
            ApprovedOn = approverId is null ? null : approvedOn ?? Anchor,
            AcknowledgementStatus = ackStatus,
            AcknowledgedOn = ackOn,
            CommittedShipDate = committedShipDate,
            IdempotencyKey = $"sla-po-{id}",
            RequestHash = new string('a', 64),
            Version = 1,
            CreatedOn = approvedOn ?? Anchor,
            CreatedBy = "seed"
        };
        ctx.SupplierPurchaseOrders.Add(order);
        return order;
    }

    private sealed record SentAlert(string ToEmail, string Level, string EntityLabel, long BusinessUnitId);

    private sealed class CapturingNotifications : ISlaNotifications
    {
        private readonly object _gate = new();
        public List<SentAlert> Sent { get; } = new();

        public Task<bool> SendDeadlineAlertAsync(
            string toEmail, string? toName, string level, string entityLabel,
            string headline, string detail, long businessUnitId, CancellationToken ct = default)
        {
            lock (_gate) Sent.Add(new SentAlert(toEmail, level, entityLabel, businessUnitId));
            return Task.FromResult(true);
        }

        public Task<bool> SendStaleQuotesDigestAsync(
            string toEmail, string? toName, IReadOnlyList<StaleQuoteDigestLine> lines,
            long businessUnitId, CancellationToken ct = default)
        {
            lock (_gate) Sent.Add(new SentAlert(toEmail, "stale", "digest", businessUnitId));
            return Task.FromResult(true);
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

    /// <summary>Production-shaped worker host: ambient tenant scope pushed by the worker,
    /// read by a scoped ITenantContext when the DbContext is built.</summary>
    private sealed class SweepHost : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _provider;
        private readonly DbContextOptions<ErpRfqAutomationContext> _rawOptions;
        private readonly int _shipReminderDays;
        private readonly int _ackEscalationHours;

        public CapturingNotifications Notifications { get; } = new();

        public SweepHost(int shipReminderDays = 3, int ackEscalationHours = 48)
        {
            _shipReminderDays = shipReminderDays;
            _ackEscalationHours = ackEscalationHours;

            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
            _rawOptions = new DbContextOptionsBuilder<ErpRfqAutomationContext>()
                .UseSqlite(_connection)
                .Options;
            using (var create = new ErpRfqAutomationContext(_rawOptions, new StubTenant(null)))
                create.Database.EnsureCreated();

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

        public async Task SeedAsync(Action<ErpRfqAutomationContext> addOrders)
        {
            await using var seed = UnscopedContext();

            Seed.EnsureBusinessUnit(seed, Bu);
            await seed.SaveChangesAsync();

            seed.SetupMasters.Add(Role(ManagerRoleId, "MANAGER", "Manager", RoleRanks.Manager));
            seed.SetupMasters.Add(Role(MemberRoleId, "MEMBER", "Member", RoleRanks.Member));
            AgentSeed.Rfq(seed, RfqId, Bu);
            AgentSeed.Supplier(seed, SupplierId, Bu, "QA Supplier", "supplier@example.test");
            seed.Currencies.Add(new Currency
            {
                Id = CurrencyId, BusinessUnitId = Bu, Code = "SAR", CurrencyName = "Saudi Riyal",
                ExchangeRate = 1m, IsBaseCurrency = true, IsActive = true,
                CreatedBy = "seed", CreatedOn = Anchor
            });
            seed.Set<SlaPolicy>().Add(new SlaPolicy
            {
                BusinessUnitId = Bu,
                SupplierShipDateReminderDays = _shipReminderDays,
                SupplierAckEscalationHours = _ackEscalationHours,
                CreatedOn = Anchor,
                UpdatedOn = Anchor
            });
            await seed.SaveChangesAsync();

            seed.Users.Add(User(SupervisorId, SupervisorEmail, "Sam", ManagerRoleId, managerId: null));
            await seed.SaveChangesAsync();
            seed.Users.Add(User(BuyerId, BuyerEmail, "Bea", MemberRoleId, managerId: SupervisorId));
            seed.Users.Add(User(LonerId, "loner@tenant.test", "Lee", MemberRoleId, managerId: null));
            await seed.SaveChangesAsync();

            addOrders(seed);
            await seed.SaveChangesAsync();
        }

        private static SetupMaster Role(long setupId, string code, string value, short rank) => new()
        {
            SetupId = setupId, SetupType = "Role", SetupCode = code, SetupValue = value,
            BusinessUnitId = Bu, RoleRank = rank, IsActive = true,
            CreatedBy = "seed", CreatedOn = Anchor
        };

        private static Models.User User(long id, string email, string firstName, long roleId, long? managerId) => new()
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
