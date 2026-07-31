using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.CommercialIntelligence.Sales;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class Module03SalesRoutingMigrationPostgreSqlTests(PostgreSqlTestDatabase database)
{
    private const string PreviousMigration = "20260730222700_Module02CustomerContinuity";
    private const string CurrentMigration = "20260730234426_Module03TenantSafeSalesRouting";

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Concurrent_queue_assignment_returns_one_success_and_one_domain_conflict()
    {
        var suffix = Random.Shared.Next(1, 50_000);
        var tenant = 9_700_000L + suffix;
        var leadId = 9_800_000L + suffix;
        var userId = 9_900_000L + suffix;
        long workItemId;
        await using (var seed = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, tenant);
            Seed.Lead(seed, leadId, tenant, buyersName: "Concurrent routing account");
            seed.Users.Add(User(userId, tenant, $"concurrent-{suffix}"));
            seed.SalesRepProfiles.Add(new SalesRepProfile
            {
                BusinessUnitId = tenant, UserId = userId, IsRoutingEligible = true,
                CapacityPercent = 100, DistributionWeight = 1, EffectiveFromUtc = DateTime.UtcNow.AddDays(-1),
                Version = 1, UpdatedAtUtc = DateTime.UtcNow, UpdatedBy = "tests",
                LastMutationIdempotencyKey = $"module03-concurrent-profile-{suffix}"
            });
            await seed.SaveChangesAsync();
            var routed = await Service(seed).RouteLeadAsync(tenant,
                new RouteLeadCommand(leadId, $"module03-concurrent-route-{suffix}", $"route-{suffix}"), default);
            workItemId = routed.WorkItemId!.Value;
        }

        using var start = new Barrier(2);
        var first = Task.Run(() => AssignAsync("first"));
        var second = Task.Run(() => AssignAsync("second"));
        var outcomes = await Task.WhenAll(first, second);

        Assert.Single(outcomes, outcome => outcome is RoutingDecisionResponse);
        Assert.Single(outcomes, outcome => outcome is RoutingConflictException);
        await using var verify = database.ContextFor(tenant);
        Assert.Single(await verify.Set<LeadAssignment>().Where(value => value.LeadId == leadId && value.EffectiveTo == null).ToListAsync());
        Assert.Equal(WorkItemStatus.Resolved,
            (await verify.Set<UnassignedWorkItem>().SingleAsync(value => value.Id == workItemId)).Status);

        async Task<object> AssignAsync(string actor)
        {
            await using var context = database.ContextFor(tenant);
            var service = Service(context);
            start.SignalAndWait();
            try
            {
                return await service.AssignQueueItemAsync(tenant, workItemId,
                    new AssignQueueItemCommand(1, userId, userId,
                        $"module03-concurrent-{actor}-{suffix}", $"concurrent-{actor}-{suffix}"), default);
            }
            catch (Exception ex)
            {
                return ex;
            }
        }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Populated_upgrade_enforces_tenant_routing_lineage_and_reupgrades()
    {
        var databaseName = $"nexora_module03_{Guid.NewGuid():N}";
        var connection = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Database = databaseName };
        await ExecuteAdminAsync(database.ConnectionString, $"CREATE DATABASE \"{databaseName}\"");
        try
        {
            await using var context = database.ContextForConnectionString(connection.ConnectionString, null);
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(PreviousMigration);

            Seed.EnsureBusinessUnit(context, 99_501);
            Seed.EnsureBusinessUnit(context, 99_502);
            var lead = Seed.Lead(context, 99_511, 99_501, buyersName: "Routing tenant A");
            context.Users.AddRange(User(99_521, 99_501, "owner-a"), User(99_522, 99_502, "owner-b"));
            await context.SaveChangesAsync();
            var decision = new LeadRoutingDecision
            {
                BusinessUnitId = 99_501, LeadId = lead.Id, SuggestedUserId = 99_521,
                MatchStatus = CustomerMatchStatus.NoEvidence, Outcome = RoutingOutcome.Unassigned,
                MatchConfidence = 0, DecisionCode = "MIGRATION_REHEARSAL", Explanation = "{}",
                PolicyVersion = "test", CorrelationId = "module03", IdempotencyKey = "module03-decision",
                CreatedOn = DateTime.UtcNow
            };
            context.Add(decision);
            await context.SaveChangesAsync();
            context.Add(new UnassignedWorkItem
            {
                BusinessUnitId = 99_501, LeadId = lead.Id, RoutingDecisionId = decision.Id,
                ReasonCode = "NO_OWNER", RequiredAction = "Assign owner", Priority = 1,
                EnteredOn = DateTime.UtcNow, SlaDueOn = DateTime.UtcNow.AddHours(4),
                SuggestedUserId = 99_521, IdempotencyKey = "module03-work", Version = 1
            });
            await context.SaveChangesAsync();

            await migrator.MigrateAsync(CurrentMigration);

            var routingConstraintCount = await context.Database.SqlQueryRaw<int>("""
                SELECT count(*)::int AS "Value"
                FROM pg_constraint constraint_row
                JOIN pg_class table_row ON table_row.oid = constraint_row.conrelid
                WHERE constraint_row.contype = 'f'
                  AND table_row.relname IN ('customer_ownerships', 'lead_routing_decisions', 'lead_assignments', 'unassigned_work_items')
                  AND array_length(constraint_row.conkey, 1) = 2
                """).SingleAsync();
            Assert.Equal(20, routingConstraintCount);

            var crossTenantLead = await Assert.ThrowsAsync<PostgresException>(() =>
                context.Database.ExecuteSqlRawAsync("""
                    INSERT INTO lead_routing_decisions
                        ("BusinessUnitId", "LeadId", "SuggestedUserId", "MatchStatus", "Outcome",
                         "MatchConfidence", "DecisionCode", "Explanation", "PolicyVersion",
                         "CorrelationId", "IdempotencyKey", "CreatedOn")
                    VALUES (99502, 99511, 99522, 'NoEvidence', 'Unassigned', 0,
                            'CROSS_TENANT', '{{}}'::jsonb, 'test', 'cross', 'cross-lead', now())
                    """));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, crossTenantLead.SqlState);

            var immutable = await Assert.ThrowsAsync<PostgresException>(() =>
                context.Database.ExecuteSqlRawAsync("""
                    UPDATE lead_routing_decisions SET "DecisionCode" = 'CHANGED'
                    WHERE "BusinessUnitId" = 99501 AND "IdempotencyKey" = 'module03-decision'
                    """));
            Assert.Equal(PostgresErrorCodes.RaiseException, immutable.SqlState);

            await migrator.MigrateAsync(PreviousMigration);
            Assert.Equal(1, await context.Database.SqlQueryRaw<int>("""
                SELECT count(*)::int AS "Value" FROM lead_routing_decisions
                WHERE "BusinessUnitId" = 99501
                """).SingleAsync());
            Assert.Equal(0, await context.Database.SqlQueryRaw<int>("""
                SELECT count(*)::int AS "Value"
                FROM pg_proc
                WHERE proname = 'nexora_reject_routing_decision_mutation'
                """).SingleAsync());
            await migrator.MigrateAsync(CurrentMigration);
            Assert.Equal(1, await context.Database.SqlQueryRaw<int>("""
                SELECT count(*)::int AS "Value"
                FROM pg_proc
                WHERE proname = 'nexora_reject_routing_decision_mutation'
                """).SingleAsync());
            Assert.Equal(1, await context.Database.SqlQueryRaw<int>("""
                SELECT count(*)::int AS "Value" FROM unassigned_work_items
                WHERE "BusinessUnitId" = 99501
                """).SingleAsync());
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAdminAsync(database.ConnectionString, $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)");
        }
    }

    private static User User(long id, long tenant, string name) => new()
    {
        Id = id, Buid = tenant, FirstName = name, LastName = "Test", Email = $"{name}@nexora.invalid",
        PasswordHash = "not-used", ImageUrl = "n/a", IsActive = true,
        CreatedBy = "tests", CreatedOn = DateTime.UtcNow
    };

    private static CommercialRoutingApplicationService Service(ErpRfqAutomationContext context) =>
        new(context, new DeterministicRoutingEngine(), new RoutingPolicy());

    private static async Task ExecuteAdminAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
