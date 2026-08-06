using System.Security.Claims;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.DTOs.UserDTO;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// BEHAVIOURAL proof that privilege escalation through <see cref="UserController"/> is blocked by
/// the imperative <c>CanManageRoleAsync</c> guard (Controllers/UserController.cs:246 for Create,
/// :326 and :328 for Update) backed by <see cref="RoleGate"/>, plus the <c>callerId == id</c>
/// self-mutation check at :328.
///
/// Why this class exists: the attribute-reflection assertion it replaces
/// (<c>AdministrativeSecurityTests.UserRoleMutations_RequireRoleAdministrationAuthority</c>) could
/// only prove that a declaration was typed. It could not prove that a caller who reaches the
/// method body is actually stopped from handing themselves — or anyone else — a role they do not
/// outrank. Every test below drives the REAL controller with the REAL <see cref="RoleGate"/> over
/// a relational SQLite database, so each assertion is on the HTTP result, not on metadata.
///
/// IMPORTANT — these results are independent of <c>[RequireModulePermission]</c>. Those attributes
/// are enforced by the MVC authorization filter, which never runs in a unit-constructed
/// controller, so every test here models a caller who has ALREADY passed whatever declarative
/// gates the endpoint carries. That is deliberate: it isolates the imperative escalation control
/// and makes the coverage stable whether or not the debated
/// <c>[RequireModulePermission("Roles &amp; Permissions", Edit)]</c> attribute is present. See the
/// long note on <c>AdministrativeSecurityTests.UserMutations_DeclareUserAdministrationPermission</c>
/// for that policy question; it does not change any expectation below.
///
/// The caller under test is the role that whole debate is about: a "User Coordinator" that can
/// administer users and holds NO RBAC administration grant. Its escalation ceiling is what these
/// tests pin down.
/// </summary>
public sealed class UserAdministrationEscalationTests
{
    private const long Bu = 42;

    // Modules
    private const long ModuleUsersId = 101;
    private const long ModuleRbacId = 102;

    // Roles. Authority is the explicit Setup_Master.RoleRank column (RoleGate no longer looks at
    // the name at all), so each role below declares the tier that isolates which rule does the work.
    private const long RoleCoordinator = 1;  // the B7 pilot role: users yes, RBAC no. RoleRanks.Member.
    private const long RoleCompliance = 2;   // outranks Coordinator ONLY by permission set (same rank).
    private const long RoleSuperAdmin = 3;   // RoleRanks.Owner.
    private const long RoleFieldManager = 4; // outranks nobody by permissions; blocked purely by RoleRanks.Manager.
    private const long RoleExecutive = 5;    // strictly below Coordinator — the legitimate assignment target.

    private const long CallerUserId = 77;
    private const long OtherUserId = 78;

    // ---------------- seeding ----------------

    private static void SeedRbac(ErpRfqAutomationContext ctx)
    {
        Seed.EnsureBusinessUnit(ctx, Bu);

        ctx.Modules.AddRange(
            NewModule(ModuleUsersId, "Users"),
            NewModule(ModuleRbacId, "Roles & Permissions"));

        // Rank is now an explicit column, so these roles say what they are instead of being
        // reverse-engineered from their names. The tiers are the ones the old name rule produced,
        // so the escalation invariants under test are unchanged.
        ctx.SetupMasters.AddRange(
            NewRole(RoleCoordinator, "User Coordinator"),
            NewRole(RoleCompliance, "Compliance Officer"),
            NewRole(RoleSuperAdmin, "Super Admin", RoleRanks.Owner),
            NewRole(RoleFieldManager, "Field Manager", RoleRanks.Manager),
            NewRole(RoleExecutive, "Sales Executive"));

        // Coordinator: full user administration, zero RBAC administration.
        ctx.RolePermissions.Add(NewPermission(9001, RoleCoordinator, ModuleUsersId,
            canView: true, canCreate: true, canEdit: true, canDelete: false));

        // Compliance Officer: everything the Coordinator has, PLUS RBAC administration and delete.
        ctx.RolePermissions.Add(NewPermission(9002, RoleCompliance, ModuleUsersId,
            canView: true, canCreate: true, canEdit: true, canDelete: true));
        ctx.RolePermissions.Add(NewPermission(9003, RoleCompliance, ModuleRbacId,
            canView: true, canCreate: true, canEdit: true, canDelete: true));

        // Super Admin: satisfies module checks by RANK (RoleRanks.Owner); the row is incidental.
        ctx.RolePermissions.Add(NewPermission(9004, RoleSuperAdmin, ModuleUsersId,
            canView: true, canCreate: true, canEdit: true, canDelete: true));

        // Field Manager: strictly FEWER permissions than the Coordinator. If the caller is refused
        // this role, only the rank comparison can be responsible.
        ctx.RolePermissions.Add(NewPermission(9005, RoleFieldManager, ModuleUsersId, canView: true));

        // Sales Executive: strictly below the Coordinator on every flag.
        ctx.RolePermissions.Add(NewPermission(9006, RoleExecutive, ModuleUsersId, canView: true));

        ctx.SaveChanges();
    }

