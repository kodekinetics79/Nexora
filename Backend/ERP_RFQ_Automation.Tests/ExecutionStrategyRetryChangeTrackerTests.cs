using System.Text.Json;
using ERP_RFQ_Automation.Agent.Models;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Procurement;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// <c>Program.cs</c> configures <c>EnableRetryOnFailure</c>, so a PostgreSQL 40001 serialization
/// failure makes <c>IExecutionStrategy.ExecuteAsync</c> re-run its delegate ON THE SAME
/// DbContext. The change tracker is not reset between attempts, so attempt 2's re-query returns
/// the instance attempt 1 already mutated — EF does not overwrite the current values of an
/// entity it is already tracking — and every guard in the delegate then re-evaluates against
/// attempt 1's state instead of the row as it now stands.
///
/// <para>These tests install a genuinely retrying execution strategy over SQLite and inject a
/// transient failure at the exact moment the defect describes: <c>SaveChangesAsync</c> inside
/// <c>ProcurementDispatchWorker.FinishAsync</c>, after the message and the solicitation have
/// been flipped to Sent. Remove the <c>ChangeTracker.Clear()</c> from that delegate and the
/// retry's ownership guard throws, the outbox row is left PROCESSING to be fenced as
/// DELIVERY_UNCERTAIN, and a human is invited to re-send an RFQ the supplier already has.</para>
/// </summary>
public sealed class ExecutionStrategyRetryChangeTrackerTests
{
    [Fact]
    public async Task A_retried_delegate_does_not_see_attempt_1_mutations()
    {
        using var fixture = new RetryingDispatchFixture();
        fixture.SeedPending();

        // One transient failure, thrown from inside the SaveChanges that commits the send.
        fixture.Interceptor.FailOnce = true;

        Assert.True(await fixture.Worker.ProcessOneAsync(default));

        Assert.Equal(1, fixture.Interceptor.Failures);
        Assert.True(fixture.Interceptor.SaveAttempts >= 2, "The execution strategy must have retried the delegate.");

        // The retry saw the row as PROCESSING (its persisted state), passed the ownership guard,
        // and completed the send exactly once.
        var state = await fixture.StateAsync();
        Assert.Equal(ProcurementOutboxStatuses.Sent, state.Message.Status);
        Assert.Equal("test-acceptance-reference", state.Message.ProviderReference);
        Assert.Null(state.Message.LeaseToken);
        Assert.Equal(SolicitationStatus.Sent, state.Solicitation.Status);
        Assert.Equal(1, fixture.Notification.SendCount);

        // And the rolled-back attempt left nothing behind: one event, not two.
        Assert.Equal("SUPPLIER_SOLICITATION_SENT", Assert.Single(state.Events).EventType);
    }

    [Fact]
    public async Task A_retried_delegate_does_not_leave_the_message_fenced_as_delivery_uncertain()
    {
        using var fixture = new RetryingDispatchFixture();
        fixture.SeedPending();
        fixture.Interceptor.FailOnce = true;

        await fixture.Worker.ProcessOneAsync(default);

        var state = await fixture.StateAsync();
        Assert.NotEqual(ProcurementOutboxStatuses.Processing, state.Message.Status);
        Assert.NotEqual(ProcurementOutboxStatuses.Failed, state.Message.Status);
        Assert.Null(state.Message.LastErrorCode);
        Assert.NotEqual(SolicitationStatus.DeliveryFailed, state.Solicitation.Status);

        // Nothing more to do: a second pass must not re-send an RFQ the supplier already has.
        Assert.False(await fixture.Worker.ProcessOneAsync(default));
        Assert.Equal(1, fixture.Notification.SendCount);
    }

    // ------------------------------------------------------------------ harness

    /// <summary>The transient fault, standing in for PostgreSQL's 40001.</summary>
    private sealed class TransientProbeException(string message) : Exception(message);

    /// <summary>
    /// A REAL retrying strategy — the portable lane's default is non-retrying, which is why
    /// this defect was invisible to every existing test.
    /// </summary>
    private sealed class ProbeRetryExecutionStrategy(ExecutionStrategyDependencies dependencies)
        : ExecutionStrategy(dependencies, maxRetryCount: 3, maxRetryDelay: TimeSpan.Zero)
    {
        protected override bool ShouldRetryOn(Exception exception) => exception is TransientProbeException;
    }

    /// <summary>Throws once from inside SaveChanges, after the delegate has staged the
    /// Sent flip — the precise interleaving in the defect report.</summary>
    private sealed class FailOnceInterceptor : ISaveChangesInterceptor
    {
        public bool FailOnce { get; set; }
        public int Failures { get; private set; }
        public int SaveAttempts { get; private set; }

