using ERP_RFQ_Automation.DTOs.RolePermission;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class RolePermissionController : ControllerBase
    {
        private readonly IRolePermissionRepository _repository;
        private static readonly int[] AllowedPageSizes = { 5, 10, 25, 50 };

        public RolePermissionController(IRolePermissionRepository repository)
        {
            _repository = repository;
        }

        // GET: api/RolePermission?pageNumber=1&pageSize=10&id=1&roleId=1&moduleId=1&businessUnitId=1
        [HttpGet]
        public async Task<ActionResult<PaginatedResponseDTO<RolePermissionResponseDTO>>> GetAll(
            [FromQuery] long? businessUnitId = null,
            [FromQuery] int pageNumber = 1,
            [FromQuery] int pageSize = 10,
            [FromQuery] long? id = null,
            [FromQuery] long? roleId = null,
            [FromQuery] long? moduleId = null)
        {
            try
            {
                var targetBUId = 1; // Enforce global BUID 1 for RolePermissions

                if (pageNumber < 1)
                    return BadRequest("Page number must be greater than or equal to 1.");
                
                // Relaxed validation: Allow any page size up to 1000
                if (pageSize < 1 || pageSize > 1000)
                    return BadRequest("Page size must be between 1 and 1000.");

                var (rolePermissions, totalCount) = await _repository.GetAllAsync(pageNumber, pageSize, id, roleId, moduleId, targetBUId);
                var rolePermissionDTOs = rolePermissions.Select(MapToResponse).ToList();

                var response = new PaginatedResponseDTO<RolePermissionResponseDTO>
                {
                    Items = rolePermissionDTOs,
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

        // GET: api/RolePermission/5
        [HttpGet("{id}")]
        public async Task<ActionResult<RolePermissionResponseDTO>> GetById(long id, [FromQuery] long? businessUnitId = null)
        {
            try
            {
                var targetBUId = 1; // Enforce global BUID 1 for RolePermissions

                var rolePermission = await _repository.GetByIdAsync(id, targetBUId);
                return Ok(MapToResponse(rolePermission));
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

        // POST: api/RolePermission
        [HttpPost]
        public async Task<ActionResult<RolePermissionResponseDTO>> Create([FromBody] RolePermissionCreateRequestDTO request)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                if (claimBUId > 0) request.BusinessUnitId = claimBUId;

                request.BusinessUnitId = 1;

                var rolePermission = new RolePermission
                {
                    RoleId = request.RoleId,
                    ModuleId = request.ModuleId,
                    BusinessUnitId = request.BusinessUnitId,
                    CanCreate = request.CanCreate,
                    CanEdit = request.CanEdit,
                    CanDelete = request.CanDelete,
                    CreatedBy = User.Identity?.Name ?? request.CreatedBy ?? "System",
                    CreatedOn = DateTime.UtcNow
                };

                await _repository.AddAsync(rolePermission);

                var response = MapToResponse(rolePermission);
                return CreatedAtAction(nameof(GetById), new { id = rolePermission.Id, businessUnitId = rolePermission.BusinessUnitId }, response);
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

        // PUT: api/RolePermission/5
        [HttpPut("{id}")]
        public async Task<ActionResult> Update(long id, [FromBody] RolePermissionUpdateRequestDTO request)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                if (claimBUId > 0) request.BusinessUnitId = claimBUId;

                request.BusinessUnitId = 1;

                var existing = await _repository.GetByIdAsync(id, request.BusinessUnitId);

                existing.RoleId = request.RoleId;
                existing.ModuleId = request.ModuleId;
                existing.BusinessUnitId = request.BusinessUnitId;
                existing.CanCreate = request.CanCreate;
                existing.CanEdit = request.CanEdit;
                existing.CanDelete = request.CanDelete;
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

        // DELETE: api/RolePermission/5
        [HttpDelete("{id}")]
        public async Task<ActionResult> Delete(long id, [FromQuery] long? businessUnitId = null)
        {
            try
            {
                var targetBUId = 1; // Enforce global BUID 1 for RolePermissions

                await _repository.DeleteAsync(id, targetBUId);
                return NoContent();
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ex.Message);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error deleting data: {ex.Message}");
            }
        }

        private RolePermissionResponseDTO MapToResponse(RolePermission rolePermission)
        {
            return new RolePermissionResponseDTO
            {
                Id = rolePermission.Id,
                RoleId = rolePermission.RoleId,
                RoleName = rolePermission.Role != null ? rolePermission.Role.SetupValue : null,
                ModuleId = rolePermission.ModuleId,
                ModuleName = rolePermission.Module != null ? rolePermission.Module.ModuleName : null,
                BusinessUnitId = rolePermission.BusinessUnitId,
                CanCreate = rolePermission.CanCreate,
                CanEdit = rolePermission.CanEdit,
                CanDelete = rolePermission.CanDelete,
                CreatedBy = rolePermission.CreatedBy,
                CreatedOn = rolePermission.CreatedOn,
                ModifiedBy = rolePermission.ModifiedBy,
                ModifiedOn = rolePermission.ModifiedOn
            };
        }
    }
}