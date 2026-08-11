using System.Security.Claims;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.DTOs;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// <c>Setup_Master.RoleRank</c> — the column that replaced "privilege is whatever the role name
/// contains".
///
/// Three things are proven here, and only the third is about the new feature:
///  1. the one-time migration backfill really does reproduce the legacy name heuristic, by
///     executing THE MIGRATION'S OWN SQL against a relational database, so no live tenant silently
///     loses (or keeps) access at deploy time;
///  2. rank is compared, not matched — <c>CanManageRoleAsync</c> refuses a target that outranks
///     the caller;
///  3. rank cannot be used to climb: no caller may set a role to a rank at or above their own,
///     nor move the rank of a role that already outranks them.
///
/// The regression that motivated the whole change — "Supervisor Admin" is NOT a super
/// administrator — lives next to the gates it broke, in
/// <c>RbacAuthorizationTests.PrivilegedLookingName_WithMemberRank_GrantsNothing</c>.
/// </summary>
public sealed class RoleRankAuthorityTests
{
    private const long Bu = 42;

    // ---------------- 1. the one-time rank backfill ----------------

    /// <summary>
    /// Executes <see cref="LegacyRoleRankBackfill.Sql"/> — the exact statement the migration ran
    /// against production PostgreSQL, moved out of the migration when
    /// 20260811033109_SquashedSchemaBaseline retired it — over a seeded relational database, and
    /// asserts the rank each representative legacy name lands on.
    ///
    /// The names are the real ones: the two tiers the old rule recognised, the ordinary job titles
    /// it mis-classified as tenant owners, and the neutral roles it left alone. The backfill
    /// grandfathers ALL of them exactly as they behave today — including the dangerous ones —
    /// because a deploy that silently demoted a live customer's only administrator would be an
    /// outage, not a fix. What changes is that the privilege is now visible and editable.
    /// </summary>
    [Fact]
    public void Backfill_ReproducesLegacyNameHeuristic_Once()
    {
        using var db = new TestDb();
        using var ctx = db.ContextFor(null);
        Seed.EnsureBusinessUnit(ctx, Bu);

        // (code, value, expected rank)
        var cases = new (string Code, string Value, short Expected)[]
        {
            // Canonical owners under the old rule.
            ("SUPER_ADMIN",  "Super Admin",                    RoleRanks.Owner),
            ("SUPERADMIN",   "Super_Administrator",            RoleRanks.Owner),
            // The defect, grandfathered on purpose: these ARE super admins in production today.
            ("SUPERVISOR",   "Supervisor Admin",               RoleRanks.Owner),
            ("SUPERINTENDENT", "Superintendent Administrator", RoleRanks.Owner),
            ("SITE_SUP",     "Site Supervisor - Admin",        RoleRanks.Owner),
            // Manager tier: "admin" or "manager" but not both keywords.
            ("SALES_MGR",    "Sales Manager",                  RoleRanks.Manager),
            ("OFFICE_MGR",   "Office Manager",                 RoleRanks.Manager),
            ("CONTRACT_ADM", "Contract Administrator",         RoleRanks.Manager),
            ("ADMIN",        "Administrator",                  RoleRanks.Manager),
            // A "super" with no "admin" anywhere is only a Member — it never matched either half.
            ("SUPERVISOR2",  "Site Supervisor",                RoleRanks.Member),
            // Neutral roles.
            ("SALES_EXEC",   "Sales Executive",                RoleRanks.Member),
            ("BUYER",        "Procurement Buyer",              RoleRanks.Member),
            // The match is case-insensitive and reads EITHER column: the code alone carries it here.
            ("super-ADMIN",  "Regional Lead",                  RoleRanks.Owner),
            ("mgr-emea",     "manager, EMEA",                  RoleRanks.Manager),
        };

        long id = 1;
        foreach (var (code, value, _) in cases)
            ctx.SetupMasters.Add(NewRow(id++, "role", code, value));

        // Non-role rows must be untouched by the backfill even when their text matches.
        var quoteStatusId = id++;
        ctx.SetupMasters.Add(NewRow(quoteStatusId, "QuoteStatus", "SUPER_ADMIN", "Super Admin"));
        // ' Role ' with padding is a real production variant, and the backfill's
        // lower(replace(...)) predicate must still classify it as a role.
        var paddedRoleId = id++;
        ctx.SetupMasters.Add(NewRow(paddedRoleId, " Role ", "SUPER_ADMIN", "Super Admin"));

        ctx.SaveChanges();
        // Nothing is privileged before the backfill: the column defaults to Member.
        Assert.All(ctx.SetupMasters.AsNoTracking().ToList(), r => Assert.Equal(RoleRanks.Member, r.RoleRank));

        ctx.Database.ExecuteSqlRaw(LegacyRoleRankBackfill.Sql);

        var stored = ctx.SetupMasters.AsNoTracking().ToDictionary(r => r.SetupId, r => r.RoleRank);
        id = 1;
        foreach (var (code, value, expected) in cases)
        {
            var actual = stored[id++];
            Assert.True(expected == actual,
                $"'{code}' / '{value}' backfilled to {RoleRanks.Describe(actual)}, expected {RoleRanks.Describe(expected)}.");
        }

        Assert.Equal(RoleRanks.Member, stored[quoteStatusId]);
        Assert.Equal(RoleRanks.Owner, stored[paddedRoleId]);
    }

