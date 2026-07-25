using ERP_RFQ_Automation.Extraction;

namespace ERP_RFQ_Automation.Tests;

public sealed class DocumentIngestionServiceTests
{
    [Fact]
    public void OccurrenceIdentity_DistinguishesIdenticalSameBatchAttachments()
    {
        var batchId = Guid.NewGuid();

        var first = SourceOccurrenceIdentity.BuildKey(
            batchId, ExtractionSourceType.Email,
            new ExtractionJobMetadata { SourceOccurrenceId = "attachment-1" });
        var second = SourceOccurrenceIdentity.BuildKey(
            batchId, ExtractionSourceType.Email,
            new ExtractionJobMetadata { SourceOccurrenceId = "attachment-2" });

        Assert.NotEqual(first, second);
        Assert.StartsWith($"{batchId:D}:Email:", first, StringComparison.Ordinal);
    }

    [Fact]
    public void OccurrenceIdentity_IsStableWhenCallerSuppliesSourceIdentity()
    {
        var batchId = Guid.NewGuid();
        var metadata = new ExtractionJobMetadata { SourceOccurrenceId = "mime-part-7" };

        var first = SourceOccurrenceIdentity.BuildKey(batchId, ExtractionSourceType.Email, metadata);
        var retry = SourceOccurrenceIdentity.BuildKey(batchId, ExtractionSourceType.Email, metadata);

        Assert.Equal(first, retry);
        Assert.DoesNotContain(metadata.SourceOccurrenceId, first, StringComparison.Ordinal);
    }

    [Fact]
    public void OccurrenceIdentity_DistinguishesReceiptsWhenCallerOmitsBatchAndSourceIdentity()
    {
        const long businessUnitId = 91;
        const string fileName = "customer-rfq.csv";
        const string contentHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        var firstBatch = SourceOccurrenceIdentity.BuildFallbackBatchId(
            businessUnitId, ExtractionSourceType.ManualUpload, fileName, contentHash, null);
        var retryBatch = SourceOccurrenceIdentity.BuildFallbackBatchId(
            businessUnitId, ExtractionSourceType.ManualUpload, fileName, contentHash, null);
        var first = SourceOccurrenceIdentity.BuildKey(
            firstBatch, ExtractionSourceType.ManualUpload, null);
        var retry = SourceOccurrenceIdentity.BuildKey(
            retryBatch, ExtractionSourceType.ManualUpload, null);

        Assert.NotEqual(firstBatch, retryBatch);
        Assert.NotEqual(first, retry);
    }

    [Fact]
    public void OccurrenceIdentity_ReusesFallbackBatchWhenCallerSuppliesSourceIdentity()
    {
        var metadata = new ExtractionJobMetadata { SourceOccurrenceId = "upload-request-42:file.csv" };
        var first = SourceOccurrenceIdentity.BuildFallbackBatchId(91, ExtractionSourceType.ManualUpload,
            "file.csv", new string('a', 64), metadata);
        var retry = SourceOccurrenceIdentity.BuildFallbackBatchId(91, ExtractionSourceType.ManualUpload,
            "file.csv", new string('a', 64), metadata);

        Assert.Equal(first, retry);
    }
}
