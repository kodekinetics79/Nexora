using System.Net;
using ERP_RFQ_Automation.CommercialFinance;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.Tests;

public sealed class FinanceOutboxDispatcherTests
{
    [Fact]
    public async Task HttpPublisher_SendsSignedIdempotentMinimalEnvelope()
    {
        var eventId = Guid.NewGuid();
        var handler = new RecordingHandler();
        var options = new FinanceOutboxDispatcherOptions
        {
            Enabled = true,
            Endpoint = "http://localhost/finance-events",
            HmacSecret = new string('s', 32),
            RequestTimeout = TimeSpan.FromSeconds(5)
        };
        var publisher = new FinanceHttpEventPublisher(
            new SingleClientFactory(new HttpClient(handler)),
            new StaticOptionsMonitor<FinanceOutboxDispatcherOptions>(options),
            new TestEnvironment("Development"));
        var envelope = new FinanceOutboxEnvelope(
            1, 42, eventId, "ReceivableDocument", 99, 2,
            "finance.receivable.issued", "{\"Id\":99,\"Status\":\"Issued\"}",
            1, DateTime.UtcNow, 1, Guid.NewGuid(), DateTime.UtcNow.AddMinutes(1));

        await publisher.PublishAsync(envelope, default);

        Assert.Equal(eventId.ToString("D"), handler.Headers["Idempotency-Key"]);
        Assert.Equal("finance.receivable.issued", handler.Headers["X-Nexora-Event-Type"]);
        Assert.StartsWith("sha256=", handler.Headers["X-Nexora-Signature"]);
        Assert.Contains("\"businessUnitId\":42", handler.Body);
        Assert.Contains("\"payload\":{\"Id\":99,\"Status\":\"Issued\"}", handler.Body);
        Assert.DoesNotContain("leaseToken", handler.Body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The publish outcome decides the claim's transition: success completes it, failure fails it.
    ///
    /// <para><b>Why this needs a real container.</b> The dispatcher is tenant-scoped on BOTH halves
    /// of its cycle. <c>DispatchAsync</c> pushes the message's business unit as its first statement
    /// and then calls <c>EnsureScoped</c>, so it resolves <see cref="ITenantScopeAccessor"/> and
    /// <c>ErpRfqAutomationContext</c> on every message and refuses to transition anything it cannot
    /// prove is scoped. This test used to hand it a two-registration container holding only a store
    /// and a publisher; the dispatch therefore threw <c>InvalidOperationException</c> ("no service
    /// for type ITenantScopeAccessor") inside <c>Parallel.ForEachAsync</c>, the worker's own
    /// cycle-level catch swallowed it into a backoff, and the only visible symptom was the three
    /// second wait expiring — which reads exactly like a race and is not one. It is deterministic:
    /// the fixture was missing the wiring the control needs, so lengthening the timeout would
    /// have produced a slower red and nothing else.</para>
    ///
    /// <para>The store and the publisher stay stubs — what is under test is which transition a
    /// publish outcome causes, not the SQL — but the tenant plumbing is now the real thing, so the
    /// scope guard is exercised rather than dodged. One genuine outbox row is seeded because
    /// tenant enumeration reads the table itself.</para>
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dispatcher_TransitionsClaimAfterPublishOutcome(bool publisherFails)
    {
        const long businessUnitId = 42;
        var envelope = new FinanceOutboxEnvelope(
            7, businessUnitId, Guid.NewGuid(), "CustomerPayment", 101, 1,
            "finance.payment.posted", "{}", 1, DateTime.UtcNow, 1,
            Guid.NewGuid(), DateTime.UtcNow.AddMinutes(1));
        var store = new RecordingStore(envelope);
        using var harness = new TenantWorkGateHarness(configure: services => services
            .AddSingleton<IFinanceOutboxStore>(store)
            .AddSingleton<IFinanceEventPublisher>(new StubPublisher(publisherFails)));
        await harness.SeedTenantAsync(businessUnitId, TenantStatus.Active, "outbox-dispatch");
        await SeedPendingOutboxRowAsync(harness, businessUnitId);

        var options = new StaticOptionsMonitor<FinanceOutboxDispatcherOptions>(new()
        {
            Enabled = true,
            Endpoint = "http://localhost/finance-events",
            BatchSize = 1,
            MaxConcurrency = 1,
            LeaseDuration = TimeSpan.FromSeconds(10),
            RequestTimeout = TimeSpan.FromSeconds(2),
            PollInterval = TimeSpan.FromMilliseconds(250),
            InitialRetryDelay = TimeSpan.FromSeconds(1),
            MaximumRetryDelay = TimeSpan.FromSeconds(1)
        });
        var dispatcher = new FinanceOutboxDispatcherService(
            harness.ScopeFactory, options,
            NullLogger<FinanceOutboxDispatcherService>.Instance,
            harness.TenantScope);

        await dispatcher.StartAsync(default);
        await store.Transitioned.Task.WaitAsync(TestWaits.Liveness);
        await dispatcher.StopAsync(default);

        Assert.Equal(publisherFails ? 0 : 1, store.Completed);
        Assert.Equal(publisherFails ? 1 : 0, store.Failed);
    }

    /// <summary>
    /// One claimable row, so <c>ResolvePendingBusinessUnitsAsync</c> — the dispatcher's only
    /// unscoped query, and the thing that decides which tenants get a scoped claim — returns this
    /// tenant. The stubbed store is what actually answers the claim.
    /// </summary>
    private static async Task SeedPendingOutboxRowAsync(
        TenantWorkGateHarness harness, long businessUnitId)
    {
        await using var db = harness.Context();
        db.Set<FinanceOutboxMessage>().Add(new FinanceOutboxMessage
        {
            BusinessUnitId = businessUnitId,
            EventId = Guid.NewGuid(),
            AggregateType = "CustomerPayment",
            AggregateId = 101,
            AggregateVersion = 1,
            EventType = "finance.payment.posted",
            Payload = "{}",
            SchemaVersion = 1,
            OccurredOn = DateTime.UtcNow.AddMinutes(-5),
            AvailableOn = DateTime.UtcNow.AddMinutes(-5)
        });
        await db.SaveChangesAsync();
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            foreach (var header in request.Headers)
                Headers[header.Key] = string.Join(",", header.Value);
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubPublisher(bool fails) : IFinanceEventPublisher
    {
        public Task PublishAsync(FinanceOutboxEnvelope envelope, CancellationToken cancellationToken)
            => fails ? Task.FromException(new HttpRequestException("downstream unavailable")) : Task.CompletedTask;
    }

    private sealed class RecordingStore(FinanceOutboxEnvelope envelope) : IFinanceOutboxStore
    {
        private int _claimed;
        public int Completed { get; private set; }
        public int Failed { get; private set; }
        public TaskCompletionSource Transitioned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<FinanceOutboxEnvelope>> ClaimAsync(
            string workerId, int batchSize, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<FinanceOutboxEnvelope>>(
                Interlocked.Exchange(ref _claimed, 1) == 0 ? [envelope] : []);

        public Task CompleteAsync(
            long messageId, string workerId, Guid leaseToken, CancellationToken cancellationToken)
        {
            Completed++;
            Transitioned.TrySetResult();
            return Task.CompletedTask;
        }

        public Task FailAsync(
            long messageId, string workerId, Guid leaseToken, string error,
            TimeSpan retryDelay, int maxAttempts, CancellationToken cancellationToken)
        {
            Failed++;
            Transitioned.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class TestEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Nexora.Tests";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
