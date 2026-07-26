using System.Text.Json;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;

namespace ERP_RFQ_Automation.CommercialDocuments;

public enum CommercialDocumentType
{
    CustomerRfq,
    CustomerRfqRevision,
    SupplierQuote,
    SupplierQuoteRevision,
    CustomerQuoteResponse,
    CustomerOrder,
    SupplierConfirmation,
    ReceiptOrDeliveryDocument,
    CustomerRejection,
    PurchaseOrder,
    SupplierInvoice,
    InventoryFile,
    Unknown
}

public enum CommercialDocumentReviewStatus
{
    AutoClassified,
    ReviewRequired,
    Confirmed,
    Rejected
}

public static class CommercialDocumentClassificationMethods
{
    public const string LocalDeterministicV1 = "LocalDeterministic/v1";
    public const string HumanReview = "HumanReview";
}

public sealed class CommercialDocumentClassification
{
    private CommercialDocumentClassification() { }

    private CommercialDocumentClassification(long businessUnitId, long sourceDocumentId,
        string sourceDocumentContentHash, string sourceObjectVersion, string idempotencyKey,
        string requestHash, CommercialDocumentDecision decision, CommercialDocumentMatchReferences matches,
        DateTimeOffset now)
    {
        Id = Guid.NewGuid();
        BusinessUnitId = Guard.Positive(businessUnitId, nameof(businessUnitId));
        SourceDocumentId = Guard.Positive(sourceDocumentId, nameof(sourceDocumentId));
        SourceDocumentContentHash = Guard.Hash(sourceDocumentContentHash, nameof(sourceDocumentContentHash));
        SourceObjectVersion = Guard.Required(sourceObjectVersion, 255, nameof(sourceObjectVersion));
        IdempotencyKey = Guard.Required(idempotencyKey, 256, nameof(idempotencyKey));
        RequestHash = Guard.Hash(requestHash, nameof(requestHash));
        DocumentType = decision.DocumentType;
        ReviewStatus = decision.RequiresReview
            ? CommercialDocumentReviewStatus.ReviewRequired
            : CommercialDocumentReviewStatus.AutoClassified;
        Confidence = Guard.Confidence(decision.Confidence);
        ClassificationMethod = Guard.Required(decision.Method, 128, nameof(decision.Method));
        EvidenceJson = Guard.Json(decision.EvidenceJson, nameof(decision.EvidenceJson));
        ApplyMatches(matches);
        Version = 1;
        CreatedOn = now;
        UpdatedOn = now;
    }

    public Guid Id { get; private set; }
    public long BusinessUnitId { get; private set; }
    public long SourceDocumentId { get; private set; }
    public string SourceDocumentContentHash { get; private set; } = null!;
    public string SourceObjectVersion { get; private set; } = null!;
    public CommercialDocumentType DocumentType { get; private set; }
    public CommercialDocumentReviewStatus ReviewStatus { get; private set; }
    public decimal Confidence { get; private set; }
    public string ClassificationMethod { get; private set; } = null!;
    public string EvidenceJson { get; private set; } = null!;
    public string IdempotencyKey { get; private set; } = null!;
    public string RequestHash { get; private set; } = null!;
    public long? CustomerRfqId { get; private set; }
    public long? SupplierRfqId { get; private set; }
    public long? SourcingCaseId { get; private set; }
    public long? SupplierQuoteId { get; private set; }
    public long? PurchaseOrderId { get; private set; }
    public long? SupplierInvoiceId { get; private set; }
    public int Version { get; private set; }
    public string? ReviewedBy { get; private set; }
    public string? ReviewReason { get; private set; }
    public DateTimeOffset? ReviewedOn { get; private set; }
    public DateTimeOffset CreatedOn { get; private set; }
    public DateTimeOffset UpdatedOn { get; private set; }
    public SourceDocument SourceDocument { get; private set; } = null!;

    public static CommercialDocumentClassification Create(long businessUnitId, long sourceDocumentId,
        string sourceDocumentContentHash, string sourceObjectVersion, string idempotencyKey,
        string requestHash, CommercialDocumentDecision decision, CommercialDocumentMatchReferences? matches = null,
        DateTimeOffset? createdOn = null) =>
        new(businessUnitId, sourceDocumentId, sourceDocumentContentHash, sourceObjectVersion,
            idempotencyKey, requestHash, decision, matches ?? new(), createdOn ?? DateTimeOffset.UtcNow);