    private static Module NewModule(long id, string name) => new()
    {
        Id = id, ModuleName = name, IsActive = true, CreatedBy = "seed", CreatedOn = DateTime.UtcNow
    };

    private static SetupMaster NewRole(long id, string name, short rank = RoleRanks.Member) => new()
    {
        SetupId = id, SetupType = "role", SetupCode = name, SetupValue = name, RoleRank = rank,
        BusinessUnitId = Bu, IsActive = true, CreatedBy = "seed", CreatedOn = DateTime.UtcNow
    };

    private static RolePermission NewPermission(
        long id, long roleId, long moduleId,
        bool canView = false, bool canCreate = false, bool canEdit = false, bool canDelete = false) => new()
    {
        Id = id, RoleId = roleId, ModuleId = moduleId, BusinessUnitId = Bu,
        CanView = canView, CanCreate = canCreate, CanEdit = canEdit, CanDelete = canDelete,
        CreatedBy = "seed", CreatedOn = DateTime.UtcNow
    };

    // ---------------- harness ----------------

    /// <summary>
    /// Records whether a write actually happened, so "refused" means "no row was written" rather
    /// than only "an ActionResult of some shape came back".
    /// </summary>
    private sealed class RecordingUserRepository : IUserRepository
    {
        public User? Existing { get; set; }
        public bool Added { get; private set; }
        public bool Updated { get; private set; }
        public User? LastWritten { get; private set; }

        public Task AddAsync(User user) { Added = true; LastWritten = user; return Task.CompletedTask; }
        public Task UpdateAsync(User user) { Updated = true; LastWritten = user; return Task.CompletedTask; }
        public Task<User> GetByIdAsync(long id, long businessUnitId)
            => Existing is not null ? Task.FromResult(Existing) : throw new KeyNotFoundException();

        // Only reached by the last-administrator lockout guard; no test below is meant to hit it,
        // so it throws loudly rather than returning a roster that could mask a wrong code path.
        public Task<(IEnumerable<UserResponseDTO>, int TotalCount)> GetAllAsync(int pageNumber, int pageSize,
            long? id, string? userName, string? email, long? roleId, string? region, bool? isActive, long businessUnitId)
            => throw new NotSupportedException("Lockout-guard roster read was not expected in this test.");

        public Task DeleteAsync(long id, long businessUnitId) => throw new NotSupportedException();
        public Task<IEnumerable<RoleResponseDTO>> GetRolesAsync(long businessUnitId) => throw new NotSupportedException();
        public Task<IEnumerable<DTOs.TeamDTOs.TeamResponseDTO>> GetTeamsAsync(long businessUnitId) => throw new NotSupportedException();
        public Task<IEnumerable<DTOs.BusinessUnit.BusinessUnitResponseDTO>> GetBusinessUnitsAsync() => throw new NotSupportedException();
        public Task<IEnumerable<DTOs.UserGroup.UserGroupResponseDTO>> GetUserGroupsAsync(long businessUnitId) => throw new NotSupportedException();
        public Task ChangePasswordAsync(long id, string newPassword) => throw new NotSupportedException();
    }

