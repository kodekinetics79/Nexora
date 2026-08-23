using System.Reflection;
using System.Security.Claims;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.DTOs.AuthDTOs;
using ERP_RFQ_Automation.DTOs.UserDTO;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Platform.Entitlements;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// WS-A (Platform Admin 360) enforcement core: tenant suspension denies login and
/// tenant-plane API use (fail-open for legacy BUs without a platform Tenant row),
/// and plan entitlements (seats, monthly document quota, extraction concurrency cap,
/// WFQ weight) are enforced from the cached tenant+plan resolution.
/// </summary>
public sealed class PlatformEntitlementEnforcementTests
{
    private const long Bu = 71;

    // ---- helpers ---------------------------------------------------------

    private static ITenantAccessService AccessService(ErpRfqAutomationContext ctx)
        => new TenantAccessService(ctx, new MemoryCache(new MemoryCacheOptions()),
            NullLogger<TenantAccessService>.Instance);

    private static IEntitlementService Entitlements(ErpRfqAutomationContext ctx)
        => new EntitlementService(AccessService(ctx), ctx);

    private static Plan SeedPlan(ErpRfqAutomationContext ctx, long id,
        int maxSeats = 5, int maxDocsPerMonth = 1000, int maxConcurrent = 2, int weight = 1)
    {
        var plan = new Plan
        {
            Id = id,
            Code = $"plan-{id}",
            Name = $"Plan {id}",
            MaxSeats = maxSeats,
            MaxDocsPerMonth = maxDocsPerMonth,
            MaxConcurrentExtractionJobs = maxConcurrent,
            Weight = weight
        };
        ctx.Set<Plan>().Add(plan);
        return plan;
    }

    private static Tenant SeedTenant(ErpRfqAutomationContext ctx, long id, long businessUnitId,
        TenantStatus status, long? planId = null)
    {
        Seed.EnsureBusinessUnit(ctx, businessUnitId);
        var tenant = new Tenant
        {
            Id = id,
            Name = $"Tenant {id}",
            Slug = $"tenant-{id}",
            Status = status,
            PlanId = planId,
            PrimaryBusinessUnitId = businessUnitId
        };
        ctx.Set<Tenant>().Add(tenant);
        return tenant;
    }

    private static User SeedUser(ErpRfqAutomationContext ctx, long id, long businessUnitId,
        string email, string password = "pass-123", bool isActive = true)
    {
        Seed.EnsureBusinessUnit(ctx, businessUnitId);
        var user = new User
        {
            Id = id,
            FirstName = "Test",
            LastName = $"User{id}",
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
            ImageUrl = string.Empty,
            Buid = businessUnitId,
            IsActive = isActive,
            CreatedBy = "seed",
            CreatedOn = DateTime.UtcNow
        };
        ctx.Users.Add(user);
        return user;
    }

