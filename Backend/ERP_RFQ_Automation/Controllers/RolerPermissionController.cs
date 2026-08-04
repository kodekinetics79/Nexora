using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.DTOs.RolePermission;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace ERP_RFQ_Automation.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class RolePermissionController : ControllerBase
    {
        private readonly IRolePermissionRepository _repository;
        private readonly IRoleGate _roleGate;

        public RolePermissionController(IRolePermissionRepository repository, IRoleGate roleGate)
        {
            _repository = repository;
            _roleGate = roleGate;
        }

        // GET: api/RolePermission?pageNumber=1&pageSize=10&id=1&roleId=1&moduleId=1&businessUnitId=1
        [HttpGet]
        [RequireModulePermission("Roles & Permissions", PermissionAction.View)]
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
                if (!TryTenant(out var targetBUId))
                    return BadRequest("A valid businessUnitId claim is required.");

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
        [RequireModulePermission("Roles & Permissions", PermissionAction.View)]
        public async Task<ActionResult<RolePermissionResponseDTO>> GetById(long id, [FromQuery] long? businessUnitId = null)
        {
            try
            {
                if (!TryTenant(out var targetBUId))
                    return BadRequest("A valid businessUnitId claim is required.");

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
        [RequireModulePermission("Roles & Permissions", PermissionAction.Create)]
        public async Task<ActionResult<RolePermissionResponseDTO>> Create([FromBody] RolePermissionCreateRequestDTO request)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                if (!TryTenant(out var claimBUId))
                    return BadRequest("A valid businessUnitId claim is required.");
                request.BusinessUnitId = claimBUId;

                // SEC-C1: the module gate above only proves the caller may administer RBAC at
                // all. It does NOT stop the caller pointing the grant at their OWN role (self
                // escalation) or at a role that outranks them. Mirrors UserController's
                // CanManageRoleAsync guard.
                if (!await CanManageRoleGrantAsync(request.RoleId, claimBUId))
                    return Forbid();
                if (!await CanGrantAtMostOwnPermissionsAsync(
                        request.ModuleId, request.CanCreate, request.CanEdit, request.CanDelete, claimBUId))
                    return Forbid();

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
        [RequireModulePermission("Roles & Permissions", PermissionAction.Edit)]
        public async Task<ActionResult> Update(long id, [FromBody] RolePermissionUpdateRequestDTO request)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                if (!TryTenant(out var claimBUId))
                    return BadRequest("A valid businessUnitId claim is required.");
                request.BusinessUnitId = claimBUId;

                var existing = await _repository.GetByIdAsync(id, request.BusinessUnitId);

                // SEC-C1: the caller must be allowed to manage BOTH the role the row currently
                // targets and the role it is being repointed at, and may never widen a grant
                // beyond what their own role already holds.
                if (!await CanManageRoleGrantAsync(existing.RoleId, claimBUId))
                    return Forbid();
                if (!await CanManageRoleGrantAsync(request.RoleId, claimBUId))
                    return Forbid();
                if (!await CanGrantAtMostOwnPermissionsAsync(
                        request.ModuleId, request.CanCreate, request.CanEdit, request.CanDelete, claimBUId))
                    return Forbid();

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
        [RequireModulePermission("Roles & Permissions", PermissionAction.Delete)]
        public async Task<ActionResult> Delete(long id, [FromQuery] long? businessUnitId = null)
        {
            try
            {
                if (!TryTenant(out var targetBUId))
                    return BadRequest("A valid businessUnitId claim is required.");

                // SEC-C1: deleting a permission row is a privilege change on the target role.
                // Revoking a higher role's grant is an attack (denial of administration), so
                // the same role gate applies as for create/update.
                var existing = await _repository.GetByIdAsync(id, targetBUId);
                if (!await CanManageRoleGrantAsync(existing.RoleId, targetBUId))
                    return Forbid();

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

        private bool TryTenant(out long businessUnitId) =>
            long.TryParse(User.FindFirstValue("businessUnitId"), out businessUnitId) && businessUnitId > 0;

        private long CallerRoleId() =>
            long.TryParse(User.FindFirst("roleId")?.Value, out var roleId) ? roleId : 0;

        /// <summary>
        /// SEC-C1: an authenticated user must never be able to hand their own roleId — or any
        /// role that outranks them — a new permission row. Follows the
        /// <see cref="UserController"/> CanManageRoleAsync pattern and delegates the
        /// rank comparison to the shared <see cref="IRoleGate"/>.
        /// Fail-closed: a missing/unparseable roleId claim, or a row with no role, is denied.
        /// </summary>
        private async Task<bool> CanManageRoleGrantAsync(long? targetRoleId, long businessUnitId)
        {
            if (!targetRoleId.HasValue) return false;

            var callerRoleId = CallerRoleId();
            if (callerRoleId <= 0) return false;

            // Super admins own the tenant and must keep the ability to repair their own grants.
            if (await _roleGate.IsSuperAdminAsync(callerRoleId, businessUnitId)) return true;

            // Everyone else: no self-grant. IRoleGate.CanManageRoleAsync compares a role
            // against itself favourably, so the self case has to be rejected here.
            if (targetRoleId.Value == callerRoleId) return false;

            return await _roleGate.CanManageRoleAsync(callerRoleId, targetRoleId, businessUnitId);
        }

        /// <summary>
        /// SEC-C1: a caller may not grant a permission they do not themselves hold on that
        /// module (privilege escalation by proxy — grant it to a subordinate role, then have
        /// that role act). Super admins bypass, matching <see cref="Authorization.PermissionHandler"/>.
        /// </summary>
        private async Task<bool> CanGrantAtMostOwnPermissionsAsync(
            long moduleId, bool? canCreate, bool? canEdit, bool? canDelete, long businessUnitId)
        {
            var callerRoleId = CallerRoleId();
            if (callerRoleId <= 0) return false;
            if (await _roleGate.IsSuperAdminAsync(callerRoleId, businessUnitId)) return true;

            var (callerRows, _) = await _repository.GetAllAsync(
                pageNumber: 1, pageSize: 1, id: null, roleId: callerRoleId, moduleId: moduleId,
                businessUnitId: businessUnitId);
            var callerRow = callerRows.FirstOrDefault();

            // No row at all → the caller cannot even view that module, so cannot delegate it.
            if (callerRow == null) return false;

            if (canCreate == true && callerRow.CanCreate != true) return false;
            if (canEdit == true && callerRow.CanEdit != true) return false;
            if (canDelete == true && callerRow.CanDelete != true) return false;
            return true;
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
