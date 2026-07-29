using System.Security.Cryptography;
using System.Text;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Security.DocumentInspection;

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

public enum IngestionOutcomeState
{
    NONE,
    EXACT_DUPLICATE_PENDING_SECURITY,
    EXACT_DUPLICATE_CONFIRMED,
    BUSINESS_DUPLICATE_CONFIRMED,
    DUPLICATE_RESCAN_REQUIRED,
    REVISION,
    POSSIBLE_MATCH,
    SECURITY_SCAN_BLOCKED,
    MALWARE_DETECTED,
    UNSUPPORTED_FORMAT
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

public enum DocumentPageKind
{
    PhysicalPage,
    Worksheet,
    CsvSheet
}

public enum CanonicalInquiryStatus
{
    Draft,
    ReviewRequired,
    Validated,
    Rejected
}

public enum CanonicalValidationStatus
{
    Unvalidated,
    Valid,
    Warning,
    Invalid
}

public enum ExtractionRunStatus
{
    Pending,
    Processing,
    Completed,
    Failed
}

public enum ValidationSeverity
{
    Information,
    Warning,
    Error,
    Critical
}

public enum FieldValueKind
{
    Text,
    Number,
    Date,
    Boolean,
    Currency,
    Identifier,
    Json
}

public enum FieldValidationStatus
{
    Unvalidated,
    Valid,
    Warning,
    Invalid
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
    public ICollection<SourceDocumentOccurrence> Occurrences { get; } = new List<SourceDocumentOccurrence>();
    public ICollection<CanonicalInquiry> Inquiries { get; } = new List<CanonicalInquiry>();

    public static DocumentCorpus Create(long businessUnitId, Guid batchId, CorpusSourceType sourceType,
        DateTimeOffset? createdOn = null) =>
        new(businessUnitId, batchId, sourceType, createdOn ?? DateTimeOffset.UtcNow);

    public void StartProcessing(DateTimeOffset? changedOn = null) =>
        TransitionTo(CorpusStatus.Processing, changedOn ?? DateTimeOffset.UtcNow, CorpusStatus.Received);

    public void RequireReview(DateTimeOffset? changedOn = null) =>
        TransitionTo(CorpusStatus.ReviewRequired, changedOn ?? DateTimeOffset.UtcNow,
            CorpusStatus.Received, CorpusStatus.Processing);

    public void Complete(DateTimeOffset? changedOn = null) =>
        TransitionTo(CorpusStatus.Completed, changedOn ?? DateTimeOffset.UtcNow,
            CorpusStatus.Processing, CorpusStatus.ReviewRequired);

    public void Fail(DateTimeOffset? changedOn = null) =>
        TransitionTo(CorpusStatus.Failed, changedOn ?? DateTimeOffset.UtcNow,
            CorpusStatus.Received, CorpusStatus.Processing, CorpusStatus.ReviewRequired);

    private void TransitionTo(CorpusStatus next, DateTimeOffset changedOn, params CorpusStatus[] allowed)
    {
        EvidenceLedgerGuard.Transition(Status, next, allowed);
        Status = next;
        UpdatedOn = changedOn;
    }
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
    public string? MalwareVerdictStatus { get; private set; }
    public string? MalwareScannerEngine { get; private set; }
    public string? MalwareSignatureVersion { get; private set; }
    public DateTimeOffset? MalwareScannedOn { get; private set; }
    public DocumentCorpus Corpus { get; private set; } = null!;
    public ICollection<DocumentPage> Pages { get; } = new List<DocumentPage>();
    public ICollection<SourceDocumentOccurrence> Occurrences { get; } = new List<SourceDocumentOccurrence>();
    public ICollection<ExtractionRun> ExtractionRuns { get; } = new List<ExtractionRun>();

    public static SourceDocument Create(long businessUnitId, long corpusId, string contentHash,
        string originalFileName, string detectedMimeType, string objectBucket, string objectKey,
        string objectVersion, long byteSize, DateTimeOffset? createdOn = null) =>
        new(businessUnitId, corpusId, contentHash, originalFileName, detectedMimeType, objectBucket,
            objectKey, objectVersion, byteSize, createdOn ?? DateTimeOffset.UtcNow);

    public void BindExtractionJob(long extractionJobId, DateTimeOffset? changedOn = null)
    {
        var id = EvidenceLedgerGuard.Positive(extractionJobId, nameof(extractionJobId));
        if (ExtractionJobId.HasValue && ExtractionJobId != id)
            throw new InvalidOperationException("The document is already bound to another extraction job.");
        ExtractionJobId = id;
        UpdatedOn = changedOn ?? DateTimeOffset.UtcNow;
    }

    public bool HasFreshCleanMalwareVerdict(DateTimeOffset now, TimeSpan maximumAge) =>
        string.Equals(MalwareVerdictStatus, "Clean", StringComparison.Ordinal)
        && MalwareScannedOn.HasValue
        && maximumAge > TimeSpan.Zero
        && MalwareScannedOn.Value >= now.Subtract(maximumAge);

    public void RecordInspection(string detectedMimeType, DateTimeOffset? changedOn = null)
    {
        DetectedMimeType = EvidenceLedgerGuard.Required(detectedMimeType, 255, nameof(detectedMimeType));
        UpdatedOn = changedOn ?? DateTimeOffset.UtcNow;
    }

    public void RecordMalwareVerdict(
        MalwareScanStatus status,
        string engine,
        string? signatureVersion,
        DateTimeOffset? scannedOn = null)
    {
        MalwareVerdictStatus = status.ToString();
        MalwareScannerEngine = EvidenceLedgerGuard.Required(engine, 128, nameof(engine));
        MalwareSignatureVersion = string.IsNullOrWhiteSpace(signatureVersion)
            ? null
            : EvidenceLedgerGuard.Required(signatureVersion, 256, nameof(signatureVersion));
        MalwareScannedOn = scannedOn ?? DateTimeOffset.UtcNow;
        UpdatedOn = MalwareScannedOn.Value;
    }

