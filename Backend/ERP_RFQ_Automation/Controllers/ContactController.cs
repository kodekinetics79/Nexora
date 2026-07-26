using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.DTOs.Contact;
using ERP_RFQ_Automation.DTOs.CustomerDTOs;
using ERP_RFQ_Automation.DTOs.SupplierDTOs;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace ERP_RFQ_Automation.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class ContactController : ControllerBase
    {
        private readonly IContactRepository _repository;
        private readonly ErpRfqAutomationContext _context;
        private static readonly int[] AllowedPageSizes = { 5, 10, 25, 50 };

        public ContactController(IContactRepository repository, ErpRfqAutomationContext context)
        {
            _repository = repository;
            _context = context;
        }

        // GET: api/Contact?pageNumber=1&pageSize=10&id=1&firstName=john&lastName=doe&email=john@example.com&customerId=1&supplierId=1&isPrimary=true&isActive=true&businessUnitId=1
        [HttpGet]
        [RequireModulePermission("Customers", PermissionAction.View)]
        public async Task<ActionResult<DTOs.Contact.PaginatedResponseDTO<ContactResponseDTO>>> GetAll(
            [FromQuery] int pageNumber = 1,
            [FromQuery] int pageSize = 10,
            [FromQuery] long? id = null,
            [FromQuery] string? firstName = null,
            [FromQuery] string? lastName = null,
            [FromQuery] string? email = null,
            [FromQuery] long? customerId = null,
            [FromQuery] long? supplierId = null,
            [FromQuery] bool? isPrimary = null,
            [FromQuery] bool? isActive = null)
        {
            try
            {
                if (!TryGetAuthenticatedTenant(out var businessUnitId))
                    return Forbid();

                if (pageNumber < 1)
                    return BadRequest("Page number must be greater than or equal to 1.");
                
                // Relaxed validation: Allow any page size up to 1000
                if (pageSize < 1 || pageSize > 1000)
                    return BadRequest("Page size must be between 1 and 1000.");

                var (contacts, totalCount) = await _repository.GetAllAsync(pageNumber, pageSize, id, firstName, lastName, email, customerId, supplierId, isPrimary, isActive, businessUnitId);

                var response = new DTOs.Contact.PaginatedResponseDTO<ContactResponseDTO>
                {
                    Items = contacts,
                    TotalCount = totalCount,
                    PageNumber = pageNumber,
                    PageSize = pageSize
                };

                return Ok(response);
            }
            catch (Exception)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, "Unable to retrieve contacts.");
            }
        }

        // GET: api/Contact/5
        [HttpGet("{id}")]
        [RequireModulePermission("Customers", PermissionAction.View)]
        public async Task<ActionResult<ContactResponseDTO>> GetById(long id)
        {
            try
            {
                if (!TryGetAuthenticatedTenant(out var businessUnitId))
                    return Forbid();

                var contact = await _repository.GetByIdAsync(id, businessUnitId);
                return Ok(MapToResponse(contact));
            }
            catch (KeyNotFoundException)
            {
                return NotFound("Contact not found.");
            }
            catch (Exception)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, "Unable to retrieve the contact.");
            }
        }

        // POST: api/Contact
        [HttpPost]
        [RequireModulePermission("Customers", PermissionAction.Create)]
        public async Task<ActionResult<ContactResponseDTO>> Create([FromBody] ContactCreateRequestDTO request)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                if (!TryGetAuthenticatedTenant(out var businessUnitId))
                    return Forbid();

                if (!await ParentBelongsToTenantAsync(request.CustomerId, request.SupplierId, businessUnitId))
                    return BadRequest("The contact parent is invalid for the authenticated tenant.");

                var contact = new Contact
                {
                    BusinessUnitId = businessUnitId,
                    CustomerId = request.CustomerId,
                    SupplierId = request.SupplierId,
                    FirstName = request.FirstName,
                    MiddleName = request.MiddleName,
                    LastName = request.LastName,
                    Email = request.Email,
                    PhoneNo = request.PhoneNo,
                    MobileNo = request.MobileNo,
                    Position = request.Position,
                    IsPrimary = request.IsPrimary,
                    IsActive = request.IsActive ?? true,
                    CreatedBy = GetAuthenticatedActor(),
                    CreatedOn = DateTime.UtcNow
                };

                await _repository.AddAsync(contact);

                var response = MapToResponse(contact);
                return CreatedAtAction(nameof(GetById), new { id = contact.Id }, response);
            }
            catch (ArgumentException)
            {
                return BadRequest("The contact request is invalid.");
            }
            catch (Exception)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, "Unable to create the contact.");
            }
        }

        // PUT: api/Contact/5
        [HttpPut("{id}")]
        [RequireModulePermission("Customers", PermissionAction.Edit)]
        public async Task<ActionResult> Update(long id, [FromBody] ContactUpdateRequestDTO request)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                if (!TryGetAuthenticatedTenant(out var businessUnitId))
                    return Forbid();

                var existing = await _context.Contacts.AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Id == id && c.BusinessUnitId == businessUnitId);
                if (existing == null)
                    return NotFound($"Contact with ID {id} not found.");

                if (!await ParentBelongsToTenantAsync(request.CustomerId, request.SupplierId, businessUnitId))
                    return BadRequest("The contact parent is invalid for the authenticated tenant.");

                var updated = await _repository.GetByIdAsync(id, businessUnitId);
                updated.CustomerId = request.CustomerId;
                updated.SupplierId = request.SupplierId;
                updated.FirstName = request.FirstName;
                updated.MiddleName = request.MiddleName;
                updated.LastName = request.LastName;
                updated.Email = request.Email;
                updated.PhoneNo = request.PhoneNo;
                updated.MobileNo = request.MobileNo;
                updated.Position = request.Position;
                updated.IsPrimary = request.IsPrimary;
                updated.IsActive = request.IsActive ?? true;
                updated.ModifiedBy = GetAuthenticatedActor();
                updated.ModifiedOn = DateTime.UtcNow;

                await _repository.UpdateAsync(updated);

                return NoContent();
            }
            catch (KeyNotFoundException)
            {
                return NotFound("Contact not found.");
            }
            catch (ArgumentException)
            {
                return BadRequest("The contact request is invalid.");
            }
            catch (Exception)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, "Unable to update the contact.");
            }
        }

        // DELETE: api/Contact/5
        [HttpDelete("{id}")]
        [RequireModulePermission("Customers", PermissionAction.Delete)]
        public async Task<ActionResult> Delete(long id)
        {
            try
            {
                if (!TryGetAuthenticatedTenant(out var businessUnitId))
                    return Forbid();

                await _repository.DeleteAsync(id, businessUnitId);
                return NoContent();
            }
            catch (KeyNotFoundException)
            {
                return NotFound("Contact not found.");
            }
            catch (InvalidOperationException)
            {
                return BadRequest("The contact cannot be deleted.");
            }
            catch (Exception)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, "Unable to delete the contact.");
            }
        }

        // GET: api/Contact/Customers
        [HttpGet("Customers")]
        [RequireModulePermission("Customers", PermissionAction.View)]
        public async Task<ActionResult<IEnumerable<CustomerDropdown>>> GetCustomers()
        {
            try
            {
                if (!TryGetAuthenticatedTenant(out var businessUnitId))
                    return Forbid();

                var customers = await _repository.GetCustomersAsync(businessUnitId);
                return Ok(customers);
            }
            catch (Exception)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, "Unable to retrieve customers.");
            }
        }

        // GET: api/Contact/Suppliers
        [HttpGet("Suppliers")]
        [RequireModulePermission("Customers", PermissionAction.View)]
        public async Task<ActionResult<IEnumerable<SupplierDropDown>>> GetSuppliers()
        {
            try
            {
                if (!TryGetAuthenticatedTenant(out var businessUnitId))
                    return Forbid();

                var suppliers = await _repository.GetSuppliersAsync(businessUnitId);
                return Ok(suppliers);
            }
            catch (Exception)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, "Unable to retrieve suppliers.");
            }
        }

        private ContactResponseDTO MapToResponse(Contact contact)
        {
            return new ContactResponseDTO
            {
                Id = contact.Id,
                CustomerId = contact.CustomerId,
                CustomerName = contact.Customer != null ? contact.Customer.Name : null,
                SupplierId = contact.SupplierId,
                SupplierName = contact.Supplier != null ? contact.Supplier.Name : null,
                FirstName = contact.FirstName,
                MiddleName = contact.MiddleName,
                LastName = contact.LastName,
                Email = contact.Email,
                PhoneNo = contact.PhoneNo,
                MobileNo = contact.MobileNo,
                Position = contact.Position,
                IsPrimary = contact.IsPrimary,
                IsActive = contact.IsActive,
                CreatedBy = contact.CreatedBy,
                CreatedOn = contact.CreatedOn,
                ModifiedBy = contact.ModifiedBy,
                ModifiedOn = contact.ModifiedOn
            };
        }

        private bool TryGetAuthenticatedTenant(out long businessUnitId)
        {
            return long.TryParse(User.FindFirst("businessUnitId")?.Value, out businessUnitId)
                && businessUnitId > 0;
        }

        private string GetAuthenticatedActor()
        {
            return User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? User.FindFirstValue(ClaimTypes.Email)
                ?? User.Identity?.Name
                ?? "authenticated-user";
        }

        private async Task<bool> ParentBelongsToTenantAsync(
            long? customerId,
            long? supplierId,
            long businessUnitId)
        {
            if (customerId.HasValue == supplierId.HasValue)
                return false;

            if (customerId.HasValue)
                return await _context.Customers.AsNoTracking()
                    .AnyAsync(c => c.Id == customerId.Value && c.Buid == businessUnitId);

            return await _context.Suppliers.AsNoTracking()
                .AnyAsync(s => s.Id == supplierId!.Value && s.Buid == businessUnitId);
        }
    }
}
