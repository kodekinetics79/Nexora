using System.Security.Cryptography;
using System.Text;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.CommercialCases.Lifecycle;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Sla;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class PostgreSqlProductionDialectTests
{
    private readonly PostgreSqlTestDatabase _database;

    public PostgreSqlProductionDialectTests(PostgreSqlTestDatabase database)
        => _database = database;

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task FinancialMigration_ClassifiesHistoricalQuoteArithmetic()
    {
        var databaseName = $"finance_{Guid.NewGuid():N}";
        var adminBuilder = new NpgsqlConnectionStringBuilder(_database.ConnectionString)
        {
            Database = "postgres"
        };
        var isolatedBuilder = new NpgsqlConnectionStringBuilder(_database.ConnectionString)
        {
            Database = databaseName
        };

        await using (var admin = new NpgsqlConnection(adminBuilder.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = admin.CreateCommand();
            create.CommandText = $"CREATE DATABASE \"{databaseName}\"";
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            await using (var context = _database.ContextForConnectionString(isolatedBuilder.ConnectionString, null))
            {
                var migrator = context.GetService<IMigrator>();
                await migrator.MigrateAsync("20260723140000_AddAiGovernanceLedger");
                await context.Database.ExecuteSqlRawAsync("""
                    INSERT INTO "BusinessUnits"
                        ("ID", "BusinessUnitCode", "BusinessUnitName", "CreatedBy", "CreatedOn")
                    VALUES (94001, 'FINMIG', 'Finance Migration', 'tests', now());

                    INSERT INTO "Quotes"
                        ("ID", "QuoteNo", "BusinessUnitID", "CreatedBy", "CreatedDate", "TotalAmount")
                    VALUES
                        (94001, 'QT-LEGACY-EXCLUSIVE', 94001, 'tests', now(), 100),
                        (94002, 'QT-LEGACY-INCLUSIVE', 94001, 'tests', now(), 105);

                    INSERT INTO "QuoteItems"
                        ("ID", "QuoteID", "ItemDescription", "Quantity", "UnitPrice", "Discount", "TaxAmount", "TotalAmount", "CreatedBy", "CreatedDate")
                    VALUES
                        (94001, 94001, 'Exclusive', 1, 100, 0, 5, 100, 'tests', now()),
                        (94002, 94002, 'Inclusive', 1, 100, 0, 5, 105, 'tests', now());
                    """);

                await migrator.MigrateAsync("20260723150000_EnforceQuoteOrderFinancialIntegrity");

                var versions = await context.Quotes.IgnoreQueryFilters()
                    .Where(quote => quote.Id == 94_001 || quote.Id == 94_002)
                    .OrderBy(quote => quote.Id)
                    .Select(quote => quote.FinancialCalculationVersion)
                    .ToListAsync();
                Assert.Equal(new[] { 1, 2 }, versions);

                await context.Database.ExecuteSqlRawAsync("""
                    INSERT INTO "Setup_Master"
                        ("SetupID", "SetupType", "SetupCode", "SetupValue", "BusinessUnitID", "IsActive", "CreatedBy", "CreatedOn")
                    VALUES (94010, 'Role', 'FINANCE_MANAGER', 'Finance Manager', 94001, true, 'tests', now());
                    """);
                await migrator.MigrateAsync("20260723160000_AddCommercialFinanceLedger");

                var financePermissionCount = await context.Database.SqlQueryRaw<long>("""
                    SELECT count(*) AS "Value"
                    FROM "RolePermissions" permission
                    JOIN "Module" module ON module."ID" = permission."ModuleID"
                    WHERE permission."RoleID" = 94010
                      AND permission."BusinessUnitID" = 94001
                      AND permission."CanCreate" = true
                      AND permission."CanEdit" = true
                      AND module."ModuleName" IN ('Accounts Receivable', 'Customer Payments')
                    """).SingleAsync();
                Assert.Equal(2, financePermissionCount);
            }
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
    public async Task AiLedger_IsTenantBoundAndImmutable()
    {
        Guid requestId;
        await using (var context = _database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(context, 9_911);
            Seed.EnsureBusinessUnit(context, 9_912);
            var request = new AiRequest
            {
                Id = Guid.NewGuid(),
                BusinessUnitId = 9_911,
                Operation = "RfqExtraction",
                IdempotencyKey = "postgres-ai-ledger-test",
                PromptHash = new string('B', 64),
                PromptVersion = "rfq-v1",
                Provider = "Ollama",
                Model = "test",
                Status = AiCallStatuses.Succeeded,
                TokenSource = AiTokenSources.ProviderExact,
                CreatedOn = DateTime.UtcNow,
                CompletedOn = DateTime.UtcNow
            };
            requestId = request.Id;
            context.AiRequests.Add(request);
            context.AiCallAttempts.Add(new AiCallAttempt
            {
                Request = request,
                BusinessUnitId = 9_911,
                AttemptNumber = 1,
                Provider = "Ollama",
                Model = "test",
                Status = AiCallStatuses.Succeeded,
                TokenSource = AiTokenSources.ProviderExact,
                StartedOn = DateTime.UtcNow,
                CompletedOn = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
            Assert.False((await context.AiProcessingPolicies.IgnoreQueryFilters()
                .SingleAsync(p => p.BusinessUnitId == 9_912)).ExternalProcessingAllowed);
        }

        await using var connection = await _database.OpenConnectionAsync();
        await using var updateAttempt = connection.CreateCommand();
        updateAttempt.CommandText = "UPDATE public.\"AiCallAttempts\" SET \"Status\" = 'Failed' WHERE \"RequestId\" = @id";
        updateAttempt.Parameters.AddWithValue("id", requestId);
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(
            () => updateAttempt.ExecuteNonQueryAsync())).SqlState);

        await using var deleteRequest = connection.CreateCommand();
        deleteRequest.CommandText = "DELETE FROM public.\"AiRequests\" WHERE \"Id\" = @id";
        deleteRequest.Parameters.AddWithValue("id", requestId);
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(
            () => deleteRequest.ExecuteNonQueryAsync())).SqlState);

        await using var rewriteRequest = connection.CreateCommand();
        rewriteRequest.CommandText = "UPDATE public.\"AiRequests\" SET \"Provider\" = 'Anthropic' WHERE \"Id\" = @id";
        rewriteRequest.Parameters.AddWithValue("id", requestId);
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(
            () => rewriteRequest.ExecuteNonQueryAsync())).SqlState);

        await using var rewriteTerminalUsage = connection.CreateCommand();
        rewriteTerminalUsage.CommandText = "UPDATE public.\"AiRequests\" SET \"ErrorCode\" = 'rewritten' WHERE \"Id\" = @id";
        rewriteTerminalUsage.Parameters.AddWithValue("id", requestId);
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(
            () => rewriteTerminalUsage.ExecuteNonQueryAsync())).SqlState);

        await using var mismatch = connection.CreateCommand();
        mismatch.CommandText = """
            INSERT INTO public."AiCallAttempts"
                ("RequestId", "BusinessUnitId", "AttemptNumber", "Provider", "Model", "Status",
                 "InputTokens", "OutputTokens", "TokenSource", "LatencyMilliseconds", "StartedOn", "CompletedOn")
            VALUES (@id, 9912, 2, 'Ollama', 'test', 'Succeeded', 0, 0, 'Estimated', 0, now(), now())
            """;
        mismatch.Parameters.AddWithValue("id", requestId);
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation,
            (await Assert.ThrowsAsync<PostgresException>(() => mismatch.ExecuteNonQueryAsync())).SqlState);

        await using var tenantTx = await connection.BeginTransactionAsync();
        await using (var scopeTenant = connection.CreateCommand())
        {
            scopeTenant.Transaction = tenantTx;
            scopeTenant.CommandText = "SET LOCAL ROLE nexora_tenant_app; SELECT set_config('nexora.business_unit_id', '9912', true);";
            await scopeTenant.ExecuteNonQueryAsync();
        }
        await using (var hidden = connection.CreateCommand())
        {
            hidden.Transaction = tenantTx;
            hidden.CommandText = "SELECT count(*) FROM public.\"AiRequests\" WHERE \"Id\" = @id";
            hidden.Parameters.AddWithValue("id", requestId);
            Assert.Equal(0L, (long)(await hidden.ExecuteScalarAsync())!);
        }
        await tenantTx.RollbackAsync();
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task AiLedger_RuntimeLoginRequiresExplicitTenantRoleScope()
    {
        const string runtimeRole = "nexora_runtime_test";
        const string runtimePassword = "runtime-test-password";
        await using (var context = _database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(context, 9_911);
            await context.SaveChangesAsync();
            await EnqueueGovernedJobAsync(
                context, NewQueue(context), $"runtime-role-{Guid.NewGuid():N}", 9_911, maxAttempts: 3);
        }
        await using (var admin = await _database.OpenConnectionAsync())
        await using (var create = admin.CreateCommand())
        {
            create.CommandText = $"""
                INSERT INTO platform."Tenants"
                    ("Id", "Name", "Slug", "Status", "PrimaryBusinessUnitId", "CreatedOn")
                VALUES (991100, 'Runtime Test Tenant', 'runtime-test-tenant', 'Active', 9911, now())
                ON CONFLICT ("Id") DO NOTHING;
                DROP ROLE IF EXISTS {runtimeRole};
                CREATE ROLE {runtimeRole} LOGIN PASSWORD '{runtimePassword}' NOINHERIT NOSUPERUSER NOBYPASSRLS;
                GRANT nexora_tenant_app, nexora_identity_app, nexora_pipeline_app TO {runtimeRole};
                """;
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            var runtimeConnectionString = new NpgsqlConnectionStringBuilder(_database.ConnectionString)
            {
                Username = runtimeRole,
                Password = runtimePassword,
                Pooling = false
            }.ConnectionString;
            await using var runtime = new NpgsqlConnection(runtimeConnectionString);
            await runtime.OpenAsync();

            await using (var attributes = runtime.CreateCommand())
            {
                attributes.CommandText = """
                    SELECT NOT runtime_role.rolinherit
                           AND NOT runtime_role.rolsuper
                           AND NOT runtime_role.rolbypassrls
                           AND pg_has_role(current_user, 'nexora_tenant_app', 'MEMBER')
                           AND pg_has_role(current_user, 'nexora_identity_app', 'MEMBER')
                           AND pg_has_role(current_user, 'nexora_pipeline_app', 'MEMBER')
                           AND EXISTS (
                               SELECT 1 FROM pg_roles execution_role
                               WHERE execution_role.rolname IN ('nexora_identity_app', 'nexora_pipeline_app')
                                 AND NOT execution_role.rolcanlogin
                                 AND NOT execution_role.rolinherit
                                 AND NOT execution_role.rolsuper
                                 AND NOT execution_role.rolcreatedb
                                 AND NOT execution_role.rolcreaterole
                                 AND execution_role.rolbypassrls
                               HAVING count(*) = 2)
                    FROM pg_roles runtime_role WHERE runtime_role.rolname = current_user;
                    """;
                Assert.True((bool)(await attributes.ExecuteScalarAsync())!);
            }

            await using (var direct = runtime.CreateCommand())
            {
                direct.CommandText = "SELECT count(*) FROM public.\"AiProcessingPolicies\"";
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,
                    (await Assert.ThrowsAsync<PostgresException>(() => direct.ExecuteScalarAsync())).SqlState);
            }

            await using var transaction = await runtime.BeginTransactionAsync();
            await using (var setup = runtime.CreateCommand())
            {
                setup.Transaction = transaction;
                setup.CommandText = """
                SET LOCAL ROLE nexora_tenant_app;
                SELECT set_config('nexora.business_unit_id', '9911', true);
                """;
                await setup.ExecuteNonQueryAsync();
            }
            await using var scoped = runtime.CreateCommand();
            scoped.Transaction = transaction;
            scoped.CommandText = "SELECT count(*) FROM public.\"AiProcessingPolicies\" WHERE \"BusinessUnitId\" = 9911;";
            Assert.Equal(1L, (long)(await scoped.ExecuteScalarAsync())!);
            await using var audit = runtime.CreateCommand();
            audit.Transaction = transaction;
            audit.CommandText = """
                INSERT INTO platform."PlatformAuditLogs"
                    ("ActorPlatformUserId", "ActAsTenantId", "Action", "TargetType", "TargetId", "Metadata", "CreatedOn")
                VALUES (1, 991100, 'tenant.ai-policy.update', 'AiProcessingPolicy', '9911', '{}', now());
                """;
            Assert.Equal(1, await audit.ExecuteNonQueryAsync());
            await transaction.CommitAsync();

            await using var deniedTransaction = await runtime.BeginTransactionAsync();
            await using var forgedAudit = runtime.CreateCommand();
            forgedAudit.Transaction = deniedTransaction;
            forgedAudit.CommandText = """
                SET LOCAL ROLE nexora_tenant_app;
                SELECT set_config('nexora.business_unit_id', '9911', true);
                INSERT INTO platform."PlatformAuditLogs"
                    ("ActorPlatformUserId", "ActAsTenantId", "Action", "TargetType", "TargetId", "Metadata", "CreatedOn")
                VALUES (1, 991100, 'tenant.suspend', 'Tenant', '991100', '{}', now());
                """;
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,
                (await Assert.ThrowsAsync<PostgresException>(() => forgedAudit.ExecuteNonQueryAsync())).SqlState);
            await deniedTransaction.RollbackAsync();

            await using (var identityTransaction = await runtime.BeginTransactionAsync())
            {
                await using var identityRead = runtime.CreateCommand();
                identityRead.Transaction = identityTransaction;
                identityRead.CommandText = "SET LOCAL ROLE nexora_identity_app; SELECT count(*) FROM public.\"Users\";";
                Assert.IsType<long>(await identityRead.ExecuteScalarAsync());
                await identityTransaction.RollbackAsync();
            }

            await using (var identityWriteTransaction = await runtime.BeginTransactionAsync())
            {
                await using var identityWrite = runtime.CreateCommand();
                identityWrite.Transaction = identityWriteTransaction;
                identityWrite.CommandText = "SET LOCAL ROLE nexora_identity_app; UPDATE public.\"Users\" SET \"Email\" = \"Email\" WHERE false;";
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,
                    (await Assert.ThrowsAsync<PostgresException>(() => identityWrite.ExecuteNonQueryAsync())).SqlState);
                await identityWriteTransaction.RollbackAsync();
            }

            await using (var pipelineTransaction = await runtime.BeginTransactionAsync())
            {
                await using var pipelineRead = runtime.CreateCommand();
                pipelineRead.Transaction = pipelineTransaction;
                pipelineRead.CommandText = "SET LOCAL ROLE nexora_pipeline_app; SELECT count(*) FROM public.\"ExtractionJobs\";";
                Assert.IsType<long>(await pipelineRead.ExecuteScalarAsync());
                await pipelineTransaction.RollbackAsync();
            }

            var runtimeOptions = new DbContextOptionsBuilder<ErpRfqAutomationContext>()
                .UseNpgsql(runtimeConnectionString)
                .Options;
            await using (var claimContext = new ErpRfqAutomationContext(runtimeOptions, new StubTenant(null)))
            {
                var queue = new ExtractionQueue(
                    claimContext, new NoopLogger<ExtractionQueue>(), new StubTenant(null));
                var claimed = await queue.ClaimAsync(
                    "runtime-role-test", TimeSpan.FromMinutes(2), perTenantCap: 1);
                Assert.NotNull(claimed);
                Assert.Equal(9_911, claimed.BusinessUnitId);
                Assert.Equal(ExtractionStatus.Leased, claimed.Status);

                await using var tenantContext = new ErpRfqAutomationContext(
                    runtimeOptions, new StubTenant(claimed.BusinessUnitId));
                var tenantQueue = new ExtractionQueue(
                    tenantContext, new NoopLogger<ExtractionQueue>(), new StubTenant(claimed.BusinessUnitId));
                Assert.True(await tenantQueue.SetStatusAsync(
                    claimed.Id, "runtime-role-test", claimed.Attempts, ExtractionStatus.Extracting));
            }

            await using (var secretTransaction = await runtime.BeginTransactionAsync())
            {
                await using var secretRead = runtime.CreateCommand();
                secretRead.Transaction = secretTransaction;
                secretRead.CommandText = "SET LOCAL ROLE nexora_pipeline_app; SELECT count(*) FROM public.\"FinanceProviderSecrets\";";
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,
                    (await Assert.ThrowsAsync<PostgresException>(() => secretRead.ExecuteScalarAsync())).SqlState);
                await secretTransaction.RollbackAsync();
            }
        }
        finally
        {
            await using var admin = await _database.OpenConnectionAsync();
            await using var drop = admin.CreateCommand();
            drop.CommandText = $"REVOKE nexora_tenant_app, nexora_identity_app, nexora_pipeline_app FROM {runtimeRole}; DROP ROLE IF EXISTS {runtimeRole};";
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task QuoteOutcome_LifecycleTransactionUsesConfiguredRetryStrategy()
    {
        var businessUnitId = 9_800_000L + Random.Shared.Next(1, 100_000);
        var options = new DbContextOptionsBuilder<ErpRfqAutomationContext>()
            .UseNpgsql(_database.ConnectionString, npgsql => npgsql.EnableRetryOnFailure())
            .Options;
        await using var context = new ErpRfqAutomationContext(options, new StubTenant(null));
        Seed.EnsureBusinessUnit(context, businessUnitId);
        var sent = new SetupMaster
        {
            SetupType = "QuoteStatus",
            SetupCode = "SENT",
            SetupValue = "Sent",
            BusinessUnitId = businessUnitId,
            IsActive = true,
            CreatedBy = "retry-strategy-test",
            CreatedOn = DateTime.UtcNow
        };
        context.SetupMasters.Add(sent);
        await context.SaveChangesAsync();
        var quote = new Quote
        {
            QuoteNo = $"RETRY-{Guid.NewGuid():N}",
            BusinessUnitId = businessUnitId,
            StatusId = sent.SetupId,
            SentOn = DateTime.UtcNow.AddDays(-2),
            ValidUntil = DateTime.UtcNow.AddDays(-1),
            CreatedBy = "retry-strategy-test",
            CreatedDate = DateTime.UtcNow.AddDays(-2)
        };
        context.Quotes.Add(quote);
        await context.SaveChangesAsync();

        var service = new QuoteOutcomeService(
            context,
            null!,
            new NoopLogger<QuoteOutcomeService>(),
            lifecycle: new QueryingQuoteLifecycle(context));

        Assert.True(await service.ExpireAsync(quote.Id));
        context.ChangeTracker.Clear();
        Assert.NotNull((await context.Quotes.SingleAsync(x => x.Id == quote.Id)).OutcomeOn);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ReviewAudit_IsImmutableAndCannotReferenceAnotherTenantsLead()
    {
        await using (var context = _database.ContextFor(null))
        {
            Seed.Lead(context, 9_910_001, 9_901, parseStatus: "NeedsReview");
            Seed.Lead(context, 9_920_001, 9_902, parseStatus: "NeedsReview");
            await context.SaveChangesAsync();
            context.Set<LeadReviewAudit>().Add(new LeadReviewAudit
            {
                BusinessUnitId = 9_901,
                LeadId = 9_910_001,
                FromVersion = 1,
                ToVersion = 2,
                Action = "save",
                ReviewedBy = "database-test",
                BeforeJson = "{}",
                AfterJson = "{}",
                ReviewedOn = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }

        await using var connection = await _database.OpenConnectionAsync();
        await using var update = connection.CreateCommand();
        update.CommandText = "UPDATE public.\"LeadReviewAudits\" SET \"Action\" = 'approve' WHERE \"LeadId\" = 9910001";
        var updateError = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync());
        Assert.Equal("55000", updateError.SqlState);

        await using var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM public.\"LeadReviewAudits\" WHERE \"LeadId\" = 9910001";
        var deleteError = await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
        Assert.Equal("55000", deleteError.SqlState);

        // A plain TRUNCATE is now refused TWICE over: ExtractionCorpusEntries carries a
        // foreign key to this table, so PostgreSQL rejects it as feature_not_supported
        // (0A000) before the immutability trigger is ever reached. Both are refusals and
        // either is acceptable here.
        await using var truncate = connection.CreateCommand();
        truncate.CommandText = "TRUNCATE TABLE public.\"LeadReviewAudits\"";
        var truncateError = await Assert.ThrowsAsync<PostgresException>(() => truncate.ExecuteNonQueryAsync());
        Assert.Contains(truncateError.SqlState, new[] { "55000", "0A000" });

        // …and the trigger itself still refuses when the foreign-key objection is removed,
        // which is the property this assertion actually exists to protect. Without this
        // second case the FK above would have silently replaced a trigger test with a
        // referential-integrity test.
        await using var truncateCascade = connection.CreateCommand();
        truncateCascade.CommandText = "TRUNCATE TABLE public.\"LeadReviewAudits\" CASCADE";
        var truncateCascadeError = await Assert.ThrowsAsync<PostgresException>(
            () => truncateCascade.ExecuteNonQueryAsync());
        Assert.Equal("55000", truncateCascadeError.SqlState);

        await using var mismatch = connection.CreateCommand();
        mismatch.CommandText = """
            INSERT INTO public."LeadReviewAudits"
                ("BusinessUnitId", "LeadId", "FromVersion", "ToVersion", "Action",
                 "ReviewedBy", "BeforeJson", "AfterJson", "ReviewedOn")
            VALUES (9901, 9920001, 1, 2, 'save', 'database-test', '{}', '{}', now())
            """;
        var mismatchError = await Assert.ThrowsAsync<PostgresException>(() => mismatch.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, mismatchError.SqlState);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ProviderEvidenceMigration_PreservesUnsignedHistoricalRows()
    {
        var databaseName = $"provider_evidence_{Guid.NewGuid():N}";
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
            await migrator.MigrateAsync("20260723232000_GovernPromiseIdempotency");
            await context.Database.ExecuteSqlRawAsync("""
                INSERT INTO "BusinessUnits"
                    ("ID", "BusinessUnitCode", "BusinessUnitName", "CreatedBy", "CreatedOn")
                VALUES (94801, 'EVIDENCE', 'Evidence migration', 'tests', now());
                INSERT INTO "Customers"
                    ("ID", "Name", "ImageURL", "BUID", "CreatedBy", "CreatedOn")
                VALUES (94802, 'Legacy evidence customer', '', 94801, 'tests', now());
                INSERT INTO "FinanceCommunicationContacts"
                    ("Id", "BusinessUnitId", "CustomerId", "Purpose", "Channel", "DestinationToken",
                     "MaskedDestination", "IsVerified", "IsActive", "EffectiveFrom",
                     "VerificationEvidenceReference", "VerificationProviderEventId", "IdempotencyKey",
                     "RequestHash", "Version", "CreatedBy", "CreatedOn")
                VALUES (94803, 94801, 94802, 'Collections', 'Email', 'vault:legacy-contact',
                        'l***@example.com', true, true, timestamp '2026-07-01', 'legacy-provider-evidence',
                        '94803000-0000-0000-0000-000000000001', 'legacy-contact-94803', repeat('1', 64),
                        1, 'tests', timestamp '2026-07-01');

                SET session_replication_role = replica;
                INSERT INTO "DunningDeliveryAttempts"
                    ("Id", "BusinessUnitId", "DunningNoticeId", "ProviderEventId", "AttemptNumber",
                     "Status", "MaskedDestination", "ArtifactHash", "TemplateVersion", "ProviderReference",
                     "ProviderOccurredOn", "SignedEvidenceReference", "OccurredOn", "RecordedBy")
                VALUES (94804, 94801, 94899, '94804000-0000-0000-0000-000000000001', 1,
                        'Delivered', 'l***@example.com', repeat('2', 64), 'legacy-v1', 'legacy-provider-ref',
                        timestamp '2026-07-01', 'legacy-signed-evidence', timestamp '2026-07-01', 'tests');
                SET session_replication_role = origin;
                """);

            await migrator.MigrateAsync("20260723233000_GovernProviderEvidence");

            var unsignedRows = await context.Database.SqlQueryRaw<long>("""
                SELECT
                    (SELECT count(*) FROM "FinanceCommunicationContacts" WHERE "ProviderSignature" IS NULL) +
                    (SELECT count(*) FROM "DunningDeliveryAttempts" WHERE "ProviderSignature" IS NULL)
                    AS "Value"
                """).SingleAsync();
            Assert.Equal(2, unsignedRows);

            await migrator.MigrateAsync("20260723232000_GovernPromiseIdempotency");
            var secretTableSurvivedDowngrade = await context.Database.SqlQueryRaw<bool>("""
                SELECT to_regclass('public."FinanceProviderSecrets"') IS NOT NULL AS "Value"
                """).SingleAsync();
            Assert.True(secretTableSurvivedDowngrade);
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
    public async Task AllMigrationsApplyToAnEmptyPostgreSqlDatabase()
    {
        await using var context = _database.ContextFor(null);

        var pending = await context.Database.GetPendingMigrationsAsync();
        var applied = await context.Database.GetAppliedMigrationsAsync();

        Assert.Empty(pending);
        Assert.Contains("20260723120000_CompleteTenantRlsCoverage", applied);
        Assert.Contains("20260723130000_GovernExtractionReview", applied);
        Assert.Contains("20260723140000_AddAiGovernanceLedger", applied);

        await using var connection = await _database.OpenConnectionAsync();
        await using var roleCommand = connection.CreateCommand();
        roleCommand.CommandText = """
            SELECT NOT rolcanlogin AND NOT rolsuper AND NOT rolbypassrls
            FROM pg_roles WHERE rolname = 'nexora_tenant_app';
            """;
        Assert.True((bool)(await roleCommand.ExecuteScalarAsync())!);

        await using var removedMaintenanceRoleCommand = connection.CreateCommand();
        removedMaintenanceRoleCommand.CommandText =
            "SELECT NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_ai_maintenance');";
        Assert.True((bool)(await removedMaintenanceRoleCommand.ExecuteScalarAsync())!);

        var filteredTables = context.Model.GetEntityTypes()
            .Where(entity => entity.GetQueryFilter() is not null && (entity.GetSchema() ?? "public") == "public")
            .Select(entity => entity.GetTableName())
            .Where(table => table is not null)
            .Concat(new[]
            {
                "Attachments", "Contacts", "EmailIngests", "LeadItems", "OrderItems",
                "ProductAttachments", "QuoteItems", "RFQItems", "ShipmentItems",
                "ShipmentStatusHistory", "SupplierPurchaseHistory"
            })
            .Distinct(StringComparer.Ordinal)
            .OrderBy(table => table, StringComparer.Ordinal)
            .ToArray()!;

        await using var policyCommand = connection.CreateCommand();
        policyCommand.CommandText = """
            WITH expected(table_name) AS (SELECT unnest(@tables::text[]))
            SELECT string_agg(expected.table_name, ', ' ORDER BY expected.table_name)
            FROM expected
            LEFT JOIN pg_class table_definition ON table_definition.relname = expected.table_name
            LEFT JOIN pg_namespace schema_definition
                ON schema_definition.oid = table_definition.relnamespace
               AND schema_definition.nspname = 'public'
            LEFT JOIN pg_policy policy
                ON policy.polrelid = table_definition.oid
               AND policy.polname = 'nexora_tenant_isolation'
            LEFT JOIN pg_roles tenant_role ON tenant_role.rolname = 'nexora_tenant_app'
            WHERE schema_definition.oid IS NULL
               OR NOT table_definition.relrowsecurity
               OR policy.oid IS NULL
               OR policy.polqual IS NULL
               OR policy.polwithcheck IS NULL
               OR NOT tenant_role.oid = ANY(policy.polroles)
               OR position('nexora.business_unit_id' in pg_get_expr(policy.polqual, policy.polrelid)) = 0
               OR position('nexora.business_unit_id' in pg_get_expr(policy.polwithcheck, policy.polrelid)) = 0;
            """;
        policyCommand.Parameters.AddWithValue("tables", filteredTables);
        Assert.Null((await policyCommand.ExecuteScalarAsync()) as string);

        await using var forcedAiRlsCommand = connection.CreateCommand();
        forcedAiRlsCommand.CommandText = """
            SELECT string_agg(expected.table_name, ', ' ORDER BY expected.table_name)
            FROM unnest(ARRAY['AiProcessingPolicies', 'AiRequests', 'AiCallAttempts', 'AiBudgetPeriods'])
                AS expected(table_name)
            LEFT JOIN pg_class table_definition ON table_definition.relname = expected.table_name
            LEFT JOIN pg_namespace schema_definition
              ON schema_definition.oid = table_definition.relnamespace
             AND schema_definition.nspname = 'public'
            WHERE schema_definition.oid IS NULL
               OR NOT table_definition.relrowsecurity
               OR NOT table_definition.relforcerowsecurity;
            """;
        Assert.Null((await forcedAiRlsCommand.ExecuteScalarAsync()) as string);

        await using var provisioningPolicyCommand = connection.CreateCommand();
        provisioningPolicyCommand.CommandText = """
            SELECT policy.polwithcheck IS NOT NULL
                   AND position('ExternalProcessingAllowed' in pg_get_expr(policy.polwithcheck, policy.polrelid)) > 0
                   AND position('tenant-provisioning' in pg_get_expr(policy.polwithcheck, policy.polrelid)) > 0
            FROM pg_policy policy
            JOIN pg_class table_definition ON table_definition.oid = policy.polrelid
            WHERE table_definition.relname = 'AiProcessingPolicies'
              AND policy.polname = 'nexora_ai_default_provisioning';
            """;
        Assert.True((bool)(await provisioningPolicyCommand.ExecuteScalarAsync())!);

        await using var unresolvedIndexCommand = connection.CreateCommand();
        unresolvedIndexCommand.CommandText = """
            SELECT indexdef LIKE '%WHERE (("CompletedOn" IS NULL)%'
            FROM pg_indexes
            WHERE schemaname = 'public' AND indexname = 'IX_AiRequests_Unresolved_CreatedOn';
            """;
        Assert.True((bool)(await unresolvedIndexCommand.ExecuteScalarAsync())!);

        await using var tenantColumnCommand = connection.CreateCommand();
        tenantColumnCommand.CommandText = """
            SELECT string_agg(columns.table_name, ', ' ORDER BY columns.table_name)
            FROM information_schema.columns columns
            JOIN pg_class table_definition ON table_definition.relname = columns.table_name
            JOIN pg_namespace schema_definition
              ON schema_definition.oid = table_definition.relnamespace
             AND schema_definition.nspname = columns.table_schema
            WHERE columns.table_schema = 'public'
              AND columns.column_name = ANY(ARRAY[
                  'BusinessUnitID', 'BusinessUnitId', 'business_unit_id',
                  'BUID', 'Buid', 'buid'])
              AND NOT table_definition.relrowsecurity;
            """;
        Assert.Null((await tenantColumnCommand.ExecuteScalarAsync()) as string);

        await using var privilegeCommand = connection.CreateCommand();
        privilegeCommand.CommandText = """
            SELECT string_agg(table_definition.relname, ', ' ORDER BY table_definition.relname)
            FROM pg_class table_definition
            JOIN pg_namespace schema_definition ON schema_definition.oid = table_definition.relnamespace
            WHERE schema_definition.nspname = 'public'
              AND table_definition.relkind IN ('r', 'p')
              AND NOT table_definition.relrowsecurity
              AND table_definition.relname <> 'Module'
              AND (
                  has_table_privilege('nexora_tenant_app', table_definition.oid, 'SELECT')
                  OR has_table_privilege('nexora_tenant_app', table_definition.oid, 'INSERT')
                  OR has_table_privilege('nexora_tenant_app', table_definition.oid, 'UPDATE')
                  OR has_table_privilege('nexora_tenant_app', table_definition.oid, 'DELETE'));
            """;
        Assert.Null((await privilegeCommand.ExecuteScalarAsync()) as string);

        await using var modulePrivilegeCommand = connection.CreateCommand();
        modulePrivilegeCommand.CommandText = """
            SELECT has_table_privilege('nexora_tenant_app', 'public."Module"', 'SELECT')
               AND NOT has_table_privilege('nexora_tenant_app', 'public."Module"', 'INSERT, UPDATE, DELETE');
            """;
        Assert.True((bool)(await modulePrivilegeCommand.ExecuteScalarAsync())!);

        await using var deniedTableCommand = connection.CreateCommand();
        deniedTableCommand.CommandText = """
            SELECT table_definition.relrowsecurity,
                   has_table_privilege('nexora_tenant_app', 'public."__EFMigrationsHistory"', 'SELECT')
            FROM pg_class table_definition
            JOIN pg_namespace schema_definition ON schema_definition.oid = table_definition.relnamespace
            WHERE schema_definition.nspname = 'public' AND table_definition.relname = 'SetCountry';
            """;
        await using var privilegeReader = await deniedTableCommand.ExecuteReaderAsync();
        Assert.True(await privilegeReader.ReadAsync());
        Assert.True(privilegeReader.GetBoolean(0));
        Assert.False(privilegeReader.GetBoolean(1));
        await privilegeReader.DisposeAsync();

        await using var futureTableCommand = connection.CreateCommand();
        futureTableCommand.CommandText = """
            CREATE TABLE public.rls_privilege_canary (id bigint PRIMARY KEY);
            SELECT has_table_privilege('nexora_tenant_app', 'public.rls_privilege_canary', 'SELECT, INSERT, UPDATE, DELETE');
            """;
        Assert.False((bool)(await futureTableCommand.ExecuteScalarAsync())!);
        await using var dropFutureTableCommand = connection.CreateCommand();
        dropFutureTableCommand.CommandText = "DROP TABLE public.rls_privilege_canary;";
        await dropFutureTableCommand.ExecuteNonQueryAsync();

        await using var sequencePrivilegeCommand = connection.CreateCommand();
        sequencePrivilegeCommand.CommandText = """
            SELECT string_agg(sequence_definition.relname, ', ' ORDER BY sequence_definition.relname)
            FROM pg_class sequence_definition
            JOIN pg_namespace schema_definition ON schema_definition.oid = sequence_definition.relnamespace
            WHERE schema_definition.nspname = 'public'
              AND sequence_definition.relkind = 'S'
              AND sequence_definition.relname NOT IN ('CommercialCaseReferenceSequence', 'nexora_rfq_number_seq', 'nexora_supplier_po_doc_seq')
              AND CASE WHEN sequence_definition.relkind = 'S' THEN has_sequence_privilege(
                      'nexora_tenant_app',
                      format('%I.%I', schema_definition.nspname, sequence_definition.relname),
                      'USAGE, SELECT, UPDATE')
                  ELSE false END
              AND NOT EXISTS (
                  SELECT 1
                  FROM pg_depend dependency
                  JOIN pg_class table_definition ON table_definition.oid = dependency.refobjid
                  WHERE dependency.objid = sequence_definition.oid
                    AND dependency.deptype IN ('a', 'i')
                    AND table_definition.relrowsecurity);
            """;
        Assert.Null((await sequencePrivilegeCommand.ExecuteScalarAsync()) as string);

        await using var mutableSequenceCommand = connection.CreateCommand();
        mutableSequenceCommand.CommandText = """
            SELECT string_agg(sequence_definition.relname, ', ' ORDER BY sequence_definition.relname)
            FROM pg_class sequence_definition
            JOIN pg_namespace schema_definition ON schema_definition.oid = sequence_definition.relnamespace
            WHERE schema_definition.nspname = 'public'
              AND sequence_definition.relkind = 'S'
              AND CASE WHEN sequence_definition.relkind = 'S' THEN
                  has_sequence_privilege(
                      'nexora_tenant_app',
                      format('%I.%I', schema_definition.nspname, sequence_definition.relname),
                      'SELECT, UPDATE')
                  ELSE false END;
            """;
        Assert.Null((await mutableSequenceCommand.ExecuteScalarAsync()) as string);

        await using (var rfqSequenceTransaction = await connection.BeginTransactionAsync())
        {
            await using var rfqSequenceCommand = connection.CreateCommand();
            rfqSequenceCommand.Transaction = rfqSequenceTransaction;
            rfqSequenceCommand.CommandText = """
                SET LOCAL ROLE nexora_tenant_app;
                SELECT nextval('public.nexora_rfq_number_seq');
                """;
            Assert.True(Convert.ToInt64(await rfqSequenceCommand.ExecuteScalarAsync()) > 0);
            await rfqSequenceTransaction.RollbackAsync();
        }

        // The purchase-history PO document number is server-authoritative for the same reason
        // the RFQ number is: the tenant role must be able to draw from the sequence, and only
        // from the sequence, so two concurrent callers can never be issued the same number.
        await using (var poSequenceTransaction = await connection.BeginTransactionAsync())
        {
            await using var poSequenceCommand = connection.CreateCommand();
            poSequenceCommand.Transaction = poSequenceTransaction;
            poSequenceCommand.CommandText = """
                SET LOCAL ROLE nexora_tenant_app;
                SELECT nextval('public.nexora_supplier_po_doc_seq');
                """;
            var firstPoNumber = Convert.ToInt64(await poSequenceCommand.ExecuteScalarAsync());
            var secondPoNumber = Convert.ToInt64(await poSequenceCommand.ExecuteScalarAsync());
            Assert.True(firstPoNumber > 0);
            Assert.True(secondPoNumber > firstPoNumber);
            await poSequenceTransaction.RollbackAsync();
        }

        await using var futureSequenceCommand = connection.CreateCommand();
        futureSequenceCommand.CommandText = """
            CREATE SEQUENCE public.rls_sequence_canary;
            SELECT has_sequence_privilege('nexora_tenant_app', 'public.rls_sequence_canary', 'USAGE, SELECT, UPDATE');
            """;
        Assert.False((bool)(await futureSequenceCommand.ExecuteScalarAsync())!);
        await using var dropFutureSequenceCommand = connection.CreateCommand();
        dropFutureSequenceCommand.CommandText = "DROP SEQUENCE public.rls_sequence_canary;";
        await dropFutureSequenceCommand.ExecuteNonQueryAsync();
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ConcurrentWorkersClaimDistinctJobsAndRespectTenantCap()
    {
        var marker = Guid.NewGuid().ToString("N");
        const long businessUnitId = 91_001;
        var targetJobIds = new List<long>();

        await using (var seed = _database.ContextFor(null))
        {
            var queue = NewQueue(seed);
            for (var index = 0; index < 5; index++)
            {
                var result = await EnqueueGovernedJobAsync(seed, queue, $"{marker}-{index}", businessUnitId, 5);
                Assert.Equal(EnqueueOutcome.Enqueued, result.Outcome);
                targetJobIds.Add(result.JobId);
            }
            await seed.Set<ExtractionJob>()
                .Where(job => targetJobIds.Contains(job.Id))
                .ExecuteUpdateAsync(update => update.SetProperty(job => job.Priority, int.MaxValue));
        }

        var claims = await Task.WhenAll(Enumerable.Range(0, 4).Select(async index =>
        {
            await using var context = _database.ContextFor(null);
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var claim = await NewQueue(context).ClaimAsync(
                    $"worker-{marker}-{index}", TimeSpan.FromMinutes(5), 4);
                if (claim is not null) return claim;
                await Task.Delay(10);
            }
            return null;
        }));

        Assert.All(claims, claim => Assert.NotNull(claim));
        Assert.All(claims, claim => Assert.Equal(businessUnitId, claim!.BusinessUnitId));
        Assert.Equal(4, claims.Select(claim => claim!.Id).Distinct().Count());

        await using var capContext = _database.ContextFor(null);
        var cappedClaim = await NewQueue(capContext).ClaimAsync($"worker-{marker}-capped", TimeSpan.FromMinutes(5), 4);
        Assert.True(cappedClaim is null || cappedClaim.BusinessUnitId != businessUnitId);
        Assert.Equal(1, await capContext.Set<ExtractionJob>()
            .CountAsync(job => job.BusinessUnitId == businessUnitId && job.Status == ExtractionStatus.Pending));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task SlaDeadlineQueryExcludesPostgresInfinityWithoutIntegerYearCast()
    {
        const long businessUnitId = 91_051;
        var marker = Guid.NewGuid().ToString("N");
        await using var context = _database.ContextFor(null);
        Seed.EnsureBusinessUnit(context, businessUnitId);
        Seed.EmailConfig(context, businessUnitId, businessUnitId);
        Seed.EmailIngest(context, businessUnitId, businessUnitId, "NeedsReview");
        await context.SaveChangesAsync();
        await context.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO "Leads"
                ("RFQNo", "RecDate", "BidClosingDate", "LeadSource", "CreatedBy", "CreatedDate", "BusinessUnitID", "EmailIngestsID")
            VALUES
                ({{marker + "-finite"}}, now(), now() + interval '1 day', 'Test', 'tests', now(), {{businessUnitId}}, {{businessUnitId}}),
                ({{marker + "-infinity"}}, now(), 'infinity'::timestamp, 'Test', 'tests', now(), {{businessUnitId}}, {{businessUnitId}});
            """);

        var candidates = await SlaSweepWorker
            .OpenLeadDeadlineCandidates(context, businessUnitId, DateTime.UtcNow.AddDays(2))
            .Where(lead => lead.Rfqno != null && lead.Rfqno.StartsWith(marker))
            .Select(lead => lead.Rfqno)
            .ToListAsync();

        Assert.Equal(new[] { marker + "-finite" }, candidates);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task QueueTransitionsAreLeaseFencedAndRetryToDeadLetter()
    {
        var marker = Guid.NewGuid().ToString("N");
        const long businessUnitId = 91_101;

        await using var context = _database.ContextFor(null);
        var queue = NewQueue(context);

        var completedJobId = (await EnqueueGovernedJobAsync(context, queue, marker + "-complete", businessUnitId, 3)).JobId;
        var completedClaim = await queue.ClaimAsync("worker-a", TimeSpan.FromMinutes(5), 4);
        Assert.Equal(completedJobId, completedClaim!.Id);
        Assert.False(await queue.RenewLeaseAsync(completedJobId, "worker-b", completedClaim.Attempts, TimeSpan.FromMinutes(5)));
        Assert.False(await queue.SetStatusAsync(completedJobId, "worker-b", completedClaim.Attempts, ExtractionStatus.Extracting));
        Assert.False(await queue.FailAsync(completedJobId, "worker-b", completedClaim.Attempts, "not owner"));
        Assert.False(await queue.CompleteAsync(completedJobId, "worker-b", completedClaim.Attempts, 77_001));
        Assert.True(await queue.SetStatusAsync(completedJobId, "worker-a", completedClaim.Attempts, ExtractionStatus.Extracting));
        Assert.False(await queue.CompleteAsync(completedJobId, "worker-a", completedClaim.Attempts, 77_001));
        Assert.True(await queue.RenewLeaseAsync(completedJobId, "worker-a", completedClaim.Attempts, TimeSpan.FromMinutes(5)));
        Assert.True(await queue.SetStatusAsync(completedJobId, "worker-a", completedClaim.Attempts, ExtractionStatus.Persisting));
        Assert.False(await queue.SetStatusAsync(completedJobId, "worker-a", completedClaim.Attempts, ExtractionStatus.Extracting));
        Assert.True(await queue.CompleteAsync(completedJobId, "worker-a", completedClaim.Attempts, 77_001));

        var expiredJobId = (await EnqueueGovernedJobAsync(context, queue, marker + "-expired", businessUnitId, 2)).JobId;
        var expiredClaim = await queue.ClaimAsync("worker-a", TimeSpan.FromMinutes(5), 4);
        Assert.Equal(expiredJobId, expiredClaim!.Id);
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"ExtractionJobs\" SET \"LeaseExpiresAt\" = now() - INTERVAL '1 second' WHERE \"Id\" = {expiredJobId}");
        Assert.False(await queue.RenewLeaseAsync(expiredJobId, "worker-a", expiredClaim.Attempts, TimeSpan.FromMinutes(5)));

        var reclaimed = await queue.ClaimAsync("worker-b", TimeSpan.FromMinutes(5), 4);
        Assert.Equal(expiredJobId, reclaimed!.Id);
        Assert.Equal(2, reclaimed.Attempts);
        Assert.False(await queue.CompleteAsync(expiredJobId, "worker-a", expiredClaim.Attempts, 77_002));
        Assert.False(await queue.FailAsync(expiredJobId, "worker-a", expiredClaim.Attempts, "stale worker"));
        Assert.False(await queue.CompleteAsync(expiredJobId, "worker-b", expiredClaim.Attempts, 77_002));
        Assert.True(await queue.FailAsync(expiredJobId, "worker-b", reclaimed.Attempts, "poison document"));

        var deadLetter = await context.Set<ExtractionJob>().AsNoTracking().SingleAsync(job => job.Id == expiredJobId);
        Assert.Equal(ExtractionStatus.DeadLetter, deadLetter.Status);
        Assert.Equal("poison document", deadLetter.LastError);
        Assert.Null(deadLetter.LeasedBy);

        var permanentJobId = (await EnqueueGovernedJobAsync(
            context, queue, marker + "-permanent", businessUnitId, 5)).JobId;
        var permanentClaim = await queue.ClaimAsync("worker-permanent", TimeSpan.FromMinutes(5), 4);
        Assert.Equal(permanentJobId, permanentClaim!.Id);
        Assert.True(await queue.FailPermanentlyAsync(
            permanentJobId, "worker-permanent", permanentClaim.Attempts, "invalid spreadsheet"));
        var permanent = await context.Set<ExtractionJob>().AsNoTracking()
            .SingleAsync(job => job.Id == permanentJobId);
        Assert.Equal(ExtractionStatus.DeadLetter, permanent.Status);
        Assert.Equal(1, permanent.Attempts);
        Assert.Equal("invalid spreadsheet", permanent.LastError);

        var retryJobId = (await EnqueueGovernedJobAsync(context, queue, marker + "-retry", businessUnitId, 3)).JobId;
        var retryClaim = await queue.ClaimAsync("worker-c", TimeSpan.FromMinutes(5), 4);
        Assert.Equal(retryJobId, retryClaim!.Id);
        var failedAt = DateTime.UtcNow;
        Assert.True(await queue.FailAsync(retryJobId, "worker-c", retryClaim!.Attempts, "transient"));
        var retry = await context.Set<ExtractionJob>().AsNoTracking().SingleAsync(job => job.Id == retryJobId);
        Assert.Equal(ExtractionStatus.Pending, retry.Status);
        Assert.True(retry.NextAttemptAt > failedAt);
        Assert.Null(await queue.ClaimAsync("worker-d", TimeSpan.FromMinutes(5), 4));

        var crashedFinalJobId = (await EnqueueGovernedJobAsync(context, queue, marker + "-crashed-final", businessUnitId, 1)).JobId;
        var finalClaim = await queue.ClaimAsync("stable-worker-id", TimeSpan.FromMinutes(5), 4);
        Assert.Equal(crashedFinalJobId, finalClaim!.Id);
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"ExtractionJobs\" SET \"LeaseExpiresAt\" = now() - INTERVAL '1 second' WHERE \"Id\" = {crashedFinalJobId}");

        Assert.Null(await queue.ClaimAsync("stable-worker-id", TimeSpan.FromMinutes(5), 4));
        var crashedFinal = await context.Set<ExtractionJob>().AsNoTracking()
            .SingleAsync(job => job.Id == crashedFinalJobId);
        Assert.Equal(ExtractionStatus.DeadLetter, crashedFinal.Status);
        Assert.Equal(1, crashedFinal.Attempts);
        Assert.Null(crashedFinal.LeasedBy);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task CommercialCaseReferencesAreServerGeneratedUniqueAndImmutable()
    {
        var marker = Guid.NewGuid().ToString("N");
        const long businessUnitId = 92_001;
        const long emailIngestId = 92_001;

        await using (var connection = await _database.OpenConnectionAsync())
        await using (var seed = connection.CreateCommand())
        {
            seed.CommandText = """
                INSERT INTO "BusinessUnits" ("ID", "BusinessUnitCode", "BusinessUnitName", "CreatedBy", "CreatedOn")
                VALUES (92001, 'PGCERT', 'PostgreSQL Certification', 'tests', now());

                INSERT INTO "Email_Configurations"
                    ("ID", "BusinessUnitID", "ConfigurationName", "EmailAddress", "Protocol", "Host", "Port", "Username", "Password", "UseSSL", "PollingInterval", "IsActive", "CreatedOn")
                VALUES (92001, 92001, 'tests', 'tests@nexora.invalid', 'IMAP', 'localhost', 993, 'tests', 'tests', true, 300, false, now());

                INSERT INTO "EmailIngests"
                    ("ID", "MessageID", "FromEmail", "EmailConfigurationID", "CreatedOn")
                VALUES (92001, 'postgres-certification', 'buyer@nexora.invalid', 92001, now());
                """;
            await seed.ExecuteNonQueryAsync();
        }

        var inserts = Enumerable.Range(0, 12).Select(async index =>
        {
            await using var connection = await _database.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO "Leads"
                    ("RFQNo", "RecDate", "LeadSource", "CreatedBy", "CreatedDate", "BusinessUnitID", "EmailIngestsID")
                VALUES (@rfq, now(), 'IntegrationTest', 'tests', now(), @businessUnitId, @emailIngestId)
                RETURNING "ID", "CommercialCaseReference";
                """;
            command.Parameters.AddWithValue("rfq", $"{marker}-{index}");
            command.Parameters.AddWithValue("businessUnitId", businessUnitId);
            command.Parameters.AddWithValue("emailIngestId", emailIngestId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (Id: reader.GetInt64(0), Reference: reader.GetString(1));
        });

        var leads = await Task.WhenAll(inserts);

        Assert.Equal(12, leads.Select(lead => lead.Reference).Distinct().Count());
        Assert.All(leads, lead => Assert.StartsWith("NXR-", lead.Reference));

        await using var immutableConnection = await _database.OpenConnectionAsync();
        await using var immutableCommand = immutableConnection.CreateCommand();
        immutableCommand.CommandText = "UPDATE \"Leads\" SET \"CommercialCaseReference\" = 'FORGED' WHERE \"ID\" = @id;";
        immutableCommand.Parameters.AddWithValue("id", leads[0].Id);
        await Assert.ThrowsAsync<PostgresException>(() => immutableCommand.ExecuteNonQueryAsync());
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task TenantRoleAndCommandInterceptorEnforceRowLevelIsolation()
    {
        var marker = Guid.NewGuid().ToString("N");
        const long tenantOne = 93_001;
        const long tenantTwo = 93_002;

        await SeedRlsLeadsAsync(marker, tenantOne, tenantTwo);

        await using (var tenantContext = _database.TenantContextWithRls(tenantOne))
        {
            var visible = await tenantContext.Leads.IgnoreQueryFilters()
                .Where(lead => lead.Rfqno != null && lead.Rfqno.StartsWith(marker))
                .Select(lead => lead.BusinessUnitId)
                .ToListAsync();

            Assert.Equal(new[] { tenantOne }, visible);

            var visibleChildRows = await tenantContext.LeadItems.IgnoreQueryFilters()
                .CountAsync(item => item.LeadId == tenantOne || item.LeadId == tenantTwo);
            Assert.Equal(1, visibleChildRows);
        }

        // The RLS test pool has MaxPoolSize=1, so this second scope reuses the same
        // physical connection and proves transaction-local tenant/role state cannot leak.
        await using (var tenantContext = _database.TenantContextWithRls(tenantTwo))
        {
            var visible = await tenantContext.Leads.IgnoreQueryFilters()
                .Where(lead => lead.Rfqno != null && lead.Rfqno.StartsWith(marker))
                .Select(lead => lead.BusinessUnitId)
                .ToListAsync();

            Assert.Equal(new[] { tenantTwo }, visible);
        }

        await using (var tenantContext = _database.TenantContextWithRls(tenantOne))
        await using (var transaction = await tenantContext.Database.BeginTransactionAsync())
        {
            var visibleInsideServiceTransaction = await tenantContext.Leads.IgnoreQueryFilters()
                .CountAsync(lead => lead.Rfqno != null && lead.Rfqno.StartsWith(marker));
            Assert.Equal(1, visibleInsideServiceTransaction);
            await transaction.CommitAsync();
        }

        await using (var tenantContext = _database.TenantContextWithRls(tenantOne))
        {
            var childException = await Assert.ThrowsAsync<PostgresException>(() =>
                tenantContext.Database.ExecuteSqlRawAsync("""
                    INSERT INTO "LeadItems" ("LeadID", "Quantity") VALUES (93002, 1);
                    """));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, childException.SqlState);

            var exception = await Assert.ThrowsAsync<PostgresException>(() => tenantContext.Database.ExecuteSqlRawAsync("""
                INSERT INTO "Leads"
                    ("RFQNo", "RecDate", "LeadSource", "CreatedBy", "CreatedDate", "BusinessUnitID", "EmailIngestsID")
                VALUES ('rls-forged', now(), 'IntegrationTest', 'tests', now(), 93002, 93002);
                """));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
        }

        await using var connection = await _database.OpenConnectionAsync();
        await using var transactionWithoutTenant = await connection.BeginTransactionAsync();
        await using var noTenantCommand = connection.CreateCommand();
        noTenantCommand.Transaction = transactionWithoutTenant;
        noTenantCommand.CommandText = """
            SET LOCAL ROLE nexora_tenant_app;
            SELECT count(*) FROM "Leads" WHERE "RFQNo" LIKE @marker;
            """;
        noTenantCommand.Parameters.AddWithValue("marker", marker + "%");
        Assert.Equal(0L, (long)(await noTenantCommand.ExecuteScalarAsync())!);
        await transactionWithoutTenant.RollbackAsync();
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ExtractionJobs_are_read_and_write_isolated_by_runtime_rls()
    {
        const long tenantOne = 93_101;
        const long tenantTwo = 93_102;
        var marker = Guid.NewGuid().ToString("N");
        var hashOne = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(marker + "-one"))).ToLowerInvariant();
        var hashTwo = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(marker + "-two"))).ToLowerInvariant();

        await using (var owner = _database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(owner, tenantOne);
            Seed.EnsureBusinessUnit(owner, tenantTwo);
            owner.Set<ExtractionJob>().AddRange(
                Extraction(tenantOne, hashOne, marker + "-one.pdf"),
                Extraction(tenantTwo, hashTwo, marker + "-two.pdf"));
            await owner.SaveChangesAsync();
        }

        await using (var tenantContext = _database.TenantContextWithRls(tenantOne))
        {
            var visible = await tenantContext.Set<ExtractionJob>().IgnoreQueryFilters()
                .Where(job => job.FileName != null && job.FileName.StartsWith(marker))
                .Select(job => job.BusinessUnitId)
                .ToListAsync();
            Assert.Equal(new[] { tenantOne }, visible);

            var forgedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(marker + "-forged"))).ToLowerInvariant();
            var exception = await Assert.ThrowsAsync<PostgresException>(() =>
                tenantContext.Database.ExecuteSqlInterpolatedAsync($$"""
                    INSERT INTO "ExtractionJobs"
                        ("BatchId", "BusinessUnitId", "SourceType", "ContentHash", "StoragePath", "FileName",
                         "Status", "Priority", "SchedulerTag", "Attempts", "MaxAttempts", "NextAttemptAt", "CreatedOn", "UpdatedOn")
                    VALUES
                        ({{Guid.NewGuid()}}, {{tenantTwo}}, 'ManualUpload', {{forgedHash}}, {{"evidence/" + forgedHash}},
                         {{marker + "-forged.pdf"}}, 'Pending', 0, 0, 0, 5, now(), now(), now());
                    """));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
        }

        static ExtractionJob Extraction(long tenantId, string hash, string fileName) => new()
        {
            BatchId = Guid.NewGuid(),
            BusinessUnitId = tenantId,
            SourceType = ExtractionSourceType.ManualUpload,
            ContentHash = hash,
            StoragePath = "evidence/" + hash,
            FileName = fileName,
            Status = ExtractionStatus.Pending,
            MaxAttempts = 5,
            NextAttemptAt = DateTime.UtcNow,
            CreatedOn = DateTime.UtcNow,
            UpdatedOn = DateTime.UtcNow
        };
    }

    private async Task SeedRlsLeadsAsync(string marker, long tenantOne, long tenantTwo)
    {
        await using var connection = await _database.OpenConnectionAsync();
        await using var seed = connection.CreateCommand();
        seed.CommandText = """
            INSERT INTO "BusinessUnits" ("ID", "BusinessUnitCode", "BusinessUnitName", "CreatedBy", "CreatedOn")
            VALUES
                (@tenantOne, 'RLS1', 'RLS Tenant One', 'tests', now()),
                (@tenantTwo, 'RLS2', 'RLS Tenant Two', 'tests', now());

            INSERT INTO "Email_Configurations"
                ("ID", "BusinessUnitID", "ConfigurationName", "EmailAddress", "Protocol", "Host", "Port", "Username", "Password", "UseSSL", "PollingInterval", "IsActive", "CreatedOn")
            VALUES
                (@tenantOne, @tenantOne, 'rls-1', 'rls1@nexora.invalid', 'IMAP', 'localhost', 993, 'tests', 'tests', true, 300, false, now()),
                (@tenantTwo, @tenantTwo, 'rls-2', 'rls2@nexora.invalid', 'IMAP', 'localhost', 993, 'tests', 'tests', true, 300, false, now());

            INSERT INTO "EmailIngests" ("ID", "MessageID", "FromEmail", "EmailConfigurationID", "CreatedOn")
            VALUES
                (@tenantOne, @messageOne, 'buyer1@nexora.invalid', @tenantOne, now()),
                (@tenantTwo, @messageTwo, 'buyer2@nexora.invalid', @tenantTwo, now());

            INSERT INTO "Leads"
                ("ID", "RFQNo", "RecDate", "LeadSource", "CreatedBy", "CreatedDate", "BusinessUnitID", "EmailIngestsID")
            VALUES
                (@tenantOne, @rfqOne, now(), 'IntegrationTest', 'tests', now(), @tenantOne, @tenantOne),
                (@tenantTwo, @rfqTwo, now(), 'IntegrationTest', 'tests', now(), @tenantTwo, @tenantTwo);

            INSERT INTO "LeadItems" ("LeadID", "Quantity")
            VALUES (@tenantOne, 1), (@tenantTwo, 1);
            """;
        seed.Parameters.AddWithValue("tenantOne", tenantOne);
        seed.Parameters.AddWithValue("tenantTwo", tenantTwo);
        seed.Parameters.AddWithValue("messageOne", marker + "-message-1");
        seed.Parameters.AddWithValue("messageTwo", marker + "-message-2");
        seed.Parameters.AddWithValue("rfqOne", marker + "-rfq-1");
        seed.Parameters.AddWithValue("rfqTwo", marker + "-rfq-2");
        await seed.ExecuteNonQueryAsync();
    }

    private static async Task<EnqueueResult> EnqueueGovernedJobAsync(
        ErpRfqAutomationContext context, IExtractionQueue queue, string marker, long businessUnitId, int maxAttempts)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(marker))).ToLowerInvariant();
        var corpus = DocumentCorpus.Create(businessUnitId, Guid.NewGuid(), CorpusSourceType.ManualUpload);
        context.Set<DocumentCorpus>().Add(corpus);
        await context.SaveChangesAsync();
        var source = SourceDocument.Create(businessUnitId, corpus.Id, hash, marker + ".pdf", "application/pdf",
            "acceptance", marker, "v1", 1);
        context.Set<SourceDocument>().Add(source);
        await context.SaveChangesAsync();
        var occurrence = SourceDocumentOccurrence.Create(businessUnitId, source.Id, corpus.Id,
            "queue-test:" + marker, "{}");
        context.Set<SourceDocumentOccurrence>().Add(occurrence);
        await context.SaveChangesAsync();
        var result = await queue.EnqueueAsync(new EnqueueExtractionRequest
        {
            BusinessUnitId = businessUnitId,
            SourceDocumentOccurrenceId = occurrence.Id,
            SourceType = ExtractionSourceType.ManualUpload,
            StoragePath = "test://" + marker,
            ContentHash = hash,
            FileName = marker + ".pdf",
            FileType = "pdf",
            MaxAttempts = maxAttempts
        });
        Assert.Equal(EnqueueOutcome.Enqueued, result.Outcome);
        occurrence.BindExtractionJob(result.JobId);
        await context.SaveChangesAsync();
        return result;
    }

    private static ExtractionQueue NewQueue(ERP_RFQ_Automation.Models.ErpRfqAutomationContext context)
        => new(context, new NoopLogger<ExtractionQueue>());

    private sealed class QueryingQuoteLifecycle(ErpRfqAutomationContext context) : ILifecycleApplicationService
    {
        public async Task<LifecycleTransitionResult> TransitionQuoteInCurrentTransactionAsync(
            long businessUnitId, long quoteId, LifecycleActor actor, LifecycleTransitionCommand command,
            bool reopen, CancellationToken ct)
        {
            Assert.NotNull(context.Database.CurrentTransaction);
            await context.Quotes.SingleAsync(
                quote => quote.BusinessUnitId == businessUnitId && quote.Id == quoteId, ct);
            await context.SaveChangesAsync(ct);
            return null!;
        }

        public Task<LifecycleStateView> GetLeadStateAsync(long businessUnitId, long leadId, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<LifecycleStateView> GetRfqStateAsync(long businessUnitId, long rfqId, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<LifecycleStateView> GetQuoteStateAsync(long businessUnitId, long quoteId, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<LifecycleTransitionResult> TransitionLeadAsync(long businessUnitId, long leadId,
            LifecycleActor actor, LifecycleTransitionCommand command, bool reopen, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<LifecycleTransitionResult> TransitionRfqAsync(long businessUnitId, long rfqId,
            LifecycleActor actor, LifecycleTransitionCommand command, bool reopen, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<LifecycleTransitionResult> TransitionQuoteAsync(long businessUnitId, long quoteId,
            LifecycleActor actor, LifecycleTransitionCommand command, bool reopen, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<LifecycleTransitionResult> TransitionLeadInCurrentTransactionAsync(long businessUnitId,
            long leadId, LifecycleActor actor, LifecycleTransitionCommand command, bool reopen, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
