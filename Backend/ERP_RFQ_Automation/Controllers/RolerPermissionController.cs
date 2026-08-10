using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.DTOs.RolePermission;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using ERP_RFQ_Automation.Security;

namespace ERP_RFQ_Automation.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class RolePermissionController : ControllerBase
    {
        private readonly IRolePermissionRepository _repository;
        private readonly IRoleGate _roleGate;
        private readonly IIamAuditWriter? _audit;
        private readonly ErpRfqAutomationContext? _context;

        /// <summary>
        /// The audit writer and the DbContext are optional so the existing unit-test constructions
        /// keep compiling; the bulk endpoint requires both and refuses to run without them rather
        /// than degrading to a non-transactional apply.
        /// </summary>
        public RolePermissionController(
            IRolePermissionRepository repository,
            IRoleGate roleGate,
            IIamAuditWriter? audit = null,
            ErpRfqAutomationContext? context = null)
        {
            _repository = repository;
            _roleGate = roleGate;
            _audit = audit;
            _context = context;
        }

        // GET: api/RolePermission?pageNumber=1&pageSize=10&id=1&roleId=1&moduleId=1&businessUnitId=1
        //
        // Deliberately still gated on "Roles & Permissions":View. This is the RBAC ADMINISTRATION
        // grid — it returns every role's grants — and widening it was never the fix for the empty
        // login bootstrap. Callers that need their OWN permissions use GET /api/User/me/permissions
        // (B2), which is authentication-only because reading your own grants is not privileged.
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
                return this.ServerError(ex, "Error retrieving data.");
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
                return this.ServerError(ex, "Error retrieving data.");
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
                    return await DenyAsync(request.RoleId, request.ModuleId, "self-or-higher-role grant");
                if (!await CanGrantAtMostOwnPermissionsAsync(
                        request.ModuleId, request.CanView, request.CanCreate, request.CanEdit, request.CanDelete, claimBUId))
                    return await DenyAsync(request.RoleId, request.ModuleId, "grant exceeds caller's own permissions");

                var createdBy = ActorContext.From(User).Stamp;

                // Retry-safe: see IIamAuditWriter.ExecuteAtomicAsync. Opening the transaction
                // directly made every permission grant fail with a 500 at save time.
                //
                // The entity is constructed INSIDE the delegate because the delegate is RETRIABLE.
                // Built outside, a transient Npgsql fault on the first attempt leaves it tracked as
                // Added and the retry re-adds the same instance, which can grant the permission
                // twice. A fresh instance per attempt carries no prior tracked state.
                RolePermission? rolePermission = null;
                await ExecuteAtomicAsync(async () =>
                {
                    rolePermission = new RolePermission
                    {
                        RoleId = request.RoleId,
                        ModuleId = request.ModuleId,
                        BusinessUnitId = request.BusinessUnitId,
                        CanView = request.CanView,
                        CanCreate = request.CanCreate,
                        CanEdit = request.CanEdit,
                        CanDelete = request.CanDelete,
                        // RC-7: server-derived. `User.Identity?.Name` was always null on this
                        // token, so the client-supplied request.CreatedBy always decided its own
                        // attribution.
                        CreatedBy = createdBy,
                        CreatedOn = DateTime.UtcNow
                    };

                    await _repository.AddAsync(rolePermission);
                    if (_audit is not null)
                        await _audit.WriteAsync(User, new IamAuditEntry(
                            IamAuditActions.PermissionGranted, IamAuditTargets.RolePermission,
                            rolePermission.Id, $"role:{rolePermission.RoleId} module:{rolePermission.ModuleId}",
                            After: Snapshot(rolePermission)));
                });

                var granted = rolePermission ?? throw new InvalidOperationException(
                    "Permission grant committed without producing an entity.");

                var response = MapToResponse(granted);
                return CreatedAtAction(nameof(GetById), new { id = granted.Id, businessUnitId = granted.BusinessUnitId }, response);
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
                    return await DenyAsync(existing.RoleId, existing.ModuleId, "self-or-higher-role grant");
                if (!await CanManageRoleGrantAsync(request.RoleId, claimBUId))
                    return await DenyAsync(request.RoleId, request.ModuleId, "self-or-higher-role grant");
                if (!await CanGrantAtMostOwnPermissionsAsync(
                        request.ModuleId, request.CanView, request.CanCreate, request.CanEdit, request.CanDelete, claimBUId))
                    return await DenyAsync(request.RoleId, request.ModuleId, "grant exceeds caller's own permissions");

                var before = Snapshot(existing);

                existing.RoleId = request.RoleId;
                existing.ModuleId = request.ModuleId;
                existing.BusinessUnitId = request.BusinessUnitId;
                existing.CanView = request.CanView;
                existing.CanCreate = request.CanCreate;
                existing.CanEdit = request.CanEdit;
                existing.CanDelete = request.CanDelete;
                // RC-7: server-derived; request.ModifiedBy is never read.
                existing.ModifiedBy = ActorContext.From(User).Stamp;
                existing.ModifiedOn = DateTime.UtcNow;

                // Enlisted before the repository's SaveChangesAsync so both rows share one
                // implicit transaction: a failed update leaves no audit trace.
                _audit?.Enlist(User, new IamAuditEntry(
                    IamAuditActions.PermissionModified, IamAuditTargets.RolePermission, existing.Id,
                    $"role:{existing.RoleId} module:{existing.ModuleId}", before, Snapshot(existing)));

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
                    return await DenyAsync(existing.RoleId, existing.ModuleId, "self-or-higher-role grant");

                _audit?.Enlist(User, new IamAuditEntry(
                    IamAuditActions.PermissionRevoked, IamAuditTargets.RolePermission, existing.Id,
                    $"role:{existing.RoleId} module:{existing.ModuleId}", Before: Snapshot(existing)));

                await _repository.DeleteAsync(id, targetBUId);
                return NoContent();
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ex.Message);
            }
            catch (Exception ex)
            {
                return this.ServerError(ex, "Error deleting data.");
            }
        }

        // POST: api/RolePermission/bulk
        /// <summary>
        /// B5: apply a whole matrix selection atomically.
        ///
        /// The screen used to fire ~51 independent writes for one "select all" click. Each was
        /// authorised separately, so a denial partway through left a half-applied grant set that
        /// matched no administrator's intent — and the UI reported success anyway. Here the FIRST
        /// denial rolls the entire batch back and returns 403: either every entry applies or none
        /// does.
        ///
        /// Every entry still routes through BOTH SEC-C1 guards — the same two methods used by
        /// Create/Update, not a repository shortcut — so bulk cannot become the soft path around
        /// the hardening.
        /// </summary>
        [HttpPost("bulk")]
        [RequireModulePermission("Roles & Permissions", PermissionAction.Edit)]
        public async Task<ActionResult<RolePermissionBulkApplyResponseDTO>> BulkApply(
            [FromBody] RolePermissionBulkApplyRequestDTO request)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                if (!TryTenant(out var claimBUId))
                    return BadRequest("A valid businessUnitId claim is required.");

                if (_context is null || _audit is null)
                    return StatusCode(
                        StatusCodes.Status500InternalServerError,
                        "Bulk permission apply requires the transactional data context.");

                if (request.RoleId is not { } roleId)
                    return BadRequest("RoleId is required.");

                var duplicateModule = request.Entries
                    .GroupBy(entry => entry.ModuleId)
                    .FirstOrDefault(group => group.Count() > 1);
                if (duplicateModule is not null)
                    return BadRequest($"Module {duplicateModule.Key} appears more than once in the batch.");

                // SEC-C1 guard #1 — evaluated once because it is a property of (caller, target
                // role), not of any individual entry.
                if (!await CanManageRoleGrantAsync(roleId, claimBUId))
                    return await DenyAsync(roleId, null, "bulk apply to self-or-higher role");

                var roleExists = await _context.SetupMasters.AsNoTracking()
                    .Where(SetupTypes.IsRoleRow)
                    .AnyAsync(sm => sm.SetupId == roleId && sm.BusinessUnitId == claimBUId && sm.IsActive != false);
                if (!roleExists)
                    return BadRequest($"Role with ID {roleId} does not exist.");

                var moduleIds = request.Entries.Select(entry => entry.ModuleId).Distinct().ToList();
                var knownModuleIds = await _context.Modules.AsNoTracking()
                    .Where(module => moduleIds.Contains(module.Id))
                    .Select(module => module.Id)
                    .ToListAsync();
                var unknownModuleIds = moduleIds.Where(moduleId => !knownModuleIds.Contains(moduleId)).ToList();
                if (unknownModuleIds.Count > 0)
                    return BadRequest($"Module with ID {unknownModuleIds[0]} does not exist.");

                // SEC-C1 guard #2 — per entry, because a caller's ceiling differs per module.
                foreach (var entry in request.Entries)
                {
                    if (!await CanGrantAtMostOwnPermissionsAsync(
                            entry.ModuleId, entry.CanView, entry.CanCreate, entry.CanEdit, entry.CanDelete, claimBUId))
                        return await DenyAsync(roleId, entry.ModuleId, "grant exceeds caller's own permissions");
                }

                var stamp = ActorContext.From(User).Stamp;
                var now = DateTime.UtcNow;
                var created = 0;
                var updated = 0;

                // Retry-safe: see IIamAuditWriter.ExecuteAtomicAsync. The whole read-modify-write
                // must be inside the strategy so a transient failure replays it as one unit —
                // `created`/`updated` are reset on entry for exactly that reason.
                await ExecuteAtomicAsync(async () =>
                {
                created = 0;
                updated = 0;

                var existingRows = await _context.RolePermissions
                    .Where(rp => rp.BusinessUnitId == claimBUId && rp.RoleId == roleId
                                 && moduleIds.Contains(rp.ModuleId))
                    .ToListAsync();

                foreach (var entry in request.Entries)
                {
                    var row = existingRows.FirstOrDefault(rp => rp.ModuleId == entry.ModuleId);
                    if (row is null)
                    {
                        row = new RolePermission
                        {
                            RoleId = roleId,
                            ModuleId = entry.ModuleId,
                            BusinessUnitId = claimBUId,
                            CanView = entry.CanView,
                            CanCreate = entry.CanCreate,
                            CanEdit = entry.CanEdit,
                            CanDelete = entry.CanDelete,
                            CreatedBy = stamp,
                            CreatedOn = now
                        };
                        _context.RolePermissions.Add(row);
                        created++;
                        _audit.Enlist(User, new IamAuditEntry(
                            IamAuditActions.PermissionGranted, IamAuditTargets.RolePermission, null,
                            $"role:{roleId} module:{entry.ModuleId}", After: Snapshot(row),
                            Reason: request.Reason));
                        continue;
                    }

                    var before = Snapshot(row);
                    var after = new PermissionSnapshot(
                        entry.CanView, entry.CanCreate, entry.CanEdit, entry.CanDelete);
                    if (before == after)
                        continue;

                    row.CanView = entry.CanView;
                    row.CanCreate = entry.CanCreate;
                    row.CanEdit = entry.CanEdit;
                    row.CanDelete = entry.CanDelete;
                    row.ModifiedBy = stamp;
                    row.ModifiedOn = now;
                    updated++;

                    // Revocation NEVER deletes: an all-false row is "no access" and is retained for
                    // provenance, which is what makes the matrix's uncheck-everything behaviour
                    // correct instead of the silent grant it used to be (RC-4).
                    var isRevocation = after is { CanView: not true, CanCreate: not true, CanEdit: not true, CanDelete: not true };
                    _audit.Enlist(User, new IamAuditEntry(
                        isRevocation ? IamAuditActions.PermissionRevoked : IamAuditActions.PermissionModified,
                        IamAuditTargets.RolePermission, row.Id,
                        $"role:{roleId} module:{entry.ModuleId}", before, after, request.Reason));
                }

                await _context.SaveChangesAsync(HttpContext?.RequestAborted ?? default);
                });

                return Ok(new RolePermissionBulkApplyResponseDTO
                {
                    Applied = request.Entries.Count,
                    Created = created,
                    Updated = updated
                });
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                return this.ServerError(ex, "Error applying permissions.");
            }
        }

        private bool TryTenant(out long businessUnitId) =>
            long.TryParse(User.FindFirstValue("businessUnitId"), out businessUnitId) && businessUnitId > 0;

        private long CallerRoleId() =>
            long.TryParse(User.FindFirst("roleId")?.Value, out var roleId) ? roleId : 0;

        /// <summary>Mutation + audit as one retriable unit. See IIamAuditWriter.ExecuteAtomicAsync.</summary>
        private Task ExecuteAtomicAsync(Func<Task> work)
            => _audit is null
                ? work()
                : _audit.ExecuteAtomicAsync(work, HttpContext?.RequestAborted ?? default);

        /// <summary>
        /// RC-7: a REFUSED escalation is the signal worth keeping — a successful grant is routine
        /// administration, an attempt to grant yourself more than you hold is not. Written on its
        /// own transaction so it survives the rollback of whatever the caller was attempting.
        /// Best-effort: an audit failure must never convert a 403 into a 500.
        /// </summary>
        private async Task<ActionResult> DenyAsync(long? targetRoleId, long? moduleId, string reason)
        {
            if (_audit is not null)
            {
                try
                {
                    await _audit.WriteAsync(User, new IamAuditEntry(
                        IamAuditActions.PermissionGrantDenied, IamAuditTargets.RolePermission,
                        targetRoleId,
                        moduleId is { } id ? $"role:{targetRoleId} module:{id}" : $"role:{targetRoleId}",
                        Reason: reason));
                }
                catch (Exception)
                {
                    // swallowed on purpose — see summary
                }
            }

            return Forbid();
        }

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
        ///
        /// B3 added <paramref name="canView"/> to the signature. Before CanView existed, read
        /// access was implied by row existence, so "grant a subordinate a module I cannot even see"
        /// was not expressible — and therefore not blockable.
        /// </summary>
        private async Task<bool> CanGrantAtMostOwnPermissionsAsync(
            long moduleId, bool? canView, bool? canCreate, bool? canEdit, bool? canDelete, long businessUnitId)
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

            if (canView == true && callerRow.CanView != true) return false;
            if (canCreate == true && callerRow.CanCreate != true) return false;
            if (canEdit == true && callerRow.CanEdit != true) return false;
            if (canDelete == true && callerRow.CanDelete != true) return false;
            return true;
        }

        /// <summary>Value-equal snapshot of the four grant flags, for audit before/after and for
        /// the bulk endpoint's "did anything actually change" test.</summary>
        internal readonly record struct PermissionSnapshot(
            bool? CanView, bool? CanCreate, bool? CanEdit, bool? CanDelete);

        private static PermissionSnapshot Snapshot(RolePermission row)
            => new(row.CanView, row.CanCreate, row.CanEdit, row.CanDelete);

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
                CanView = rolePermission.CanView,
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
