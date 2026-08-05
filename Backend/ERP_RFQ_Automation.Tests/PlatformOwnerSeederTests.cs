using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Bootstrap-owner seeder posture: creates exactly one Owner on a completely empty
/// platform-user table when (and only when) both config values are present; never
/// overwrites; without configuration it fails closed — including in Production,
/// where no default credential may ever appear.
/// </summary>
public sealed class PlatformOwnerSeederTests
{
    [Fact]
    public async Task Seeds_a_single_active_owner_when_the_table_is_empty_and_config_is_present()
    {
        using var db = new TestDb();
        await PlatformOwnerSeeder.EnsureAsync(Services(db), Configuration(
            email: "boot@example.test", password: "bootstrap-secret-123"));

        await using var verification = db.ContextFor(null);
        var owner = await verification.Set<PlatformUser>().SingleAsync();
        Assert.Equal("boot@example.test", owner.Email);
        Assert.Equal(PlatformRole.Owner, owner.PlatformRole);
        Assert.True(owner.IsActive);
        Assert.True(BCrypt.Net.BCrypt.Verify("bootstrap-secret-123", owner.PasswordHash));
        Assert.Equal("system:platform-bootstrap", owner.CreatedBy);

        // Sec7: the bootstrap itself is audited, attributed to the created owner.
        var audit = await verification.Set<PlatformAuditLog>()
            .SingleAsync(a => a.Action == "platform.owner.bootstrap");
        Assert.Equal(owner.Id, audit.ActorPlatformUserId);
        Assert.Equal(owner.Id.ToString(), audit.TargetId);
        Assert.Equal(PlatformAuditResults.Success, audit.Result);
        Assert.Contains(owner.Email, audit.Metadata);
    }

    [Theory]
    [InlineData("short")]        // far below the floor
    [InlineData("elevenchars")]  // 11 — one below the floor
    public async Task Rejects_a_bootstrap_password_shorter_than_twelve_characters(string weakPassword)
    {
        // Sec7: a weak configured secret is refused (logged + skipped) — the seeder
        // never creates an Owner with a password below the platform-user floor.
        using var db = new TestDb();
        await PlatformOwnerSeeder.EnsureAsync(Services(db), Configuration(
            email: "boot@example.test", password: weakPassword));

        await using var verification = db.ContextFor(null);
        Assert.Empty(await verification.Set<PlatformUser>().ToListAsync());
        Assert.Empty(await verification.Set<PlatformAuditLog>().ToListAsync());
    }

    [Fact]
    public async Task Twelve_character_password_is_accepted_at_the_boundary()
    {
        using var db = new TestDb();
        await PlatformOwnerSeeder.EnsureAsync(Services(db), Configuration(
            email: "boot@example.test", password: "exactly12chr")); // length 12

        await using var verification = db.ContextFor(null);
        Assert.Single(await verification.Set<PlatformUser>().ToListAsync());
    }

    [Fact]
    public async Task Never_runs_when_any_platform_user_exists_even_a_deactivated_one()
    {
        using var db = new TestDb();
        string originalHash;
        await using (var seed = db.ContextFor(null))
        {
            var existing = new PlatformUser
            {
                Email = "boot@example.test",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("operator-rotated-secret"),
                PlatformRole = PlatformRole.SupportAdmin,
                IsActive = false,
                CreatedBy = "test",
                CreatedOn = DateTime.UtcNow
            };
            seed.Set<PlatformUser>().Add(existing);
            await seed.SaveChangesAsync();
            originalHash = existing.PasswordHash;
        }

        await PlatformOwnerSeeder.EnsureAsync(Services(db), Configuration(
            email: "boot@example.test", password: "bootstrap-secret-123"));

        await using var verification = db.ContextFor(null);
        var user = await verification.Set<PlatformUser>().SingleAsync();
        // Nothing was created, promoted, reactivated or re-hashed.
        Assert.Equal(originalHash, user.PasswordHash);
        Assert.Equal(PlatformRole.SupportAdmin, user.PlatformRole);
        Assert.False(user.IsActive);
    }

    [Fact]
    public async Task Production_without_bootstrap_config_fails_closed_and_creates_nothing()
    {
        using var db = new TestDb();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ASPNETCORE_ENVIRONMENT"] = "Production" }).Build();

        await PlatformOwnerSeeder.EnsureAsync(Services(db), configuration);

        await using var verification = db.ContextFor(null);
        Assert.Empty(await verification.Set<PlatformUser>().ToListAsync());
    }

    [Theory]
    [InlineData("boot@example.test", null)]
    [InlineData(null, "bootstrap-secret-123")]
    [InlineData("boot@example.test", "  ")]
    public async Task Partial_configuration_creates_no_credential(string? email, string? password)
    {
        using var db = new TestDb();
        await PlatformOwnerSeeder.EnsureAsync(Services(db), Configuration(email, password));

        await using var verification = db.ContextFor(null);
        Assert.Empty(await verification.Set<PlatformUser>().ToListAsync());
    }

    private static IServiceProvider Services(TestDb db) => new ServiceCollection()
        .AddLogging()
        .AddScoped(_ => db.ContextFor(null))
        .BuildServiceProvider();

    private static IConfiguration Configuration(string? email, string? password) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [PlatformOwnerSeeder.EmailConfigKey] = email,
            [PlatformOwnerSeeder.PasswordConfigKey] = password
        }).Build();
}