    /// <summary>The backfill is idempotent in the trivial sense that re-running it over already
    /// ranked rows produces the same result — it is a pure function of the name. That matters
    /// because it is the ONLY time the name will ever be consulted: a later administrator change
    /// is not re-derivable and must never be recomputed from the name.</summary>
    [Fact]
    public void Backfill_IsPureFunctionOfName_AndIsNeverReapplied()
    {
        using var db = new TestDb();
        using var ctx = db.ContextFor(null);
        Seed.EnsureBusinessUnit(ctx, Bu);
        ctx.SetupMasters.Add(NewRow(1, "role", "SUPERVISOR", "Supervisor Admin"));
        ctx.SaveChanges();

        ctx.Database.ExecuteSqlRaw(LegacyRoleRankBackfill.Sql);
        // Raw SQL bypasses the change tracker, which still holds the pre-backfill entity.
        ctx.ChangeTracker.Clear();
        Assert.Equal(RoleRanks.Owner, ctx.SetupMasters.AsNoTracking().Single(r => r.SetupId == 1).RoleRank);

        // An administrator lowers it — the whole point of making rank explicit.
        var row = ctx.SetupMasters.Single(r => r.SetupId == 1);
        row.RoleRank = RoleRanks.Member;
        ctx.SaveChanges();
        ctx.ChangeTracker.Clear();

        // The migration has already run and will not run again; the demotion stands. (Re-running
        // the statement here would resurrect Owner, which is exactly why it is a migration and not
        // a runtime rule.)
        Assert.Equal(RoleRanks.Member, ctx.SetupMasters.AsNoTracking().Single(r => r.SetupId == 1).RoleRank);
    }

    // ---------------- 2. rank comparison ----------------

    [Fact]
    public async Task CanManageRole_RefusesATargetThatOutranksTheCaller()
    {
        using var db = new TestDb();
        using var ctx = db.ContextFor(Bu);
        Seed.EnsureBusinessUnit(ctx, Bu);
        ctx.SetupMasters.AddRange(
            NewRole(1, "Member Role", RoleRanks.Member),
            NewRole(2, "Manager Role", RoleRanks.Manager),
            NewRole(3, "Admin Role", RoleRanks.Admin),
            NewRole(4, "Owner Role", RoleRanks.Owner));
        ctx.SaveChanges();

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var gate = new RoleGate(ctx, cache);

        // Upward: always refused.
        Assert.False(await gate.CanManageRoleAsync(1, 2, Bu));
        Assert.False(await gate.CanManageRoleAsync(2, 3, Bu));
        Assert.False(await gate.CanManageRoleAsync(3, 4, Bu));

        // Downward: permitted (no permission rows exist, so the superset check is vacuous).
        Assert.True(await gate.CanManageRoleAsync(2, 1, Bu));
        Assert.True(await gate.CanManageRoleAsync(3, 2, Bu));
        Assert.True(await gate.CanManageRoleAsync(4, 3, Bu));

        // Admin(20) is genuinely above Manager(10) now. Under the deleted name rule both were the
        // same "contains admin-or-manager" bucket and this refusal was not expressible.
        Assert.False(await gate.CanManageRoleAsync(2, 3, Bu));
    }

