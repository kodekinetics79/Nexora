using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ERP_RFQ_Automation.Tests.PostgreSQL.OpportunityPriority;

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class OpportunityPriorityPostgreSqlSecurityTests(PostgreSqlTestDatabase database)
{
    private static readonly string[] Tables =
    [
        "commercial_opportunity_recommendations",
        "commercial_opportunity_outcomes",
        "commercial_opportunity_feedback",
        "commercial_opportunity_events",
        "commercial_opportunity_outbox",
        "commercial_opportunity_operations"
    ];

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Latest_schema_forces_tenant_RLS_and_installs_integrity_guards()
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT count(*) FROM "__EFMigrationsHistory"
                 WHERE "MigrationId" = '20260729031740_V2Gate02OpportunityPriorityShadow') = 1,
                (SELECT count(*) FROM pg_policies
                 WHERE schemaname = 'public'
                   AND policyname = 'nexora_tenant_isolation'
                   AND tablename = ANY(ARRAY[
                     'commercial_opportunity_recommendations',
                     'commercial_opportunity_outcomes',
                     'commercial_opportunity_feedback',
                     'commercial_opportunity_events',
                     'commercial_opportunity_outbox',
                     'commercial_opportunity_operations'])) = 6,
                (SELECT count(*) FROM pg_class
                 WHERE relname = ANY(ARRAY[
                   'commercial_opportunity_recommendations',
                   'commercial_opportunity_outcomes',
                   'commercial_opportunity_feedback',
                   'commercial_opportunity_events',
                   'commercial_opportunity_outbox',
                   'commercial_opportunity_operations'])
                   AND relrowsecurity AND relforcerowsecurity) = 6,
                (SELECT count(*) FROM pg_trigger
                 WHERE NOT tgisinternal AND tgname = ANY(ARRAY[
                   'trg_opportunity_recommendations_append_only',
                   'trg_opportunity_outcomes_append_only',
                   'trg_opportunity_feedback_append_only',
                   'trg_opportunity_events_append_only',
                   'trg_opportunity_operations_append_only',
                   'trg_guard_opportunity_outbox',
                   'trg_validate_opportunity_recommendation_lineage',
                   'trg_validate_opportunity_outcome',
                   'trg_validate_opportunity_feedback',
                   'trg_require_opportunity_recommendation_event',
                   'trg_require_opportunity_feedback_event',
                   'trg_require_opportunity_outcome_event',
                   'trg_require_opportunity_outbox'])) = 13,
                has_table_privilege('nexora_tenant_app', 'public.commercial_opportunity_recommendations', 'SELECT,INSERT')
                  AND NOT has_table_privilege('nexora_tenant_app', 'public.commercial_opportunity_recommendations', 'UPDATE,DELETE'),
                has_table_privilege('nexora_tenant_app', 'public.commercial_opportunity_outcomes', 'SELECT,INSERT')
                  AND NOT has_table_privilege('nexora_tenant_app', 'public.commercial_opportunity_outcomes', 'UPDATE,DELETE'),
                has_table_privilege('nexora_tenant_app', 'public.commercial_opportunity_feedback', 'SELECT,INSERT')
                  AND NOT has_table_privilege('nexora_tenant_app', 'public.commercial_opportunity_feedback', 'UPDATE,DELETE'),
                has_table_privilege('nexora_tenant_app', 'public.commercial_opportunity_events', 'SELECT,INSERT')
                  AND NOT has_table_privilege('nexora_tenant_app', 'public.commercial_opportunity_events', 'UPDATE,DELETE'),
                has_table_privilege('nexora_tenant_app', 'public.commercial_opportunity_outbox', 'SELECT,INSERT,UPDATE')
                  AND NOT has_table_privilege('nexora_tenant_app', 'public.commercial_opportunity_outbox', 'DELETE'),
                has_table_privilege('nexora_tenant_app', 'public.commercial_opportunity_operations', 'SELECT,INSERT')
                  AND NOT has_table_privilege('nexora_tenant_app', 'public.commercial_opportunity_operations', 'UPDATE,DELETE');
            """;

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        for (var index = 0; index < 10; index++)
            Assert.True(reader.GetBoolean(index), $"Gate 2 schema security assertion {index + 1} failed.");
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Recommendation_lineage_rejects_mismatched_lead_identity_and_cross_case_supersession()
    {
        var tenantA = new OpportunityFixture(841_001, 841_011, 841_101, 'a');
        var tenantB = new OpportunityFixture(841_002, 841_012, 841_201, 'b');
        await SeedGraphAsync(tenantA, tenantB);

        await AssertInsertRejectedAsync($$"""
            INSERT INTO commercial_opportunity_recommendations
              ("Id","BusinessUnitId","CommercialCaseId","NexoraSerial","LeadId","LeadVersion",
               "RecommendationKey","PolicyVersion","FeatureSchemaVersion","EvidenceCutoffAtUtc",
               "EvidenceSnapshotJson","EvidenceHash","PriorityScore","PriorityBand","Confidence",
               "Completeness","SampleSize","RecommendedActionCode","RecommendedActionLabel",
               "RationaleJson","CohortKey","Mode","GeneratedAtUtc")
            VALUES ({{tenantA.BaseId + 60}}, {{tenantA.TenantId}}, {{tenantB.CommercialCaseId}}, '{{tenantB.NexoraSerial}}',
                    {{tenantA.LeadId}}, 1, 'mismatched-lineage', 'test-policy', 'test-schema', now(), '{}'::jsonb,
                    repeat('6', 64), 50, 'Medium', 0.5, 0.5, 1, 'REVIEW', 'Review opportunity',
                    '[]'::jsonb, 'eligible-shadow', 'Shadow', now());
            """, "opportunity recommendation must retain tenant-qualified lead and Nexora Serial lineage");

        await AssertInsertRejectedAsync($$"""
            INSERT INTO commercial_opportunity_recommendations
              ("Id","BusinessUnitId","CommercialCaseId","NexoraSerial","LeadId","LeadVersion",
               "SupersedesRecommendationId","RecommendationKey","PolicyVersion","FeatureSchemaVersion",
               "EvidenceCutoffAtUtc","EvidenceSnapshotJson","EvidenceHash","PriorityScore","PriorityBand",
               "Confidence","Completeness","SampleSize","RecommendedActionCode","RecommendedActionLabel",
               "RationaleJson","CohortKey","Mode","GeneratedAtUtc")
            VALUES ({{tenantA.BaseId + 61}}, {{tenantA.TenantId}}, {{tenantA.CommercialCaseId}}, '{{tenantA.NexoraSerial}}',
                    {{tenantA.LeadId}}, 2, {{tenantB.RecommendationId}}, 'cross-case-supersession', 'test-policy',
                    'test-schema', now(), '{}'::jsonb, repeat('7', 64), 50, 'Medium', 0.5, 0.5, 1,
                    'REVIEW', 'Review opportunity', '[]'::jsonb, 'eligible-shadow', 'Shadow', now());
            """, "superseded recommendation must retain the same commercial identity");
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Tenant_runtime_role_sees_only_its_rows_and_denies_cross_tenant_writes()
    {
        var tenantA = new OpportunityFixture(842_001, 842_011, 842_101, 'a');
        var tenantB = new OpportunityFixture(842_002, 842_012, 842_201, 'b');
        await SeedGraphAsync(tenantA, tenantB);

        await using var connection = await database.OpenConnectionAsync();
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await ExecuteAsync(connection, transaction, $$"""
                SET LOCAL ROLE nexora_tenant_app;
                SELECT set_config('nexora.business_unit_id', '{{tenantA.TenantId}}', true);
                """);

            await using var visible = connection.CreateCommand();
            visible.Transaction = transaction;
            visible.CommandText = $"SELECT {string.Join(", ", Tables.Select(table => $"(SELECT count(*) FROM {table})"))}";
            await using var reader = await visible.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            long[] expectedCounts = [1, 1, 1, 3, 3, 1];
            for (var index = 0; index < Tables.Length; index++)
                Assert.Equal(expectedCounts[index], reader.GetInt64(index));
            await reader.DisposeAsync();
            await transaction.RollbackAsync();
        }

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await ExecuteAsync(connection, transaction, "SET LOCAL ROLE nexora_tenant_app");
            await using var hidden = connection.CreateCommand();
            hidden.Transaction = transaction;
            hidden.CommandText = $"SELECT {string.Join(", ", Tables.Select(table => $"(SELECT count(*) FROM {table})"))}";
            await using var reader = await hidden.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            for (var index = 0; index < Tables.Length; index++)
                Assert.Equal(0L, reader.GetInt64(index));
            await reader.DisposeAsync();
            await transaction.RollbackAsync();
        }

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await ExecuteAsync(connection, transaction, $$"""
                SET LOCAL ROLE nexora_tenant_app;
                SELECT set_config('nexora.business_unit_id', '{{tenantA.TenantId}}', true);
                """);
            await using var crossTenantWrite = connection.CreateCommand();
            crossTenantWrite.Transaction = transaction;
            crossTenantWrite.CommandText = $$"""
                INSERT INTO commercial_opportunity_operations
                  ("BusinessUnitId","OperationType","IdempotencyKey","RequestHash",
                   "CorrelationId","ActorId","ResultJson","OccurredAtUtc")
                VALUES ({{tenantB.TenantId}}, 'Reconcile', 'cross-tenant-denied', repeat('f', 64),
                        'cross-tenant-denied', 'security-test', '{}'::jsonb, now());
                """;
            var error = await Assert.ThrowsAsync<PostgresException>(
                () => crossTenantWrite.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
            await transaction.RollbackAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Opportunity_records_are_append_only_while_outbox_delivery_state_remains_mutable()
    {
        var fixture = new OpportunityFixture(843_001, 843_011, 843_101, 'c');
        await SeedGraphAsync(fixture);

        await using var connection = await database.OpenConnectionAsync();
        await AssertImmutableAsync(connection,
            $"UPDATE commercial_opportunity_recommendations SET \"PriorityScore\" = 1 WHERE \"Id\" = {fixture.RecommendationId}");
        await AssertImmutableAsync(connection,
            $"UPDATE commercial_opportunity_outcomes SET \"OutcomeCode\" = 'QUOTE_LOST' WHERE \"Id\" = {fixture.OutcomeId}");
        await AssertImmutableAsync(connection,
            $"UPDATE commercial_opportunity_feedback SET \"Reason\" = 'rewritten' WHERE \"Id\" = {fixture.FeedbackId}");
        await AssertImmutableAsync(connection,
            $"UPDATE commercial_opportunity_events SET \"ActorId\" = 'rewritten' WHERE \"Id\" = {fixture.RecommendationEventId}");
        await AssertImmutableAsync(connection,
            $"UPDATE commercial_opportunity_operations SET \"ActorId\" = 'rewritten' WHERE \"Id\" = {fixture.OperationId}");
        await AssertImmutableAsync(connection,
            $"DELETE FROM commercial_opportunity_outbox WHERE \"Id\" = {fixture.RecommendationOutboxId}");
        await AssertImmutableAsync(connection,
            $"UPDATE commercial_opportunity_outbox SET \"PayloadJson\" = '{{\"rewritten\":true}}'::jsonb WHERE \"Id\" = {fixture.RecommendationOutboxId}");

        await using var deliveryUpdate = connection.CreateCommand();
        deliveryUpdate.CommandText = $$"""
            UPDATE commercial_opportunity_outbox
            SET "ProcessedAtUtc" = now(), "AttemptCount" = "AttemptCount" + 1, "LastError" = NULL
            WHERE "Id" = {{fixture.RecommendationOutboxId}};
            """;
        Assert.Equal(1, await deliveryUpdate.ExecuteNonQueryAsync());
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Audited_feedback_insert_is_accepted_by_the_shared_linkage_trigger()
    {
        var fixture = new OpportunityFixture(843_501, 843_511, 843_601, 'e');
        await SeedGraphAsync(fixture);

        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction, $$"""
            INSERT INTO commercial_opportunity_feedback
              ("Id","BusinessUnitId","OpportunityRecommendationId","Decision","Reason","ActorId",
               "OccurredAtUtc","IdempotencyKey","CorrelationId")
            VALUES ({{fixture.BaseId + 50}}, {{fixture.TenantId}}, {{fixture.RecommendationId}}, 'Accepted',
                    'Second audited feedback entry', 'security-test', now(),
                    'direct-feedback-regression', 'direct-feedback-regression');
            INSERT INTO commercial_opportunity_events
              ("Id","BusinessUnitId","OpportunityRecommendationId","EventType","SourceType","SourceId",
               "ActorId","OccurredAtUtc","CorrelationId","IdempotencyKey","RequestHash","PayloadJson")
            VALUES ({{fixture.BaseId + 51}}, {{fixture.TenantId}}, {{fixture.RecommendationId}},
                    'OpportunityRecommendation.FeedbackRecorded', 'Feedback', {{fixture.BaseId + 50}},
                    'security-test', now(), 'direct-feedback-regression', 'direct-feedback-event-regression',
                    repeat('6', 64), '{}'::jsonb);
            INSERT INTO commercial_opportunity_outbox
              ("Id","BusinessUnitId","OpportunityEventId","EventType","PayloadJson",
               "OccurredAtUtc","AvailableAtUtc","AttemptCount")
            VALUES ({{fixture.BaseId + 52}}, {{fixture.TenantId}}, {{fixture.BaseId + 51}},
                    'OpportunityRecommendation.FeedbackRecorded', '{}'::jsonb, now(), now(), 0);
            """);
        await transaction.CommitAsync();

        await AssertCommitRejectedAsync($$"""
            INSERT INTO commercial_opportunity_feedback
              ("Id","BusinessUnitId","OpportunityRecommendationId","Decision","Reason","ActorId",
               "OccurredAtUtc","IdempotencyKey","CorrelationId")
            VALUES ({{fixture.BaseId + 54}}, {{fixture.TenantId}}, {{fixture.RecommendationId}}, 'Rejected',
                    'Unaudited feedback', 'security-test', now(),
                    'unaudited-feedback-regression', 'unaudited-feedback-regression');
            """, "commercial opportunity record requires a matching append-only event");
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Audited_outcome_insert_is_accepted_by_the_shared_linkage_trigger()
    {
        var fixture = new OpportunityFixture(843_701, 843_711, 843_801, 'f');
        await SeedGraphAsync(fixture);

        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction, $$"""
            INSERT INTO commercial_opportunity_outcomes
              ("Id","BusinessUnitId","OpportunityRecommendationId","OutcomeCode","ObservedAtUtc",
               "SourceType","SourceId","SourceVersion","EvidenceJson","EvidenceHash","CorrelationId")
            VALUES ({{fixture.BaseId + 50}}, {{fixture.TenantId}}, {{fixture.RecommendationId}}, 'QUOTE_LOST',
                    now() + interval '1 minute', 'Quote', {{fixture.BaseId + 53}}, 1, '{}'::jsonb,
                    repeat('5', 64), 'direct-outcome-regression');
            INSERT INTO commercial_opportunity_events
              ("Id","BusinessUnitId","OpportunityRecommendationId","EventType","SourceType","SourceId",
               "ActorId","OccurredAtUtc","CorrelationId","IdempotencyKey","RequestHash","PayloadJson")
            VALUES ({{fixture.BaseId + 51}}, {{fixture.TenantId}}, {{fixture.RecommendationId}},
                    'OpportunityRecommendation.OutcomeObserved', 'Outcome', {{fixture.BaseId + 50}},
                    'security-test', now(), 'direct-outcome-regression', 'direct-outcome-event-regression',
                    repeat('5', 64), '{}'::jsonb);
            INSERT INTO commercial_opportunity_outbox
              ("Id","BusinessUnitId","OpportunityEventId","EventType","PayloadJson",
               "OccurredAtUtc","AvailableAtUtc","AttemptCount")
            VALUES ({{fixture.BaseId + 52}}, {{fixture.TenantId}}, {{fixture.BaseId + 51}},
                    'OpportunityRecommendation.OutcomeObserved', '{}'::jsonb, now(), now(), 0);
            """);
        await transaction.CommitAsync();

        await AssertCommitRejectedAsync($$"""
            INSERT INTO commercial_opportunity_outcomes
              ("Id","BusinessUnitId","OpportunityRecommendationId","OutcomeCode","ObservedAtUtc",
               "SourceType","SourceId","SourceVersion","EvidenceJson","EvidenceHash","CorrelationId")
            VALUES ({{fixture.BaseId + 54}}, {{fixture.TenantId}}, {{fixture.RecommendationId}}, 'QUOTE_EXPIRED',
                    now() + interval '2 minutes', 'Quote', {{fixture.BaseId + 55}}, 1, '{}'::jsonb,
                    repeat('4', 64), 'unaudited-outcome-regression');
            """, "commercial opportunity record requires a matching append-only event");
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Deferred_constraints_reject_unaudited_recommendation_and_event_without_matching_outbox()
    {
        var fixture = new OpportunityFixture(844_001, 844_011, 844_101, 'd');
        await SeedGraphAsync(fixture);

        await AssertCommitRejectedAsync($$"""
            INSERT INTO commercial_opportunity_recommendations
              ("Id","BusinessUnitId","CommercialCaseId","NexoraSerial","LeadId","LeadVersion",
               "RecommendationKey","PolicyVersion","FeatureSchemaVersion","EvidenceCutoffAtUtc",
               "EvidenceSnapshotJson","EvidenceHash","PriorityScore","PriorityBand","Confidence",
               "Completeness","SampleSize","RecommendedActionCode","RecommendedActionLabel",
               "RationaleJson","CohortKey","Mode","GeneratedAtUtc")
            VALUES ({{fixture.BaseId + 91}}, {{fixture.TenantId}}, {{fixture.CommercialCaseId}}, '{{fixture.NexoraSerial}}',
                    {{fixture.LeadId}}, 1, 'unaudited-recommendation', 'test-policy-unaudited', 'test-schema',
                    now() - interval '1 minute', '{}'::jsonb, repeat('e', 64), 50, 'Medium', 0.5, 0.5, 1,
                    'REVIEW', 'Review opportunity', '[]'::jsonb, 'eligible-shadow', 'Shadow', now());
            """, "commercial opportunity record requires a matching append-only event");

        await AssertCommitRejectedAsync($$"""
            INSERT INTO commercial_opportunity_events
              ("Id","BusinessUnitId","OpportunityRecommendationId","EventType","SourceType","SourceId",
               "ActorId","OccurredAtUtc","CorrelationId","IdempotencyKey","RequestHash","PayloadJson")
            VALUES ({{fixture.BaseId + 95}}, {{fixture.TenantId}}, {{fixture.RecommendationId}},
                    'OpportunityRecommendation.Generated', 'Recommendation', {{fixture.RecommendationId}},
                    'security-test', now(), 'event-without-outbox', 'event-without-outbox', repeat('8', 64), '{}'::jsonb);
            """, "commercial opportunity event requires a matching outbox message");
    }

    private async Task SeedGraphAsync(params OpportunityFixture[] fixtures)
    {
        await using (var context = database.ContextFor(null))
        {
            foreach (var fixture in fixtures)
                Seed.Lead(context, fixture.LeadId, fixture.TenantId);
            await context.SaveChangesAsync();

            foreach (var fixture in fixtures)
            {
                var identity = await context.Leads.IgnoreQueryFilters().AsNoTracking()
                    .Where(x => x.Id == fixture.LeadId)
                    .Select(x => new { x.CommercialCaseId, x.CommercialCaseReference })
                    .SingleAsync();
                fixture.SetCommercialIdentity(identity.CommercialCaseId, identity.CommercialCaseReference);
            }
        }

        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        foreach (var fixture in fixtures)
            await InsertOpportunityGraphAsync(connection, transaction, fixture);
        await transaction.CommitAsync();
    }

    private static async Task InsertOpportunityGraphAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OpportunityFixture fixture)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $$"""
            INSERT INTO commercial_opportunity_recommendations
              ("Id","BusinessUnitId","CommercialCaseId","NexoraSerial","LeadId","LeadVersion",
               "RecommendationKey","PolicyVersion","FeatureSchemaVersion","EvidenceCutoffAtUtc",
               "EvidenceSnapshotJson","EvidenceHash","PriorityScore","PriorityBand","Confidence",
               "Completeness","SampleSize","RecommendedActionCode","RecommendedActionLabel",
               "RationaleJson","CohortKey","Mode","GeneratedAtUtc")
            VALUES ({{fixture.RecommendationId}}, {{fixture.TenantId}}, {{fixture.CommercialCaseId}}, @serial,
                    {{fixture.LeadId}}, 1, @recommendationKey, 'test-policy', 'test-schema',
                    now() - interval '2 minutes', '{}'::jsonb, @evidenceHash, 80, 'High', 0.8, 0.9, 3,
                    'FOLLOW_UP', 'Follow up now', '["deterministic evidence"]'::jsonb,
                    'eligible-shadow', 'Shadow', now() - interval '1 minute');

            INSERT INTO commercial_opportunity_feedback
              ("Id","BusinessUnitId","OpportunityRecommendationId","Decision","Reason","ActorId",
               "OccurredAtUtc","IdempotencyKey","CorrelationId")
            VALUES ({{fixture.FeedbackId}}, {{fixture.TenantId}}, {{fixture.RecommendationId}}, 'Rejected',
                    'Representative reviewer correction', 'security-test', now(), @feedbackKey, @feedbackKey);

            INSERT INTO commercial_opportunity_outcomes
              ("Id","BusinessUnitId","OpportunityRecommendationId","OutcomeCode","ObservedAtUtc",
               "SourceType","SourceId","SourceVersion","EvidenceJson","EvidenceHash","CorrelationId")
            VALUES ({{fixture.OutcomeId}}, {{fixture.TenantId}}, {{fixture.RecommendationId}}, 'QUOTE_WON',
                    now(), 'Quote', {{fixture.BaseId + 80}}, 1, '{}'::jsonb, @outcomeHash, @outcomeKey);

            INSERT INTO commercial_opportunity_operations
              ("Id","BusinessUnitId","OperationType","CommercialCaseId","OpportunityRecommendationId",
               "IdempotencyKey","RequestHash","CorrelationId","ActorId","ResultJson","OccurredAtUtc")
            VALUES ({{fixture.OperationId}}, {{fixture.TenantId}}, 'Feedback', {{fixture.CommercialCaseId}},
                    {{fixture.RecommendationId}}, @operationKey, @requestHash, @operationKey,
                    'security-test', '{}'::jsonb, now());

            INSERT INTO commercial_opportunity_events
              ("Id","BusinessUnitId","OpportunityRecommendationId","EventType","SourceType","SourceId",
               "ActorId","OccurredAtUtc","CorrelationId","IdempotencyKey","RequestHash","PayloadJson") VALUES
              ({{fixture.RecommendationEventId}}, {{fixture.TenantId}}, {{fixture.RecommendationId}},
               'OpportunityRecommendation.Generated', 'Recommendation', {{fixture.RecommendationId}},
               'security-test', now(), @recommendationEventKey, @recommendationEventKey, @requestHash, '{}'::jsonb),
              ({{fixture.FeedbackEventId}}, {{fixture.TenantId}}, {{fixture.RecommendationId}},
               'OpportunityRecommendation.FeedbackRecorded', 'Feedback', {{fixture.FeedbackId}},
               'security-test', now(), @feedbackEventKey, @feedbackEventKey, @requestHash, '{}'::jsonb),
              ({{fixture.OutcomeEventId}}, {{fixture.TenantId}}, {{fixture.RecommendationId}},
               'OpportunityRecommendation.OutcomeObserved', 'Outcome', {{fixture.OutcomeId}},
               'security-test', now(), @outcomeEventKey, @outcomeEventKey, @requestHash, '{}'::jsonb);

            INSERT INTO commercial_opportunity_outbox
              ("Id","BusinessUnitId","OpportunityEventId","EventType","PayloadJson",
               "OccurredAtUtc","AvailableAtUtc","AttemptCount") VALUES
              ({{fixture.RecommendationOutboxId}}, {{fixture.TenantId}}, {{fixture.RecommendationEventId}},
               'OpportunityRecommendation.Generated', '{}'::jsonb, now(), now(), 0),
              ({{fixture.FeedbackOutboxId}}, {{fixture.TenantId}}, {{fixture.FeedbackEventId}},
               'OpportunityRecommendation.FeedbackRecorded', '{}'::jsonb, now(), now(), 0),
              ({{fixture.OutcomeOutboxId}}, {{fixture.TenantId}}, {{fixture.OutcomeEventId}},
               'OpportunityRecommendation.OutcomeObserved', '{}'::jsonb, now(), now(), 0);
            """;
        command.Parameters.AddWithValue("serial", fixture.NexoraSerial);
        command.Parameters.AddWithValue("recommendationKey", $"recommendation-{fixture.BaseId}");
        command.Parameters.AddWithValue("evidenceHash", new string(fixture.HashCharacter, 64));
        command.Parameters.AddWithValue("outcomeHash", new string(char.ToUpperInvariant(fixture.HashCharacter), 64));
        command.Parameters.AddWithValue("requestHash", new string('7', 64));
        command.Parameters.AddWithValue("feedbackKey", $"feedback-{fixture.BaseId}");
        command.Parameters.AddWithValue("outcomeKey", $"outcome-{fixture.BaseId}");
        command.Parameters.AddWithValue("operationKey", $"operation-{fixture.BaseId}");
        command.Parameters.AddWithValue("recommendationEventKey", $"recommendation-event-{fixture.BaseId}");
        command.Parameters.AddWithValue("feedbackEventKey", $"feedback-event-{fixture.BaseId}");
        command.Parameters.AddWithValue("outcomeEventKey", $"outcome-event-{fixture.BaseId}");
        await command.ExecuteNonQueryAsync();
    }

    private async Task AssertCommitRejectedAsync(string sql, string expectedMessage)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction, sql);
        var error = await Assert.ThrowsAsync<PostgresException>(() => transaction.CommitAsync());
        Assert.Equal(PostgresErrorCodes.RaiseException, error.SqlState);
        Assert.Contains(expectedMessage, error.MessageText, StringComparison.Ordinal);
    }

    private async Task AssertInsertRejectedAsync(string sql, string expectedMessage)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.RaiseException, error.SqlState);
        Assert.Contains(expectedMessage, error.MessageText, StringComparison.Ordinal);
    }

    private static async Task AssertImmutableAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.RaiseException, error.SqlState);
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class OpportunityFixture(long tenantId, long leadId, long baseId, char hashCharacter)
    {
        public long TenantId { get; } = tenantId;
        public long LeadId { get; } = leadId;
        public long BaseId { get; } = baseId;
        public char HashCharacter { get; } = hashCharacter;
        public long CommercialCaseId { get; private set; }
        public string NexoraSerial { get; private set; } = string.Empty;
        public long RecommendationId => BaseId + 1;
        public long FeedbackId => BaseId + 2;
        public long OutcomeId => BaseId + 3;
        public long OperationId => BaseId + 4;
        public long RecommendationEventId => BaseId + 5;
        public long FeedbackEventId => BaseId + 6;
        public long OutcomeEventId => BaseId + 7;
        public long RecommendationOutboxId => BaseId + 8;
        public long FeedbackOutboxId => BaseId + 9;
        public long OutcomeOutboxId => BaseId + 10;

        public void SetCommercialIdentity(long commercialCaseId, string nexoraSerial)
        {
            CommercialCaseId = commercialCaseId;
            NexoraSerial = nexoraSerial;
        }
    }
}
