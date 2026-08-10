using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.DTOs.ModuleDTOs;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using ERP_RFQ_Automation.Security;

namespace ERP_RFQ_Automation.Controllers
{
    /// <summary>
    /// SEC-H2: <see cref="Module"/> is platform-global reference data — it has no
    /// BusinessUnitId and no EF global query filter, and its rows are the keys that
    /// <see cref="RequireModulePermissionAttribute"/> resolves against. Renaming or deleting
    /// one voids RBAC resolution for EVERY tenant on the platform, so mutations are
    /// restricted to the tenant super-admin role (the highest tenant-plane authority
    /// <see cref="IRoleGate"/> recognises). Reads stay available to RBAC administrators so
    /// the Roles &amp; Permissions matrix can still be rendered.
    /// </summary>
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class ModuleController : ControllerBase
    {
        private readonly IModuleRepository _repository;
        private readonly IRoleGate _roleGate;
        private static readonly int[] AllowedPageSizes = { 5, 10, 25, 50 };

        public ModuleController(IModuleRepository repository, IRoleGate roleGate)
        {
            _repository = repository;
            _roleGate = roleGate;
        }

        // GET: api/Module?pageNumber=1&pageSize=10&id=1&moduleName=admin&isActive=true&businessUnitId=1
        [HttpGet]
        [RequireModulePermission("Roles & Permissions", PermissionAction.View)]
        public async Task<ActionResult<PaginatedResponseDTO<ModuleResponseDTO>>> GetAll(
           
            [FromQuery] int pageNumber = 1,
            [FromQuery] int pageSize = 10,
            [FromQuery] long? id = null,
            [FromQuery] string? moduleName = null,
            [FromQuery] bool? isActive = null)
        {
            try
            {
                

                if (pageNumber < 1)
                    return BadRequest("Page number must be greater than or equal to 1.");
                
                // Relaxed validation: Allow any page size up to 1000
                if (pageSize < 1 || pageSize > 1000)
                    return BadRequest("Page size must be between 1 and 1000.");

                var (modules, totalCount) = await _repository.GetAllAsync(pageNumber, pageSize, id, moduleName, isActive);
                var moduleDTOs = modules.Select(MapToResponse).ToList();

                var response = new PaginatedResponseDTO<ModuleResponseDTO>
                {
                    Items = moduleDTOs,
                    TotalCount = totalCount,
                    PageNumber = pageNumber,
                    PageSize = pageSize
                };

                return Ok(response);
            }
            catch (Exception ex)
            {
                return this.ServerError(ex, "Error retrieving data.");
            }
        }

        // GET: api/Module/5
        [HttpGet("{id}")]
        [RequireModulePermission("Roles & Permissions", PermissionAction.View)]
        public async Task<ActionResult<ModuleResponseDTO>> GetById(long id)
        {
            try
            {
               
                var module = await _repository.GetByIdAsync(id);
                return Ok(MapToResponse(module));
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ex.Message);
            }
            catch (Exception ex)
            {
                return this.ServerError(ex, "Error retrieving data.");
            }
        }

        // POST: api/Module
        [HttpPost]
        [RequireModulePermission("Roles & Permissions", PermissionAction.Create)]
        public async Task<ActionResult<ModuleResponseDTO>> Create([FromBody] ModuleCreateRequestDTO request)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                if (!await IsPlatformReferenceDataAdministratorAsync())
                    return Forbid();

                var module = new Module
                {
                    ModuleName = request.ModuleName,
                    Description = request.Description,
                    IsActive = true, // Always true on create
                    CreatedBy = User.Identity?.Name ?? request.CreatedBy ?? "System",
                    CreatedOn = DateTime.UtcNow
                };

                await _repository.AddAsync(module);

                var response = MapToResponse(module);
                return CreatedAtAction(nameof(GetById), new { id = module.Id }, response);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                return this.ServerError(ex, "Error creating data.");
            }
        }

        // PUT: api/Module/5
        [HttpPut("{id}")]
        [RequireModulePermission("Roles & Permissions", PermissionAction.Edit)]
        public async Task<ActionResult> Update(long id, [FromBody] ModuleUpdateRequestDTO request)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                if (!await IsPlatformReferenceDataAdministratorAsync())
                    return Forbid();

                var existing = await _repository.GetByIdAsync(id);

                existing.ModuleName = request.ModuleName;
                existing.Description = request.Description;
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
                return this.ServerError(ex, "Error updating data.");
            }
        }

        // DELETE: api/Module/5
        [HttpDelete("{id}")]
        [RequireModulePermission("Roles & Permissions", PermissionAction.Delete)]
        public async Task<ActionResult> Delete(long id)
        {
            try
            {
                if (!await IsPlatformReferenceDataAdministratorAsync())
                    return Forbid();

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
                return this.ServerError(ex, "Error deleting data.");
            }
        }

        /// <summary>
        /// SEC-H2: only a tenant super admin may mutate the platform-global Modules table.
        /// Uses the shared <see cref="IRoleGate"/> imperatively (same shape as
        /// <c>UserController.CanManageRoleAsync</c>) rather than a new attribute, so no new
        /// authorization abstraction or DI registration is introduced.
        /// Fail-closed: missing/unparseable roleId or businessUnitId claims are denied.
        /// </summary>
        private async Task<bool> IsPlatformReferenceDataAdministratorAsync()
        {
            if (!long.TryParse(User.FindFirst("roleId")?.Value, out var roleId) || roleId <= 0)
                return false;
            if (!long.TryParse(User.FindFirst("businessUnitId")?.Value, out var businessUnitId) || businessUnitId <= 0)
                return false;

            return await _roleGate.IsSuperAdminAsync(roleId, businessUnitId);
        }

        private ModuleResponseDTO MapToResponse(Module module)
        {
            return new ModuleResponseDTO
            {
                Id = module.Id,
                ModuleName = module.ModuleName,
                Description = module.Description,
                IsActive = module.IsActive,
                CreatedBy = module.CreatedBy,
                CreatedOn = module.CreatedOn,
                ModifiedBy = module.ModifiedBy,
                ModifiedOn = module.ModifiedOn,
            };
        }
    }
}