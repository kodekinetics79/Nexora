using ERP_RFQ_Automation.Agent.Models;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.DTOs.QuoteDTOs;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Sla;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// FR-SBF-01 — the two reminder triggers built in Gate 8.
///
/// <para><b>(a) pending Quote/No-Quote decisions</b> and <b>(b) RFQs approaching closure without a
/// decision</b> are the same fact seen from two directions, and both need
/// <c>Rfqitem.ParticipationDecision</c>. Sweep 1 (lead deadlines) could not see it: it chases every
/// lead whose bid closes soon, whether the work is done or not.</para>
///
/// <para><b>(c) supplier responses overdue against SLA</b> reads
/// <c>SupplierSolicitation.DueOn</c> — a column the dispatch path has always written and validated,
/// and which nothing has ever read back.</para>
///
/// <para>Each test below fails if a specific piece of that wiring is removed.</para>
/// </summary>
public sealed class Gate8FollowUpTriggerTests
{
    private const long Bu = 8_400;
    private const long LeadId = 8_410;
    private const long RfqId = 8_420;
    private const long SupplierId = 8_430;
    private const long OwnerId = 8_440;
    private const long ManagerRoleId = 8_450;

    private const string OwnerEmail = "owner@tenant.test";
    private static readonly DateTime Anchor = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    // ───────────────────────────── (a) + (b) undecided lines near close

    /// <summary>
    /// The trigger itself. Without it an RFQ can close with lines nobody ever decided — neither
    /// quoted nor declined — and the buyer simply receives a partial response.
    /// </summary>
    [Fact]
    public async Task An_rfq_closing_soon_with_undecided_lines_reminds_its_owner()
    {
        using var host = new SweepHost();
        await host.SeedAsync(seed =>
        {
            var rfq = Rfq(seed, closingInDays: 2);
            Line(seed, 1, rfq.Id, Rfqitem.ParticipationPending);
            Line(seed, 2, rfq.Id, Rfqitem.ParticipationQuote);
        });

        await host.CreateWorker().SweepOnceAsync(default);

        var alert = Assert.Single(host.Notifications.Sent, a => a.Level == "warn");
        Assert.Equal(OwnerEmail, alert.ToEmail);
        Assert.Contains("undecided", alert.Headline);
        Assert.Contains("1 line", alert.Headline);
    }

    /// <summary>
    /// A signal that fires on everything carries no information. An RFQ closing tomorrow with every
    /// line decided is NOT chased — that is the whole difference between this sweep and the
    /// lead-deadline sweep, and if the participation predicate is dropped this fails.
    /// </summary>
    [Fact]
    public async Task An_rfq_whose_lines_are_all_decided_is_not_chased()
    {
        using var host = new SweepHost();
        await host.SeedAsync(seed =>
        {
            var rfq = Rfq(seed, closingInDays: 1);
            Line(seed, 1, rfq.Id, Rfqitem.ParticipationQuote);
            Line(seed, 2, rfq.Id, Rfqitem.ParticipationNoQuote);
        });

        await host.CreateWorker().SweepOnceAsync(default);

        Assert.Empty(host.Notifications.Sent);
    }

    /// <summary>An RFQ far from closing is not yet worth chasing.</summary>
    [Fact]
    public async Task An_rfq_outside_the_reminder_horizon_is_not_chased()
    {
        using var host = new SweepHost();
        await host.SeedAsync(seed =>
        {
            var rfq = Rfq(seed, closingInDays: 60);
            Line(seed, 1, rfq.Id, Rfqitem.ParticipationPending);
        });

        await host.CreateWorker().SweepOnceAsync(default);

        Assert.Empty(host.Notifications.Sent);
    }

    /// <summary>
    /// Register R12, pinned. A non-positive policy value means NOT CONFIGURED, not "chase
    /// immediately" — the reading that turns a backfilled zero into a mass mailout on first sweep.
    /// </summary>
    [Fact]
    public async Task A_non_positive_reminder_window_means_not_configured_and_sends_nothing()
    {
        using var host = new SweepHost(quoteDecisionReminderDays: 0);
        await host.SeedAsync(seed =>
        {
            var rfq = Rfq(seed, closingInDays: 1);
            Line(seed, 1, rfq.Id, Rfqitem.ParticipationPending);
        });

        await host.CreateWorker().SweepOnceAsync(default);

        Assert.Empty(host.Notifications.Sent);
    }

