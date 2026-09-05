using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Security;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The stamp rotates on every write that changes an account's AUTHORITY and on nothing else
/// (docs/design/token-revocation.md). The "nothing else" half is the control: a rotation on
/// every profile edit would log people out for changing their timezone, and a test suite that
/// only asserted "it changed" could not tell the two apart.
/// </summary>
public sealed class SecurityStampRotationTests
{
    private const long Bu = 7_401;
    private const long RoleA = 7_411;
    private const long RoleB = 7_412;

    private sealed class RecordingSessionCache : ITenantSessionCache
    {
        public List<long> Evicted { get; } = [];
        public void Evict(long userId) => Evicted.Add(userId);
    }

    [Fact]
    public async Task Deactivating_through_the_repository_rotates_the_stamp_and_evicts()
    {
        using var db = new TestDb();
        var (userId, before) = await SeedAsync(db);
        var sessions = new RecordingSessionCache();

        await using (var ctx = db.ContextFor(null))
        {
            var repo = new UserRepository(ctx, sessions);
            var user = await repo.GetByIdAsync(userId, Bu);
            user.IsActive = false;
            Detach(user);
            await repo.UpdateAsync(user);
        }

        Assert.NotEqual(before, await StampAsync(db, userId));
        Assert.Equal([userId], sessions.Evicted);
    }

    [Fact]
    public async Task Changing_the_role_through_the_repository_rotates_the_stamp()
    {
        using var db = new TestDb();
        var (userId, before) = await SeedAsync(db);

        await using (var ctx = db.ContextFor(null))
        {
            var repo = new UserRepository(ctx);
            var user = await repo.GetByIdAsync(userId, Bu);
            user.RoleId = RoleB;
            Detach(user);
            await repo.UpdateAsync(user);
        }

        Assert.NotEqual(before, await StampAsync(db, userId));
    }

    [Fact]
    public async Task A_profile_edit_does_not_rotate_the_stamp()
    {
        using var db = new TestDb();
        var (userId, before) = await SeedAsync(db);
        var sessions = new RecordingSessionCache();

        await using (var ctx = db.ContextFor(null))
        {
            var repo = new UserRepository(ctx, sessions);
            var user = await repo.GetByIdAsync(userId, Bu);
            user.FirstName = "Renamed";
            user.Timezone = "Asia/Riyadh";
            // A stale copy of the stamp on the incoming entity must not be able to overwrite it.
            user.SecurityStamp = "stale-client-copy";
            Detach(user);
            await repo.UpdateAsync(user);
        }

        Assert.Equal(before, await StampAsync(db, userId));
        Assert.Empty(sessions.Evicted);
    }

    [Fact]
    public async Task Changing_the_password_rotates_the_stamp()
    {
        using var db = new TestDb();
        var (userId, before) = await SeedAsync(db);
        var sessions = new RecordingSessionCache();

        await using (var ctx = db.ContextFor(null))
            await new UserRepository(ctx, sessions).ChangePasswordAsync(userId, "New-Password-9#x");

        Assert.NotEqual(before, await StampAsync(db, userId));
        Assert.Equal([userId], sessions.Evicted);
    }

    [Fact]
    public async Task Deleting_through_the_repository_evicts_the_cached_session()
    {
        // Audit 2026-09-04: the row is gone, but the validator's cached verdict from before the
        // delete would honour the account's tokens for the cache TTL unless it is evicted here.
        using var db = new TestDb();
        var (userId, _) = await SeedAsync(db);
        var sessions = new RecordingSessionCache();

        await using (var ctx = db.ContextFor(null))
        {
            var repo = new UserRepository(ctx, sessions);
            await repo.DeleteAsync(userId, Bu);
        }

        Assert.Equal([userId], sessions.Evicted);
    }

    [Fact]
    public void Every_new_user_starts_with_a_distinct_stamp()
    {
        var a = new User();
        var b = new User();
        Assert.Equal(32, a.SecurityStamp.Length);
        Assert.NotEqual(a.SecurityStamp, b.SecurityStamp);
    }

    private static async Task<(long Id, string Stamp)> SeedAsync(TestDb db)
    {
        await using var ctx = db.ContextFor(null);
        ctx.BusinessUnits.Add(new BusinessUnit
        {
            Id = Bu, BusinessUnitCode = "STAMP", BusinessUnitName = "Stamp BU", IsActive = true,
            CreatedBy = "tests", CreatedOn = DateTime.UtcNow
        });
        ctx.SetupMasters.AddRange(Role(RoleA, "ROLE_A"), Role(RoleB, "ROLE_B"));
        var user = new User
        {
            Buid = Bu, RoleId = RoleA, FirstName = "Stamp", LastName = "User",
            Email = $"stamp-{Guid.NewGuid():N}@nexora.invalid", PasswordHash = BCrypt.Net.BCrypt.HashPassword("Old-Password-9#x"),
            ImageUrl = "n/a", IsActive = true, CreatedBy = "tests", CreatedOn = DateTime.UtcNow
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();
        return (user.Id, user.SecurityStamp);
    }

    private static SetupMaster Role(long id, string code) => new()
    {
        SetupId = id, BusinessUnitId = Bu, SetupType = SetupTypes.Role, SetupCode = code, SetupValue = code,
        IsActive = true, CreatedBy = "tests", CreatedOn = DateTime.UtcNow
    };

    private static async Task<string> StampAsync(TestDb db, long userId)
    {
        await using var ctx = db.ContextFor(null);
        return await ctx.Users.IgnoreQueryFilters().Where(u => u.Id == userId).Select(u => u.SecurityStamp).SingleAsync();
    }

    private static void Detach(User user)
    {
        user.Role = null; user.Team = null; user.UserGroup = null; user.Manager = null; user.Bu = null;
    }
}
