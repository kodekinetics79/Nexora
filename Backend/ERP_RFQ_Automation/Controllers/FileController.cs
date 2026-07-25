using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.LeadIdentity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.Models;
using System;
using System.IO;
using System.Threading.Tasks;

namespace ERP_RFQ_Automation.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class FileController : ControllerBase
    {
        private readonly ErpRfqAutomationContext _context;
        private readonly IEvidenceObjectStorage _evidenceStorage;
        private readonly IFileStorage? _legacyStorage;
        private readonly ILogger<FileController> _logger;

        public FileController(
            ErpRfqAutomationContext context,
            IFileStorage legacyStorage,
            ILogger<FileController> logger)
        {
            _context = context;
            _legacyStorage = legacyStorage;
            _evidenceStorage = new LocalEvidenceObjectStorage(legacyStorage);
            _logger = logger;
        }

        [ActivatorUtilitiesConstructor]
        public FileController(
            ErpRfqAutomationContext context,
            IFileStorage legacyStorage,
            IEvidenceObjectStorage evidenceStorage,
            ILogger<FileController> logger)
        {
            _context = context;
            _legacyStorage = null;
            _evidenceStorage = evidenceStorage;
            _logger = logger;
        }

        // Path-addressed downloads cannot prove record ownership and are intentionally retired.
        [HttpGet("DownloadFile")]
        public IActionResult DownloadFile([FromQuery] string filePath)
            => StatusCode(StatusCodes.Status410Gone, new
            {
                message = "Path-based downloads are no longer supported. Use the attachment endpoint."
            });

        [HttpGet("attachment/{attachmentId:long}")]
        [RequireModulePermission("Leads", PermissionAction.View)]
        public async Task<IActionResult> DownloadAttachment(long attachmentId, CancellationToken ct)
        {
            var rawBusinessUnitId = User.FindFirst("businessUnitId")?.Value;
            if (!long.TryParse(rawBusinessUnitId, out var businessUnitId) || businessUnitId <= 0)
                return BadRequest("Business Unit ID is required.");

            try
            {
                // Attachments are polymorphic today, but production records currently use Lead.
                // The explicit BU predicate is deliberate defense-in-depth even though the Lead
                // query filter is also tenant-scoped.
                var attachment = await _context.Attachments
                    .AsNoTracking()
                    .SingleOrDefaultAsync(a => a.Id == attachmentId, ct);

                if (attachment is null || !string.Equals(attachment.ParentType, "Lead", StringComparison.OrdinalIgnoreCase))
                    return NotFound();

                var belongsToTenant = await _context.Leads
                    .AsNoTracking()
                    .AnyAsync(l => l.Id == attachment.ParentId && l.BusinessUnitId == businessUnitId, ct);
                if (!belongsToTenant)
                    return NotFound();

                var sourceDocumentId = await _context.Set<LeadOccurrenceDocument>()
                    .AsNoTracking()
                    .Where(link => link.BusinessUnitId == businessUnitId
                                   && link.Occurrence.LeadId == attachment.ParentId)
                    .OrderBy(link => link.Ordinal)
                    .ThenBy(link => link.Id)
                    .Select(link => (long?)link.SourceDocumentId)
                    .FirstOrDefaultAsync(ct);

                var source = sourceDocumentId.HasValue
                    ? await _context.Set<SourceDocument>().AsNoTracking()
                        .SingleOrDefaultAsync(document => document.BusinessUnitId == businessUnitId
                            && document.Id == sourceDocumentId.Value
                            && document.OriginalFileName == attachment.FileName, ct)
                    : null;
                var job = source?.ExtractionJobId is { } extractionJobId
                    ? await _context.Set<ExtractionJob>().AsNoTracking()
                        .SingleOrDefaultAsync(candidate => candidate.BusinessUnitId == businessUnitId
                            && candidate.Id == extractionJobId, ct)
                    : null;

                if (source is null || job is null)
                {
                    // Compatibility is limited to direct legacy construction. The application DI
                    // path always supplies IEvidenceObjectStorage and therefore fails closed.
                    if (_legacyStorage is null)
                        return NotFound();
                    var legacyStream = await _legacyStorage.OpenReadAsync(attachment.FilePath, ct);
                    return File(legacyStream, attachment.MimeType ?? "application/octet-stream",
                        attachment.FileName, enableRangeProcessing: true);
                }

                if (attachment.FileSize.HasValue && attachment.FileSize.Value != source.ByteSize)
                {
                    _logger.LogWarning("Attachment {AttachmentId} does not match authoritative evidence size.", attachmentId);
                    return Problem(statusCode: StatusCodes.Status409Conflict,
                        title: "The evidence object failed integrity verification.");
                }

                var stream = await _evidenceStorage.OpenVerifiedReadAsync(
                    job.StoragePath, source.ContentHash, ct);
                var contentType = string.IsNullOrWhiteSpace(source.DetectedMimeType)
                    ? "application/octet-stream"
                    : source.DetectedMimeType;
                return File(stream, contentType, attachment.FileName, enableRangeProcessing: true);
            }
            catch (FileNotFoundException)
            {
                return NotFound("The requested file was not found in evidence storage.");
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Rejected unsafe storage path for attachment {AttachmentId}.", attachmentId);
                return NotFound();
            }
            catch (InvalidDataException ex)
            {
                _logger.LogWarning(ex, "Evidence integrity verification failed for attachment {AttachmentId}.", attachmentId);
                return Problem(statusCode: StatusCodes.Status409Conflict,
                    title: "The evidence object failed integrity verification.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error downloading attachment {AttachmentId}.", attachmentId);
                return StatusCode(500, "An error occurred while retrieving the file.");
            }
        }
    }
}
