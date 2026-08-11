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
    /// <summary>
    /// SQUASH NOTE — this replaces
    /// Populated_upgrade_backfills_versions_enforces_customer_numbers_and_reupgrades.
    ///
    /// That test built a database at 20260730193414_SynchronizeSharedExtractionOccurrences, wrote
    /// two customers and two contacts with no ConcurrencyToken column, upgraded to
    /// 20260730222700_Module02CustomerContinuity and asserted the backfill gave each row a distinct
    /// non-empty token, then exercised the two uniqueness rules and walked back down and up again
    /// counting history rows.
    ///
    /// 20260811033109_SquashedSchemaBaseline erased both ids and, with them, the walk. The token
    /// BACKFILL is retired — the column is now created NOT NULL and a row without one cannot exist
    /// — but the three rules the migration existed to install are asserted here against real
    /// writes on the live schema, which is where they actually have to hold:
    ///
    ///   * every customer and contact carries a distinct, non-empty concurrency token;
    ///   * a customer number is unique within a tenant;
    ///   * a customer has at most ONE active primary contact, enforced by a partial unique index
    ///     rather than by application code, so a second one is refused even from raw SQL.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Customer_identity_is_versioned_numbered_once_and_has_one_active_primary_contact()
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await using (var seed = connection.CreateCommand())
        {
            seed.Transaction = transaction;
            seed.CommandText = """
                INSERT INTO "BusinessUnits"
                    ("ID", "BusinessUnitCode", "BusinessUnitName", "CreatedBy", "CreatedOn")
                VALUES (99401, 'CRM-MIG', 'CRM continuity', 'tests', now());

                INSERT INTO "Customers"
                    ("ID", "DocId", "Name", "ImageURL", "BUID", "IsActive", "CreatedBy", "CreatedOn",
                     "ConcurrencyToken")
                VALUES
                    (99402, 'CU00099402', 'Continuity Customer A', '', 99401, true, 'tests', now(), gen_random_uuid()),
                    (99403, 'CU00099403', 'Continuity Customer B', '', 99401, true, 'tests', now(), gen_random_uuid());

                INSERT INTO "Contacts"
                    ("ID", "BusinessUnitID", "CustomerID", "FirstName", "LastName",
                     "IsPrimary", "IsActive", "CreatedBy", "CreatedOn", "ConcurrencyToken")
                VALUES
                    (99404, 99401, 99402, 'Primary', 'Buyer', true, true, 'tests', now(), gen_random_uuid()),
                    (99405, 99401, 99403, 'Other', 'Buyer', true, true, 'tests', now(), gen_random_uuid());
                """;
            await seed.ExecuteNonQueryAsync();
        }

        // Four rows, four distinct non-empty tokens. An all-zero or shared token would let two
        // concurrent edits both believe they held the current version.
        await using (var versions = connection.CreateCommand())
        {
            versions.Transaction = transaction;
            versions.CommandText = """
                SELECT count(DISTINCT "ConcurrencyToken")::int
                FROM (
                    SELECT "ConcurrencyToken" FROM "Customers" WHERE "BUID" = 99401
                    UNION ALL
                    SELECT "ConcurrencyToken" FROM "Contacts" WHERE "BusinessUnitID" = 99401
                ) tokens
                WHERE "ConcurrencyToken" IS NOT NULL
                  AND "ConcurrencyToken" <> '00000000-0000-0000-0000-000000000000'::uuid;
                """;
            Assert.Equal(4, Convert.ToInt32(await versions.ExecuteScalarAsync()));
        }

        await AssertUniqueViolationAsync(connection, transaction, """
            INSERT INTO "Customers"
                ("DocId", "Name", "ImageURL", "BUID", "IsActive", "CreatedBy", "CreatedOn", "ConcurrencyToken")
            VALUES ('CU00099402', 'Duplicate number', '', 99401, true, 'tests', now(), gen_random_uuid());
            """);

        // A DEACTIVATED former primary is not in the index's predicate, so replacing a primary
        // stays possible…
        await using (var former = connection.CreateCommand())
        {
            former.Transaction = transaction;
            former.CommandText = """
                INSERT INTO "Contacts"
                    ("ID", "BusinessUnitID", "CustomerID", "FirstName", "LastName",
                     "IsPrimary", "IsActive", "CreatedBy", "CreatedOn", "ConcurrencyToken")
                VALUES (99406, 99401, 99402, 'Former', 'Primary', true, false, 'tests', now(), gen_random_uuid());
                """;
            Assert.Equal(1, await former.ExecuteNonQueryAsync());
        }

        // …while a SECOND ACTIVE primary for the same customer is not.
        await AssertUniqueViolationAsync(connection, transaction, """
            INSERT INTO "Contacts"
                ("ID", "BusinessUnitID", "CustomerID", "FirstName", "LastName",
                 "IsPrimary", "IsActive", "CreatedBy", "CreatedOn", "ConcurrencyToken")
            VALUES (99407, 99401, 99402, 'Duplicate', 'Primary', true, true, 'tests', now(), gen_random_uuid());
            """);

        await transaction.RollbackAsync();
    }

    /// <summary>
    /// Each refusal runs in its own savepoint, because a rejected statement aborts the enclosing
    /// transaction and every later assertion would otherwise fail for the wrong reason.
    /// </summary>
    private static async Task AssertUniqueViolationAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await transaction.SaveAsync("uniqueness");
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
        await transaction.RollbackAsync("uniqueness");
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
