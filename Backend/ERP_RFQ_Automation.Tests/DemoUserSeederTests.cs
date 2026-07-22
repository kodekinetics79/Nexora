using ERP_RFQ_Automation.Infrastructure;
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
        await DemoUserSeeder.EnsureAsync(provider, Config(new()), new FakeEnv());

        await using var db = NewContext();
        Assert.Equal(0, await db.Users.CountAsync());
        Assert.Equal(0, await db.Set<PlatformUser>().CountAsync());
    }

    [Fact]
    public async Task Seeder_enabled_without_passwords_seeds_nothing()
    {
        var provider = BuildProvider();
        var config = Config(new() { ["DemoUser:Enabled"] = "true" }); // enabled, but no passwords supplied

        await DemoUserSeeder.EnsureAsync(provider, config, new FakeEnv());

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
        await DemoUserSeeder.EnsureAsync(BuildProvider(), config, new FakeEnv());

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
        await DemoUserSeeder.EnsureAsync(BuildProvider(), rotatedConfig, new FakeEnv());

        await using (var db = NewContext())
        {
            var user = await db.Users.SingleAsync(u => u.Email == "robert@example.com");
            var owner = await db.Set<PlatformUser>().SingleAsync(u => u.Email == "owner@nexora.app");
            // The password hash must be unchanged from the first run — the restart must not reset it.
            Assert.Equal(userHashAfterFirstRun, user.PasswordHash);
            Assert.Equal(ownerHashAfterFirstRun, owner.PasswordHash);
        }
    }

    public void Dispose() => _connection.Dispose();
}