    private sealed class StubWebHostEnvironment : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
        public string ApplicationName { get; set; } = "tests";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = "Test";
    }

    /// <summary>
    /// The REAL <see cref="RoleGate"/> over the seeded database — deliberately not the
    /// permissive stub used by the seat-limit tests, because the gate IS the control under test.
    /// </summary>
    private static UserController Controller(
        ErpRfqAutomationContext ctx, IMemoryCache cache, IUserRepository repo,
        long callerRoleId, long callerUserId = CallerUserId)
        => new(repo, new StubWebHostEnvironment(), new RoleGate(ctx, cache))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("roleId", callerRoleId.ToString()),
                        new Claim("businessUnitId", Bu.ToString()),
                        new Claim(ClaimTypes.NameIdentifier, callerUserId.ToString())
                    }, "test"))
                }
            }
        };

    private static UserCreateRequestDTO CreateRequest(long? roleId) => new()
    {
        FirstName = "New", LastName = "Hire", Email = "new.hire@example.com",
        Password = "pass-123", Buid = Bu, RoleId = roleId, IsActive = true
    };

    private static UserUpdateRequestDTO UpdateRequest(long? roleId, bool isActive = true) => new()
    {
        FirstName = "Existing", LastName = "Person", Email = "existing.person@example.com",
        Buid = Bu, RoleId = roleId, IsActive = isActive
    };

    private static User ExistingUser(long id, long? roleId, bool isActive = true) => new()
    {
        Id = id, FirstName = "Existing", LastName = "Person", Email = "existing.person@example.com",
        PasswordHash = "x", ImageUrl = string.Empty, RoleId = roleId, Buid = Bu, IsActive = isActive,
        CreatedBy = "seed", CreatedOn = DateTime.UtcNow
    };

    /// <summary>Runs the same <see cref="PermissionHandler"/> the [RequireModulePermission] filter uses.</summary>
    private static async Task<bool> AttributeWouldAllow(
        ErpRfqAutomationContext ctx, IMemoryCache cache, long roleId, string module, PermissionAction action)
    {
        var handler = new PermissionHandler(
            new RolePermissionRepository(ctx), new RoleGate(ctx, cache), cache,
            NullLogger<PermissionHandler>.Instance);
        var requirement = new PermissionRequirement(module, "Can" + action);
        var authContext = new AuthorizationHandlerContext(new[] { requirement }, ClaimsFor(roleId), null);
        await handler.HandleAsync(authContext);
        return authContext.HasSucceeded;
    }

    private static ClaimsPrincipal ClaimsFor(long roleId) => new(new ClaimsIdentity(new[]
    {
        new Claim("roleId", roleId.ToString()),
        new Claim("businessUnitId", Bu.ToString())
    }, "test"));

    // ---------------- 0. the premise B7 rests on ----------------

    /// <summary>
    /// Characterises the "User Coordinator" that every escalation test below uses: run through the
    /// real <see cref="PermissionHandler"/>, it PASSES "Users":Create and "Users":Edit and FAILS
    /// "Roles &amp; Permissions" outright.
    ///
    /// This is the exact role the B7 argument turns on, and it quantifies the trade-off rather
    /// than asserting a side:
    ///  - WITH <c>[RequireModulePermission("Roles &amp; Permissions", Edit)]</c> on Create/Update,
    ///    this role cannot call them at all — "administers users, grants no permissions" is not a
    ///    representable role, which is the cost B7 objected to.
    ///  - WITHOUT it, this role reaches the method body — and every test below shows what happens
    ///    then: it still cannot obtain, or confer, any role it does not outrank.
    /// </summary>
    [Fact]
    public async Task PilotUserAdministratorRole_PassesUsersGates_ButHasNoRbacAdministrationGrant()
    {
        using var db = new TestDb();
        using var ctx = db.ContextFor(Bu);
        SeedRbac(ctx);
        using var cache = new MemoryCache(new MemoryCacheOptions());

        Assert.True(await AttributeWouldAllow(ctx, cache, RoleCoordinator, "Users", PermissionAction.Create));
        Assert.True(await AttributeWouldAllow(ctx, cache, RoleCoordinator, "Users", PermissionAction.Edit));

        // The gate B7 removed from UserController — still correctly denied for this role, and still
        // enforced on RolePermissionController, which is where RBAC is actually administered.
        Assert.False(await AttributeWouldAllow(ctx, cache, RoleCoordinator, "Roles & Permissions", PermissionAction.Edit));
        Assert.False(await AttributeWouldAllow(ctx, cache, RoleCoordinator, "Roles & Permissions", PermissionAction.View));
    }

    // ---------------- 1. Create cannot grant a role the caller does not outrank ----------------

    [Theory]
    [InlineData(RoleCompliance)]   // outranked on permissions
    [InlineData(RoleSuperAdmin)]   // RoleRanks.Owner
    [InlineData(RoleFieldManager)] // RoleRanks.Manager, despite holding fewer permissions
    public async Task Create_ByNonPrivilegedUserAdministrator_ForbidsRoleTheCallerDoesNotOutrank(long targetRoleId)
    {
        using var db = new TestDb();
        using var ctx = db.ContextFor(Bu);
        SeedRbac(ctx);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var repo = new RecordingUserRepository();
        var controller = Controller(ctx, cache, repo, callerRoleId: RoleCoordinator);

        var result = await controller.Create(CreateRequest(targetRoleId));

        Assert.IsType<ForbidResult>(result.Result);
        Assert.False(repo.Added);
    }

    [Theory]
    [InlineData(RoleCoordinator)] // equal rank
    [InlineData(RoleExecutive)]   // strictly lower rank
    public async Task Create_ByNonPrivilegedUserAdministrator_AllowsRoleAtOrBelowItsOwnRank(long targetRoleId)
    {
        using var db = new TestDb();
        using var ctx = db.ContextFor(Bu);
        SeedRbac(ctx);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var repo = new RecordingUserRepository();
        var controller = Controller(ctx, cache, repo, callerRoleId: RoleCoordinator);

        var result = await controller.Create(CreateRequest(targetRoleId));

        // The negative cases above are only meaningful if this one succeeds — otherwise the guard
        // would be refusing everything and B7's pilot role would still be unusable.
        Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.True(repo.Added);
        Assert.Equal(targetRoleId, repo.LastWritten!.RoleId);
    }

    // ---------------- 2. Update cannot escalate an existing user ----------------

    [Theory]
    [InlineData(RoleCompliance)]
    [InlineData(RoleSuperAdmin)]
    [InlineData(RoleFieldManager)]
    public async Task Update_ByNonPrivilegedUserAdministrator_ForbidsEscalatingAnotherUser(long targetRoleId)
    {
        using var db = new TestDb();
        using var ctx = db.ContextFor(Bu);
        SeedRbac(ctx);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var repo = new RecordingUserRepository { Existing = ExistingUser(OtherUserId, RoleExecutive) };
        var controller = Controller(ctx, cache, repo, callerRoleId: RoleCoordinator);

        var result = await controller.Update(OtherUserId, UpdateRequest(targetRoleId));

        Assert.IsType<ForbidResult>(result);
        Assert.False(repo.Updated);
    }

    /// <summary>
    /// The complementary half of the guard (UserController.cs:316): the caller must also outrank
    /// the account's CURRENT role, or a user administrator could edit a super administrator's
    /// account (e-mail takeover, deactivation) without ever touching the role field.
    /// </summary>
    [Fact]
    public async Task Update_ByNonPrivilegedUserAdministrator_ForbidsTouchingAHigherRankedAccountAtAll()
    {
        using var db = new TestDb();
        using var ctx = db.ContextFor(Bu);
        SeedRbac(ctx);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var repo = new RecordingUserRepository { Existing = ExistingUser(OtherUserId, RoleSuperAdmin) };
        var controller = Controller(ctx, cache, repo, callerRoleId: RoleCoordinator);

        // Role unchanged — this is a plain profile edit on a super administrator's account.
        var result = await controller.Update(OtherUserId, UpdateRequest(RoleSuperAdmin));

        Assert.IsType<ForbidResult>(result);
        Assert.False(repo.Updated);
    }

    // ---------------- 3. Nobody changes their own role ----------------

    [Fact]
    public async Task Update_CannotChangeOwnRole_EvenAsSuperAdministrator()
    {
        using var db = new TestDb();
        using var ctx = db.ContextFor(Bu);
        SeedRbac(ctx);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var repo = new RecordingUserRepository { Existing = ExistingUser(CallerUserId, RoleSuperAdmin) };
        var controller = Controller(ctx, cache, repo, callerRoleId: RoleSuperAdmin, callerUserId: CallerUserId);

        // A super administrator outranks every role, so CanManageRoleAsync alone would allow this;
        // the refusal has to come from the explicit callerId == id self-mutation check.
        var result = await controller.Update(CallerUserId, UpdateRequest(RoleCoordinator));

        Assert.IsType<ForbidResult>(result);
        Assert.False(repo.Updated);
    }

    [Fact]
    public async Task Update_CannotSelfEscalate_AsNonPrivilegedUserAdministrator()
    {
        using var db = new TestDb();
        using var ctx = db.ContextFor(Bu);
        SeedRbac(ctx);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var repo = new RecordingUserRepository { Existing = ExistingUser(CallerUserId, RoleCoordinator) };
        var controller = Controller(ctx, cache, repo, callerRoleId: RoleCoordinator, callerUserId: CallerUserId);

        var result = await controller.Update(CallerUserId, UpdateRequest(RoleCompliance));

        Assert.IsType<ForbidResult>(result);
        Assert.False(repo.Updated);
    }

    [Fact]
    public async Task Update_SelfEditWithoutRoleChange_IsStillAllowed()
    {
        using var db = new TestDb();
        using var ctx = db.ContextFor(Bu);
        SeedRbac(ctx);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var repo = new RecordingUserRepository { Existing = ExistingUser(CallerUserId, RoleCoordinator) };
        var controller = Controller(ctx, cache, repo, callerRoleId: RoleCoordinator, callerUserId: CallerUserId);

        var result = await controller.Update(CallerUserId, UpdateRequest(RoleCoordinator));

        Assert.IsType<NoContentResult>(result);
        Assert.True(repo.Updated);
    }

    // ---------------- 4. A legitimately privileged caller can still do the job ----------------

    [Fact]
    public async Task Create_BySuperAdministrator_AssignsAnyRole()
    {
        using var db = new TestDb();
        using var ctx = db.ContextFor(Bu);
        SeedRbac(ctx);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var repo = new RecordingUserRepository();
        var controller = Controller(ctx, cache, repo, callerRoleId: RoleSuperAdmin);

        var result = await controller.Create(CreateRequest(RoleCompliance));

        Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.True(repo.Added);
        Assert.Equal(RoleCompliance, repo.LastWritten!.RoleId);
    }

    [Fact]
    public async Task Update_BySuperAdministrator_PromotesAnotherUser()
    {
        using var db = new TestDb();
        using var ctx = db.ContextFor(Bu);
        SeedRbac(ctx);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var repo = new RecordingUserRepository { Existing = ExistingUser(OtherUserId, RoleExecutive) };
        var controller = Controller(ctx, cache, repo, callerRoleId: RoleSuperAdmin, callerUserId: CallerUserId);

        var result = await controller.Update(OtherUserId, UpdateRequest(RoleCompliance));

        Assert.IsType<NoContentResult>(result);
        Assert.True(repo.Updated);
        Assert.Equal(RoleCompliance, repo.LastWritten!.RoleId);
    }

    /// <summary>
    /// The Compliance Officer holds no super-admin name and no RBAC-administration bypass; it
    /// outranks the Coordinator purely on its permission set, which is enough to manage it.
    /// This is the case that shows the gate ranks by GRANTS, not just by role naming.
    /// </summary>
    [Fact]
    public async Task Update_ByPermissionSuperset_PromotesALowerRankedUser()
    {
        using var db = new TestDb();
        using var ctx = db.ContextFor(Bu);
        SeedRbac(ctx);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var repo = new RecordingUserRepository { Existing = ExistingUser(OtherUserId, RoleExecutive) };
        var controller = Controller(ctx, cache, repo, callerRoleId: RoleCompliance, callerUserId: CallerUserId);

        var result = await controller.Update(OtherUserId, UpdateRequest(RoleCoordinator));

        Assert.IsType<NoContentResult>(result);
        Assert.True(repo.Updated);
    }

    // ---------------- 5. Fail-closed edges ----------------

    /// <summary>
    /// UserController.cs:660 — a token with no usable roleId claim has no rank, so it can manage
    /// no role at all. Worth pinning: after B7 this claim is the ONLY input to the escalation
    /// decision, so "missing claim" must mean deny, never allow.
    /// </summary>
    [Fact]
    public async Task Create_WithoutRoleIdClaim_IsForbiddenWhenARoleIsRequested()
    {
        using var db = new TestDb();
        using var ctx = db.ContextFor(Bu);
        SeedRbac(ctx);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var repo = new RecordingUserRepository();
        var controller = new UserController(repo, new StubWebHostEnvironment(), new RoleGate(ctx, cache))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("businessUnitId", Bu.ToString()),
                        new Claim(ClaimTypes.NameIdentifier, CallerUserId.ToString())
                    }, "test"))
                }
            }
        };

        var result = await controller.Create(CreateRequest(RoleExecutive));

        Assert.IsType<ForbidResult>(result.Result);
        Assert.False(repo.Added);
    }
}
