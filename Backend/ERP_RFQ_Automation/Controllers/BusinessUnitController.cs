using ERP_RFQ_Automation.DTOs.BusinessUnit;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Authorization;
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
        [RequireModulePermission("Business Units", PermissionAction.View)]
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

                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                if (claimBUId <= 0) return Forbid();
                if (id.HasValue && id.Value != claimBUId) return Forbid();
                var (businessUnits, totalCount) = await _repository.GetAllAsync(pageNumber, pageSize, claimBUId, businessUnitName);
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
        [RequireModulePermission("Business Units", PermissionAction.View)]
        public async Task<ActionResult<BusinessUnitResponseDTO>> GetById(long id)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                if (claimBUId <= 0 || id != claimBUId) return Forbid();
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
        public async Task<ActionResult<IEnumerable<BusinessUnitResponseDTO>>> GetDropdown()
        {
            try
            {
                // SEC-H3: this used to call GetAllAsync(1, 1000, null, null), which applies no
                // tenant predicate — BusinessUnit carries no EF global query filter, so any
                // authenticated user of any tenant enumerated every tenant on the platform.
                // The dropdown only ever needs the caller's own business unit; scope it to the
                // businessUnitId claim and fail closed when that claim is absent.
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                if (claimBUId <= 0) return Forbid();

                var (businessUnits, _) = await _repository.GetAllAsync(1, 1000, claimBUId, null);
                var response = businessUnits.Select(MapToResponse).ToList();
                return Ok(response);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error retrieving data: {ex.Message}");
            }
        }

        [HttpPost]
        [RequireModulePermission("Business Units", PermissionAction.Create)]
        public ActionResult<BusinessUnitResponseDTO> Create([FromBody] BusinessUnitCreateRequestDTO request)
        {
            // Tenant identities cannot provision tenants/business units. The governed
            // platform control plane owns that lifecycle.
            return Forbid();
        }

        [HttpPut("{id}")]
        [RequireModulePermission("Business Units", PermissionAction.Edit)]
        public async Task<ActionResult> Update(long id, [FromBody] BusinessUnitUpdateRequestDTO request)
        {
            await Task.CompletedTask;
            return Forbid();
        }
        // DELETE: api/BusinessUnit/5
        [HttpDelete("{id}")]
        [RequireModulePermission("Business Units", PermissionAction.Delete)]
        public ActionResult Delete(long id)
        {
            return Forbid();
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
