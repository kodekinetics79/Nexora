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

    /// <summary>
    /// SQUASH NOTE — this replaces FinancialMigration_ClassifiesHistoricalQuoteArithmetic.
    ///
    /// That test built an isolated database at 20260723140000_AddAiGovernanceLedger, wrote two
    /// legacy quotes — one whose line total excluded tax and one whose total included it — and
    /// upgraded to 20260723150000_EnforceQuoteOrderFinancialIntegrity to prove the migration
    /// classified them as FinancialCalculationVersion 1 and 2 respectively rather than assuming one
    /// arithmetic for both and silently restating a customer's price. It then seeded a legacy role
    /// and upgraded to 20260723160000_AddCommercialFinanceLedger to prove the finance modules were
    /// granted to it.
    ///
    /// 20260811033109_SquashedSchemaBaseline erased all three ids. Both are one-time data
    /// migrations over rows that predate the columns, and neither can run again: the version column
    /// is NOT NULL with a store default, and the role-permission seed acted on roles that existed
    /// at that moment.
    ///
    /// What survives, and is asserted here, is the half that still governs every quote written
    /// today: the arithmetic version is recorded on the row, defaults to the CURRENT arithmetic
    /// (2), and is never null — so no quote is ever evaluated under an assumed convention. The
    /// version-1 population is closed and finite; the risk the migration was managing was
    /// misreading it, and a row that cannot omit its version cannot be misread.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Quote_arithmetic_version_is_recorded_on_every_quote_and_defaults_to_current()
    {
        await using var connection = await _database.OpenConnectionAsync();

        await using (var column = connection.CreateCommand())
        {
            column.CommandText = """
                SELECT is_nullable = 'NO' AND column_default LIKE '%2%'
                FROM information_schema.columns
                WHERE table_schema = 'public' AND table_name = 'Quotes'
                  AND column_name = 'FinancialCalculationVersion';
                """;
            Assert.True((bool)(await column.ExecuteScalarAsync())!);
        }

        await using var transaction = await connection.BeginTransactionAsync();
        await using (var seed = connection.CreateCommand())
        {
            seed.Transaction = transaction;
            seed.CommandText = """
                INSERT INTO "BusinessUnits"
                    ("ID", "BusinessUnitCode", "BusinessUnitName", "CreatedBy", "CreatedOn")
                VALUES (94001, 'FINMIG', 'Finance arithmetic', 'tests', now());
                INSERT INTO "Leads"
                    ("ID", "RFQNo", "RecDate", "LeadSource", "CreatedBy", "CreatedDate", "BusinessUnitID")
                VALUES (94001, 'FIN-LEAD', now(), 'Tests', 'tests', now(), 94001);
                INSERT INTO "RFQ"
                    ("ID", "RFQNo", "RecDate", "BusinessUnitID", "LeadID", "CommercialCaseID",
                     "NexoraSerial", "CreatedBy", "CreatedDate")
                SELECT 94001, 'FIN-RFQ', now(), 94001, 94001,
                       lead."CommercialCaseId", lead."CommercialCaseReference", 'tests', now()
                FROM "Leads" lead WHERE lead."ID" = 94001;
                INSERT INTO "Quotes"
                    ("ID", "QuoteNo", "BusinessUnitID", "RFQID", "CommercialCaseID", "NexoraSerial",
                     "CreatedBy", "CreatedDate", "TotalAmount")
                SELECT 94001, 'QT-DEFAULT-VERSION', 94001, 94001, rfq."CommercialCaseID",
                       rfq."NexoraSerial", 'tests', now(), 100
                FROM "RFQ" rfq WHERE rfq."ID" = 94001;
                """;
            await seed.ExecuteNonQueryAsync();
        }

        // The quote named no version, and landed on the current arithmetic rather than on 0 or null.
        await using (var version = connection.CreateCommand())
        {
            version.Transaction = transaction;
            version.CommandText = """SELECT "FinancialCalculationVersion" FROM "Quotes" WHERE "ID" = 94001;""";
            Assert.Equal(2, Convert.ToInt32(await version.ExecuteScalarAsync()));
        }

        await transaction.RollbackAsync();
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

    /// <summary>
    /// SQUASH NOTE — this replaces ProviderEvidenceMigration_PreservesUnsignedHistoricalRows.
    ///
    /// That test built a database at 20260723232000_GovernPromiseIdempotency, wrote a finance
    /// contact and a dunning delivery attempt from before provider signatures existed, and upgraded
    /// to 20260723233000_GovernProviderEvidence to prove the migration left both rows UNSIGNED
    /// rather than stamping them with a placeholder digest that would later read as cryptographic
    /// proof a provider never gave. It then migrated back down and checked the secrets table
    /// survived.
    ///
    /// 20260811033109_SquashedSchemaBaseline erased both ids, and with them the walk. The
    /// migration's rule did not go with them — it was written into the schema as two CHECK
    /// constraints, and those are what is asserted here, on the live catalogue and by trying to
    /// break them:
    ///
    ///   * ProviderSignature stays NULLABLE, so "nobody signed this" remains representable and no
    ///     future writer is forced to invent a value;
    ///   * but a signature that IS present must be a 64-character lowercase hex digest, so the
    ///     placeholder the migration refused to write cannot be written by anything else either.
    ///
    /// This is strictly more than the old test proved: it checked two specific historical rows
    /// were left alone, this checks that no row anywhere can carry a fabricated signature.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Provider_signatures_are_optional_but_never_fabricated()
    {
        await using var connection = await _database.OpenConnectionAsync();

        await using (var schema = connection.CreateCommand())
        {
            schema.CommandText = """
                SELECT
                    (SELECT count(*)::int FROM information_schema.columns
                     WHERE table_schema = 'public'
                       AND table_name IN ('FinanceCommunicationContacts', 'DunningDeliveryAttempts')
                       AND column_name = 'ProviderSignature'
                       AND is_nullable = 'YES') = 2,
                    (SELECT count(*)::int FROM pg_constraint
                     WHERE conname IN ('CK_FinanceCommunicationContacts_ProviderSignature',
                                       'CK_DunningDeliveryAttempts_ProviderSignature')
                       AND contype = 'c' AND convalidated
                       AND position('[0-9a-f]{64}' in pg_get_constraintdef(oid)) > 0) = 2,
                    to_regclass('public."FinanceProviderSecrets"') IS NOT NULL;
                """;
            await using var reader = await schema.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            for (var index = 0; index < 3; index++)
                Assert.True(reader.GetBoolean(index), $"Provider evidence assertion {index + 1} failed.");
        }

        // Now the two halves, on real rows.
        //
        // session_replication_role = replica disarms the signing TRIGGER but leaves CHECK
        // constraints in force. That separation is the whole point: it lets the historical shape
        // — a contact row carrying no provider signature — be created exactly as it existed before
        // the migration, and then shows that the CHECK still refuses a FABRICATED one. A test that
        // could not create the historical shape could not prove anything about it.
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var seed = connection.CreateCommand())
        {
            seed.Transaction = transaction;
            seed.CommandText = """
                INSERT INTO "BusinessUnits"
                    ("ID", "BusinessUnitCode", "BusinessUnitName", "CreatedBy", "CreatedOn")
                VALUES (94801, 'EVIDENCE', 'Provider evidence', 'tests', now());
                INSERT INTO "Customers"
                    ("ID", "Name", "ImageURL", "BUID", "CreatedBy", "CreatedOn", "ConcurrencyToken")
                VALUES (94802, 'Provider evidence customer', '', 94801, 'tests', now(), gen_random_uuid());
                SET LOCAL session_replication_role = replica;
                INSERT INTO "FinanceCommunicationContacts"
                    ("Id", "BusinessUnitId", "CustomerId", "Purpose", "Channel", "DestinationToken",
                     "MaskedDestination", "IsVerified", "IsActive", "EffectiveFrom",
                     "VerificationEvidenceReference", "VerificationProviderEventId", "IdempotencyKey",
                     "RequestHash", "Version", "CreatedBy", "CreatedOn")
                VALUES (94803, 94801, 94802, 'Collections', 'Email', 'vault:legacy-contact',
                        'l***@example.com', true, true, timestamp '2026-07-01', 'legacy-provider-evidence',
                        '94803000-0000-0000-0000-000000000001', 'legacy-contact-94803', repeat('1', 64),
                        1, 'tests', timestamp '2026-07-01');
                INSERT INTO "DunningDeliveryAttempts"
                    ("Id", "BusinessUnitId", "DunningNoticeId", "ProviderEventId", "AttemptNumber",
                     "Status", "MaskedDestination", "ArtifactHash", "TemplateVersion", "ProviderReference",
                     "ProviderOccurredOn", "SignedEvidenceReference", "OccurredOn", "RecordedBy")
                VALUES (94804, 94801, 94899, '94804000-0000-0000-0000-000000000001', 1,
                        'Delivered', 'l***@example.com', repeat('2', 64), 'legacy-v1', 'legacy-provider-ref',
                        timestamp '2026-07-01', 'legacy-signed-evidence', timestamp '2026-07-01', 'tests');
                """;
            await seed.ExecuteNonQueryAsync();
        }

        // BOTH historical rows exist and BOTH are unsigned. Nothing stamped either. The delivery
        // attempt is the second table the migration had to leave alone and is re-seeded here after
        // an earlier revision of this test dropped it from the behavioural half.
        await using (var unsigned = connection.CreateCommand())
        {
            unsigned.Transaction = transaction;
            unsigned.CommandText = """
                SELECT (SELECT "ProviderSignature" IS NULL FROM "FinanceCommunicationContacts" WHERE "Id" = 94803)
                   AND (SELECT "ProviderSignature" IS NULL FROM "DunningDeliveryAttempts" WHERE "Id" = 94804);
                """;
            Assert.True((bool)(await unsigned.ExecuteScalarAsync())!);
        }

        // A placeholder is refused by the CHECK, so the value the migration declined to write
        // cannot be written by anything else either.
        await transaction.SaveAsync("fabricated");
        await using (var fabricated = connection.CreateCommand())
        {
            fabricated.Transaction = transaction;
            fabricated.CommandText = """
                UPDATE "FinanceCommunicationContacts" SET "ProviderSignature" = 'unverified'
                WHERE "Id" = 94803;
                """;
            var error = await Assert.ThrowsAsync<PostgresException>(() => fabricated.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        }
        await transaction.RollbackAsync("fabricated");

        // The same refusal on the delivery attempt — a separate table with its own constraint, and
        // the one an operator is most tempted to backfill because a delivery either happened or it
        // did not.
        await transaction.SaveAsync("fabricated_attempt");
        await using (var fabricatedAttempt = connection.CreateCommand())
        {
            fabricatedAttempt.Transaction = transaction;
            fabricatedAttempt.CommandText = """
                UPDATE "DunningDeliveryAttempts" SET "ProviderSignature" = 'unverified'
                WHERE "Id" = 94804;
                """;
            var error = await Assert.ThrowsAsync<PostgresException>(() => fabricatedAttempt.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        }
        await transaction.RollbackAsync("fabricated_attempt");

        // …while a well-formed digest is accepted, so the constraint is discriminating rather than
        // simply refusing every write.
        await transaction.SaveAsync("wellformed");
        await using (var wellFormed = connection.CreateCommand())
        {
            wellFormed.Transaction = transaction;
            wellFormed.CommandText = """
                UPDATE "FinanceCommunicationContacts" SET "ProviderSignature" = repeat('a', 64)
                WHERE "Id" = 94803;
                """;
            Assert.Equal(1, await wellFormed.ExecuteNonQueryAsync());
        }
        await transaction.RollbackAsync("wellformed");

        // And with the triggers ARMED, the unsigned shape cannot be created at all: the write path
        // is fail-closed on provider evidence, so the unsigned population is closed and finite —
        // which is what made leaving those rows alone the safe choice rather than a gap. The guard
        // reports a missing verification secret (55000) or an invalid signature (23514) depending
        // on whether a secret happens to be configured; both are the same refusal.
        await using (var armed = connection.CreateCommand())
        {
            armed.Transaction = transaction;
            armed.CommandText = """
                SET LOCAL session_replication_role = origin;
                INSERT INTO "FinanceCommunicationContacts"
                    ("Id", "BusinessUnitId", "CustomerId", "Purpose", "Channel", "DestinationToken",
                     "MaskedDestination", "IsVerified", "IsActive", "EffectiveFrom",
                     "VerificationEvidenceReference", "VerificationProviderEventId", "IdempotencyKey",
                     "RequestHash", "Version", "CreatedBy", "CreatedOn")
                VALUES (94804, 94801, 94802, 'Collections', 'Email', 'vault:new-contact',
                        'n***@example.com', true, true, timestamp '2026-07-02', 'new-provider-evidence',
                        '94804000-0000-0000-0000-000000000001', 'new-contact-94804', repeat('2', 64),
                        1, 'tests', timestamp '2026-07-02');
                """;
            var error = await Assert.ThrowsAsync<PostgresException>(() => armed.ExecuteNonQueryAsync());
            Assert.Contains(error.SqlState,
                new[] { PostgresErrorCodes.ObjectNotInPrerequisiteState, PostgresErrorCodes.CheckViolation });
        }

        await transaction.RollbackAsync();
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task AllMigrationsApplyToAnEmptyPostgreSqlDatabase()
    {
        await using var context = _database.ContextFor(null);

        var pending = await context.Database.GetPendingMigrationsAsync();
        var applied = await context.Database.GetAppliedMigrationsAsync();

        Assert.Empty(pending);

        // Model-versus-migration drift, which GetPendingMigrationsAsync cannot see. That call
        // compares migrations *authored* against migrations *applied*, so a table that exists only
        // in the C# model — mapped in OnModelCreating and never migrated — passes it cleanly.
        //
        // The two lanes build their schema from different sources and therefore disagree silently:
        // the portable lane calls EnsureCreated(), which derives the schema from the model, so an
        // unmigrated table exists there and every test is green. Production and this lane run the
        // migrations, where the table simply is not there and every query against it raises 42P01.
        //
        // That is how an entire gate's schema — seven tables across inbound logistics and
        // traceability — reached a green build while goods receipt was dead on PostgreSQL. This
        // assertion is the guard that makes the drift fail here instead of in production.
        Assert.False(context.Database.HasPendingModelChanges(),
            "The EF model has changes no migration reflects. Author the migration before merging: "
            + "the portable lane builds its schema from the model and will not catch this.");
        // Squash note: three specific ids used to be named here
        // ('..._CompleteTenantRlsCoverage', '..._GovernExtractionReview', '..._AddAiGovernanceLedger').
        // 20260811033109_SquashedSchemaBaseline erased them, and naming the baseline's own id would
        // only break again on the next migration. The property those three were standing in for —
        // that this database was built by applying migrations rather than by EnsureCreated —
        // is asserted directly.
        Assert.NotEmpty(applied);

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

        // Inverted by 20260811110019_DropAiDefaultProvisioningPolicy. This used to assert that
        // nexora_ai_default_provisioning EXISTED. It was a second PERMISSIVE policy on a table
        // that already had nexora_tenant_isolation, and permissive policies OR — so its nine
        // pinned constants became an alternative route past the isolation predicate, which it
        // did not pin BusinessUnitId in. The row it was written for is created by the
        // SECURITY DEFINER trigger nexora_create_default_ai_policy(), where the effective role
        // is the function owner, so the policy was never the thing admitting that write. The
        // assertion is now that AiProcessingPolicies carries EXACTLY ONE permissive policy that a
        // REQUEST can reach, and that it is the tenant-isolation one.
        //
        // The reachability filter arrived with 20260811154500_TenantPurgeExecutionRole, which adds
        // nexora_tenant_purge TO nexora_purge_app to every tenant table so the offboarding sweep
        // can reach the ones declared FORCE. It is a tightening, not an exemption: the hazard the
        // dropped policy carried was that it ORed into the decision for a role a request executes
        // under (it was TO PUBLIC). nexora_purge_app is NOLOGIN, granted only to the migration
        // role, and none of the three execution roles is a member of it, so nothing on a request
        // path can execute under it. A policy added TO PUBLIC or TO any request-path role still
        // fails here, which is the whole of what this was defending.
        //
        // The two named exemptions arrived with
        // 20260811210000_TenantProvisioningSeedsUnderForcedRowSecurity. It adds two policies TO
        // PUBLIC so that nexora_create_default_ai_policy() — SECURITY DEFINER, and therefore
        // running as the schema owner — can write the row it exists to write on an owner that
        // does not bypass RLS. TO PUBLIC is forced, not chosen: a SECURITY DEFINER function
        // cannot change role, measured both as `SET LOCAL ROLE` and as a `SET role TO` clause, so
        // no role list can name it and the only alternative is the owner's per-deployment name.
        //
        // Exempted BY NAME. An earlier draft exempted by shape — "the predicate mentions
        // CURRENT_USER and pg_get_userbyid(seeder.proowner)" — and that was a strict weakening
        // with a working exploit: a substring test cannot tell a conjunct from a dead disjunct, so
        // `WITH CHECK (<anything> OR CURRENT_USER = (SELECT ...))` passed while admitting exactly
        // the cross-tenant INSERT 20260811110019 was written to close. A name list stays total for
        // anything new; the pin below covers the case a name list cannot, an edit to a policy that
        // keeps its name. The full treatment, including the behavioural proof, lives in
        // AiProcessingPolicyTenantIsolationPostgreSqlTests.
        await using var provisioningPolicyCommand = connection.CreateCommand();
        provisioningPolicyCommand.CommandText = """
            SELECT string_agg(policy.polname, ', ' ORDER BY policy.polname)
            FROM pg_policy policy
            JOIN pg_class table_definition ON table_definition.oid = policy.polrelid
            JOIN pg_namespace schema_definition
              ON schema_definition.oid = table_definition.relnamespace
             AND schema_definition.nspname = 'public'
            WHERE table_definition.relname = 'AiProcessingPolicies'
              AND policy.polpermissive
              AND (policy.polroles = '{0}'::oid[]
                   OR EXISTS (
                       SELECT 1
                       FROM unnest(policy.polroles) AS admitted(role_oid)
                       CROSS JOIN pg_roles request_role
                       WHERE request_role.rolname IN (
                                 'nexora_tenant_app', 'nexora_identity_app', 'nexora_pipeline_app')
                         AND pg_has_role(request_role.oid, admitted.role_oid, 'USAGE')))
              AND policy.polname NOT IN ('nexora_ai_default_policy_seed_read',
                                         'nexora_ai_default_policy_seed_write');
            """;
        Assert.Equal("nexora_tenant_isolation",
            (await provisioningPolicyCommand.ExecuteScalarAsync()) as string);

        // The two exempted policies pinned to their exact deparsed predicate and command, so that
        // an AND->OR edit of either fence — same name, same substrings, and it leaks — moves this
        // digest. So does widening FOR SELECT to FOR ALL, dropping one of the pair, or loosening
        // the tenant pin. Constants are md5 of pg_get_expr output on this fixture's connection
        // (default search_path, postgres:16-alpine). A deparse change on a PostgreSQL upgrade
        // fails this and wants re-recording, which is the correct direction for a tripwire.
        await using var seedPolicyPinCommand = connection.CreateCommand();
        seedPolicyPinCommand.CommandText = """
            SELECT string_agg(
                       policy.polname || ':' || policy.polcmd::text || ':' ||
                       md5(coalesce(pg_get_expr(policy.polqual, policy.polrelid), '')
                        || coalesce(pg_get_expr(policy.polwithcheck, policy.polrelid), '')),
                       ', ' ORDER BY policy.polname)
            FROM pg_policy policy
            JOIN pg_class table_definition ON table_definition.oid = policy.polrelid
            JOIN pg_namespace schema_definition
              ON schema_definition.oid = table_definition.relnamespace
             AND schema_definition.nspname = 'public'
            WHERE table_definition.relname = 'AiProcessingPolicies'
              AND policy.polname IN ('nexora_ai_default_policy_seed_read',
                                     'nexora_ai_default_policy_seed_write');
            """;
        Assert.Equal(
            "nexora_ai_default_policy_seed_read:r:151354757dd6a274a2635be36b6ff046, "
          + "nexora_ai_default_policy_seed_write:a:678997dac075447f78592d5998530ebd",
            (await seedPolicyPinCommand.ExecuteScalarAsync()) as string);

        // No request-path role holds the privileges of either seeder function's owner, which is
        // what makes the exclusion above an exclusion and not a hole. Both seeders are checked:
        // public.nexora_create_default_ai_policy() writes public."AiProcessingPolicies", and
        // platform.nexora_seed_tenant_meter_source_policies() writes
        // platform."TenantMeterSourcePolicies" on the same tenant-creation journey.
        await using var seederOwnerCommand = connection.CreateCommand();
        seederOwnerCommand.CommandText = """
            SELECT count(*)
            FROM pg_roles request_role
            CROSS JOIN unnest(ARRAY[
                     'public.nexora_create_default_ai_policy()',
                     'platform.nexora_seed_tenant_meter_source_policies()'
                 ]) AS seeder(signature)
            WHERE request_role.rolname IN (
                      'nexora_tenant_app', 'nexora_identity_app', 'nexora_pipeline_app')
              AND pg_has_role(
                      request_role.oid,
                      (SELECT proowner FROM pg_proc WHERE oid = seeder.signature::regprocedure),
                      'USAGE');
            """;
        Assert.Equal(0L, (long)(await seederOwnerCommand.ExecuteScalarAsync())!);

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

        // The inverse assertion, and the one that was missing. Everything above proves a table
        // WITHOUT row-level security holds no grant. Nothing proved that a table WITH row-level
        // security holds one — and a policy without a grant is not a tighter boundary, it is a
        // table nobody can read: PostgreSQL raises 42501 on the grant check before it ever
        // evaluates a row predicate.
        //
        // That gap is not hypothetical. Three tenant tables shipped in one gate with a correct
        // nexora_tenant_isolation policy and no GRANT, and every test passed, because the RLS
        // assertions look for the policy and the privilege assertions only look in the negative
        // direction. It surfaced when a pricing path finally read one of them and every request
        // returned 500 — including price attestation, which would have failed the first time a
        // tenant attested a price in production.
        //
        // The schema is deny-by-default: CompleteTenantRlsCoverage revoked the schema default
        // privileges deliberately, so every new tenant table needs an explicit grant and this
        // assertion is what makes forgetting one fail here instead of in front of a customer.
        await using var rlsWithoutGrantCommand = connection.CreateCommand();
        rlsWithoutGrantCommand.CommandText = """
            SELECT string_agg(table_definition.relname, ', ' ORDER BY table_definition.relname)
            FROM pg_class table_definition
            JOIN pg_namespace schema_definition ON schema_definition.oid = table_definition.relnamespace
            WHERE schema_definition.nspname = 'public'
              AND table_definition.relkind IN ('r', 'p')
              AND table_definition.relrowsecurity
              -- Deliberately denied to the tenant role, not accidentally ungranted. This table has
              -- no EF entity and no query filter; it is written and read only by a privileged role,
              -- and CompleteLedgerKernelControls explicitly REVOKEs it from nexora_tenant_app. A
              -- table can legitimately carry row-level security AND be unreachable by the tenant
              -- role — the defect this assertion hunts is the opposite: a table the application
              -- genuinely reads, whose policy makes it look isolated while the missing grant makes
              -- it unreadable. Anything added here must carry that same explicit REVOKE.
              AND table_definition.relname <> 'LedgerActorNonces'
              AND NOT has_table_privilege('nexora_tenant_app', table_definition.oid, 'SELECT');
            """;
        var rlsWithoutGrant = (await rlsWithoutGrantCommand.ExecuteScalarAsync()) as string;
        Assert.True(rlsWithoutGrant is null,
            "These tables have row-level security and no SELECT grant to nexora_tenant_app, so every "
            + "query against them raises 42501 before any row predicate runs. Add the GRANT in the "
            + $"same migration that creates the table: {rlsWithoutGrant}");

        // The OTHER direction, and the one that let a real defect through. Every assertion above
        // this point is satisfied by granting MORE: the under-granting guard passes the moment a
        // table holds SELECT, and the negative guard only looks at tables without row-level
        // security. Nothing anywhere asserted that a table holds no MORE than it should.
        //
        // So when 20260810050406 granted SELECT, INSERT, UPDATE, DELETE on all fifteen tables it
        // created — in one blanket statement, including five append-only ledgers — the lane was
        // structurally incapable of seeing it. DELETE on delivery_proofs is the sharp end: the
        // proof lines cascade from the header, and their accepted quantities are what caps the
        // invoice, so deleting a POD un-caps the ceiling and the customer can be billed for goods
        // they refused. The wiring contract had said in terms that DELETE must not be granted on
        // inventory_reorder_alerts; the migration granted it anyway and nothing objected.
        //
        // This is a declared inventory rather than something derived from the schema, because
        // "append-only" is a domain fact about what the row MEANS and no amount of catalogue
        // inspection can recover it. Each entry is a verb some migration deliberately REVOKEd. If a
        // future migration grants one back — by naming the table, or by a blanket GRANT over a list
        // — this test names it. Removing an entry is a decision to be argued in a pull request, not
        // a side effect of a grant statement written three files away.
        var forbiddenPrivileges = new (string Table, string Privilege, string Why)[]
        {
            // Gate 5-8 ledgers. The five that follow are the defect this guard exists for.
            ("delivery_proofs", "DELETE", "deleting a POD un-caps the invoice ceiling via its cascaded lines"),
            ("delivery_proof_lines", "DELETE", "accepted quantity is what an invoice is raised against"),
            ("delivery_shortfall_decisions", "DELETE", "one decision per shortfall is the append-only guarantee"),
            ("inventory_reorder_alerts", "DELETE", "an alert is resolved by a status transition, never removed"),
            ("material_lot_consumptions", "DELETE", "the lot-to-issue link is what traceability is reconstructed from"),
            ("QuoteRemovalRecords", "DELETE", "a record of a removal that can itself be removed records nothing"),
            ("MasterDataChangeEvents", "DELETE", "field-level audit trail"),
            ("MasterDataChangeEvents", "UPDATE", "the only reason to update it is to change what it says happened"),
            ("MasterDataFieldChanges", "DELETE", "field-level audit trail"),
            ("MasterDataFieldChanges", "UPDATE", "the only reason to update it is to change what it says happened"),

            // Pre-existing REVOKEs from earlier gates, none of which was certified until now.
            ("procurement_events", "UPDATE", "append-only command log"),
            ("procurement_events", "DELETE", "append-only command log"),
            ("goods_receipts", "UPDATE", "a receipt is corrected by a further receipt"),
            ("goods_receipts", "DELETE", "a receipt is corrected by a further receipt"),
            ("goods_receipt_lines", "UPDATE", "received quantity drives the three-way match"),
            ("goods_receipt_lines", "DELETE", "received quantity drives the three-way match"),
            ("customer_quote_sourcing_decisions", "UPDATE", "the priced decision a customer quote was built on"),
            ("customer_quote_sourcing_decisions", "DELETE", "the priced decision a customer quote was built on"),
            ("commercial_demand_lines", "UPDATE", "demand lineage"),
            ("commercial_demand_lines", "DELETE", "demand lineage"),
            ("supplier_purchase_orders", "DELETE", "a PO is cancelled, not erased"),
            ("supplier_purchase_order_lines", "DELETE", "a PO is cancelled, not erased"),
            ("supplier_quotes", "DELETE", "the offer a decision was made against"),
            ("sourcing_cases", "DELETE", "the case spine"),
            ("procurement_outbox", "DELETE", "at-least-once dispatch evidence"),
            ("Suppliers", "DELETE", "a supplier is deactivated, not erased"),
            ("OrderToCashDocumentCounters", "DELETE", "deleting a counter restarts a legal document series"),
            ("OrderToCashAuditEvents", "INSERT", "written by a privileged role only"),
            ("OrderToCashAuditEvents", "UPDATE", "audit trail"),
            ("OrderToCashAuditEvents", "DELETE", "audit trail"),
            ("LegalDocumentCounters", "INSERT", "a legal series is allocated by a privileged path only"),
            ("LegalDocumentCounters", "UPDATE", "a legal series is allocated by a privileged path only"),
            ("LegalDocumentCounters", "DELETE", "a legal series is allocated by a privileged path only"),
            ("LedgerAccounts", "DELETE", "double-entry ledger"),
            ("AccountingPeriods", "DELETE", "double-entry ledger"),
            ("JournalEntries", "DELETE", "a journal entry is reversed, not deleted"),
            ("JournalEntryLines", "DELETE", "a journal entry is reversed, not deleted"),
            // UPDATE is deliberately NOT on this list. CompleteLedgerKernelControls revoked it, and
            // GovernTreasuryRulesAdjustmentsAndCashBridge granted it back on purpose so the
            // receivables control account and unapplied-cash account can be set once. The revoke
            // that reads like the current state is in that migration's Down(). The grant is safe
            // because nexora_gl_guard_book bounds it: DELETE always raises, and an UPDATE is
            // accepted only when both control accounts move from NULL to two distinct, active,
            // correctly-categorised accounts, Version goes up by exactly one, and every other
            // column is unchanged. That is a trigger doing what a grant cannot express.
            ("LedgerBooks", "DELETE", "the book a period was closed against"),
            ("BankStatementImports", "UPDATE", "an imported statement is what reconciliation is evidence against"),
            ("BankStatements", "UPDATE", "an imported statement is what reconciliation is evidence against"),
            ("BankStatementLines", "UPDATE", "an imported statement is what reconciliation is evidence against"),
            ("ReconciliationAllocations", "UPDATE", "an allocation is reversed by a further allocation"),
        };

        await using var overGrantCommand = connection.CreateCommand();
        overGrantCommand.CommandText = """
            WITH forbidden(table_name, privilege) AS (
                SELECT unnest(@tables::text[]), unnest(@privileges::text[])
            )
            SELECT string_agg(
                       forbidden.table_name || '.' || forbidden.privilege,
                       ', ' ORDER BY forbidden.table_name, forbidden.privilege)
            FROM forbidden
            JOIN pg_class table_definition ON table_definition.relname = forbidden.table_name
            JOIN pg_namespace schema_definition
              ON schema_definition.oid = table_definition.relnamespace
             AND schema_definition.nspname = 'public'
            WHERE has_table_privilege('nexora_tenant_app', table_definition.oid, forbidden.privilege);
            """;
        overGrantCommand.CommandText = """
            WITH forbidden(table_name, privilege) AS (
                SELECT unnest(@tables::text[]), unnest(@privileges::text[])
            )
            SELECT string_agg(
                       forbidden.table_name || '.' || forbidden.privilege
                         || ' [acl=' || coalesce(array_to_string(table_definition.relacl, ' '), 'default') || ']',
                       ', ' ORDER BY forbidden.table_name, forbidden.privilege)
            FROM forbidden
            JOIN pg_class table_definition ON table_definition.relname = forbidden.table_name
            JOIN pg_namespace schema_definition
              ON schema_definition.oid = table_definition.relnamespace
             AND schema_definition.nspname = 'public'
            WHERE has_table_privilege('nexora_tenant_app', table_definition.oid, forbidden.privilege);
            """;
        overGrantCommand.Parameters.AddWithValue(
            "tables", forbiddenPrivileges.Select(entry => entry.Table).ToArray());
        overGrantCommand.Parameters.AddWithValue(
            "privileges", forbiddenPrivileges.Select(entry => entry.Privilege).ToArray());
        var overGranted = (await overGrantCommand.ExecuteScalarAsync()) as string;
        Assert.True(overGranted is null,
            "nexora_tenant_app holds a privilege on an append-only ledger that a migration "
            + "deliberately REVOKEd. A blanket GRANT over a list of tables is how this happens — "
            + "grant the verbs each table actually needs, per table. Over-granted: " + overGranted);

        // The tables above are protected by a grant, which the table OWNER bypasses. The two that
        // carry a field-level before/after trail are protected by a trigger as well, which nobody
        // bypasses — four separate code comments assert this trigger exists, including one that
        // justifies a CASCADE delete on the grounds that "the header itself can never be deleted
        // (append-only trigger)". It did not exist until 20260810110923. Assert the thing the
        // comments claim, so the claim and the schema cannot drift apart again.
        await using var auditTriggerCommand = connection.CreateCommand();
        auditTriggerCommand.CommandText = """
            SELECT string_agg(expected.table_name, ', ' ORDER BY expected.table_name)
            FROM unnest(ARRAY['MasterDataChangeEvents', 'MasterDataFieldChanges'])
                AS expected(table_name)
            LEFT JOIN pg_class table_definition ON table_definition.relname = expected.table_name
            LEFT JOIN pg_namespace schema_definition
              ON schema_definition.oid = table_definition.relnamespace
             AND schema_definition.nspname = 'public'
            LEFT JOIN pg_trigger trigger_definition
              ON trigger_definition.tgrelid = table_definition.oid
             AND trigger_definition.tgname = 'trg_master_data_audit_append_only'
             AND NOT trigger_definition.tgisinternal
            WHERE trigger_definition.oid IS NULL
               OR trigger_definition.tgenabled <> 'O'
               -- tgtype bits, per PostgreSQL's TRIGGER_TYPE_* macros: 1 = FOR EACH ROW,
               -- 2 = BEFORE, 8 = DELETE, 16 = UPDATE. A BEFORE ... FOR EACH ROW trigger on
               -- UPDATE OR DELETE is therefore exactly 27. AFTER would let the row change and
               -- then raise, and a statement-level trigger would not see the rows at all.
               OR (trigger_definition.tgtype & 16) = 0
               OR (trigger_definition.tgtype & 8) = 0
               OR (trigger_definition.tgtype & 2) = 0
               OR (trigger_definition.tgtype & 1) = 0;
            """;
        var missingAuditTrigger = (await auditTriggerCommand.ExecuteScalarAsync()) as string;
        Assert.True(missingAuditTrigger is null,
            "trg_master_data_audit_append_only is missing, disabled, or does not fire BEFORE both "
            + "UPDATE and DELETE. Four code comments state that this trigger is what makes the "
            + $"master-data audit trail immutable: {missingAuditTrigger}");

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

    // SEC-ING-02: the tenant context is mandatory. Every context passed here is built with a null
    // tenant (the cross-tenant worker view), so the queue gets the matching null-tenant StubTenant
    // and takes the deliberate nexora_pipeline_app role — the same pairing the explicit
    // ExtractionQueue construction in the runtime-role test above uses.
    private static ExtractionQueue NewQueue(ERP_RFQ_Automation.Models.ErpRfqAutomationContext context)
        => new(context, new NoopLogger<ExtractionQueue>(), new StubTenant(null));

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
