using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.QuoteDelivery;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Services.Interfaces;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

public sealed class QuoteDeliveryTests
{
    private const long Tenant = 95_001;

    [Fact]
    public async Task QuoteSend_QueuesDeliveryWithoutClaimingExternalSuccess_AndReplaysIdempotently()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        SeedQuote(context);
        var email = new RecordingEmailService();
        var service = new QuoteService(context, email, null!);

        var queued = await service.SendQuoteEmailAsync(95_011, Tenant, "buyer@nexora.invalid");
        var replay = await service.SendQuoteEmailAsync(95_011, Tenant, "buyer@nexora.invalid");

        context.ChangeTracker.Clear();
        var quote = await context.Quotes.SingleAsync(x => x.Id == 95_011);
        var delivery = await context.QuoteDeliveryRequests.SingleAsync();
        Assert.Null(quote.SentOn);
        Assert.Null(quote.StatusId);
        Assert.Equal("quote:95011:delivery:v1", delivery.IdempotencyKey);
        Assert.Null(delivery.CompletedOn);
        Assert.Equal(0, email.SendCount);
        Assert.True(queued.QueuedForDelivery);
        Assert.True(replay.Replayed);
    }

    [Fact]
    public async Task SuccessfulDeliveryFinalization_marks_quote_sent_idempotently()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        SeedQuote(context);
        var service = new QuoteService(context, new RecordingEmailService(), null!);

        await service.FinalizeQuoteDeliveryAsync(95_011, Tenant);
        var sentOn = (await context.Quotes.SingleAsync(x => x.Id == 95_011)).SentOn;
        await service.FinalizeQuoteDeliveryAsync(95_011, Tenant);

        var quote = await context.Quotes.SingleAsync(x => x.Id == 95_011);
        Assert.NotNull(sentOn);
        Assert.Equal(sentOn, quote.SentOn);
        Assert.Equal(95_002, quote.StatusId);
    }

    [Fact]
    public async Task QuoteSend_CannotAddressAnotherTenantsQuote()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        SeedQuote(context);
        var service = new QuoteService(context, new RecordingEmailService(), null!);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.SendQuoteEmailAsync(95_011, Tenant + 1, "buyer@nexora.invalid"));
        Assert.Empty(context.QuoteDeliveryRequests.IgnoreQueryFilters());
    }

    [Fact]
    public async Task QuoteSend_replay_rejects_changed_recipient()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        SeedQuote(context);
        var service = new QuoteService(context, new RecordingEmailService(), null!);

        await service.SendQuoteEmailAsync(95_011, Tenant, "buyer@nexora.invalid");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SendQuoteEmailAsync(95_011, Tenant, "corrected@nexora.invalid"));
        Assert.Equal("buyer@nexora.invalid",
            (await context.QuoteDeliveryRequests.SingleAsync()).RecipientEmail);
    }

    [Fact]
    public async Task DeliveryStore_FailureIsObservableAndRetriable_CompletedRequestCannotBeClaimedAgain()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        SeedQuote(context);
        context.QuoteDeliveryRequests.Add(NewDelivery());
        await context.SaveChangesAsync();
        var store = new QuoteDeliveryStore(context);

        var first = Assert.Single(await store.ClaimAsync("worker-a", 10, TimeSpan.FromMinutes(1), default));
        await store.FailAsync(first.Id, "worker-a", first.LeaseToken, "SmtpUnavailable", 3, default);

        var failed = await context.QuoteDeliveryRequests.IgnoreQueryFilters().SingleAsync();
        Assert.Equal("SmtpUnavailable", failed.LastErrorCode);
        Assert.Equal(1, failed.AttemptCount);
        Assert.Null(failed.CompletedOn);
        failed.AvailableOn = DateTime.UtcNow.AddSeconds(-1);
        await context.SaveChangesAsync();

        var retry = Assert.Single(await store.ClaimAsync("worker-b", 10, TimeSpan.FromMinutes(1), default));
        await store.CompleteAsync(retry.Id, "worker-b", retry.LeaseToken, default);
        Assert.Empty(await store.ClaimAsync("worker-c", 10, TimeSpan.FromMinutes(1), default));

        var completed = await context.QuoteDeliveryRequests.IgnoreQueryFilters().SingleAsync();
        Assert.NotNull(completed.CompletedOn);
        Assert.Null(completed.LastErrorCode);
        Assert.Equal(2, completed.AttemptCount);
    }

    [Fact]
    public async Task DeliveryStore_FencesNonOwningWorkerAndDeadLettersAtLimit()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        SeedQuote(context);
        context.QuoteDeliveryRequests.Add(NewDelivery());
        await context.SaveChangesAsync();
        var store = new QuoteDeliveryStore(context);

        var claim = Assert.Single(await store.ClaimAsync("owner", 1, TimeSpan.FromMinutes(1), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.CompleteAsync(claim.Id, "intruder", claim.LeaseToken, default));
        await store.FailAsync(claim.Id, "owner", claim.LeaseToken, "PermanentFailure", 1, default);

        var request = await context.QuoteDeliveryRequests.IgnoreQueryFilters().SingleAsync();
        Assert.NotNull(request.DeadLetteredOn);
        Assert.Equal("PermanentFailure", request.LastErrorCode);
    }

    [Fact]
    public async Task DeliveryStore_DoesNotAutomaticallyReplayAnAmbiguousExpiredAttempt()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        SeedQuote(context);
        context.QuoteDeliveryRequests.Add(NewDelivery());
        await context.SaveChangesAsync();
        var store = new QuoteDeliveryStore(context);

        var claim = Assert.Single(await store.ClaimAsync("worker-a", 1, TimeSpan.FromSeconds(10), default));
        var request = await context.QuoteDeliveryRequests.IgnoreQueryFilters().SingleAsync();
        request.LeaseUntil = DateTime.UtcNow.AddSeconds(-1);
        await context.SaveChangesAsync();

        Assert.Empty(await store.ClaimAsync("worker-b", 1, TimeSpan.FromMinutes(1), default));
        var terminal = await context.QuoteDeliveryRequests.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(claim.Id, terminal.Id);
        Assert.NotNull(terminal.DeadLetteredOn);
        Assert.Equal("DeliveryOutcomeUncertain", terminal.LastErrorCode);
    }

    [Fact]
    public async Task Dispatcher_DoesNotRetryAnAmbiguousSmtpException()
    {
        var envelope = new QuoteDeliveryEnvelope(
            1, Tenant, 95_011, "buyer@nexora.invalid", "Quote", "Body",
            null, "quote.pdf", 1, Guid.NewGuid());
        var store = new RecordingDeliveryStore(envelope);
        await using var provider = new ServiceCollection()
            .AddSingleton<IQuoteDeliveryStore>(store)
            .AddSingleton<IQuoteDeliverySender>(new ThrowingDeliverySender())
            .BuildServiceProvider();
        var dispatcher = new QuoteDeliveryDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<QuoteDeliveryDispatcher>.Instance);

        Assert.Equal(1, await dispatcher.DispatchOnceAsync(default));
        Assert.Equal(0, store.RetriableFailureCount);
        Assert.Equal(1, store.UncertainFailureCount);
        Assert.Equal(nameof(InvalidOperationException), store.LastErrorCode);
    }

    private static void SeedQuote(ErpRfqAutomationContext context)
    {
        if (!context.BusinessUnits.IgnoreQueryFilters().Any(x => x.Id == Tenant))
        {
            context.BusinessUnits.Add(new BusinessUnit
            {
                Id = Tenant, BusinessUnitCode = "QD", BusinessUnitName = "Quote Delivery",
                CreatedBy = "tests", CreatedOn = DateTime.UtcNow
            });
            context.SetupMasters.Add(new SetupMaster
            {
                SetupId = 95_002, BusinessUnitId = Tenant, SetupType = "QuoteStatus",
                SetupCode = "SENT", SetupValue = "Sent", CreatedBy = "tests", CreatedOn = DateTime.UtcNow
            });
            context.Quotes.Add(new Quote
            {
                Id = 95_011, QuoteNo = "Q-DELIVERY-1", BusinessUnitId = Tenant,
                QuoteDate = DateTime.UtcNow, ValidUntil = DateTime.UtcNow.AddDays(30),
                TotalAmount = 100, CreatedBy = "tests", CreatedDate = DateTime.UtcNow
            });
            context.SaveChanges();
        }
    }

    private static QuoteDeliveryRequest NewDelivery() => new()
    {
        BusinessUnitId = Tenant,
        QuoteId = 95_011,
        IdempotencyKey = Guid.NewGuid().ToString("N"),
        RecipientEmail = "buyer@nexora.invalid",
        Subject = "Test quote",
        Body = "Test body",
        AttachmentFileName = "quote.pdf",
        RequestedOn = DateTime.UtcNow,
        AvailableOn = DateTime.UtcNow.AddSeconds(-1),
        Version = 1
    };

    private sealed class RecordingEmailService : IEmailService
    {
        public int SendCount { get; private set; }
        public Task FetchAndSaveLeadsAsync(long? businessUnitId = null) => Task.CompletedTask;
        public Task SendEmailAsync(string to, string subject, string body,
            List<(string FileName, byte[] FileContent, string ContentType)> attachments = null!,
            string fromEmail = null!, long? businessUnitId = null)
        {
            SendCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingDeliverySender : IQuoteDeliverySender
    {
        public Task SendAsync(QuoteDeliveryEnvelope request, CancellationToken ct) =>
            throw new InvalidOperationException("Ambiguous SMTP result.");
    }

    private sealed class RecordingDeliveryStore(QuoteDeliveryEnvelope envelope) : IQuoteDeliveryStore
    {
        private bool _claimed;
        public int RetriableFailureCount { get; private set; }
        public int UncertainFailureCount { get; private set; }
        public string? LastErrorCode { get; private set; }

        public Task<IReadOnlyList<QuoteDeliveryEnvelope>> ClaimAsync(
            string workerId, int batchSize, TimeSpan lease, CancellationToken ct)
        {
            IReadOnlyList<QuoteDeliveryEnvelope> result = _claimed ? [] : [envelope];
            _claimed = true;
            return Task.FromResult(result);
        }

        public Task CompleteAsync(long id, string workerId, Guid leaseToken, CancellationToken ct) =>
            Task.CompletedTask;

        public Task FailAsync(long id, string workerId, Guid leaseToken, string errorCode,
            int maxAttempts, CancellationToken ct)
        {
            RetriableFailureCount++;
            LastErrorCode = errorCode;
            return Task.CompletedTask;
        }

        public Task MarkOutcomeUncertainAsync(long id, string workerId, Guid leaseToken,
            string errorCode, CancellationToken ct)
        {
            UncertainFailureCount++;
            LastErrorCode = errorCode;
            return Task.CompletedTask;
        }
    }
}
