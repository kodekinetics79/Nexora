namespace ERP_RFQ_Automation.DocumentIntelligence.Persistence;

public enum CorpusSourceType
{
    Email,
    ManualUpload,
    Folder,
    Api,
    Import
}

public enum CorpusStatus
{
    Received,
    Processing,
    ReviewRequired,
    Completed,
    Failed
}

public enum DocumentSecurityStatus
{
    Pending,
    Quarantined,
    Cleared,
    Rejected
}

public enum DocumentProcessingStatus
{
    Received,
    Extracting,
    Normalizing,
    ReviewRequired,
    Completed,
    Failed
}

public enum OcrStatus
{
    NotRequired,
    Pending,
    Processing,
    Completed,
    Failed
}

public enum DocumentRegionType
{
    Text,
    Table,
    TableCell,
    Header,
    Footer,
    Image,
    Barcode,
    Signature,
    Unknown
}

public enum CanonicalInquiryStatus
{
    Draft,
    ReviewRequired,
    Validated,
    Rejected
}

public sealed class DocumentCorpus
{
    private DocumentCorpus() { }

    private DocumentCorpus(long businessUnitId, Guid batchId, CorpusSourceType sourceType, DateTimeOffset now)
    {
        BusinessUnitId = EvidenceLedgerGuard.Positive(businessUnitId, nameof(businessUnitId));
        BatchId = EvidenceLedgerGuard.NotEmpty(batchId, nameof(batchId));
        SourceType = sourceType;
        Status = CorpusStatus.Received;
        CreatedOn = now;
        UpdatedOn = now;
    }

    public long Id { get; private set; }
    public long BusinessUnitId { get; private set; }
    public Guid BatchId { get; private set; }
    public CorpusSourceType SourceType { get; private set; }
    public CorpusStatus Status { get; private set; }
    public DateTimeOffset CreatedOn { get; private set; }
    public DateTimeOffset UpdatedOn { get; private set; }
    public ICollection<SourceDocument> Documents { get; } = new List<SourceDocument>();
    public ICollection<CanonicalInquiry> Inquiries { get; } = new List<CanonicalInquiry>();

    public static DocumentCorpus Create(long businessUnitId, Guid batchId, CorpusSourceType sourceType,
        DateTimeOffset? createdOn = null) =>
        new(businessUnitId, batchId, sourceType, createdOn ?? DateTimeOffset.UtcNow);
}

public sealed class SourceDocument
{
    private SourceDocument() { }

    private SourceDocument(long businessUnitId, long corpusId, string contentHash, string originalFileName,
        string detectedMimeType, string objectBucket, string objectKey, string objectVersion, long byteSize,
        DateTimeOffset now)
    {
        BusinessUnitId = EvidenceLedgerGuard.Positive(businessUnitId, nameof(businessUnitId));
        CorpusId = EvidenceLedgerGuard.Positive(corpusId, nameof(corpusId));
        ContentHash = EvidenceLedgerGuard.Sha256(contentHash, nameof(contentHash));
        OriginalFileName = EvidenceLedgerGuard.Required(originalFileName, 512, nameof(originalFileName));
        DetectedMimeType = EvidenceLedgerGuard.Required(detectedMimeType, 255, nameof(detectedMimeType));
        ObjectBucket = EvidenceLedgerGuard.Required(objectBucket, 255, nameof(objectBucket));
        ObjectKey = EvidenceLedgerGuard.Required(objectKey, 1024, nameof(objectKey));
        ObjectVersion = EvidenceLedgerGuard.Required(objectVersion, 255, nameof(objectVersion));
        ByteSize = EvidenceLedgerGuard.NonNegative(byteSize, nameof(byteSize));
        SecurityStatus = DocumentSecurityStatus.Pending;
        ProcessingStatus = DocumentProcessingStatus.Received;
        CreatedOn = now;
        UpdatedOn = now;
    }

    public long Id { get; private set; }
    public long BusinessUnitId { get; private set; }
    public long CorpusId { get; private set; }
    public long? ExtractionJobId { get; private set; }
    public string ContentHash { get; private set; } = null!;
    public string OriginalFileName { get; private set; } = null!;
    public string DetectedMimeType { get; private set; } = null!;
    public string ObjectBucket { get; private set; } = null!;
    public string ObjectKey { get; private set; } = null!;
    public string ObjectVersion { get; private set; } = null!;
    public long ByteSize { get; private set; }
    public int PageCount { get; private set; }
    public DocumentSecurityStatus SecurityStatus { get; private set; }
    public DocumentProcessingStatus ProcessingStatus { get; private set; }
    public DateTimeOffset CreatedOn { get; private set; }
    public DateTimeOffset UpdatedOn { get; private set; }
    public DocumentCorpus Corpus { get; private set; } = null!;
    public ICollection<DocumentPage> Pages { get; } = new List<DocumentPage>();