    [Fact]
    public async Task ForeignTenantRank_IsInvisible_ToCanManageRole()
    {
        using var db = new TestDb();
        using var ctx = db.ContextFor(Bu);
        Seed.EnsureBusinessUnit(ctx, Bu);
        Seed.EnsureBusinessUnit(ctx, 99);
        ctx.SetupMasters.AddRange(
            NewRole(1, "Manager Role", RoleRanks.Manager),
            NewRole(2, "Foreign Owner", RoleRanks.Owner, businessUnitId: 99));
        ctx.SaveChanges();

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var gate = new RoleGate(ctx, cache);

        // The foreign Owner row resolves to Member in THIS tenant, so it does not outrank the
        // caller — but it is still refused, because it is not a role of this tenant at all.
        Assert.Equal(RoleRanks.Member, await gate.GetRoleRankAsync(2, Bu));
        Assert.False(await gate.CanManageRoleAsync(1, 2, Bu));

        // And a foreign caller claiming that Owner role id gets nothing in this tenant.
        Assert.False(await gate.IsSuperAdminAsync(2, Bu));
        Assert.False(await gate.CanManageRoleAsync(2, 1, Bu));
    }

    // ---------------- 3. rank mutation guard ----------------

    public static TheoryData<short, short> EscalatingRanks => new()
    {
        // callerRank, requestedRank — every one is "at or above my own".
        { RoleRanks.Member,  RoleRanks.Member },
        { RoleRanks.Manager, RoleRanks.Manager },
        { RoleRanks.Manager, RoleRanks.Admin },
        { RoleRanks.Manager, RoleRanks.Owner },
        { RoleRanks.Admin,   RoleRanks.Admin },
        { RoleRanks.Admin,   RoleRanks.Owner },
        { RoleRanks.Owner,   RoleRanks.Owner },
    };

    [Theory]
    [MemberData(nameof(EscalatingRanks))]
    public async Task Create_RefusesARoleAtOrAboveTheCallersOwnRank(short callerRank, short requestedRank)
    {
        var (controller, repo, _) = await BuildControllerAsync(callerRank);

        var result = await controller.Create(new SetupMasterCreateRequestDTO
        {
            SetupType = "role",
            SetupCode = "NEW_ROLE",
            SetupName = "New Role",
            RoleRank = requestedRank
        });

        AssertForbidden(result.Result);
        Assert.Null(repo.Added);
    }

    [Fact]
    public async Task Create_AllowsARoleStrictlyBelowTheCallersOwnRank()
    {
        var (controller, repo, _) = await BuildControllerAsync(RoleRanks.Owner);

        var result = await controller.Create(new SetupMasterCreateRequestDTO
        {
            SetupType = "role",
            SetupCode = "NEW_ROLE",
            SetupName = "New Role",
            RoleRank = RoleRanks.Admin
        });

        Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.NotNull(repo.Added);
        Assert.Equal(RoleRanks.Admin, repo.Added!.RoleRank);
    }

    /// <summary>The Create set minus the pairs that would be a no-op against a Member target — an
    /// unchanged rank is not a rank mutation and deliberately raises no guard.</summary>
    public static TheoryData<short, short> EscalatingRankChanges => new()
    {
        { RoleRanks.Member,  RoleRanks.Manager },
        { RoleRanks.Manager, RoleRanks.Manager },
        { RoleRanks.Manager, RoleRanks.Admin },
        { RoleRanks.Manager, RoleRanks.Owner },
        { RoleRanks.Admin,   RoleRanks.Admin },
        { RoleRanks.Admin,   RoleRanks.Owner },
        { RoleRanks.Owner,   RoleRanks.Owner },
    };

    [Theory]
    [MemberData(nameof(EscalatingRankChanges))]
    public async Task Update_RefusesRaisingARoleToOrAboveTheCallersOwnRank(short callerRank, short requestedRank)
    {
        var (controller, repo, _) = await BuildControllerAsync(callerRank, targetRank: RoleRanks.Member);

        var result = await controller.Update(TargetRoleId, new SetupMasterUpdateRequestDTO
        {
            SetupType = "role",
            SetupCode = "TARGET",
            SetupName = "Target Role",
            RoleRank = requestedRank
        });

        AssertForbidden(result);
        Assert.Null(repo.Updated);
        Assert.Equal(RoleRanks.Member, repo.Stored.RoleRank);
    }

    /// <summary>
    /// The other half of the invariant. Without it a Manager takes the tenant by DEMOTING the
    /// owner role instead of promoting their own — removing everyone above them rather than
    /// climbing.
    /// </summary>
    [Fact]
    public async Task Update_RefusesLoweringTheRankOfARoleThatOutranksTheCaller()
    {
        var (controller, repo, _) = await BuildControllerAsync(RoleRanks.Manager, targetRank: RoleRanks.Owner);

        var result = await controller.Update(TargetRoleId, new SetupMasterUpdateRequestDTO
        {
            SetupType = "role",
            SetupCode = "TARGET",
            SetupName = "Target Role",
            RoleRank = RoleRanks.Member
        });

        AssertForbidden(result);
        Assert.Equal(RoleRanks.Owner, repo.Stored.RoleRank);
    }