        public ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var sendingFlip = eventData.Context!.ChangeTracker.Entries<ProcurementOutboxMessage>()
                .Any(e => e.State == EntityState.Modified
                          && e.Entity.Status == ProcurementOutboxStatuses.Sent);
            if (!sendingFlip) return ValueTask.FromResult(result);

            SaveAttempts++;
            if (FailOnce)
            {
                FailOnce = false;
                Failures++;
                throw new TransientProbeException("Simulated 40001 from a concurrent transaction.");
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class RetryingDispatchFixture : IDisposable
    {
        private const long Tenant = 74_001;
        private const long Rfq = 74_010;
        private const long Supplier = 74_020;
        private const long Solicitation = 74_030;
        private const long Message = 74_040;

        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<ErpRfqAutomationContext> _options;
        private readonly ServiceProvider _provider;
        private readonly TenantScopeAccessor _tenantScope = new();

        public FailOnceInterceptor Interceptor { get; } = new();
        public ProcurementDispatchWorkerTests.RecordingNotification Notification { get; } = new();
        public ProcurementDispatchWorker Worker { get; }

        public RetryingDispatchFixture()
        {
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
            _options = new DbContextOptionsBuilder<ErpRfqAutomationContext>()
                .UseSqlite(_connection, o => o.ExecutionStrategy(d => new ProbeRetryExecutionStrategy(d)))
                .AddInterceptors(Interceptor)
                .EnableSensitiveDataLogging()
                .Options;

            using (var create = new ErpRfqAutomationContext(_options, new StubTenant(null)))
                create.Database.EnsureCreated();

            var services = new ServiceCollection()
                .AddScoped(_ => new ErpRfqAutomationContext(_options, new StubTenant(_tenantScope.BusinessUnitId)))
                .AddSingleton<ERP_RFQ_Automation.Notifications.INotificationService>(Notification);
            _provider = services.BuildServiceProvider();

            Worker = new ProcurementDispatchWorker(
                _provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<ProcurementDispatchWorker>.Instance,
                _tenantScope,
                new ProcurementDispatchHeartbeat());
        }

        public void SeedPending()
        {
            using var db = new ErpRfqAutomationContext(_options, new StubTenant(null));
            db.Set<Tenant>().Add(new Tenant
            {
                Id = Tenant,
                Name = "Retry Tenant",
                Slug = "retry-tenant",
                Status = TenantStatus.Active,
                PrimaryBusinessUnitId = Tenant
            });
            AgentSeed.Rfq(db, Rfq, Tenant, "RFQ-RETRY-1");
            AgentSeed.Supplier(db, Supplier, Tenant, "Supplier One", "supplier@example.test");
            var supplier = db.Suppliers.Local.Single(x => x.Id == Supplier);
            supplier.IsActive = true;
            supplier.GovernanceStatus = SupplierGovernanceStatuses.Approved;
            supplier.VerificationStatus = SupplierVerificationStatuses.Verified;
            supplier.ComplianceStatus = SupplierComplianceStatuses.Cleared;
            supplier.RiskStatus = SupplierRiskStatuses.Low;
            supplier.ReadinessStatus = SupplierReadinessStatuses.Ready;
            AgentSeed.Solicitation(db, Solicitation, Tenant, Rfq, Supplier, SolicitationStatus.PendingDispatch);

            var now = DateTime.UtcNow;
            db.ProcurementOutboxMessages.Add(new ProcurementOutboxMessage
            {
                Id = Message,
                BusinessUnitId = Tenant,
                SupplierSolicitationId = Solicitation,
                Status = ProcurementOutboxStatuses.Pending,
                PayloadJson = JsonSerializer.Serialize(new
                {
                    SolicitationId = Solicitation,
                    BusinessUnitId = Tenant,
                    RfqId = Rfq,
                    ToEmail = "supplier@example.test",
                    SupplierName = "Supplier One",
                    RfqNumber = "RFQ-RETRY-1",
                    ItemSummary = "10 x NX-100",
                    DueOn = now.AddDays(3)
                }),
                AttemptCount = 0,
                NextAttemptOn = now.AddMinutes(-1),
                CreatedOn = now,
                UpdatedOn = now
            });
            db.SaveChanges();
        }

        public async Task<(ProcurementOutboxMessage Message, SupplierSolicitation Solicitation, List<ProcurementEvent> Events)> StateAsync()
        {
            await using var db = new ErpRfqAutomationContext(_options, new StubTenant(null));
            return (
                await db.ProcurementOutboxMessages.AsNoTracking().SingleAsync(),
                await db.Set<SupplierSolicitation>().AsNoTracking().SingleAsync(),
                await db.ProcurementEvents.AsNoTracking().ToListAsync());
        }

        public void Dispose()
        {
            _provider.Dispose();
            _connection.Dispose();
        }
    }
}
