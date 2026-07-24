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

    [Fact]
    public void Occurrence_ValidatesMetadataAndBindsJobOnlyOnce()
    {
        var occurrence = SourceDocumentOccurrence.Create(7, 12, 15, "email:message-1:attachment-2",
            "{\"sender\":\"buyer@example.com\"}");

        occurrence.BindExtractionJob(42);

        Assert.Equal(42, occurrence.ExtractionJobId);
        Assert.Throws<InvalidOperationException>(() => occurrence.BindExtractionJob(43));
        Assert.Throws<ArgumentException>(() => SourceDocumentOccurrence.Create(7, 12, 15, "key", "not-json"));
    }

    [Fact]
    public void ExtractionRun_EnforcesLifecycleAndCapturesTerminalCounts()
    {
        var run = ExtractionRun.Create(7, 12, Guid.NewGuid(), 42, 2, "xlsx-native/v2", "rfq-canonical/v1");

        run.Start();
        run.Complete(3, 24, 2, 12, 48, 1);

        Assert.Equal(ExtractionRunStatus.Completed, run.Status);
        Assert.Equal(48, run.EvidenceCount);
        Assert.NotNull(run.StartedOn);
        Assert.NotNull(run.CompletedOn);
        Assert.Throws<InvalidOperationException>(() => run.Start());
    }

    [Fact]
    public void SpreadsheetCoordinates_RequireSheetAndAddress()
    {
        var sheet = DocumentPage.Create(7, 12, 1, 20, 10, pageKind: DocumentPageKind.Worksheet,
            sheetName: "Line Items");
        var cell = DocumentRegion.Create(7, 20, DocumentRegionType.TableCell, 0, 0, 1, 1,
            "10", 1m, sourceAddress: "C7", rowNumber: 7, columnNumber: 3);

        Assert.Equal("Line Items", sheet.SheetName);
        Assert.Equal("C7", cell.SourceAddress);
        Assert.Throws<ArgumentException>(() => DocumentPage.Create(7, 12, 1, 20, 10,
            pageKind: DocumentPageKind.Worksheet));
        Assert.Throws<ArgumentException>(() => DocumentRegion.Create(7, 20,
            DocumentRegionType.TableCell, 0, 0, 1, 1, "10", 1m, rowNumber: 7, columnNumber: 3));
    }

    [Fact]
    public void CanonicalEntities_PopulateValidateAndBindCommercialProjection()
    {
        var inquiry = CanonicalInquiry.Create(7, 15, 1);
        inquiry.PopulateHeader("RFQ-100", "Ada Buyer",
            new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.Zero));
        inquiry.RequireReview();
        inquiry.BindLead(100);
        inquiry.Validate();

        var line = CanonicalLineItem.Create(7, 30, 1, "Valve", 10, "EA");
        line.Enrich("Acme", "V-10", "USD", 12.50m, 7, "{\"source\":\"C7\"}",
            CanonicalValidationStatus.Valid);
        line.BindLeadItem(101);

        Assert.Equal(CanonicalInquiryStatus.Validated, inquiry.Status);
        Assert.Equal(100, inquiry.LeadId);
        Assert.Equal(12.50m, line.UnitPrice);
        Assert.Equal(101, line.LeadItemId);
        Assert.Throws<InvalidOperationException>(() => inquiry.PopulateHeader("changed", null, null, null));
    }

    [Fact]
    public void FieldEvidence_CreatesStableKeyAndCarriesValidationMetadata()
    {
        var runId = Guid.Parse("b7d3db63-5c43-470f-9fbb-6224cc589739");
        var first = FieldEvidence.ForLineItem(7, 21, 40, "Quantity", "010", "10", 1m,
            "xlsx-native/v2", runId, valueKind: FieldValueKind.Number,
            validationStatus: FieldValidationStatus.Valid,
            transformationsJson: "[\"trim\",\"parse-decimal\"]");
        var replay = FieldEvidence.ForLineItem(7, 21, 40, "quantity", "010", "10", 1m,
            "xlsx-native/v2", runId, valueKind: FieldValueKind.Number,
            validationStatus: FieldValidationStatus.Valid,
            transformationsJson: "[\"trim\",\"parse-decimal\"]");

        Assert.Equal(first.EvidenceKey, replay.EvidenceKey);
        Assert.Equal(64, first.EvidenceKey.Length);
        Assert.Equal(FieldValueKind.Number, first.ValueKind);
        Assert.Equal(FieldValidationStatus.Valid, first.ValidationStatus);
    }

    [Fact]
    public void ValidationFinding_AllowsRunLevelOrSingleCanonicalTarget()
    {
        var runFinding = ValidationFinding.ForRun(7, 50, "SHEET_SKIPPED",
            ValidationSeverity.Warning, "Hidden worksheet was skipped.", regionId: 20);
        var lineFinding = ValidationFinding.ForLineItem(7, 50, 40, "QUANTITY_INVALID",
            ValidationSeverity.Error, "Quantity must be positive.");

        Assert.Null(runFinding.InquiryId);
        Assert.Null(runFinding.LineItemId);
        Assert.Equal(40, lineFinding.LineItemId);
    }
}
