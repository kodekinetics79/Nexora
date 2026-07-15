using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

namespace ERP_RFQ_Automation.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class ManualUploadController : ControllerBase
    {
        private readonly ManualUploadService _manualUploadService;
        private readonly ErpRfqAutomationContext _context;
        private readonly ILogger<ManualUploadController> _logger;
        private readonly string _attachmentPath;

        public ManualUploadController(
            ManualUploadService manualUploadService,
            ErpRfqAutomationContext context,
            ILogger<ManualUploadController> logger,
            IWebHostEnvironment env)
        {
            _manualUploadService = manualUploadService;
            _context = context;
            _logger = logger;
            _attachmentPath = Path.Combine(env.ContentRootPath, "Uploads", "Manual_Attachments");
        }

        /// <summary>
        /// Uploads multiple files, processes them to extract lead data, and saves to the database.
        /// </summary>
        /// <param name="files">The files to upload.</param>
        /// <param name="businessUnitId">The BusinessUnitId for the lead.</param>
        /// <returns>The ID of the created Lead.</returns>
        /// <summary>
        /// Uploads multiple files, processes them to extract lead data, and saves to the database.
        /// </summary>
        /// <param name="files">The files to upload.</param>
        /// <param name="businessUnitId">The BusinessUnitId for the lead.</param>
        /// <returns>The ID of the created Lead.</returns>
        [HttpPost("upload")]
        public async Task<IActionResult> UploadFiles(List<IFormFile> files, [FromForm] long? businessUnitId = null)
        {
            var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
            var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

            if (targetBUId <= 0)
                return BadRequest(new { success = false, message = "Business Unit ID is required." });

            if (files == null || !files.Any())
            {
                return BadRequest(new { success = false, message = "No files uploaded." });
            }

            try
            {
                var result = await _manualUploadService.ProcessUploadedFilesAsync(files, targetBUId);
                
                if (!result.Success)
                {
                    return BadRequest(new { success = false, message = result.Message, errorCode = result.ErrorCode });
                }

                return Ok(new { success = true, message = result.Message, data = new { LeadId = result.Data } });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during file upload.");
                return StatusCode(500, new { success = false, message = "Internal server error." });
            }
        }

        /// <summary>
        /// Uploads a specialized RFQ Excel file and creates an RFQ.
        /// </summary>
        [HttpPost("upload-rfq-excel")]
        public async Task<IActionResult> UploadCustomerRfqExcel(IFormFile file, [FromForm] long? businessUnitId = null, [FromForm] string? createdBy = null)
        {
            var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
            var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

            if (targetBUId <= 0)
                return BadRequest(new { success = false, message = "Business Unit ID is required." });

            if (file == null)
            {
                return BadRequest(new { success = false, message = "No file uploaded." });
            }

            try
            {
                var userEmail = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? createdBy ?? "system";
                var result = await _manualUploadService.ProcessCustomerRfqExcelAsync(file, targetBUId, userEmail);

                if (!result.Success)
                {
                    return BadRequest(new { success = false, message = result.Message });
                }

                return Ok(new { success = true, message = result.Message, data = new { RfqId = result.Data } });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during customer RFQ Excel upload.");
                return StatusCode(500, new { success = false, message = "Internal server error." });
            }
        }

        /// <summary>
        /// Lists the uploaded files for manual leads.
        /// </summary>
        /// <returns>A list of file information.</returns>
        [HttpGet("list")]
        public async Task<IActionResult> ListFiles([FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0)
                    return BadRequest(new { success = false, message = "Business Unit ID is required." });

                // Query attachments from DB where ParentType is Lead and Lead.LeadSource is Manual
                var manualLeads = await _context.Leads
                    .AsNoTracking()
                    .Where(l => l.LeadSource == "Manual" && l.BusinessUnitId == targetBUId)
                    .Select(l => l.Id)
                    .ToListAsync();

                var files = await _context.Attachments
                    .AsNoTracking()
                    .Where(a => a.ParentType == "Lead" && manualLeads.Contains(a.ParentId))
                    .Select(a => new
                    {
                        a.Id,
                        a.FileName,
                        a.FileSize,
                        a.UploadedDate,
                        LeadId = a.ParentId
                    })
                    .OrderByDescending(a => a.UploadedDate)
                    .ToListAsync();

                return Ok(files);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error listing files.");
                return StatusCode(500, "Internal server error.");
            }
        }
    }
}