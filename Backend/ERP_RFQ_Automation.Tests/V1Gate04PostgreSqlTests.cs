using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.CommercialLearning;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class V1Gate04PostgreSqlTests(PostgreSqlTestDatabase database)
{
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Learning_governance_is_tenant_bound_append_only_and_least_privilege()
    {
        const long tenantA = 48_401;
        const long tenantB = 48_402;
        await using (var context = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(context, tenantA);
            Seed.EnsureBusinessUnit(context, tenantB);
            context.LearningGovernanceEvents.AddRange(
                LearningEvent(tenantA, 'a', "gate4-learning-a"),
                LearningEvent(tenantB, 'b', "gate4-learning-b"));
            await context.SaveChangesAsync();
        }

        await using var connection = await database.OpenConnectionAsync();
        await using (var update = connection.CreateCommand())
        {
            update.CommandText = "UPDATE public.learning_governance_events SET \"Reason\" = 'rewrite' WHERE \"BusinessUnitId\" = 48401";
            Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(
                () => update.ExecuteNonQueryAsync())).SqlState);
        }
        await using (var delete = connection.CreateCommand())
        {
            delete.CommandText = "DELETE FROM public.learning_governance_events WHERE \"BusinessUnitId\" = 48401";
            Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(
                () => delete.ExecuteNonQueryAsync())).SqlState);
        }

        await using var transaction = await connection.BeginTransactionAsync();
        await using (var scope = connection.CreateCommand())
        {
            scope.Transaction = transaction;
            scope.CommandText = "SET LOCAL ROLE nexora_tenant_app; SELECT set_config('nexora.business_unit_id', '48401', true);";
            await scope.ExecuteNonQueryAsync();
        }
        await using (var visible = connection.CreateCommand())
        {
            visible.Transaction = transaction;
            visible.CommandText = "SELECT count(*) FROM public.learning_governance_events";
            Assert.Equal(1L, (long)(await visible.ExecuteScalarAsync())!);
        }
        await using (var privileges = connection.CreateCommand())
        {
            privileges.Transaction = transaction;
            privileges.CommandText = """
                SELECT has_table_privilege('nexora_tenant_app', 'public.learning_governance_events', 'SELECT,INSERT')
                   AND NOT has_table_privilege('nexora_tenant_app', 'public.learning_governance_events', 'UPDATE,DELETE,TRUNCATE')
                """;
            Assert.True((bool)(await privileges.ExecuteScalarAsync())!);
        }

        await using (var crossTenantInsert = connection.CreateCommand())
        {
            crossTenantInsert.Transaction = transaction;
            crossTenantInsert.CommandText = """
                INSERT INTO public.learning_governance_events
                    ("BusinessUnitId", "SignalId", "Version", "Action", "Reason", "ActorUserId",
                     "IdempotencyKey", "EvidenceReference", "SnapshotJson", "OccurredOn")
                VALUES
                    (48402, repeat('c', 64), 1, 'APPROVED', 'cross tenant', 1,
                     'gate4-cross-tenant', 'test', '{}', now())
                """;
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(
                () => crossTenantInsert.ExecuteNonQueryAsync())).SqlState);
        }
        await transaction.RollbackAsync();
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Learning_rollback_must_compensate_immediately_preceding_version()
    {
        const long tenant = 48_404;
        await using (var context = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(context, tenant);
            context.LearningGovernanceEvents.AddRange(
                LearningEvent(tenant, 'd', "gate4-learning-v1"),
                LearningEvent(tenant, 'd', "gate4-learning-v2", 2,
                    LearningGovernanceActions.Disabled));
            await context.SaveChangesAsync();
        }

        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO public.learning_governance_events
                ("BusinessUnitId", "SignalId", "Version", "Action", "Reason", "ActorUserId",
                 "IdempotencyKey", "EvidenceReference", "SnapshotJson", "OccurredOn", "RevertsVersion")
            VALUES
                (48404, repeat('d', 64), 3, 'ROLLED_BACK', 'invalid rollback', 1,
                 'gate4-invalid-rollback', 'test', '{}', now(), 1)
            """;
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(
            () => command.ExecuteNonQueryAsync())).SqlState);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Ai_request_linkage_budget_and_inflight_cost_are_immutable()
    {
        const long tenant = 48_403;
        var requestId = Guid.NewGuid();
        await using (var context = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(context, tenant);
            context.AiRequests.Add(new AiRequest
            {
                Id = requestId,
                BusinessUnitId = tenant,
                Operation = AiPurposes.RfqExtraction,
                IdempotencyKey = $"gate4-ai-{requestId:N}",
                PromptHash = new string('A', 64),
                PromptVersion = "gate4/v1",
                Provider = "OllamaLocal",
                ProviderClass = AiProviderClass.Local,
                Model = "test",
                Status = AiCallStatuses.Reserved,
                InputHash = new string('B', 64),
                EstimatedInputTokens = 10,
                ReservedTokens = 20,
                BudgetWarning = true,
                CostStatus = "LocalUnpriced",
                TokenSource = AiTokenSources.Estimated,
                CreatedOn = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }

        await using var connection = await database.OpenConnectionAsync();
        foreach (var mutation in new[]
        {
            "\"ProviderClass\" = 'External'",
            "\"ExtractionJobId\" = 999",
            "\"SourceDocumentOccurrenceId\" = 999",
            "\"BudgetWarning\" = false",
            "\"EstimatedCost\" = 1, \"CostCurrency\" = 'USD', \"CostStatus\" = 'Priced'"
        })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"UPDATE public.\"AiRequests\" SET {mutation} WHERE \"Id\" = @id";
            command.Parameters.AddWithValue("id", requestId);
            Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(
                () => command.ExecuteNonQueryAsync())).SqlState);
        }
    }

    private static LearningGovernanceEvent LearningEvent(
        long tenant,
        char signal,
        string key,
        long version = 1,
        string action = LearningGovernanceActions.Approved) => new()
    {
        BusinessUnitId = tenant,
        SignalId = new string(signal, 64),
        Version = version,
        Action = action,
        Reason = "Authorized PostgreSQL tenant-isolation evidence",
        ActorUserId = 1,
        IdempotencyKey = key,
        EvidenceReference = "test:authorized",
        SnapshotJson = "{}",
        OccurredOn = DateTime.UtcNow
    };
}