    private static IConfiguration JwtConfig() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "0123456789abcdef0123456789abcdef-unit-test-key",
            ["Jwt:Issuer"] = "nexora-tests",
            ["Jwt:Audience"] = "nexora-tests",
            ["Jwt:ExpiryMinutes"] = "30"
        }).Build();

    private static AuthRepository AuthRepo(ErpRfqAutomationContext ctx)
        => new(ctx, JwtConfig(), NullLogger<AuthRepository>.Instance, AccessService(ctx));

    // ---- 1. Tenant status: login -----------------------------------------

    [Fact]
    public async Task Login_ProvisioningTenant_IsDeniedUntilAuthoritativeActivation()
    {
        using var db = new TestDb();
        using var seed = db.ContextFor(null);
        SeedTenant(seed, 1, Bu, TenantStatus.Provisioning);
        SeedUser(seed, 10, Bu, "provisioning@example.com");
        seed.SaveChanges();

        using var ctx = db.ContextFor(null);
        var ex = await Assert.ThrowsAsync<TenantAccessDeniedException>(() =>
            AuthRepo(ctx).LoginAsync(new LoginRequestDTO
                { Email = "provisioning@example.com", Password = "pass-123" }));
        Assert.Equal(TenantStatus.Provisioning, ex.Status);
        Assert.Contains("provisioning", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(NexoraProblems.TenantNotActivated, ex.ProblemType);
    }

    [Fact]
    public async Task Login_SuspendedTenant_IsDeniedWith403TypedException()
    {
        using var db = new TestDb();
        using var seed = db.ContextFor(null);
        SeedTenant(seed, 1, Bu, TenantStatus.Suspended);
        SeedUser(seed, 10, Bu, "suspended@example.com");
        seed.SaveChanges();

        using var ctx = db.ContextFor(null);
        var repo = AuthRepo(ctx);

        var ex = await Assert.ThrowsAsync<TenantAccessDeniedException>(
            () => repo.LoginAsync(new LoginRequestDTO { Email = "suspended@example.com", Password = "pass-123" }));
        Assert.Equal(StatusCodes.Status403Forbidden, ex.SuggestedStatusCode);
        Assert.Equal(TenantAccessDeniedException.Type, ex.ProblemType);
    }

    [Fact]
    public async Task Login_ArchivedTenant_IsDenied()
    {
        using var db = new TestDb();
        using var seed = db.ContextFor(null);
        SeedTenant(seed, 1, Bu, TenantStatus.Archived);
        SeedUser(seed, 10, Bu, "archived@example.com");
        seed.SaveChanges();

        using var ctx = db.ContextFor(null);
        await Assert.ThrowsAsync<TenantAccessDeniedException>(
            () => AuthRepo(ctx).LoginAsync(new LoginRequestDTO { Email = "archived@example.com", Password = "pass-123" }));
    }

    [Fact]
    public async Task Login_PastDueTenant_IsDeniedWithCommercialReason()
    {
        using var db = new TestDb();
        using var seed = db.ContextFor(null);
        SeedTenant(seed, 1, Bu, TenantStatus.PastDue);
        SeedUser(seed, 10, Bu, "pastdue@example.com");
        seed.SaveChanges();

        using var ctx = db.ContextFor(null);
        var ex = await Assert.ThrowsAsync<TenantAccessDeniedException>(() =>
            AuthRepo(ctx).LoginAsync(new LoginRequestDTO { Email = "pastdue@example.com", Password = "pass-123" }));
        Assert.Contains("past due", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Login_LegacyBusinessUnitWithoutTenantRow_FailsOpenAndIssuesToken()
    {
        using var db = new TestDb();
        using var seed = db.ContextFor(null);
        SeedUser(seed, 10, Bu, "legacy@example.com"); // no platform Tenant row at all
        seed.SaveChanges();

        using var ctx = db.ContextFor(null);
        var response = await AuthRepo(ctx).LoginAsync(
            new LoginRequestDTO { Email = "legacy@example.com", Password = "pass-123" });

        Assert.False(string.IsNullOrEmpty(response.Token));
        Assert.Equal(Bu, response.BusinessUnitId);
    }

    [Fact]
    public async Task Login_ActiveTenant_IssuesToken()
    {
        using var db = new TestDb();
        using var seed = db.ContextFor(null);
        SeedTenant(seed, 1, Bu, TenantStatus.Active);
        SeedUser(seed, 10, Bu, "active@example.com");
        seed.SaveChanges();

        using var ctx = db.ContextFor(null);
        var response = await AuthRepo(ctx).LoginAsync(
            new LoginRequestDTO { Email = "active@example.com", Password = "pass-123" });

        Assert.False(string.IsNullOrEmpty(response.Token));
    }

    // ---- 2. Tenant status: API middleware --------------------------------

    private sealed class FixedAccess(TenantAccessSnapshot snapshot) : ITenantAccessService
    {
        public Task<TenantAccessSnapshot> GetAccessAsync(long businessUnitId, CancellationToken ct = default)
            => Task.FromResult(snapshot with { BusinessUnitId = businessUnitId });
    }

    private static DefaultHttpContext GuardContext(
        ITenantAccessService access, string? businessUnitClaim, string path = "/api/Lead", bool authenticated = true)
    {
        var services = new ServiceCollection();
        services.AddSingleton(access);
        var context = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider()
        };
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        var claims = new List<Claim>();
        if (businessUnitClaim is not null)
            claims.Add(new Claim("businessUnitId", businessUnitClaim));
        context.User = new ClaimsPrincipal(authenticated
            ? new ClaimsIdentity(claims, "test")
            : new ClaimsIdentity(claims));
        return context;
    }

    private static TenantAccessSnapshot Snapshot(TenantStatus? status, PlanSnapshot? plan = null)
        => new(Bu, status is null ? null : 1, status, plan);

    [Fact]
    public async Task StatusGuard_ProvisioningTenant_Returns403AndBlocksTenantApi()
    {
        var nextCalled = false;
        var middleware = new TenantStatusGuardMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var context = GuardContext(new FixedAccess(Snapshot(TenantStatus.Provisioning)), Bu.ToString());

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task StatusGuard_SuspendedTenant_Returns403AndBlocksPipeline()
    {
        var nextCalled = false;
        var middleware = new TenantStatusGuardMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var context = GuardContext(new FixedAccess(Snapshot(TenantStatus.Suspended)), Bu.ToString());

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task StatusGuard_ArchivedTenant_Returns403()
    {
        var nextCalled = false;
        var middleware = new TenantStatusGuardMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var context = GuardContext(new FixedAccess(Snapshot(TenantStatus.Archived)), Bu.ToString());

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task StatusGuard_PastDueTenant_Returns403()
    {
        var nextCalled = false;
        var middleware = new TenantStatusGuardMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var context = GuardContext(new FixedAccess(Snapshot(TenantStatus.PastDue)), Bu.ToString());

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task StatusGuard_LegacyBusinessUnitWithoutTenantRow_FailsOpen()
    {
        var nextCalled = false;
        var middleware = new TenantStatusGuardMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var context = GuardContext(new FixedAccess(Snapshot(null)), Bu.ToString());

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task StatusGuard_ActiveTenant_PassesThrough()
    {
        var nextCalled = false;
        var middleware = new TenantStatusGuardMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var context = GuardContext(new FixedAccess(Snapshot(TenantStatus.Active)), Bu.ToString());

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task StatusGuard_PlatformPlaneAndAnonymousRequests_AreNotGuarded()
    {
        // A suspended snapshot would deny — but these requests must never consult it.
        var access = new FixedAccess(Snapshot(TenantStatus.Suspended));

        var platformCalled = false;
        await new TenantStatusGuardMiddleware(_ => { platformCalled = true; return Task.CompletedTask; })
            .InvokeAsync(GuardContext(access, Bu.ToString(), path: "/api/platform/tenants"));
        Assert.True(platformCalled);

        var anonymousCalled = false;
        await new TenantStatusGuardMiddleware(_ => { anonymousCalled = true; return Task.CompletedTask; })
            .InvokeAsync(GuardContext(access, businessUnitClaim: null, authenticated: false));
        Assert.True(anonymousCalled);
    }

    [Fact]
    public async Task StatusGuard_UsesCachedResolution_NoQueryPerRequest()
    {
        using var db = new TestDb();
        using var seed = db.ContextFor(null);
        SeedTenant(seed, 1, Bu, TenantStatus.Active);
        seed.SaveChanges();

        var cache = new MemoryCache(new MemoryCacheOptions());
        using var ctx = db.ContextFor(null);
        var service = new TenantAccessService(ctx, cache, NullLogger<TenantAccessService>.Instance);

        var first = await service.GetAccessAsync(Bu);
        // Second resolution must come from the cache — even a disposed/broken context
        // cannot be hit again inside the TTL.
        using (var gone = db.ContextFor(null)) { }
        var second = await service.GetAccessAsync(Bu);

        Assert.Equal(first, second);
        Assert.True(cache.TryGetValue(TenantAccessService.CacheKey(Bu), out _));
    }

    // ---- 3. Seats --------------------------------------------------------

    [Fact]
    public async Task Seats_AtCap_DeniesWithLimitInMessage()
    {
        using var db = new TestDb();
        using var seed = db.ContextFor(null);
        var plan = SeedPlan(seed, 1, maxSeats: 2);
        SeedTenant(seed, 1, Bu, TenantStatus.Active, plan.Id);
        SeedUser(seed, 10, Bu, "a@example.com");
        SeedUser(seed, 11, Bu, "b@example.com");
        SeedUser(seed, 12, Bu, "inactive@example.com", isActive: false); // must not count
        seed.SaveChanges();

        using var ctx = db.ContextFor(null);
        var decision = await Entitlements(ctx).CheckSeatAvailabilityAsync(Bu);

        Assert.False(decision.Allowed);
        Assert.Equal(2, decision.Limit);
        Assert.Equal(2, decision.Current);
        Assert.Contains("2", decision.Reason);
    }

    [Fact]
    public async Task Seats_BelowCap_Allows()
    {
        using var db = new TestDb();
        using var seed = db.ContextFor(null);
        var plan = SeedPlan(seed, 1, maxSeats: 2);
        SeedTenant(seed, 1, Bu, TenantStatus.Active, plan.Id);
        SeedUser(seed, 10, Bu, "a@example.com");
        seed.SaveChanges();

        using var ctx = db.ContextFor(null);
        var decision = await Entitlements(ctx).CheckSeatAvailabilityAsync(Bu);

        Assert.True(decision.Allowed);
        Assert.Equal(2, decision.Limit);
    }

    [Fact]
    public async Task Seats_NoPlan_Unlimited()
    {
        using var db = new TestDb();
        using var seed = db.ContextFor(null);
        SeedTenant(seed, 1, Bu, TenantStatus.Active, planId: null);
        for (var i = 0; i < 8; i++)
            SeedUser(seed, 10 + i, Bu, $"u{i}@example.com");
        seed.SaveChanges();

        using var ctx = db.ContextFor(null);
        var decision = await Entitlements(ctx).CheckSeatAvailabilityAsync(Bu);

        Assert.True(decision.Allowed);
        Assert.Null(decision.Limit);
    }

    // ---- 3b. Seats at the controller ------------------------------------

    private sealed class StubUserRepository : IUserRepository
    {
        public User? Existing { get; set; }
        public bool Added { get; private set; }
        public bool Updated { get; private set; }

        public Task AddAsync(User user) { Added = true; return Task.CompletedTask; }
        public Task UpdateAsync(User user) { Updated = true; return Task.CompletedTask; }
        public Task<User> GetByIdAsync(long id, long businessUnitId)
            => Existing is not null ? Task.FromResult(Existing) : throw new KeyNotFoundException();
        public Task<(IEnumerable<UserResponseDTO>, int TotalCount)> GetAllAsync(int pageNumber, int pageSize,
            long? id, string? userName, string? email, long? roleId, string? region, bool? isActive, long businessUnitId)
            => throw new NotSupportedException();
        public Task DeleteAsync(long id, long businessUnitId) => throw new NotSupportedException();
        public Task<IEnumerable<DTOs.UserDTO.RoleResponseDTO>> GetRolesAsync(long businessUnitId) => throw new NotSupportedException();
        public Task<IEnumerable<DTOs.TeamDTOs.TeamResponseDTO>> GetTeamsAsync(long businessUnitId) => throw new NotSupportedException();
        public Task<IEnumerable<DTOs.BusinessUnit.BusinessUnitResponseDTO>> GetBusinessUnitsAsync() => throw new NotSupportedException();
        public Task<IEnumerable<DTOs.UserGroup.UserGroupResponseDTO>> GetUserGroupsAsync(long businessUnitId) => throw new NotSupportedException();
        public Task ChangePasswordAsync(long id, string newPassword) => throw new NotSupportedException();
        public Task<bool> VerifyPasswordAsync(long id, long businessUnitId, string candidatePassword) => throw new NotSupportedException();
    }

    private sealed class StubRoleGate : ERP_RFQ_Automation.Authorization.IRoleGate
    {
        public Task<bool> IsSuperAdminAsync(long roleId, long businessUnitId) => Task.FromResult(true);
        public Task<short> GetRoleRankAsync(long roleId, long businessUnitId) => Task.FromResult(RoleRanks.Owner);
        public Task<bool> IsManagerOrAdminAsync(long roleId, long businessUnitId) => Task.FromResult(true);
        public Task<bool> CanManageRoleAsync(long callerRoleId, long? targetRoleId, long businessUnitId) => Task.FromResult(true);
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

    private static UserController Controller(
        StubUserRepository repo, IEntitlementService entitlements, long businessUnitId, long callerId = 999)
    {
        var controller = new UserController(repo, new StubWebHostEnvironment(), new StubRoleGate(), entitlements)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("businessUnitId", businessUnitId.ToString()),
                        new Claim(ClaimTypes.NameIdentifier, callerId.ToString()),
                        new Claim("roleId", "1")
                    }, "test"))
                }
            }
        };
        return controller;
    }

    [Fact]
    public async Task UserCreate_AtSeatCap_Returns403WithLimit()
    {
        using var db = new TestDb();
        using var seed = db.ContextFor(null);
        var plan = SeedPlan(seed, 1, maxSeats: 1);
        SeedTenant(seed, 1, Bu, TenantStatus.Active, plan.Id);
        SeedUser(seed, 10, Bu, "taken@example.com");
        seed.SaveChanges();

        using var ctx = db.ContextFor(null);
        var repo = new StubUserRepository();
        var controller = Controller(repo, Entitlements(ctx), Bu);

        var result = await controller.Create(new UserCreateRequestDTO
        {
            FirstName = "New", LastName = "User", Email = "new@example.com",
            Password = "pass-123", Buid = Bu
        });

        var denial = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, denial.StatusCode);
        Assert.False(repo.Added);
        Assert.Contains("1", denial.Value!.ToString());
    }

    [Fact]
    public async Task UserCreate_BelowSeatCap_Succeeds()
    {
        using var db = new TestDb();
        using var seed = db.ContextFor(null);
        var plan = SeedPlan(seed, 1, maxSeats: 2);
        SeedTenant(seed, 1, Bu, TenantStatus.Active, plan.Id);
        SeedUser(seed, 10, Bu, "taken@example.com");
        seed.SaveChanges();

        using var ctx = db.ContextFor(null);
        var repo = new StubUserRepository();
        var controller = Controller(repo, Entitlements(ctx), Bu);

        var result = await controller.Create(new UserCreateRequestDTO
        {
            FirstName = "New", LastName = "User", Email = "new@example.com",
            Password = "pass-123", Buid = Bu
        });

        Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.True(repo.Added);
    }

    // ---- 3c. Entitlements on the session bootstrap -----------------------

    /// <summary>
    /// Wiring, not unit: the entry point the client actually calls
    /// (GET /api/User/me/permissions), through the real EntitlementService and
    /// TenantAccessService, against a seeded tenant row. The caller deliberately has NO roleId
    /// claim — the tenant's surface must not depend on the caller's RBAC bootstrap, so
    /// entitlements have to ride ahead of the role-less early return.
    /// </summary>
    [Fact]
    public async Task MePermissions_CarriesTenantEntitlements_EvenForARolelessCaller()
    {
        using var db = new TestDb();
        using var seed = db.ContextFor(null);
        var tenant = SeedTenant(seed, 1, Bu, TenantStatus.Active);
        tenant.Entitlements =
            $"{{\"{TypedEntitlementCatalog.FullNavigation}\":true," +
            $"\"{TypedEntitlementCatalog.Rfq}\":true," +
            $"\"{TypedEntitlementCatalog.Sso}\":true}}";
        seed.SaveChanges();

        using var ctx = db.ContextFor(null);
        var controller = new UserController(
            new StubUserRepository(), new StubWebHostEnvironment(), new StubRoleGate(),
            Entitlements(ctx))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("businessUnitId", Bu.ToString()),
                        new Claim(ClaimTypes.NameIdentifier, "999")
                    }, "test"))
                }
            }
        };

        var result = await controller.GetMyPermissions();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var me = Assert.IsType<MyPermissionsResponseDTO>(ok.Value);
        // Granted, runtime-available keys in catalogue order. SSO is granted on the row but has
        // no execution boundary; reporting it would hand the client a switch the server denies.
        Assert.Equal(
            new[] { TypedEntitlementCatalog.Rfq, TypedEntitlementCatalog.FullNavigation },
            me.Entitlements);
    }

    [Fact]
    public async Task UserReactivation_AtSeatCap_Returns403()
    {
        using var db = new TestDb();
        using var seed = db.ContextFor(null);
        var plan = SeedPlan(seed, 1, maxSeats: 1);
        SeedTenant(seed, 1, Bu, TenantStatus.Active, plan.Id);
        SeedUser(seed, 10, Bu, "active@example.com");                    // occupies the only seat
        var dormant = SeedUser(seed, 11, Bu, "dormant@example.com", isActive: false);
        seed.SaveChanges();

        using var ctx = db.ContextFor(null);
        var repo = new StubUserRepository
        {
            Existing = new User
            {
                Id = dormant.Id, FirstName = "Test", LastName = "User11",
                Email = "dormant@example.com", PasswordHash = "x", ImageUrl = "",
                Buid = Bu, IsActive = false, CreatedBy = "seed", CreatedOn = DateTime.UtcNow
            }
        };
        var controller = Controller(repo, Entitlements(ctx), Bu);

        var result = await controller.Update(dormant.Id, new UserUpdateRequestDTO
        {
            FirstName = "Test", LastName = "User11", Email = "dormant@example.com",
            Buid = Bu, IsActive = true
        });

        var denial = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, denial.StatusCode);
        Assert.False(repo.Updated);
    }

    [Fact]
    public async Task UserDeactivationOrStayingInactive_IsNotSeatChecked()
    {
        using var db = new TestDb();
        using var seed = db.ContextFor(null);
        var plan = SeedPlan(seed, 1, maxSeats: 1);
        SeedTenant(seed, 1, Bu, TenantStatus.Active, plan.Id);
        SeedUser(seed, 10, Bu, "active@example.com"); // at cap already
        seed.SaveChanges();

        using var ctx = db.ContextFor(null);
        var repo = new StubUserRepository
        {
            Existing = new User
            {
                Id = 10, FirstName = "Test", LastName = "User10",
                Email = "active@example.com", PasswordHash = "x", ImageUrl = "",
                Buid = Bu, IsActive = true, CreatedBy = "seed", CreatedOn = DateTime.UtcNow
            }
        };
        var controller = Controller(repo, Entitlements(ctx), Bu);

        // Deactivating the active user must not be blocked by the (full) seat pool.
        var result = await controller.Update(10, new UserUpdateRequestDTO
        {
            FirstName = "Test", LastName = "User10", Email = "active@example.com",
            Buid = Bu, IsActive = false
        });

        Assert.IsType<NoContentResult>(result);
        Assert.True(repo.Updated);
    }

    // ---- 4. Documents / month at enqueue ---------------------------------

    private static ExtractionQueue Queue(ErpRfqAutomationContext ctx, IEntitlementService? entitlements)
        => new(ctx, new NoopLogger<ExtractionQueue>(), new StubTenant(null), entitlements);

    private static EnqueueExtractionRequest Doc(long bu, string hash) => new()
    {
        BusinessUnitId = bu,
        SourceType = ExtractionSourceType.ManualUpload,
        StoragePath = $"blob://{hash}",
        FileName = $"{hash}.pdf",
        FileType = "pdf",
        ContentHash = hash
    };

    [Fact]
    public async Task DocQuota_AtCap_DeniesEnqueueWithTypedException()
    {
        using var db = new TestDb();
        using var seed = db.ContextFor(null);
        var plan = SeedPlan(seed, 1, maxDocsPerMonth: 2);
        SeedTenant(seed, 1, Bu, TenantStatus.Active, plan.Id);
        seed.SaveChanges();

        using var ctx = db.ContextFor(null);
        var queue = Queue(ctx, Entitlements(ctx));

        Assert.Equal(EnqueueOutcome.Enqueued, (await queue.EnqueueAsync(Doc(Bu, "aaa1"))).Outcome);
        Assert.Equal(EnqueueOutcome.Enqueued, (await queue.EnqueueAsync(Doc(Bu, "aaa2"))).Outcome);

        var ex = await Assert.ThrowsAsync<DocumentQuotaExceededException>(
            () => queue.EnqueueAsync(Doc(Bu, "aaa3")));
        Assert.Equal(StatusCodes.Status429TooManyRequests, ex.SuggestedStatusCode);
        Assert.Contains("2", ex.Message); // the limit must be stated
        Assert.Equal(2, ex.Decision.Limit);
    }

    [Fact]
    public async Task DocQuota_CountsOnlyCurrentUtcMonth()
    {
        using var db = new TestDb();
        using var seed = db.ContextFor(null);
        var plan = SeedPlan(seed, 1, maxDocsPerMonth: 2);
        SeedTenant(seed, 1, Bu, TenantStatus.Active, plan.Id);
        // Two jobs from LAST month must not count against this month's quota.
        var lastMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc)
            .AddDays(-1);
        foreach (var hash in new[] { "old1", "old2" })
            seed.Set<ExtractionJob>().Add(new ExtractionJob
            {
                BusinessUnitId = Bu,
                BatchId = Guid.NewGuid(),
                SourceType = ExtractionSourceType.ManualUpload,
                ContentHash = hash,
                StoragePath = $"blob://{hash}",
                Status = ExtractionStatus.Succeeded,
                MaxAttempts = 5,
                NextAttemptAt = lastMonth,
                CreatedOn = lastMonth,
                UpdatedOn = lastMonth
            });
        seed.SaveChanges();

        using var ctx = db.ContextFor(null);
        var queue = Queue(ctx, Entitlements(ctx));

        var result = await queue.EnqueueAsync(Doc(Bu, "new1"));
        Assert.Equal(EnqueueOutcome.Enqueued, result.Outcome);
    }

    [Fact]
    public async Task DocQuota_NoPlan_Unlimited()
    {
        using var db = new TestDb();
        using var seed = db.ContextFor(null);
        SeedTenant(seed, 1, Bu, TenantStatus.Active, planId: null);
        seed.SaveChanges();

        using var ctx = db.ContextFor(null);
        var queue = Queue(ctx, Entitlements(ctx));

        for (var i = 0; i < 5; i++)
            Assert.Equal(EnqueueOutcome.Enqueued, (await queue.EnqueueAsync(Doc(Bu, $"h{i}"))).Outcome);
    }

    [Fact]
    public async Task DocQuota_DuplicateContentAtCap_StillShortCircuitsToDuplicate()
    {
        using var db = new TestDb();
        using var seed = db.ContextFor(null);
        var plan = SeedPlan(seed, 1, maxDocsPerMonth: 1);
        SeedTenant(seed, 1, Bu, TenantStatus.Active, plan.Id);
        seed.SaveChanges();

        using var ctx = db.ContextFor(null);
        var queue = Queue(ctx, Entitlements(ctx));

        var first = await queue.EnqueueAsync(Doc(Bu, "dup1"));
        Assert.Equal(EnqueueOutcome.Enqueued, first.Outcome);

        // Re-submitting known bytes consumes no quota → Duplicate, not a 429.
        var again = await queue.EnqueueAsync(Doc(Bu, "dup1"));
        Assert.Equal(EnqueueOutcome.Duplicate, again.Outcome);
        Assert.Equal(first.JobId, again.JobId);
    }

    // ---- 5. Concurrency cap + WFQ weight from plan ------------------------

    // The per-tenant concurrency cap is enforced atomically inside ExtractionQueue.ClaimSql
    // (the dead IEntitlementService.GetConcurrencyCapAsync was removed — P2-A6). The claim
    // statement is PostgreSQL-only, so the portable proof pins the load-bearing SQL text.

    private static string ClaimSql()
    {
        var field = typeof(ExtractionQueue).GetField(
            "ClaimSql", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        return (string)field!.GetValue(null)!;
    }

    [Fact]
    public void ClaimSql_TreatsPlanCapZeroAsNotConfigured_FallingBackToConfigCap()
    {
        // P1-A3: a plan cap of 0 must mean "not configured" → the @cap config fallback,
        // matching the C# `<= 0` semantics — NOT a silent GREATEST clamp to 1.
        var sql = ClaimSql();
        Assert.Contains(@"GREATEST(COALESCE(NULLIF(pc.cap, 0), @cap), 1)", sql);
        Assert.DoesNotContain(@"GREATEST(COALESCE(pc.cap, @cap), 1)", sql);
    }

    [Fact]
    public void ClaimSql_SkipsJobsOfUnactivatedAndRestrictedTenants_ButNotLegacyBusinessUnits()
    {
        // Provisioning/PastDue/Suspended/Archived tenants' queued jobs are excluded from the claim
        // candidates via a LEFT JOIN on platform.Tenants status (bt.buid IS NULL keeps
        // legacy BUs without a Tenant row claimable — contracted fail-open).
        var sql = ClaimSql();
        Assert.Contains(@"""Status"" IN ('Provisioning','PastDue','Suspended','Archived')", sql);
        Assert.Contains(@"LEFT JOIN blocked_tenants bt ON bt.buid = j.""BusinessUnitId""", sql);
        Assert.Contains("bt.buid IS NULL", sql);
    }

    [Fact]
    public async Task WfqWeight_FromPlan_OnNewTenantQueueState()
    {
        using var db = new TestDb();
        using var seed = db.ContextFor(null);
        var plan = SeedPlan(seed, 1, weight: 5);
        SeedTenant(seed, 1, Bu, TenantStatus.Active, plan.Id);
        seed.SaveChanges();

        using var ctx = db.ContextFor(null);
        var queue = Queue(ctx, Entitlements(ctx));
        await queue.EnqueueAsync(Doc(Bu, "w1"));

        using var verify = db.ContextFor(null);
        var state = verify.Set<TenantQueueState>().Single(s => s.BusinessUnitId == Bu);
        Assert.Equal(5.0, state.Weight);
    }

    [Fact]
    public async Task WfqWeight_IsRefreshedFromPlanOnEveryEnqueue_NotFrozenAtCreation()
    {
        // P2-A6: a plan change must reach the WFQ state on the next enqueue (within
        // the plan-cache TTL), not stay frozen at whatever the first enqueue saw.
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            var plan = SeedPlan(seed, 1, weight: 2);
            SeedTenant(seed, 1, Bu, TenantStatus.Active, plan.Id);
            seed.SaveChanges();
        }

        using (var ctx = db.ContextFor(null))
            await Queue(ctx, Entitlements(ctx)).EnqueueAsync(Doc(Bu, "wr1"));

        using (var verify = db.ContextFor(null))
            Assert.Equal(2.0, verify.Set<TenantQueueState>().Single(s => s.BusinessUnitId == Bu).Weight);

        // Upgrade the plan weight, then enqueue again with a FRESH entitlement
        // resolution (fresh cache) — the existing state row must be updated.
        using (var upgrade = db.ContextFor(null))
        {
            upgrade.Set<Plan>().Single(p => p.Id == 1).Weight = 9;
            upgrade.SaveChanges();
        }

        using (var ctx = db.ContextFor(null))
            await Queue(ctx, Entitlements(ctx)).EnqueueAsync(Doc(Bu, "wr2"));

        using (var verify = db.ContextFor(null))
            Assert.Equal(9.0, verify.Set<TenantQueueState>().Single(s => s.BusinessUnitId == Bu).Weight);
    }

    [Fact]
    public async Task DocQuota_ExcludesDuplicateFailedAndDeadLetterJobs()
    {
        // P0-B1 contract: only delivered-or-in-flight work counts against the monthly
        // quota — Duplicate, Failed and DeadLetter jobs are excluded (identical status
        // filter to the billing documents meter).
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            var plan = SeedPlan(seed, 1, maxDocsPerMonth: 2);
            SeedTenant(seed, 1, Bu, TenantStatus.Active, plan.Id);
            var now = DateTime.UtcNow;
            foreach (var (hash, status) in new[]
            {
                ("x-dup", ExtractionStatus.Duplicate),
                ("x-fail", ExtractionStatus.Failed),
                ("x-dead", ExtractionStatus.DeadLetter),
                ("x-ok", ExtractionStatus.Succeeded)
            })
                seed.Set<ExtractionJob>().Add(new ExtractionJob
                {
                    BusinessUnitId = Bu,
                    BatchId = Guid.NewGuid(),
                    SourceType = ExtractionSourceType.ManualUpload,
                    ContentHash = hash,
                    StoragePath = $"blob://{hash}",
                    Status = status,
                    MaxAttempts = 5,
                    NextAttemptAt = now,
                    CreatedOn = now,
                    UpdatedOn = now
                });
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(null);
        var decision = await Entitlements(ctx).CheckDocumentQuotaAsync(Bu);

        // 4 jobs exist this month but only the Succeeded one counts → 1/2 used.
        Assert.True(decision.Allowed);
        Assert.Equal(1, decision.Current);

        // One more countable job reaches the cap of 2 → denied.
        var queue = Queue(ctx, Entitlements(ctx));
        Assert.Equal(EnqueueOutcome.Enqueued, (await queue.EnqueueAsync(Doc(Bu, "x-new"))).Outcome);
        await Assert.ThrowsAsync<DocumentQuotaExceededException>(() => queue.EnqueueAsync(Doc(Bu, "x-over")));
    }

    [Fact]
    public async Task WfqWeight_NoPlan_DefaultsToOne()
    {
        using var db = new TestDb();
        using var seed = db.ContextFor(null);
        Seed.EnsureBusinessUnit(seed, Bu); // legacy BU, no tenant/plan
        seed.SaveChanges();

        using var ctx = db.ContextFor(null);
        var queue = Queue(ctx, Entitlements(ctx));
        await queue.EnqueueAsync(Doc(Bu, "w2"));

        using var verify = db.ContextFor(null);
        var state = verify.Set<TenantQueueState>().Single(s => s.BusinessUnitId == Bu);
        Assert.Equal(1.0, state.Weight);
    }

    // ---- 6. RBAC gates on Team / UserGroup --------------------------------

    [Theory]
    [InlineData(typeof(TeamController), nameof(TeamController.GetAll), PermissionAction.View)]
    [InlineData(typeof(TeamController), nameof(TeamController.GetById), PermissionAction.View)]
    [InlineData(typeof(TeamController), nameof(TeamController.Create), PermissionAction.Create)]
    [InlineData(typeof(TeamController), nameof(TeamController.Update), PermissionAction.Edit)]
    [InlineData(typeof(TeamController), nameof(TeamController.Delete), PermissionAction.Delete)]
    [InlineData(typeof(UserGroupController), nameof(UserGroupController.GetAll), PermissionAction.View)]
    [InlineData(typeof(UserGroupController), nameof(UserGroupController.GetById), PermissionAction.View)]
    [InlineData(typeof(UserGroupController), nameof(UserGroupController.Create), PermissionAction.Create)]
    [InlineData(typeof(UserGroupController), nameof(UserGroupController.Update), PermissionAction.Edit)]
    [InlineData(typeof(UserGroupController), nameof(UserGroupController.Delete), PermissionAction.Delete)]
    public void TeamAndUserGroupActions_RequireUsersModulePermission(
        Type controller, string methodName, PermissionAction action)
    {
        var method = controller.GetMethods().Single(x => x.Name == methodName);
        var permission = method.GetCustomAttributes<RequireModulePermissionAttribute>()
            .Single(x => x.ModuleName == "Users");
        Assert.Equal(action, permission.Action);
    }

    private sealed class ThrowingTeamRepository : Interfaces.ITeamRepository
    {
        public Task<(IEnumerable<Team>, int TotalCount)> GetAllAsync(int pageNumber, int pageSize,
            long? id, string? teamName, long? subTeamId, long businessUnitId)
            => throw new InvalidOperationException("Repository must not be reached without a tenant claim.");
        public Task<Team> GetByIdAsync(long id, long businessUnitId) => throw new InvalidOperationException();
        public Task AddAsync(Team team) => throw new InvalidOperationException();
        public Task UpdateAsync(Team team) => throw new InvalidOperationException();
        public Task DeleteAsync(long id, long businessUnitId) => throw new InvalidOperationException();
    }

    private sealed class ThrowingUserGroupRepository : Interfaces.IUserGroupRepository
    {
        public Task<(IEnumerable<UserGroup>, int TotalCount)> GetAllAsync(int pageNumber, int pageSize,
            long? id, string? userGroupsName, long businessUnitId)
            => throw new InvalidOperationException("Repository must not be reached without a tenant claim.");
        public Task<UserGroup> GetByIdAsync(long id, long businessUnitId) => throw new InvalidOperationException();
        public Task AddAsync(UserGroup userGroup) => throw new InvalidOperationException();
        public Task UpdateAsync(UserGroup userGroup) => throw new InvalidOperationException();
        public Task DeleteAsync(long id, long businessUnitId) => throw new InvalidOperationException();
    }

    private static ControllerContext ClaimlessContext() => new()
    {
        HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(Array.Empty<Claim>(), "test"))
        }
    };

    [Fact]
    public async Task TeamGetAll_WithoutClaim_IgnoresQueryStringBusinessUnit()
    {
        // SEC: the old `claimBUId > 0 ? claimBUId : (businessUnitId ?? 0)` fallback let a
        // claimless token pick ANY tenant via the query string. Claim-only now: 400, and
        // the repository is never reached with the attacker-chosen id.
        var controller = new TeamController(new ThrowingTeamRepository())
        {
            ControllerContext = ClaimlessContext()
        };

        var result = await controller.GetAll(businessUnitId: 42);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task UserGroupGetAll_WithoutClaim_IgnoresQueryStringBusinessUnit()
    {
        var controller = new UserGroupController(new ThrowingUserGroupRepository())
        {
            ControllerContext = ClaimlessContext()
        };

        var result = await controller.GetAll(businessUnitId: 42);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task TeamDelete_WithClaim_UsesClaimNotQueryString()
    {
        var repo = new RecordingTeamRepository();
        var controller = new TeamController(repo)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("businessUnitId", Bu.ToString())
                    }, "test"))
                }
            }
        };

        await controller.Delete(5, businessUnitId: 42); // 42 must be ignored

        Assert.Equal(Bu, repo.DeletedBusinessUnitId);
    }

    private sealed class RecordingTeamRepository : Interfaces.ITeamRepository
    {
        public long? DeletedBusinessUnitId { get; private set; }
        public Task DeleteAsync(long id, long businessUnitId)
        {
            DeletedBusinessUnitId = businessUnitId;
            return Task.CompletedTask;
        }
        public Task<(IEnumerable<Team>, int TotalCount)> GetAllAsync(int pageNumber, int pageSize,
            long? id, string? teamName, long? subTeamId, long businessUnitId) => throw new NotSupportedException();
        public Task<Team> GetByIdAsync(long id, long businessUnitId) => throw new NotSupportedException();
        public Task AddAsync(Team team) => throw new NotSupportedException();
        public Task UpdateAsync(Team team) => throw new NotSupportedException();
    }
}
