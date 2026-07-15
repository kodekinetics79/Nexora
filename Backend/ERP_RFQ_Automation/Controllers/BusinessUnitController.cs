using ERP_RFQ_Automation.DTOs.BusinessUnit;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class BusinessUnitController : ControllerBase
    {
        private readonly IBusinessUnitRepository _repository;
        private static readonly int[] AllowedPageSizes = { 5, 10, 25, 50 };

        public BusinessUnitController(IBusinessUnitRepository repository)
        {
            _repository = repository;
        }

        // GET: api/BusinessUnit?pageNumber=1&pageSize=10&id=1&businessUnitName=corp
        [HttpGet]
        public async Task<ActionResult<PaginatedResponseDTO<BusinessUnitResponseDTO>>> GetAll(
            [FromQuery] int pageNumber = 1,
            [FromQuery] int pageSize = 10,
            [FromQuery] long? id = null,
            [FromQuery] string? businessUnitName = null)
        {
            try
            {
                if (pageNumber < 1)
                    return BadRequest("Page number must be greater than or equal to 1.");
                
                // Relaxed validation: Allow any page size up to 1000
                if (pageSize < 1 || pageSize > 1000)
                    return BadRequest("Page size must be between 1 and 1000.");

                var (businessUnits, totalCount) = await _repository.GetAllAsync(pageNumber, pageSize, id, businessUnitName);
                var businessUnitDTOs = businessUnits.Select(MapToResponse).ToList();

                var response = new PaginatedResponseDTO<BusinessUnitResponseDTO>
                {
                    Items = businessUnitDTOs,
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

        // GET: api/BusinessUnit/5
        [HttpGet("{id}")]
        public async Task<ActionResult<BusinessUnitResponseDTO>> GetById(long id)
        {
            try
            {
                var businessUnit = await _repository.GetByIdAsync(id);
                return Ok(MapToResponse(businessUnit));
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

        // GET: api/BusinessUnit/Dropdown
        [HttpGet("Dropdown")]
        [AllowAnonymous]
        public async Task<ActionResult<IEnumerable<BusinessUnitResponseDTO>>> GetDropdown()
        {
            try
            {
                var (businessUnits, _) = await _repository.GetAllAsync(1, 1000, null, null); // Fetch all for dropdown
                var response = businessUnits.Select(MapToResponse).ToList();
                return Ok(response);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error retrieving data: {ex.Message}");
            }
        }

        [HttpPost]
        public async Task<ActionResult<BusinessUnitResponseDTO>> Create([FromBody] BusinessUnitCreateRequestDTO request)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            var businessUnit = new BusinessUnit
            {
                BusinessUnitCode = request.BusinessUnitCode,
                BusinessUnitName = request.BusinessUnitName,
                Description = request.Description,
                IsActive = request.IsActive,
                CreatedBy = User.Identity?.Name ?? request.CreatedBy ?? "System",
                CreatedOn = DateTime.UtcNow
            };

            await _repository.AddAsync(businessUnit);
            var response = MapToResponse(businessUnit);
            return CreatedAtAction(nameof(GetById), new { id = businessUnit.Id }, response);
        }

        [HttpPut("{id}")]
        public async Task<ActionResult> Update(long id, [FromBody] BusinessUnitUpdateRequestDTO request)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            var existing = await _repository.GetByIdAsync(id);
            existing.BusinessUnitCode = request.BusinessUnitCode;
            existing.BusinessUnitName = request.BusinessUnitName;
            existing.Description = request.Description;
            existing.IsActive = request.IsActive;
            existing.ModifiedBy = User.Identity?.Name ?? request.ModifiedBy ?? "System";
            existing.ModifiedOn = DateTime.UtcNow;

            await _repository.UpdateAsync(existing);
            return NoContent();
        }
        // DELETE: api/BusinessUnit/5
        [HttpDelete("{id}")]
        public async Task<ActionResult> Delete(long id)
        {
            try
            {
                await _repository.DeleteAsync(id);
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

        private BusinessUnitResponseDTO MapToResponse(BusinessUnit businessUnit)
        {
            return new BusinessUnitResponseDTO
            {
                Id = businessUnit.Id,
                BusinessUnitCode = businessUnit.BusinessUnitCode,
                BusinessUnitName = businessUnit.BusinessUnitName,
                Description = businessUnit.Description,
                IsActive = businessUnit.IsActive,
                CreatedBy = businessUnit.CreatedBy,
                CreatedOn = businessUnit.CreatedOn,
                ModifiedBy = businessUnit.ModifiedBy,
                ModifiedOn = businessUnit.ModifiedOn
            };
        }
    }
}