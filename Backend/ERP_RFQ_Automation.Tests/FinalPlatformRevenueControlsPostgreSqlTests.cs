using ERP_RFQ_Automation.Billing;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Independent production-dialect certification for the final platform revenue migration.
/// The tests deliberately write through PostgreSQL execution roles and raw SQL so application
/// validation cannot conceal a missing grant, RLS policy, constraint, or ALWAYS trigger.
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class FinalPlatformRevenueControlsPostgreSqlTests(PostgreSqlTestDatabase database)
{
    private const string PreviousMigration = "20260808210430_Wave6BillingCutoverIntegrity";
    private const string CurrentMigration = "20260808234734_FinalPlatformRevenueControls";

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Data_bearing_upgrade_preserves_invoices_and_outbox_and_backfills_zero_rollups()
    {
        var databaseName = $"nexora_revenue_upgrade_{Guid.NewGuid():N}";
        var target = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Database = databaseName };
        await ExecuteAdminAsync(database.ConnectionString, $"CREATE DATABASE \"{databaseName}\"");

        try
        {
            await using var context = database.ContextForConnectionString(target.ConnectionString, null);
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(PreviousMigration);
            await context.Database.ExecuteSqlRawAsync("""
                INSERT INTO platform."Tenants"
                    ("Id","Name","Slug","Status","CreatedOn","BillingMode")
                VALUES (997101,'Revenue upgrade tenant','revenue-upgrade-997101','Active',now(),'Billable');
                INSERT INTO platform."RateCards"
                    ("Id","Code","Currency","EffectiveFromUtc","IsActive","CreatedOn","Version")
                VALUES (997102,'revenue-upgrade-card','USD','2025-01-01',true,now(),1);
                INSERT INTO platform."BillingStatements"
                    ("Id","TenantId","PeriodStartUtc","PeriodEndUtc","RateCardId","Currency",
                     "Status","TotalAmount","ComputedAtUtc","ComputedBy","Version")
                VALUES (997103,997101,'2026-01-01','2026-02-01',997102,'USD',
                        'Final',100,now(),'system:upgrade-test',1);
                INSERT INTO platform."SubscriptionInvoices"
                    ("Id","TenantId","BillingStatementId","InvoiceNumber","Status","Currency",
                     "Subtotal","TaxRatePercent","TaxAmount","TotalAmount","CreditedAmount","PaidAmount",
                     "IssuedAtUtc","DueAtUtc","SellerSnapshotJson","BuyerSnapshotJson","TaxTreatment",
                     "SourceEvidenceJson","SourceEvidenceSha256","CreatedBy","CreatedAtUtc","Version")
                VALUES (997104,997101,997103,'NX-UPGRADE-997104','Finalized','USD',100,0,0,100,0,0,
                        now(),now()+interval '30 days',jsonb_build_object(),jsonb_build_object(),'exempt',
                        jsonb_build_object(),repeat('a',64),
                        'system:upgrade-test',now(),1);
                INSERT INTO platform."AccountingOutbox"
                    ("Id","TenantId","SubscriptionInvoiceId","MessageType","IdempotencyKey","PayloadJson",
                     "PayloadSha256","Status","ReconciliationStatus","AttemptCount","MaxAttempts",
                     "CreatedAtUtc","AvailableAtUtc")
                VALUES ('99710400-0000-0000-0000-000000000001',997101,997104,'invoice.finalized',
                        'upgrade-outbox-997104',jsonb_build_object(),repeat('b',64),
                        'Pending','NotSent',0,8,now(),now());
                """);

            await migrator.MigrateAsync(CurrentMigration);

            await using var connection = new NpgsqlConnection(target.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("""
                SELECT count(*) = 1
                       AND min("RefundedAmount") = 0
                       AND min("ReversedPaymentAmount") = 0
                       AND min("WrittenOffAmount") = 0
                FROM platform."SubscriptionInvoices" WHERE "Id"=997104;
                """, connection);
            Assert.True((bool)(await command.ExecuteScalarAsync())!);
            await using var outbox = new NpgsqlCommand("""
                SELECT count(*) FROM platform."AccountingOutbox"
                WHERE "SubscriptionInvoiceId"=997104 AND "SubscriptionRevenueActionId" IS NULL;
                """, connection);
            Assert.Equal(1L, (long)(await outbox.ExecuteScalarAsync())!);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAdminAsync(database.ConnectionString,
                $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)");
        }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task New_tables_force_RLS_and_expose_only_the_platform_pipeline_contract()
    {
        await using var connection = await database.OpenConnectionAsync();
        foreach (var table in new[] { "SubscriptionRevenueActions", "SubscriptionTaxRules" })
        {
            await using var catalog = new NpgsqlCommand("""
                SELECT c.relrowsecurity AND c.relforcerowsecurity
                FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
                WHERE n.nspname='platform' AND c.relname=@table;
                """, connection);
            catalog.Parameters.AddWithValue("table", table);
            Assert.True((bool)(await catalog.ExecuteScalarAsync())!);

            Assert.True(await HasPrivilegeAsync(connection, "nexora_pipeline_app", table, "SELECT"));
            Assert.True(await HasPrivilegeAsync(connection, "nexora_pipeline_app", table, "INSERT"));
            Assert.True(await HasPrivilegeAsync(connection, "nexora_pipeline_app", table, "UPDATE"));
            Assert.False(await HasPrivilegeAsync(connection, "nexora_pipeline_app", table, "DELETE"));
            Assert.False(await HasPrivilegeAsync(connection, "nexora_tenant_app", table, "SELECT"));
            Assert.False(await HasPrivilegeAsync(connection, "nexora_identity_app", table, "SELECT"));

            foreach (var deniedRole in new[] { "nexora_tenant_app", "nexora_identity_app" })
            {
                var denied = await Assert.ThrowsAsync<PostgresException>(() =>
                    ExecuteAsRoleAsync(connection, deniedRole,
                        $"SELECT count(*) FROM platform.\"{table}\";"));
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
            }
        }

        await using var policies = new NpgsqlCommand("""
            SELECT count(*) FROM pg_policies
            WHERE schemaname='platform'
              AND tablename IN ('SubscriptionRevenueActions','SubscriptionTaxRules')
              AND 'nexora_pipeline_app'=ANY(roles) AND cmd='ALL';
            """, connection);
        Assert.Equal(2L, (long)(await policies.ExecuteScalarAsync())!);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Tax_rules_enforce_pipeline_access_maker_checker_intervals_tuple_and_immutability()
    {
        var seed = await SeedAsync();
        await using var connection = await database.OpenConnectionAsync();
        var jurisdiction = $"US-NY-{Guid.NewGuid():N}";
        var ruleId = await ScalarAsRoleAsync<long>(connection, "nexora_pipeline_app", $"""
            INSERT INTO platform."SubscriptionTaxRules"
                ("JurisdictionCode","BuyerCountryCode","Currency","Treatment","RatePercent",
                 "LegalAuthorityReference","EvidenceSha256","EffectiveFromUtc","EffectiveToUtc",
                 "Status","Version","ProposedByPlatformUserId","ProposedAtUtc",
                 "ApprovedByPlatformUserId","ApprovedAtUtc")
            VALUES ('{jurisdiction}','US','USD','standard',8.8750,'NY tax authority',repeat('a',64),
                    '2026-01-01',NULL,'Approved',1,{seed.MakerId},now(),{seed.CheckerId},now())
            RETURNING "Id";
            """);

        var overlap = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsRoleAsync(connection,
            "nexora_pipeline_app", $"""
                INSERT INTO platform."SubscriptionTaxRules"
                    ("JurisdictionCode","BuyerCountryCode","Currency","Treatment","RatePercent",
                     "LegalAuthorityReference","EvidenceSha256","EffectiveFromUtc","EffectiveToUtc",
                     "Status","Version","ProposedByPlatformUserId","ProposedAtUtc",
                     "ApprovedByPlatformUserId","ApprovedAtUtc")
                VALUES ('{jurisdiction}','US','USD','standard',8.8750,'overlap evidence',repeat('b',64),
                        '2026-06-01','2027-01-01','Approved',1,{seed.MakerId},now(),{seed.CheckerId},now());
                """));
        Assert.Equal(PostgresErrorCodes.ExclusionViolation, overlap.SqlState);

        var sameActor = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsRoleAsync(connection,
            "nexora_pipeline_app", $"""
                INSERT INTO platform."SubscriptionTaxRules"
                    ("JurisdictionCode","BuyerCountryCode","Currency","Treatment","RatePercent",
                     "LegalAuthorityReference","EvidenceSha256","EffectiveFromUtc","Status","Version",
                     "ProposedByPlatformUserId","ProposedAtUtc","ApprovedByPlatformUserId","ApprovedAtUtc")
                VALUES ('SAME-{Guid.NewGuid():N}','US','USD','standard',1,'same actor evidence',repeat('c',64),
                        '2026-01-01','Approved',1,{seed.MakerId},now(),{seed.MakerId},now());
                """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, sameActor.SqlState);

        var partialTuple = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, $"""
            UPDATE platform."SubscriptionInvoices" SET "TaxRuleId"={ruleId}
            WHERE "Id"={seed.DraftInvoiceId};
            """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, partialTuple.SqlState);

        var nonexistentTuple = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, $"""
            UPDATE platform."SubscriptionInvoices"
            SET "TaxRuleId"=9223372036854770000,"TaxRuleVersion"=1,"TaxJurisdictionCode"='US-X',
                "TaxEvidenceJson"='null',"TaxEvidenceSha256"=repeat('d',64),"TaxDeterminedAtUtc"=now()
            WHERE "Id"={seed.DraftInvoiceId};
            """));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, nonexistentTuple.SqlState);

        await ExecuteAsRoleAsync(connection, "nexora_pipeline_app", $"""
            UPDATE platform."SubscriptionInvoices"
            SET "TaxRuleId"={ruleId},"TaxRuleVersion"=1,"TaxJurisdictionCode"='{jurisdiction}',
                "TaxEvidenceJson"='"rule"',"TaxEvidenceSha256"=repeat('e',64),
                "TaxDeterminedAtUtc"=now()
            WHERE "Id"={seed.DraftInvoiceId};
            UPDATE platform."SubscriptionInvoices"
            SET "Status"='Finalized',"FinalizedBy"='checker@test.invalid',"FinalizedAtUtc"=now()
            WHERE "Id"={seed.DraftInvoiceId};
            """);

        var taxMutation = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsRoleAsync(connection,
            "nexora_pipeline_app", $"""
                UPDATE platform."SubscriptionInvoices" SET "TaxEvidenceSha256"=repeat('f',64)
                WHERE "Id"={seed.DraftInvoiceId};
                """));
        Assert.Equal(PostgresErrorCodes.RaiseException, taxMutation.SqlState);

        var ruleMutation = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsRoleAsync(connection,
            "nexora_pipeline_app", $"""
                UPDATE platform."SubscriptionTaxRules" SET "RatePercent"=9 WHERE "Id"={ruleId};
                """));
        Assert.Equal(PostgresErrorCodes.RaiseException, ruleMutation.SqlState);
        var ruleDelete = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsync(connection, $"DELETE FROM platform.\"SubscriptionTaxRules\" WHERE \"Id\"={ruleId};"));
        Assert.Equal(PostgresErrorCodes.RaiseException, ruleDelete.SqlState);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Revenue_actions_and_outbox_enforce_lineage_transitions_immutability_and_amount_bounds()
    {
        var seed = await SeedAsync();
        await using var connection = await database.OpenConnectionAsync();
        var key = $"refund-{Guid.NewGuid():N}";
        await ExecuteAsRoleAsync(connection, "nexora_pipeline_app", $"""
            INSERT INTO platform."SubscriptionPayments"
                ("SubscriptionInvoiceId","ExternalReference","Amount","ReceivedAtUtc","RecordedBy","RecordedAtUtc")
            VALUES ({seed.FinalInvoiceId},'payment-{key}',100,now(),'sdet-collector',now());
            UPDATE platform."SubscriptionInvoices" SET "PaidAmount"=100 WHERE "Id"={seed.FinalInvoiceId};
            """);
        var actionId = await ScalarAsRoleAsync<long>(connection, "nexora_pipeline_app", $"""
            INSERT INTO platform."SubscriptionRevenueActions"
                ("TenantId","SubscriptionInvoiceId","Kind","Status","IdempotencyKey","Amount","Currency",
                 "Reason","EvidenceSha256","ProposedByPlatformUserId","ProposedAtUtc")
            VALUES ({seed.TenantId},{seed.FinalInvoiceId},'Refund','Proposed','{key}',40,'USD',
                    'Customer refund approved from received cash',repeat('1',64),{seed.MakerId},now())
            RETURNING "Id";
            """);
        await ExecuteAsRoleAsync(connection, "nexora_pipeline_app", $"""
            UPDATE platform."SubscriptionRevenueActions"
            SET "Status"='Completed',"ApprovedByPlatformUserId"={seed.CheckerId},
                "ApprovedAtUtc"=now(),"CompletedAtUtc"=now()
            WHERE "Id"={actionId};
            UPDATE platform."SubscriptionInvoices" SET "RefundedAmount"=40
            WHERE "Id"={seed.FinalInvoiceId};
            """);

        var outboxId = Guid.NewGuid();
        await ExecuteAsRoleAsync(connection, "nexora_pipeline_app", $"""
            INSERT INTO platform."AccountingOutbox"
                ("Id","TenantId","SubscriptionInvoiceId","SubscriptionRevenueActionId","MessageType",
                 "IdempotencyKey","PayloadJson","PayloadSha256","Status","ReconciliationStatus",
                 "AttemptCount","MaxAttempts","CreatedAtUtc","AvailableAtUtc")
            VALUES ('{outboxId}',{seed.TenantId},{seed.FinalInvoiceId},{actionId},'invoice.refunded',
                    'outbox-{key}',jsonb_build_object('actionId',{actionId}),repeat('2',64),'Pending','NotSent',0,8,now(),now());
            """);

        var immutableAction = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsRoleAsync(connection,
            "nexora_pipeline_app", $"""
                UPDATE platform."SubscriptionRevenueActions" SET "Reason"='tampered legal evidence'
                WHERE "Id"={actionId};
                """));
        Assert.Equal(PostgresErrorCodes.RaiseException, immutableAction.SqlState);
        var invalidTransition = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsRoleAsync(connection,
            "nexora_pipeline_app", $"""
                UPDATE platform."SubscriptionRevenueActions" SET "Status"='Failed',"CompletedAtUtc"=NULL
                WHERE "Id"={actionId};
                """));
        Assert.Equal(PostgresErrorCodes.RaiseException, invalidTransition.SqlState);
        var actionDelete = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection,
            $"DELETE FROM platform.\"SubscriptionRevenueActions\" WHERE \"Id\"={actionId};"));
        Assert.Equal(PostgresErrorCodes.RaiseException, actionDelete.SqlState);

        var zeroRefund = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsRoleAsync(connection,
            "nexora_pipeline_app", $"""
                INSERT INTO platform."SubscriptionRevenueActions"
                    ("TenantId","SubscriptionInvoiceId","Kind","Status","IdempotencyKey","Amount","Currency",
                     "Reason","EvidenceSha256","ProposedByPlatformUserId","ProposedAtUtc")
                VALUES ({seed.TenantId},{seed.FinalInvoiceId},'Refund','Proposed','zero-{key}',0,'USD',
                        'A zero refund is invalid',repeat('3',64),{seed.MakerId},now());
                """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, zeroRefund.SqlState);

        var sameActorActionId = await ScalarAsRoleAsync<long>(connection, "nexora_pipeline_app", $"""
            INSERT INTO platform."SubscriptionRevenueActions"
                ("TenantId","SubscriptionInvoiceId","Kind","Status","IdempotencyKey","Amount","Currency",
                 "Reason","EvidenceSha256","ProposedByPlatformUserId","ProposedAtUtc")
            VALUES ({seed.TenantId},{seed.FinalInvoiceId},'WriteOff','Proposed','same-actor-{key}',10,'USD',
                    'Maker cannot approve this write off',repeat('5',64),{seed.MakerId},now())
            RETURNING "Id";
            """);
        var sameActionActor = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsRoleAsync(connection,
            "nexora_pipeline_app", $"""
                UPDATE platform."SubscriptionRevenueActions"
                SET "Status"='Completed',"ApprovedByPlatformUserId"={seed.MakerId},
                    "ApprovedAtUtc"=now(),"CompletedAtUtc"=now()
                WHERE "Id"={sameActorActionId};
                """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, sameActionActor.SqlState);

        await ExecuteAsRoleAsync(connection, "nexora_pipeline_app", $"""
            INSERT INTO platform."SubscriptionRevenueActions"
                ("TenantId","SubscriptionInvoiceId","Kind","Status","IdempotencyKey","Amount","Currency",
                 "Reason","EvidenceSha256","ProposedByPlatformUserId","ProposedAtUtc",
                 "ApprovedByPlatformUserId","ApprovedAtUtc","CompletedAtUtc")
            VALUES ({seed.TenantId},{seed.FinalInvoiceId},'Dunning','Completed','dunning-{key}',0,'USD',
                    'Automated overdue reminder dispatched',repeat('6',64),NULL,now(),NULL,now(),now());
            """);

        var wrongTenant = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsRoleAsync(connection,
            "nexora_pipeline_app", $"""
                INSERT INTO platform."AccountingOutbox"
                    ("Id","TenantId","SubscriptionInvoiceId","SubscriptionRevenueActionId","MessageType",
                     "IdempotencyKey","PayloadJson","PayloadSha256","Status","ReconciliationStatus",
                     "AttemptCount","MaxAttempts","CreatedAtUtc","AvailableAtUtc")
                VALUES ('{Guid.NewGuid()}',{seed.TenantId + 900000},{seed.FinalInvoiceId},{actionId},'invoice.refunded',
                        'cross-{key}','null',repeat('4',64),'Pending','NotSent',0,8,now(),now());
                """));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, wrongTenant.SqlState);

        var lineageMutation = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsRoleAsync(connection,
            "nexora_pipeline_app", $"""
                UPDATE platform."AccountingOutbox" SET "SubscriptionRevenueActionId"=NULL WHERE "Id"='{outboxId}';
                """));
        Assert.Equal(PostgresErrorCodes.RaiseException, lineageMutation.SqlState);

        var refundOverflow = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsRoleAsync(connection,
            "nexora_pipeline_app", $"""
                UPDATE platform."SubscriptionInvoices" SET "RefundedAmount"=101 WHERE "Id"={seed.FinalInvoiceId};
                """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, refundOverflow.SqlState);
        var combinedOverflow = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsRoleAsync(connection,
            "nexora_pipeline_app", $"""
                UPDATE platform."SubscriptionInvoices"
                SET "ReversedPaymentAmount"=61 WHERE "Id"={seed.FinalInvoiceId};
                """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, combinedOverflow.SqlState);
        var nonMonotonic = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsRoleAsync(connection,
            "nexora_pipeline_app", $"""
                UPDATE platform."SubscriptionInvoices" SET "RefundedAmount"=39 WHERE "Id"={seed.FinalInvoiceId};
                """));
        Assert.Equal(PostgresErrorCodes.RaiseException, nonMonotonic.SqlState);
    }

    private async Task<Seed> SeedAsync()
    {
        var suffix = Guid.NewGuid().ToString("N");
        await using var context = database.ContextFor(null);
        var maker = NewOwner($"maker-{suffix}@example.test");
        var checker = NewOwner($"checker-{suffix}@example.test");
        var tenant = new Tenant
        {
            Name = $"Revenue tenant {suffix}", LegalName = $"Revenue tenant {suffix} LLC",
            Slug = $"revenue-{suffix}", Status = TenantStatus.Active,
            BillingContactEmail = $"billing-{suffix}@example.test", PaymentTermsDays = 30
        };
        var card = new RateCard
        {
            Code = $"revenue-card-{suffix}", Currency = "USD", IsActive = true,
            EffectiveFromUtc = DateTime.UtcNow.AddYears(-1)
        };
        context.AddRange(maker, checker, tenant, card);
        await context.SaveChangesAsync();

        var finalStatement = Statement(tenant.Id, card.Id, DateTime.UtcNow.AddMonths(-2));
        var draftStatement = Statement(tenant.Id, card.Id, DateTime.UtcNow.AddMonths(-1));
        context.AddRange(finalStatement, draftStatement);
        await context.SaveChangesAsync();
        var finalInvoice = Invoice(tenant.Id, finalStatement.Id, $"NX-FINAL-{suffix}", SubscriptionInvoiceStatus.Finalized);
        finalInvoice.FinalizedBy = "checker@test.invalid";
        finalInvoice.FinalizedAtUtc = DateTime.UtcNow;
        var draftInvoice = Invoice(tenant.Id, draftStatement.Id, $"NX-DRAFT-{suffix}", SubscriptionInvoiceStatus.Draft);
        context.AddRange(finalInvoice, draftInvoice);
        await context.SaveChangesAsync();
        return new Seed(tenant.Id, maker.Id, checker.Id, finalInvoice.Id, draftInvoice.Id);
    }

    private static PlatformUser NewOwner(string email) => new()
    {
        Email = email, PasswordHash = BCrypt.Net.BCrypt.HashPassword("revenue-test-password"),
        PlatformRole = PlatformRole.Owner, IsActive = true, CreatedOn = DateTime.UtcNow, CreatedBy = "sdet"
    };

    private static BillingStatement Statement(long tenantId, long cardId, DateTime start) => new()
    {
        TenantId = tenantId, RateCardId = cardId, PeriodStartUtc = start,
        PeriodEndUtc = start.AddMonths(1), Currency = "USD", Status = BillingStatementStatus.Final,
        TotalAmount = 100, ComputedAtUtc = DateTime.UtcNow, ComputedBy = "maker@test.invalid",
        FinalizedAtUtc = DateTime.UtcNow, FinalizedBy = "checker@test.invalid",
        ReadinessStatus = BillingReadinessStatus.Ready, ReadinessManifestJson = "{\"ready\":true}",
        ReadinessManifestSha256 = new string('a', 64)
    };

    private static SubscriptionInvoice Invoice(long tenantId, long statementId, string number,
        SubscriptionInvoiceStatus status) => new()
    {
        TenantId = tenantId, BillingStatementId = statementId, InvoiceNumber = number, Status = status,
        Currency = "USD", Subtotal = 100, TaxRatePercent = 0, TaxAmount = 0, TotalAmount = 100,
        IssuedAtUtc = DateTime.UtcNow, DueAtUtc = DateTime.UtcNow.AddDays(30),
        SellerSnapshotJson = "{}", BuyerSnapshotJson = "{}", TaxTreatment = "exempt",
        SourceEvidenceJson = "{}", SourceEvidenceSha256 = new string('b', 64),
        CreatedBy = "sdet", CreatedAtUtc = DateTime.UtcNow
    };

    private static async Task<bool> HasPrivilegeAsync(NpgsqlConnection connection, string role,
        string table, string privilege)
    {
        await using var command = new NpgsqlCommand(
            "SELECT has_table_privilege(@role, format('%I.%I','platform',@table), @privilege);", connection);
        command.Parameters.AddWithValue("role", role);
        command.Parameters.AddWithValue("table", table);
        command.Parameters.AddWithValue("privilege", privilege);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsRoleAsync(NpgsqlConnection connection, string role, string sql)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand($"SET LOCAL ROLE {role};\n{sql}", connection, transaction);
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private static async Task<T> ScalarAsRoleAsync<T>(NpgsqlConnection connection, string role, string sql)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand($"SET LOCAL ROLE {role};\n{sql}", connection, transaction);
        var value = (T)(await command.ExecuteScalarAsync())!;
        await transaction.CommitAsync();
        return value;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteAdminAsync(string connectionString, string sql)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres" };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private sealed record Seed(long TenantId, long MakerId, long CheckerId,
        long FinalInvoiceId, long DraftInvoiceId);
}
