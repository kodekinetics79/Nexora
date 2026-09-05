using System.Security.Claims;
using ERP_RFQ_Automation.DTOs.QuoteDTOs;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Operations;
using ERP_RFQ_Automation.Platform.Services;
using ERP_RFQ_Automation.QuoteDelivery;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Services.Interfaces;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Durability of the customer-quote delivery stage across the one boundary that cannot be
/// undone: the SMTP send. Once the provider has returned acceptance evidence, everything that
/// follows is our own bookkeeping — two writes, on two aggregates, that used to run in the
/// wrong order and treat any failure between them as "the customer may or may not have received
/// it". Each test here kills the operation between the writes and asserts what the tenant is
/// told and whether anything is resent.
///
/// <para>Revert-proofed: with <c>QuoteDeliveryDispatcher</c> restored to finalize-then-complete
/// and no reconcile, the first three tests fail (the row is dead-lettered as
/// <c>DeliveryOutcomeUncertain</c>, or the quote is never marked SENT); the recovery test fails
/// against a <c>PlatformDeadLetterRecoveryService</c> without the confirmation gate; the edit
/// tests fail against the old in-flight-only guard.</para>
/// </summary>
public sealed class QuoteDeliveryDurabilityTests
{
    private const long Tenant = 97_001;
    private const long QuoteId = 97_011;
    private const long SentStatusId = 97_002;

    /// <summary>
    /// TORN WRITE #1. SMTP accepted, then the quote's own status update throws (a lifecycle
    /// refusal, a follow-up-task write, a dropped connection — anything after the send). The
    /// ledger row must be SEALED (provider acceptance is a fact) and must NOT say uncertain;
    /// the next cycle marks the quote SENT without any email leaving again.
    /// </summary>
    [Fact]
    public async Task A_status_update_that_fails_after_provider_acceptance_is_not_reported_as_uncertain_and_catches_up()
    {
        var killQuoteWrite = new QuoteStatusWriteKiller(failures: 1);
        var sender = new AcceptingSender();
        using var host = new DurabilityHost(sender, killQuoteWrite);
        await using (var seed = host.ContextFor(null))
        {
            SeedQuote(seed);
            seed.QuoteDeliveryRequests.Add(PendingDelivery());
            await seed.SaveChangesAsync();
        }
        var dispatcher = host.Dispatcher();

        // Cycle 1: send succeeds, sealing succeeds, the quote write is killed.
        Assert.Equal(1, await dispatcher.DispatchOnceAsync(default));

        await using (var assert = host.ContextFor(null))
        {
            var row = await assert.QuoteDeliveryRequests.IgnoreQueryFilters().SingleAsync();
            Assert.NotNull(row.CompletedOn);
            Assert.Null(row.DeadLetteredOn);
            Assert.DoesNotContain("Uncertain", row.LastErrorCode ?? string.Empty);
            Assert.Null((await assert.Quotes.SingleAsync(q => q.Id == QuoteId)).SentOn);
        }
        Assert.Equal(1, sender.SendCount);

        // Cycle 2: nothing is claimable, but the sealed-and-unfinalized quote is reconciled.
        await dispatcher.DispatchOnceAsync(default);

        await using (var assert = host.ContextFor(null))
        {
            var quote = await assert.Quotes.SingleAsync(q => q.Id == QuoteId);
            Assert.NotNull(quote.SentOn);
            Assert.Equal(SentStatusId, quote.StatusId);
        }
        Assert.Equal(1, sender.SendCount); // at-most-once held: no second email
    }

    /// <summary>
    /// TORN WRITE #2. The process died right after sealing the row (the deploy stop/start,
    /// ~40 s on a single Render instance) and before the quote was marked SENT. On restart the
    /// quote is reconciled from the ledger. Nothing is claimed, nothing is sent.
    /// </summary>
    [Fact]
    public async Task A_delivery_sealed_by_a_process_that_died_before_marking_the_quote_is_reconciled_without_resending()
    {
        var sender = new AcceptingSender();
        using var host = new DurabilityHost(sender, new QuoteStatusWriteKiller(failures: 0));
        await using (var seed = host.ContextFor(null))
        {
            SeedQuote(seed);
            var sealedRow = PendingDelivery();
            sealedRow.AttemptCount = 1;
            sealedRow.CompletedOn = DateTime.UtcNow.AddMinutes(-3);
            seed.QuoteDeliveryRequests.Add(sealedRow);
            await seed.SaveChangesAsync();
        }

        await host.Dispatcher().DispatchOnceAsync(default);

        await using var assert = host.ContextFor(null);
        Assert.NotNull((await assert.Quotes.SingleAsync(q => q.Id == QuoteId)).SentOn);
        Assert.Equal(0, sender.SendCount);
    }

