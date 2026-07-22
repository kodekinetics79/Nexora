using ERP_RFQ_Automation.DocumentIntelligence.Persistence;

namespace ERP_RFQ_Automation.Tests.DocumentIntelligence.Persistence;

public sealed class EvidenceLedgerDomainTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void SourceDocument_RequiresImmutableContentAddressAndObjectVersion()
    {
        var document = SourceDocument.Create(7, 12, Hash, "buyer-rfq.pdf", "application/pdf",
            "tenant-7", "sha256/01/23/document.pdf", "version-1", 4096);

        Assert.Equal(Hash, document.ContentHash);
        Assert.Equal("tenant-7", document.ObjectBucket);
        Assert.Equal("sha256/01/23/document.pdf", document.ObjectKey);
        Assert.Equal("version-1", document.ObjectVersion);
        Assert.Equal(DocumentSecurityStatus.Pending, document.SecurityStatus);
        Assert.Equal(DocumentProcessingStatus.Received, document.ProcessingStatus);
        Assert.False(typeof(SourceDocument).GetProperty(nameof(SourceDocument.ContentHash))!.SetMethod!.IsPublic);
        Assert.False(typeof(SourceDocument).GetProperty(nameof(SourceDocument.ObjectKey))!.SetMethod!.IsPublic);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef0123456789")]
    [InlineData("not-a-sha256")]
    public void SourceDocument_RejectsInvalidHashes(string hash)
    {
        Assert.Throws<ArgumentException>(() => SourceDocument.Create(7, 12, hash, "rfq.pdf",
            "application/pdf", "tenant-7", "document.pdf", "v1", 1));
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public void Region_RejectsConfidenceOutsideUnitInterval(decimal confidence)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DocumentRegion.Create(
            7, 10, DocumentRegionType.TableCell, 0, 0, 100, 20, "value", confidence));
    }

    [Fact]
    public void Page_RejectsUnsupportedRotation()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DocumentPage.Create(7, 10, 1, 612, 792, rotation: 45, textHash: Hash));
    }

    [Fact]
    public void EvidenceFactories_CreateExactlyOneTypedTarget()
    {
        var runId = Guid.NewGuid();
        var inquiryEvidence = FieldEvidence.ForInquiry(7, 20, 30, "BuyerName", "ACME", "Acme Ltd",
            0.98m, "native-pdf-v1", runId);
        var lineEvidence = FieldEvidence.ForLineItem(7, 21, 40, "Quantity", "10", "10",
            0.93m, "table-v2", runId);

        Assert.Equal(30, inquiryEvidence.InquiryId);
        Assert.Null(inquiryEvidence.LineItemId);
        Assert.Null(lineEvidence.InquiryId);
        Assert.Equal(40, lineEvidence.LineItemId);
    }
}