    public void MarkSecurityStatus(DocumentSecurityStatus status, DateTimeOffset? changedOn = null)
    {
        var allowed = SecurityStatus switch
        {
            DocumentSecurityStatus.Pending => status is DocumentSecurityStatus.Quarantined
                or DocumentSecurityStatus.Cleared or DocumentSecurityStatus.Rejected,
            DocumentSecurityStatus.Quarantined => status is DocumentSecurityStatus.Cleared
                or DocumentSecurityStatus.Rejected,
            DocumentSecurityStatus.Cleared => status is DocumentSecurityStatus.Rejected,
            _ => false
        };
        if (!allowed)
            throw new InvalidOperationException($"Security status cannot transition from {SecurityStatus} to {status}.");
        SecurityStatus = status;
        UpdatedOn = changedOn ?? DateTimeOffset.UtcNow;
    }

    public void ReleaseFromQuarantine(
        string objectBucket,
        string objectKey,
        string objectVersion,
        DateTimeOffset? changedOn = null)
    {
        if (SecurityStatus is not (DocumentSecurityStatus.Pending or DocumentSecurityStatus.Quarantined))
            throw new InvalidOperationException("Only a pending or quarantined source document can be cleared.");

        ObjectBucket = EvidenceLedgerGuard.Required(objectBucket, 255, nameof(objectBucket));
        ObjectKey = EvidenceLedgerGuard.Required(objectKey, 1024, nameof(objectKey));
        ObjectVersion = EvidenceLedgerGuard.Required(objectVersion, 255, nameof(objectVersion));
        MarkSecurityStatus(DocumentSecurityStatus.Cleared, changedOn);
    }

    public void StartExtraction(DateTimeOffset? changedOn = null) =>
        TransitionProcessing(DocumentProcessingStatus.Extracting, changedOn ?? DateTimeOffset.UtcNow,
            DocumentProcessingStatus.Received);

    public void StartNormalization(DateTimeOffset? changedOn = null) =>
        TransitionProcessing(DocumentProcessingStatus.Normalizing, changedOn ?? DateTimeOffset.UtcNow,
            DocumentProcessingStatus.Extracting);

    public void RequireReview(int pageCount, DateTimeOffset? changedOn = null)
    {
        PageCount = EvidenceLedgerGuard.NonNegative(pageCount, nameof(pageCount));
        TransitionProcessing(DocumentProcessingStatus.ReviewRequired, changedOn ?? DateTimeOffset.UtcNow,
            DocumentProcessingStatus.Extracting, DocumentProcessingStatus.Normalizing);
    }

    public void Complete(int pageCount, DateTimeOffset? changedOn = null)
    {
        PageCount = EvidenceLedgerGuard.NonNegative(pageCount, nameof(pageCount));
        TransitionProcessing(DocumentProcessingStatus.Completed, changedOn ?? DateTimeOffset.UtcNow,
            DocumentProcessingStatus.Normalizing, DocumentProcessingStatus.ReviewRequired);
    }

    public void Fail(DateTimeOffset? changedOn = null) =>
        TransitionProcessing(DocumentProcessingStatus.Failed, changedOn ?? DateTimeOffset.UtcNow,
            DocumentProcessingStatus.Received, DocumentProcessingStatus.Extracting,
            DocumentProcessingStatus.Normalizing, DocumentProcessingStatus.ReviewRequired);

    private void TransitionProcessing(DocumentProcessingStatus next, DateTimeOffset changedOn,
        params DocumentProcessingStatus[] allowed)
    {
        EvidenceLedgerGuard.Transition(ProcessingStatus, next, allowed);
        ProcessingStatus = next;
        UpdatedOn = changedOn;
    }
}

public sealed class SourceDocumentOccurrence
{
    private SourceDocumentOccurrence() { }

    private SourceDocumentOccurrence(long businessUnitId, long sourceDocumentId, long corpusId,
        long? extractionJobId, string idempotencyKey, string sourceMetadataJson, DateTimeOffset receivedOn)
    {
        BusinessUnitId = EvidenceLedgerGuard.Positive(businessUnitId, nameof(businessUnitId));
        SourceDocumentId = EvidenceLedgerGuard.Positive(sourceDocumentId, nameof(sourceDocumentId));
        CorpusId = EvidenceLedgerGuard.Positive(corpusId, nameof(corpusId));
        ExtractionJobId = extractionJobId is null
            ? null
            : EvidenceLedgerGuard.Positive(extractionJobId.Value, nameof(extractionJobId));
        IdempotencyKey = EvidenceLedgerGuard.Required(idempotencyKey, 256, nameof(idempotencyKey));
        SourceMetadataJson = EvidenceLedgerGuard.Json(sourceMetadataJson, nameof(sourceMetadataJson));
        IntakeStatus = IntakeOccurrenceStatus.Accepted;
        ReceivedOn = receivedOn;
        UpdatedOn = receivedOn;
    }

    public long Id { get; private set; }
    public long BusinessUnitId { get; private set; }
    public long SourceDocumentId { get; private set; }
    public long CorpusId { get; private set; }
    public long? ExtractionJobId { get; private set; }
    public long? OriginalOccurrenceId { get; private set; }
    public string IdempotencyKey { get; private set; } = null!;
    public string SourceMetadataJson { get; private set; } = null!;
    public string? LogicalGroupKey { get; private set; }
    public IntakeOccurrenceStatus IntakeStatus { get; private set; }
    public string? LastErrorCategory { get; private set; }
    public string? LastErrorCode { get; private set; }
    public string? LastErrorDetailsJson { get; private set; }
    public DateTimeOffset UpdatedOn { get; private set; }
    public DateTimeOffset ReceivedOn { get; private set; }
    public IngestionOutcomeState OutcomeState { get; private set; } = IngestionOutcomeState.NONE;
    public long BytesUploaded { get; private set; }
    public long HashingDurationMs { get; private set; }
    public long StoragePhysicalBytes { get; private set; }
    public long StorageLogicalBytes { get; private set; }
    public bool MalwareScanReused { get; private set; }
    public bool MalwareScanRerun { get; private set; }
    public bool ParserReused { get; private set; }
    public bool OcrReused { get; private set; }
    public bool LocalModelReused { get; private set; }
    public bool ExternalModelReused { get; private set; }
    public bool ProcessingReused { get; private set; }
    public decimal LocalComputeCost { get; private set; }
    public decimal ExternalProcessingCost { get; private set; }
    public decimal TotalActualCost { get; private set; }
    public decimal EstimatedProcessingAvoided { get; private set; }
    public string CostStatus { get; private set; } = "LOCAL_COMPUTE_UNPRICED";
    public SourceDocument SourceDocument { get; private set; } = null!;
    public DocumentCorpus Corpus { get; private set; } = null!;
    public SourceDocumentOccurrence? OriginalOccurrence { get; private set; }
    public ICollection<SourceDocumentOccurrence> DuplicateOccurrences { get; } = new List<SourceDocumentOccurrence>();

