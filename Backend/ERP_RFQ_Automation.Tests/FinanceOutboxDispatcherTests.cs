using System.Net;
using ERP_RFQ_Automation.CommercialFinance;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dispatcher_TransitionsClaimAfterPublishOutcome(bool publisherFails)
    {
        var envelope = new FinanceOutboxEnvelope(
            7, 42, Guid.NewGuid(), "CustomerPayment", 101, 1,
            "finance.payment.posted", "{}", 1, DateTime.UtcNow, 1,
            Guid.NewGuid(), DateTime.UtcNow.AddMinutes(1));
        var store = new RecordingStore(envelope);
        var services = new ServiceCollection()
            .AddSingleton<IFinanceOutboxStore>(store)
            .AddSingleton<IFinanceEventPublisher>(new StubPublisher(publisherFails))
            .BuildServiceProvider();
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
            services.GetRequiredService<IServiceScopeFactory>(), options,
            NullLogger<FinanceOutboxDispatcherService>.Instance);

        await dispatcher.StartAsync(default);
        await store.Transitioned.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await dispatcher.StopAsync(default);

        Assert.Equal(publisherFails ? 0 : 1, store.Completed);
        Assert.Equal(publisherFails ? 1 : 0, store.Failed);
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
