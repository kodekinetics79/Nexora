using ERP_RFQ_Automation.CommercialDocuments;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Certifies the write paths that open their OWN transaction against the execution strategy
/// PRODUCTION actually configures.
///
/// <para><b>The defect that made this file necessary.</b> <c>POST /api/Supplier</c> returned 500 for
/// every request against PostgreSQL:
/// <c>The configured execution strategy 'NpgsqlRetryingExecutionStrategy' does not support
/// user-initiated transactions.</c> <c>SupplierRepository.AddAsync</c> called
/// <c>Database.BeginTransactionAsync(Serializable)</c> directly, and Program.cs registers the
/// DbContext with <c>EnableRetryOnFailure</c>, which installs a strategy that refuses exactly
/// that. Supplier creation — the whole feature — shipped broken and nothing caught it.</para>
///
/// <para><b>Why nothing caught it, and what that dictates about this fixture.</b> Every existing
/// test builds its DbContext WITHOUT <c>EnableRetryOnFailure</c>: the SQLite lane cannot have one,
/// and <see cref="PostgreSqlTestDatabase"/> does not configure one. Under a non-retrying strategy a
/// user-initiated transaction is perfectly legal, so the defect is invisible to a test that merely
/// uses PostgreSQL — it is only visible to a test that reproduces production's DbContext
/// CONFIGURATION. That is what <see cref="RetryingContext"/> does, and it is the whole point of
/// this file. A test here that stops reproducing production's options stops testing anything.</para>
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class RetryingExecutionStrategyWritePathsPostgreSqlTests
{
    private const long BusinessUnitId = 981_401;

    private readonly PostgreSqlTestDatabase _database;

    public RetryingExecutionStrategyWritePathsPostgreSqlTests(PostgreSqlTestDatabase database)
        => _database = database;

    // ---------------------------------------------------------------- the control

    /// <summary>
    /// Proves the fixture is actually hostile. If this ever stops throwing, the retrying strategy is
    /// no longer installed and every other test in this file has quietly become a no-op — passing
    /// because nothing is being enforced rather than because the code is correct.
    ///
    /// <para>Note WHERE the refusal lands: <c>BeginTransactionAsync</c> itself succeeds, and the
    /// strategy objects at the first SAVE inside it. That is why the original defect was invisible
    /// to code that guarded the BeginTransaction call in a try/catch, and it is the exact shape
    /// <c>SupplierRepository.AddAsync</c> had.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_write_in_a_transaction_opened_outside_a_strategy_delegate_is_refused()
    {
        await SeedBusinessUnitAsync();
        try
        {
            await using var context = RetryingContext();
            await using var transaction = await context.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable);

            context.Suppliers.Add(new Supplier
            {
                Name = "Hostile Fixture Probe",
                ImageUrl = string.Empty,
                Buid = BusinessUnitId,
                IsActive = true,
                CreatedBy = "tests",
                CreatedOn = DateTime.UtcNow
            });

            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => context.SaveChangesAsync());

            Assert.Contains("does not support user-initiated transactions", failure.Message);
        }
        finally
        {
            await CleanupAsync();
        }
    }

    // ---------------------------------------------------------------- the defect

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Creating_a_supplier_succeeds_under_the_retrying_execution_strategy()
    {
        await SeedBusinessUnitAsync();
        try
        {
            await using var context = RetryingContext();
            var repository = new SupplierRepository(context);

            var supplier = new Supplier
            {
                Name = "Retry Strategy Trading Co",
                ContactEmail = "buyer@retry-strategy.test",
                ImageUrl = string.Empty,
                Buid = BusinessUnitId,
                IsActive = true,
                CreatedBy = "tests",
                CreatedOn = DateTime.UtcNow
            };

            // Before the fix this threw InvalidOperationException from inside AddAsync, and
            // SupplierController turned it into a 500 titled "Supplier not created".
            await repository.AddAsync(supplier);

            Assert.True(supplier.Id > 0);
            // The second SaveChanges inside the same retriable unit is what assigns DocId. Asserting
            // it proves the whole unit committed, not just the insert.
            Assert.False(string.IsNullOrWhiteSpace(supplier.DocId));

            await using var verify = RetryingContext();
            var stored = await verify.Suppliers.AsNoTracking()
                .SingleAsync(s => s.Id == supplier.Id);
            Assert.Equal("Retry Strategy Trading Co", stored.Name);
            Assert.Equal(SupplierGovernanceStatuses.Unverified, stored.GovernanceStatus);
            // SupplierGovernanceIdentityRules.Stamp runs inside SaveChanges; it has to have run
            // inside the strategy's transaction, or the row is ungovernable.
            Assert.NotNull(stored.ConcurrencyToken);
            Assert.NotNull(stored.EffectiveFrom);
        }
        finally
        {
            await CleanupAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Updating_a_supplier_succeeds_under_the_retrying_execution_strategy()
    {
        await SeedBusinessUnitAsync();
        try
        {
            long supplierId;
            Guid? token;
            await using (var seedContext = RetryingContext())
            {
                var repository = new SupplierRepository(seedContext);
                var supplier = new Supplier
                {
                    Name = "Retry Strategy Update Co",
                    ImageUrl = string.Empty,
                    Buid = BusinessUnitId,
                    IsActive = true,
                    CreatedBy = "tests",
                    CreatedOn = DateTime.UtcNow
                };
                await repository.AddAsync(supplier);
                supplierId = supplier.Id;
                token = supplier.ConcurrencyToken;
            }

            await using (var updateContext = RetryingContext())
            {
                var repository = new SupplierRepository(updateContext);
                await repository.UpdateAsync(new Supplier
                {
                    Id = supplierId,
                    Name = "Retry Strategy Update Co (renamed)",
                    ImageUrl = string.Empty,
                    Buid = BusinessUnitId,
                    IsActive = true,
                    ConcurrencyToken = token,
                    CreatedBy = "tests",
                    CreatedOn = DateTime.UtcNow
                }, BusinessUnitId);
            }

            await using var verify = RetryingContext();
            var stored = await verify.Suppliers.AsNoTracking().SingleAsync(s => s.Id == supplierId);
            Assert.Equal("Retry Strategy Update Co (renamed)", stored.Name);
        }
        finally
        {
            await CleanupAsync();
        }
    }

    // ---------------------------------------------------------------- the untranslatable query

    /// <summary>
    /// <c>GET /api/commercial-inbox/classifications</c> returned 500 on its FIRST, unfiltered page:
    /// the inbox ordered by <c>row.Classification.UpdatedOn</c> reached through a constructor
    /// projection, which EF Core cannot translate. Executing the store's own search is the assertion
    /// — a query that does not translate throws here regardless of how many rows exist, which is why
    /// this needs no fixture data.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Searching_the_commercial_document_inbox_translates_to_sql()
    {
        await using var context = _database.ContextFor(BusinessUnitId);
        var store = new EfCommercialDocumentClassificationStore(context);

        var (rows, total) = await store.SearchInboxAsync(
            BusinessUnitId, new CommercialDocumentInboxQuery(1, 25, null, null, null), default);

        Assert.Empty(rows);
        Assert.Equal(0, total);

        // The projection-state filters compose onto the same query and must translate too.
        foreach (var state in Enum.GetValues<SupplierQuoteProjectionState>())
        {
            var (filtered, filteredTotal) = await store.SearchInboxAsync(
                BusinessUnitId, new CommercialDocumentInboxQuery(1, 25, null, null, state), default);
            Assert.Empty(filtered);
            Assert.Equal(0, filteredTotal);
        }
    }

    // ---------------------------------------------------------------- plumbing

    /// <summary>
    /// A DbContext configured the way <c>Program.cs</c> configures the production one — the retry
    /// policy is copied from it deliberately, because the strategy's PRESENCE is the thing under
    /// test.
    /// </summary>
    private ErpRfqAutomationContext RetryingContext()
    {
        var tenant = new StubTenant(BusinessUnitId);
        var options = new DbContextOptionsBuilder<ErpRfqAutomationContext>()
            .UseNpgsql(_database.ConnectionString, npgsql =>
            {
                npgsql.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(10),
                    errorCodesToAdd: null);
                npgsql.CommandTimeout(60);
            })
            .EnableDetailedErrors()
            .Options;
        return new ErpRfqAutomationContext(options, tenant);
    }

    private async Task SeedBusinessUnitAsync()
    {
        await CleanupAsync();
        await using var context = _database.ContextFor(null);
        context.BusinessUnits.Add(new BusinessUnit
        {
            Id = BusinessUnitId,
            BusinessUnitCode = "RETRY-STRATEGY",
            BusinessUnitName = "Retry Strategy Test Unit",
            IsActive = true,
            CreatedBy = "tests",
            CreatedOn = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Removes only this fixture's rows.
    ///
    /// <para><c>session_replication_role = 'replica'</c> for the duration, which is what
    /// <c>TenantPurgeExecutor</c> does and for the same reason: creating a supplier also writes the
    /// FR-MDM-05 audit, <c>trg_master_data_audit_append_only</c> refuses to let those rows be
    /// deleted, and <c>FK_MasterDataChangeEvents_BusinessUnits_BusinessUnitId</c> refuses to let the
    /// business unit go while they exist. Suspending both is the only way a test fixture can undo
    /// itself, and it is scoped to this one session.</para>
    /// </summary>
    private async Task CleanupAsync()
    {
        await using var context = _database.ContextFor(null);
        await context.Database.ExecuteSqlRawAsync(
            $"""
             SET session_replication_role = 'replica';
             DELETE FROM "MasterDataFieldChanges" WHERE "BusinessUnitId" = {BusinessUnitId};
             DELETE FROM "MasterDataChangeEvents" WHERE "BusinessUnitId" = {BusinessUnitId};
             DELETE FROM "Suppliers" WHERE "BUID" = {BusinessUnitId};
             DELETE FROM "BusinessUnits" WHERE "ID" = {BusinessUnitId};
             SET session_replication_role = 'origin';
             """);
    }
}
