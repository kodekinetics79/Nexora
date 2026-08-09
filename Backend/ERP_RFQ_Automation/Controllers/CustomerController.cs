using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.DTOs.CustomerDTOs;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Security.DocumentInspection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.IO;
using System.Security.Claims;

namespace ERP_RFQ_Automation.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class CustomerController : ControllerBase
    {
        private readonly ICustomerRepository _repository;
        private readonly IWebHostEnvironment _environment;
        private readonly IFileInspectionService _fileInspection;
        private static readonly int[] AllowedPageSizes = { 5, 10, 25, 50, 100, 1000 };

        public CustomerController(
            ICustomerRepository repository,
            IWebHostEnvironment environment,
            IFileInspectionService fileInspection)
        {
            _repository = repository;
            _environment = environment;
            _fileInspection = fileInspection;
        }

        // GET: api/Customer?pageNumber=1&pageSize=10&id=1&name=abc&contactEmail=abc@example.com&isActive=true&docId=CU00000001

        [HttpGet]
        [RequireModulePermission("Customers", PermissionAction.View)]
        public async Task<ActionResult<DTOs.CustomerDTOs.PaginatedResponseDTO<CustomerResponseDTO>>> GetAll(
            [FromQuery] int pageNumber = 1,
            [FromQuery] int pageSize = 10,
            [FromQuery] long? id = null,
            [FromQuery] string? name = null,
            [FromQuery] string? contactEmail = null,
            [FromQuery] bool? isActive = null,
            [FromQuery] string? docId = null)
        { 
            try
            {
                if (!TryGetAuthenticatedBusinessUnitId(out var targetBUId))
                    return BadRequest("Business Unit ID is required.");
                
                if (pageNumber < 1)
                    return BadRequest("Page number must be greater than or equal to 1.");
                // Relaxed validation: Allow any page size up to 1000
                if (pageSize < 1 || pageSize > 1000)
                     return BadRequest("Page size must be between 1 and 1000.");

                var (customers, totalCount) = await _repository.GetAllAsync(pageNumber, pageSize, id, name, contactEmail, isActive, docId, targetBUId);

                var response = new DTOs.CustomerDTOs.PaginatedResponseDTO<CustomerResponseDTO>
                {
                    Items = customers,
                    TotalCount = totalCount,
                    PageNumber = pageNumber,
                    PageSize = pageSize
                };

                return Ok(response);
            }
            catch (Exception)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, "Unable to retrieve customers.");
            }
        }

        // GET: api/Customer/5
        [HttpGet("{id}")]
        [RequireModulePermission("Customers", PermissionAction.View)]
        public async Task<ActionResult<CustomerResponseDTO>> GetById(long id)
        {
            try
            {
                if (!TryGetAuthenticatedBusinessUnitId(out var targetBUId))
                    return BadRequest("Business Unit ID is required.");

                var customer = await _repository.GetByIdAsync(id, targetBUId);
                return Ok(MapToResponse(customer));
            }
            catch (KeyNotFoundException)
            {
                return NotFound("Customer not found.");
            }
            catch (Exception)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, "Unable to retrieve the customer.");
            }
        }

        [HttpGet("by-email")]
        [RequireModulePermission("Customers", PermissionAction.View)]
        public async Task<ActionResult<CustomerResponseDTO?>> GetByEmail([FromQuery] string email)
        {
            try
            {
                if (!long.TryParse(User.FindFirst("businessUnitId")?.Value, out var businessUnitId) || businessUnitId <= 0)
                    return BadRequest("Business Unit ID is required.");

                var customer = await _repository.GetByEmailAsync(email, businessUnitId);
                if (customer == null) return Ok(null);
                return Ok(MapToResponse(customer));
            }
            catch (Exception)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, "Unable to retrieve the customer.");
            }
        }

        // POST: api/Customer
        [HttpPost]
        [RequireModulePermission("Customers", PermissionAction.Create)]
        public async Task<ActionResult<CustomerResponseDTO>> Create([FromForm] CustomerCreateRequestDTO request)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                if (!TryGetAuthenticatedBusinessUnitId(out var businessUnitId))
                    return BadRequest("Business Unit ID is required.");

                string? imagePath = null;
                if (request.ImageFile != null)
                    imagePath = await SaveCustomerImageAsync(request.ImageFile, HttpContext.RequestAborted);

                var customer = new Customer
                {
                    Name = request.Name,
                    ContactEmail = request.ContactEmail,
                    ImageUrl = imagePath ?? request.ImageUrl ?? string.Empty, 
                    BillingAddressLine1 = request.BillingAddressLine1,
                    BillingAddressLine2 = request.BillingAddressLine2,
                    BillingCity = request.BillingCity,
                    BillingState = request.BillingState,
                    BillingCountry = request.BillingCountry,
                    BillingPostalCode = request.BillingPostalCode,
                    ShippingAddressLine1 = request.ShippingAddressLine1,
                    ShippingAddressLine2 = request.ShippingAddressLine2,
                    ShippingCity = request.ShippingCity,
                    ShippingState = request.ShippingState,
                    ShippingCountry = request.ShippingCountry,
                    ShippingPostalCode = request.ShippingPostalCode,
                    IsActive = request.IsActive ?? true
                };

                await _repository.AddAsync(customer, businessUnitId, AuthenticatedActor());

                var response = MapToResponse(customer);
                return CreatedAtAction(nameof(GetById), new { id = customer.Id }, response);
            }
            catch (CustomerIdentityConflictException)
            {
                return Conflict("The email or phone number is already linked to another customer in this tenant.");
            }
            catch (ArgumentException)
            {
                return BadRequest("The customer request is invalid.");
            }
            catch (DbUpdateException)
            {
                return Conflict("A customer identity conflict prevented this change. Reload and try again.");
            }
            catch (Exception)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, "Unable to create the customer.");
            }
        }

        // PUT: api/Customer/5
        [HttpPut("{id}")]
        [RequireModulePermission("Customers", PermissionAction.Edit)]
        public async Task<ActionResult> Update(long id, [FromForm] CustomerUpdateRequestDTO request)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                if (!TryGetAuthenticatedBusinessUnitId(out var targetBUId))
                    return BadRequest("Business Unit ID is required.");
                if (!request.ConcurrencyToken.HasValue || request.ConcurrencyToken == Guid.Empty)
                    return BadRequest("A concurrency token is required.");

                var existing = await _repository.GetByIdAsync(id, targetBUId);

                string? imagePath = existing.ImageUrl;
                if (request.ImageFile != null)
                    imagePath = await SaveCustomerImageAsync(request.ImageFile, HttpContext.RequestAborted);

                existing.Name = request.Name;
                existing.ContactEmail = request.ContactEmail;
                existing.ImageUrl = imagePath ?? request.ImageUrl ?? string.Empty;
                existing.BillingAddressLine1 = request.BillingAddressLine1;
                existing.BillingAddressLine2 = request.BillingAddressLine2;
                existing.BillingCity = request.BillingCity;
                existing.BillingState = request.BillingState;
                existing.BillingCountry = request.BillingCountry;
                existing.BillingPostalCode = request.BillingPostalCode;
                existing.ShippingAddressLine1 = request.ShippingAddressLine1;
                existing.ShippingAddressLine2 = request.ShippingAddressLine2;
                existing.ShippingCity = request.ShippingCity;
                existing.ShippingState = request.ShippingState;
                existing.ShippingCountry = request.ShippingCountry;
                existing.ShippingPostalCode = request.ShippingPostalCode;
                await _repository.UpdateAsync(
                    existing, targetBUId, AuthenticatedActor(), request.ConcurrencyToken.Value);

                return NoContent();
            }
            catch (KeyNotFoundException)
            {
                return NotFound("Customer not found.");
            }
            catch (CustomerIdentityConflictException)
            {
                return Conflict("The email or phone number is already linked to another customer in this tenant.");
            }
            catch (ArgumentException)
            {
                return BadRequest("The customer request is invalid.");
            }
            catch (DbUpdateConcurrencyException)
            {
                return Conflict("The customer was changed by another user. Reload it and try again.");
            }
            catch (DbUpdateException)
            {
                return Conflict("A customer identity conflict prevented this change. Reload and try again.");
            }
            catch (Exception)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, "Unable to update the customer.");
            }
        }

        // DELETE: api/Customer/5
        [HttpDelete("{id}")]
        [RequireModulePermission("Customers", PermissionAction.Delete)]
        public async Task<ActionResult> Delete(long id, [FromQuery] Guid concurrencyToken)
        {
            try
            {
                if (!TryGetAuthenticatedBusinessUnitId(out var targetBUId))
                    return BadRequest("Business Unit ID is required.");
                if (concurrencyToken == Guid.Empty)
                    return BadRequest("A concurrency token is required.");

                await _repository.DeleteAsync(id, targetBUId, AuthenticatedActor(), concurrencyToken);
                return NoContent();
            }
            catch (KeyNotFoundException)
            {
                return NotFound("Customer not found.");
            }
            catch (DbUpdateConcurrencyException)
            {
                return Conflict("The customer was changed by another user. Reload it and try again.");
            }
            catch (Exception)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, "Unable to deactivate the customer.");
            }
        }

        private bool TryGetAuthenticatedBusinessUnitId(out long businessUnitId) =>
            long.TryParse(User.FindFirst("businessUnitId")?.Value, out businessUnitId) && businessUnitId > 0;

        private string AuthenticatedActor() =>
            User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.Identity?.Name ?? "authenticated-user";

        private async Task<string> SaveCustomerImageAsync(IFormFile image, CancellationToken cancellationToken)
        {
            await using var content = new MemoryStream();
            await image.CopyToAsync(content, cancellationToken);
            content.Position = 0;
            var inspection = await _fileInspection.InspectAsync(new FileInspectionRequest(
                content, Path.GetFileName(image.FileName), image.ContentType, image.Length), cancellationToken);
            var extension = inspection.DetectedContentType switch
            {
                "image/png" => ".png",
                "image/jpeg" => ".jpg",
                "image/gif" => ".gif",
                "image/bmp" => ".bmp",
                "image/tiff" => ".tiff",
                "image/webp" => ".webp",
                _ => null
            };
            if (!inspection.IsCleared || extension == null)
                throw new ArgumentException("The customer image failed security inspection.");

            var uploadsFolder = Path.Combine(_environment.WebRootPath, "CustomerImages");
            Directory.CreateDirectory(uploadsFolder);
            var storedName = $"{Guid.NewGuid():N}{extension}";
            content.Position = 0;
            await using var output = new FileStream(
                Path.Combine(uploadsFolder, storedName), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await content.CopyToAsync(output, cancellationToken);
            return $"/CustomerImages/{storedName}";
        }

        private CustomerResponseDTO MapToResponse(Customer customer)
        {
            return new CustomerResponseDTO
            {
                Id = customer.Id,
                Name = customer.Name,
                ContactEmail = customer.ContactEmail,
                ImageUrl = customer.ImageUrl,
                DocId = customer.DocId,
                BillingAddressLine1 = customer.BillingAddressLine1,
                BillingAddressLine2 = customer.BillingAddressLine2,
                BillingCity = customer.BillingCity,
                BillingState = customer.BillingState,
                BillingCountry = customer.BillingCountry,
                BillingPostalCode = customer.BillingPostalCode,
                ShippingAddressLine1 = customer.ShippingAddressLine1,
                ShippingAddressLine2 = customer.ShippingAddressLine2,
                ShippingCity = customer.ShippingCity,
                ShippingState = customer.ShippingState,
                ShippingCountry = customer.ShippingCountry,
                ShippingPostalCode = customer.ShippingPostalCode,
                Buid = customer.Buid,
                BusinessUnitName = customer.Bu != null ? customer.Bu.BusinessUnitName : null,
                IsActive = customer.IsActive,
                CreatedBy = customer.CreatedBy,
                CreatedOn = customer.CreatedOn,
                ModifiedBy = customer.ModifiedBy,
                ModifiedOn = customer.ModifiedOn,
                ConcurrencyToken = customer.ConcurrencyToken,
                // AA-01: tenant-defined custom field values, as stored jsonb.
                CustomFields = customer.CustomFieldsJson
            };
        }
    }
}
