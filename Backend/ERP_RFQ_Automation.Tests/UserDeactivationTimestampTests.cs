using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Seats-reproducibility contract (FIX CONTRACTS / P0-B2): the public Users entity
/// carries a nullable DeactivatedAtUtc maintained wherever IsActive flips —
/// true→false stamps UtcNow (only when previously active), reactivation clears it,
/// and an already-inactive user keeps the original deactivation instant. FIX-1's
/// billing seats meter consumes this column.
/// </summary>
public sealed class UserDeactivationTimestampTests
{
    private const long Bu = 81;

    [Fact]
    public async Task Deactivating_an_active_user_stamps_DeactivatedAtUtc()
    {
        using var db = new TestDb();
        var userId = await SeedUserAsync(db, isActive: true);
        var before = DateTime.UtcNow;

        await using (var ctx = db.ContextFor(null))
        {
            var repo = new UserRepository(ctx);
            var user = await repo.GetByIdAsync(userId, Bu);
            user.IsActive = false;
            DetachNavigations(user);
            await repo.UpdateAsync(user);
        }

        await using var verify = db.ContextFor(null);
        var row = await verify.Users.SingleAsync(u => u.Id == userId);
        Assert.False(row.IsActive);
        Assert.NotNull(row.DeactivatedAtUtc);
        Assert.InRange(row.DeactivatedAtUtc!.Value, before.AddSeconds(-1), DateTime.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task Reactivating_clears_DeactivatedAtUtc()
    {
        using var db = new TestDb();
        var userId = await SeedUserAsync(db, isActive: false,
            deactivatedAtUtc: DateTime.UtcNow.AddDays(-3));

        await using (var ctx = db.ContextFor(null))
        {
            var repo = new UserRepository(ctx);
            var user = await repo.GetByIdAsync(userId, Bu);
            user.IsActive = true;
            DetachNavigations(user);
            await repo.UpdateAsync(user);
        }

        await using var verify = db.ContextFor(null);
        var row = await verify.Users.SingleAsync(u => u.Id == userId);
        Assert.True(row.IsActive);
        Assert.Null(row.DeactivatedAtUtc);
    }

    [Fact]
    public async Task Updating_an_already_inactive_user_preserves_the_original_deactivation_instant()
    {
        using var db = new TestDb();
        var original = DateTime.UtcNow.AddDays(-7);
        // Truncate to whole seconds so the round-trip comparison is provider-stable.
        original = new DateTime(original.Year, original.Month, original.Day,
            original.Hour, original.Minute, original.Second, DateTimeKind.Utc);
        var userId = await SeedUserAsync(db, isActive: false, deactivatedAtUtc: original);

        await using (var ctx = db.ContextFor(null))
        {
            var repo = new UserRepository(ctx);
            var user = await repo.GetByIdAsync(userId, Bu);
            user.IsActive = false;              // still inactive
            user.Region = "Updated";            // unrelated edit
            DetachNavigations(user);
            await repo.UpdateAsync(user);
        }

        await using var verify = db.ContextFor(null);
        var row = await verify.Users.SingleAsync(u => u.Id == userId);
        Assert.Equal(original, row.DeactivatedAtUtc);
    }

    [Fact]
    public async Task Updating_an_active_user_without_flipping_keeps_the_stamp_null()
    {
        using var db = new TestDb();
        var userId = await SeedUserAsync(db, isActive: true);

        await using (var ctx = db.ContextFor(null))
        {
            var repo = new UserRepository(ctx);
            var user = await repo.GetByIdAsync(userId, Bu);
            user.Region = "Updated";
            DetachNavigations(user);
            await repo.UpdateAsync(user);
        }

        await using var verify = db.ContextFor(null);
        Assert.Null((await verify.Users.SingleAsync(u => u.Id == userId)).DeactivatedAtUtc);
    }

    [Fact]
    public async Task Creating_a_user_already_inactive_stamps_deactivation_at_creation()
    {
        using var db = new TestDb();
        await EnsureBusinessUnitAsync(db);

        long userId;
        await using (var ctx = db.ContextFor(null))
        {
            var repo = new UserRepository(ctx);
            var user = NewUser("created-inactive@example.test", isActive: false);
            await repo.AddAsync(user);
            userId = user.Id;
        }

        await using var verify = db.ContextFor(null);
        var row = await verify.Users.SingleAsync(u => u.Id == userId);
        Assert.False(row.IsActive);
        Assert.NotNull(row.DeactivatedAtUtc);
    }

    [Fact]
    public async Task Creating_an_active_user_leaves_the_stamp_null()
    {
        using var db = new TestDb();
        await EnsureBusinessUnitAsync(db);

        long userId;
        await using (var ctx = db.ContextFor(null))
        {
            var repo = new UserRepository(ctx);
            var user = NewUser("created-active@example.test", isActive: true);
            await repo.AddAsync(user);
            userId = user.Id;
        }

        await using var verify = db.ContextFor(null);
        Assert.Null((await verify.Users.SingleAsync(u => u.Id == userId)).DeactivatedAtUtc);
    }

    // ---- helpers ----------------------------------------------------------

    private static User NewUser(string email, bool isActive) => new()
    {
        FirstName = "Seat",
        LastName = "Holder",
        Email = email,
        PasswordHash = "x",
        ImageUrl = string.Empty,
        Buid = Bu,
        IsActive = isActive,
        CreatedBy = "test",
        CreatedOn = DateTime.UtcNow
    };

    private static void DetachNavigations(User user)
    {
        user.Role = null;
        user.Team = null;
        user.UserGroup = null;
        user.Manager = null;
        user.Bu = null;
    }

    private static async Task EnsureBusinessUnitAsync(TestDb db)
    {
        await using var seed = db.ContextFor(null);
        if (!await seed.BusinessUnits.AnyAsync(b => b.Id == Bu))
        {
            seed.BusinessUnits.Add(new BusinessUnit
            {
                Id = Bu,
                BusinessUnitCode = "SEAT",
                BusinessUnitName = "Seat BU",
                IsActive = true,
                CreatedBy = "test",
                CreatedOn = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }
    }

    private static async Task<long> SeedUserAsync(
        TestDb db, bool isActive, DateTime? deactivatedAtUtc = null)
    {
        await EnsureBusinessUnitAsync(db);
        await using var seed = db.ContextFor(null);
        var user = NewUser($"seed-{Guid.NewGuid():N}@example.test", isActive);
        user.DeactivatedAtUtc = deactivatedAtUtc;
        seed.Users.Add(user);
        await seed.SaveChangesAsync();
        return user.Id;
    }
}
