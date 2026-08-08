using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Services;
using ERP_RFQ_Automation.Platform.Controllers;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class PlatformOwnerInvariantPostgreSqlTests(PostgreSqlTestDatabase database)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "PostgreSQL")]
    public async Task Concurrent_owner_removals_commit_exactly_one_and_preserve_an_active_Owner(
        bool deactivate)
    {
        var suffix = Guid.NewGuid().ToString("N");
        long firstId;
        long secondId;
        await using (var seed = database.ContextFor(null))
        {
            var first = PlatformIamInvariantTests.NewOwner($"first-{suffix}@example.test");
            var second = PlatformIamInvariantTests.NewOwner($"second-{suffix}@example.test");
            seed.Set<PlatformUser>().AddRange(first, second);
            await seed.SaveChangesAsync();
            firstId = first.Id;
            secondId = second.Id;
        }

        await using var blocker = await database.OpenConnectionAsync();
        await using var blockerTx = await blocker.BeginTransactionAsync();
        var blockerCommitted = false;
        await using (var command = new NpgsqlCommand(
                         $"SELECT pg_advisory_xact_lock({PlatformUsersController.OwnerMutationLockNamespace}, " +
                         $"{PlatformUsersController.OwnerMutationLockKey});", blocker, blockerTx))
            await command.ExecuteNonQueryAsync();

        try
        {
            await using var firstContext = ProductionContext("owner-invariant-first");
            await using var secondContext = ProductionContext("owner-invariant-second");
            var firstController = Controller(firstContext, firstId);
            var secondController = Controller(secondContext, secondId);

            var firstMutation = deactivate
                ? firstController.Deactivate(secondId, CancellationToken.None)
                : firstController.ChangeRole(firstId,
                    new ChangePlatformUserRoleRequest { Role = nameof(PlatformRole.SupportAdmin) },
                    CancellationToken.None);
            var secondMutation = deactivate
                ? secondController.Deactivate(firstId, CancellationToken.None)
                : secondController.ChangeRole(secondId,
                    new ChangePlatformUserRoleRequest { Role = nameof(PlatformRole.SupportAdmin) },
                    CancellationToken.None);

            await WaitForTwoAdvisoryWaitersAsync();
            await blockerTx.CommitAsync();
            blockerCommitted = true;

            var results = await Task.WhenAll(firstMutation, secondMutation);
            Assert.Single(results, result => result.Result is OkObjectResult);
            Assert.Single(results, result => result.Result is ConflictObjectResult);

            await using var verification = database.ContextFor(null);
            Assert.Equal(1, await verification.Set<PlatformUser>().AsNoTracking()
                .CountAsync(user => (user.Id == firstId || user.Id == secondId)
                                    && user.IsActive && user.PlatformRole == PlatformRole.Owner));
        }
        finally
        {
            if (!blockerCommitted)
                await blockerTx.RollbackAsync();
            await CleanupAsync(firstId, secondId);
        }
    }

    private ErpRfqAutomationContext ProductionContext(string applicationName)
    {
        var connection = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            ApplicationName = applicationName
        };
        var options = new DbContextOptionsBuilder<ErpRfqAutomationContext>()
            .UseNpgsql(connection.ConnectionString, npgsql => npgsql.EnableRetryOnFailure())
            .Options;
        return new ErpRfqAutomationContext(options, new StubTenant(null));
    }

    private static ERP_RFQ_Automation.Platform.Controllers.PlatformUsersController Controller(
        ErpRfqAutomationContext context, long actorId) => new(
        context, new PlatformAuditService(context, NullLogger<PlatformAuditService>.Instance))
    {
        ControllerContext = PlatformIamInvariantTests.Controller(context, actorId).ControllerContext
    };

    private async Task WaitForTwoAdvisoryWaitersAsync()
    {
        await using var observer = await database.OpenConnectionAsync();
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await using var command = new NpgsqlCommand("""
                SELECT count(*)::int
                FROM pg_locks
                WHERE locktype = 'advisory'
                  AND classid::bigint = @lockNamespace
                  AND objid::bigint = @lockKey
                  AND NOT granted;
                """, observer);
            command.Parameters.AddWithValue("lockNamespace", PlatformUsersController.OwnerMutationLockNamespace);
            command.Parameters.AddWithValue("lockKey", PlatformUsersController.OwnerMutationLockKey);
            if ((int)(await command.ExecuteScalarAsync())! == 2)
                return;
            await Task.Delay(25);
        }

        throw new TimeoutException("Both Owner mutations did not reach the advisory-lock boundary.");
    }

    private async Task CleanupAsync(long firstId, long secondId)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var cleanup = new NpgsqlCommand(
                         """
                         DELETE FROM platform."PlatformSessions"
                         WHERE "PlatformUserId" = @first OR "PlatformUserId" = @second;
                         UPDATE platform."PlatformUsers"
                         SET "IsActive" = FALSE,
                             "PlatformRole" = 1,
                             "SessionGeneration" = "SessionGeneration" + 1
                         WHERE "Id" = @first OR "Id" = @second;
                         """, connection, transaction))
        {
            cleanup.Parameters.AddWithValue("first", firstId);
            cleanup.Parameters.AddWithValue("second", secondId);
            await cleanup.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }
}
