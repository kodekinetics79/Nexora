using System.Security.Cryptography;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Security.DocumentInspection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ERP_RFQ_Automation.Traceability;

public interface IMaterialLotCertificateService
{
    Task<LotCertificateView> AttachAsync(
        RecordLotCertificateCommand command,
        string fileName,
        byte[] bytes,
        string? declaredContentType,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LotCertificateView>> ListAsync(
        long businessUnitId, long materialLotId, CancellationToken cancellationToken = default);
}

/// <summary>
/// FR-MTR-02. Files a manufacturer certificate, certificate of origin, certificate of conformity or
/// SASO/SABER document against a lot.
///
/// <para><b>No second upload path.</b> The bytes go through the same
/// <see cref="IFileInspectionService"/> as every other door — bounded size, magic-byte typing
/// against the declared extension, macro refusal, malware scan — and are written to the same
/// content-addressed immutable <see cref="IEvidenceObjectStorage"/>, producing an
/// <see cref="Attachment"/> row with its SHA-256 recorded. That means the certificate is served by
/// the existing digest-verifying download route rather than by a new one, and a compliance document
/// that has been altered since it was filed fails the same integrity check an RFQ source document
/// does. Building a parallel pipeline here would have been a second place for an unscanned file to
/// enter the estate and a second set of integrity promises to keep in step with the first.</para>
///
/// <para>The one thing that is new is the parent type — see
/// <see cref="CertificateParentType"/> — which <c>FileController</c> now serves.</para>
/// </summary>
public sealed class MaterialLotCertificateService(
    ErpRfqAutomationContext db,
    IFileInspectionService inspection,
    IEvidenceObjectStorage storage,
    ILogger<MaterialLotCertificateService> log) : IMaterialLotCertificateService
{
    /// <summary>
    /// The <c>Attachment.ParentType</c> a lot certificate is filed under. The parent id is the
    /// material lot, so ownership is provable against a tenant-scoped table rather than assumed.
    /// </summary>
    public const string CertificateParentType = "MaterialLotCertificate";

    /// <summary>Matches <c>DocumentFileInspectionService.DefaultMaximumFileBytes</c>.</summary>
    public const long MaximumDocumentBytes = 25L * 1024 * 1024;

    private readonly ErpRfqAutomationContext _db = db;
    private readonly IFileInspectionService _inspection = inspection;
    private readonly IEvidenceObjectStorage _storage = storage;
    private readonly ILogger<MaterialLotCertificateService> _log = log;

    public async Task<LotCertificateView> AttachAsync(
        RecordLotCertificateCommand command,
        string fileName,
        byte[] bytes,
        string? declaredContentType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.BusinessUnitId <= 0)
            throw new UnauthorizedAccessException("A valid authenticated tenant is required.");
        if (string.IsNullOrWhiteSpace(command.Actor))
            throw new MaterialTraceabilityValidationException("An authenticated actor is required.");
        if (string.IsNullOrWhiteSpace(fileName))
            throw new MaterialTraceabilityValidationException("The certificate file must have a name.");
        if (bytes is null || bytes.Length == 0)
            throw new MaterialTraceabilityValidationException("The uploaded certificate is empty.");
        if (bytes.LongLength > MaximumDocumentBytes)
            throw new MaterialTraceabilityValidationException("The certificate exceeds the 25 MB limit.");

        var type = (command.CertificateType ?? string.Empty).Trim().ToUpperInvariant();
        if (!MaterialCertificateTypes.All.Contains(type))
            throw new MaterialTraceabilityValidationException(
                $"'{command.CertificateType}' is not a recognised certificate type.");
        if (command.IssuedOn.HasValue && command.ExpiresOn.HasValue
            && command.ExpiresOn.Value < command.IssuedOn.Value)
            throw new MaterialTraceabilityValidationException(
                "A certificate cannot expire before it was issued.");

        // Ownership is proved before a single byte is scanned or stored: an upload against another
        // tenant's lot must not even reach the malware scanner, let alone the object store.
        var lot = await _db.Set<MaterialLot>().AsNoTracking()
            .Where(x => x.BusinessUnitId == command.BusinessUnitId && x.Id == command.MaterialLotId)
            .Select(x => new { x.Id, x.LotNumber })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new MaterialTraceabilityValidationException("Material lot was not found in this tenant.");

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
                "Lot certificate {FileName} for tenant {BusinessUnitId} was stopped by inspection: {Status} {Code}.",
                safeName, command.BusinessUnitId, result.Status, result.ErrorCode);
            throw new DocumentInspectionException(result);
        }

        var stored = await _storage.WriteImmutableAsync(
            command.BusinessUnitId, "cleared", hash, Path.GetExtension(safeName), bytes, cancellationToken);

        var now = DateTime.UtcNow;
        var attachment = new Attachment
        {
            ParentType = CertificateParentType,
            ParentId = lot.Id,
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

        var certificate = new MaterialLotCertificate
        {
            BusinessUnitId = command.BusinessUnitId,
            MaterialLotId = lot.Id,
            CertificateType = type,
            CertificateNumber = Truncate(command.CertificateNumber?.Trim(), 120),
            IssuedBy = Truncate(command.IssuedBy?.Trim(), 255),
            IssuedOn = command.IssuedOn,
            ExpiresOn = command.ExpiresOn,
            AttachmentId = attachment.Id,
            ContentSha256 = hash,
            FileName = attachment.FileName,
            Notes = Truncate(command.Notes?.Trim(), 1000),
            UploadedOn = now,
            UploadedBy = command.Actor.Trim(),
        };
        _db.Set<MaterialLotCertificate>().Add(certificate);
        await _db.SaveChangesAsync(cancellationToken);

        _log.LogInformation(
            "Lot certificate {Type} filed against lot {LotNumber} ({LotId}) for tenant {BusinessUnitId} by {Actor}.",
            type, lot.LotNumber, lot.Id, command.BusinessUnitId, command.Actor);

        var today = DateOnly.FromDateTime(now);
        return ToView(certificate, today);
    }

    public async Task<IReadOnlyList<LotCertificateView>> ListAsync(
        long businessUnitId, long materialLotId, CancellationToken cancellationToken = default)
    {
        if (businessUnitId <= 0)
            throw new UnauthorizedAccessException("A valid authenticated tenant is required.");

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var rows = await _db.Set<MaterialLotCertificate>().AsNoTracking()
            .Where(c => c.BusinessUnitId == businessUnitId && c.MaterialLotId == materialLotId)
            .OrderBy(c => c.CertificateType).ThenByDescending(c => c.ExpiresOn).ThenBy(c => c.Id)
            .ToListAsync(cancellationToken);
        return rows.Select(c => ToView(c, today)).ToList();
    }

    private static LotCertificateView ToView(MaterialLotCertificate certificate, DateOnly today)
        => new(certificate.Id, certificate.MaterialLotId, certificate.CertificateType,
            certificate.CertificateNumber, certificate.IssuedBy, certificate.IssuedOn, certificate.ExpiresOn,
            certificate.ExpiryState(today),
            certificate.ExpiresOn.HasValue ? certificate.ExpiresOn.Value.DayNumber - today.DayNumber : null,
            certificate.AttachmentId, certificate.FileName, certificate.ContentSha256, certificate.Notes,
            certificate.UploadedOn, certificate.UploadedBy);

    private static string? Truncate(string? value, int length)
        => string.IsNullOrWhiteSpace(value) ? null : value.Length <= length ? value : value[..length];
}