    /// <summary>
    /// TORN WRITE #3, persistent form. The quote refuses to be marked SENT on every attempt.
    /// The ledger row stays sealed, names the failure, and is deferred (not retried every five
    /// seconds); the tenant's send-readiness says DELIVERED — never UNCERTAIN — and the quote is
    /// not offered for sending again.
    /// </summary>
    [Fact]
    public async Task A_persistently_failing_status_update_is_deferred_on_the_ledger_and_the_tenant_is_told_it_was_delivered()
    {
        var sender = new AcceptingSender();
        using var host = new DurabilityHost(sender, new QuoteStatusWriteKiller(failures: int.MaxValue));
        await using (var seed = host.ContextFor(null))
        {
            SeedQuote(seed);
            var sealedRow = PendingDelivery();
            sealedRow.AttemptCount = 1;
            sealedRow.CompletedOn = DateTime.UtcNow.AddMinutes(-3);
            seed.QuoteDeliveryRequests.Add(sealedRow);
            await seed.SaveChangesAsync();
        }

        var before = DateTime.UtcNow;
        await host.Dispatcher().DispatchOnceAsync(default);

        await using var assert = host.ContextFor(Tenant);
        var row = await assert.QuoteDeliveryRequests.SingleAsync();
        Assert.NotNull(row.CompletedOn);
        Assert.Null(row.DeadLetteredOn);
        Assert.StartsWith("SentNotFinalized:", row.LastErrorCode);
        Assert.True(row.AvailableOn > before.AddMinutes(4), "the retry must be deferred, not immediate");
        Assert.Null((await assert.Quotes.SingleAsync(q => q.Id == QuoteId)).SentOn);
        Assert.Equal(0, sender.SendCount);

        var readiness = await new QuoteService(assert, new RecordingEmailService(), Configured())
            .EvaluateSendReadinessAsync(QuoteId, Tenant);
        Assert.Equal("DELIVERED", readiness.DeliveryOutcome);
        Assert.False(readiness.CanSend);
        Assert.Contains(readiness.Blockers, b => b.Code == "DELIVERY_STATUS_PENDING");
        Assert.DoesNotContain(readiness.Blockers, b => b.Code == "DELIVERY_OUTCOME_UNCERTAIN");
    }

    /// <summary>
    /// Operator recovery RE-SENDS. A dead-letter that never left is safe to release; an
    /// UNCERTAIN one may already be in the customer's inbox — releasing it blind is how a
    /// customer gets the same quote twice. The operator must state that they checked.
    /// </summary>
    [Fact]
    public async Task Operator_recovery_of_an_uncertain_quote_delivery_requires_confirmation_of_non_delivery()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(null);
        Seed.EnsureBusinessUnit(db, 970);
        db.Set<Tenant>().Add(new Tenant
        {
            Id = 97, Name = "Uncertain tenant", Slug = "uncertain-tenant-97",
            Status = TenantStatus.Active, PrimaryBusinessUnitId = 970
        });
        db.Quotes.Add(new Quote
        {
            Id = 971, BusinessUnitId = 970, QuoteNo = "Q-UNCERTAIN-971",
            QuoteDate = DateTime.UtcNow, ValidUntil = DateTime.UtcNow.AddDays(7),
            TotalAmount = 125, CreatedBy = "tests", CreatedDate = DateTime.UtcNow
        });
        db.QuoteDeliveryRequests.Add(new QuoteDeliveryRequest
        {
            Id = 972, BusinessUnitId = 970, QuoteId = 971,
            IdempotencyKey = "uncertain-delivery", RecipientEmail = "buyer@example.test",
            Subject = "Quote", Body = "Attached", AttachmentFileName = "quote.pdf",
            RequestedOn = DateTime.UtcNow.AddHours(-1), AvailableOn = DateTime.UtcNow.AddHours(-1),
            AttemptCount = 1, DeadLetteredOn = DateTime.UtcNow.AddMinutes(-10),
            LastErrorCode = "DeliveryOutcomeUncertain:SmtpCommandException", Version = 2
        });
        await db.SaveChangesAsync();
        var service = new PlatformDeadLetterRecoveryService(
            db, new PlatformAuditService(db, NullLogger<PlatformAuditService>.Instance), null!);

