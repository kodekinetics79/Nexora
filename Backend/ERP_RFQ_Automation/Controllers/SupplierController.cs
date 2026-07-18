using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.DTOs.BusinessUnit;
using ERP_RFQ_Automation.DTOs.CurrencyDTOs;
using ERP_RFQ_Automation.DTOs.SupplierDTOs;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.IO;

namespace ERP_RFQ_Automation.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class SupplierController : ControllerBase
    {
        private readonly ISupplierRepository _repository;
        private readonly IWebHostEnvironment _environment;
        private static readonly int[] AllowedPageSizes = { 5, 10, 25, 50 };

        public SupplierController(ISupplierRepository repository, IWebHostEnvironment environment)
        {
            _repository = repository;
            _environment = environment;
        }

        // GET: api/Supplier?pageNumber=1&pageSize=10&id=1&name=abc&contactEmail=abc@example.com&taxId=123&currencyId=1&isActive=true&businessUnitId=1
        [HttpGet]
        [RequireModulePermission("Suppliers", PermissionAction.View)]
        public async Task<ActionResult<DTOs.SupplierDTOs.PaginatedResponseDTO<SupplierResponseDTO>>> GetAll(
            [FromQuery] long? businessUnitId = null,
            [FromQuery] int pageNumber = 1,
            [FromQuery] int pageSize = 10,
            [FromQuery] long? id = null,
            [FromQuery] string? name = null,
            [FromQuery] string? contactEmail = null,
            [FromQuery] long? currencyId = null,
            [FromQuery] bool? isActive = null,
            [FromQuery] string? docId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");
                
                if (pageNumber < 1)
                    return BadRequest("Page number must be greater than or equal to 1.");

                // Relaxed validation: Allow any page size up to 1000
                if (pageSize < 1 || pageSize > 1000)
                    return BadRequest("Page size must be between 1 and 1000.");

                var (suppliers, totalCount) = await _repository.GetAllAsync(pageNumber, pageSize, id, name, contactEmail, currencyId, isActive, docId, targetBUId);

                var response = new DTOs.SupplierDTOs.PaginatedResponseDTO<SupplierResponseDTO>
                {
                    Items = suppliers,
                    TotalCount = totalCount,
                    PageNumber = pageNumber,
                    PageSize = pageSize
                };

                return Ok(response);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error retrieving data: {ex.Message}");
            }
        }

        // GET: api/Supplier/5
        [HttpGet("{id}")]
        [RequireModulePermission("Suppliers", PermissionAction.View)]
        public async Task<ActionResult<SupplierResponseDTO>> GetById(long id, [FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                var supplier = await _repository.GetByIdAsync(id, targetBUId);
                return Ok(MapToResponse(supplier));
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ex.Message);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error retrieving data: {ex.Message}");
            }
        }

        // POST: api/Supplier
        [HttpPost]
        [RequireModulePermission("Suppliers", PermissionAction.Create)]
        public async Task<ActionResult<SupplierResponseDTO>> Create([FromForm] SupplierCreateRequestDTO request)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                if (claimBUId > 0) request.Buid = claimBUId;

                if (request.Buid <= 0) return BadRequest("Business Unit ID is required.");

                string? imagePath = null;
                if (request.ImageFile != null)
                {
                    var uploadsFolder = Path.Combine(_environment.WebRootPath, "SupplierImages");
                    if (!Directory.Exists(uploadsFolder))
                        Directory.CreateDirectory(uploadsFolder);

                    var uniqueFileName = $"{Guid.NewGuid()}_{request.ImageFile.FileName}";
                    var filePath = Path.Combine(uploadsFolder, uniqueFileName);
                    using (var fileStream = new FileStream(filePath, FileMode.Create))
                    {
                        await request.ImageFile.CopyToAsync(fileStream);
                    }
                    imagePath = $"/SupplierImages/{uniqueFileName}";
                }

                var supplier = new Supplier
                {
                    Name = request.Name,
                    ContactEmail = request.ContactEmail,
                    ImageUrl = imagePath ?? string.Empty,  // Default to empty if no image provided
                    PaymentTerms = request.PaymentTerms,
                    AddressLine1 = request.AddressLine1,
                    AddressLine2 = request.AddressLine2,
                    CityId = request.CityId,
                    CountryId = request.CountryId,
                    PostalCode = request.PostalCode,
                    SuccessRate = request.SuccessRate,
                    AvgResponseTime = request.AvgResponseTime,
                    Tags = request.Tags,
                    Comments = request.Comments,
                    CurrencyId = request.CurrencyId,
                    Buid = request.Buid,
                    IsActive = request.IsActive ?? true,
                    CreatedBy = request.CreatedBy,
                    CreatedOn = DateTime.UtcNow
                };

                await _repository.AddAsync(supplier);

                var response = MapToResponse(supplier);
                return CreatedAtAction(nameof(GetById), new { id = supplier.Id, businessUnitId = supplier.Buid }, response);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error creating data: {ex.Message}");
            }
        }

        [HttpPut("{id}")]
        [RequireModulePermission("Suppliers", PermissionAction.Edit)]
        public async Task<ActionResult> Update(long id, [FromQuery] long? businessUnitId, [FromForm] SupplierUpdateRequestDTO request)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                if (claimBUId > 0) request.Buid = claimBUId;

                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? request.Buid);

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                if (targetBUId != request.Buid)
                    return BadRequest("Business Unit ID mismatch between context and request.");

                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                var existing = await _repository.GetByIdAsync(id, targetBUId);

                string? imagePath = existing.ImageUrl;
                if (request.ImageFile != null)
                {
                    var uploadsFolder = Path.Combine(_environment.WebRootPath, "SupplierImages");
                    if (!Directory.Exists(uploadsFolder))
                        Directory.CreateDirectory(uploadsFolder);

                    var uniqueFileName = $"{Guid.NewGuid()}_{request.ImageFile.FileName}";
                    var filePath = Path.Combine(uploadsFolder, uniqueFileName);
                    using (var fileStream = new FileStream(filePath, FileMode.Create))
                    {
                        await request.ImageFile.CopyToAsync(fileStream);
                    }
                    imagePath = $"/SupplierImages/{uniqueFileName}";
                }

                existing.Name = request.Name;
                existing.ContactEmail = request.ContactEmail;
                existing.ImageUrl = imagePath ?? string.Empty;
                existing.PaymentTerms = request.PaymentTerms;
                existing.AddressLine1 = request.AddressLine1;
                existing.AddressLine2 = request.AddressLine2;
                existing.CityId = request.CityId;
                existing.CountryId = request.CountryId;
                existing.PostalCode = request.PostalCode;
                existing.SuccessRate = request.SuccessRate;
                existing.AvgResponseTime = request.AvgResponseTime;
                existing.Tags = request.Tags;
                existing.Comments = request.Comments;
                existing.CurrencyId = request.CurrencyId;
                existing.Buid = request.Buid;
                existing.IsActive = request.IsActive ?? true;
                existing.ModifiedBy = request.ModifiedBy;
                existing.ModifiedOn = DateTime.UtcNow;

                await _repository.UpdateAsync(existing, targetBUId);

                return NoContent();
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ex.Message);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error updating data: {ex.Message}");
            }
        }

        // DELETE: api/Supplier/5
        [HttpDelete("{id}")]
        [RequireModulePermission("Suppliers", PermissionAction.Delete)]
        public async Task<ActionResult> Delete(long id, [FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                await _repository.DeleteAsync(id, targetBUId);
                return NoContent();
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error deleting data: {ex.Message}");
            }
        }


        private SupplierResponseDTO MapToResponse(Supplier supplier)
        {
            return new SupplierResponseDTO
            {
                Id = supplier.Id,
                DocId = supplier.DocId,
                Name = supplier.Name,
                ContactEmail = supplier.ContactEmail,
                ImageUrl = supplier.ImageUrl,
                PaymentTerms = supplier.PaymentTerms,
                AddressLine1 = supplier.AddressLine1,
                AddressLine2 = supplier.AddressLine2,
                CityId = supplier.CityId,
                CityName = supplier.City != null ? supplier.City.CityName : null,
                CountryId = supplier.CountryId,
                CountryName = supplier.Country != null ? supplier.Country.CountryName : null,
                PostalCode = supplier.PostalCode,
                SuccessRate = supplier.SuccessRate,
                AvgResponseTime = supplier.AvgResponseTime,
                Tags = supplier.Tags,
                Comments = supplier.Comments,
                CurrencyId = supplier.CurrencyId,
                CurrencyName = supplier.Currency != null ? supplier.Currency.CurrencyName : null, 
                Buid = supplier.Buid,
                BusinessUnitName = supplier.Bu != null ? supplier.Bu.BusinessUnitName : null,
                IsActive = supplier.IsActive,
                CreatedBy = supplier.CreatedBy,
                CreatedOn = supplier.CreatedOn,
                ModifiedBy = supplier.ModifiedBy,
                ModifiedOn = supplier.ModifiedOn
            };
        }

        [HttpGet("search")]
        public async Task<ActionResult<List<SupplierSearchResultDTO>>> Search(
            [FromQuery] string? searchTerm,
            [FromQuery] string? productCategory,
            [FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                var suppliers = await _repository.SearchSuppliersAsync(searchTerm, productCategory, targetBUId);
                return Ok(suppliers);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error searching suppliers: {ex.Message}");
            }
        }

        // External Web Search Endpoint
        // SEC-15: was [AllowAnonymous] (unauthenticated outbound-search / SSRF surface).
        // Now requires authentication like the rest of the controller.
        [HttpGet("web-search")]
        public async Task<ActionResult<List<SupplierSearchResultDTO>>> WebSearch([FromQuery] string query)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(query))
                    return BadRequest("Search query is required.");

                var results = await _repository.SearchWebSuppliersAsync(query);
                return Ok(results);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error performing web search: {ex.Message}");
            }
        }

        // Compose Quote Email
        [HttpPost("compose-quote-email")]
        public ActionResult<QuoteEmailTemplateDTO> ComposeQuoteEmail([FromBody] BatchQuoteRequestDTO request)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                var template = GenerateQuoteEmailTemplate(request);
                return Ok(template);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error composing email: {ex.Message}");
            }
        }

        private QuoteEmailTemplateDTO GenerateQuoteEmailTemplate(BatchQuoteRequestDTO request)
        {
            var subject = string.IsNullOrWhiteSpace(request.RfqNumber)
                ? $"Quote Request for {request.Items.Count} Item(s)"
                : $"Quote Request - RFQ #{request.RfqNumber}";

            var body = new System.Text.StringBuilder();
            body.AppendLine($"Dear {request.SupplierName},");
            body.AppendLine();
            body.AppendLine("We would like to request a quotation for the following items:");
            body.AppendLine();
            body.AppendLine("| # | Part Number | Manufacturer | Description | Quantity | UOM |");
            body.AppendLine("|---|-------------|--------------|-------------|----------|-----|");

            int index = 1;
            foreach (var item in request.Items)
            {
                body.AppendLine($"| {index++} | {item.PartNumber ?? "N/A"} | {item.Manufacturer ?? "N/A"} | {item.Description ?? "N/A"} | {item.Quantity} | {item.UnitOfMeasure ?? "EA"} |");
            }

            body.AppendLine();
            if (request.RequiredDate.HasValue)
            {
                body.AppendLine($"Required Date: {request.RequiredDate.Value:yyyy-MM-dd}");
                body.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(request.AdditionalNotes))
            {
                body.AppendLine("Additional Notes:");
                body.AppendLine(request.AdditionalNotes);
                body.AppendLine();
            }

            body.AppendLine("Please provide your best pricing and lead time for the items listed above.");
            body.AppendLine();    
            body.AppendLine("Thank you for your assistance.");
            body.AppendLine();
            body.AppendLine("Best regards");

            return new QuoteEmailTemplateDTO
            {
                Subject = subject,
                Body = body.ToString(),
                ToEmail = request.SupplierEmail,
                Items = request.Items
            };
        }
    }
}