    [Fact]
    public async Task Update_AllowsLoweringARoleBelowTheCaller_AndAuditsTheRankChange()
    {
        var (controller, repo, audit) = await BuildControllerAsync(RoleRanks.Owner, targetRank: RoleRanks.Admin);

        var result = await controller.Update(TargetRoleId, new SetupMasterUpdateRequestDTO
        {
            SetupType = "role",
            SetupCode = "TARGET",
            SetupName = "Target Role",
            RoleRank = RoleRanks.Manager
        });

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(RoleRanks.Manager, repo.Stored.RoleRank);
        Assert.Contains(audit.Entries, e => e.Action == IamAuditActions.RoleRankChanged
                                            && e.TargetId == TargetRoleId);
    }

    /// <summary>A rename with no rank in the payload keeps the stored rank and raises no rank
    /// guard: a client that predates the column cannot demote a role by omission, and cosmetic
    /// edits stay available to whoever may administer roles.</summary>
    [Fact]
    public async Task Update_WithoutRoleRank_KeepsStoredRank_AndDoesNotAuditARankChange()
    {
        var (controller, repo, audit) = await BuildControllerAsync(RoleRanks.Owner, targetRank: RoleRanks.Manager);

        var result = await controller.Update(TargetRoleId, new SetupMasterUpdateRequestDTO
        {
            SetupType = "role",
            SetupCode = "TARGET",
            SetupName = "Renamed Target"
        });

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(RoleRanks.Manager, repo.Stored.RoleRank);
        Assert.DoesNotContain(audit.Entries, e => e.Action == IamAuditActions.RoleRankChanged);
    }

    [Fact]
    public async Task Create_RejectsARankThatIsNotADefinedTier()
    {
        var (controller, repo, _) = await BuildControllerAsync(RoleRanks.Owner);

        var result = await controller.Create(new SetupMasterCreateRequestDTO
        {
            SetupType = "role",
            SetupCode = "NEW_ROLE",
            SetupName = "New Role",
            RoleRank = 29      // just under Owner: the obvious way to sneak past a ">= tier" check
        });

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Null(repo.Added);
    }

    [Fact]
    public async Task Create_RejectsARankOnANonRoleRow()
    {
        var (controller, repo, _) = await BuildControllerAsync(RoleRanks.Owner);

        var result = await controller.Create(new SetupMasterCreateRequestDTO
        {
            SetupType = "QuoteStatus",
            SetupCode = "DRAFT",
            SetupName = "Draft",
            RoleRank = RoleRanks.Admin
        });

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Null(repo.Added);
    }

    /// <summary>Fail-closed: a controller with no role gate wired reports the caller as Member, so
    /// every rank above Member is refused rather than silently permitted.</summary>
    [Fact]
    public async Task Create_WithNoRoleGate_RefusesAnyRank()
    {
        var repo = new RecordingSetupRepository(NewRole(TargetRoleId, "Target Role", RoleRanks.Member));
        var controller = new SetupMasterController(repo)
        {
            ControllerContext = ContextFor(CallerRoleId)
        };

        var result = await controller.Create(new SetupMasterCreateRequestDTO
        {
            SetupType = "role", SetupCode = "X", SetupName = "X",
            RoleRank = RoleRanks.Manager
        });

        AssertForbidden(result.Result);
        Assert.Null(repo.Added);
    }

    // ---------------- harness ----------------

    private const long CallerRoleId = 700;
    private const long TargetRoleId = 800;