    public static SourceDocument Create(long businessUnitId, long corpusId, string contentHash,
        string originalFileName, string detectedMimeType, string objectBucket, string objectKey,
        string objectVersion, long byteSize, DateTimeOffset? createdOn = null) =>
        new(businessUnitId, corpusId, contentHash, originalFileName, detectedMimeType, objectBucket,
            objectKey, objectVersion, byteSize, createdOn ?? DateTimeOffset.UtcNow);
}

public sealed class DocumentPage
{
    private DocumentPage() { }

    private DocumentPage(long businessUnitId, long documentId, int pageNumber, decimal width,
        decimal height, int rotation, string? textHash, DateTimeOffset now)
    {
        BusinessUnitId = EvidenceLedgerGuard.Positive(businessUnitId, nameof(businessUnitId));
        DocumentId = EvidenceLedgerGuard.Positive(documentId, nameof(documentId));
        PageNumber = EvidenceLedgerGuard.Positive(pageNumber, nameof(pageNumber));
        Width = EvidenceLedgerGuard.Positive(width, nameof(width));
        Height = EvidenceLedgerGuard.Positive(height, nameof(height));
        Rotation = EvidenceLedgerGuard.Rotation(rotation, nameof(rotation));
        TextHash = textHash is null ? null : EvidenceLedgerGuard.Sha256(textHash, nameof(textHash));
        OcrStatus = OcrStatus.Pending;
        CreatedOn = now;
        UpdatedOn = now;
    }

    public long Id { get; private set; }
    public long BusinessUnitId { get; private set; }
    public long DocumentId { get; private set; }
    public int PageNumber { get; private set; }
    public decimal Width { get; private set; }
    public decimal Height { get; private set; }
    public int Rotation { get; private set; }
    public string? TextHash { get; private set; }
    public OcrStatus OcrStatus { get; private set; }
    public decimal? OcrConfidence { get; private set; }
    public DateTimeOffset CreatedOn { get; private set; }
    public DateTimeOffset UpdatedOn { get; private set; }
    public SourceDocument Document { get; private set; } = null!;
    public ICollection<DocumentRegion> Regions { get; } = new List<DocumentRegion>();

    public static DocumentPage Create(long businessUnitId, long documentId, int pageNumber, decimal width,
        decimal height, int rotation = 0, string? textHash = null, DateTimeOffset? createdOn = null) =>
        new(businessUnitId, documentId, pageNumber, width, height, rotation, textHash,
            createdOn ?? DateTimeOffset.UtcNow);
}

public sealed class DocumentRegion
{
    private DocumentRegion() { }

    private DocumentRegion(long businessUnitId, long pageId, DocumentRegionType regionType,
        decimal x, decimal y, decimal width, decimal height, string? text, decimal confidence,
        DateTimeOffset now)
    {
        BusinessUnitId = EvidenceLedgerGuard.Positive(businessUnitId, nameof(businessUnitId));
        PageId = EvidenceLedgerGuard.Positive(pageId, nameof(pageId));
        RegionType = regionType;
        X = EvidenceLedgerGuard.NonNegative(x, nameof(x));
        Y = EvidenceLedgerGuard.NonNegative(y, nameof(y));
        Width = EvidenceLedgerGuard.Positive(width, nameof(width));
        Height = EvidenceLedgerGuard.Positive(height, nameof(height));
        Text = EvidenceLedgerGuard.Optional(text, 100_000, nameof(text));
        Confidence = EvidenceLedgerGuard.Confidence(confidence, nameof(confidence));
        CreatedOn = now;
    }

    public long Id { get; private set; }
    public long BusinessUnitId { get; private set; }
    public long PageId { get; private set; }
    public DocumentRegionType RegionType { get; private set; }
    public decimal X { get; private set; }
    public decimal Y { get; private set; }
    public decimal Width { get; private set; }
    public decimal Height { get; private set; }
    public string? Text { get; private set; }
    public decimal Confidence { get; private set; }
    public DateTimeOffset CreatedOn { get; private set; }
    public DocumentPage Page { get; private set; } = null!;
    public ICollection<FieldEvidence> Evidence { get; } = new List<FieldEvidence>();

    public static DocumentRegion Create(long businessUnitId, long pageId, DocumentRegionType regionType,
        decimal x, decimal y, decimal width, decimal height, string? text, decimal confidence,
        DateTimeOffset? createdOn = null) =>
        new(businessUnitId, pageId, regionType, x, y, width, height, text, confidence,
            createdOn ?? DateTimeOffset.UtcNow);
}

