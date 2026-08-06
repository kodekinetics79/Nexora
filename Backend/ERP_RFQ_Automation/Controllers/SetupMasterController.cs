using ERP_RFQ_Automation.DTOs;
using ERP_RFQ_Automation.DTOs.SetupDTOs;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using System;
using System.Security.Claims;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ERP_RFQ_Automation.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class SetupMasterController : ControllerBase
    {
        private readonly ISetupMasterRepository _repository;
        private readonly IRoleGate? _roleGate;
        private readonly IRolePermissionRepository? _rolePermissions;
        private readonly IIamAuditWriter? _audit;
        private readonly IMemoryCache? _cache;

        public SetupMasterController(
            ISetupMasterRepository repository,
            IRoleGate? roleGate = null,
            IRolePermissionRepository? rolePermissions = null,
            IIamAuditWriter? audit = null,
            IMemoryCache? cache = null)
        {
            _repository = repository;
            _roleGate = roleGate;
            _rolePermissions = rolePermissions;
            _audit = audit;
            _cache = cache;
        }

        /// <summary>
        /// The business unit that owns rows written by this request. Setup_Master holds roles and
        /// lookup values, so it is tenant-owned data, not global reference data.
        /// TenantClaimGuardMiddleware 403s any authenticated request to a non-platform route whose
        /// token carries no businessUnitId > 0, so a caller reaching an action here always has one;
        /// 0 is still treated as "no tenant" and refused rather than silently defaulted.
        /// </summary>
        private long TenantId
            => long.TryParse(User.FindFirst("businessUnitId")?.Value, out var id) && id > 0 ? id : 0;

        // GET: api/SetupMaster?pageNumber=1&pageSize=10&setupId=1&setupType=Type1&setupCode=CODE1&setupName=Name1&isActive=true&businessUnitId=1
        [HttpGet]
        public async Task<ActionResult<PaginatedSetupMasterResponseDTO>> GetAll(
            [FromQuery] int pageNumber = 1,
            [FromQuery] int pageSize = 10,
            [FromQuery] long? setupId = null,
            [FromQuery] string? setupType = null,
            [FromQuery] string? setupCode = null,
            [FromQuery] string? setupName = null,
            [FromQuery] bool? isActive = null)
        {
            try
            {
                var setupMasters = await _repository.GetAllAsync();

                // Apply filters
                var filteredSetupMasters = setupMasters.AsQueryable();

                if (setupId.HasValue)
                    filteredSetupMasters = filteredSetupMasters.Where(s => s.SetupId == setupId.Value);

                if (!string.IsNullOrWhiteSpace(setupType))
                    filteredSetupMasters = filteredSetupMasters.Where(s => s.SetupType != null && s.SetupType.Contains(setupType, StringComparison.OrdinalIgnoreCase));

                if (!string.IsNullOrWhiteSpace(setupCode))
                    filteredSetupMasters = filteredSetupMasters.Where(s => s.SetupCode != null && s.SetupCode.Contains(setupCode, StringComparison.OrdinalIgnoreCase));

                if (!string.IsNullOrWhiteSpace(setupName))
                    filteredSetupMasters = filteredSetupMasters.Where(s => s.SetupValue != null && s.SetupValue.Contains(setupName, StringComparison.OrdinalIgnoreCase));

                if (isActive.HasValue)
                    filteredSetupMasters = filteredSetupMasters.Where(s => s.IsActive == isActive.Value);

                int totalItems = filteredSetupMasters.Count();
                var resultItems = filteredSetupMasters
                    .Skip((pageNumber - 1) * pageSize)
                    .Take(pageSize)
                    .Select(MapToResponse);

                var response = new PaginatedSetupMasterResponseDTO
                {
                    Items = resultItems.ToList(),
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

        // GET: api/SetupMaster/5
        [HttpGet("{id}")]
        public async Task<ActionResult<SetupMasterResponseDTO>> GetById(long id)
        {
            try
            {
                var setupMaster = await _repository.GetByIdAsync(id);
                return Ok(MapToResponse(setupMaster));
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

        [HttpPost]
        [RequireManagerRole]
        public async Task<ActionResult<SetupMasterResponseDTO>> Create([FromBody] SetupMasterCreateRequestDTO request)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                // Setup_Master rows are tenant-owned (the table has a BusinessUnitID column, an EF
                // global query filter, and an RLS policy). This used to hardcode BusinessUnitId = 1,
                // which meant every tenant's admin wrote their roles and lookup values into business
                // unit 1's namespace. Stamp the caller's own tenant instead.
                var tenantId = TenantId;
                if (tenantId <= 0)
                    return StatusCode(StatusCodes.Status403Forbidden, "A valid tenant claim is required.");

                var requestedRank = request.RoleRank ?? RoleRanks.Member;
                if (!RoleRanks.IsDefined(requestedRank))
                    return BadRequest($"RoleRank must be one of {string.Join(", ", RoleRanks.All)}.");

                // B4 / RC-6: role rows are RBAC, not lookup data. [RequireManagerRole] admits any
                // role at RoleRanks.Manager or above, so on its own it let a mid-level manager mint
                // roles at will.
                if (SetupTypes.IsRole(request.SetupType))
                {
                    if (await RoleAdministrationDenialAsync(tenantId, "CanCreate") is { } denial)
                        return denial;
                    if (await RoleRankDenialAsync(tenantId, requestedRank, existingRank: null) is { } rankDenial)
                        return rankDenial;
                }
                else if (requestedRank != RoleRanks.Member)
                {
                    // RoleRank is meaningless on a UOM/City/status row; refuse rather than store a
                    // value that would become live the moment somebody flipped SetupType.
                    return BadRequest("RoleRank may only be set on a role.");
                }

                // Normalize: treat 0 as null
                long? parentId = (request.ParentSetupId.HasValue && request.ParentSetupId.Value == 0)
                    ? null
                    : request.ParentSetupId;

                var setupMaster = new SetupMaster
                {
                    SetupType = request.SetupType,
                    SetupCode = request.SetupCode,
                    SetupValue = request.SetupName,
                    Description = request.Description,
                    ParentSetupId = parentId,
                    BusinessUnitId = tenantId,
                    // Server-derived: validated above and clamped to Member for non-role rows.
                    RoleRank = SetupTypes.IsRole(request.SetupType) ? requestedRank : RoleRanks.Member,
                    IsActive = request.IsActive,
                    CreatedBy = request.CreatedBy,
                    CreatedOn = DateTime.UtcNow
                };

                // Extra safety: if parentId == setupMaster.SetupId (unlikely on create), throw
                if (parentId.HasValue && parentId.Value == setupMaster.SetupId)
                    return BadRequest("ParentSetupId cannot be same as the entity SetupId.");

                await _repository.AddAsync(setupMaster);
                if (SetupTypes.IsRole(setupMaster.SetupType) && _audit is not null)
                    await _audit.WriteAsync(User, new IamAuditEntry(
                        IamAuditActions.RoleCreated, IamAuditTargets.Role, setupMaster.SetupId,
                        setupMaster.SetupValue,
                        After: new { setupMaster.SetupCode, setupMaster.SetupValue, setupMaster.IsActive, setupMaster.RoleRank }));

                var response = MapToResponse(setupMaster);
                return CreatedAtAction(nameof(GetById), new { id = setupMaster.SetupId, businessUnitId = setupMaster.BusinessUnitId }, response);
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

        [HttpPut("{id}")]
        [RequireManagerRole]
        public async Task<ActionResult> Update(long id, [FromBody] SetupMasterUpdateRequestDTO request)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                var existing = await _repository.GetByIdAsync(id);

                // ==== B4 / RC-6: one-request self-escalation to tenant super admin ====
                //
                // This action was gated ONLY by [RequireManagerRole] and assigned
                // SetupType/SetupCode/SetupValue verbatim, with no check that the row was a role or
                // was not the caller's OWN role. A holder of production role 4 "Sales Manager" could
                // rename their own role to "Sales Super Admin"; the name-matching gate then called
                // it a super admin, PermissionHandler short-circuited every module check, and they
                // held the whole tenant — inside the 60s cache TTL, with NO re-login.
                //
                // A RENAME can no longer escalate anything: authority is the RoleRank column and a
                // name is just a label. What replaces the old name guard is the rank guard below —
                // the escalation vector moved from "what you call the role" to "what number you put
                // in the column", and that number is bounded by the caller's own rank.
                var wasRole = SetupTypes.IsRole(existing.SetupType);
                var willBeRole = SetupTypes.IsRole(request.SetupType);
                var requestedRank = request.RoleRank ?? existing.RoleRank;

                if (!RoleRanks.IsDefined(requestedRank))
                    return BadRequest($"RoleRank must be one of {string.Join(", ", RoleRanks.All)}.");

                if (wasRole || willBeRole)
                {
                    var tenantId = TenantId;
                    if (tenantId <= 0)
                        return StatusCode(StatusCodes.Status403Forbidden, "A valid tenant claim is required.");

                    // (2) A row may not be smuggled into or out of "role" — that would let a
                    // lookup value become a role (or a role shed its guards) in one hop.
                    if (wasRole != willBeRole)
                        return StatusCode(
                            StatusCodes.Status403Forbidden,
                            "A setup row cannot be converted into or out of a role.");

                    if (await RoleAdministrationDenialAsync(tenantId, "CanEdit") is { } denial)
                        return denial;

                    // (4) Lockout/self-escalation: editing your own role is a super-admin-only act.
                    if (!await IsSuperAdminAsync(tenantId) && id == CallerRoleId())
                        return StatusCode(
                            StatusCodes.Status403Forbidden,
                            "You may not modify the role you currently hold.");

                    // (3) Rank may only move DOWN the caller's own ladder — including for a super
                    // administrator, who therefore cannot mint a second owner role from the API.
                    if (await RoleRankDenialAsync(tenantId, requestedRank, existing.RoleRank) is { } rankDenial)
                        return rankDenial;
                }
                else if (requestedRank != RoleRanks.Member)
                {
                    return BadRequest("RoleRank may only be set on a role.");
                }

                // Normalize parent id (treat 0 as null)
                long? parentId = (request.ParentSetupId.HasValue && request.ParentSetupId.Value == 0)
                    ? null
                    : request.ParentSetupId;

                // Prevent self-parenting
                if (parentId.HasValue && parentId.Value == existing.SetupId)
                    return BadRequest("ParentSetupId cannot be same as the entity SetupId.");

                var beforeRole = new { existing.SetupCode, existing.SetupValue, existing.IsActive, existing.RoleRank };
                var rankChanged = wasRole && existing.RoleRank != requestedRank;

                // Update relevant fields
                existing.SetupType = request.SetupType;
                existing.SetupCode = request.SetupCode;
                existing.SetupValue = request.SetupName;
                existing.Description = request.Description;
                existing.ParentSetupId = parentId;
                existing.RoleRank = wasRole ? requestedRank : RoleRanks.Member;
                // BusinessUnitId is deliberately NOT assigned. It used to be rewritten to 1 on every
                // update, which reassigned the row away from its owning tenant; on PostgreSQL the RLS
                // WITH CHECK clause turned that into a 500, and on any path that is not covered by RLS
                // (background/pipeline role, non-PostgreSQL provider) it silently handed the row to
                // business unit 1. SetupMasterRepository.UpdateAsync re-pins the stored owner as well.
                existing.IsActive = request.IsActive;
                // RC-7: server-derived; request.ModifiedBy is never read.
                existing.ModifiedBy = ActorContext.From(User).Stamp;
                existing.ModifiedOn = DateTime.UtcNow;

                if (wasRole)
                {
                    var afterRole = new { existing.SetupCode, existing.SetupValue, existing.IsActive, existing.RoleRank };
                    _audit?.Enlist(User, new IamAuditEntry(
                        IamAuditActions.RoleRenamed, IamAuditTargets.Role, existing.SetupId,
                        existing.SetupValue, beforeRole, afterRole));

                    // A rank move is the privilege-bearing edit on this endpoint, so it gets its own
                    // audit action rather than hiding inside a ROLE_RENAMED diff.
                    if (rankChanged)
                        _audit?.Enlist(User, new IamAuditEntry(
                            IamAuditActions.RoleRankChanged, IamAuditTargets.Role, existing.SetupId,
                            existing.SetupValue,
                            Before: new { RoleRank = beforeRole.RoleRank, Tier = RoleRanks.Describe(beforeRole.RoleRank) },
                            After: new { existing.RoleRank, Tier = RoleRanks.Describe(existing.RoleRank) }));
                }

                await _repository.UpdateAsync(existing);

                // (6) The resolved role name is cached for 60s. Without this eviction a rename
                // that a guard above already permitted would still be invisible to RoleGate for up
                // to a minute — and, worse, a name REVOKED by an administrator would keep granting.
                if (wasRole)
                    _cache?.Remove(RoleGate.RoleCacheKey(existing.BusinessUnitId, existing.SetupId));

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

        [HttpDelete("{id}")]
        [RequireManagerRole]
        public async Task<ActionResult> Delete(long id)
        {
            try
            {
                var existing = await _repository.GetByIdAsync(id);

                if (SetupTypes.IsRole(existing.SetupType))
                {
                    var tenantId = TenantId;
                    if (tenantId <= 0)
                        return StatusCode(StatusCodes.Status403Forbidden, "A valid tenant claim is required.");

                    if (await RoleAdministrationDenialAsync(tenantId, "CanDelete") is { } denial)
                        return denial;

                    // (4) Deleting the role you hold is refused UNCONDITIONALLY — including for a
                    // super administrator. It is not a privilege question, it is a lockout: the
                    // tenant plane has no break-glass recovery path.
                    if (id == CallerRoleId())
                        return StatusCode(
                            StatusCodes.Status403Forbidden,
                            "You may not delete the role you currently hold.");

                    _audit?.Enlist(User, new IamAuditEntry(
                        IamAuditActions.RoleDeleted, IamAuditTargets.Role, existing.SetupId,
                        existing.SetupValue,
                        Before: new { existing.SetupCode, existing.SetupValue, existing.IsActive }));
                }

                await _repository.DeleteAsync(id);

                if (SetupTypes.IsRole(existing.SetupType))
                    _cache?.Remove(RoleGate.RoleCacheKey(existing.BusinessUnitId, existing.SetupId));

                return NoContent();
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ex.Message);
            }
            catch (RoleInUseException ex)
            {
                // A role still referenced by users or permission rows cannot be deleted, and the
                // caller needs to know WHAT is blocking it, not just that something is.
                return Conflict(ex.Message);
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

        private long CallerRoleId()
            => long.TryParse(User.FindFirst("roleId")?.Value, out var roleId) ? roleId : 0;

        private async Task<bool> IsSuperAdminAsync(long tenantId)
        {
            var callerRoleId = CallerRoleId();
            return _roleGate is not null && callerRoleId > 0
                   && await _roleGate.IsSuperAdminAsync(callerRoleId, tenantId);
        }

        /// <summary>
        /// (1) A role mutation additionally requires super-admin status OR an explicit
        /// "Roles &amp; Permissions" grant for the matching action. [RequireManagerRole] stays on the
        /// actions because the UOM/City/lookup screens depend on it — this guard fires ONLY when
        /// the row in question is a role, so those screens are untouched.
        ///
        /// Fail-closed: without the collaborators wired (unit-test construction), a role mutation
        /// is refused rather than silently permitted.
        /// </summary>
        private async Task<ActionResult?> RoleAdministrationDenialAsync(long tenantId, string action)
        {
            if (await IsSuperAdminAsync(tenantId)) return null;

            var callerRoleId = CallerRoleId();
            if (_rolePermissions is not null && callerRoleId > 0
                && await _rolePermissions.CheckPermissionAsync(
                    callerRoleId, "Roles & Permissions", action, tenantId))
                return null;

            return StatusCode(
                StatusCodes.Status403Forbidden,
                "Managing roles requires the Roles & Permissions module permission.");
        }

        /// <summary>
        /// (3) Rank mutation guard — the replacement for the deleted "privileged NAME" guard.
        ///
        /// Invariant: <b>a caller may never set a role's rank at or above their own</b>, and may
        /// never move the rank of a role that already sits at or above their own. Both halves are
        /// required. Without the first, a Manager(10) mints an Owner(30) role and assigns it to
        /// themselves. Without the second, a Manager(10) DEMOTES the Owner role to Member and takes
        /// the tenant by removing everyone above them.
        ///
        /// This applies to the owner tier as well — deliberately. There is no "super admins are
        /// exempt" branch, because such a branch is exactly what the old name guard had, and it is
        /// what let a role that merely LOOKED like an owner mint real owners. The consequence is
        /// that a second Owner role cannot be created through this API at all; owner provisioning
        /// is a platform-plane act (seeder/migration), not a tenant-plane one.
        ///
        /// Non-rank edits (name, description, active flag) are untouched: this returns null when
        /// the requested rank equals the stored one.
        ///
        /// Fail-closed: with no role gate wired, <see cref="CallerRankAsync"/> reports Member, so
        /// every rank above Member is refused rather than silently permitted.
        /// </summary>
        /// <param name="existingRank">The role's CURRENT rank, or null when the role is being
        /// created. On create there is no "unchanged" case: every rank, including Member, is a rank
        /// the caller is choosing, so it must be strictly below their own.</param>
        private async Task<ActionResult?> RoleRankDenialAsync(long tenantId, short requestedRank, short? existingRank)
        {
            if (existingRank == requestedRank) return null;

            var callerRank = await CallerRankAsync(tenantId);

            if (requestedRank >= callerRank)
                return StatusCode(
                    StatusCodes.Status403Forbidden,
                    $"You may not set a role to rank {RoleRanks.Describe(requestedRank)} " +
                    $"at or above your own rank {RoleRanks.Describe(callerRank)}.");

            if (existingRank.HasValue && existingRank.Value >= callerRank)
                return StatusCode(
                    StatusCodes.Status403Forbidden,
                    $"You may not change the rank of a role at or above your own rank " +
                    $"{RoleRanks.Describe(callerRank)}.");

            return null;
        }

        private async Task<short> CallerRankAsync(long tenantId)
        {
            var callerRoleId = CallerRoleId();
            return _roleGate is not null && callerRoleId > 0
                ? await _roleGate.GetRoleRankAsync(callerRoleId, tenantId)
                : RoleRanks.Member;
        }

        // Helper: Map entity to response DTO
        private SetupMasterResponseDTO MapToResponse(SetupMaster setupMaster)
        {
            return new SetupMasterResponseDTO
            {
                SetupId = setupMaster.SetupId,
                SetupType = setupMaster.SetupType,
                SetupCode = setupMaster.SetupCode,
                SetupName = setupMaster.SetupValue,
                Description = setupMaster.Description,
                ParentSetupId = setupMaster.ParentSetupId,
                RoleRank = setupMaster.RoleRank,

                IsActive = setupMaster.IsActive,
                CreatedBy = setupMaster.CreatedBy,
                CreatedOn = setupMaster.CreatedOn,
                ModifiedBy = setupMaster.ModifiedBy,
                ModifiedOn = setupMaster.ModifiedOn
            };
        }
    }
}
