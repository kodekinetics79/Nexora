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
            [FromQuery] long? businessUnitId = null,
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
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                if (pageNumber < 1)
                    return BadRequest("Page number must be greater than or equal to 1.");
                
                // Relaxed validation: Allow any page size up to 1000
                if (pageSize < 1 || pageSize > 1000)
                    return BadRequest("Page size must be between 1 and 1000.");

                var (contacts, totalCount) = await _repository.GetAllAsync(pageNumber, pageSize, id, firstName, lastName, email, customerId, supplierId, isPrimary, isActive, targetBUId);

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
        public async Task<ActionResult<ContactResponseDTO>> GetById(long id, [FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                var contact = await _repository.GetByIdAsync(id, targetBUId);
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
                    IsActive = request.IsActive ?? true,
                    CreatedBy = request.CreatedBy,
                    CreatedOn = DateTime.UtcNow
                };

                await _repository.AddAsync(contact);

                var response = MapToResponse(contact);
                // Determine BU ID for route (fetch from parent; assume from repo logic)
                long buId = contact.CustomerId.HasValue ? (await _context.Customers.FindAsync(contact.CustomerId.Value))?.Buid ?? 0 : (await _context.Suppliers.FindAsync(contact.SupplierId.Value))?.Buid ?? 0;
                return CreatedAtAction(nameof(GetById), new { id = contact.Id, businessUnitId = buId }, response);
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

                // Fetch existing to get BU for GetByIdAsync
                var existing = await _context.Contacts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
                if (existing == null)
                    return NotFound($"Contact with ID {id} not found.");

                long buId = existing.CustomerId.HasValue ? (await _context.Customers.FindAsync(existing.CustomerId.Value))?.Buid ?? 0 : (await _context.Suppliers.FindAsync(existing.SupplierId.Value))?.Buid ?? 0;

                var updated = await _repository.GetByIdAsync(id, buId);
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
                updated.ModifiedBy = request.ModifiedBy;
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
        public async Task<ActionResult<IEnumerable<CustomerDropdown>>> GetCustomers([FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                var customers = await _repository.GetCustomersAsync(targetBUId);
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
        public async Task<ActionResult<IEnumerable<SupplierDropDown>>> GetSuppliers([FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                var suppliers = await _repository.GetSuppliersAsync(targetBUId);
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
    }
}
