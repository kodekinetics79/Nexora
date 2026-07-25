using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.DTOs.BusinessUnit;
using ERP_RFQ_Automation.DTOs.CurrencyDTOs;
using ERP_RFQ_Automation.DTOs.CustomerDTOs;
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
    public class CustomerController : ControllerBase
    {
        private readonly ICustomerRepository _repository;
        private readonly IWebHostEnvironment _environment;
        private static readonly int[] AllowedPageSizes = { 5, 10, 25, 50, 100, 1000 };

        public CustomerController(ICustomerRepository repository, IWebHostEnvironment environment)
        {
            _repository = repository;
            _environment = environment;
        }

        // GET: api/Customer?pageNumber=1&pageSize=10&id=1&name=abc&contactEmail=abc@example.com&taxId=123&currencyId=1&isActive=true&businessUnitId=1

        [HttpGet]
        [RequireModulePermission("Customers", PermissionAction.View)]
        public async Task<ActionResult<DTOs.CustomerDTOs.PaginatedResponseDTO<CustomerResponseDTO>>> GetAll(
            [FromQuery] long? businessUnitId = null,
            [FromQuery] int pageNumber = 1,
            [FromQuery] int pageSize = 10,
            [FromQuery] long? id = null,
            [FromQuery] string? name = null,
            [FromQuery] string? contactEmail = null,
            [FromQuery] string? taxId = null,
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
        public async Task<ActionResult<CustomerResponseDTO>> GetById(long id, [FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0)
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

                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                if (claimBUId > 0) request.Buid = claimBUId;

                if (request.Buid <= 0) return BadRequest("Business Unit ID is required.");

                string? imagePath = null;
                if (request.ImageFile != null)
                {
                    var uploadsFolder = Path.Combine(_environment.WebRootPath, "CustomerImages");
                    if (!Directory.Exists(uploadsFolder))
                        Directory.CreateDirectory(uploadsFolder);

                    var uniqueFileName = $"{Guid.NewGuid()}_{request.ImageFile.FileName}";
                    var filePath = Path.Combine(uploadsFolder, uniqueFileName);
                    using (var fileStream = new FileStream(filePath, FileMode.Create))
                    {
                        await request.ImageFile.CopyToAsync(fileStream);
                    }
                    imagePath = $"/CustomerImages/{uniqueFileName}";
                }

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
                    Buid = request.Buid,
                    IsActive = request.IsActive ?? true,
                    CreatedBy = request.CreatedBy,
                    CreatedOn = DateTime.UtcNow
                };

                await _repository.AddAsync(customer);

                var response = MapToResponse(customer);
                return CreatedAtAction(nameof(GetById), new { id = customer.Id, businessUnitId = customer.Buid }, response);
            }
            catch (ArgumentException)
            {
                return BadRequest("The customer request is invalid.");
            }
            catch (Exception)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, "Unable to create the customer.");
            }
        }

        // PUT: api/Customer/5
        [HttpPut("{id}")]
        [RequireModulePermission("Customers", PermissionAction.Edit)]
        public async Task<ActionResult> Update(long id, [FromForm] CustomerUpdateRequestDTO request, [FromQuery] long? businessUnitId = null)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                if (claimBUId > 0) request.Buid = claimBUId;

                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? request.Buid);

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                var existing = await _repository.GetByIdAsync(id, targetBUId);

                string? imagePath = existing.ImageUrl;
                if (request.ImageFile != null)
                {
                    var uploadsFolder = Path.Combine(_environment.WebRootPath, "CustomerImages");
                    if (!Directory.Exists(uploadsFolder))
                        Directory.CreateDirectory(uploadsFolder);

                    var uniqueFileName = $"{Guid.NewGuid()}_{request.ImageFile.FileName}";
                    var filePath = Path.Combine(uploadsFolder, uniqueFileName);
                    using (var fileStream = new FileStream(filePath, FileMode.Create))
                    {
                        await request.ImageFile.CopyToAsync(fileStream);
                    }
                    imagePath = $"/CustomerImages/{uniqueFileName}";
                }

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
                existing.Buid = request.Buid;
                existing.IsActive = request.IsActive ?? true;
                existing.ModifiedBy = request.ModifiedBy;
                existing.ModifiedOn = DateTime.UtcNow;

                await _repository.UpdateAsync(existing, targetBUId);

                return NoContent();
            }
            catch (KeyNotFoundException)
            {
                return NotFound("Customer not found.");
            }
            catch (ArgumentException)
            {
                return BadRequest("The customer request is invalid.");
            }
            catch (Exception)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, "Unable to update the customer.");
            }
        }

        // DELETE: api/Customer/5
        [HttpDelete("{id}")]
        [RequireModulePermission("Customers", PermissionAction.Delete)]
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
            catch (KeyNotFoundException)
            {
                return NotFound("Customer not found.");
            }
            catch (InvalidOperationException)
            {
                return BadRequest("The customer cannot be deleted.");
            }
            catch (Exception)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, "Unable to delete the customer.");
            }
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
                ModifiedOn = customer.ModifiedOn
            };
        }
    }
}
