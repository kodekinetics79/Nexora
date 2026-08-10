using System.Security.Cryptography;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Security.DocumentInspection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ERP_RFQ_Automation.Delivery;

public sealed record DeliveryProofEvidenceView(
    long AttachmentId, string FileName, string? MimeType, long FileSize, string ContentSha256);

public interface IDeliveryProofEvidenceService
{
    Task<DeliveryProofEvidenceView> CaptureAsync(
        long businessUnitId,
        long shipmentId,
        string kind,
        string fileName,
        byte[] bytes,
        string? declaredContentType,
        string actor,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// FR-DLM-03. Files a signature, a stamp or a condition photograph against a shipment.
///
/// <para><b>No second upload path.</b> A signature is a document. The bytes go through the same
/// <see cref="IFileInspectionService"/> as every other door — bounded size, magic-byte typing
/// against the declared extension, macro refusal, malware scan — and are written to the same
/// content-addressed immutable <see cref="IEvidenceObjectStorage"/>, producing an
/// <see cref="Attachment"/> row with its SHA-256 recorded. Gate 5 made the identical call for lot
/// certificates and the reasoning has not changed: a parallel pipeline is a second place for an
/// unscanned file to enter the estate and a second set of integrity promises to keep in step.</para>
///
/// <para><b>Why the upload is separate from the confirmation.</b> A driver on a loading bay uploads
/// three files and then signs off. Putting the bytes inside the confirmation command would mean a
/// failed malware scan on the third photograph rolls back the accepted quantities the customer has
/// already agreed — and the operator would key them again from memory. The evidence is captured
/// first, is inert until a POD references it, and the confirmation carries ids.</para>
///
/// <para><b>Ownership is proved before a byte is scanned.</b> The parent is the shipment, which is
/// tenant-scoped by both a query filter and (once migrated) a row-level-security policy, so an
/// upload against another tenant's shipment never reaches the scanner, let alone the object
/// store.</para>
/// </summary>
public sealed class DeliveryProofEvidenceService(
    ErpRfqAutomationContext db,
    IFileInspectionService inspection,
    IEvidenceObjectStorage storage,
    ILogger<DeliveryProofEvidenceService> log) : IDeliveryProofEvidenceService
{
    /// <summary>
    /// The <c>Attachment.ParentType</c> POD evidence is filed under, with the shipment as parent id.
    /// <c>FileController.DownloadDeliveryProofEvidence</c> serves this and only this.
    /// </summary>
    public const string EvidenceParentType = "DeliveryProofEvidence";

    /// <summary>Matches <c>DocumentFileInspectionService.DefaultMaximumFileBytes</c>.</summary>
    public const long MaximumEvidenceBytes = 25L * 1024 * 1024;

    public const string SignatureKind = "SIGNATURE";
    public const string StampKind = "STAMP";
    public const string PhotoKind = "PHOTO";

    public static readonly IReadOnlySet<string> Kinds = new HashSet<string>(StringComparer.Ordinal)
    {
        SignatureKind, StampKind, PhotoKind
    };

    private readonly ErpRfqAutomationContext _db = db;
    private readonly IFileInspectionService _inspection = inspection;
    private readonly IEvidenceObjectStorage _storage = storage;
    private readonly ILogger<DeliveryProofEvidenceService> _log = log;

    public async Task<DeliveryProofEvidenceView> CaptureAsync(
        long businessUnitId,
        long shipmentId,
        string kind,
        string fileName,
        byte[] bytes,
        string? declaredContentType,
        string actor,
        CancellationToken cancellationToken = default)
    {
        if (businessUnitId <= 0)
            throw new UnauthorizedAccessException("A valid authenticated tenant is required.");
        if (string.IsNullOrWhiteSpace(actor))
            throw new DeliveryValidationException("An authenticated actor is required.");

        var normalizedKind = (kind ?? string.Empty).Trim().ToUpperInvariant();
        if (!Kinds.Contains(normalizedKind))
            throw new DeliveryValidationException(
                $"'{kind}' is not a proof-of-delivery evidence kind.");
        if (string.IsNullOrWhiteSpace(fileName))
            throw new DeliveryValidationException("The evidence file must have a name.");
        if (bytes is null || bytes.Length == 0)
            throw new DeliveryValidationException("The uploaded evidence is empty.");
        if (bytes.LongLength > MaximumEvidenceBytes)
            throw new DeliveryValidationException("The evidence exceeds the 25 MB limit.");

        var shipment = await _db.Shipments.AsNoTracking()
            .Where(s => s.BusinessUnitId == businessUnitId && s.Id == shipmentId && s.IsActive)
            .Select(s => new { s.Id, s.ShipmentNo, s.DeliveryStatus })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new DeliveryValidationException("Shipment was not found in this tenant.");

        // A POD is evidence of a handover. Capturing one against a shipment still sitting in the
        // warehouse, or one already confirmed and closed, is capturing evidence of nothing.
        if (!DeliveryStatuses.Confirmable.Contains(shipment.DeliveryStatus))
            throw new DeliveryConflictException(
                $"Proof of delivery cannot be captured against a {shipment.DeliveryStatus} shipment.");

        var safeName = Path.GetFileName(fileName.Trim());
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        FileInspectionResult result;
        await using (var content = new MemoryStream(bytes, writable: false))
        {
            result = await _inspection.InspectAsync(
                new FileInspectionRequest(content, safeName, declaredContentType, bytes.LongLength),
                cancellationToken);
        }

        if (!result.IsCleared)
        {
            _log.LogWarning(
                "POD evidence {FileName} for shipment {ShipmentNo} (tenant {BusinessUnitId}) was "
                + "stopped by inspection: {Status} {Code}.",
                safeName, shipment.ShipmentNo, businessUnitId, result.Status, result.ErrorCode);
            throw new DocumentInspectionException(result);
        }

        var stored = await _storage.WriteImmutableAsync(
            businessUnitId, "cleared", hash, Path.GetExtension(safeName), bytes, cancellationToken);

        var now = DateTime.UtcNow;
        var attachment = new Attachment
        {
            ParentType = EvidenceParentType,
            ParentId = shipment.Id,
            FileName = Truncate(safeName, 255)!,
            FilePath = Truncate(stored.StorageUri, 500)!,
            MimeType = Truncate(result.DetectedContentType, 100),
            FileSize = bytes.LongLength,
            ContentType = Truncate(result.DetectedContentType?.Split('/')[0], 200),
            ContentSha256 = hash,
            CreatedOn = now,
            UploadedDate = now,
        };
        _db.Attachments.Add(attachment);
        await _db.SaveChangesAsync(cancellationToken);

        _log.LogInformation(
            "POD {Kind} evidence captured against shipment {ShipmentNo} ({ShipmentId}) for tenant "
            + "{BusinessUnitId} by {Actor}.",
            normalizedKind, shipment.ShipmentNo, shipment.Id, businessUnitId, actor);

        return new DeliveryProofEvidenceView(
            attachment.Id, attachment.FileName, attachment.MimeType, bytes.LongLength, hash);
    }

    private static string? Truncate(string? value, int length)
        => string.IsNullOrWhiteSpace(value) ? null : value.Length <= length ? value : value[..length];
}
