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

    /// <summary>
    /// SQUASH NOTE — this replaces Populated_upgrade_enforces_tenant_routing_lineage_and_reupgrades.
    ///
    /// That test built a database at 20260730222700_Module02CustomerContinuity, seeded a routing
    /// decision and a work item, upgraded to 20260730234426_Module03TenantSafeSalesRouting and
    /// asserted the routing tables came out tenant-qualified and immutable, then walked back down
    /// (asserting the guard function disappeared and the rows did not) and up again.
    ///
    /// 20260811033109_SquashedSchemaBaseline erased both ids and the walk with them. Nothing the
    /// migration INSTALLED was erased, and that is all that is asserted here — now against the
    /// live catalogue and against real cross-tenant and rewrite attempts rather than against an
    /// object appearing and disappearing:
    ///
    ///   * every foreign key out of the four routing tables carries the tenant in the key, so a
    ///     row cannot reference another tenant's lead, owner or decision;
    ///   * a routing decision naming another tenant's lead is refused by the database;
    ///   * a routing decision cannot be rewritten after the fact — the audit trail of who a lead
    ///     was routed to, and why, is append-only.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Routing_lineage_is_tenant_qualified_and_decisions_are_immutable()
    {
        await using var connection = await database.OpenConnectionAsync();

        // 20 composite foreign keys across the four routing tables. Counted rather than named
        // because the point is that NONE of them is single-column: a one-column FK to "Leads"
        // would let a decision in tenant B cite a lead in tenant A and satisfy the constraint.
        await using (var lineage = connection.CreateCommand())
        {
            lineage.CommandText = """
                SELECT
                    count(*) FILTER (WHERE array_length(constraint_row.conkey, 1) = 2)::int,
                    count(*)::int
                FROM pg_constraint constraint_row
                JOIN pg_class table_row ON table_row.oid = constraint_row.conrelid
                WHERE constraint_row.contype = 'f'
                  AND table_row.relname IN ('customer_ownerships', 'lead_routing_decisions',
                                            'lead_assignments', 'unassigned_work_items');
                """;
            await using var reader = await lineage.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            var tenantQualified = reader.GetInt32(0);
            var total = reader.GetInt32(1);
            Assert.Equal(20, tenantQualified);
            Assert.Equal(total, tenantQualified);
        }

        await using var transaction = await connection.BeginTransactionAsync();
        await using (var seed = connection.CreateCommand())
        {
            seed.Transaction = transaction;
            seed.CommandText = """
                INSERT INTO "BusinessUnits"
                    ("ID", "BusinessUnitCode", "BusinessUnitName", "CreatedBy", "CreatedOn")
                VALUES (99501, 'M03-A', 'Routing tenant A', 'tests', now()),
                       (99502, 'M03-B', 'Routing tenant B', 'tests', now());
                INSERT INTO "Users"
                    ("ID", "BUID", "FirstName", "LastName", "Email", "Password_Hash",
                     "ImageURL", "IsActive", "CreatedBy", "CreatedOn")
                VALUES (99521, 99501, 'owner-a', 'Test', 'owner-a@nexora.invalid', 'not-used',
                        'n/a', true, 'tests', now()),
                       (99522, 99502, 'owner-b', 'Test', 'owner-b@nexora.invalid', 'not-used',
                        'n/a', true, 'tests', now());
                INSERT INTO "Leads"
                    ("ID", "RFQNo", "RecDate", "LeadSource", "CreatedBy", "CreatedDate", "BusinessUnitID")
                VALUES (99511, 'M03-LEAD', now(), 'Tests', 'tests', now(), 99501);
                INSERT INTO lead_routing_decisions
                    ("BusinessUnitId", "LeadId", "SuggestedUserId", "MatchStatus", "Outcome",
                     "MatchConfidence", "DecisionCode", "Explanation", "PolicyVersion",
                     "CorrelationId", "IdempotencyKey", "CreatedOn")
                VALUES (99501, 99511, 99521, 'NoEvidence', 'Unassigned', 0,
                        'ROUTING_TEST', jsonb_build_object(), 'test', 'module03', 'module03-decision', now());
                """;
            await seed.ExecuteNonQueryAsync();
        }

        // Tenant B cannot cite tenant A's lead, even from raw SQL as the owner role.
        await transaction.SaveAsync("cross_tenant");
        await using (var crossTenant = connection.CreateCommand())
        {
            crossTenant.Transaction = transaction;
            crossTenant.CommandText = """
                INSERT INTO lead_routing_decisions
                    ("BusinessUnitId", "LeadId", "SuggestedUserId", "MatchStatus", "Outcome",
                     "MatchConfidence", "DecisionCode", "Explanation", "PolicyVersion",
                     "CorrelationId", "IdempotencyKey", "CreatedOn")
                VALUES (99502, 99511, 99522, 'NoEvidence', 'Unassigned', 0,
                        'CROSS_TENANT', jsonb_build_object(), 'test', 'cross', 'cross-lead', now());
                """;
            var error = await Assert.ThrowsAsync<PostgresException>(() => crossTenant.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
        }
        await transaction.RollbackAsync("cross_tenant");

        // And a decision already taken cannot be re-explained.
        await using (var immutable = connection.CreateCommand())
        {
            immutable.Transaction = transaction;
            immutable.CommandText = """
                UPDATE lead_routing_decisions SET "DecisionCode" = 'CHANGED'
                WHERE "BusinessUnitId" = 99501 AND "IdempotencyKey" = 'module03-decision';
                """;
            var error = await Assert.ThrowsAsync<PostgresException>(() => immutable.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.RaiseException, error.SqlState);
        }

        await transaction.RollbackAsync();
    }

    /// <summary>Owner row for the tests that run against the fully migrated fixture.</summary>
    private static User User(long id, long tenant, string name) => new()
    {
        Id = id, Buid = tenant, FirstName = name, LastName = "Test", Email = $"{name}@nexora.invalid",
        PasswordHash = "not-used", ImageUrl = "n/a", IsActive = true,
        CreatedBy = "tests", CreatedOn = DateTime.UtcNow
    };

    private static CommercialRoutingApplicationService Service(ErpRfqAutomationContext context) =>
        new(context, new DeterministicRoutingEngine(), new RoutingPolicy());

}
