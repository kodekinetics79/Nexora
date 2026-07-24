using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Infrastructure.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

public sealed class ProductionDocumentReaderTests
{
    [Fact]
    public async Task VerifiedReadFailure_ThrowsTypedIntegrityFailure()
    {
        var job = new ExtractionJob
        {
            Id = 123,
            BusinessUnitId = 7,
            StoragePath = "s3://evidence/object",
            ContentHash = new string('a', 64),
            FileName = "rfq.csv",
            FileType = "csv"
        };
        var reader = new ProductionDocumentReader(
            NullLogger<ProductionDocumentReader>.Instance,
            new TestEnvironment(),
            new FailingStorage());

        var error = await Assert.ThrowsAsync<EvidenceIntegrityException>(() => reader.ReadAsync(job));

        Assert.Equal(job.Id, error.ExtractionJobId);
        Assert.Equal("verified_read_failed", error.Code);
        Assert.IsType<InvalidDataException>(error.InnerException);
        Assert.DoesNotContain(job.StoragePath, error.Message, StringComparison.Ordinal);
    }

    private sealed class FailingStorage : IEvidenceObjectStorage
    {
        public bool IsDurable => true;
        public Task ProbeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<EvidenceObject> WriteImmutableAsync(
            long businessUnitId, string zone, string sha256, string extension,
            ReadOnlyMemory<byte> content, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Stream> OpenVerifiedReadAsync(
            string storageUri, string expectedSha256, CancellationToken ct = default)
            => Task.FromException<Stream>(new InvalidDataException("hash mismatch"));
    }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
