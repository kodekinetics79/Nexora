using ERP_RFQ_Automation.DTOs.Warehouse;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;

namespace ERP_RFQ_Automation.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class WarehouseController : ControllerBase
    {
        private readonly IWarehouseRepository _repository;

        public WarehouseController(IWarehouseRepository repository)
        {
            _repository = repository;
        }

        // GET: api/Warehouse?pageNumber=1&pageSize=10&warehouseCode=WH1&warehouseName=Main&location=City&country=USA&isActive=true&businessUnitId=1
        [HttpGet]
        public async Task<ActionResult<PaginatedWarehouseResponseDTO>> GetAll(
            [FromQuery] long? businessUnitId = null,
            [FromQuery] int pageNumber = 1,
            [FromQuery] int pageSize = 10,
            [FromQuery] string? warehouseCode = null,
            [FromQuery] string? warehouseName = null,
            [FromQuery] string? location = null,
            [FromQuery] string? country = null,
            [FromQuery] bool? isActive = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                // Validate pagination parameters
                if (pageNumber < 1)
                    return BadRequest("Page number must be greater than or equal to 1.");
                
                // Relaxed validation: Allow any page size up to 1000
                if (pageSize < 1 || pageSize > 1000)
                    return BadRequest("Page size must be between 1 and 1000.");

                var warehouses = await _repository.GetAllAsync(targetBUId);

                // Apply filters
                var filteredWarehouses = warehouses.AsQueryable();

                if (!string.IsNullOrWhiteSpace(warehouseCode))
                    filteredWarehouses = filteredWarehouses.Where(w => w.WarehouseCode.Contains(warehouseCode, StringComparison.OrdinalIgnoreCase));

                if (!string.IsNullOrWhiteSpace(warehouseName))
                    filteredWarehouses = filteredWarehouses.Where(w => w.WarehouseName.Contains(warehouseName, StringComparison.OrdinalIgnoreCase));

                if (!string.IsNullOrWhiteSpace(location))
                    filteredWarehouses = filteredWarehouses.Where(w => w.Location != null && w.Location.Contains(location, StringComparison.OrdinalIgnoreCase));

                if (!string.IsNullOrWhiteSpace(country))
                    filteredWarehouses = filteredWarehouses.Where(w => w.Country != null && w.Country.Contains(country, StringComparison.OrdinalIgnoreCase));

                if (isActive.HasValue)
                    filteredWarehouses = filteredWarehouses.Where(w => w.IsActive == isActive.Value);

                // Get total items after filtering
                var totalItems = filteredWarehouses.Count();

                // Apply pagination
                var paginatedWarehouses = filteredWarehouses
                    .Skip((pageNumber - 1) * pageSize)
                    .Take(pageSize)
                    .Select(MapToResponse)
                    .ToList();

                var response = new PaginatedWarehouseResponseDTO
                {
                    Items = paginatedWarehouses,
                    TotalItems = totalItems,
                    PageNumber = pageNumber,
                    PageSize = pageSize,
                    TotalPages = (int)Math.Ceiling(totalItems / (double)pageSize)
                };

                return Ok(response);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error retrieving data: {ex.Message}");
            }
        }

        // GET: api/Warehouse/5
        [HttpGet("{id}")]
        public async Task<ActionResult<WarehouseResponseDTO>> GetById(long id, [FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                var warehouse = await _repository.GetByIdAsync(id, targetBUId);
                return Ok(MapToResponse(warehouse));
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

        // POST: api/Warehouse
        [HttpPost]
        public async Task<ActionResult<WarehouseResponseDTO>> Create([FromBody] WarehouseCreateRequestDTO request)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                if (claimBUId > 0) request.BusinessUnitId = claimBUId;

                if (request.BusinessUnitId <= 0) return BadRequest("Business Unit ID is required.");

                var warehouse = new Warehouse
                {
                    WarehouseCode = request.WarehouseCode,
                    WarehouseName = request.WarehouseName,
                    Location = request.Location,
                    AddressLine1 = request.AddressLine1,
                    AddressLine2 = request.AddressLine2,
                    City = request.City,
                    State = request.State,
                    Country = request.Country,
                    PostalCode = request.PostalCode,
                    Capacity = request.Capacity,
                    ManagerName = request.ManagerName,
                    ContactPhone = request.ContactPhone,
                    ContactEmail = request.ContactEmail,
                    BusinessUnitId = request.BusinessUnitId,
                    IsActive = true,
                    CreatedBy = User.Identity?.Name ?? request.CreatedBy ?? "System",
                    CreatedOn = DateTime.UtcNow
                };

                await _repository.AddAsync(warehouse);

                var response = MapToResponse(warehouse);
                return CreatedAtAction(nameof(GetById), new { id = warehouse.Id, businessUnitId = warehouse.BusinessUnitId }, response);
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

        // PUT: api/Warehouse/5
        [HttpPut("{id}")]
        public async Task<ActionResult> Update(long id, [FromBody] WarehouseUpdateRequestDTO request)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                if (claimBUId > 0) request.BusinessUnitId = claimBUId;

                if (request.BusinessUnitId <= 0) return BadRequest("Business Unit ID is required.");

                var existing = await _repository.GetByIdAsync(id, request.BusinessUnitId);

                existing.WarehouseCode = request.WarehouseCode;
                existing.WarehouseName = request.WarehouseName;
                existing.Location = request.Location;
                existing.AddressLine1 = request.AddressLine1;
                existing.AddressLine2 = request.AddressLine2;
                existing.City = request.City;
                existing.State = request.State;
                existing.Country = request.Country;
                existing.PostalCode = request.PostalCode;
                existing.Capacity = request.Capacity;
                existing.ManagerName = request.ManagerName;
                existing.ContactPhone = request.ContactPhone;
                existing.ContactEmail = request.ContactEmail;
                existing.BusinessUnitId = request.BusinessUnitId;
                existing.IsActive = request.IsActive ?? true;
                existing.ModifiedBy = User.Identity?.Name ?? request.ModifiedBy ?? "System";
                existing.ModifiedOn = DateTime.UtcNow;

                await _repository.UpdateAsync(existing);
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

        // DELETE: api/Warehouse/5
        [HttpDelete("{id}")]
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

        private WarehouseResponseDTO MapToResponse(Warehouse warehouse)
        {
            return new WarehouseResponseDTO
            {
                Id = warehouse.Id,
                WarehouseCode = warehouse.WarehouseCode,
                WarehouseName = warehouse.WarehouseName,
                Location = warehouse.Location,
                AddressLine1 = warehouse.AddressLine1,
                AddressLine2 = warehouse.AddressLine2,
                City = warehouse.City,
                State = warehouse.State,
                Country = warehouse.Country,
                PostalCode = warehouse.PostalCode,
                Capacity = warehouse.Capacity,
                ManagerName = warehouse.ManagerName,
                ContactPhone = warehouse.ContactPhone,
                ContactEmail = warehouse.ContactEmail,
                BusinessUnitId = warehouse.BusinessUnitId,
                IsActive = warehouse.IsActive,
                CreatedBy = warehouse.CreatedBy,
                CreatedOn = warehouse.CreatedOn,
                ModifiedBy = warehouse.ModifiedBy,
                ModifiedOn = warehouse.ModifiedOn
            };
        }
    }
}