        var blind = new RecoverPlatformDeadLetterCommand(
            PlatformDeadLetterQueues.QuoteDelivery, 972, "Retrying the failed delivery.", "quote-retry-972");
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RecoverAsync(97, blind, Actor(), null, default));
        Assert.Contains("may already have reached the customer", refused.Message);
        Assert.NotNull((await db.QuoteDeliveryRequests.AsNoTracking().SingleAsync(x => x.Id == 972)).DeadLetteredOn);

        var confirmed = blind with { ConfirmedNotDelivered = true, IdempotencyKey = "quote-retry-972-confirmed" };
        var result = await service.RecoverAsync(97, confirmed, Actor(), null, default);
        Assert.Equal("RetryQueued", result.Status);
        Assert.Null((await db.QuoteDeliveryRequests.AsNoTracking().SingleAsync(x => x.Id == 972)).DeadLetteredOn);
        var audit = await db.Set<PlatformAuditLog>().SingleAsync(x =>
            x.Action == PlatformDeadLetterRecoveryService.AuditAction);
        Assert.Contains("\"confirmedNotDelivered\":true", audit.Metadata, StringComparison.Ordinal);
    }

    /// <summary>
    /// The delivery ledger, not the status flag, decides whether a quote is editable. A sealed
    /// row means the customer holds the PDF even while SentOn has not caught up.
    /// </summary>
    [Fact]
    public async Task A_delivered_quote_refuses_edits_before_its_status_catches_up()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        SeedQuote(context);
        var sealedRow = PendingDelivery();
        sealedRow.AttemptCount = 1;
        sealedRow.CompletedOn = DateTime.UtcNow;
        context.QuoteDeliveryRequests.Add(sealedRow);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new QuoteService(context, new RecordingEmailService(), null!).UpdateQuoteAsync(QuoteId, Edit()));

        Assert.Contains("has been delivered", error.Message);
    }

    [Fact]
    public async Task A_quote_whose_delivery_is_uncertain_refuses_edits()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        SeedQuote(context);
        var uncertain = PendingDelivery();
        uncertain.AttemptCount = 1;
        uncertain.DeadLetteredOn = DateTime.UtcNow;
        uncertain.LastErrorCode = "DeliveryOutcomeUncertain";
        context.QuoteDeliveryRequests.Add(uncertain);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new QuoteService(context, new RecordingEmailService(), null!).UpdateQuoteAsync(QuoteId, Edit()));

        Assert.Contains("may already have reached the customer", error.Message);
    }

    // ------------------------------------------------------------------ harness

    /// <summary>
    /// Throws from SaveChanges exactly when the quote's SentOn is being written — the
    /// "process died between the two writes" injection, scoped so the ledger write, the claim
    /// and every other save succeed normally.
    /// </summary>
    private sealed class QuoteStatusWriteKiller(int failures) : SaveChangesInterceptor
    {
        private int _remaining = failures;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var writesSentOn = eventData.Context!.ChangeTracker.Entries<Quote>()
                .Any(e => e.State == EntityState.Modified && e.Property(nameof(Quote.SentOn)).IsModified);
            if (writesSentOn && _remaining > 0)
            {
                if (_remaining != int.MaxValue) _remaining--;
                throw new InvalidOperationException("Injected: the process died before the quote status was written.");
            }
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class AcceptingSender : IQuoteDeliverySender
    {
        public int SendCount { get; private set; }
        public Task SendAsync(QuoteDeliveryEnvelope request, CancellationToken ct)
        {
            SendCount++;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// The production composition of the delivery path — real store, real QuoteService, tenant
    /// scope honoured — over SQLite, with the failure injector attached to every context the
    /// dispatcher resolves.
    /// </summary>
    private sealed class DurabilityHost : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _provider;
        private readonly IInterceptor[] _interceptors;

        public DurabilityHost(IQuoteDeliverySender sender, params IInterceptor[] interceptors)
        {
            _interceptors = interceptors;
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
            var services = new ServiceCollection();
            services.AddSingleton<ITenantScopeAccessor, TenantScopeAccessor>();
            services.AddScoped<ITenantContext, ScopeOnlyTenantContext>();
            services.AddScoped(sp => new ErpRfqAutomationContext(Options(), sp.GetRequiredService<ITenantContext>()));
            services.AddScoped<IQuoteDeliveryStore, QuoteDeliveryStore>();
            services.AddSingleton(sender);
            services.AddScoped<IQuoteService>(sp => new QuoteService(
                sp.GetRequiredService<ErpRfqAutomationContext>(), new RecordingEmailService(), Configured()));
            _provider = services.BuildServiceProvider();
            using var seedScope = _provider.CreateScope();
            seedScope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>().Database.EnsureCreated();
        }

        public QuoteDeliveryDispatcher Dispatcher() => new(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<QuoteDeliveryDispatcher>.Instance,
            _provider.GetRequiredService<ITenantScopeAccessor>());

        public ErpRfqAutomationContext ContextFor(long? businessUnitId)
            => new(new DbContextOptionsBuilder<ErpRfqAutomationContext>().UseSqlite(_connection).Options,
                new StubTenant(businessUnitId));

        private DbContextOptions<ErpRfqAutomationContext> Options()
            => new DbContextOptionsBuilder<ErpRfqAutomationContext>()
                .UseSqlite(_connection)
                .AddInterceptors(_interceptors)
                .EnableSensitiveDataLogging()
                .Options;

        public void Dispose()
        {
            _provider.Dispose();
            _connection.Dispose();
        }

        private sealed class ScopeOnlyTenantContext(ITenantScopeAccessor scope) : ITenantContext
        {
            public long? BusinessUnitId { get; } = scope.BusinessUnitId;
        }
    }

    private static void SeedQuote(ErpRfqAutomationContext context)
    {
        if (context.BusinessUnits.IgnoreQueryFilters().Any(x => x.Id == Tenant)) return;
        context.BusinessUnits.Add(new BusinessUnit
        {
            Id = Tenant, BusinessUnitCode = $"QD{Tenant}", BusinessUnitName = "Quote Durability",
            CreatedBy = "tests", CreatedOn = DateTime.UtcNow
        });
        context.SetupMasters.Add(new SetupMaster
        {
            SetupId = SentStatusId, BusinessUnitId = Tenant, SetupType = "QuoteStatus",
            SetupCode = "SENT", SetupValue = "Sent", CreatedBy = "tests", CreatedOn = DateTime.UtcNow
        });
        context.Quotes.Add(new Quote
        {
            Id = QuoteId, QuoteNo = "Q-DURABLE-1", BusinessUnitId = Tenant,
            QuoteDate = DateTime.UtcNow, ValidUntil = DateTime.UtcNow.AddDays(30),
            TotalAmount = 100, CreatedBy = "tests", CreatedDate = DateTime.UtcNow
        });
        context.SaveChanges();
    }

    private static QuoteDeliveryRequest PendingDelivery() => new()
    {
        BusinessUnitId = Tenant,
        QuoteId = QuoteId,
        IdempotencyKey = $"quote:{QuoteId}:delivery:v1",
        RecipientEmail = "buyer@nexora.invalid",
        Subject = "Test quote",
        Body = "Test body",
        AttachmentFileName = "quote.pdf",
        RequestedOn = DateTime.UtcNow.AddMinutes(-5),
        AvailableOn = DateTime.UtcNow.AddMinutes(-5),
        Version = 1
    };

    private static QuoteUpdateRequestDTO Edit() => new()
    {
        Id = QuoteId, QuoteNo = "Q-DURABLE-1", ModifiedBy = "rep@nexora.invalid"
    };

    private static IQuoteConfigurationRepository Configured() => new StubConfig(new QuoteConfiguration
    {
        BusinessUnitId = Tenant,
        CompanyAddress = "King Fahd Road, Al Khobar 34423",
        CompanyPhone = "+966 13 800 0000",
        CompanyEmail = "sales@durable.example"
    });

    private static ClaimsPrincipal Actor() => new(new ClaimsIdentity(new[]
    {
        new Claim("sub", "7"), new Claim("email", "owner@nexora.test"),
        new Claim(PlatformAuthConstants.PlatformRoleClaim, nameof(PlatformRole.Owner)),
        new Claim(PlatformAuthConstants.AuthenticationMethodClaim, PlatformAuthConstants.MfaAuthenticationMethod)
    }, "test"));

    private sealed class StubConfig(QuoteConfiguration? configuration) : IQuoteConfigurationRepository
    {
        public Task<QuoteConfiguration?> GetByBusinessUnitIdAsync(long businessUnitId) => Task.FromResult(configuration);
        public Task<QuoteConfiguration> UpsertAsync(QuoteConfiguration configurationToSave) => Task.FromResult(configurationToSave);
        public Task AddAsync(QuoteConfiguration configurationToSave) => Task.CompletedTask;
        public Task UpdateAsync(QuoteConfiguration configurationToSave) => Task.CompletedTask;
    }

    private sealed class RecordingEmailService : IEmailService
    {
        public Task<MailboxPollReport> FetchAndSaveLeadsAsync(long? businessUnitId = null)
            => Task.FromResult(MailboxPollReport.Empty);
        public Task SendEmailAsync(string to, string subject, string body,
            List<(string FileName, byte[] FileContent, string ContentType)> attachments = null!,
            string fromEmail = null!, long? businessUnitId = null) => Task.CompletedTask;
    }
}
