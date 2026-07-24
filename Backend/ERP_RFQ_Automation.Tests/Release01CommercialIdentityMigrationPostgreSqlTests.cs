using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class Release01CommercialIdentityMigrationPostgreSqlTests
{
    private readonly PostgreSqlTestDatabase _database;

    public Release01CommercialIdentityMigrationPostgreSqlTests(PostgreSqlTestDatabase database) =>
        _database = database;

    [Theory]
    [InlineData("cross_tenant", "23503")]
    [InlineData("null_customer", "23514")]
    [InlineData("order_quote_null_customer", "23514")]
    [Trait("Category", "PostgreSQL")]
    public async Task PopulatedUpgrade_RejectsUnsafeLegacyIdentity(string scenario, string expectedSqlState)
    {
        var databaseName = $"release01_identity_guard_{Guid.NewGuid():N}";
        var adminBuilder = new NpgsqlConnectionStringBuilder(_database.ConnectionString) { Database = "postgres" };
        var isolatedBuilder = new NpgsqlConnectionStringBuilder(_database.ConnectionString) { Database = databaseName };
        await using (var admin = new NpgsqlConnection(adminBuilder.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = admin.CreateCommand();
            create.CommandText = $"CREATE DATABASE \"{databaseName}\"";
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            await using var context = _database.ContextForConnectionString(isolatedBuilder.ConnectionString, null);
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync("20260724004000_AuthoritativeEvidenceIngestion");
            await context.Database.ExecuteSqlRawAsync("""
                INSERT INTO "BusinessUnits"
                    ("ID", "BusinessUnitCode", "BusinessUnitName", "CreatedBy", "CreatedOn")
                VALUES (94911, 'R1G1', 'Guard tenant one', 'tests', now()),
                       (94912, 'R1G2', 'Guard tenant two', 'tests', now());
                INSERT INTO "Email_Configurations"
                    ("ID", "BusinessUnitID", "ConfigurationName", "EmailAddress", "Protocol", "Host",
                     "Port", "Username", "Password", "UseSSL", "PollingInterval", "IsActive", "CreatedOn")
                VALUES (94912, 94912, 'guard', 'guard@nexora.invalid', 'IMAP', 'localhost',
                        993, 'tests', 'tests', true, 300, false, now());
                INSERT INTO "EmailIngests" ("ID", "MessageID", "FromEmail", "EmailConfigurationID", "CreatedOn")
                VALUES (94912, 'release-01-guard', 'unknown@nexora.invalid', 94912, now());
                INSERT INTO "Customers"
                    ("ID", "Name", "ContactEmail", "ImageURL", "BUID", "CreatedBy", "CreatedOn")
                VALUES (94911, 'Guard customer', 'buyer@nexora.invalid', '', 94911, 'tests', now());
                """);

            if (scenario == "cross_tenant")
            {
                await context.Database.ExecuteSqlRawAsync("""
                    INSERT INTO "Leads"
                        ("ID", "RFQNo", "RecDate", "LeadSource", "CreatedBy", "CreatedDate",
                         "BusinessUnitID", "EmailIngestsID", "Clientemail")
                    VALUES (94912, 'CROSS-LEAD', now(), 'MigrationTest', 'tests', now(),
                            94912, 94912, 'unknown@nexora.invalid');
                    INSERT INTO "RFQ"
                        ("ID", "RFQNo", "RecDate", "BusinessUnitID", "LeadID", "CreatedBy", "CreatedDate")
                    VALUES (94911, 'CROSS-RFQ', now(), 94911, 94912, 'tests', now());
                    """);
            }
            else if (scenario == "null_customer")
            {
                await context.Database.ExecuteSqlRawAsync("""
                    INSERT INTO "Leads"
                        ("ID", "RFQNo", "RecDate", "LeadSource", "CreatedBy", "CreatedDate",
                         "BusinessUnitID", "EmailIngestsID", "Clientemail")
                    VALUES (94911, 'NULL-CUSTOMER-LEAD', now(), 'MigrationTest', 'tests', now(),
                            94911, 94912, 'unknown@nexora.invalid');
                    INSERT INTO "RFQ"
                        ("ID", "RFQNo", "RecDate", "BusinessUnitID", "LeadID", "CustomerID", "CreatedBy", "CreatedDate")
                    VALUES (94911, 'NULL-CUSTOMER-RFQ', now(), 94911, 94911, 94911, 'tests', now());
                    """);
            }

            var targetMigration = "20260724223932_Release01CommercialIdentity";
            if (scenario == "order_quote_null_customer")
            {
                await context.Database.ExecuteSqlRawAsync("""
                    INSERT INTO "Leads"
                        ("ID", "RFQNo", "RecDate", "LeadSource", "CreatedBy", "CreatedDate",
                         "BusinessUnitID", "EmailIngestsID", "Clientemail")
                    VALUES (94911, 'ORDER-LEAD', now(), 'MigrationTest', 'tests', now(),
                            94911, 94912, 'unknown@nexora.invalid');
                    INSERT INTO "RFQ"
                        ("ID", "RFQNo", "RecDate", "BusinessUnitID", "LeadID", "CreatedBy", "CreatedDate")
                    VALUES (94911, 'ORDER-RFQ', now(), 94911, 94911, 'tests', now());
                    INSERT INTO "Quotes"
                        ("ID", "QuoteNo", "BusinessUnitID", "RFQID", "CreatedBy", "CreatedDate")
                    VALUES (94911, 'ORDER-QUOTE', 94911, 94911, 'tests', now());
                    """);
                await migrator.MigrateAsync(targetMigration);
                var statusId = await context.Database.SqlQueryRaw<long>("""
                    SELECT "SetupID" AS "Value" FROM "Setup_Master"
                    WHERE "BusinessUnitID" = 94911 AND "SetupType" = 'QuoteStatus' AND "SetupCode" = 'DRAFT'
                    """).SingleAsync();
                await context.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO "Orders"
                        ("ID", "OrderNo", "QuoteID", "LeadID", "RFQID", "CustomerID", "BusinessUnitID", "SourceType",
                         "StatusID", "OrderDate", "TotalAmount", "PaidAmount", "CreatedBy", "CreatedOn")
                    VALUES (94911, 'ORDER-QUOTE-NULL-CUSTOMER', 94911, 94911, 94911, 94911, 94911, 'LEGACY_QUOTE',
                            {statusId}, now(), 100, 0, 'tests', now())
                    """);
                targetMigration = "20260724230121_Release01OrderLineage";
            }

            var failure = await Assert.ThrowsAsync<PostgresException>(() =>
                migrator.MigrateAsync(targetMigration));
            Assert.Equal(expectedSqlState, failure.SqlState);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
            await admin.OpenAsync();
            await using var drop = admin.CreateCommand();
            drop.CommandText = $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)";
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task PopulatedUpgrade_BackfillsAndProtectsNexoraSerialLineage()
    {
        var databaseName = $"release01_identity_{Guid.NewGuid():N}";
        var adminBuilder = new NpgsqlConnectionStringBuilder(_database.ConnectionString) { Database = "postgres" };
        var isolatedBuilder = new NpgsqlConnectionStringBuilder(_database.ConnectionString) { Database = databaseName };

        await using (var admin = new NpgsqlConnection(adminBuilder.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = admin.CreateCommand();
            create.CommandText = $"CREATE DATABASE \"{databaseName}\"";
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            await using var context = _database.ContextForConnectionString(isolatedBuilder.ConnectionString, null);
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync("20260724004000_AuthoritativeEvidenceIngestion");
            await context.Database.ExecuteSqlRawAsync("""
                INSERT INTO "BusinessUnits"
                    ("ID", "BusinessUnitCode", "BusinessUnitName", "CreatedBy", "CreatedOn")
                VALUES (94901, 'REL01', 'Release 01 migration', 'tests', now());

                INSERT INTO "Email_Configurations"
                    ("ID", "BusinessUnitID", "ConfigurationName", "EmailAddress", "Protocol", "Host",
                     "Port", "Username", "Password", "UseSSL", "PollingInterval", "IsActive", "CreatedOn")
                VALUES (94901, 94901, 'release-01', 'release01@nexora.invalid', 'IMAP', 'localhost',
                        993, 'tests', 'tests', true, 300, false, now());

                INSERT INTO "EmailIngests"
                    ("ID", "MessageID", "FromEmail", "EmailConfigurationID", "CreatedOn")
                VALUES (94901, 'release-01-upgrade', 'buyer@nexora.invalid', 94901, now());

                INSERT INTO "Customers"
                    ("ID", "Name", "ContactEmail", "ImageURL", "BUID", "CreatedBy", "CreatedOn")
                VALUES (94901, 'Release 01 customer', 'buyer@nexora.invalid', '', 94901, 'tests', now());

                INSERT INTO "Contacts"
                    ("ID", "CustomerID", "FirstName", "LastName", "Email", "CreatedBy", "CreatedOn")
                VALUES (94901, 94901, 'Release', 'Buyer', 'buyer@nexora.invalid', 'tests', now());

                INSERT INTO "Leads"
                    ("ID", "RFQNo", "RecDate", "LeadSource", "CreatedBy", "CreatedDate",
                     "BusinessUnitID", "EmailIngestsID", "Clientemail")
                VALUES (94901, 'CUSTOMER-RFQ-94901', now(), 'MigrationTest', 'tests', now(), 94901, 94901,
                        'buyer@nexora.invalid');

                INSERT INTO "RFQ"
                    ("ID", "RFQNo", "RecDate", "BusinessUnitID", "LeadID", "CreatedBy", "CreatedDate")
                VALUES (94901, 'NEXORA-RFQ-94901', now(), 94901, 94901, 'tests', now());

                INSERT INTO "Quotes"
                    ("ID", "QuoteNo", "BusinessUnitID", "RFQID", "CreatedBy", "CreatedDate")
                VALUES (94901, 'QUOTE-94901', 94901, 94901, 'tests', now());
                """);

            var leadSerial = await context.Database.SqlQueryRaw<string>("""
                SELECT "CommercialCaseReference" AS "Value" FROM "Leads" WHERE "ID" = 94901
                """).SingleAsync();

            await migrator.MigrateAsync("20260724223932_Release01CommercialIdentity");

            var downstream = await context.Database.SqlQueryRaw<string>("""
                SELECT "NexoraSerial" AS "Value" FROM "RFQ" WHERE "ID" = 94901
                UNION ALL
                SELECT "NexoraSerial" AS "Value" FROM "Quotes" WHERE "ID" = 94901
                ORDER BY "Value"
                """).ToListAsync();
            Assert.Equal(2, downstream.Count);
            Assert.All(downstream, serial => Assert.Equal(leadSerial, serial));

            var constraint = await context.Database.SqlQueryRaw<string>("""
                SELECT pg_get_constraintdef(oid) AS "Value"
                FROM pg_constraint
                WHERE conname = 'CK_lifecycle_events_AggregateType'
                """).SingleAsync();
            Assert.Contains("Quote", constraint);

            var quoteStatusCount = await context.Database.SqlQueryRaw<int>("""
                SELECT count(*)::int AS "Value" FROM "Setup_Master"
                WHERE "BusinessUnitID" = 94901 AND "SetupType" = 'QuoteStatus'
                  AND "SetupCode" IN ('DRAFT','SENT','ACCEPTED','REJECTED','EXPIRED','ORDERED')
                """).SingleAsync();
            Assert.Equal(6, quoteStatusCount);

            var statusId = await context.Database.SqlQueryRaw<long>("""
                SELECT "SetupID" AS "Value" FROM "Setup_Master"
                WHERE "BusinessUnitID" = 94901 AND "SetupType" = 'QuoteStatus' AND "SetupCode" = 'DRAFT'
                """).SingleAsync();
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "Orders"
                    ("ID", "OrderNo", "QuoteID", "LeadID", "RFQID", "CustomerID", "BusinessUnitID", "SourceType",
                     "StatusID", "OrderDate", "TotalAmount", "PaidAmount", "CreatedBy", "CreatedOn")
                VALUES (94901, 'ORDER-94901', 94901, 94901, 94901, 94901, 94901, 'LEGACY_QUOTE',
                        {statusId}, now(), 100, 0, 'tests', now())
                """);
            await migrator.MigrateAsync("20260724230121_Release01OrderLineage");

            var orderSerial = await context.Database.SqlQueryRaw<string>("""
                SELECT "NexoraSerial" AS "Value" FROM "Orders" WHERE "ID" = 94901
                """).SingleAsync();
            Assert.Equal(leadSerial, orderSerial);

            var forgedOrder = await Assert.ThrowsAsync<PostgresException>(() =>
                context.Database.ExecuteSqlRawAsync(
                    "UPDATE \"Orders\" SET \"NexoraSerial\" = 'FORGED' WHERE \"ID\" = 94901"));
            Assert.Equal("55000", forgedOrder.SqlState);

            await context.Database.ExecuteSqlRawAsync("""
                INSERT INTO "BusinessUnits"
                    ("ID", "BusinessUnitCode", "BusinessUnitName", "CreatedBy", "CreatedOn")
                VALUES (94902, 'REL02', 'Other tenant', 'tests', now());
                INSERT INTO "Customers"
                    ("ID", "Name", "ContactEmail", "ImageURL", "BUID", "CreatedBy", "CreatedOn")
                VALUES (94902, 'Other customer', 'other@nexora.invalid', '', 94902, 'tests', now());
                """);

            await using (var tenantTransaction = await context.Database.BeginTransactionAsync())
            {
                await context.Database.ExecuteSqlRawAsync("""
                    SET LOCAL ROLE nexora_tenant_app;
                    SET LOCAL nexora.business_unit_id = '94901';
                    """);
                var visibleCustomers = await context.Database.SqlQueryRaw<int>("""
                    SELECT count(*)::int AS "Value" FROM "Customers"
                    """).SingleAsync();
                var visibleContacts = await context.Database.SqlQueryRaw<int>("""
                    SELECT count(*)::int AS "Value" FROM "Contacts"
                    """).SingleAsync();
                Assert.Equal(1, visibleCustomers);
                Assert.Equal(1, visibleContacts);

                var deniedCustomer = await Assert.ThrowsAsync<PostgresException>(() =>
                    context.Database.ExecuteSqlRawAsync("""
                        INSERT INTO "Customers"
                            ("ID", "Name", "ContactEmail", "ImageURL", "BUID", "CreatedBy", "CreatedOn")
                        VALUES (94903, 'Forged customer', 'forged@nexora.invalid', '', 94902, 'tests', now())
                        """));
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, deniedCustomer.SqlState);
                await tenantTransaction.RollbackAsync();
            }

            var crossTenantLead = await Assert.ThrowsAsync<PostgresException>(() =>
                context.Database.ExecuteSqlRawAsync("""
                    INSERT INTO "Leads"
                        ("ID", "RFQNo", "RecDate", "LeadSource", "CreatedBy", "CreatedDate",
                         "BusinessUnitID", "CustomerID", "CustomerMatchStatus")
                    VALUES (94902, 'CROSS-TENANT', now(), 'MigrationTest', 'tests', now(),
                            94901, 94902, 'VERIFIED')
                    """));
            Assert.Equal("23503", crossTenantLead.SqlState);

            var immutable = await Assert.ThrowsAsync<PostgresException>(() =>
                context.Database.ExecuteSqlRawAsync(
                    "UPDATE \"Quotes\" SET \"NexoraSerial\" = 'FORGED' WHERE \"ID\" = 94901"));
            Assert.Equal("55000", immutable.SqlState);

            var ungovernedStatus = await Assert.ThrowsAsync<PostgresException>(() =>
                context.Database.ExecuteSqlRawAsync(
                    "UPDATE \"Quotes\" SET \"StatusID\" = 1 WHERE \"ID\" = 94901"));
            Assert.Equal("55000", ungovernedStatus.SqlState);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
            await admin.OpenAsync();
            await using var drop = admin.CreateCommand();
            drop.CommandText = $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)";
            await drop.ExecuteNonQueryAsync();
        }
    }
}