public sealed class CanonicalInquiry
{
    private CanonicalInquiry() { }

    private CanonicalInquiry(long businessUnitId, long corpusId, int inquiryNumber, DateTimeOffset now)
    {
        BusinessUnitId = EvidenceLedgerGuard.Positive(businessUnitId, nameof(businessUnitId));
        CorpusId = EvidenceLedgerGuard.Positive(corpusId, nameof(corpusId));
        InquiryNumber = EvidenceLedgerGuard.Positive(inquiryNumber, nameof(inquiryNumber));
        Status = CanonicalInquiryStatus.Draft;
        CreatedOn = now;
        UpdatedOn = now;
    }

    public long Id { get; private set; }
    public long BusinessUnitId { get; private set; }
    public long CorpusId { get; private set; }
    public int InquiryNumber { get; private set; }
    public long? LeadId { get; private set; }
    public string? CustomerRfqNumber { get; private set; }
    public string? BuyerName { get; private set; }
    public CanonicalInquiryStatus Status { get; private set; }
    public DateTimeOffset CreatedOn { get; private set; }
    public DateTimeOffset UpdatedOn { get; private set; }
    public DocumentCorpus Corpus { get; private set; } = null!;
    public ICollection<CanonicalLineItem> LineItems { get; } = new List<CanonicalLineItem>();
    public ICollection<FieldEvidence> Evidence { get; } = new List<FieldEvidence>();

    public static CanonicalInquiry Create(long businessUnitId, long corpusId, int inquiryNumber,
        DateTimeOffset? createdOn = null) =>
        new(businessUnitId, corpusId, inquiryNumber, createdOn ?? DateTimeOffset.UtcNow);
}

public sealed class CanonicalLineItem
{
    private CanonicalLineItem() { }

    private CanonicalLineItem(long businessUnitId, long inquiryId, int lineNumber, string description,
        decimal? quantity, string? unitOfMeasure, DateTimeOffset now)
    {
        BusinessUnitId = EvidenceLedgerGuard.Positive(businessUnitId, nameof(businessUnitId));
        InquiryId = EvidenceLedgerGuard.Positive(inquiryId, nameof(inquiryId));
        LineNumber = EvidenceLedgerGuard.Positive(lineNumber, nameof(lineNumber));
        Description = EvidenceLedgerGuard.Required(description, 4_000, nameof(description));
        Quantity = quantity is null ? null : EvidenceLedgerGuard.Positive(quantity.Value, nameof(quantity));
        UnitOfMeasure = EvidenceLedgerGuard.Optional(unitOfMeasure, 64, nameof(unitOfMeasure));
        CreatedOn = now;
        UpdatedOn = now;
    }

    public long Id { get; private set; }
    public long BusinessUnitId { get; private set; }
    public long InquiryId { get; private set; }
    public int LineNumber { get; private set; }
    public string Description { get; private set; } = null!;
    public decimal? Quantity { get; private set; }
    public string? UnitOfMeasure { get; private set; }
    public string? Manufacturer { get; private set; }
    public string? ManufacturerPartNumber { get; private set; }
    public string? CurrencyCode { get; private set; }
    public string? RawPayload { get; private set; }
    public DateTimeOffset CreatedOn { get; private set; }
    public DateTimeOffset UpdatedOn { get; private set; }
    public CanonicalInquiry Inquiry { get; private set; } = null!;
    public ICollection<FieldEvidence> Evidence { get; } = new List<FieldEvidence>();

    public static CanonicalLineItem Create(long businessUnitId, long inquiryId, int lineNumber,
        string description, decimal? quantity = null, string? unitOfMeasure = null,
        DateTimeOffset? createdOn = null) =>
        new(businessUnitId, inquiryId, lineNumber, description, quantity, unitOfMeasure,
            createdOn ?? DateTimeOffset.UtcNow);
}

public sealed class FieldEvidence
{
    private FieldEvidence() { }