    public static SourceDocumentOccurrence Create(long businessUnitId, long sourceDocumentId, long corpusId,
        string idempotencyKey, string sourceMetadataJson, long? extractionJobId = null,
        DateTimeOffset? receivedOn = null) =>
        new(businessUnitId, sourceDocumentId, corpusId, extractionJobId, idempotencyKey,
            sourceMetadataJson, receivedOn ?? DateTimeOffset.UtcNow);

    public void SetLogicalGroup(string? groupKey)
    {
        LogicalGroupKey = string.IsNullOrWhiteSpace(groupKey) ? null
            : EvidenceLedgerGuard.Required(groupKey.Trim(), 256, nameof(groupKey));
    }

    public void RecordUploadResources(long bytesUploaded, long hashingDurationMs, long physicalBytes)
    {
        BytesUploaded = EvidenceLedgerGuard.NonNegative(bytesUploaded, nameof(bytesUploaded));
        HashingDurationMs = EvidenceLedgerGuard.NonNegative(hashingDurationMs, nameof(hashingDurationMs));
        StorageLogicalBytes = bytesUploaded;
        StoragePhysicalBytes = EvidenceLedgerGuard.NonNegative(physicalBytes, nameof(physicalBytes));
        UpdatedOn = DateTimeOffset.UtcNow;
    }

    public void MarkExactDuplicateCandidate(long originalOccurrenceId, bool requiresRescan)
    {
        OriginalOccurrenceId = EvidenceLedgerGuard.Positive(originalOccurrenceId, nameof(originalOccurrenceId));
        OutcomeState = requiresRescan
            ? IngestionOutcomeState.DUPLICATE_RESCAN_REQUIRED
            : IngestionOutcomeState.EXACT_DUPLICATE_PENDING_SECURITY;
        UpdatedOn = DateTimeOffset.UtcNow;
    }

    public void RecordSecurityWork(bool reused, bool rerun)
    {
        MalwareScanReused = reused;
        MalwareScanRerun = rerun;
        UpdatedOn = DateTimeOffset.UtcNow;
    }

    public void ConfirmExactDuplicate(bool processingReused, decimal estimatedProcessingAvoided = 0m)
    {
        if (!OriginalOccurrenceId.HasValue)
            throw new InvalidOperationException("An exact duplicate must reference its original occurrence.");
        OutcomeState = IngestionOutcomeState.EXACT_DUPLICATE_CONFIRMED;
        ProcessingReused = processingReused;
        ParserReused = processingReused;
        OcrReused = processingReused;
        LocalModelReused = processingReused;
        ExternalModelReused = processingReused;
        EstimatedProcessingAvoided = NonNegativeMoney(estimatedProcessingAvoided, nameof(estimatedProcessingAvoided));
        UpdatedOn = DateTimeOffset.UtcNow;
    }

    public void ResolveByProcessingReuse(long extractionJobId)
    {
        BindExtractionJob(extractionJobId);
        IntakeStatus = IntakeOccurrenceStatus.Resolved;
        ProcessingReused = true;
        UpdatedOn = DateTimeOffset.UtcNow;
    }

    public void RecordActualCost(decimal localComputeCost, decimal externalCost, string costStatus = "RECORDED")
    {
        LocalComputeCost = NonNegativeMoney(localComputeCost, nameof(localComputeCost));
        ExternalProcessingCost = NonNegativeMoney(externalCost, nameof(externalCost));
        TotalActualCost = LocalComputeCost + ExternalProcessingCost;
        CostStatus = EvidenceLedgerGuard.Required(costStatus, 48, nameof(costStatus));
        UpdatedOn = DateTimeOffset.UtcNow;
    }

    public void MarkOutcome(IngestionOutcomeState state)
    {
        OutcomeState = state;
        UpdatedOn = DateTimeOffset.UtcNow;
    }

    public void BindExtractionJob(long extractionJobId)
    {
        var id = EvidenceLedgerGuard.Positive(extractionJobId, nameof(extractionJobId));
        if (ExtractionJobId.HasValue && ExtractionJobId != id)
            throw new InvalidOperationException("The occurrence is already bound to another extraction job.");
        ExtractionJobId = id;
        IntakeStatus = IntakeOccurrenceStatus.Queued;
        LastErrorCategory = null;
        LastErrorCode = null;
        LastErrorDetailsJson = null;
        UpdatedOn = DateTimeOffset.UtcNow;
    }

    public void MarkAwaitingSecurityScan(string errorCode, string detailsJson, DateTimeOffset? changedOn = null)
    {
        LastErrorCategory = "SecurityInspection";
        LastErrorCode = EvidenceLedgerGuard.Required(errorCode, 128, nameof(errorCode));
        LastErrorDetailsJson = EvidenceLedgerGuard.Json(detailsJson, nameof(detailsJson));
        Transition(IntakeOccurrenceStatus.AwaitingSecurityScan, changedOn,
            IntakeOccurrenceStatus.Accepted,
            IntakeOccurrenceStatus.AwaitingSecurityScan,
            IntakeOccurrenceStatus.Rejected);
        OutcomeState = IngestionOutcomeState.SECURITY_SCAN_BLOCKED;
    }

