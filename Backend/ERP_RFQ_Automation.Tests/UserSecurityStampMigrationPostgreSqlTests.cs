using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Data-bearing upgrade test for 20260902120000_UserSecurityStamp (docs/design/token-revocation.md).
///
/// <para>Two properties an empty-database migration test cannot see: (1) every row that EXISTS
/// when the column lands gets its OWN stamp — a shared value would make one revoked token's
/// stamp equal to everyone else's and the whole mechanism a no-op for the pre-existing
/// accounts; (2) <c>nexora_identity_app</c> can UPDATE the column, because the two anonymous
/// paths that rotate it (password reset, invitation activation) run as that role and hold only
/// column-level UPDATE on "Users". Without the grant the first customer password reset in
/// production fails with 42501, and nothing on the portable lane would have said so.</para>
/// </summary>
public sealed class UserSecurityStampMigrationPostgreSqlTests
{
    private const string PriorMigration = "20260829223000_GovernShipmentAndDeliveryReplay";
    private const string TargetMigration = "20260902120000_UserSecurityStamp";

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Existing_users_each_get_a_distinct_stamp_and_the_identity_role_can_rotate_it()
    {
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        await using var container = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("nexora_security_stamp_upgrade")
            .WithUsername("nexora")
            .WithPassword("nexora-tests")
            .Build();
        await container.StartAsync();

        var options = new DbContextOptionsBuilder<ErpRfqAutomationContext>()
            .UseNpgsql(container.GetConnectionString())
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var context = new ErpRfqAutomationContext(options, new StubTenant(null));
        var migrator = context.GetService<IMigrator>();

        await migrator.MigrateAsync(PriorMigration);

        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using (var seed = connection.CreateCommand())
        {
            seed.CommandText = """
                INSERT INTO public."BusinessUnits" ("ID", "BusinessUnitCode", "BusinessUnitName", "IsActive", "CreatedBy", "CreatedOn")
                VALUES (9601, 'STAMP', 'Stamp BU', true, 'test', now());
                INSERT INTO public."Users" ("FirstName", "LastName", "Email", "Password_Hash", "ImageURL", "BUID", "IsActive", "CreatedBy", "CreatedOn")
                VALUES ('A', 'One', 'stamp-a@nexora.invalid', 'x', 'n/a', 9601, true, 'test', now()),
                       ('B', 'Two', 'stamp-b@nexora.invalid', 'x', 'n/a', 9601, false, 'test', now());
                """;
            await seed.ExecuteNonQueryAsync();
        }

        await migrator.MigrateAsync(TargetMigration);

        await using (var check = connection.CreateCommand())
        {
            check.CommandText = """
                SELECT count(*) = 2
                   AND count(DISTINCT "SecurityStamp") = 2
                   AND bool_and("SecurityStamp" IS NOT NULL AND length("SecurityStamp") = 32)
                FROM public."Users";
                """;
            Assert.True((bool)(await check.ExecuteScalarAsync())!,
                "The migration must give every pre-existing user its own non-null stamp.");
        }

        await using (var column = connection.CreateCommand())
        {
            column.CommandText = """
                SELECT is_nullable = 'NO' AND column_default IS NOT NULL AND character_maximum_length = 64
                FROM information_schema.columns
                WHERE table_schema = 'public' AND table_name = 'Users' AND column_name = 'SecurityStamp';
                """;
            Assert.True((bool)(await column.ExecuteScalarAsync())!,
                "SecurityStamp must be NOT NULL varchar(64) and KEEP a per-row default.");
        }

        // A raw-SQL insert that omits the column — an ops script, a fixture, a seeder — must
        // succeed and must get its OWN stamp. The first cut dropped the default after the
        // backfill and three guard tests that insert users by SQL failed with 23502.
        await using (var raw = connection.CreateCommand())
        {
            raw.CommandText = """
                INSERT INTO public."Users" ("FirstName", "LastName", "Email", "Password_Hash", "ImageURL", "BUID", "IsActive", "CreatedBy", "CreatedOn")
                VALUES ('C', 'Three', 'stamp-c@nexora.invalid', 'x', 'n/a', 9601, true, 'raw-sql', now()),
                       ('D', 'Four', 'stamp-d@nexora.invalid', 'x', 'n/a', 9601, true, 'raw-sql', now());
                SELECT count(*) = 4 AND count(DISTINCT "SecurityStamp") = 4
                   AND bool_and(length("SecurityStamp") = 32)
                FROM public."Users";
                """;
            Assert.True((bool)(await raw.ExecuteScalarAsync())!,
                "A raw insert without SecurityStamp must succeed and receive a distinct 32-char stamp.");
        }

        await using (var grant = connection.CreateCommand())
        {
            grant.CommandText = """
                SELECT has_column_privilege('nexora_identity_app', 'public."Users"', 'SecurityStamp', 'UPDATE')
                   AND has_table_privilege('nexora_tenant_app', 'public."Users"', 'UPDATE')
                   AND has_table_privilege('nexora_pipeline_app', 'public."Users"', 'UPDATE')
                   AND NOT has_table_privilege('nexora_identity_app', 'public."Users"', 'UPDATE');
                """;
            Assert.True((bool)(await grant.ExecuteScalarAsync())!,
                "nexora_identity_app needs UPDATE on SecurityStamp (and only that column, plus the "
                + "two it already held); the tenant and pipeline roles keep table-level UPDATE.");
        }

        // The new column is visible to the model without drift: what the previous test in the
        // lane (AllMigrationsApplyToAnEmptyPostgreSqlDatabase) asserts for the whole estate,
        // asserted here for the one migration this stream authored.
        await migrator.MigrateAsync();
        Assert.False(context.Database.HasPendingModelChanges());

        // Down is honest: the grant goes, the column goes, nothing else moves.
        await migrator.MigrateAsync(PriorMigration);
        await using (var gone = connection.CreateCommand())
        {
            gone.CommandText = """
                SELECT NOT EXISTS (SELECT 1 FROM information_schema.columns
                                   WHERE table_name = 'Users' AND column_name = 'SecurityStamp')
                   AND (SELECT count(*) FROM public."Users") = 4;
                """;
            Assert.True((bool)(await gone.ExecuteScalarAsync())!);
        }
    }
}
