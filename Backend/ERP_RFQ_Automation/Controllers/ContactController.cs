using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.CommercialRouting;
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
        private readonly IAuthorizationService _authorization;

        public ContactController(
            IContactRepository repository,
            ErpRfqAutomationContext context,
            IAuthorizationService authorization)
        {
            _repository = repository;
            _context = context;
            _authorization = authorization;
        }

        // GET: api/Contact?pageNumber=1&pageSize=10&id=1&firstName=john&lastName=doe&email=john@example.com&customerId=1&supplierId=1&isPrimary=true&isActive=true&businessUnitId=1
        [HttpGet]
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

                if (customerId.HasValue && supplierId.HasValue)
                    return BadRequest("Filter contacts by either customer or supplier, not both.");
                if (id.HasValue)
                {
                    var requested = await _repository.GetByIdAsync(id.Value, businessUnitId);
                    if (!await HasParentPermissionAsync(requested.CustomerId, requested.SupplierId, PermissionAction.View))
                        return Forbid();
                }
                else if (customerId.HasValue || supplierId.HasValue)
                {
                    if (!await HasParentPermissionAsync(customerId, supplierId, PermissionAction.View))
                        return Forbid();
                }
                else if (!await HasModulePermissionAsync("Customers", PermissionAction.View) ||
                         !await HasModulePermissionAsync("Suppliers", PermissionAction.View))
                {
                    return Forbid();
                }

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
            catch (KeyNotFoundException)
            {
                return NotFound("Contact not found.");
            }
            catch (Exception)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, "Unable to retrieve contacts.");
            }
        }

        // GET: api/Contact/5
        [HttpGet("{id}")]
        public async Task<ActionResult<ContactResponseDTO>> GetById(long id)
        {
            try
            {
                if (!TryGetAuthenticatedTenant(out var businessUnitId))
                    return Forbid();

                var contact = await _repository.GetByIdAsync(id, businessUnitId);
                if (!await HasParentPermissionAsync(contact.CustomerId, contact.SupplierId, PermissionAction.View))
                    return Forbid();
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
        public async Task<ActionResult<ContactResponseDTO>> Create([FromBody] ContactCreateRequestDTO request)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                if (!TryGetAuthenticatedTenant(out var businessUnitId))
                    return Forbid();

                if (!await HasParentPermissionAsync(request.CustomerId, request.SupplierId, PermissionAction.Create))
                    return Forbid();

                if (!await ParentBelongsToTenantAsync(
                        request.CustomerId, request.SupplierId, businessUnitId, request.IsActive != false))
                    return BadRequest("The contact parent is invalid for the authenticated tenant.");

                var contact = new Contact
                {
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
                    IsActive = request.IsActive ?? true
                };

                await _repository.AddAsync(contact, businessUnitId, GetAuthenticatedActor());

                var response = MapToResponse(contact);
                return CreatedAtAction(nameof(GetById), new { id = contact.Id }, response);
            }
            catch (CustomerIdentityConflictException)
            {
                return Conflict("The email or phone number is already linked to another customer in this tenant.");
            }
            catch (ArgumentException)
            {
                return BadRequest("The contact request is invalid.");
            }
            catch (DbUpdateException)
            {
                return Conflict("A contact identity conflict prevented this change. Reload and try again.");
            }
            catch (Exception)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, "Unable to create the contact.");
            }
        }

        // PUT: api/Contact/5
        [HttpPut("{id}")]
        public async Task<ActionResult> Update(long id, [FromBody] ContactUpdateRequestDTO request)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                if (!TryGetAuthenticatedTenant(out var businessUnitId))
                    return Forbid();
                if (!request.ConcurrencyToken.HasValue || request.ConcurrencyToken == Guid.Empty)
                    return BadRequest("A concurrency token is required.");

                var updated = await _repository.GetByIdAsync(id, businessUnitId);
                if (!await HasParentPermissionAsync(updated.CustomerId, updated.SupplierId, PermissionAction.Edit))
                    return Forbid();
                if (updated.CustomerId != request.CustomerId || updated.SupplierId != request.SupplierId)
                    return BadRequest("A contact cannot be reassigned to a different customer or supplier.");
                if (!await ParentBelongsToTenantAsync(
                        updated.CustomerId, updated.SupplierId, businessUnitId, updated.IsActive != false))
                    return BadRequest("The contact parent is invalid for the authenticated tenant.");

                updated.FirstName = request.FirstName;
                updated.MiddleName = request.MiddleName;
                updated.LastName = request.LastName;
                updated.Email = request.Email;
                updated.PhoneNo = request.PhoneNo;
                updated.MobileNo = request.MobileNo;
                updated.Position = request.Position;
                updated.IsPrimary = request.IsPrimary;

                await _repository.UpdateAsync(
                    updated, businessUnitId, GetAuthenticatedActor(), request.ConcurrencyToken.Value);

                return NoContent();
            }
            catch (KeyNotFoundException)
            {
                return NotFound("Contact not found.");
            }
            catch (CustomerIdentityConflictException)
            {
                return Conflict("The email or phone number is already linked to another customer in this tenant.");
            }
            catch (ArgumentException)
            {
                return BadRequest("The contact request is invalid.");
            }
            catch (DbUpdateConcurrencyException)
            {
                return Conflict("The contact was changed by another user. Reload it and try again.");
            }
            catch (DbUpdateException)
            {
                return Conflict("A contact identity conflict prevented this change. Reload and try again.");
            }
            catch (Exception)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, "Unable to update the contact.");
            }
        }

        // DELETE: api/Contact/5
        [HttpDelete("{id}")]
        public async Task<ActionResult> Delete(long id, [FromQuery] Guid concurrencyToken)
        {
            try
            {
                if (!TryGetAuthenticatedTenant(out var businessUnitId))
                    return Forbid();
                if (concurrencyToken == Guid.Empty)
                    return BadRequest("A concurrency token is required.");

                var contact = await _repository.GetByIdAsync(id, businessUnitId);
                if (!await HasParentPermissionAsync(contact.CustomerId, contact.SupplierId, PermissionAction.Delete))
                    return Forbid();

                await _repository.DeleteAsync(
                    id, businessUnitId, GetAuthenticatedActor(), concurrencyToken);
                return NoContent();
            }
            catch (KeyNotFoundException)
            {
                return NotFound("Contact not found.");
            }
            catch (DbUpdateConcurrencyException)
            {
                return Conflict("The contact was changed by another user. Reload it and try again.");
            }
            catch (Exception)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, "Unable to deactivate the contact.");
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
        [RequireModulePermission("Suppliers", PermissionAction.View)]
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
                ModifiedOn = contact.ModifiedOn,
                ConcurrencyToken = contact.ConcurrencyToken
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
            long businessUnitId,
            bool requireActive)
        {
            if (customerId.HasValue == supplierId.HasValue)
                return false;

            if (customerId.HasValue)
                return await _context.Customers.AsNoTracking()
                    .AnyAsync(c => c.Id == customerId.Value && c.Buid == businessUnitId &&
                        (!requireActive || c.IsActive != false));

            return await _context.Suppliers.AsNoTracking()
                .AnyAsync(s => s.Id == supplierId!.Value && s.Buid == businessUnitId &&
                    (!requireActive || s.IsActive != false));
        }

        private Task<bool> HasParentPermissionAsync(
            long? customerId,
            long? supplierId,
            PermissionAction action)
        {
            if (customerId.HasValue == supplierId.HasValue)
                return Task.FromResult(false);
            return HasModulePermissionAsync(customerId.HasValue ? "Customers" : "Suppliers", action);
        }

        private async Task<bool> HasModulePermissionAsync(string module, PermissionAction action)
        {
            var result = await _authorization.AuthorizeAsync(
                User, null, $"{RequireModulePermissionAttribute.PolicyPrefix}{module}:{action}");
            return result.Succeeded;
        }
    }
}
