using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class Module02CustomerContinuityMigrationPostgreSqlTests(
    PostgreSqlTestDatabase database)
{
    private const string PreviousMigration = "20260730193414_SynchronizeSharedExtractionOccurrences";
    private const string CurrentMigration = "20260730222700_Module02CustomerContinuity";

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Populated_upgrade_backfills_versions_enforces_customer_numbers_and_reupgrades()
    {
        var databaseName = $"nexora_customer_continuity_{Guid.NewGuid():N}";
        var rehearsal = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            Database = databaseName
        };

        await ExecuteAdminAsync(database.ConnectionString, $"CREATE DATABASE \"{databaseName}\"");
        try
        {
            await using var context = database.ContextForConnectionString(rehearsal.ConnectionString, null);
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(PreviousMigration);
            var historicalMigrationCount = await MigrationCountAsync(context);

            await context.Database.ExecuteSqlRawAsync("""
                INSERT INTO "BusinessUnits"
                    ("ID", "BusinessUnitCode", "BusinessUnitName", "CreatedBy", "CreatedOn")
                VALUES (99401, 'CRM-MIG', 'CRM migration', 'tests', now());

                INSERT INTO "Customers"
                    ("ID", "DocId", "Name", "ImageURL", "BUID", "IsActive", "CreatedBy", "CreatedOn")
                VALUES
                    (99402, 'CU00099402', 'Migration Customer A', '', 99401, true, 'tests', now()),
                    (99403, 'CU00099403', 'Migration Customer B', '', 99401, true, 'tests', now());

                INSERT INTO "Contacts"
                    ("ID", "BusinessUnitID", "CustomerID", "FirstName", "LastName",
                     "IsPrimary", "IsActive", "CreatedBy", "CreatedOn")
                VALUES
                    (99404, 99401, 99402, 'Primary', 'Buyer', true, true, 'tests', now()),
                    (99405, 99401, 99403, 'Other', 'Buyer', true, true, 'tests', now());
                """);

            await migrator.MigrateAsync(CurrentMigration);

            var versions = await context.Database.SqlQueryRaw<Guid>("""
                SELECT "ConcurrencyToken" AS "Value" FROM "Customers" WHERE "BUID" = 99401
                UNION ALL
                SELECT "ConcurrencyToken" AS "Value" FROM "Contacts" WHERE "BusinessUnitID" = 99401
                """).ToListAsync();
            Assert.Equal(4, versions.Count);
            Assert.All(versions, version => Assert.NotEqual(Guid.Empty, version));
            Assert.Equal(4, versions.Distinct().Count());
            Assert.Equal(historicalMigrationCount + 1, await MigrationCountAsync(context));

            var duplicateNumber = await Assert.ThrowsAsync<PostgresException>(() =>
                context.Database.ExecuteSqlRawAsync("""
                    INSERT INTO "Customers"
                        ("DocId", "Name", "ImageURL", "BUID", "IsActive", "CreatedBy", "CreatedOn", "ConcurrencyToken")
                    VALUES ('CU00099402', 'Duplicate number', '', 99401, true, 'tests', now(), gen_random_uuid());
                    """));
            Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicateNumber.SqlState);

            await context.Database.ExecuteSqlRawAsync("""
                INSERT INTO "Contacts"
                    ("ID", "BusinessUnitID", "CustomerID", "FirstName", "LastName",
                     "IsPrimary", "IsActive", "CreatedBy", "CreatedOn", "ConcurrencyToken")
                VALUES
                    (99406, 99401, 99402, 'Former', 'Primary', true, false, 'tests', now(), gen_random_uuid());
                """);
            var duplicateActivePrimary = await Assert.ThrowsAsync<PostgresException>(() =>
                context.Database.ExecuteSqlRawAsync("""
                    INSERT INTO "Contacts"
                        ("ID", "BusinessUnitID", "CustomerID", "FirstName", "LastName",
                         "IsPrimary", "IsActive", "CreatedBy", "CreatedOn", "ConcurrencyToken")
                    VALUES
                        (99407, 99401, 99402, 'Duplicate', 'Primary', true, true, 'tests', now(), gen_random_uuid());
                    """));
            Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicateActivePrimary.SqlState);

            await migrator.MigrateAsync(PreviousMigration);
            Assert.Equal(historicalMigrationCount, await MigrationCountAsync(context));
            Assert.Equal(2, await CustomerCountAsync(context));

            await migrator.MigrateAsync(CurrentMigration);
            Assert.Equal(historicalMigrationCount + 1, await MigrationCountAsync(context));
            Assert.Equal(2, await CustomerCountAsync(context));
            Assert.Equal(5, await context.Database.SqlQueryRaw<int>("""
                SELECT count(*)::int AS "Value" FROM (
                    SELECT "ConcurrencyToken" FROM "Customers" WHERE "BUID" = 99401
                    UNION ALL
                    SELECT "ConcurrencyToken" FROM "Contacts" WHERE "BusinessUnitID" = 99401
                ) versions WHERE "ConcurrencyToken" <> '00000000-0000-0000-0000-000000000000'::uuid
                """).SingleAsync());
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAdminAsync(database.ConnectionString,
                $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)");
        }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Customer_and_contact_mutations_support_retry_strategy_and_serialize_identity_sync()
    {
        const long tenant = 99420;
        await using (var seed = database.ContextFor(null))
        {
            seed.BusinessUnits.Add(new BusinessUnit
            {
                Id = tenant,
                BusinessUnitCode = "CRM-RETRY",
                BusinessUnitName = "CRM retry tenant",
                IsActive = true,
                CreatedBy = "tests",
                CreatedOn = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        var options = new DbContextOptionsBuilder<ErpRfqAutomationContext>()
            .UseNpgsql(database.ConnectionString, npgsql => npgsql.EnableRetryOnFailure())
            .Options;
        long customerId;
        await using (var context = new ErpRfqAutomationContext(options, new StubTenant(tenant)))
        {
            var customers = new CustomerRepository(context);
            var customer = new Customer
            {
                Name = "Retry Strategy Account",
                ContactEmail = "routing@retry.test",
                ImageUrl = string.Empty
            };
            await customers.AddAsync(customer, tenant, "tests");
            customerId = customer.Id;

            var contacts = new ContactRepository(context);
            var contact = new Contact
            {
                CustomerId = customerId,
                FirstName = "Retry",
                LastName = "Contact",
                Email = "contact@retry.test"
            };
            await contacts.AddAsync(contact, tenant, "tests");
            await contacts.UpdateAsync(new Contact
            {
                Id = contact.Id,
                CustomerId = customerId,
                FirstName = "Updated",
                LastName = "Contact",
                Email = "contact@retry.test"
            }, tenant, "tests", contact.ConcurrencyToken);
        }

        await using (var seed = new ErpRfqAutomationContext(options, new StubTenant(tenant)))
        {
            seed.Contacts.Add(new Contact
            {
                BusinessUnitId = tenant,
                CustomerId = customerId,
                FirstName = "Concurrent",
                LastName = "Identity",
                Email = "concurrent@retry.test",
                IsActive = true,
                CreatedBy = "tests",
                CreatedOn = DateTime.UtcNow,
                ConcurrencyToken = Guid.NewGuid()
            });
            await seed.SaveChangesAsync();
        }

        async Task SynchronizeAsync()
        {
            await using var context = new ErpRfqAutomationContext(options, new StubTenant(tenant));
            await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var transaction = await context.Database.BeginTransactionAsync();
                await CustomerIdentityMaintenance.SynchronizeAsync(
                    context, tenant, customerId, "CustomerContact");
                await context.SaveChangesAsync();
                await transaction.CommitAsync();
            });
        }

        await Task.WhenAll(SynchronizeAsync(), SynchronizeAsync());

        await using var verify = new ErpRfqAutomationContext(options, new StubTenant(tenant));
        var activeConcurrentEmails = await verify.Set<CustomerIdentifier>().AsNoTracking()
            .CountAsync(x => x.BusinessUnitId == tenant && x.CustomerId == customerId &&
                x.IdentifierType == CustomerIdentifierType.Email &&
                x.NormalizedValue == "concurrent@retry.test" && x.EffectiveTo == null);
        Assert.Equal(1, activeConcurrentEmails);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Customer_update_rebuilds_tracked_state_after_transient_post_save_failure()
    {
        const long tenant = 99430;
        await using (var seed = database.ContextFor(null))
        {
            seed.BusinessUnits.Add(new BusinessUnit
            {
                Id = tenant,
                BusinessUnitCode = "CRM-TRANSIENT",
                BusinessUnitName = "CRM transient tenant",
                IsActive = true,
                CreatedBy = "tests",
                CreatedOn = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        var interceptor = new ThrowOnceAfterSaveInterceptor();
        var options = new DbContextOptionsBuilder<ErpRfqAutomationContext>()
            .UseNpgsql(database.ConnectionString, npgsql => npgsql.EnableRetryOnFailure(maxRetryCount: 3))
            .AddInterceptors(interceptor)
            .Options;
        long customerId;
        Guid token;
        await using (var context = new ErpRfqAutomationContext(options, new StubTenant(tenant)))
        {
            var repository = new CustomerRepository(context);
            var customer = new Customer { Name = "Before retry", ImageUrl = string.Empty };
            await repository.AddAsync(customer, tenant, "tests");
            customerId = customer.Id;
            token = customer.ConcurrencyToken;

            interceptor.Arm();
            await repository.UpdateAsync(new Customer
            {
                Id = customerId,
                Name = "After retry",
                ContactEmail = "after-retry@example.test",
                ImageUrl = string.Empty
            }, tenant, "tests", token);
        }

        Assert.Equal(1, interceptor.Failures);
        await using var verify = database.ContextFor(tenant);
        var persisted = await verify.Customers.AsNoTracking().SingleAsync(customer => customer.Id == customerId);
        Assert.Equal("After retry", persisted.Name);
        Assert.Equal("after-retry@example.test", persisted.ContactEmail);
        Assert.NotEqual(token, persisted.ConcurrencyToken);
    }

    private static Task<int> CustomerCountAsync(DbContext context) =>
        context.Database.SqlQueryRaw<int>("""
            SELECT count(*)::int AS "Value" FROM "Customers" WHERE "BUID" = 99401
            """).SingleAsync();

    private static Task<int> MigrationCountAsync(DbContext context) =>
        context.Database.SqlQueryRaw<int>("""
            SELECT count(*)::int AS "Value" FROM "__EFMigrationsHistory"
            """).SingleAsync();

    private static async Task ExecuteAdminAsync(string connectionString, string sql)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres" };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class ThrowOnceAfterSaveInterceptor : SaveChangesInterceptor
    {
        private bool _armed;
        public int Failures { get; private set; }

        public void Arm() => _armed = true;

        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            if (_armed && Failures == 0)
            {
                Failures++;
                throw new NpgsqlException("Simulated transient post-save failure.", new TimeoutException());
            }

            return ValueTask.FromResult(result);
        }
    }
}
