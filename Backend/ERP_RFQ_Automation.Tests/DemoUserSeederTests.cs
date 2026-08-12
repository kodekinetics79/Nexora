using ERP_RFQ_Automation.Infrastructure;
using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Locks in the fail-closed hardening of <see cref="DemoUserSeeder"/> (SEC finding: the seeder
/// previously ran by default in Production and reset the Super Admin / Platform Owner password
/// hash to a repo-published value on every restart).
/// </summary>
public sealed class DemoUserSeederTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ErpRfqAutomationContext> _options;

    public DemoUserSeederTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<ErpRfqAutomationContext>()
            .UseSqlite(_connection)
            .Options;
        using var ctx = NewContext();
        ctx.Database.EnsureCreated();
    }

    private ErpRfqAutomationContext NewContext() => new(_options, new StubTenant(null));

    private IServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // Register the same in-memory database the test asserts against.
        services.AddScoped(_ => new ErpRfqAutomationContext(_options, new StubTenant(null)));
        return services.BuildServiceProvider();
    }

    private static IConfiguration Config(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private sealed class FakeEnv : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    [Fact]
    public async Task Seeder_disabled_by_default_creates_no_users()
    {
        var provider = BuildProvider();

        // No DemoUser:Enabled key at all — must fail closed.
        await DemoUserSeeder.EnsureAsync(provider, Config(new()), new FakeEnv { EnvironmentName = "Development" });

        await using var db = NewContext();
        Assert.Equal(0, await db.Users.CountAsync());
        Assert.Equal(0, await db.Set<PlatformUser>().CountAsync());
    }

    [Fact]
    public async Task Seeder_enabled_without_passwords_seeds_nothing()
    {
        var provider = BuildProvider();
        var config = Config(new() { ["DemoUser:Enabled"] = "true" }); // enabled, but no passwords supplied

        await DemoUserSeeder.EnsureAsync(provider, config, new FakeEnv { EnvironmentName = "Development" });

        await using var db = NewContext();
        Assert.Equal(0, await db.Users.CountAsync());
        Assert.Equal(0, await db.Set<PlatformUser>().CountAsync());
    }

    [Fact]
    public async Task Seeder_never_overwrites_an_existing_users_password()
    {
        var config = Config(new()
        {
            ["DemoUser:Enabled"] = "true",
            ["DemoUser:Password"] = "FirstRunSecret!1",
            ["PlatformOwner:Password"] = "FirstRunSecret!1",
        });

        // First run creates the users.
        await DemoUserSeeder.EnsureAsync(BuildProvider(), config, new FakeEnv { EnvironmentName = "Development" });

        string userHashAfterFirstRun;
        string ownerHashAfterFirstRun;
        await using (var db = NewContext())
        {
            userHashAfterFirstRun = (await db.Users.SingleAsync(u => u.Email == "robert@example.com")).PasswordHash;
            ownerHashAfterFirstRun = (await db.Set<PlatformUser>().SingleAsync(u => u.Email == "owner@nexora.app")).PasswordHash;
        }

        // Second run with a DIFFERENT password (simulating a restart after an operator rotated creds).
        var rotatedConfig = Config(new()
        {
            ["DemoUser:Enabled"] = "true",
            ["DemoUser:Password"] = "AttackerControlled!2",
            ["PlatformOwner:Password"] = "AttackerControlled!2",
        });
        await DemoUserSeeder.EnsureAsync(BuildProvider(), rotatedConfig, new FakeEnv { EnvironmentName = "Development" });

        await using (var db = NewContext())
        {
            var user = await db.Users.SingleAsync(u => u.Email == "robert@example.com");
            var owner = await db.Set<PlatformUser>().SingleAsync(u => u.Email == "owner@nexora.app");
            // The password hash must be unchanged from the first run — the restart must not reset it.
            Assert.Equal(userHashAfterFirstRun, user.PasswordHash);
            Assert.Equal(ownerHashAfterFirstRun, owner.PasswordHash);
        }
    }

    [Fact]
    public async Task Seeder_provisions_one_fail_closed_ai_policy()
    {
        var config = Config(new()
        {
            ["DemoUser:Enabled"] = "true",
            ["DemoUser:Password"] = "FirstRunSecret!1",
            ["PlatformOwner:Password"] = "FirstRunSecret!1",
        });

        await DemoUserSeeder.EnsureAsync(BuildProvider(), config, new FakeEnv { EnvironmentName = "Development" });
        await DemoUserSeeder.EnsureAsync(BuildProvider(), config, new FakeEnv { EnvironmentName = "Development" });

        await using var db = NewContext();
        var policy = await db.Set<AiProcessingPolicy>().SingleAsync();
        Assert.False(policy.ExternalProcessingAllowed);
        Assert.True(policy.RedactionRequired);
        Assert.True(policy.PrivacyReviewRequired);
        Assert.Equal(10m, policy.ExternalDependencyCeilingPercent);
    }

    /// <summary>
    /// The demo tenant can raise a quote.
    ///
    /// <para>This seeder created a business unit, an AI policy, a role and a user, and no
    /// <c>Setup_Master</c> lifecycle rows at all — so the pilot business unit somebody actually
    /// walks the journey on had zero <c>QuoteStatus</c> rows. <c>QuoteService</c> then falls back
    /// to the documented legacy id 42, and <c>Quotes."StatusID"</c> is a foreign key to
    /// <c>Setup_Master."SetupID"</c>: no row with that id means the first quote fails on the
    /// foreign key, and another tenant's row holding it means the quote is stamped with a status
    /// from someone else's workspace. The assertion is the resolution the application performs,
    /// not a row count.</para>
    /// </summary>
    [Fact]
    public async Task Seeder_provisions_the_lifecycle_states_the_quote_and_order_paths_resolve()
    {
        var config = Config(new()
        {
            ["DemoUser:Enabled"] = "true",
            ["DemoUser:Password"] = "FirstRunSecret!1",
            ["PlatformOwner:Password"] = "FirstRunSecret!1",
        });

        // Twice: a restart must not give every picker a duplicate of every state.
        await DemoUserSeeder.EnsureAsync(BuildProvider(), config, new FakeEnv { EnvironmentName = "Development" });
        await DemoUserSeeder.EnsureAsync(BuildProvider(), config, new FakeEnv { EnvironmentName = "Development" });

        await using var db = NewContext();
        var businessUnit = await db.BusinessUnits.SingleAsync();

        var draftQuoteStatusId = await ERP_RFQ_Automation.CommercialCases.Lifecycle.LifecycleStatusCatalog
            .ResolveIdAsync(db, businessUnit.Id, "Quote", "DRAFT");
        var draft = await db.SetupMasters.SingleAsync(row => row.SetupId == draftQuoteStatusId);
        Assert.Equal("QuoteStatus", draft.SetupType);
        Assert.Equal(businessUnit.Id, draft.BusinessUnitId);

        Assert.Equal(6, await db.SetupMasters.CountAsync(row => row.SetupType == "QuoteStatus"));

        // OrderService resolves these two by name on the conversion a won quote becomes, and
        // throws "No OrderStatus setup found in the system" when either is absent.
        Assert.Equal(1, await db.SetupMasters.CountAsync(
            row => row.SetupType == "OrderStatus" && row.SetupCode == "DRAFT"));
        Assert.Equal(1, await db.SetupMasters.CountAsync(
            row => row.SetupType == "PaymentStatus" && row.SetupCode == "UNPAID"));
    }

    [Fact]
    public async Task Seeder_refuses_to_run_in_production()
    {
        var config = Config(new()
        {
            ["DemoUser:Enabled"] = "true",
            ["DemoUser:Password"] = "FirstRunSecret!1",
            ["PlatformOwner:Password"] = "FirstRunSecret!1",
        });

        // Fully configured and explicitly enabled — Production must still refuse. The seeder runs
        // outside any HttpContext, so it executes with a null tenant (EF filters off) under the
        // BYPASSRLS pipeline role, and it grants Super Admin. That combination has no place in
        // Production even when an operator switched it on.
        await DemoUserSeeder.EnsureAsync(BuildProvider(), config, new FakeEnv { EnvironmentName = "Production" });

        await using var db = NewContext();
        Assert.Equal(0, await db.Users.CountAsync());
        Assert.Equal(0, await db.Set<PlatformUser>().CountAsync());
        Assert.Equal(0, await db.BusinessUnits.CountAsync());

        // And nothing in the CONTROL PLANE either. This seeder now writes a platform.Plans row that
        // enables every runtime-available entitlement and an Active platform.Tenants row against it,
        // so "no logins were created" is no longer the whole of what the refusal has to guarantee.
        Assert.Equal(0, await db.Set<Plan>().CountAsync());
        Assert.Equal(0, await db.Set<Tenant>().IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task Seeder_never_adopts_an_existing_account_from_another_business_unit()
    {
        const string sharedEmail = "robert@example.com";

        // A REAL tenant, with a real user whose address happens to match DemoUser:Email.
        // Users.Email is globally unique, so an email-only lookup finds this row.
        await using (var db = NewContext())
        {
            db.BusinessUnits.Add(new BusinessUnit
            {
                Id = 77,
                BusinessUnitCode = "REAL-TENANT",
                BusinessUnitName = "Real Tenant",
                IsActive = true,
                CreatedBy = "test",
                CreatedOn = DateTime.UtcNow
            });
            db.Users.Add(new User
            {
                Id = 501,
                FirstName = "Real",
                LastName = "Customer",
                Email = sharedEmail,
                PasswordHash = "$2a$11$realcustomerhash",
                ImageUrl = string.Empty,
                RoleId = null,
                Buid = 77,
                IsActive = true,
                CreatedBy = "test",
                CreatedOn = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var config = Config(new()
        {
            ["DemoUser:Enabled"] = "true",
            ["DemoUser:Email"] = sharedEmail,
            ["DemoUser:Password"] = "FirstRunSecret!1",
            ["PlatformOwner:Password"] = "FirstRunSecret!1",
        });

        await DemoUserSeeder.EnsureAsync(
            BuildProvider(), config, new FakeEnv { EnvironmentName = "Development" });

        await using (var db = NewContext())
        {
            var demoBusinessUnit = await db.BusinessUnits.SingleAsync(b => b.BusinessUnitCode == "CUSTOMER-POC");
            var superAdmin = await db.SetupMasters.SingleAsync(s => s.SetupCode == "SUPER_ADMIN");
            var untouched = await db.Users.SingleAsync(u => u.Id == 501);

            // The real account must still belong to its own tenant, keep its own password, and
            // must NOT have been handed the demo tenant's Super Admin role.
            Assert.Equal(77, untouched.Buid);
            Assert.Equal("$2a$11$realcustomerhash", untouched.PasswordHash);
            Assert.NotEqual(superAdmin.SetupId, untouched.RoleId);
            Assert.NotEqual(demoBusinessUnit.Id, untouched.Buid);

            // And no duplicate demo user was created behind it.
            Assert.Equal(1, await db.Users.CountAsync(u => u.Email == sharedEmail));
        }
    }

    public void Dispose() => _connection.Dispose();
}
