using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
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
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<FileController> _logger;

        public FileController(IWebHostEnvironment env, ILogger<FileController> logger)
        {
            _env = env;
            _logger = logger;
        }

        [HttpGet("DownloadFile")]
        [AllowAnonymous]
        public IActionResult DownloadFile([FromQuery] string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                return BadRequest("File path is required.");
            }

            try
            {
                string physicalPath;
                
                // Sanitize the file path to prevent directory traversal and handle leading slashes
                filePath = filePath.TrimStart('/', '\\');

                // If the path is absolute (rooted), use it as is (for backward compatibility), 
                // but strictly check if it's safe later
                if (Path.IsPathRooted(filePath))
                {
                    physicalPath = filePath;
                }
                else
                {
                    // If the path is relative, combine it with the ContentRootPath
                    physicalPath = Path.Combine(_env.ContentRootPath, filePath);
                }

                if (!System.IO.File.Exists(physicalPath))
                {
                    _logger.LogWarning("File not found: {Path}", physicalPath);
                    return NotFound("The requested file was not found on the server.");
                }

                // Security check: Ensure the file is within the Uploads directory
                var absolutePhysicalPath = Path.GetFullPath(physicalPath);
                var uploadsRoot = Path.GetFullPath(Path.Combine(_env.ContentRootPath, "Uploads"));
                
                if (!absolutePhysicalPath.StartsWith(uploadsRoot, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Access denied to path: {Path}", absolutePhysicalPath);
                    return Forbid("Access to this file path is restricted.");
                }

                var fileName = Path.GetFileName(physicalPath);
                var provider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
                if (!provider.TryGetContentType(fileName, out var contentType))
                {
                    contentType = "application/octet-stream";
                }

                return PhysicalFile(absolutePhysicalPath, contentType, fileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error downloading file: {Path}", filePath);
                return StatusCode(500, "An error occurred while retrieving the file.");
            }
        }
    }
}
