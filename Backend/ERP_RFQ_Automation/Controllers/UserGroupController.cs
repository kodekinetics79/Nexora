using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.DTOs.UserGroup;
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
    public class UserGroupController : ControllerBase
    {
        private readonly IUserGroupRepository _repository;
        private static readonly int[] AllowedPageSizes = { 5, 10, 25, 50 };

        public UserGroupController(IUserGroupRepository repository)
        {
            _repository = repository;
        }

        // GET: api/UserGroup?pageNumber=1&pageSize=10&id=1&userGroupsName=admin
        [HttpGet]
        [RequireModulePermission("Users", PermissionAction.View)]
        public async Task<ActionResult<PaginatedResponseDTO<UserGroupResponseDTO>>> GetAll(
            [FromQuery] long? businessUnitId = null,
            [FromQuery] int pageNumber = 1,
            [FromQuery] int pageSize = 10,
            [FromQuery] long? id = null,
            [FromQuery] string? userGroupsName = null)
        {
            try
            {
                // SEC: claim-only tenant scope — the businessUnitId query value is ignored.
                var targetBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                if (pageNumber < 1)
                    return BadRequest("Page number must be greater than or equal to 1.");
                
                // Relaxed validation: Allow any page size up to 1000
                if (pageSize < 1 || pageSize > 1000)
                    return BadRequest("Page size must be between 1 and 1000.");

                var (userGroups, totalCount) = await _repository.GetAllAsync(pageNumber, pageSize, id, userGroupsName, targetBUId);
                var userGroupDTOs = userGroups.Select(MapToResponse).ToList();

                var response = new PaginatedResponseDTO<UserGroupResponseDTO>
                {
                    Items = userGroupDTOs,
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

        // GET: api/UserGroup/5
        [HttpGet("{id}")]
        [RequireModulePermission("Users", PermissionAction.View)]
        public async Task<ActionResult<UserGroupResponseDTO>> GetById(long id, [FromQuery] long? businessUnitId = null)
        {
            try
            {
                // SEC: claim-only tenant scope — the businessUnitId query value is ignored.
                var targetBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                var userGroup = await _repository.GetByIdAsync(id, targetBUId);
                return Ok(MapToResponse(userGroup));
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

        // POST: api/UserGroup
        [HttpPost]
        [RequireModulePermission("Users", PermissionAction.Create)]
        public async Task<ActionResult<UserGroupResponseDTO>> Create([FromBody] UserGroupCreateRequestDTO request)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                // SEC: claim-only tenant scope — a client-supplied BusinessUnitId is overwritten.
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                if (claimBUId <= 0) return Forbid();
                request.BusinessUnitId = claimBUId;

                var userGroup = new UserGroup
                {
                    UserGroupsName = request.UserGroupsName,
                    BusinessUnitId = request.BusinessUnitId,
                    CreatedBy = User.Identity?.Name ?? request.CreatedBy ?? "System",
                    CreatedOn = DateTime.UtcNow
                };

                await _repository.AddAsync(userGroup);

                var response = MapToResponse(userGroup);
                return CreatedAtAction(nameof(GetById), new { id = userGroup.Id, businessUnitId = userGroup.BusinessUnitId }, response);
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

        // PUT: api/UserGroup/5
        [HttpPut("{id}")]
        [RequireModulePermission("Users", PermissionAction.Edit)]
        public async Task<ActionResult> Update(long id, [FromBody] UserGroupUpdateRequestDTO request)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                // SEC: claim-only tenant scope — a client-supplied BusinessUnitId is overwritten.
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                if (claimBUId <= 0) return Forbid();
                request.BusinessUnitId = claimBUId;

                var existing = await _repository.GetByIdAsync(id, request.BusinessUnitId);

                existing.UserGroupsName = request.UserGroupsName;
                existing.BusinessUnitId = request.BusinessUnitId;
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

        // DELETE: api/UserGroup/5
        [HttpDelete("{id}")]
        [RequireModulePermission("Users", PermissionAction.Delete)]
        public async Task<ActionResult> Delete(long id, [FromQuery] long? businessUnitId = null)
        {
            try
            {
                // SEC: claim-only tenant scope — the businessUnitId query value is ignored.
                var targetBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");

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

        private UserGroupResponseDTO MapToResponse(UserGroup userGroup)
        {
            return new UserGroupResponseDTO
            {
                Id = userGroup.Id,
                UserGroupsName = userGroup.UserGroupsName,
                BusinessUnitId = userGroup.BusinessUnitId,
                CreatedBy = userGroup.CreatedBy,
                CreatedOn = userGroup.CreatedOn,
                ModifiedBy = userGroup.ModifiedBy,
                ModifiedOn = userGroup.ModifiedOn
            };
        }
    }
}