    public void MarkProcessing(DateTimeOffset? changedOn = null) => Transition(IntakeOccurrenceStatus.Processing, changedOn,
        IntakeOccurrenceStatus.Queued, IntakeOccurrenceStatus.Retryable);

    public void MarkRetryable(string errorCode, string? detailsJson = null, DateTimeOffset? changedOn = null)
    {
        LastErrorCategory = "Processing";
        LastErrorCode = EvidenceLedgerGuard.Required(errorCode, 128, nameof(errorCode));
        LastErrorDetailsJson = string.IsNullOrWhiteSpace(detailsJson) ? null
            : EvidenceLedgerGuard.Json(detailsJson, nameof(detailsJson));
        Transition(IntakeOccurrenceStatus.Retryable, changedOn, IntakeOccurrenceStatus.Processing);
    }

    public void MarkResolved(DateTimeOffset? changedOn = null) => Transition(IntakeOccurrenceStatus.Resolved, changedOn,
        IntakeOccurrenceStatus.Processing, IntakeOccurrenceStatus.Retryable);

    public void MarkReviewRequired(DateTimeOffset? changedOn = null) => Transition(IntakeOccurrenceStatus.ReviewRequired, changedOn,
        IntakeOccurrenceStatus.Processing, IntakeOccurrenceStatus.Retryable);

    public void MarkDeadLetter(string errorCode, string? detailsJson = null, DateTimeOffset? changedOn = null)
    {
        LastErrorCategory = "Processing";
        LastErrorCode = EvidenceLedgerGuard.Required(errorCode, 128, nameof(errorCode));
        LastErrorDetailsJson = string.IsNullOrWhiteSpace(detailsJson) ? null
            : EvidenceLedgerGuard.Json(detailsJson, nameof(detailsJson));
        Transition(IntakeOccurrenceStatus.DeadLetter, changedOn, IntakeOccurrenceStatus.Processing, IntakeOccurrenceStatus.Retryable);
    }

    public void MarkRejected(string category, string errorCode, string detailsJson, DateTimeOffset? changedOn = null)
    {
        LastErrorCategory = EvidenceLedgerGuard.Required(category, 64, nameof(category));
        LastErrorCode = EvidenceLedgerGuard.Required(errorCode, 128, nameof(errorCode));
        LastErrorDetailsJson = EvidenceLedgerGuard.Json(detailsJson, nameof(detailsJson));
        Transition(IntakeOccurrenceStatus.Rejected, changedOn,
            IntakeOccurrenceStatus.Accepted,
            IntakeOccurrenceStatus.AwaitingSecurityScan);
        OutcomeState = string.Equals(errorCode, "malware_detected", StringComparison.OrdinalIgnoreCase)
            ? IngestionOutcomeState.MALWARE_DETECTED
            : IngestionOutcomeState.UNSUPPORTED_FORMAT;
    }

    private static decimal NonNegativeMoney(decimal value, string name) =>
        value < 0m ? throw new ArgumentOutOfRangeException(name) : value;

    private void Transition(IntakeOccurrenceStatus next, DateTimeOffset? changedOn, params IntakeOccurrenceStatus[] allowed)
    {
        EvidenceLedgerGuard.Transition(IntakeStatus, next, allowed);
        IntakeStatus = next;
        UpdatedOn = changedOn ?? DateTimeOffset.UtcNow;
    }
}

public enum IntakeOccurrenceStatus { Accepted, AwaitingSecurityScan, Queued, Processing, Retryable, Resolved, ReviewRequired, Rejected, DeadLetter }

public sealed class ExtractionRun
{
    private ExtractionRun() { }

    private ExtractionRun(long businessUnitId, long sourceDocumentId, Guid runId, long extractionJobId,
        int attemptNumber, string parserVersion, string schemaVersion, DateTimeOffset now)
    {
        BusinessUnitId = EvidenceLedgerGuard.Positive(businessUnitId, nameof(businessUnitId));
        SourceDocumentId = EvidenceLedgerGuard.Positive(sourceDocumentId, nameof(sourceDocumentId));
        RunId = EvidenceLedgerGuard.NotEmpty(runId, nameof(runId));
        ExtractionJobId = EvidenceLedgerGuard.Positive(extractionJobId, nameof(extractionJobId));
        AttemptNumber = EvidenceLedgerGuard.Positive(attemptNumber, nameof(attemptNumber));
        ParserVersion = EvidenceLedgerGuard.Required(parserVersion, 128, nameof(parserVersion));
        SchemaVersion = EvidenceLedgerGuard.Required(schemaVersion, 128, nameof(schemaVersion));
        Status = ExtractionRunStatus.Pending;
        ProcessingPath = parserVersion.StartsWith("native-spreadsheet/", StringComparison.Ordinal)
            ? ExtractionProcessingPath.DeterministicRules
            : ExtractionProcessingPath.NativeParser;
        OcrStatus = ExtractionOcrStatus.NotRequired;
        ProcessingCostStatus = "NotSettled";
        OcrCostStatus = "NotRequired";
        CreatedOn = now;
        UpdatedOn = now;
    }

    public long Id { get; private set; }
    public long BusinessUnitId { get; private set; }
    public long SourceDocumentId { get; private set; }
    public Guid RunId { get; private set; }
    public long ExtractionJobId { get; private set; }
    public int AttemptNumber { get; private set; }
    public string ParserVersion { get; private set; } = null!;
    public string SchemaVersion { get; private set; } = null!;
    public ExtractionRunStatus Status { get; private set; }
    public DateTimeOffset? StartedOn { get; private set; }
    public DateTimeOffset? CompletedOn { get; private set; }
    public int PageCount { get; private set; }
    public int RegionCount { get; private set; }
    public int InquiryCount { get; private set; }
    public int LineItemCount { get; private set; }
    public int EvidenceCount { get; private set; }
    public int FindingCount { get; private set; }
    public string? FailureReason { get; private set; }
    public ExtractionProcessingPath ProcessingPath { get; private set; }
    public ExtractionOcrStatus OcrStatus { get; private set; }
    public int OcrPageCount { get; private set; }
    public bool OcrTruncated { get; private set; }
    public decimal? ProcessingCostAmount { get; private set; }
    public string? ProcessingCostCurrency { get; private set; }
    public string ProcessingCostStatus { get; private set; } = null!;
    public string OcrCostStatus { get; private set; } = null!;
    public DateTimeOffset CreatedOn { get; private set; }
    public DateTimeOffset UpdatedOn { get; private set; }
    public SourceDocument SourceDocument { get; private set; } = null!;
    public ICollection<FieldEvidence> Evidence { get; } = new List<FieldEvidence>();
    public ICollection<ValidationFinding> Findings { get; } = new List<ValidationFinding>();