    private static void AssertForbidden(ActionResult? result)
    {
        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, status.StatusCode);
    }

    /// <summary>
    /// Real controller, real <see cref="RoleGate"/>, real SQLite database — only the repository and
    /// the audit writer are doubles, so the refusals below come from the guard and not from a stub.
    /// The caller is given a permissive "Roles &amp; Permissions" grant so the earlier
    /// <c>RoleAdministrationDenialAsync</c> gate cannot be what produces the 403.
    /// </summary>
    private static async Task<(SetupMasterController Controller, RecordingSetupRepository Repo, RecordingAuditWriter Audit)>
        BuildControllerAsync(short callerRank, short targetRank = RoleRanks.Member)
    {
        var db = new TestDb();
        var ctx = db.ContextFor(Bu);
        Seed.EnsureBusinessUnit(ctx, Bu);
        ctx.SetupMasters.AddRange(
            NewRole(CallerRoleId, "Caller Role", callerRank),
            NewRole(TargetRoleId, "Target Role", targetRank));
        await ctx.SaveChangesAsync();

        var cache = new MemoryCache(new MemoryCacheOptions());
        var repo = new RecordingSetupRepository(NewRole(TargetRoleId, "Target Role", targetRank));
        var audit = new RecordingAuditWriter();
        var controller = new SetupMasterController(
            repo, new RoleGate(ctx, cache), new PermissiveRolePermissions(), audit, cache)
        {
            ControllerContext = ContextFor(CallerRoleId)
        };
        return (controller, repo, audit);
    }

    private static ControllerContext ContextFor(long roleId)
    {
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim("roleId", roleId.ToString()),
                new Claim("businessUnitId", Bu.ToString())
            }, "TestAuth"))
        };
        return new ControllerContext { HttpContext = http };
    }

    private static SetupMaster NewRole(long id, string name, short rank, long businessUnitId = Bu)
        => NewRow(id, "role", name, name, businessUnitId, rank);

    private static SetupMaster NewRow(
        long id, string setupType, string code, string value, long businessUnitId = Bu,
        short rank = RoleRanks.Member) => new()
        {
            SetupId = id,
            SetupType = setupType,
            SetupCode = code,
            SetupValue = value,
            RoleRank = rank,
            BusinessUnitId = businessUnitId,
            IsActive = true,
            CreatedBy = "seed",
            CreatedOn = DateTime.UtcNow
        };

    private sealed class RecordingSetupRepository : ISetupMasterRepository
    {
        public RecordingSetupRepository(SetupMaster stored) => Stored = stored;

        public SetupMaster Stored { get; }
        public SetupMaster? Added { get; private set; }
        public SetupMaster? Updated { get; private set; }

        public Task<IEnumerable<SetupMaster>> GetAllAsync()
            => Task.FromResult<IEnumerable<SetupMaster>>(new[] { Stored });

        public Task<SetupMaster> GetByIdAsync(long id)
            => id == Stored.SetupId ? Task.FromResult(Stored) : throw new KeyNotFoundException();

        public Task AddAsync(SetupMaster setupMaster) { Added = setupMaster; return Task.CompletedTask; }
        public Task UpdateAsync(SetupMaster setupMaster) { Updated = setupMaster; return Task.CompletedTask; }
        public Task DeleteAsync(long id) => Task.CompletedTask;
    }

    private sealed class RecordingAuditWriter : IIamAuditWriter
    {
        public List<IamAuditEntry> Entries { get; } = new();

        public IamAuditEvent Enlist(ClaimsPrincipal? principal, IamAuditEntry entry)
        {
            Entries.Add(entry);
            return new IamAuditEvent { Action = entry.Action, TargetType = entry.TargetType };
        }

        public Task<IamAuditEvent> WriteAsync(
            ClaimsPrincipal? principal, IamAuditEntry entry, CancellationToken cancellationToken = default)
            => Task.FromResult(Enlist(principal, entry));

        public Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?> BeginAtomicAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?>(null);

        public Task ExecuteAtomicAsync(Func<Task> work, CancellationToken cancellationToken = default)
            => work();
    }

    /// <summary>Grants every module action, so no test below can pass or fail because of the
    /// module-permission gate that runs before the rank guard.</summary>
    private sealed class PermissiveRolePermissions : IRolePermissionRepository
    {
        public Task<(IEnumerable<RolePermission>, int TotalCount)> GetAllAsync(
            int pageNumber, int pageSize, long? id, long? roleId, long? moduleId, long businessUnitId)
            => Task.FromResult<(IEnumerable<RolePermission>, int)>((Array.Empty<RolePermission>(), 0));

        public Task<RolePermission> GetByIdAsync(long id, long businessUnitId) => throw new KeyNotFoundException();
        public Task AddAsync(RolePermission rolePermission) => Task.CompletedTask;
        public Task UpdateAsync(RolePermission rolePermission) => Task.CompletedTask;
        public Task DeleteAsync(long id, long businessUnitId) => Task.CompletedTask;

        public Task<bool> CheckPermissionAsync(long roleId, string moduleName, string action, long businessUnitId)
            => Task.FromResult(true);
    }
}