    /// <summary>Send-once, enforced by the database claim, exactly as every other sweep.</summary>
    [Fact]
    public async Task The_undecided_line_reminder_is_sent_once_per_closing_date()
    {
        using var host = new SweepHost();
        await host.SeedAsync(seed =>
        {
            var rfq = Rfq(seed, closingInDays: 2);
            Line(seed, 1, rfq.Id, Rfqitem.ParticipationPending);
        });

        await host.CreateWorker().SweepOnceAsync(default);
        await host.CreateWorker().SweepOnceAsync(default);

        Assert.Single(host.Notifications.Sent);
    }

    // ───────────────────────────── (c) supplier response overdue

    /// <summary>
    /// <c>SupplierSolicitation.DueOn</c> is written on dispatch, rendered into the outbound email,
    /// and until now read back by nothing. This is the read that makes it load-bearing.
    /// </summary>
    [Fact]
    public async Task A_supplier_who_missed_the_response_deadline_alerts_the_owner()
    {
        using var host = new SweepHost(quoteDecisionReminderDays: 0);
        await host.SeedAsync(seed =>
        {
            var rfq = Rfq(seed, closingInDays: 20);
            Solicitation(seed, dueInDays: -1);
        });

        await host.CreateWorker().SweepOnceAsync(default);

        var alert = Assert.Single(host.Notifications.Sent, a => a.Level == "overdue");
        Assert.Equal(OwnerEmail, alert.ToEmail);
        Assert.Contains("overdue", alert.Headline, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A deadline still in the future is not a breach.</summary>
    [Fact]
    public async Task A_supplier_still_inside_its_deadline_is_not_chased()
    {
        using var host = new SweepHost(quoteDecisionReminderDays: 0);
        await host.SeedAsync(seed =>
        {
            Rfq(seed, closingInDays: 20);
            Solicitation(seed, dueInDays: 3);
        });

        await host.CreateWorker().SweepOnceAsync(default);

        Assert.Empty(host.Notifications.Sent);
    }

    /// <summary>
    /// No deadline, no alert. Inventing one would fire this on every supplier RFQ in a tenant that
    /// does not set response deadlines, which is how an alerting channel gets muted for good.
    /// </summary>
    [Fact]
    public async Task A_solicitation_with_no_deadline_is_silent()
    {
        using var host = new SweepHost(quoteDecisionReminderDays: 0);
        await host.SeedAsync(seed =>
        {
            Rfq(seed, closingInDays: 20);
            Solicitation(seed, dueInDays: null);
        });

        await host.CreateWorker().SweepOnceAsync(default);

        Assert.Empty(host.Notifications.Sent);
    }

    /// <summary>A supplier who answered is not late, whatever the date says.</summary>
    [Fact]
    public async Task A_supplier_that_responded_is_not_chased()
    {
        using var host = new SweepHost(quoteDecisionReminderDays: 0);
        await host.SeedAsync(seed =>
        {
            Rfq(seed, closingInDays: 20);
            var s = Solicitation(seed, dueInDays: -5);
            s.RespondedOn = DateTime.UtcNow.AddDays(-6);
            s.Status = SolicitationStatus.Responded;
        });

        await host.CreateWorker().SweepOnceAsync(default);

        Assert.Empty(host.Notifications.Sent);
    }

    [Fact]
    public async Task The_supplier_response_alert_is_sent_once_per_due_date()
    {
        using var host = new SweepHost(quoteDecisionReminderDays: 0);
        await host.SeedAsync(seed =>
        {
            Rfq(seed, closingInDays: 20);
            Solicitation(seed, dueInDays: -2);
        });

        await host.CreateWorker().SweepOnceAsync(default);
        await host.CreateWorker().SweepOnceAsync(default);

        Assert.Single(host.Notifications.Sent);
    }

    // ───────────────────────────── seeding helpers

    private static Rfq Rfq(ErpRfqAutomationContext ctx, int closingInDays)
    {
        var existing = ctx.Rfqs.Local.FirstOrDefault(r => r.Id == RfqId);
        if (existing is not null) return existing;

        var rfq = new Rfq
        {
            Id = RfqId,
            Rfqno = "RFQ-8420",
            BuyersName = "Acme Buyer",
            RecDate = Anchor,
            BidClosingDate = DateTime.UtcNow.Date.AddDays(closingInDays),
            BusinessUnitId = Bu,
            LeadId = LeadId,
            CreatedBy = "seed",
            CreatedDate = Anchor
        };
        ctx.Rfqs.Add(rfq);
        return rfq;
    }

    private static void Line(ErpRfqAutomationContext ctx, long id, long rfqId, string decision)
    {
        var item = new Rfqitem
        {
            Id = id, Rfqid = rfqId, ProductShortName = $"Item {id}", Quantity = 1,
            CreatedBy = "seed", CreatedDate = Anchor
        };
        // ParticipationDecision has a private setter and a domain method that enforces the reason
        // rule; the seed goes through the domain so the fixture cannot create a state the
        // application could not.
        if (decision != Rfqitem.ParticipationPending)
            item.DecideParticipation(decision,
                decision == Rfqitem.ParticipationNoQuote ? "Seeded decline" : null, "seed", Anchor);
        ctx.Rfqitems.Add(item);
    }

    private static SupplierSolicitation Solicitation(ErpRfqAutomationContext ctx, int? dueInDays)
    {
        var s = new SupplierSolicitation
        {
            Id = 1,
            BusinessUnitId = Bu,
            RfqId = RfqId,
            SupplierId = SupplierId,
            SupplierRfqNumber = "SRFQ-1",
            IdempotencyKey = "seed:solicitation:1",
            RequestHash = new string('0', 64),
            Status = SolicitationStatus.Sent,
            SentOn = DateTime.UtcNow.AddDays(-10),
            DueOn = dueInDays is null ? null : DateTime.UtcNow.AddDays(dueInDays.Value),
            Channel = "Email",
            CreatedOn = Anchor,
            UpdatedOn = Anchor
        };
        ctx.Set<SupplierSolicitation>().Add(s);
        return s;
    }

    // ───────────────────────────── harness

    private sealed record SentAlert(string ToEmail, string Level, string EntityLabel, string Headline);

    private sealed class CapturingNotifications : ISlaNotifications
    {
        private readonly object _gate = new();
        public List<SentAlert> Sent { get; } = new();

        public Task<SlaSendResult> SendDeadlineAlertAsync(string toEmail, string? toName, string level,
            string entityLabel, string headline, string detail, long businessUnitId,
            CancellationToken ct = default)
        {
            lock (_gate) Sent.Add(new SentAlert(toEmail, level, entityLabel, headline));
            return Task.FromResult(new SlaSendResult(SlaSendOutcome.Sent, "test-transport", "accepted"));
        }

        public Task<SlaSendResult> SendStaleQuotesDigestAsync(string toEmail, string? toName,
            IReadOnlyList<StaleQuoteDigestLine> lines, long businessUnitId, CancellationToken ct = default)
        {
            lock (_gate) Sent.Add(new SentAlert(toEmail, "stale", "digest", "digest"));
            return Task.FromResult(new SlaSendResult(SlaSendOutcome.Sent, "test-transport", "accepted"));
        }
    }

    private sealed class NoOpOutcomes : IQuoteOutcomeService
    {
        public Task<QuoteResponseDTO> SetOutcomeAsync(long quoteId, long businessUnitId, string actorEmail,
            string outcome, string? reasonCode = null, string? note = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> ExpireAsync(long quoteId, string reasonCode = "AUTO_EXPIRED", CancellationToken ct = default)
            => Task.FromResult(false);

        public Task MarkRespondedAsync(long quoteId, long businessUnitId, string actorEmail, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<OutcomeReasonDto>> GetOutcomeReasonsAsync(long businessUnitId,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<OutcomeReasonDto>>(Array.Empty<OutcomeReasonDto>());
    }

    private sealed class SweepHost : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _provider;
        private readonly DbContextOptions<ErpRfqAutomationContext> _rawOptions;
        private readonly int _quoteDecisionReminderDays;

        public CapturingNotifications Notifications { get; } = new();

        public SweepHost(int quoteDecisionReminderDays = 3)
        {
            _quoteDecisionReminderDays = quoteDecisionReminderDays;
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
            _rawOptions = new DbContextOptionsBuilder<ErpRfqAutomationContext>().UseSqlite(_connection).Options;
            using (var create = new ErpRfqAutomationContext(_rawOptions, new StubTenant(null)))
            {
                create.Database.EnsureCreated();
                create.Database.ExecuteSqlRaw("PRAGMA ignore_check_constraints = ON");
            }

            var services = new ServiceCollection();
            services.AddSingleton<ITenantScopeAccessor, TenantScopeAccessor>();
            services.AddScoped<ITenantContext>(sp =>
                new StubTenant(sp.GetRequiredService<ITenantScopeAccessor>().BusinessUnitId));
            services.AddDbContext<ErpRfqAutomationContext>(o => o.UseSqlite(_connection), ServiceLifetime.Scoped);
            services.AddSingleton<ISlaNotifications>(Notifications);
            services.AddScoped<IQuoteOutcomeService, NoOpOutcomes>();
            _provider = services.BuildServiceProvider();
        }

        public SlaSweepWorker CreateWorker() => new(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            _provider.GetRequiredService<ITenantScopeAccessor>(),
            NullLogger<SlaSweepWorker>.Instance);

        public ErpRfqAutomationContext UnscopedContext() => new(_rawOptions, new StubTenant(null));

        public async Task SeedAsync(Action<ErpRfqAutomationContext> addRows)
        {
            await using var seed = UnscopedContext();
            seed.Database.ExecuteSqlRaw("PRAGMA ignore_check_constraints = ON");

            Seed.EnsureBusinessUnit(seed, Bu);
            await seed.SaveChangesAsync();

            seed.SetupMasters.Add(new SetupMaster
            {
                SetupId = ManagerRoleId, BusinessUnitId = Bu, SetupType = "Role", SetupCode = "MANAGER",
                SetupValue = "Manager", RoleRank = RoleRanks.Manager, IsActive = true,
                CreatedBy = "seed", CreatedOn = Anchor
            });
            seed.Users.Add(new User
            {
                Id = OwnerId, FirstName = "Owner", LastName = "One", Email = OwnerEmail,
                PasswordHash = "x", ImageUrl = "n/a", Buid = Bu, RoleId = ManagerRoleId,
                IsActive = true, CreatedBy = "seed", CreatedOn = Anchor
            });
            AgentSeed.Supplier(seed, SupplierId, Bu, "QA Supplier", "supplier@example.test");
            await seed.SaveChangesAsync();

            // The lead exists only so the RFQ has an assignee to resolve; its own bid closing date
            // is left unset so sweep 1 (lead deadlines) cannot fire and confuse these assertions.
            var lead = Seed.Lead(seed, leadId: LeadId, businessUnitId: Bu);
            lead.AssignTo = OwnerId;
            await seed.SaveChangesAsync();

            // Every OTHER sweep is switched off so each test observes exactly the trigger it names.
            seed.Set<SlaPolicy>().Add(new SlaPolicy
            {
                BusinessUnitId = Bu,
                UnassignedHours = 0,
                WarnDaysBeforeClose = 0,
                CriticalDaysBeforeClose = 0,
                StaleQuoteDays = 3650,
                QuoteNoResponseExpiryDays = 3650,
                ApprovalEscalationHours = 0,
                SupplierShipDateReminderDays = 0,
                SupplierAckEscalationHours = 0,
                QuoteDecisionReminderDays = _quoteDecisionReminderDays,
                CreatedOn = Anchor,
                UpdatedOn = Anchor
            });
            await seed.SaveChangesAsync();

            addRows(seed);
            await seed.SaveChangesAsync();
        }

        public void Dispose()
        {
            _provider.Dispose();
            _connection.Dispose();
        }
    }
}
