using System.Security.Cryptography;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Security.DocumentInspection;
using ERP_RFQ_Automation.Services.DocumentIntelligence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ERP_RFQ_Automation.OrderToCash.PurchaseOrderIntake;

public interface ICustomerPurchaseOrderDocumentService
{
    Task<CustomerPurchaseOrderDocumentView> ExtractAsync(
        long businessUnitId,
        string fileName,
        byte[] bytes,
        string? declaredContentType,
        string actor,
        CancellationToken cancellationToken = default);

    Task<CustomerPurchaseOrderView> CreateFromDocumentAsync(
        long businessUnitId,
        string idempotencyKey,
        string correlationId,
        CreateCustomerPurchaseOrderFromDocumentCommand command,
        string actor,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// FR-COM-01. The governed door for a customer purchase-order document.
///
/// <para><b>Governance is not re-implemented here.</b> The bytes go through the same
/// <see cref="IFileInspectionService"/> every other upload door uses — bounded size, magic-byte
/// typing against the declared extension, macro refusal, malware scan — and are then written to
/// the same immutable, content-addressed <see cref="IEvidenceObjectStorage"/> the extraction
/// evidence ledger writes to. Reading is done by the shared
/// <see cref="IExtractionDocumentReader"/>, so a scanned PO reaches Tesseract through the same
/// OCR entitlement check as every other scan.</para>
///
/// <para><b>Why it is not <see cref="IDocumentIngestion"/>.</b> That gateway's contract is
/// "one document -> one job -> one Lead": it enqueues an extraction job whose success condition
/// is a new Lead/RFQ. A customer PO is the buyer's answer to a quotation we already sent; putting
/// it through that door would manufacture a phantom inbound enquiry for every award. The
/// governance primitives that door composes are reused; its lead-producing side effect is not.
/// The consequence — no queue row, so no automatic retry — is deliberate: this door is
/// interactive, and the user is shown the failure and re-uploads.</para>
/// </summary>
public sealed class CustomerPurchaseOrderDocumentService : ICustomerPurchaseOrderDocumentService
{
    /// <summary>
    /// The parent an uploaded PO document is filed under before the purchase order exists. The
    /// row is re-parented to the purchase order the moment one is created from it, so an
    /// abandoned upload is distinguishable from a committed one.
    /// </summary>
    public const string PendingParentType = "CustomerPoDocument";

    /// <summary>The parent of a PO document that has been committed to a purchase order.</summary>
    public const string PurchaseOrderParentType = "CustomerPurchaseOrder";

    /// <summary>Matches <see cref="DocumentFileInspectionService.DefaultMaximumFileBytes"/>.</summary>
    public const long MaximumDocumentBytes = 25L * 1024 * 1024;

    private readonly ErpRfqAutomationContext _db;
    private readonly IFileInspectionService _inspection;
    private readonly IEvidenceObjectStorage _storage;
    private readonly IExtractionDocumentReader _reader;
    private readonly ICustomerAwardApplicationService _awards;
    private readonly CustomerPurchaseOrderDocumentExtractor _extractor;
    private readonly ILogger<CustomerPurchaseOrderDocumentService> _log;

    public CustomerPurchaseOrderDocumentService(
        ErpRfqAutomationContext db,
        IFileInspectionService inspection,
        IEvidenceObjectStorage storage,
        IExtractionDocumentReader reader,
        ICustomerAwardApplicationService awards,
        ILogger<CustomerPurchaseOrderDocumentService> log)
        : this(db, inspection, storage, reader, awards,
            new CustomerPurchaseOrderDocumentExtractor(new NativeSpreadsheetParser()), log)
    {
    }

    public CustomerPurchaseOrderDocumentService(
        ErpRfqAutomationContext db,
        IFileInspectionService inspection,
        IEvidenceObjectStorage storage,
        IExtractionDocumentReader reader,
        ICustomerAwardApplicationService awards,
        CustomerPurchaseOrderDocumentExtractor extractor,
        ILogger<CustomerPurchaseOrderDocumentService> log)
    {
        _db = db;
        _inspection = inspection;
        _storage = storage;
        _reader = reader;
        _awards = awards;
        _extractor = extractor;
        _log = log;
    }

    public async Task<CustomerPurchaseOrderDocumentView> ExtractAsync(
        long businessUnitId,
        string fileName,
        byte[] bytes,
        string? declaredContentType,
        string actor,
        CancellationToken cancellationToken = default)
    {
        if (businessUnitId <= 0)
            throw new ArgumentException("A valid tenant claim is required.", nameof(businessUnitId));
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("The uploaded file must have a name.", nameof(fileName));
        if (bytes is null || bytes.Length == 0)
            throw new ArgumentException("The uploaded purchase-order document is empty.", nameof(bytes));
        if (bytes.LongLength > MaximumDocumentBytes)
            throw new ArgumentException("The purchase-order document exceeds the 25 MB limit.", nameof(bytes));

        var safeName = Path.GetFileName(fileName.Trim());
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        FileInspectionResult inspection;
        await using (var content = new MemoryStream(bytes, writable: false))
        {
            inspection = await _inspection.InspectAsync(
                new FileInspectionRequest(content, safeName, declaredContentType, bytes.LongLength),
                cancellationToken);
        }

        if (!inspection.IsCleared)
        {
            _log.LogWarning(
                "Customer PO document {FileName} for tenant {BusinessUnitId} was stopped by inspection: {Status} {Code}.",
                safeName, businessUnitId, inspection.Status, inspection.ErrorCode);
            throw new DocumentInspectionException(inspection);
        }

        var extension = Path.GetExtension(safeName).TrimStart('.').ToLowerInvariant();
        var stored = await _storage.WriteImmutableAsync(
            businessUnitId, "cleared", hash, Path.GetExtension(safeName), bytes, cancellationToken);

        var job = new ExtractionJob
        {
            BusinessUnitId = businessUnitId,
            SourceType = ExtractionSourceType.ManualUpload,
            ContentHash = hash,
            StoragePath = stored.StorageUri,
            FileName = safeName,
            FileType = extension,
            Attempts = 1,
        };

        DocumentExtractionInput input;
        try
        {
            input = await _reader.ReadAsync(job, cancellationToken);
        }
        catch (DocumentParsingException exception)
        {
            _log.LogWarning(exception,
                "Customer PO document {FileName} ({Hash}) for tenant {BusinessUnitId} could not be read.",
                safeName, hash, businessUnitId);
            throw new CustomerPurchaseOrderDocumentException(
                CustomerPurchaseOrderDocumentErrorCodes.Unreadable, exception.Message);
        }

        var reading = _extractor.Read(input, bytes, extension);

        var now = DateTime.UtcNow;
        var attachment = new Attachment
        {
            ParentType = PendingParentType,
            ParentId = businessUnitId,
            FileName = Truncate(safeName, 255)!,
            FilePath = Truncate(stored.StorageUri, 500)!,
            MimeType = Truncate(inspection.DetectedContentType, 100),
            FileSize = bytes.LongLength,
            ContentType = Truncate(inspection.DetectedContentType?.Split('/')[0], 200),
            ContentSha256 = hash,
            CreatedOn = now,
            UploadedDate = now,
        };
        _db.Attachments.Add(attachment);
        await _db.SaveChangesAsync(cancellationToken);

        _log.LogInformation(
            "Customer PO document {FileName} ({Hash}) read for tenant {BusinessUnitId} by {Actor} via {Path}: "
            + "{Lines} line(s), attachment {AttachmentId}.",
            safeName, hash, businessUnitId, actor, input.ProcessingPath, reading.Lines.Count, attachment.Id);

        return new CustomerPurchaseOrderDocumentView(
            attachment.Id,
            safeName,
            hash,
            bytes.LongLength,
            input.ProcessingPath.ToString(),
            input.OcrStatus.ToString(),
            reading.ExternalPoNumber,
            reading.PoDate,
            reading.PoDateText,
            reading.PoDateIsDayMonthAmbiguous,
            reading.Lines,
            reading.ReviewReasons);
    }

    /// <summary>
    /// Creates the purchase order through the existing customer-award application service — which
    /// owns numbering, idempotent replay, tenant locking and the audit event — and then records
    /// which document it was read from.
    ///
    /// <para>The evidence link is written here rather than inside that service so this feature adds
    /// nothing to the matching aggregate's command surface. It is a provenance fact about a record
    /// that already exists, not a state transition, so it does not take a version of its own.</para>
    /// </summary>
    public async Task<CustomerPurchaseOrderView> CreateFromDocumentAsync(
        long businessUnitId,
        string idempotencyKey,
        string correlationId,
        CreateCustomerPurchaseOrderFromDocumentCommand command,
        string actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.PurchaseOrder is null)
            throw new ArgumentException("A purchase order is required.", nameof(command));

        await EnsureDocumentIsOwnedAsync(businessUnitId, command.SourceAttachmentId, cancellationToken);

        var result = await _awards.CreatePurchaseOrderAsync(businessUnitId, idempotencyKey, correlationId,
            command.PurchaseOrder, actor, cancellationToken);

        // Both entities are re-read here rather than carried across the call: the application
        // service owns its own unit of work and may reset the change tracker, which would silently
        // drop an edit made to an instance obtained beforehand.
        var purchaseOrder = await _db.CustomerPurchaseOrders
            .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == result.Id, cancellationToken)
            ?? throw new KeyNotFoundException("The customer PO was not found after it was created.");
        var attachment = await _db.Attachments
            .SingleOrDefaultAsync(x => x.Id == command.SourceAttachmentId, cancellationToken)
            ?? throw new KeyNotFoundException("The uploaded purchase-order document was not found.");

        purchaseOrder.SourceAttachmentId = attachment.Id;
        attachment.ParentType = PurchaseOrderParentType;
        attachment.ParentId = purchaseOrder.Id;
        await _db.SaveChangesAsync(cancellationToken);

        _log.LogInformation(
            "Customer PO {InternalNumber} for tenant {BusinessUnitId} recorded source document {AttachmentId}.",
            purchaseOrder.InternalNumber, businessUnitId, attachment.Id);

        return result;
    }

    /// <summary>
    /// The uploaded document must belong to the calling tenant. A pending upload is filed under
    /// the tenant itself; once committed it hangs off the purchase order, and that purchase order
    /// carries the tenant. Anything else is refused rather than silently linked.
    /// </summary>
    private async Task EnsureDocumentIsOwnedAsync(
        long businessUnitId, long attachmentId, CancellationToken cancellationToken)
    {
        var attachment = await _db.Attachments.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == attachmentId, cancellationToken)
            ?? throw new KeyNotFoundException("The uploaded purchase-order document was not found.");

        var owned = attachment.ParentType switch
        {
            PendingParentType => attachment.ParentId == businessUnitId,
            PurchaseOrderParentType => await _db.CustomerPurchaseOrders.AsNoTracking().AnyAsync(
                x => x.BusinessUnitId == businessUnitId && x.Id == attachment.ParentId, cancellationToken),
            _ => false,
        };

        if (!owned)
            throw new KeyNotFoundException("The uploaded purchase-order document was not found for this tenant.");
    }

    private static string? Truncate(string? value, int length)
        => value is null || value.Length <= length ? value : value[..length];
}