    public static ExtractionRun Create(long businessUnitId, long sourceDocumentId, Guid runId,
        long extractionJobId, int attemptNumber, string parserVersion, string schemaVersion,
        DateTimeOffset? createdOn = null) =>
        new(businessUnitId, sourceDocumentId, runId, extractionJobId, attemptNumber, parserVersion,
            schemaVersion, createdOn ?? DateTimeOffset.UtcNow);

    public void Start(DateTimeOffset? startedOn = null)
    {
        EvidenceLedgerGuard.Transition(Status, ExtractionRunStatus.Processing, ExtractionRunStatus.Pending);
        Status = ExtractionRunStatus.Processing;
        StartedOn = startedOn ?? DateTimeOffset.UtcNow;
        UpdatedOn = StartedOn.Value;
    }

    public void RecordCostStatus(string processingStatus, string ocrStatus,
        decimal? amount = null, string? currency = null)
    {
        ProcessingCostStatus = EvidenceLedgerGuard.Required(processingStatus, 32, nameof(processingStatus));
        OcrCostStatus = EvidenceLedgerGuard.Required(ocrStatus, 32, nameof(ocrStatus));
        ProcessingCostAmount = amount;
        ProcessingCostCurrency = string.IsNullOrWhiteSpace(currency) ? null
            : EvidenceLedgerGuard.Required(currency.Trim().ToUpperInvariant(), 3, nameof(currency));
        if (amount.HasValue && amount.Value < 0)
            throw new ArgumentOutOfRangeException(nameof(amount));
        if (amount.HasValue != (ProcessingCostCurrency is not null))
            throw new ArgumentException("Cost amount and currency must be supplied together.");
    }

    public void RecordProcessingEvidence(ExtractionProcessingPath processingPath,
        ExtractionOcrStatus ocrStatus, int ocrPageCount, bool ocrTruncated)
    {
        if (ocrPageCount < 0)
            throw new ArgumentOutOfRangeException(nameof(ocrPageCount));
        if (ocrStatus == ExtractionOcrStatus.NotRequired && (ocrPageCount != 0 || ocrTruncated))
            throw new ArgumentException("OCR evidence cannot contain pages or truncation when OCR was not required.");

        ProcessingPath = processingPath;
        OcrStatus = ocrStatus;
        OcrPageCount = ocrPageCount;
        OcrTruncated = ocrTruncated;
        UpdatedOn = DateTimeOffset.UtcNow;
    }

    public void Complete(int pageCount, int regionCount, int inquiryCount, int lineItemCount,
        int evidenceCount, int findingCount, DateTimeOffset? completedOn = null)
    {
        EvidenceLedgerGuard.Transition(Status, ExtractionRunStatus.Completed, ExtractionRunStatus.Processing);
        SetCounts(pageCount, regionCount, inquiryCount, lineItemCount, evidenceCount, findingCount);
        Status = ExtractionRunStatus.Completed;
        CompletedOn = completedOn ?? DateTimeOffset.UtcNow;
        UpdatedOn = CompletedOn.Value;
    }

    public void Fail(string failureReason, int pageCount = 0, int regionCount = 0, int inquiryCount = 0,
        int lineItemCount = 0, int evidenceCount = 0, int findingCount = 0,
        DateTimeOffset? completedOn = null)
    {
        EvidenceLedgerGuard.Transition(Status, ExtractionRunStatus.Failed,
            ExtractionRunStatus.Pending, ExtractionRunStatus.Processing);
        FailureReason = EvidenceLedgerGuard.Required(failureReason, 4_000, nameof(failureReason));
        SetCounts(pageCount, regionCount, inquiryCount, lineItemCount, evidenceCount, findingCount);
        Status = ExtractionRunStatus.Failed;
        CompletedOn = completedOn ?? DateTimeOffset.UtcNow;
        UpdatedOn = CompletedOn.Value;
    }

    private void SetCounts(int pageCount, int regionCount, int inquiryCount, int lineItemCount,
        int evidenceCount, int findingCount)
    {
        PageCount = EvidenceLedgerGuard.NonNegative(pageCount, nameof(pageCount));
        RegionCount = EvidenceLedgerGuard.NonNegative(regionCount, nameof(regionCount));
        InquiryCount = EvidenceLedgerGuard.NonNegative(inquiryCount, nameof(inquiryCount));
        LineItemCount = EvidenceLedgerGuard.NonNegative(lineItemCount, nameof(lineItemCount));
        EvidenceCount = EvidenceLedgerGuard.NonNegative(evidenceCount, nameof(evidenceCount));
        FindingCount = EvidenceLedgerGuard.NonNegative(findingCount, nameof(findingCount));
    }
}

public sealed class DocumentPage
{
    private DocumentPage() { }

