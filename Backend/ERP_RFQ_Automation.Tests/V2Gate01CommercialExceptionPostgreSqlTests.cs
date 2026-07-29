using ERP_RFQ_Automation.CommercialIntelligence.Exceptions;
using ERP_RFQ_Automation.CommercialIntelligence.Sales;
using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class V2Gate01CommercialExceptionPostgreSqlTests(PostgreSqlTestDatabase database)
{
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Data_bearing_baseline_upgrades_without_rewriting_existing_routing_rows()
    {
        const string databaseName = "nexora_v2g1_upgrade_rehearsal";
        var adminBuilder = new NpgsqlConnectionStringBuilder(database.ConnectionString);
        await using (var admin = await database.OpenConnectionAsync())
        {
            await using var reset = admin.CreateCommand();
            reset.CommandText = $"DROP DATABASE IF EXISTS {databaseName}; CREATE DATABASE {databaseName};";
            await reset.ExecuteNonQueryAsync();
        }

        try
        {
            var rehearsalBuilder = new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString)
            {
                Database = databaseName
            };
            await using (var baseline = database.ContextForConnectionString(rehearsalBuilder.ConnectionString, 831001))
            {
                await baseline.Database.MigrateAsync("20260728202215_AllowNonEmailLeadIntake");
                var lead = Seed.Lead(baseline, 831011, 831001);
                await baseline.SaveChangesAsync();
                var decision = new LeadRoutingDecision
                {
                    BusinessUnitId = 831001,
                    LeadId = lead.Id,
                    MatchStatus = CustomerMatchStatus.NoEvidence,
                    Outcome = RoutingOutcome.Unassigned,
                    DecisionCode = "NO_OWNER",
                    Explanation = "{\"reason\":\"Authorized migration rehearsal row.\"}",
                    PolicyVersion = "routing-v1",
                    CorrelationId = "upgrade-rehearsal",
                    IdempotencyKey = "upgrade-rehearsal",
                    CreatedOn = DateTime.UtcNow
                };
                baseline.Add(new UnassignedWorkItem
                {
                    BusinessUnitId = 831001,
                    LeadId = lead.Id,
                    RoutingDecision = decision,
                    ReasonCode = "NO_OWNER",
                    Status = WorkItemStatus.Open,
                    Priority = 80,
                    EnteredOn = DateTime.UtcNow,
                    SlaDueOn = DateTime.UtcNow.AddHours(1),
                    RequiredAction = "Assign an owner",
                    IdempotencyKey = "upgrade-work-item",
                    Version = 1
                });
                await baseline.SaveChangesAsync();
                Assert.Equal(1, await baseline.Set<UnassignedWorkItem>().CountAsync());
            }

            await using (var upgraded = database.ContextForConnectionString(rehearsalBuilder.ConnectionString, 831001))
            {
                await upgraded.Database.MigrateAsync();
                var row = Assert.Single(await upgraded.Set<UnassignedWorkItem>().AsNoTracking().ToArrayAsync());
                Assert.Equal(831011, row.LeadId);
                Assert.Equal("upgrade-work-item", row.IdempotencyKey);
                Assert.True(await upgraded.Database.CanConnectAsync());
            }

            await using var verify = new NpgsqlConnection(rehearsalBuilder.ConnectionString);
            await verify.OpenAsync();
            await using var command = verify.CreateCommand();
            command.CommandText = """
                SELECT
                  EXISTS (
                    SELECT 1 FROM "__EFMigrationsHistory"
                    WHERE "MigrationId" = '20260729020217_V2Gate01CommercialExceptionCenter'
                  ),
                  (SELECT count(*) FROM commercial_exception_cases) = 0,
                  EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'AK_unassigned_work_items_BusinessUnitId_Id');
                """;
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.GetBoolean(0));
            Assert.True(reader.GetBoolean(1));
            Assert.True(reader.GetBoolean(2));
        }
        finally
        {
            await using var admin = await database.OpenConnectionAsync();
            await using var drop = admin.CreateCommand();
            drop.CommandText = $"DROP DATABASE IF EXISTS {databaseName} WITH (FORCE);";
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Concurrent_refresh_and_transition_share_one_receipt_and_one_audit_transition()
    {
        const long tenantId = 832001;
        const long ownerId = 832021;
        await using (var seed = database.ContextFor(tenantId))
        {
            var lead = Seed.Lead(seed, 832011, tenantId);
            seed.Users.Add(new User
            {
                Id = ownerId,
                Buid = tenantId,
                FirstName = "Concurrency",
                LastName = "Owner",
                Email = "v2g1-concurrency@example.test",
                PasswordHash = "not-a-real-secret",
                ImageUrl = "n/a",
                IsActive = true,
                CreatedBy = "qa",
                CreatedOn = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
            var decision = new LeadRoutingDecision
            {
                BusinessUnitId = tenantId,
                LeadId = lead.Id,
                MatchStatus = CustomerMatchStatus.NoEvidence,
                Outcome = RoutingOutcome.Unassigned,
                DecisionCode = "NO_OWNER",
                Explanation = "{\"reason\":\"Concurrent reconciliation seed.\"}",
                PolicyVersion = "routing-v1",
                CorrelationId = "concurrent-seed",
                IdempotencyKey = "concurrent-seed",
                CreatedOn = DateTime.UtcNow
            };
            seed.AddRange(
                new UnassignedWorkItem
                {
                    BusinessUnitId = tenantId,
                    LeadId = lead.Id,
                    RoutingDecision = decision,
                    ReasonCode = "NO_OWNER",
                    Status = WorkItemStatus.Open,
                    Priority = 90,
                    EnteredOn = DateTime.UtcNow.AddHours(-3),
                    SlaDueOn = DateTime.UtcNow.AddHours(-1),
                    RequiredAction = "Assign an owner",
                    IdempotencyKey = "concurrent-work-item",
                    Version = 1
                },
                new FollowUpTask
                {
                    BusinessUnitId = tenantId,
                    AssignedToUserId = ownerId,
                    AggregateType = CommercialAggregateType.Lead,
                    AggregateId = lead.Id,
                    DueAtUtc = DateTime.UtcNow.AddHours(-2),
                    Status = FollowUpStatus.Open,
                    Priority = 80,
                    PurposeCode = "CUSTOMER_RESPONSE",
                    CreatedAtUtc = DateTime.UtcNow.AddDays(-1),
                    UpdatedAtUtc = DateTime.UtcNow,
                    Version = 1,
                    CreatedBy = "qa",
                    CorrelationId = "concurrent-follow-up",
                    CreationIdempotencyKey = "concurrent-follow-up"
                });
            await seed.SaveChangesAsync();
        }

        await using var refreshContextA = database.ContextFor(tenantId);
        await using var refreshContextB = database.ContextFor(tenantId);
        var refreshA = new CommercialExceptionApplicationService(refreshContextA, new StubTenant(tenantId));
        var refreshB = new CommercialExceptionApplicationService(refreshContextB, new StubTenant(tenantId));
        var refreshResults = await Task.WhenAll(
            refreshA.RefreshAsync(tenantId,
                new RefreshCommercialExceptionsCommand("parallel-a", "parallel-refresh", "qa"), default),
            refreshB.RefreshAsync(tenantId,
                new RefreshCommercialExceptionsCommand("parallel-b", "parallel-refresh", "qa"), default));
        Assert.Equal(refreshResults[0], refreshResults[1]);

        long exceptionId;
        long exceptionVersion;
        await using (var inspect = database.ContextFor(tenantId))
        {
            var exception = await inspect.CommercialExceptionCases.AsNoTracking()
                .SingleAsync(x => x.ExceptionType == CommercialExceptionType.OverdueFollowUp);
            exceptionId = exception.Id;
            exceptionVersion = exception.Version;
            Assert.Equal(2, await inspect.CommercialExceptionCases.CountAsync());
            Assert.Equal(2, await inspect.CommercialExceptionEvents.CountAsync());
            Assert.Equal(2, await inspect.CommercialExceptionOutboxMessages.CountAsync());
            Assert.Single(await inspect.CommercialExceptionOperations.ToArrayAsync());
        }

        await using var transitionContextA = database.ContextFor(tenantId);
        await using var transitionContextB = database.ContextFor(tenantId);
        var transitionA = new CommercialExceptionApplicationService(transitionContextA, new StubTenant(tenantId));
        var transitionB = new CommercialExceptionApplicationService(transitionContextB, new StubTenant(tenantId));
        var transitionResults = await Task.WhenAll(
            transitionA.TransitionAsync(tenantId, exceptionId,
                new TransitionCommercialExceptionCommand(exceptionVersion, CommercialExceptionStatus.Acknowledged,
                    "ACKNOWLEDGE", "Reviewed concurrently", "parallel-transition-a", "parallel-transition", "qa"),
                CommercialExceptionAccessScope.ForTenant(), default),
            transitionB.TransitionAsync(tenantId, exceptionId,
                new TransitionCommercialExceptionCommand(exceptionVersion, CommercialExceptionStatus.Acknowledged,
                    "ACKNOWLEDGE", "Reviewed concurrently", "parallel-transition-b", "parallel-transition", "qa"),
                CommercialExceptionAccessScope.ForTenant(), default));
        Assert.Equal(transitionResults[0], transitionResults[1]);

        await using var final = database.ContextFor(tenantId);
        Assert.Equal(CommercialExceptionStatus.Acknowledged,
            (await final.CommercialExceptionCases.SingleAsync(x => x.Id == exceptionId)).Status);
        Assert.Equal(3, await final.CommercialExceptionEvents.CountAsync());
        Assert.Equal(3, await final.CommercialExceptionOutboxMessages.CountAsync());
        Assert.Equal(2, await final.CommercialExceptionOperations.CountAsync());
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Latest_schema_forces_tenant_RLS_and_least_privilege()
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT count(*) FROM "__EFMigrationsHistory"
                 WHERE "MigrationId" LIKE '%_V2Gate01CommercialExceptionCenter') = 1,
                (SELECT count(*) FROM pg_policies
                 WHERE schemaname = 'public' AND policyname = 'nexora_tenant_isolation'
                   AND tablename = ANY(ARRAY['commercial_exception_cases','commercial_exception_events','commercial_exception_operations','commercial_exception_outbox'])) = 4,
                (SELECT count(*) FROM pg_class
                 WHERE relname = ANY(ARRAY['commercial_exception_cases','commercial_exception_events','commercial_exception_operations','commercial_exception_outbox'])
                   AND relrowsecurity AND relforcerowsecurity) = 4,
                has_table_privilege('nexora_tenant_app', 'public.commercial_exception_cases', 'SELECT,INSERT,UPDATE')
                  AND NOT has_table_privilege('nexora_tenant_app', 'public.commercial_exception_cases', 'DELETE'),
                has_table_privilege('nexora_tenant_app', 'public.commercial_exception_events', 'SELECT,INSERT')
                  AND NOT has_table_privilege('nexora_tenant_app', 'public.commercial_exception_events', 'UPDATE,DELETE'),
                has_table_privilege('nexora_tenant_app', 'public.commercial_exception_operations', 'SELECT,INSERT')
                  AND NOT has_table_privilege('nexora_tenant_app', 'public.commercial_exception_operations', 'UPDATE,DELETE'),
                (SELECT count(*) FROM pg_trigger WHERE NOT tgisinternal
                  AND tgname = ANY(ARRAY['trg_guard_commercial_exception_case','trg_require_commercial_exception_event','trg_commercial_exception_events_append_only','trg_commercial_exception_operations_append_only','trg_guard_commercial_exception_outbox'])) = 5;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        for (var index = 0; index < 7; index++) Assert.True(reader.GetBoolean(index));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task RLS_hides_other_tenant_and_database_guards_lineage_owner_and_events()
    {
        const long tenantA = 829001;
        const long tenantB = 829002;
        await using var connection = await database.OpenConnectionAsync();
        await using (var seed = connection.CreateCommand())
        {
            seed.CommandText = $$"""
                INSERT INTO "BusinessUnits" ("ID","BusinessUnitCode","BusinessUnitName","CreatedBy","CreatedOn") VALUES
                  ({{tenantA}}, 'V2G1-A', 'V2 Gate 1 A', 'qa', now()),
                  ({{tenantB}}, 'V2G1-B', 'V2 Gate 1 B', 'qa', now())
                ON CONFLICT ("ID") DO NOTHING;
                INSERT INTO "Users" ("ID","FirstName","LastName","Email","Password_Hash","ImageURL","BUID","IsActive","CreatedBy","CreatedOn") VALUES
                  (829011, 'Owner', 'A', 'v2g1-a@example.test', 'not-a-real-secret', 'n/a', {{tenantA}}, true, 'qa', now()),
                  (829012, 'Owner', 'B', 'v2g1-b@example.test', 'not-a-real-secret', 'n/a', {{tenantB}}, true, 'qa', now())
                ON CONFLICT ("ID") DO NOTHING;
                INSERT INTO "CommercialCases" ("Id","BusinessUnitID","AllocationNumber","MasterReference","CreatedOn","CreatedBy") VALUES
                  (829021, {{tenantA}}, 829021, 'NXR-V2G1-A', now(), 'qa'),
                  (829022, {{tenantB}}, 829022, 'NXR-V2G1-B', now(), 'qa')
                ON CONFLICT ("Id") DO NOTHING;
                INSERT INTO follow_up_tasks
                  ("Id","BusinessUnitId","AssignedToUserId","AggregateType","AggregateId","DueAtUtc","Status","Priority","PurposeCode","CreatedAtUtc","UpdatedAtUtc","Version","CreatedBy","CorrelationId","CreationIdempotencyKey") VALUES
                  (829031, {{tenantA}}, 829011, 'Lead', 829041, now() - interval '1 day', 'Open', 80, 'CUSTOMER_FOLLOW_UP', now() - interval '2 days', now(), 1, 'qa', 'v2g1-a', 'v2g1-a'),
                  (829032, {{tenantB}}, 829012, 'Lead', 829042, now() - interval '1 day', 'Open', 80, 'CUSTOMER_FOLLOW_UP', now() - interval '2 days', now(), 1, 'qa', 'v2g1-b', 'v2g1-b')
                ON CONFLICT ("Id") DO NOTHING;
                INSERT INTO commercial_exception_cases
                  ("Id","BusinessUnitId","CommercialCaseId","NexoraSerial","ExceptionType","ExceptionKey","SourceType","SourceId","SourceVersion","FollowUpTaskId","Severity","Status","ReasonCode","Title","Summary","RecommendedActionCode","EvidenceJson","RuleVersion","FirstDetectedAtUtc","LastDetectedAtUtc","SlaDueAtUtc","Version") VALUES
                  (829051, {{tenantA}}, 829021, 'NXR-V2G1-A', 'OverdueFollowUp', 'follow-up:829031', 'FollowUpTask', 829031, 1, 829031, 'High', 'Open', 'FOLLOW_UP_OVERDUE', 'Overdue follow-up', 'Follow-up requires attention.', 'OPEN_FOLLOW_UP', '{}', 'commercial-exceptions-v1', now(), now(), now() - interval '1 day', 1),
                  (829052, {{tenantB}}, 829022, 'NXR-V2G1-B', 'OverdueFollowUp', 'follow-up:829032', 'FollowUpTask', 829032, 1, 829032, 'High', 'Open', 'FOLLOW_UP_OVERDUE', 'Overdue follow-up', 'Follow-up requires attention.', 'OPEN_FOLLOW_UP', '{}', 'commercial-exceptions-v1', now(), now(), now() - interval '1 day', 1)
                ON CONFLICT ("Id") DO NOTHING;
                INSERT INTO commercial_exception_events
                  ("Id","BusinessUnitId","CommercialExceptionCaseId","ToStatus","FromVersion","ToVersion","ActionCode","Reason","ActorId","OccurredAtUtc","CorrelationId","IdempotencyKey","RequestHash") VALUES
                  (829061, {{tenantA}}, 829051, 'Open', 0, 1, 'DETECTED', 'Detected by rule.', 'system', now(), 'v2g1-detect-a', 'v2g1-detect-a', repeat('a', 64)),
                  (829062, {{tenantB}}, 829052, 'Open', 0, 1, 'DETECTED', 'Detected by rule.', 'system', now(), 'v2g1-detect-b', 'v2g1-detect-b', repeat('b', 64))
                ON CONFLICT ("Id") DO NOTHING;
                INSERT INTO commercial_exception_operations
                  ("Id","BusinessUnitId","OperationType","IdempotencyKey","RequestHash","CorrelationId","ActorId","ResultJson","OccurredAtUtc") VALUES
                  (829071, {{tenantA}}, 'Refresh', 'v2g1-operation-a', repeat('c', 64), 'v2g1-operation-a', 'system', '{}', now()),
                  (829072, {{tenantB}}, 'Refresh', 'v2g1-operation-b', repeat('d', 64), 'v2g1-operation-b', 'system', '{}', now())
                ON CONFLICT ("Id") DO NOTHING;
                INSERT INTO commercial_exception_outbox
                  ("Id","BusinessUnitId","CommercialExceptionEventId","EventType","Payload","OccurredAtUtc","AvailableAtUtc","AttemptCount") VALUES
                  (829081, {{tenantA}}, 829061, 'CommercialException.Detected', '{}', now(), now(), 0),
                  (829082, {{tenantB}}, 829062, 'CommercialException.Detected', '{}', now(), now(), 0)
                ON CONFLICT ("Id") DO NOTHING;
                """;
            await seed.ExecuteNonQueryAsync();
        }

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using var scope = connection.CreateCommand();
            scope.Transaction = transaction;
            scope.CommandText = $$"""
                SET LOCAL ROLE nexora_tenant_app;
                SELECT set_config('nexora.business_unit_id', '{{tenantA}}', true);
                """;
            await scope.ExecuteNonQueryAsync();
            await using var count = connection.CreateCommand();
            count.Transaction = transaction;
            count.CommandText = """
                SELECT (SELECT count(*) FROM commercial_exception_cases),
                       (SELECT count(*) FROM commercial_exception_events),
                       (SELECT count(*) FROM commercial_exception_operations),
                       (SELECT count(*) FROM commercial_exception_outbox)
                """;
            await using var visible = await count.ExecuteReaderAsync();
            Assert.True(await visible.ReadAsync());
            for (var index = 0; index < 4; index++) Assert.Equal(1L, visible.GetInt64(index));
            await visible.DisposeAsync();
            await transaction.RollbackAsync();
        }

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using var scope = connection.CreateCommand();
            scope.Transaction = transaction;
            scope.CommandText = "SET LOCAL ROLE nexora_tenant_app";
            await scope.ExecuteNonQueryAsync();
            await using var count = connection.CreateCommand();
            count.Transaction = transaction;
            count.CommandText = "SELECT count(*) FROM commercial_exception_operations";
            Assert.Equal(0L, Convert.ToInt64(await count.ExecuteScalarAsync()));
            await transaction.RollbackAsync();
        }

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using var crossTenantWrite = connection.CreateCommand();
            crossTenantWrite.Transaction = transaction;
            crossTenantWrite.CommandText = $$"""
                SET LOCAL ROLE nexora_tenant_app;
                SELECT set_config('nexora.business_unit_id', '{{tenantA}}', true);
                INSERT INTO commercial_exception_operations
                  ("BusinessUnitId","OperationType","IdempotencyKey","RequestHash","CorrelationId","ActorId","ResultJson","OccurredAtUtc")
                VALUES ({{tenantB}}, 'Refresh', 'cross-tenant-denied', repeat('e', 64), 'cross-tenant-denied', 'system', '{}', now());
                """;
            var error = await Assert.ThrowsAsync<PostgresException>(() => crossTenantWrite.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
            await transaction.RollbackAsync();
        }

        await using (var wrongOwner = connection.CreateCommand())
        {
            wrongOwner.CommandText = "UPDATE commercial_exception_cases SET \"OwnerUserId\" = 829012 WHERE \"Id\" = 829051";
            var error = await Assert.ThrowsAsync<PostgresException>(() => wrongOwner.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.RaiseException, error.SqlState);
        }

        await using (var rewriteLineage = connection.CreateCommand())
        {
            rewriteLineage.CommandText = "UPDATE commercial_exception_cases SET \"NexoraSerial\" = 'CHANGED' WHERE \"Id\" = 829051";
            var error = await Assert.ThrowsAsync<PostgresException>(() => rewriteLineage.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.RaiseException, error.SqlState);
        }

        await using (var rewriteEvent = connection.CreateCommand())
        {
            rewriteEvent.CommandText = "UPDATE commercial_exception_events SET \"Reason\" = 'changed' WHERE \"Id\" = 829061";
            var error = await Assert.ThrowsAsync<PostgresException>(() => rewriteEvent.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.RaiseException, error.SqlState);
        }

        await using (var unauditedStateChange = connection.CreateCommand())
        {
            unauditedStateChange.CommandText =
                "UPDATE commercial_exception_cases SET \"Status\" = 'Acknowledged', \"Version\" = 2 WHERE \"Id\" = 829051";
            var error = await Assert.ThrowsAsync<PostgresException>(() => unauditedStateChange.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.RaiseException, error.SqlState);
        }

        await using (var unauditedEvidenceChange = connection.CreateCommand())
        {
            unauditedEvidenceChange.CommandText =
                "UPDATE commercial_exception_cases SET \"EvidenceJson\" = '{\"changed\":true}' WHERE \"Id\" = 829051";
            var error = await Assert.ThrowsAsync<PostgresException>(() => unauditedEvidenceChange.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.RaiseException, error.SqlState);
        }

        await using (var contradictoryAction = connection.CreateCommand())
        {
            contradictoryAction.CommandText = """
                INSERT INTO commercial_exception_events
                  ("BusinessUnitId","CommercialExceptionCaseId","FromStatus","ToStatus","FromVersion","ToVersion","ActionCode","Reason","ActorId","OccurredAtUtc","CorrelationId","IdempotencyKey","RequestHash")
                VALUES (829001, 829051, 'Open', 'Acknowledged', 1, 2, 'APPROVED', 'invalid audit action', 'qa', now(), 'bad-action', 'bad-action', repeat('f', 64));
                """;
            var error = await Assert.ThrowsAsync<PostgresException>(() => contradictoryAction.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        }

        await using (var disguisedDismissal = connection.CreateCommand())
        {
            disguisedDismissal.CommandText = """
                INSERT INTO commercial_exception_events
                  ("BusinessUnitId","CommercialExceptionCaseId","FromStatus","ToStatus","FromVersion","ToVersion","ActionCode","Reason","ActorId","OccurredAtUtc","CorrelationId","IdempotencyKey","RequestHash")
                VALUES (829001, 829051, 'Open', 'Dismissed', 1, 2, 'REFRESHED', 'invalid refresh transition', 'qa', now(), 'bad-refresh', 'bad-refresh', repeat('f', 64));
                """;
            var error = await Assert.ThrowsAsync<PostgresException>(() => disguisedDismissal.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        }

        await using (var rewriteOperation = connection.CreateCommand())
        {
            rewriteOperation.CommandText =
                "UPDATE commercial_exception_operations SET \"ActorId\" = 'changed' WHERE \"Id\" = 829071";
            var error = await Assert.ThrowsAsync<PostgresException>(() => rewriteOperation.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.RaiseException, error.SqlState);
        }

        await using (var legalAcknowledgedReopen = connection.CreateCommand())
        {
            legalAcknowledgedReopen.CommandText = """
                BEGIN;
                INSERT INTO commercial_exception_events
                  ("BusinessUnitId","CommercialExceptionCaseId","FromStatus","ToStatus","FromVersion","ToVersion","ActionCode","Reason","ActorId","OccurredAtUtc","CorrelationId","IdempotencyKey","RequestHash")
                VALUES (829001, 829051, 'Open', 'Acknowledged', 1, 2, 'ACKNOWLEDGE', 'reviewed', 'qa', now(), 'legal-ack', 'legal-ack', repeat('a', 64));
                UPDATE commercial_exception_cases SET "Status"='Acknowledged', "Version"=2 WHERE "Id"=829051;
                INSERT INTO commercial_exception_events
                  ("BusinessUnitId","CommercialExceptionCaseId","FromStatus","ToStatus","FromVersion","ToVersion","ActionCode","Reason","ActorId","OccurredAtUtc","CorrelationId","IdempotencyKey","RequestHash")
                VALUES (829001, 829051, 'Acknowledged', 'Open', 2, 3, 'REOPEN', 'returned to active review', 'qa', now(), 'legal-reopen', 'legal-reopen', repeat('b', 64));
                UPDATE commercial_exception_cases SET "Status"='Open', "Version"=3 WHERE "Id"=829051;
                COMMIT;
                """;
            await legalAcknowledgedReopen.ExecuteNonQueryAsync();
        }
    }
}
