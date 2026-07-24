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
    public void OccurrenceIdentity_GeneratesDistinctReceiptIdsWhenCallerHasNoIdentity()
    {
        var batchId = Guid.NewGuid();

        var first = SourceOccurrenceIdentity.BuildKey(batchId, ExtractionSourceType.ManualUpload, null);
        var second = SourceOccurrenceIdentity.BuildKey(batchId, ExtractionSourceType.ManualUpload, null);

        Assert.NotEqual(first, second);
    }
}
