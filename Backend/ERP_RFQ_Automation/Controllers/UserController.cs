using ERP_RFQ_Automation.DTOs.BusinessUnit;
using ERP_RFQ_Automation.DTOs.TeamDTOs;
using ERP_RFQ_Automation.DTOs.UserDTO;
using ERP_RFQ_Automation.DTOs.UserGroup;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Entitlements;
using ERP_RFQ_Automation.Security;
using ERP_RFQ_Automation.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;

namespace ERP_RFQ_Automation.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class UserController : ControllerBase
    {
        private readonly IUserRepository _repository;
        private readonly IWebHostEnvironment _environment;
        private readonly IRoleGate _roleGate;
        private readonly IEntitlementService? _entitlements;
        private readonly IRolePermissionRepository? _rolePermissions;
        private readonly IIamAuditWriter? _audit;
        private static readonly int[] AllowedPageSizes = { 5, 10, 25, 50 };

        /// <summary>
        /// The trailing dependencies are optional so the existing unit-test constructions keep
        /// compiling; in the composed application every one of them is registered.
        /// </summary>
        public UserController(
            IUserRepository repository,
            IWebHostEnvironment environment,
            IRoleGate roleGate,
            IEntitlementService? entitlements = null,
            IRolePermissionRepository? rolePermissions = null,
            IIamAuditWriter? audit = null)
        {
            _repository = repository;
            _environment = environment;
            _roleGate = roleGate;
            _entitlements = entitlements;
            _rolePermissions = rolePermissions;
            _audit = audit;
        }

        // GET: api/User?pageNumber=1&pageSize=10&id=1&userName=john&email=john@example.com&roleId=1&region=US&isActive=true&businessUnitId=1
        [HttpGet]
        [RequireModulePermission("Users", PermissionAction.View)]
        public async Task<ActionResult<DTOs.UserDTO.PaginatedResponseDTO<UserResponseDTO>>> GetAll(
            [FromQuery] long? businessUnitId = null,
            [FromQuery] int pageNumber = 1,
            [FromQuery] int pageSize = 10,
            [FromQuery] long? id = null,
            [FromQuery] string? userName = null,
            [FromQuery] string? email = null,
            [FromQuery] long? roleId = null,
            [FromQuery] string? region = null,
            [FromQuery] bool? isActive = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0); // SEC-06: claim wins over client-supplied businessUnitId

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                if (pageNumber < 1)
                    return BadRequest("Page number must be greater than or equal to 1.");
                
                // Relaxed validation: Allow any page size up to 1000
                if (pageSize < 1 || pageSize > 1000)
                    return BadRequest("Page size must be between 1 and 1000.");

                var (users, totalCount) = await _repository.GetAllAsync(pageNumber, pageSize, id, userName, email, roleId, region, isActive, targetBUId);

                var response = new DTOs.UserDTO.PaginatedResponseDTO<UserResponseDTO>
                {
                    Items = users,
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

        // GET: api/User/5
        [HttpGet("{id}")]
        [RequireModulePermission("Users", PermissionAction.View)]
        public async Task<ActionResult<UserResponseDTO>> GetById(long id, [FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0); // SEC-06: claim wins over client-supplied businessUnitId

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                var user = await _repository.GetByIdAsync(id, targetBUId);
                return Ok(MapToResponse(user));
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

        // GET: api/User/me/permissions
        /// <summary>
        /// RC-5: the caller's own effective permissions. <c>[Authorize]</c> ONLY — deliberately no
        /// <c>[RequireModulePermission]</c>.
        ///
        /// The login bootstrap previously read <c>GET /api/RolePermission</c>, which is gated on
        /// "Roles &amp; Permissions":View. That gate is correct for the RBAC administration grid and
        /// stays exactly as it is; it is catastrophic as the source of a session's own permission
        /// set, because every role that cannot administer RBAC — i.e. every role a customer creates
        /// at pilot — bootstrapped with an empty permission array and an empty product.
        ///
        /// Reading your own grants is not privileged. roleId and businessUnitId come from the
        /// claims exclusively; there is no parameter a client could use to ask about another role.
        /// </summary>
        [HttpGet("me/permissions")]
        public async Task<ActionResult<MyPermissionsResponseDTO>> GetMyPermissions()
        {
            try
            {
                var actor = ActorContext.From(User);
                if (!actor.HasTenant)
                    return StatusCode(StatusCodes.Status403Forbidden, "A valid businessUnitId claim is required.");

                var response = new MyPermissionsResponseDTO
                {
                    UserId = actor.UserId,
                    RoleId = actor.RoleId,
                    BusinessUnitId = actor.BusinessUnitId
                };

                if (actor.RoleId is not { } roleId)
                    return Ok(response);

                // RC-8: super-admin/manager status is asked of IRoleGate, the SAME authority
                // PermissionHandler consults. AuthRepository used to answer the identical question
                // from a plain FK Include with no BU / SetupType / IsActive predicate, so the login
                // response and the API could disagree — the UI renders everything and every call
                // 403s. There is now one decision, made in one place.
                response.IsSuperAdmin = await _roleGate.IsSuperAdminAsync(roleId, actor.BusinessUnitId);
                response.IsManager = await _roleGate.IsManagerOrAdminAsync(roleId, actor.BusinessUnitId);

                var roles = await _repository.GetRolesAsync(actor.BusinessUnitId);
                response.RoleName = roles.FirstOrDefault(x => x.SetupId == roleId)?.SetupName;

                if (_rolePermissions is not null)
                {
                    // pageSize covers the full module catalogue (51+ rows today) in one call; the
                    // repository already scopes by business unit, so no foreign-tenant row can
                    // reach this projection.
                    var (rows, _) = await _rolePermissions.GetAllAsync(
                        pageNumber: 1, pageSize: 1000, id: null, roleId: roleId, moduleId: null,
                        businessUnitId: actor.BusinessUnitId);

                    response.Permissions = rows
                        .Select(rp => new MyModulePermissionDTO
                        {
                            ModuleId = rp.ModuleId,
                            ModuleName = rp.Module?.ModuleName,
                            CanView = rp.CanView == true,
                            CanCreate = rp.CanCreate == true,
                            CanEdit = rp.CanEdit == true,
                            CanDelete = rp.CanDelete == true
                        })
                        .ToList();
                }

                return Ok(response);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error retrieving permissions: {ex.Message}");
            }
        }

        // POST: api/User
        [HttpPost]
        [RequireModulePermission("Users", PermissionAction.Create)]
        // SEGREGATION OF DUTIES: creating a user ASSIGNS A ROLE, which is a privilege
        // grant — the same act RolePermissionController gates far more strictly. Without
        // this second requirement, a holder of Users:Create could mint an account carrying
        // any role, including one more privileged than their own, entirely bypassing the
        // self-grant and grant-what-you-hold guards added to the permissions controller.
        [RequireModulePermission("Roles & Permissions", PermissionAction.Edit)]
        public async Task<ActionResult<UserResponseDTO>> Create([FromForm] UserCreateRequestDTO request)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                string? imagePath = null;
                if (request.ImageFile != null)
                {
                    // SEC-11: validate the upload by magic bytes (not the client extension) and
                    // derive a safe extension from the detected type before writing to wwwroot,
                    // so a disguised script payload can never be stored and served (stored XSS).
                    var validation = await FileContentValidator.ValidateImageAsync(request.ImageFile);
                    if (!validation.IsValid)
                        return BadRequest(validation.Error);

                    var uploadsFolder = Path.Combine(_environment.WebRootPath, "UserImages");
                    if (!Directory.Exists(uploadsFolder))
                        Directory.CreateDirectory(uploadsFolder);

                    var uniqueFileName = $"{Guid.NewGuid()}{validation.Extension}";
                    var filePath = Path.Combine(uploadsFolder, uniqueFileName);
                    using (var fileStream = new FileStream(filePath, FileMode.Create))
                    {
                        await request.ImageFile.CopyToAsync(fileStream);
                    }
                    imagePath = $"/UserImages/{uniqueFileName}";
                }

                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                if (claimBUId <= 0) return Forbid();
                if (request.Buid > 0 && request.Buid != claimBUId) return Forbid();
                request.Buid = claimBUId;
                if (!await CanManageRoleAsync(request.RoleId, claimBUId)) return Forbid();

                // Plan seat entitlement (P0): creating an ACTIVE user consumes a seat.
                if (request.IsActive != false && await SeatLimitDenialAsync(claimBUId) is { } seatDenial)
                    return seatDenial;

                // Hoisted deliberately: BCrypt mints a fresh random salt per call, so hashing
                // inside the retriable delegate would burn a ~100ms KDF and change the stored
                // hash on every attempt. The password is the same either way.
                var passwordHash = BCrypt.Net.BCrypt.HashPassword(request.Password);
                var createdBy = ActorContext.From(User).Stamp;

                // The audit row and the user row commit together or not at all. The user's Id is
                // only assigned by the INSERT, so the audit write has to follow it — the explicit
                // transaction is what keeps "created but unaudited" from being a reachable state.
                // Runs inside the retrying execution strategy. Opening the transaction here and
                // calling SaveChanges under it threw "the configured execution strategy
                // 'NpgsqlRetryingExecutionStrategy' does not support user-initiated transactions"
                // at SAVE time — after BeginAtomicAsync's try/catch had already returned — so
                // every single user creation returned a 500.
                //
                // The entity is constructed INSIDE the delegate because the delegate is RETRIABLE.
                // Built outside, a first attempt that fails on a transient Npgsql fault leaves the
                // instance tracked as Added; the retry re-adds the same instance against a change
                // tracker that still holds it, which can insert the user twice. A fresh instance
                // per attempt has no prior tracked state. Everything non-transactional — the image
                // write and the KDF above — stays outside so it happens exactly once.
                User? user = null;
                await ExecuteAtomicAsync(async () =>
                {
                    user = new User
                    {
                        FirstName = request.FirstName,
                        MiddleName = request.MiddleName,
                        LastName = request.LastName,
                        Email = request.Email,
                        PasswordHash = passwordHash,
                        ImageUrl = imagePath ?? request.ImageUrl ?? string.Empty,
                        RoleId = request.RoleId,
                        TeamId = request.TeamId,
                        Timezone = request.Timezone,
                        Region = request.Region,
                        ManagerId = request.ManagerId,
                        Buid = request.Buid,
                        UserGroupId = request.UserGroupId,
                        IsActive = request.IsActive ?? true,
                        // RC-7: server-derived. `User.Identity?.Name` was always null (the tenant
                        // JWT never set NameClaimType), so the client-supplied request.CreatedBy
                        // always won.
                        CreatedBy = createdBy,
                        CreatedOn = DateTime.UtcNow
                    };

                    await _repository.AddAsync(user);
                    await AuditAsync(IamAuditActions.UserCreated, user.Id, user.Email, after: new
                    {
                        user.Email,
                        user.RoleId,
                        user.TeamId,
                        user.UserGroupId,
                        user.IsActive
                    });
                });

                // Non-null on every path where the transaction committed; the throw is unreachable
                // in practice and exists so a future refactor that stops assigning cannot silently
                // return a 201 for a user that was never written.
                var created = user ?? throw new InvalidOperationException(
                    "User creation committed without producing an entity.");

                var response = MapToResponse(created);
                return CreatedAtAction(nameof(GetById), new { id = created.Id, businessUnitId = created.Buid }, response);
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

        // PUT: api/User/5
        [HttpPut("{id}")]
        [RequireModulePermission("Users", PermissionAction.Edit)]
        // SEGREGATION OF DUTIES: see Create. Updating a user can REASSIGN ITS ROLE, so the
        // same privilege-granting authority is required; Users:Edit alone would be a
        // privilege-escalation path around the permissions controller's guards.
        [RequireModulePermission("Roles & Permissions", PermissionAction.Edit)]
        public async Task<ActionResult> Update(long id, [FromForm] UserUpdateRequestDTO request)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                if (claimBUId <= 0) return Forbid();
                if (request.Buid > 0 && request.Buid != claimBUId) return Forbid();
                request.Buid = claimBUId;

                var existing = await _repository.GetByIdAsync(id, request.Buid);
                var callerId = long.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                    ?? User.FindFirst("sub")?.Value, out var parsedCallerId) ? parsedCallerId : 0;
                if (!await CanManageRoleAsync(existing.RoleId, claimBUId)) return Forbid();
                if (request.RoleId != existing.RoleId &&
                    (callerId == id || !await CanManageRoleAsync(request.RoleId, claimBUId)))
                    return Forbid();

                // Plan seat entitlement (P0): reactivating an inactive user consumes a
                // seat exactly like a create (the effective new value is IsActive ?? true).
                if (existing.IsActive != true && (request.IsActive ?? true)
                    && await SeatLimitDenialAsync(claimBUId) is { } seatDenial)
                    return seatDenial;

                // B7 lockout invariant: an administrator can lock the whole tenant out of its own
                // RBAC screens by deactivating (or demoting) the last account that holds a
                // super-admin-capable role. There is no break-glass path in the tenant plane, so
                // this has to be refused at the point of change, not recovered from afterwards.
                var losesAdministrator =
                    (request.IsActive == false && existing.IsActive == true) ||
                    (request.RoleId != existing.RoleId &&
                     await IsSuperAdminCapableRoleAsync(existing.RoleId, claimBUId) &&
                     !await IsSuperAdminCapableRoleAsync(request.RoleId, claimBUId));
                if (losesAdministrator && await IsLastActiveAdministratorAsync(id, existing.RoleId, claimBUId))
                    return Conflict("This is the last active administrator for this business unit.");

                string? imagePath = existing.ImageUrl;
                if (request.ImageFile != null)
                {
                    // SEC-11: validate the upload by magic bytes (not the client extension) and
                    // derive a safe extension from the detected type before writing to wwwroot,
                    // so a disguised script payload can never be stored and served (stored XSS).
                    var validation = await FileContentValidator.ValidateImageAsync(request.ImageFile);
                    if (!validation.IsValid)
                        return BadRequest(validation.Error);

                    var uploadsFolder = Path.Combine(_environment.WebRootPath, "UserImages");
                    if (!Directory.Exists(uploadsFolder))
                        Directory.CreateDirectory(uploadsFolder);

                    var uniqueFileName = $"{Guid.NewGuid()}{validation.Extension}";
                    var filePath = Path.Combine(uploadsFolder, uniqueFileName);
                    using (var fileStream = new FileStream(filePath, FileMode.Create))
                    {
                        await request.ImageFile.CopyToAsync(fileStream);
                    }
                    imagePath = $"/UserImages/{uniqueFileName}";
                }

                var before = new
                {
                    existing.Email,
                    existing.RoleId,
                    existing.TeamId,
                    existing.UserGroupId,
                    existing.IsActive
                };

                existing.FirstName = request.FirstName;
                existing.MiddleName = request.MiddleName;
                existing.LastName = request.LastName;
                existing.Email = request.Email;
                existing.ImageUrl = imagePath ?? request.ImageUrl;

                // Update Foreign Keys
                existing.RoleId = request.RoleId;
                existing.TeamId = request.TeamId;
                existing.UserGroupId = request.UserGroupId;
                existing.ManagerId = request.ManagerId;
                existing.Buid = request.Buid;
                existing.Timezone = request.Timezone;
                existing.Region = request.Region;
                existing.IsActive = request.IsActive ?? true;

                // Nullify navigation properties to ensure FK changes are respected during Update
                existing.Role = null;
                existing.Team = null;
                existing.UserGroup = null;
                existing.Manager = null;
                existing.Bu = null;

                // RC-7: server-derived; request.ModifiedBy is never read.
                existing.ModifiedBy = ActorContext.From(User).Stamp;
                existing.ModifiedOn = DateTime.UtcNow;

                var after = new
                {
                    existing.Email,
                    existing.RoleId,
                    existing.TeamId,
                    existing.UserGroupId,
                    existing.IsActive
                };

                // The audit event is enlisted BEFORE the repository's SaveChangesAsync, so both
                // rows land in that one implicit transaction — a failed update leaves no trace.
                var action = before.RoleId != after.RoleId
                    ? IamAuditActions.UserRoleChanged
                    : before.IsActive == true && after.IsActive != true
                        ? IamAuditActions.UserDeactivated
                        : IamAuditActions.UserUpdated;
                _audit?.Enlist(User, new IamAuditEntry(
                    action, IamAuditTargets.User, existing.Id, existing.Email, before, after));

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

        // POST: api/User/{id}/ChangePassword
        [HttpPost("{id}/ChangePassword")]
        public async Task<ActionResult> ChangePassword(long id, [FromBody] ChangePasswordRequestDTO request)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                // SEC-01: previously ANY authenticated user could reset ANY user's password
                // in ANY tenant (account takeover). A user may now only change their OWN
                // password, and the target is scoped to the caller's business unit.
                // NOTE: admin-initiated resets for other users need a separate,
                // permission-gated endpoint (follow-up SEC-01a) — intentionally not this one.
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var callerId = long.Parse(
                    User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                    ?? User.FindFirst("sub")?.Value
                    ?? "0");
                if (claimBUId <= 0 || callerId <= 0) return Forbid();
                if (callerId != id) return Forbid();

                // Ensure the target user exists within the caller's business unit.
                var target = await _repository.GetByIdAsync(id, claimBUId);

                _audit?.Enlist(User, new IamAuditEntry(
                    IamAuditActions.PasswordChanged, IamAuditTargets.User, id, target.Email));

                await _repository.ChangePasswordAsync(id, request.NewPassword);
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
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error changing password: {ex.Message}");
            }
        }

        // DELETE: api/User/5
        [HttpDelete("{id}")]
        [RequireModulePermission("Users", PermissionAction.Delete)]
        public async Task<ActionResult> Delete(long id, [FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0); // SEC-06: claim wins over client-supplied businessUnitId

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                var existing = await _repository.GetByIdAsync(id, targetBUId);
                if (!await CanManageRoleAsync(existing.RoleId, targetBUId)) return Forbid();

                // B7 lockout invariant — see the matching guard in Update. An already-inactive
                // account cannot be the last ACTIVE administrator, so it is exempt.
                if (existing.IsActive == true
                    && await IsLastActiveAdministratorAsync(id, existing.RoleId, targetBUId))
                    return Conflict("This is the last active administrator for this business unit.");

                _audit?.Enlist(User, new IamAuditEntry(
                    IamAuditActions.UserDeleted, IamAuditTargets.User, id, existing.Email,
                    Before: new { existing.Email, existing.RoleId, existing.IsActive }));

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

        // GET: api/User/Roles
        [HttpGet("Roles")]
        [RequireModulePermission("Users", PermissionAction.View)]
        public async Task<ActionResult<IEnumerable<RoleResponseDTO>>> GetRoles()
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                if (claimBUId <= 0) return Forbid();
                var roles = await _repository.GetRolesAsync(claimBUId);
                return Ok(roles);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error retrieving roles: {ex.Message}");
            }
        }

        // GET: api/User/Teams
        [HttpGet("Teams")]
        [RequireModulePermission("Users", PermissionAction.View)]
        public async Task<ActionResult<IEnumerable<TeamResponseDTO>>> GetTeams([FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0); // SEC-06: claim wins over client-supplied businessUnitId

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                var teams = await _repository.GetTeamsAsync(targetBUId);
                return Ok(teams);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error retrieving teams: {ex.Message}");
            }
        }

        // GET: api/User/BusinessUnits
        [HttpGet("BusinessUnits")]
        [RequireModulePermission("Users", PermissionAction.View)]
        public async Task<ActionResult<IEnumerable<BusinessUnitResponseDTO>>> GetBusinessUnits()
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                if (claimBUId <= 0) return Forbid();
                var businessUnits = await _repository.GetBusinessUnitsAsync();
                return Ok(businessUnits.Where(x => x.Id == claimBUId));
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error retrieving business units: {ex.Message}");
            }
        }

    
        // GET: api/User/UserGroups
        [HttpGet("UserGroups")]
        [RequireModulePermission("Users", PermissionAction.View)]
        public async Task<ActionResult<IEnumerable<UserGroupResponseDTO>>> GetUserGroups([FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0); // SEC-06: claim wins over client-supplied businessUnitId

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                var userGroups = await _repository.GetUserGroupsAsync(targetBUId);
                return Ok(userGroups);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error retrieving user groups: {ex.Message}");
            }
        }

        private UserResponseDTO MapToResponse(User user)
        {
            return new UserResponseDTO
            {
                Id = user.Id,
                FirstName = user.FirstName,
                MiddleName = user.MiddleName,
                LastName = user.LastName,
                Email = user.Email,
                ImageUrl = user.ImageUrl,
                RoleId = user.RoleId,
                TeamId = user.TeamId,
                Timezone = user.Timezone,
                LastLogin = user.LastLogin,
                Region = user.Region,
                ManagerId = user.ManagerId,
                Buid = user.Buid,
                UserGroupId = user.UserGroupId,
                IsActive = user.IsActive,
                CreatedBy = user.CreatedBy,
                CreatedOn = user.CreatedOn,
                ModifiedBy = user.ModifiedBy,
                ModifiedOn = user.ModifiedOn,
                RoleName = user.Role != null ? user.Role.SetupValue : null,
                TeamName = user.Team != null ? user.Team.TeamName : null,
                BusinessUnitName = user.Bu != null ? user.Bu.BusinessUnitName : null,
                UserGroupName = user.UserGroup != null ? user.UserGroup.UserGroupsName : null
            };
        }

        /// <summary>
        /// Null when a seat is available (or the tenant has no plan / no platform
        /// Tenant row — fail open); otherwise the 403 problem+json denial whose title
        /// states the plan's seat limit. P2-A10: the denial is the TYPED
        /// <see cref="SeatLimitExceededException"/> rendered by the single canonical
        /// <see cref="EntitlementProblemFilter"/> shape (no hand-rolled payload here).
        /// </summary>
        private async Task<ActionResult?> SeatLimitDenialAsync(long businessUnitId)
        {
            if (_entitlements is null)
                return null;

            var seats = await _entitlements.CheckSeatAvailabilityAsync(businessUnitId, HttpContext.RequestAborted);
            if (seats.Allowed)
                return null;

            return EntitlementProblemFilter.ToResult(
                new SeatLimitExceededException(businessUnitId, seats));
        }

        private async Task<bool> CanManageRoleAsync(long? targetRoleId, long businessUnitId)
        {
            if (!targetRoleId.HasValue) return true;
            var callerRoleId = long.TryParse(User.FindFirst("roleId")?.Value, out var parsedRoleId)
                ? parsedRoleId
                : 0;
            if (callerRoleId <= 0) return false;
            return await _roleGate.CanManageRoleAsync(callerRoleId, targetRoleId, businessUnitId);
        }

        /// <summary>Mutation + audit as one retriable unit. See IIamAuditWriter.ExecuteAtomicAsync.</summary>
        private Task ExecuteAtomicAsync(Func<Task> work)
            => _audit is null
                ? work()
                : _audit.ExecuteAtomicAsync(work, HttpContext?.RequestAborted ?? default);

        private async Task AuditAsync(
            string action, long? targetId, string? targetLabel, object? before = null, object? after = null)
        {
            if (_audit is null) return;
            await _audit.WriteAsync(User, new IamAuditEntry(
                action, IamAuditTargets.User, targetId, targetLabel, before, after));
        }

        private Task<bool> IsSuperAdminCapableRoleAsync(long? roleId, long businessUnitId)
            => roleId is { } id ? _roleGate.IsSuperAdminAsync(id, businessUnitId) : Task.FromResult(false);

        /// <summary>
        /// B7: true when <paramref name="userId"/> is the ONLY active account in the business unit
        /// holding a super-admin-capable role.
        ///
        /// Deactivating or deleting that account leaves the tenant with nobody who can reach the
        /// Roles &amp; Permissions screen, and the tenant plane has no break-glass recovery
        /// (platform-plane recovery is backlog item 15). Cheap to check — user counts per business
        /// unit are small — and it fails OPEN if the roster cannot be read, because refusing every
        /// deactivation would be a worse failure than allowing one.
        /// </summary>
        private async Task<bool> IsLastActiveAdministratorAsync(long userId, long? roleId, long businessUnitId)
        {
            if (!await IsSuperAdminCapableRoleAsync(roleId, businessUnitId)) return false;

            var (users, _) = await _repository.GetAllAsync(
                pageNumber: 1, pageSize: 1000, id: null, userName: null, email: null, roleId: null,
                region: null, isActive: true, businessUnitId: businessUnitId);

            foreach (var candidate in users)
            {
                if (candidate.Id == userId) continue;
                if (await IsSuperAdminCapableRoleAsync(candidate.RoleId, businessUnitId)) return false;
            }

            return true;
        }
    }
}
