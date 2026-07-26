using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Testcontainers.PostgreSql;

namespace ERP_RFQ_Automation.Tests;

public sealed class CoreSalesPostgreSqlTests
{
    private const long TenantA = 97_610;
    private const long TenantB = 97_611;
    private const long CustomerA = 97_612;
    private const long UserA = 97_613;
    private const long UserB = 97_614;
    private DbContextOptions<ErpRfqAutomationContext> _options = null!;
    private string _connectionString = string.Empty;

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Sales_schema_enforces_tenant_references_usage_only_sequences_and_one_active_owner()
    {
        await using var database = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("nexora_sales_tests").WithUsername("nexora").WithPassword("nexora-tests").Build();
        await database.StartAsync();
        _connectionString = database.GetConnectionString();
        _options = new DbContextOptionsBuilder<ErpRfqAutomationContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using (var migrate = Context(null)) await migrate.Database.MigrateAsync();
        await SeedAsync();

        await using (var connection = new NpgsqlConnection(_connectionString))
        {
            await connection.OpenAsync();
            await using var referenceCommand = connection.CreateCommand();
            referenceCommand.CommandText = """
                SELECT count(*)
                FROM pg_constraint
                WHERE contype = 'f'
                  AND conname = ANY (ARRAY[
                    'FK_sales_activity_tenant_user', 'FK_sales_activity_tenant_customer',
                    'FK_sales_activity_tenant_assignment', 'FK_follow_up_tenant_user',
                    'FK_follow_up_tenant_customer', 'FK_follow_up_event_tenant_task',
                    'FK_sales_contribution_tenant_user', 'FK_sales_contribution_tenant_customer',
                    'FK_sales_profile_tenant_user', 'FK_sales_membership_tenant_user',
                    'FK_sales_membership_tenant_team', 'FK_customer_owner_tenant_customer',
                    'FK_customer_owner_tenant_primary_user', 'FK_customer_owner_tenant_backup_user'])
                  AND array_length(conkey, 1) = 2;
                """;
            Assert.Equal(14L, Convert.ToInt64(await referenceCommand.ExecuteScalarAsync()));

            await using var privilegeCommand = connection.CreateCommand();
            privilegeCommand.CommandText = """
                SELECT count(*)
                FROM pg_class sequence
                JOIN pg_namespace namespace ON namespace.oid = sequence.relnamespace
                WHERE namespace.nspname = 'public'
                  AND sequence.relkind = 'S'
                  AND sequence.relname = ANY (ARRAY[
                    'commercial_activities_Id_seq', 'follow_up_tasks_Id_seq',
                    'follow_up_transition_events_Id_seq', 'sales_contributions_Id_seq',
                    'sales_rep_profiles_Id_seq', 'sales_team_memberships_Id_seq'])
                  AND has_sequence_privilege('nexora_tenant_app', sequence.oid, 'USAGE')
                  AND NOT has_sequence_privilege('nexora_tenant_app', sequence.oid, 'SELECT')
                  AND NOT has_sequence_privilege('nexora_tenant_app', sequence.oid, 'UPDATE');
                """;
            Assert.Equal(6L, Convert.ToInt64(await privilegeCommand.ExecuteScalarAsync()));

            await using var invalidReference = connection.CreateCommand();
            invalidReference.CommandText = """
                INSERT INTO public.sales_rep_profiles
                    ("BusinessUnitId", "UserId", "IsRoutingEligible", "CapacityPercent",
                     "DistributionWeight", "TerritoryKeys", "ProductCategoryKeys", "EffectiveFromUtc",
                     "Version", "UpdatedAtUtc", "UpdatedBy", "LastMutationIdempotencyKey")
                VALUES (@tenant, @user, TRUE, 100, 1, ARRAY[]::text[], ARRAY[]::text[], now(),
                        1, now(), 'test', 'cross-tenant-profile');
                """;
            invalidReference.Parameters.AddWithValue("tenant", TenantB);
            invalidReference.Parameters.AddWithValue("user", UserA);
            var crossTenant = await Assert.ThrowsAsync<PostgresException>(() =>
                invalidReference.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, crossTenant.SqlState);
        }

        var starts = DateTime.UtcNow.AddMinutes(-1);
        var attempts = await Task.WhenAll(
            CreateOwnerAsync(UserA, starts),
            CreateOwnerAsync(UserB, starts));

        Assert.Single(attempts, result => result is CustomerOwnership);
        Assert.IsType<RoutingConflictException>(Assert.Single(attempts, result => result is Exception));
        await using var verify = Context(TenantA);
        Assert.Single(await verify.Set<CustomerOwnership>().Where(x =>
            x.BusinessUnitId == TenantA && x.CustomerId == CustomerA && x.IsActive && x.EffectiveTo == null)
            .ToListAsync());
    }

    private async Task<object> CreateOwnerAsync(long userId, DateTime starts)
    {
        try
        {
            await using var context = Context(TenantA);
            var service = new CommercialRoutingApplicationService(
                context, new DeterministicRoutingEngine(), new RoutingPolicy());
            return await service.CreateOwnershipAsync(TenantA, new CreateCustomerOwnershipCommand(
                CustomerA, userId, null, OwnershipScope.GeneralCustomer, null, 100,
                starts, null, "postgres-test", "concurrent owner test"), CancellationToken.None);
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private async Task SeedAsync()
    {
        await using var context = Context(null);
        Seed.EnsureBusinessUnit(context, TenantA);
        Seed.EnsureBusinessUnit(context, TenantB);
        Seed.Customer(context, CustomerA, TenantA, "Sales tenant reference customer");
        context.Users.AddRange(User(UserA, TenantA, "sales-owner-a"), User(UserB, TenantA, "sales-owner-b"));
        await context.SaveChangesAsync();
    }

    private static User User(long id, long tenant, string name) => new()
    {
        Id = id, Buid = tenant, FirstName = name, LastName = "Test",
        Email = $"{name}@nexora.invalid", PasswordHash = "not-used", ImageUrl = "n/a",
        IsActive = true, CreatedBy = "tests", CreatedOn = DateTime.UtcNow
    };

    private ErpRfqAutomationContext Context(long? tenant) => new(_options, new StubTenant(tenant));
}