    private FieldEvidence(long businessUnitId, long regionId, long? inquiryId, long? lineItemId,
        string fieldName, string? rawValue, string? normalizedValue, decimal confidence,
        string extractor, Guid runId, DateTimeOffset now)
    {
        if ((inquiryId.HasValue ? 1 : 0) + (lineItemId.HasValue ? 1 : 0) != 1)
            throw new ArgumentException("Evidence must target exactly one canonical entity.");

        BusinessUnitId = EvidenceLedgerGuard.Positive(businessUnitId, nameof(businessUnitId));
        RegionId = EvidenceLedgerGuard.Positive(regionId, nameof(regionId));
        InquiryId = inquiryId is null ? null : EvidenceLedgerGuard.Positive(inquiryId.Value, nameof(inquiryId));
        LineItemId = lineItemId is null ? null : EvidenceLedgerGuard.Positive(lineItemId.Value, nameof(lineItemId));
        FieldName = EvidenceLedgerGuard.Required(fieldName, 256, nameof(fieldName));
        RawValue = EvidenceLedgerGuard.Optional(rawValue, 100_000, nameof(rawValue));
        NormalizedValue = EvidenceLedgerGuard.Optional(normalizedValue, 100_000, nameof(normalizedValue));
        Confidence = EvidenceLedgerGuard.Confidence(confidence, nameof(confidence));
        Extractor = EvidenceLedgerGuard.Required(extractor, 256, nameof(extractor));
        RunId = EvidenceLedgerGuard.NotEmpty(runId, nameof(runId));
        CreatedOn = now;
    }

    public long Id { get; private set; }
    public long BusinessUnitId { get; private set; }
    public long RegionId { get; private set; }
    public long? InquiryId { get; private set; }
    public long? LineItemId { get; private set; }
    public string FieldName { get; private set; } = null!;
    public string? RawValue { get; private set; }
    public string? NormalizedValue { get; private set; }
    public decimal Confidence { get; private set; }
    public string Extractor { get; private set; } = null!;
    public Guid RunId { get; private set; }
    public DateTimeOffset CreatedOn { get; private set; }
    public DocumentRegion Region { get; private set; } = null!;
    public CanonicalInquiry? Inquiry { get; private set; }
    public CanonicalLineItem? LineItem { get; private set; }

    public static FieldEvidence ForInquiry(long businessUnitId, long regionId, long inquiryId,
        string fieldName, string? rawValue, string? normalizedValue, decimal confidence,
        string extractor, Guid runId, DateTimeOffset? createdOn = null) =>
        new(businessUnitId, regionId, inquiryId, null, fieldName, rawValue, normalizedValue,
            confidence, extractor, runId, createdOn ?? DateTimeOffset.UtcNow);

    public static FieldEvidence ForLineItem(long businessUnitId, long regionId, long lineItemId,
        string fieldName, string? rawValue, string? normalizedValue, decimal confidence,
        string extractor, Guid runId, DateTimeOffset? createdOn = null) =>
        new(businessUnitId, regionId, null, lineItemId, fieldName, rawValue, normalizedValue,
            confidence, extractor, runId, createdOn ?? DateTimeOffset.UtcNow);
}

internal static class EvidenceLedgerGuard
{
    public static long Positive(long value, string name) => value > 0
        ? value
        : throw new ArgumentOutOfRangeException(name, "Value must be positive.");

    public static int Positive(int value, string name) => value > 0
        ? value
        : throw new ArgumentOutOfRangeException(name, "Value must be positive.");

    public static decimal Positive(decimal value, string name) => value > 0
        ? value
        : throw new ArgumentOutOfRangeException(name, "Value must be positive.");

    public static long NonNegative(long value, string name) => value >= 0
        ? value
        : throw new ArgumentOutOfRangeException(name, "Value cannot be negative.");

    public static decimal NonNegative(decimal value, string name) => value >= 0
        ? value
        : throw new ArgumentOutOfRangeException(name, "Value cannot be negative.");

    public static decimal Confidence(decimal value, string name) => value is >= 0 and <= 1
        ? value
        : throw new ArgumentOutOfRangeException(name, "Confidence must be between zero and one.");

    public static int Rotation(int value, string name) => value is 0 or 90 or 180 or 270
        ? value
        : throw new ArgumentOutOfRangeException(name, "Rotation must be 0, 90, 180, or 270 degrees.");

    public static Guid NotEmpty(Guid value, string name) => value != Guid.Empty
        ? value
        : throw new ArgumentException("Value cannot be empty.", name);

    public static string Required(string value, int maxLength, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Value is required.", name);
        if (value.Length > maxLength)
            throw new ArgumentException($"Value cannot exceed {maxLength} characters.", name);
        return value;
    }

    public static string? Optional(string? value, int maxLength, string name)
    {
        if (value?.Length > maxLength)
            throw new ArgumentException($"Value cannot exceed {maxLength} characters.", name);
        return value;
    }

    public static string Sha256(string value, string name)
    {
        Required(value, 64, name);
        if (value.Length != 64 || value.Any(c => !char.IsAsciiHexDigit(c) || char.IsUpper(c)))
            throw new ArgumentException("Value must be a lower-case SHA-256 hex digest.", name);
        return value;
    }
}