    private DocumentPage(long businessUnitId, long documentId, int pageNumber, decimal width,
        decimal height, int rotation, string? textHash, DocumentPageKind pageKind, string? sheetName,
        DateTimeOffset now)
    {
        BusinessUnitId = EvidenceLedgerGuard.Positive(businessUnitId, nameof(businessUnitId));
        DocumentId = EvidenceLedgerGuard.Positive(documentId, nameof(documentId));
        PageNumber = EvidenceLedgerGuard.Positive(pageNumber, nameof(pageNumber));
        Width = EvidenceLedgerGuard.Positive(width, nameof(width));
        Height = EvidenceLedgerGuard.Positive(height, nameof(height));
        Rotation = EvidenceLedgerGuard.Rotation(rotation, nameof(rotation));
        TextHash = textHash is null ? null : EvidenceLedgerGuard.Sha256(textHash, nameof(textHash));
        PageKind = pageKind;
        SheetName = EvidenceLedgerGuard.Optional(sheetName, 256, nameof(sheetName));
        if (pageKind != DocumentPageKind.PhysicalPage && string.IsNullOrWhiteSpace(SheetName))
            throw new ArgumentException("A sheet name is required for worksheet and CSV pages.", nameof(sheetName));
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
    public DocumentPageKind PageKind { get; private set; }
    public string? SheetName { get; private set; }
    public OcrStatus OcrStatus { get; private set; }
    public decimal? OcrConfidence { get; private set; }
    public DateTimeOffset CreatedOn { get; private set; }
    public DateTimeOffset UpdatedOn { get; private set; }
    public SourceDocument Document { get; private set; } = null!;
    public ICollection<DocumentRegion> Regions { get; } = new List<DocumentRegion>();

    public static DocumentPage Create(long businessUnitId, long documentId, int pageNumber, decimal width,
        decimal height, int rotation = 0, string? textHash = null,
        DocumentPageKind pageKind = DocumentPageKind.PhysicalPage, string? sheetName = null,
        DateTimeOffset? createdOn = null) =>
        new(businessUnitId, documentId, pageNumber, width, height, rotation, textHash, pageKind, sheetName,
            createdOn ?? DateTimeOffset.UtcNow);

    public void MarkOcrNotRequired(DateTimeOffset? changedOn = null)
    {
        EvidenceLedgerGuard.Transition(OcrStatus, OcrStatus.NotRequired, OcrStatus.Pending);
        OcrStatus = OcrStatus.NotRequired;
        UpdatedOn = changedOn ?? DateTimeOffset.UtcNow;
    }

    public void StartOcr(DateTimeOffset? changedOn = null)
    {
        EvidenceLedgerGuard.Transition(OcrStatus, OcrStatus.Processing, OcrStatus.Pending);
        OcrStatus = OcrStatus.Processing;
        UpdatedOn = changedOn ?? DateTimeOffset.UtcNow;
    }

    public void CompleteOcr(decimal confidence, DateTimeOffset? changedOn = null)
    {
        EvidenceLedgerGuard.Transition(OcrStatus, OcrStatus.Completed, OcrStatus.Processing);
        OcrConfidence = EvidenceLedgerGuard.Confidence(confidence, nameof(confidence));
        OcrStatus = OcrStatus.Completed;
        UpdatedOn = changedOn ?? DateTimeOffset.UtcNow;
    }

    public void FailOcr(DateTimeOffset? changedOn = null)
    {
        EvidenceLedgerGuard.Transition(OcrStatus, OcrStatus.Failed, OcrStatus.Pending, OcrStatus.Processing);
        OcrStatus = OcrStatus.Failed;
        UpdatedOn = changedOn ?? DateTimeOffset.UtcNow;
    }
}

public sealed class DocumentRegion
{
    private DocumentRegion() { }

    private DocumentRegion(long businessUnitId, long pageId, DocumentRegionType regionType,
        decimal x, decimal y, decimal width, decimal height, string? text, decimal confidence,
        string? sourceAddress, int? rowNumber, int? columnNumber, DateTimeOffset now)
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
        SourceAddress = EvidenceLedgerGuard.Optional(sourceAddress, 256, nameof(sourceAddress));
        RowNumber = rowNumber is null ? null : EvidenceLedgerGuard.Positive(rowNumber.Value, nameof(rowNumber));
        ColumnNumber = columnNumber is null ? null : EvidenceLedgerGuard.Positive(columnNumber.Value, nameof(columnNumber));
        if ((RowNumber.HasValue || ColumnNumber.HasValue) && string.IsNullOrWhiteSpace(SourceAddress))
            throw new ArgumentException("A source address is required when row or column coordinates are supplied.",
                nameof(sourceAddress));
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
    public string? SourceAddress { get; private set; }
    public int? RowNumber { get; private set; }
    public int? ColumnNumber { get; private set; }
    public DateTimeOffset CreatedOn { get; private set; }
    public DocumentPage Page { get; private set; } = null!;
    public ICollection<FieldEvidence> Evidence { get; } = new List<FieldEvidence>();

