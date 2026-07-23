using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
        private readonly IFileStorage _storage;
        private readonly ILogger<FileController> _logger;

        public FileController(
            ErpRfqAutomationContext context,
            IFileStorage storage,
            ILogger<FileController> logger)
        {
            _context = context;
            _storage = storage;
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

                var stream = await _storage.OpenReadAsync(attachment.FilePath, ct);
                var contentType = string.IsNullOrWhiteSpace(attachment.MimeType)
                    ? "application/octet-stream"
                    : attachment.MimeType;
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
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error downloading attachment {AttachmentId}.", attachmentId);
                return StatusCode(500, "An error occurred while retrieving the file.");
            }
        }
    }
}
