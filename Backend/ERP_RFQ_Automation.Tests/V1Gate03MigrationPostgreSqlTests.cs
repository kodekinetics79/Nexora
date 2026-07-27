using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class V1Gate03MigrationPostgreSqlTests(PostgreSqlTestDatabase database)
{
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Populated_upgrade_restore_downgrade_and_reupgrade_preserve_mailbox_identity()
    {
        const string previousMigration = "20260727042452_V1Gate02CommercialIntelligenceIntegrity";
        const string currentMigration = "20260727171327_V1Gate03IntegrationOperationalVisibility";
        var sourceName = $"gate3_source_{Guid.NewGuid():N}";
        var restoredName = $"gate3_restore_{Guid.NewGuid():N}";
        var adminBuilder = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Database = "postgres" };
        var sourceBuilder = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Database = sourceName };
        var restoredBuilder = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Database = restoredName };

        await ExecuteAdminAsync(adminBuilder.ConnectionString, $"CREATE DATABASE \"{sourceName}\"");
        try
        {
            await using (var source = database.ContextForConnectionString(sourceBuilder.ConnectionString, null))
            {
                var migrator = source.GetService<IMigrator>();
                await migrator.MigrateAsync(previousMigration);
                await source.Database.ExecuteSqlRawAsync("""
                    INSERT INTO public."BusinessUnits"
                        ("ID", "BusinessUnitCode", "BusinessUnitName", "CreatedBy", "CreatedOn")
                    VALUES (9927301, 'G3-UP', 'Gate 3 populated upgrade', 'tests', now());
                    INSERT INTO public."Email_Configurations"
                        ("ID", "BusinessUnitID", "ConfigurationName", "EmailAddress", "Protocol", "Host",
                         "Port", "Username", "Password", "UseSSL", "PollingInterval", "IsActive", "CreatedOn")
                    VALUES (9927301, 9927301, 'Gate 3 inbox', 'gate3@example.test', 'IMAP', 'localhost',
                            993, 'gate3', 'test-only', true, 60, true, now());
                    INSERT INTO public."EmailIngests"
                        ("ID", "MessageID", "EmailSubject", "FromEmail", "ToEmail", "EmailConfigurationID",
                         "ParseStatus", "CreatedOn")
                    VALUES (9927301, '<gate3@example.test>', 'Persisted intake', 'sender@example.test',
                            'gate3@example.test', 9927301, 'PROCESSED', now());
                    """);
            }

            NpgsqlConnection.ClearAllPools();
            await ExecuteAdminAsync(adminBuilder.ConnectionString,
                $"CREATE DATABASE \"{restoredName}\" TEMPLATE \"{sourceName}\"");

            await using (var source = database.ContextForConnectionString(sourceBuilder.ConnectionString, null))
            {
                var migrator = source.GetService<IMigrator>();
                await migrator.MigrateAsync();
                await AssertMailboxEvidenceAsync(source, currentMigration, expectedCurrentMigration: true,
                    expectedCompositeIndex: true);

                await migrator.MigrateAsync(previousMigration);
                await AssertMailboxEvidenceAsync(source, currentMigration, expectedCurrentMigration: false,
                    expectedCompositeIndex: false);

                await migrator.MigrateAsync();
                await AssertMailboxEvidenceAsync(source, currentMigration, expectedCurrentMigration: true,
                    expectedCompositeIndex: true);

                await source.Database.ExecuteSqlRawAsync("""
                    INSERT INTO public."Email_Configurations"
                        ("ID", "BusinessUnitID", "ConfigurationName", "EmailAddress", "Protocol", "Host",
                         "Port", "Username", "Password", "UseSSL", "PollingInterval", "IsActive", "CreatedOn")
                    VALUES (9927302, 9927301, 'Gate 3 second inbox', 'gate3-second@example.test', 'IMAP',
                            'localhost', 993, 'gate3-second', 'test-only', true, 60, true, now());
                    INSERT INTO public."EmailIngests"
                        ("ID", "MessageID", "EmailSubject", "FromEmail", "ToEmail", "EmailConfigurationID",
                         "ParseStatus", "CreatedOn")
                    VALUES (9927302, '<gate3@example.test>', 'Same provider ID in another mailbox',
                            'sender@example.test', 'gate3-second@example.test', 9927302, 'PROCESSED', now());
                    """);
                var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    migrator.MigrateAsync(previousMigration));
                var databaseError = Assert.IsType<PostgresException>(blocked.InnerException);
                Assert.Contains("restore the verified pre-upgrade backup", databaseError.MessageText,
                    StringComparison.OrdinalIgnoreCase);
                await AssertMailboxEvidenceAsync(source, currentMigration, expectedCurrentMigration: true,
                    expectedCompositeIndex: true);
            }

            await using (var restored = database.ContextForConnectionString(restoredBuilder.ConnectionString, null))
            {
                await restored.GetService<IMigrator>().MigrateAsync();
                await AssertMailboxEvidenceAsync(restored, currentMigration, expectedCurrentMigration: true,
                    expectedCompositeIndex: true);
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAdminAsync(adminBuilder.ConnectionString,
                $"DROP DATABASE IF EXISTS \"{restoredName}\" WITH (FORCE)");
            await ExecuteAdminAsync(adminBuilder.ConnectionString,
                $"DROP DATABASE IF EXISTS \"{sourceName}\" WITH (FORCE)");
        }
    }

    private static async Task AssertMailboxEvidenceAsync(DbContext context, string currentMigration,
        bool expectedCurrentMigration, bool expectedCompositeIndex)
    {
        Assert.Equal(1, await context.Database.SqlQueryRaw<int>("""
            SELECT count(*)::int AS "Value" FROM public."EmailIngests"
            WHERE "ID" = 9927301 AND "EmailConfigurationID" = 9927301
              AND "MessageID" = '<gate3@example.test>'
            """).SingleAsync());
        Assert.Equal(expectedCurrentMigration, (await context.Database.SqlQueryRaw<string>("""
            SELECT "MigrationId" AS "Value" FROM public."__EFMigrationsHistory"
            """).ToListAsync()).Contains(currentMigration));
        Assert.Equal(expectedCompositeIndex, await context.Database.SqlQueryRaw<bool>("""
            SELECT EXISTS (
                SELECT 1 FROM pg_indexes
                WHERE schemaname = 'public' AND tablename = 'EmailIngests'
                  AND indexname = 'UQ_EmailIngests_EmailConfigurationID_MessageID') AS "Value"
            """).SingleAsync());
    }

    private static async Task ExecuteAdminAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
