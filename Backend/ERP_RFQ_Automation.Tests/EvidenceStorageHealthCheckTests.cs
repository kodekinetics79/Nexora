using ERP_RFQ_Automation.HealthChecks;
using ERP_RFQ_Automation.Infrastructure.Storage;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ERP_RFQ_Automation.Tests;

public sealed class EvidenceStorageHealthCheckTests
{
    [Theory]
    [InlineData(true, false, HealthStatus.Healthy)]
    [InlineData(true, true, HealthStatus.Unhealthy)]
    [InlineData(false, false, HealthStatus.Unhealthy)]
    public async Task Readiness_RequiresReachableDurableEvidenceStorage(
        bool durable, bool probeFails, HealthStatus expected)
    {
        var check = new EvidenceStorageHealthCheck(new StorageStub(durable, probeFails));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(expected, result.Status);
    }

    private sealed class StorageStub : IEvidenceObjectStorage
    {
        private readonly bool _probeFails;

        public StorageStub(bool durable, bool probeFails)
        {
            IsDurable = durable;
            _probeFails = probeFails;
        }

        public bool IsDurable { get; }
        public Task ProbeAsync(CancellationToken ct = default) => _probeFails
            ? Task.FromException(new IOException("probe failed"))
            : Task.CompletedTask;
        public Task<EvidenceObject> WriteImmutableAsync(long businessUnitId, string zone, string sha256,
            string extension, ReadOnlyMemory<byte> content, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<Stream> OpenVerifiedReadAsync(string storageUri, string expectedSha256,
            CancellationToken ct = default) => throw new NotSupportedException();
    }
}