    public static DocumentRegion Create(long businessUnitId, long pageId, DocumentRegionType regionType,
        decimal x, decimal y, decimal width, decimal height, string? text, decimal confidence,
        DateTimeOffset? createdOn = null, string? sourceAddress = null, int? rowNumber = null,
        int? columnNumber = null) =>
        new(businessUnitId, pageId, regionType, x, y, width, height, text, confidence,
            sourceAddress, rowNumber, columnNumber,
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
    public DateTimeOffset? ReceivedDate { get; private set; }
    public DateTimeOffset? BidClosingDate { get; private set; }
    public CanonicalInquiryStatus Status { get; private set; }
    public DateTimeOffset CreatedOn { get; private set; }
    public DateTimeOffset UpdatedOn { get; private set; }
    public DocumentCorpus Corpus { get; private set; } = null!;
    public ICollection<CanonicalLineItem> LineItems { get; } = new List<CanonicalLineItem>();
    public ICollection<FieldEvidence> Evidence { get; } = new List<FieldEvidence>();

    public static CanonicalInquiry Create(long businessUnitId, long corpusId, int inquiryNumber,
        DateTimeOffset? createdOn = null) =>
        new(businessUnitId, corpusId, inquiryNumber, createdOn ?? DateTimeOffset.UtcNow);

    public void PopulateHeader(string? customerRfqNumber, string? buyerName, DateTimeOffset? receivedDate,
        DateTimeOffset? bidClosingDate, DateTimeOffset? changedOn = null)
    {
        if (Status is CanonicalInquiryStatus.Validated or CanonicalInquiryStatus.Rejected)
            throw new InvalidOperationException($"A {Status} inquiry cannot be changed.");
        if (receivedDate.HasValue && bidClosingDate.HasValue && bidClosingDate < receivedDate)
            throw new ArgumentException("Bid closing date cannot precede the received date.", nameof(bidClosingDate));
        var validatedRfqNumber = EvidenceLedgerGuard.Optional(customerRfqNumber, 256, nameof(customerRfqNumber));
        var validatedBuyerName = EvidenceLedgerGuard.Optional(buyerName, 512, nameof(buyerName));
        CustomerRfqNumber = validatedRfqNumber;
        BuyerName = validatedBuyerName;
        ReceivedDate = receivedDate;
        BidClosingDate = bidClosingDate;
        UpdatedOn = changedOn ?? DateTimeOffset.UtcNow;
    }

    public void BindLead(long leadId, DateTimeOffset? changedOn = null)
    {
        var id = EvidenceLedgerGuard.Positive(leadId, nameof(leadId));
        if (LeadId.HasValue && LeadId != id)
            throw new InvalidOperationException("The inquiry is already bound to another lead.");
        LeadId = id;
        UpdatedOn = changedOn ?? DateTimeOffset.UtcNow;
    }

    public void RequireReview(DateTimeOffset? changedOn = null) =>
        TransitionTo(CanonicalInquiryStatus.ReviewRequired, changedOn ?? DateTimeOffset.UtcNow,
            CanonicalInquiryStatus.Draft);

    public void Validate(DateTimeOffset? changedOn = null) =>
        TransitionTo(CanonicalInquiryStatus.Validated, changedOn ?? DateTimeOffset.UtcNow,
            CanonicalInquiryStatus.Draft, CanonicalInquiryStatus.ReviewRequired);

    public void Reject(DateTimeOffset? changedOn = null) =>
        TransitionTo(CanonicalInquiryStatus.Rejected, changedOn ?? DateTimeOffset.UtcNow,
            CanonicalInquiryStatus.Draft, CanonicalInquiryStatus.ReviewRequired);

    private void TransitionTo(CanonicalInquiryStatus next, DateTimeOffset changedOn,
        params CanonicalInquiryStatus[] allowed)
    {
        EvidenceLedgerGuard.Transition(Status, next, allowed);
        Status = next;
        UpdatedOn = changedOn;
    }
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
        ValidationStatus = CanonicalValidationStatus.Unvalidated;
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
    public decimal? UnitPrice { get; private set; }
    public int? LeadTimeDays { get; private set; }
    public long? LeadItemId { get; private set; }
    public CanonicalValidationStatus ValidationStatus { get; private set; }
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

    public void Enrich(string? manufacturer, string? manufacturerPartNumber, string? currencyCode,
        decimal? unitPrice, int? leadTimeDays, string? rawPayload,
        CanonicalValidationStatus validationStatus, DateTimeOffset? changedOn = null)
    {
        var validatedManufacturer = EvidenceLedgerGuard.Optional(manufacturer, 512, nameof(manufacturer));
        var validatedPartNumber = EvidenceLedgerGuard.Optional(manufacturerPartNumber, 512,
            nameof(manufacturerPartNumber));
        var validatedCurrencyCode = EvidenceLedgerGuard.CurrencyCode(currencyCode, nameof(currencyCode));
        decimal? validatedUnitPrice = unitPrice is null
            ? null
            : EvidenceLedgerGuard.NonNegative(unitPrice.Value, nameof(unitPrice));
        int? validatedLeadTimeDays = leadTimeDays is null
            ? null
            : EvidenceLedgerGuard.NonNegative(leadTimeDays.Value, nameof(leadTimeDays));
        var validatedRawPayload = EvidenceLedgerGuard.OptionalJson(rawPayload, nameof(rawPayload));
        Manufacturer = validatedManufacturer;
        ManufacturerPartNumber = validatedPartNumber;
        CurrencyCode = validatedCurrencyCode;
        UnitPrice = validatedUnitPrice;
        LeadTimeDays = validatedLeadTimeDays;
        RawPayload = validatedRawPayload;
        ValidationStatus = validationStatus;
        UpdatedOn = changedOn ?? DateTimeOffset.UtcNow;
    }

    public void SetValidationStatus(CanonicalValidationStatus status, DateTimeOffset? changedOn = null)
    {
        ValidationStatus = status;
        UpdatedOn = changedOn ?? DateTimeOffset.UtcNow;
    }

    public void BindLeadItem(long leadItemId, DateTimeOffset? changedOn = null)
    {
        var id = EvidenceLedgerGuard.Positive(leadItemId, nameof(leadItemId));
        if (LeadItemId.HasValue && LeadItemId != id)
            throw new InvalidOperationException("The canonical line is already bound to another lead item.");
        LeadItemId = id;
        UpdatedOn = changedOn ?? DateTimeOffset.UtcNow;
    }
}

public sealed class ValidationFinding
{
    private ValidationFinding() { }

    private ValidationFinding(long businessUnitId, long extractionRunId, long? inquiryId,
        long? lineItemId, long? regionId, string code, ValidationSeverity severity, string message,
        DateTimeOffset createdOn)
    {
        if (inquiryId.HasValue && lineItemId.HasValue)
            throw new ArgumentException("A finding cannot target both an inquiry and a line item.");
        BusinessUnitId = EvidenceLedgerGuard.Positive(businessUnitId, nameof(businessUnitId));
        ExtractionRunId = EvidenceLedgerGuard.Positive(extractionRunId, nameof(extractionRunId));
        InquiryId = inquiryId is null ? null : EvidenceLedgerGuard.Positive(inquiryId.Value, nameof(inquiryId));
        LineItemId = lineItemId is null ? null : EvidenceLedgerGuard.Positive(lineItemId.Value, nameof(lineItemId));
        RegionId = regionId is null ? null : EvidenceLedgerGuard.Positive(regionId.Value, nameof(regionId));
        Code = EvidenceLedgerGuard.Required(code, 128, nameof(code));
        Severity = severity;
        Message = EvidenceLedgerGuard.Required(message, 4_000, nameof(message));
        CreatedOn = createdOn;
    }

