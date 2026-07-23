using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.Models;
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
                GRANT nexora_tenant_app TO {runtimeRole};
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
        }
        finally
        {
            await using var admin = await _database.OpenConnectionAsync();
            await using var drop = admin.CreateCommand();
            drop.CommandText = $"REVOKE nexora_tenant_app FROM {runtimeRole}; DROP ROLE IF EXISTS {runtimeRole};";
            await drop.ExecuteNonQueryAsync();
        }
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

        await using var truncate = connection.CreateCommand();
        truncate.CommandText = "TRUNCATE TABLE public.\"LeadReviewAudits\"";
        var truncateError = await Assert.ThrowsAsync<PostgresException>(() => truncate.ExecuteNonQueryAsync());
        Assert.Equal("55000", truncateError.SqlState);

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
              AND (
                  has_table_privilege('nexora_tenant_app', table_definition.oid, 'SELECT')
                  OR has_table_privilege('nexora_tenant_app', table_definition.oid, 'INSERT')
                  OR has_table_privilege('nexora_tenant_app', table_definition.oid, 'UPDATE')
                  OR has_table_privilege('nexora_tenant_app', table_definition.oid, 'DELETE'));
            """;
        Assert.Null((await privilegeCommand.ExecuteScalarAsync()) as string);

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
              AND sequence_definition.relname <> 'CommercialCaseReferenceSequence'
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

        await using (var seed = _database.ContextFor(null))
        {
            var queue = NewQueue(seed);
            for (var index = 0; index < 5; index++)
            {
                var result = await queue.EnqueueAsync(new EnqueueExtractionRequest
                {
                    BusinessUnitId = businessUnitId,
                    SourceType = ExtractionSourceType.ManualUpload,
                    StoragePath = $"test://{marker}/{index}",
                    ContentHash = $"{marker}{index}",
                    FileName = $"rfq-{index}.pdf",
                    FileType = "pdf"
                });
                Assert.Equal(EnqueueOutcome.Enqueued, result.Outcome);
            }
        }

        var claims = await Task.WhenAll(Enumerable.Range(0, 4).Select(async index =>
        {
            await using var context = _database.ContextFor(null);
            return await NewQueue(context).ClaimAsync($"worker-{marker}-{index}", TimeSpan.FromMinutes(5), 4);
        }));

        Assert.All(claims, claim => Assert.NotNull(claim));
        Assert.Equal(4, claims.Select(claim => claim!.Id).Distinct().Count());

        await using var capContext = _database.ContextFor(null);
        var cappedClaim = await NewQueue(capContext).ClaimAsync($"worker-{marker}-capped", TimeSpan.FromMinutes(5), 4);
        Assert.Null(cappedClaim);
        Assert.Equal(1, await capContext.Set<ExtractionJob>()
            .CountAsync(job => job.BusinessUnitId == businessUnitId && job.Status == ExtractionStatus.Pending));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task QueueTransitionsAreLeaseFencedAndRetryToDeadLetter()
    {
        var marker = Guid.NewGuid().ToString("N");
        const long businessUnitId = 91_101;

        await using var context = _database.ContextFor(null);
        var queue = NewQueue(context);

        var completedJobId = await EnqueueJobAsync(queue, marker + "-complete", businessUnitId, 3);
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

        var expiredJobId = await EnqueueJobAsync(queue, marker + "-expired", businessUnitId, 2);
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

        var retryJobId = await EnqueueJobAsync(queue, marker + "-retry", businessUnitId, 3);
        var retryClaim = await queue.ClaimAsync("worker-c", TimeSpan.FromMinutes(5), 4);
        Assert.Equal(retryJobId, retryClaim!.Id);
        var failedAt = DateTime.UtcNow;
        Assert.True(await queue.FailAsync(retryJobId, "worker-c", retryClaim!.Attempts, "transient"));
        var retry = await context.Set<ExtractionJob>().AsNoTracking().SingleAsync(job => job.Id == retryJobId);
        Assert.Equal(ExtractionStatus.Pending, retry.Status);
        Assert.True(retry.NextAttemptAt > failedAt);
        Assert.Null(await queue.ClaimAsync("worker-d", TimeSpan.FromMinutes(5), 4));

        var crashedFinalJobId = await EnqueueJobAsync(queue, marker + "-crashed-final", businessUnitId, 1);
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

    private static async Task<long> EnqueueJobAsync(
        IExtractionQueue queue, string marker, long businessUnitId, int maxAttempts)
    {
        var result = await queue.EnqueueAsync(new EnqueueExtractionRequest
        {
            BusinessUnitId = businessUnitId,
            SourceType = ExtractionSourceType.ManualUpload,
            StoragePath = "test://" + marker,
            ContentHash = marker.PadRight(64, '0')[..64],
            FileName = marker + ".pdf",
            FileType = "pdf",
            MaxAttempts = maxAttempts
        });
        Assert.Equal(EnqueueOutcome.Enqueued, result.Outcome);
        return result.JobId;
    }

    private static ExtractionQueue NewQueue(ERP_RFQ_Automation.Models.ErpRfqAutomationContext context)
        => new(context, new NoopLogger<ExtractionQueue>());
}