    public void Confirm(int expectedVersion, CommercialDocumentType documentType, string evidenceJson,
        string actor, string reason, CommercialDocumentMatchReferences? matches = null,
        DateTimeOffset? reviewedOn = null)
    {
        EnsureVersion(expectedVersion);
        if (documentType == CommercialDocumentType.Unknown)
            throw new ArgumentException("A confirmed classification must select a known commercial document type.",
                nameof(documentType));
        if (ReviewStatus is CommercialDocumentReviewStatus.Confirmed or CommercialDocumentReviewStatus.Rejected)
            throw new InvalidOperationException("The classification review is already final.");

        DocumentType = documentType;
        ReviewStatus = CommercialDocumentReviewStatus.Confirmed;
        Confidence = 1m;
        ClassificationMethod = CommercialDocumentClassificationMethods.HumanReview;
        EvidenceJson = Guard.Json(evidenceJson, nameof(evidenceJson));
        ReviewedBy = Guard.Required(actor, 255, nameof(actor));
        ReviewReason = Guard.Required(reason, 1_000, nameof(reason));
        ApplyMatches(matches ?? new());
        ReviewedOn = reviewedOn ?? DateTimeOffset.UtcNow;
        UpdatedOn = ReviewedOn.Value;
        Version++;
    }

    public void Reject(int expectedVersion, string actor, string reason, DateTimeOffset? reviewedOn = null)
    {
        EnsureVersion(expectedVersion);
        if (ReviewStatus is CommercialDocumentReviewStatus.Confirmed or CommercialDocumentReviewStatus.Rejected)
            throw new InvalidOperationException("The classification review is already final.");

        DocumentType = CommercialDocumentType.Unknown;
        ReviewStatus = CommercialDocumentReviewStatus.Rejected;
        Confidence = 0m;
        ClassificationMethod = CommercialDocumentClassificationMethods.HumanReview;
        ReviewedBy = Guard.Required(actor, 255, nameof(actor));
        ReviewReason = Guard.Required(reason, 1_000, nameof(reason));
        ReviewedOn = reviewedOn ?? DateTimeOffset.UtcNow;
        UpdatedOn = ReviewedOn.Value;
        Version++;
    }

    public void LinkSupplierQuote(int expectedVersion, long supplierQuoteId,
        DateTimeOffset? linkedOn = null)
    {
        EnsureVersion(expectedVersion);
        if (DocumentType is not (CommercialDocumentType.SupplierQuote or CommercialDocumentType.SupplierQuoteRevision))
            throw new InvalidOperationException("Only a Supplier Quote classification can link a Supplier Quote.");
        var verifiedId = Guard.Positive(supplierQuoteId, nameof(supplierQuoteId));
        if (SupplierQuoteId.HasValue && SupplierQuoteId.Value != verifiedId)
            throw new CommercialDocumentConflictException(
                "The commercial document is already linked to another Supplier Quote.");
        if (SupplierQuoteId == verifiedId) return;
        SupplierQuoteId = verifiedId;
        UpdatedOn = linkedOn ?? DateTimeOffset.UtcNow;
        Version++;
    }

    private void ApplyMatches(CommercialDocumentMatchReferences matches)
    {
        CustomerRfqId = Guard.OptionalPositive(matches.CustomerRfqId, nameof(matches.CustomerRfqId));
        SupplierRfqId = Guard.OptionalPositive(matches.SupplierRfqId, nameof(matches.SupplierRfqId));
        SourcingCaseId = Guard.OptionalPositive(matches.SourcingCaseId, nameof(matches.SourcingCaseId));
        SupplierQuoteId = Guard.OptionalPositive(matches.SupplierQuoteId, nameof(matches.SupplierQuoteId));
        PurchaseOrderId = Guard.OptionalPositive(matches.PurchaseOrderId, nameof(matches.PurchaseOrderId));
        SupplierInvoiceId = Guard.OptionalPositive(matches.SupplierInvoiceId, nameof(matches.SupplierInvoiceId));
    }

    private void EnsureVersion(int expectedVersion)
    {
        if (expectedVersion != Version)
            throw new CommercialDocumentConflictException("The classification changed after it was loaded.");
    }

    private static class Guard
    {
        public static long Positive(long value, string name) => value > 0
            ? value
            : throw new ArgumentOutOfRangeException(name, "Value must be positive.");

        public static long? OptionalPositive(long? value, string name) => value is null
            ? null
            : Positive(value.Value, name);

        public static string Required(string? value, int maxLength, string name)
        {
            var normalized = value?.Trim();
            if (string.IsNullOrEmpty(normalized) || normalized.Length > maxLength)
                throw new ArgumentException($"Value is required and must not exceed {maxLength} characters.", name);
            return normalized;
        }

        public static string Hash(string value, string name)
        {
            var normalized = Required(value, 64, name).ToLowerInvariant();
            if (normalized.Length != 64 || normalized.Any(c => !Uri.IsHexDigit(c)))
                throw new ArgumentException("Value must be a SHA-256 hexadecimal hash.", name);
            return normalized;
        }

        public static decimal Confidence(decimal value) => value is >= 0m and <= 1m
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), "Confidence must be between zero and one.");

        public static string Json(string value, string name)
        {
            try { using var _ = JsonDocument.Parse(value); }
            catch (JsonException exception) { throw new ArgumentException("Value must be valid JSON.", name, exception); }
            return value;
        }
    }
}

public sealed class CommercialDocumentConflictException(string message) : InvalidOperationException(message);
public sealed class CommercialDocumentNotFoundException(string message) : InvalidOperationException(message);
public sealed class CommercialDocumentMatchValidationException(string message) : ArgumentException(message);