    public long Id { get; private set; }
    public long BusinessUnitId { get; private set; }
    public long ExtractionRunId { get; private set; }
    public long? InquiryId { get; private set; }
    public long? LineItemId { get; private set; }
    public long? RegionId { get; private set; }
    public string Code { get; private set; } = null!;
    public ValidationSeverity Severity { get; private set; }
    public string Message { get; private set; } = null!;
    public DateTimeOffset CreatedOn { get; private set; }
    public ExtractionRun ExtractionRun { get; private set; } = null!;
    public CanonicalInquiry? Inquiry { get; private set; }
    public CanonicalLineItem? LineItem { get; private set; }
    public DocumentRegion? Region { get; private set; }

    public static ValidationFinding ForRun(long businessUnitId, long extractionRunId, string code,
        ValidationSeverity severity, string message, long? regionId = null,
        DateTimeOffset? createdOn = null) =>
        new(businessUnitId, extractionRunId, null, null, regionId, code, severity, message,
            createdOn ?? DateTimeOffset.UtcNow);

    public static ValidationFinding ForInquiry(long businessUnitId, long extractionRunId, long inquiryId,
        string code, ValidationSeverity severity, string message, long? regionId = null,
        DateTimeOffset? createdOn = null) =>
        new(businessUnitId, extractionRunId, inquiryId, null, regionId, code, severity, message,
            createdOn ?? DateTimeOffset.UtcNow);

    public static ValidationFinding ForLineItem(long businessUnitId, long extractionRunId, long lineItemId,
        string code, ValidationSeverity severity, string message, long? regionId = null,
        DateTimeOffset? createdOn = null) =>
        new(businessUnitId, extractionRunId, null, lineItemId, regionId, code, severity, message,
            createdOn ?? DateTimeOffset.UtcNow);
}

public sealed class FieldEvidence
{
    private FieldEvidence() { }

    private FieldEvidence(long businessUnitId, long regionId, long? inquiryId, long? lineItemId,
        string fieldName, string? rawValue, string? normalizedValue, decimal confidence,
        string extractor, Guid runId, FieldValueKind valueKind, FieldValidationStatus validationStatus,
        string transformationsJson, DateTimeOffset now)
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
        ValueKind = valueKind;
        ValidationStatus = validationStatus;
        TransformationsJson = EvidenceLedgerGuard.Json(transformationsJson, nameof(transformationsJson));
        EvidenceKey = CreateEvidenceKey(businessUnitId, regionId, inquiryId, lineItemId, FieldName, runId);
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
    public string EvidenceKey { get; private set; } = null!;
    public FieldValueKind ValueKind { get; private set; }
    public FieldValidationStatus ValidationStatus { get; private set; }
    public string TransformationsJson { get; private set; } = null!;
    public DateTimeOffset CreatedOn { get; private set; }
    public DocumentRegion Region { get; private set; } = null!;
    public CanonicalInquiry? Inquiry { get; private set; }
    public CanonicalLineItem? LineItem { get; private set; }
    public ExtractionRun ExtractionRun { get; private set; } = null!;

    public static FieldEvidence ForInquiry(long businessUnitId, long regionId, long inquiryId,
        string fieldName, string? rawValue, string? normalizedValue, decimal confidence,
        string extractor, Guid runId, DateTimeOffset? createdOn = null,
        FieldValueKind valueKind = FieldValueKind.Text,
        FieldValidationStatus validationStatus = FieldValidationStatus.Unvalidated,
        string transformationsJson = "[]") =>
        new(businessUnitId, regionId, inquiryId, null, fieldName, rawValue, normalizedValue,
            confidence, extractor, runId, valueKind, validationStatus, transformationsJson,
            createdOn ?? DateTimeOffset.UtcNow);

    public static FieldEvidence ForLineItem(long businessUnitId, long regionId, long lineItemId,
        string fieldName, string? rawValue, string? normalizedValue, decimal confidence,
        string extractor, Guid runId, DateTimeOffset? createdOn = null,
        FieldValueKind valueKind = FieldValueKind.Text,
        FieldValidationStatus validationStatus = FieldValidationStatus.Unvalidated,
        string transformationsJson = "[]") =>
        new(businessUnitId, regionId, null, lineItemId, fieldName, rawValue, normalizedValue,
            confidence, extractor, runId, valueKind, validationStatus, transformationsJson,
            createdOn ?? DateTimeOffset.UtcNow);

    private static string CreateEvidenceKey(long businessUnitId, long regionId, long? inquiryId,
        long? lineItemId, string fieldName, Guid runId)
    {
        var target = inquiryId.HasValue ? $"inquiry:{inquiryId.Value}" : $"line:{lineItemId!.Value}";
        var canonical = $"{businessUnitId}|{runId:D}|{regionId}|{target}|{fieldName.Trim().ToUpperInvariant()}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
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

    public static int NonNegative(int value, string name) => value >= 0
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

    public static string Json(string value, string name)
    {
        Required(value, 1_000_000, name);
        try
        {
            using var _ = System.Text.Json.JsonDocument.Parse(value);
        }
        catch (System.Text.Json.JsonException exception)
        {
            throw new ArgumentException("Value must contain valid JSON.", name, exception);
        }
        return value;
    }

    public static string? OptionalJson(string? value, string name) =>
        value is null ? null : Json(value, name);

    public static string? CurrencyCode(string? value, string name)
    {
        if (value is null)
            return null;
        if (value.Length != 3 || value.Any(c => !char.IsAsciiLetterUpper(c)))
            throw new ArgumentException("Currency code must contain three upper-case ASCII letters.", name);
        return value;
    }

    public static void Transition<TStatus>(TStatus current, TStatus next, params TStatus[] allowed)
        where TStatus : struct, Enum
    {
        if (!allowed.Contains(current))
            throw new InvalidOperationException($"Status cannot transition from {current} to {next}.");
    }

    public static string Sha256(string value, string name)
    {
        Required(value, 64, name);
        if (value.Length != 64 || value.Any(c => !char.IsAsciiHexDigit(c) || char.IsUpper(c)))
            throw new ArgumentException("Value must be a lower-case SHA-256 hex digest.", name);
        return value;
    }
}
