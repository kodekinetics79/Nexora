using System.Security.Cryptography;
using System.Text;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class ReceivablesOperationsPostgreSqlTests(PostgreSqlTestDatabase database)
{
    private const long TenantId = 9_823_010;
    private const long OtherTenantId = 9_823_011;
    private const long CustomerId = 9_823_020;
    private const long OtherCustomerId = 9_823_021;
    private const long MismatchedCustomerId = 9_823_022;
    private const long CurrencyId = 9_823_030;
    private const long OtherCurrencyId = 9_823_031;
    private const string ContactProviderSecret = "postgres-contact-provider-secret-at-least-32-bytes";
    private const string DeliveryProviderSecret = "postgres-delivery-provider-secret-at-least-32-bytes";
    private const string AuditActorSecret = "postgres-audit-actor-secret-at-least-32-bytes";

    private static readonly string[] GovernedTables =
    [
        "FinanceCommunicationContacts", "CustomerStatements", "CustomerStatementLines",
        "DunningPolicies", "DunningPolicySteps", "CustomerCollectionProfiles", "CollectionControls",
        "DunningCases", "PromisesToPay", "DunningRuns", "DunningNotices", "DunningDeliveryAttempts"
    ];

    private static int savepointSequence;

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task GovernedSchema_EnforcesRlsAppendOnlyEvidenceAndRestrictedPrivileges()
    {
        await SeedTenantReferencesAsync();
        await using var connection = await database.OpenConnectionAsync();

        await AssertGovernanceMetadataAsync(connection);
        await AssertRuntimeTenantIsolationAsync(connection, 9_824_000);
        await AssertTenantReferencesAsync(connection, 9_824_100);
        await AssertGovernedPrivilegesAsync(connection);
        await AssertOptionalRunDecisionGovernanceAsync(connection);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task GovernedRoots_RejectDirectNonInitialInserts()
    {
        await SeedTenantReferencesAsync();
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var graph = await SeedGraphAsync(connection, transaction, 9_825_000, releaseNotice: false);

        await AssertRejectedAsync(connection, transaction, StatementInsertSql(
            graph.Seed + 100, CustomerId, CurrencyId, "Finalized", "STM-BYPASS-1",
            finalizedBy: "bypass-checker", finalizedOn: "timestamp '2026-07-23'"));
        await AssertRejectedAsync(connection, transaction, PolicyInsertSql(
            graph.Seed + 101, 2, "Approved", "bypass-maker", "bypass-approver"));
        await AssertRejectedAsync(connection, transaction, ContactInsertSql(
            graph.Seed + 102, isActive: false, deactivatedBy: "bypass-actor"));
        await AssertRejectedAsync(connection, transaction, ControlInsertSql(
            graph.Seed + 103, "Resolved", "bypass-actor"));
        await AssertRejectedAsync(connection, transaction, CaseInsertSql(
            graph.Seed + 104, graph.PolicyId, graph.StatementId, "Resolved"));
        await AssertRejectedAsync(connection, transaction, PromiseInsertSql(
            graph.Seed + 105, graph.CaseId, "Withdrawn"));
        await AssertRejectedAsync(connection, transaction, RunInsertSql(
            graph.Seed + 106, graph.PolicyId, "Completed"));
        await AssertRejectedAsync(connection, transaction, NoticeInsertSql(
            graph.Seed + 107, graph.CaseId, graph.StatementId, graph.ContactId, "Released",
            approvedBy: "bypass-approver", releasedBy: "bypass-releaser"));

        await transaction.RollbackAsync();
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Lifecycles_RejectInvalidTransitions_AndAuditControllingActors()
    {
        await SeedTenantReferencesAsync();
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var graph = await SeedGraphAsync(connection, transaction, 9_826_000, releaseNotice: true);

        await AssertRejectedAsync(connection, transaction, $"""
            UPDATE "CustomerStatements"
            SET "Status" = 'Cancelled', "CancelledBy" = 'late-canceller',
                "CancelledOn" = timestamp '2026-07-24', "CancellationReason" = 'invalid reversal',
                "Version" = "Version" + 1
            WHERE "Id" = {graph.StatementId}
            """);
        await AssertRejectedAsync(connection, transaction, $"""
            UPDATE "DunningPolicies"
            SET "Status" = 'Draft', "Version" = "Version" + 1
            WHERE "Id" = {graph.PolicyId}
            """);
        await AssertRejectedAsync(connection, transaction, $"""
            UPDATE "DunningNotices"
            SET "Status" = 'Approved', "Version" = "Version" + 1
            WHERE "Id" = {graph.NoticeId}
            """);

        await ExecuteAsync(connection, transaction, "SET CONSTRAINTS ALL IMMEDIATE");
        await AssertAuditActorAsync(connection, transaction, "CustomerStatement", graph.StatementId,
            "Finalized", "statement-checker");
        await AssertAuditActorAsync(connection, transaction, "DunningPolicy", graph.PolicyId,
            "Active", "policy-activator");
        await AssertAuditActorAsync(connection, transaction, "DunningNotice", graph.NoticeId,
            "Released", "notice-releaser");

        await transaction.RollbackAsync();
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task NullableCurrencyBusinessKeys_AreNullsNotDistinct()
    {
        await SeedTenantReferencesAsync();
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var graph = await SeedGraphAsync(connection, transaction, 9_827_000, releaseNotice: false);

        await ExecuteAsync(connection, transaction, StatementInsertSql(
            graph.Seed + 100, CustomerId, null, "Draft", cutoffDay: 25));
        await AssertRejectedAsync(connection, transaction, StatementInsertSql(
            graph.Seed + 101, CustomerId, null, "Draft", cutoffDay: 25),
            PostgresErrorCodes.UniqueViolation);
        await FinalizeStatementAsync(connection, transaction, graph.Seed + 100);

        await ExecuteAsync(connection, transaction, ProfileInsertSql(
            graph.Seed + 102, graph.PolicyId, null));
        await AssertRejectedAsync(connection, transaction, ProfileInsertSql(
            graph.Seed + 103, graph.PolicyId, null), PostgresErrorCodes.UniqueViolation);

        await ExecuteAsync(connection, transaction, CaseInsertSql(
            graph.Seed + 104, graph.PolicyId, graph.Seed + 100, "Open", currencyId: null));
        await AssertRejectedAsync(connection, transaction, CaseInsertSql(
            graph.Seed + 105, graph.PolicyId, graph.Seed + 100, "Open", currencyId: null),
            PostgresErrorCodes.UniqueViolation);

        await transaction.RollbackAsync();
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task CancelledCorrection_DoesNotConsumeSuccessorSlot()
    {
        await SeedTenantReferencesAsync();
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var graph = await SeedGraphAsync(connection, transaction, 9_828_000, releaseNotice: false);
        var firstCorrectionId = graph.Seed + 100;
        var replacementCorrectionId = graph.Seed + 101;

        await ExecuteAsync(connection, transaction, StatementInsertSql(
            firstCorrectionId, CustomerId, CurrencyId, "Draft", supersedesId: graph.StatementId,
            revision: 2, correctionReason: "Correction required for certified source adjustment."));
        await ExecuteAsync(connection, transaction, $"""
            UPDATE "CustomerStatements"
            SET "Status" = 'Cancelled', "CancelledBy" = 'correction-canceller',
                "CancelledOn" = timestamp '2026-07-24',
                "CancellationReason" = 'Correction input was withdrawn with evidence',
                "Version" = "Version" + 1
            WHERE "Id" = {firstCorrectionId}
            """);
        await ExecuteAsync(connection, transaction, StatementInsertSql(
            replacementCorrectionId, CustomerId, CurrencyId, "Draft", supersedesId: graph.StatementId,
            revision: 2, correctionReason: "Replacement correction for certified source adjustment."));

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT count(*) FROM "CustomerStatements"
            WHERE "SupersedesStatementId" = {graph.StatementId}
              AND "Status" = 'Draft'
            """;
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);

        await transaction.RollbackAsync();
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task NoticeArtifact_IsImmutable_AndDeliveryEvidenceReferencesExactHash()
    {
        await SeedTenantReferencesAsync();
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var graph = await SeedGraphAsync(connection, transaction, 9_829_000, releaseNotice: false);

        await AssertRejectedAsync(connection, transaction, $"""
            UPDATE "DunningNotices"
            SET "Status" = 'Approved', "ApprovedBy" = 'notice-approver',
                "ApprovedOn" = timestamp '2026-07-23 10:00:00',
                "ArtifactContent" = '<html>forged approval artifact</html>',
                "ArtifactHash" = repeat('f', 64), "Version" = "Version" + 1
            WHERE "Id" = {graph.NoticeId}
            """);

        await ApproveAndReleaseNoticeAsync(connection, transaction, graph.NoticeId);
        await AssertRejectedAsync(connection, transaction, $"""
            UPDATE "DunningNotices"
            SET "Subject" = 'Forged released subject', "Version" = "Version" + 1
            WHERE "Id" = {graph.NoticeId}
            """);
        await AssertRejectedAsync(connection, transaction, DeliveryAttemptInsertSql(
            graph.Seed + 100, graph.NoticeId, new Guid("10000000-0000-0000-0000-000000000001"),
            new string('f', 64)), PostgresErrorCodes.CheckViolation);

        await ExecuteAsync(connection, transaction, DeliveryAttemptInsertSql(
            graph.Seed + 101, graph.NoticeId, new Guid("10000000-0000-0000-0000-000000000002"),
            graph.NoticeArtifactHash));

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT a."ArtifactHash" = n."ArtifactHash"
            FROM "DunningDeliveryAttempts" a
            JOIN "DunningNotices" n
              ON n."BusinessUnitId" = a."BusinessUnitId" AND n."Id" = a."DunningNoticeId"
            WHERE a."Id" = {graph.Seed + 101}
            """;
        Assert.True((bool)(await command.ExecuteScalarAsync())!);

        await transaction.RollbackAsync();
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task EvidenceChain_RejectsForgedArtifactsApprovalBypassAndUnprovenDelivery()
    {
        await SeedTenantReferencesAsync();
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var graph = await SeedGraphAsync(connection, transaction, 9_829_500, releaseNotice: false);
        var forgedStatementId = graph.Seed + 100;

        await AssertRejectedAsync(connection, transaction, ContactInsertSql(
            graph.Seed + 99, providerSignature: new string('0', 64)), PostgresErrorCodes.CheckViolation);

        await ExecuteAsync(connection, transaction, StatementInsertSql(
            forgedStatementId, CustomerId, CurrencyId, "Draft", cutoffDay: 21));
        await AssertRejectedAsync(connection, transaction, $"""
            UPDATE "CustomerStatements"
            SET "Status" = 'Finalized', "StatementNumber" = 'STM-FORGED-{forgedStatementId}',
                "ArtifactContent" = '<html><body>Statement STM-FORGED-{forgedStatementId}</body></html>',
                "ArtifactHash" = repeat('f', 64), "ArtifactReference" = 'forged:artifact',
                "FinalizedBy" = 'statement-checker', "FinalizedOn" = timestamp '2026-07-23 08:00:00',
                "Version" = "Version" + 1
            WHERE "Id" = {forgedStatementId}
            """);

        await AssertRejectedAsync(connection, transaction, $"""
            UPDATE "DunningNotices"
            SET "Status" = 'Released', "ReleasedBy" = 'notice-releaser',
                "ReleasedOn" = timestamp '2026-07-23 10:30:00', "Version" = "Version" + 1
            WHERE "Id" = {graph.NoticeId}
            """);

        await ApproveAndReleaseNoticeAsync(connection, transaction, graph.NoticeId);
        await AssertRejectedAsync(connection, transaction, DeliveryAttemptInsertSql(
            graph.Seed + 98, graph.NoticeId, new Guid("10000000-0000-0000-0000-000000000098"),
            graph.NoticeArtifactHash, providerSignature: new string('0', 64)), PostgresErrorCodes.CheckViolation);
        await AssertRejectedAsync(connection, transaction, $"""
            UPDATE "DunningNotices"
            SET "Status" = 'Delivered', "DeliveryUpdatedBy" = 'delivery-recorder',
                "DeliveryUpdatedOn" = timestamp '2026-07-23 11:00:01',
                "ProviderReference" = 'provider:missing-attempt', "Version" = "Version" + 1
            WHERE "Id" = {graph.NoticeId};
            SET CONSTRAINTS ALL IMMEDIATE;
            """, PostgresErrorCodes.CheckViolation);

        await ExecuteAsync(connection, transaction, DeliveryAttemptInsertSql(
            graph.Seed + 103, graph.NoticeId, new Guid("10000000-0000-0000-0000-000000000103"),
            graph.NoticeArtifactHash, "provider:missing-attempt",
            new DateTime(2026, 7, 23, 9, 0, 0, DateTimeKind.Utc)));
        await AssertRejectedAsync(connection, transaction, $"""
            UPDATE "DunningNotices"
            SET "Status" = 'Delivered', "DeliveryUpdatedBy" = 'delivery-recorder',
                "DeliveryUpdatedOn" = timestamp '2026-07-23 11:00:01',
                "ProviderReference" = 'provider:missing-attempt', "Version" = "Version" + 1
            WHERE "Id" = {graph.NoticeId};
            SET CONSTRAINTS ALL IMMEDIATE;
            """, PostgresErrorCodes.CheckViolation);

        var otherContactId = graph.Seed + 101;
        await ExecuteAsync(connection, transaction, ContactInsertSql(otherContactId, customerId: MismatchedCustomerId));
        var substitutedContent = "<html><body>Substituted customer contact</body></html>";
        await AssertRejectedAsync(connection, transaction, NoticeInsertSql(
            graph.Seed + 102, graph.CaseId, graph.StatementId, otherContactId, "Draft",
            artifactContent: substitutedContent, artifactHash: NoticeArtifactHash(substitutedContent)),
            PostgresErrorCodes.CheckViolation);
        await AssertRejectedAsync(connection, transaction, CaseInsertSql(
            graph.Seed + 104, graph.PolicyId, graph.StatementId, "Open",
            customerId: MismatchedCustomerId), PostgresErrorCodes.CheckViolation);

        var substitutedStatementId = graph.Seed + 105;
        await ExecuteAsync(connection, transaction, StatementInsertSql(
            substitutedStatementId, CustomerId, CurrencyId, "Draft", cutoffDay: 20));
        await FinalizeStatementAsync(connection, transaction, substitutedStatementId);
        var statementSubstitutionContent = "<html><body>Substituted statement</body></html>";
        await AssertRejectedAsync(connection, transaction, NoticeInsertSql(
            graph.Seed + 106, graph.CaseId, substitutedStatementId, graph.ContactId, "Draft",
            artifactContent: statementSubstitutionContent,
            artifactHash: NoticeArtifactHash(statementSubstitutionContent)), PostgresErrorCodes.CheckViolation);

        await transaction.RollbackAsync();
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task SignedPaymentReversal_AtomicallyBreaksKeptPromise()
    {
        await SeedTenantReferencesAsync();
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var graph = await SeedGraphAsync(connection, transaction, 9_829_800, releaseNotice: false);
        var paymentId = graph.Seed + 100;
        var promiseId = graph.Seed + 101;
        const string actor = "payment-reversal-operator";

        await ExecuteAsync(connection, transaction, $"""
            INSERT INTO "CustomerPayments"
                ("Id", "BusinessUnitId", "CustomerId", "CurrencyId", "ReceiptNumber", "Status",
                 "PaymentDate", "Amount", "IdempotencyKey", "RequestHash", "Version", "CreatedBy", "CreatedOn")
            VALUES ({paymentId}, {TenantId}, {CustomerId}, {CurrencyId}, 'RCT-{paymentId}', 'Posted',
                    timestamp '2026-07-23', 25, 'pg-payment-{paymentId}', repeat('9', 64), 1,
                    'payment-maker', timestamp '2026-07-23')
            """);
        await ExecuteAsync(connection, transaction, PromiseInsertSql(promiseId, graph.CaseId, "Open"));
        await ExecuteAsync(connection, transaction, $"""
            UPDATE "PromisesToPay"
            SET "Status" = 'Kept', "ClosedBy" = 'promise-keeper',
                "ClosedOn" = timestamp '2026-07-23 12:00:00',
                "ClosureEvidenceReference" = 'matched-payment:{paymentId}',
                "MatchedPaymentId" = {paymentId}, "MatchedAmount" = 25, "Version" = "Version" + 1
            WHERE "Id" = {promiseId}
            """);

        await ExecuteAsync(connection, transaction, $"""
            SET LOCAL ROLE nexora_tenant_app;
            SET LOCAL nexora.business_unit_id = '{TenantId}';
            SET LOCAL nexora.actor_id = '{actor}';
            SET LOCAL nexora.actor_signature = '{AuditActorSignature(actor)}';
            UPDATE "CustomerPayments"
            SET "Status" = 'Reversed', "ReversedOn" = timestamp '2026-07-23 13:00:00',
                "ReversalReason" = 'Bank reversal evidence', "Version" = "Version" + 1
            WHERE "Id" = {paymentId};
            """);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT "Status", "ClosedBy", "MatchedPaymentId" IS NULL
            FROM "PromisesToPay" WHERE "Id" = {promiseId}
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("Broken", reader.GetString(0));
        Assert.Equal(actor, reader.GetString(1));
        Assert.True(reader.GetBoolean(2));
        await reader.DisposeAsync();

        await transaction.RollbackAsync();
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ReleaseAndLeaseTransitions_RevalidateCurrentEvidenceAndOwnership()
    {
        await SeedTenantReferencesAsync();
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var graph = await SeedGraphAsync(connection, transaction, 9_829_900, releaseNotice: false);

        await ExecuteAsync(connection, transaction, $"""
            UPDATE "DunningNotices"
            SET "Status" = 'Approved', "ApprovedBy" = 'notice-approver',
                "ApprovedOn" = now() - interval '2 minutes', "Version" = "Version" + 1
            WHERE "Id" = {graph.NoticeId};
            UPDATE "FinanceCommunicationContacts"
            SET "IsActive" = false, "DeactivatedBy" = 'contact-controller',
                "DeactivatedOn" = timestamp '2026-07-23 10:15:00',
                "DeactivationReason" = 'Verified customer communication withdrawal',
                "Version" = "Version" + 1
            WHERE "Id" = {graph.ContactId};
            """);
        await AssertRejectedAsync(connection, transaction, $"""
            UPDATE "DunningNotices"
            SET "Status" = 'Released', "ReleasedBy" = 'notice-releaser',
                "ReleasedOn" = now() - interval '1 minute', "Version" = "Version" + 1
            WHERE "Id" = {graph.NoticeId}
            """);

        var runId = graph.Seed + 200;
        await ExecuteAsync(connection, transaction, RunInsertSql(runId, graph.PolicyId, "Pending"));
        var leaseToken = Guid.NewGuid();
        await ExecuteAsync(connection, transaction, $"""
            UPDATE "DunningRuns"
            SET "Status" = 'Running', "LeaseOwner" = 'lease-worker', "LeaseToken" = '{leaseToken}',
                "LeaseUntil" = now() - interval '1 minute', "Version" = "Version" + 1
            WHERE "Id" = {runId}
            """);
        await AssertRejectedAsync(connection, transaction, $"""
            UPDATE "DunningRuns"
            SET "LeaseUntil" = now() + interval '5 minutes', "Version" = "Version" + 1
            WHERE "Id" = {runId}
            """);
        await ExecuteAsync(connection, transaction, "SET LOCAL TIME ZONE 'Pacific/Honolulu'");
        await AssertRejectedAsync(connection, transaction, $"""
            UPDATE "DunningRuns"
            SET "Status" = 'Failed', "FailureReason" = 'Expired worker attempted terminal failure',
                "FailureEvidenceReference" = 'expired-worker:{runId}', "CompletedOn" = now(),
                "LeaseOwner" = NULL, "LeaseToken" = NULL, "LeaseUntil" = NULL,
                "FailedCount" = "FailedCount" + 1, "Version" = "Version" + 1
            WHERE "Id" = {runId}
            """);

        await transaction.RollbackAsync();
    }

    private async Task SeedTenantReferencesAsync()
    {
        await using var context = database.ContextFor(null);
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO public."FinanceProviderSecrets" ("Name", "Secret", "UpdatedOn")
            VALUES ('ContactVerification', {ContactProviderSecret}, now()),
                   ('DunningDelivery', {DeliveryProviderSecret}, now()),
                   ('AuditActor', {AuditActorSecret}, now())
            ON CONFLICT ("Name") DO UPDATE SET "Secret" = EXCLUDED."Secret", "UpdatedOn" = EXCLUDED."UpdatedOn"
            """);
        if (!await context.BusinessUnits.AnyAsync(x => x.Id == TenantId))
            Seed.EnsureBusinessUnit(context, TenantId);
        if (!await context.BusinessUnits.AnyAsync(x => x.Id == OtherTenantId))
            Seed.EnsureBusinessUnit(context, OtherTenantId);
        if (!await context.Customers.AnyAsync(x => x.Id == CustomerId))
            Seed.Customer(context, CustomerId, TenantId, "Receivables certification customer");
        if (!await context.Customers.AnyAsync(x => x.Id == OtherCustomerId))
            Seed.Customer(context, OtherCustomerId, OtherTenantId, "Other tenant certification customer");
        if (!await context.Customers.AnyAsync(x => x.Id == MismatchedCustomerId))
            Seed.Customer(context, MismatchedCustomerId, TenantId, "Mismatched certification customer");
        if (!await context.Currencies.AnyAsync(x => x.Id == CurrencyId))
        {
            context.Currencies.Add(new Currency
            {
                Id = CurrencyId,
                Code = "R01",
                CurrencyName = "Receivables certification currency",
                Symbol = "R",
                ExchangeRate = 1m,
                IsBaseCurrency = true,
                IsActive = true,
                CreatedBy = "receivables-pg-tests",
                CreatedOn = DateTime.UtcNow,
                BusinessUnitId = TenantId
            });
        }
        if (!await context.Currencies.AnyAsync(x => x.Id == OtherCurrencyId))
        {
            context.Currencies.Add(new Currency
            {
                Id = OtherCurrencyId,
                Code = "R02",
                CurrencyName = "Other tenant certification currency",
                Symbol = "O",
                ExchangeRate = 1m,
                IsBaseCurrency = true,
                IsActive = true,
                CreatedBy = "receivables-pg-tests",
                CreatedOn = DateTime.UtcNow,
                BusinessUnitId = OtherTenantId
            });
        }

        await context.SaveChangesAsync();
    }

    private static async Task<CertificationGraph> SeedGraphAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long seed,
        bool releaseNotice)
    {
        var statementId = seed + 1;
        var policyId = seed + 2;
        var contactId = seed + 3;
        var caseId = seed + 4;
        var noticeId = seed + 5;
        var noticeArtifact = "<html><body>Governed collection notice</body></html>";
        var noticeArtifactHash = NoticeArtifactHash(noticeArtifact);

        await ExecuteAsync(connection, transaction, StatementInsertSql(
            statementId, CustomerId, CurrencyId, "Draft",
            cutoffDay: 1 + (int)((seed / 1_000) % 20)));
        await FinalizeStatementAsync(connection, transaction, statementId);
        await ExecuteAsync(connection, transaction, PolicyInsertSql(
            policyId, 1, "Draft", "policy-maker"));
        await ExecuteAsync(connection, transaction, PolicyStepInsertSql(seed + 6, policyId));
        await ExecuteAsync(connection, transaction, $"""
            UPDATE "DunningPolicies"
            SET "Status" = 'Approved', "ApprovedBy" = 'policy-approver',
                "ApprovedOn" = timestamp '2026-07-23 09:00:00', "Version" = "Version" + 1
            WHERE "Id" = {policyId}
            """);
        await ExecuteAsync(connection, transaction, $"""
            UPDATE "DunningPolicies"
            SET "Status" = 'Active', "ActivatedBy" = 'policy-activator',
                "ActivatedOn" = timestamp '2026-07-23 09:30:00', "Version" = "Version" + 1
            WHERE "Id" = {policyId}
            """);
        await ExecuteAsync(connection, transaction, ContactInsertSql(contactId));
        await ExecuteAsync(connection, transaction, CaseInsertSql(
            caseId, policyId, statementId, "Open", currencyId: CurrencyId));
        await ExecuteAsync(connection, transaction, NoticeInsertSql(
            noticeId, caseId, statementId, contactId, "Draft",
            artifactContent: noticeArtifact, artifactHash: noticeArtifactHash));
        if (releaseNotice)
            await ApproveAndReleaseNoticeAsync(connection, transaction, noticeId);

        return new CertificationGraph(seed, statementId, policyId, contactId, caseId, noticeId,
            noticeArtifactHash);
    }

    private static async Task FinalizeStatementAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long statementId)
    {
        var statementNumber = $"STM-CERT-{statementId}";
        var content = $"<html><body>Statement {statementNumber}</body></html>";
        await ExecuteAsync(connection, transaction, $"""
            UPDATE "CustomerStatements"
            SET "Status" = 'Finalized', "StatementNumber" = '{statementNumber}',
                "ArtifactContent" = '{content}', "ArtifactHash" = '{Sha256(content)}',
                "ArtifactReference" = 'statement:{statementId}:{Sha256(content)}',
                "FinalizedBy" = 'statement-checker', "FinalizedOn" = timestamp '2026-07-23 08:00:00',
                "Version" = "Version" + 1
            WHERE "Id" = {statementId}
            """);
    }

    private static async Task ApproveAndReleaseNoticeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long noticeId)
    {
        await ExecuteAsync(connection, transaction, $"""
            UPDATE "DunningNotices"
            SET "Status" = 'Approved', "ApprovedBy" = 'notice-approver',
                "ApprovedOn" = now() - interval '2 minutes', "Version" = "Version" + 1
            WHERE "Id" = {noticeId}
            """);
        await ExecuteAsync(connection, transaction, $"""
            UPDATE "DunningNotices"
            SET "Status" = 'Released', "ReleasedBy" = 'notice-releaser',
                "ReleasedOn" = now() - interval '1 minute', "Version" = "Version" + 1
            WHERE "Id" = {noticeId}
            """);
    }

    private static async Task AssertGovernanceMetadataAsync(NpgsqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
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
               OR NOT table_definition.relforcerowsecurity
               OR policy.oid IS NULL
               OR policy.polqual IS NULL
               OR policy.polwithcheck IS NULL
               OR NOT tenant_role.oid = ANY(policy.polroles)
               OR position('nexora.business_unit_id' in pg_get_expr(policy.polqual, policy.polrelid)) = 0
               OR position('nexora.business_unit_id' in pg_get_expr(policy.polwithcheck, policy.polrelid)) = 0;
            """;
        command.Parameters.AddWithValue("tables", GovernedTables);
        Assert.Null((await command.ExecuteScalarAsync()) as string);
    }

    private static async Task AssertRuntimeTenantIsolationAsync(NpgsqlConnection connection, long seed)
    {
        await using var seedTransaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, seedTransaction, StatementInsertSql(seed, CustomerId, CurrencyId, "Draft"));
        await seedTransaction.CommitAsync();

        await using (var sameTenantTransaction = await connection.BeginTransactionAsync())
        {
            await using var sameTenant = connection.CreateCommand();
            sameTenant.Transaction = sameTenantTransaction;
            sameTenant.CommandText = $"""
                SET LOCAL ROLE nexora_tenant_app;
                SET LOCAL nexora.business_unit_id = '{TenantId}';
                SELECT count(*) FROM "CustomerStatements" WHERE "Id" = {seed};
                """;
            Assert.Equal(1L, (long)(await sameTenant.ExecuteScalarAsync())!);
            await sameTenantTransaction.RollbackAsync();
        }

        await using (var crossTenantReadTransaction = await connection.BeginTransactionAsync())
        {
            await using var crossTenantRead = connection.CreateCommand();
            crossTenantRead.Transaction = crossTenantReadTransaction;
            crossTenantRead.CommandText = $"""
                SET LOCAL ROLE nexora_tenant_app;
                SET LOCAL nexora.business_unit_id = '{OtherTenantId}';
                SELECT count(*) FROM "CustomerStatements" WHERE "Id" = {seed};
                """;
            Assert.Equal(0L, (long)(await crossTenantRead.ExecuteScalarAsync())!);
            await crossTenantReadTransaction.RollbackAsync();
        }

        await using var crossTenantWriteTransaction = await connection.BeginTransactionAsync();
        await using var crossTenantWrite = connection.CreateCommand();
        crossTenantWrite.Transaction = crossTenantWriteTransaction;
        crossTenantWrite.CommandText = $"""
            SET LOCAL ROLE nexora_tenant_app;
            SET LOCAL nexora.business_unit_id = '{TenantId}';
            {PolicyInsertSql(seed + 1, 91, "Draft", "cross-tenant-maker", businessUnitId: OtherTenantId)}
            """;
        var exception = await Assert.ThrowsAsync<PostgresException>(() => crossTenantWrite.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
        await crossTenantWriteTransaction.RollbackAsync();

        await using (var forgedActorTransaction = await connection.BeginTransactionAsync())
        {
            await using var forgedActor = connection.CreateCommand();
            forgedActor.Transaction = forgedActorTransaction;
            forgedActor.CommandText = $"""
                SET LOCAL ROLE nexora_tenant_app;
                SET LOCAL nexora.business_unit_id = '{TenantId}';
                SET LOCAL nexora.actor_id = 'forged-actor';
                SET LOCAL nexora.actor_signature = '{new string('0', 64)}';
                {PolicyInsertSql(seed + 2, 92, "Draft", "forged-actor")}
                """;
            var forgedException = await Assert.ThrowsAsync<PostgresException>(() => forgedActor.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, forgedException.SqlState);
            await forgedActorTransaction.RollbackAsync();
        }

        await using var signedActorTransaction = await connection.BeginTransactionAsync();
        await using var signedActor = connection.CreateCommand();
        signedActor.Transaction = signedActorTransaction;
        const string actorId = "signed-finance-actor";
        signedActor.CommandText = $"""
            SET LOCAL ROLE nexora_tenant_app;
            SET LOCAL nexora.business_unit_id = '{TenantId}';
            SET LOCAL nexora.actor_id = '{actorId}';
            SET LOCAL nexora.actor_signature = '{AuditActorSignature(actorId)}';
            {PolicyInsertSql(seed + 3, 93, "Draft", actorId)}
            """;
        Assert.Equal(1, await signedActor.ExecuteNonQueryAsync());
        await signedActorTransaction.RollbackAsync();
    }

    private static async Task AssertTenantReferencesAsync(NpgsqlConnection connection, long seed)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        await AssertRejectedAsync(connection, transaction,
            StatementInsertSql(seed, OtherCustomerId, CurrencyId, "Draft"),
            PostgresErrorCodes.ForeignKeyViolation);
        await AssertRejectedAsync(connection, transaction,
            StatementInsertSql(seed + 1, CustomerId, OtherCurrencyId, "Draft"),
            PostgresErrorCodes.ForeignKeyViolation);
        await transaction.RollbackAsync();
    }

    private static async Task AssertGovernedPrivilegesAsync(NpgsqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                NOT EXISTS (
                    SELECT 1 FROM unnest(@tables::text[]) AS governed(table_name)
                    WHERE NOT has_table_privilege('nexora_tenant_app',
                        format('public.%I', governed.table_name), 'SELECT,INSERT')
                       OR has_table_privilege('nexora_tenant_app',
                        format('public.%I', governed.table_name), 'DELETE,TRUNCATE')),
                NOT has_table_privilege('nexora_tenant_app', 'public."CustomerStatementLines"', 'UPDATE')
                    AND NOT has_table_privilege('nexora_tenant_app', 'public."DunningPolicySteps"', 'UPDATE')
                    AND NOT has_table_privilege('nexora_tenant_app', 'public."DunningDeliveryAttempts"', 'UPDATE'),
                has_sequence_privilege('nexora_tenant_app', 'public."CustomerStatements_Id_seq"', 'USAGE')
                    AND has_sequence_privilege('nexora_tenant_app', 'public."DunningDeliveryAttempts_Id_seq"', 'USAGE'),
                NOT has_function_privilege('public', 'public.nexora_ar_governed_mutation()', 'EXECUTE')
                    AND has_function_privilege('nexora_tenant_app', 'public.nexora_ar_governed_mutation()', 'EXECUTE');
            """;
        command.Parameters.AddWithValue("tables", GovernedTables);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(2));
        Assert.True(reader.GetBoolean(3));
    }

    private static async Task AssertOptionalRunDecisionGovernanceAsync(NpgsqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CASE WHEN to_regclass('public."DunningRunDecisions"') IS NULL THEN true ELSE
                (SELECT table_definition.relrowsecurity AND table_definition.relforcerowsecurity
                    AND EXISTS (
                        SELECT 1 FROM pg_policy policy
                        WHERE policy.polrelid = table_definition.oid
                          AND policy.polname = 'nexora_tenant_isolation')
                    AND NOT has_table_privilege('nexora_tenant_app',
                        'public."DunningRunDecisions"', 'UPDATE,DELETE,TRUNCATE')
                    AND EXISTS (
                        SELECT 1 FROM pg_trigger trigger_definition
                        WHERE trigger_definition.tgrelid = table_definition.oid
                          AND NOT trigger_definition.tgisinternal
                          AND (pg_get_triggerdef(trigger_definition.oid) ILIKE '%UPDATE%'
                            OR pg_get_triggerdef(trigger_definition.oid) ILIKE '%DELETE%'))
                    AND EXISTS (
                        SELECT 1 FROM pg_trigger checkpoint_trigger
                        WHERE checkpoint_trigger.tgrelid = table_definition.oid
                          AND checkpoint_trigger.tgname = 'trg_dunningrundecisions_verify_profile'
                          AND NOT checkpoint_trigger.tgisinternal)
                    AND EXISTS (
                        SELECT 1 FROM pg_indexes checkpoint_index
                        WHERE checkpoint_index.schemaname = 'public'
                          AND checkpoint_index.tablename = 'DunningRunDecisions'
                          AND checkpoint_index.indexname = 'UX_DunningRunDecisions_BU_Run_Profile'
                          AND checkpoint_index.indexdef ILIKE '%UNIQUE%')
                 FROM pg_class table_definition
                 WHERE table_definition.oid = to_regclass('public."DunningRunDecisions"'))
            END;
            """;
        Assert.True((bool)(await command.ExecuteScalarAsync())!);
    }

    private static async Task AssertAuditActorAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string aggregateType,
        long aggregateId,
        string action,
        string expectedActor)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT "Actor"
            FROM "CommercialFinanceAudits"
            WHERE "BusinessUnitId" = @tenant AND "AggregateType" = @aggregate
              AND "AggregateId" = @id AND "Action" = @action
            ORDER BY "Id" DESC
            LIMIT 1
            """;
        command.Parameters.AddWithValue("tenant", TenantId);
        command.Parameters.AddWithValue("aggregate", aggregateType);
        command.Parameters.AddWithValue("id", aggregateId);
        command.Parameters.AddWithValue("action", action);
        Assert.Equal(expectedActor, (string?)await command.ExecuteScalarAsync());
    }

    private static async Task AssertRejectedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        string expectedSqlState = PostgresErrorCodes.ObjectNotInPrerequisiteState)
    {
        var savepoint = $"receivables_cert_{Interlocked.Increment(ref savepointSequence)}";
        await transaction.SaveAsync(savepoint);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(expectedSqlState, exception.SqlState);
        await transaction.RollbackAsync(savepoint);
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

    private static string StatementInsertSql(
        long id,
        long customerId,
        long? currencyId,
        string status,
        string? statementNumber = null,
        string? finalizedBy = null,
        string finalizedOn = "NULL",
        int cutoffDay = 22,
        long? supersedesId = null,
        int revision = 1,
        string? correctionReason = null)
    {
        var content = status == "Draft"
            ? "<html><body>Statement {{STATEMENT_NUMBER}}</body></html>"
            : $"<html><body>Statement {statementNumber}</body></html>";
        return $"""
            INSERT INTO "CustomerStatements"
                ("Id", "BusinessUnitId", "CustomerId", "CurrencyId", "SupersedesStatementId",
                 "StatementNumber", "Status", "PeriodStart", "CutoffAt", "CapturedOn", "Revision",
                 "OpeningBalance", "DebitTotal", "CreditTotal", "UnappliedCash", "ClosingBalance",
                 "NetCustomerPosition", "AgingCurrent", "Aging1To30", "Aging31To60", "Aging61To90",
                 "AgingOver90", "SourceFingerprint", "SnapshotHash", "ArtifactHash", "ArtifactReference",
                 "ArtifactMediaType", "ArtifactContent", "GeneratorVersion", "TemplateVersion",
                 "IssuerNameSnapshot", "CustomerNameSnapshot", "BillingAddressSnapshot", "IdempotencyKey",
                 "RequestHash", "Version", "CreatedBy", "CreatedOn", "FinalizedBy", "FinalizedOn",
                 "CorrectionReason")
            VALUES
                ({id}, {TenantId}, {customerId}, {SqlLong(currencyId)}, {SqlLong(supersedesId)},
                 {SqlText(statementNumber)}, '{status}', timestamp '2026-07-01',
                 timestamp '2026-07-{cutoffDay:00}', timestamp '2026-07-26', {revision},
                 0, 100, 0, 0, 100, 100, 100, 0, 0, 0, 0, repeat('a', 64), repeat('b', 64),
                 '{Sha256(content)}', NULL, 'text/html', '{content}', 'cert-1', 'template-1', 'Nexora',
                 'Certification Customer', 'Certification Address', 'pg-statement-{id}', repeat('d', 64),
                 1, 'statement-maker', timestamp '2026-07-23', {SqlText(finalizedBy)}, {finalizedOn},
                 {SqlText(correctionReason)})
            """;
    }

    private static string PolicyInsertSql(
        long id,
        int version,
        string status,
        string createdBy,
        string? approvedBy = null,
        long businessUnitId = TenantId)
    {
        var approvedOn = approvedBy is null ? "NULL" : "timestamp '2026-07-23 09:00:00'";
        return $"""
            INSERT INTO "DunningPolicies"
                ("Id", "BusinessUnitId", "PolicyVersion", "Name", "Status", "JurisdictionCode",
                 "TimeZoneId", "GraceDays", "CadenceDays", "MaximumStage", "MinimumOverdueAmount",
                 "QuietHoursStart", "QuietHoursEnd", "TemplateVersion", "IdempotencyKey", "RequestHash",
                 "Version", "CreatedBy", "CreatedOn", "ApprovedBy", "ApprovedOn")
            VALUES ({id}, {businessUnitId}, {version}, 'Certification policy {id}', '{status}', 'US',
                    'America/New_York', 1, 7, 3, 10, 20, 8, 'v1', 'pg-policy-{id}', repeat('1', 64),
                    1, '{createdBy}', timestamp '2026-07-23', {SqlText(approvedBy)}, {approvedOn})
            """;
    }

    private static string PolicyStepInsertSql(long id, long policyId) => $"""
        INSERT INTO "DunningPolicySteps"
            ("Id", "BusinessUnitId", "DunningPolicyId", "Stage", "MinimumDaysPastDue",
             "MinimumAmount", "WaitDaysAfterPriorStage", "Channel", "TemplateVersion",
             "RequiresApproval", "EscalationRole", "MaximumAttempts")
        VALUES ({id}, {TenantId}, {policyId}, 1, 1, 10, 0, 'Email', 'v1', true, 'Collector', 3)
        """;

    private static string ContactInsertSql(
        long id,
        bool isActive = true,
        string? deactivatedBy = null,
        long customerId = CustomerId,
        string? providerSignature = null)
    {
        var deactivatedOn = deactivatedBy is null ? "NULL" : "timestamp '2026-07-23 11:00:00'";
        return $"""
            INSERT INTO "FinanceCommunicationContacts"
                ("Id", "BusinessUnitId", "CustomerId", "Purpose", "Channel", "DestinationToken",
                 "MaskedDestination", "IsVerified", "IsActive", "EffectiveFrom", "VerificationEvidenceReference",
                 "VerificationProviderEventId", "ProviderSignature", "IdempotencyKey", "RequestHash", "Version", "CreatedBy",
                 "CreatedOn", "DeactivatedBy", "DeactivatedOn", "DeactivationReason")
            VALUES ({id}, {TenantId}, {customerId}, 'Collections', 'Email', 'vault:contact:{id}',
                    'r***@example.com', true, {isActive.ToString().ToLowerInvariant()}, timestamp '2026-07-01',
                    'provider-evidence:{id}', '{GuidFromId(id)}', '{providerSignature ?? ContactProviderSignature(id, customerId)}',
                    'pg-contact-{id}', repeat('2', 64), 1,
                    'contact-maker', timestamp '2026-07-23', {SqlText(deactivatedBy)}, {deactivatedOn},
                    {SqlText(deactivatedBy is null ? null : "direct non-initial state")})
            """;
    }

    private static string ProfileInsertSql(long id, long policyId, long? currencyId) => $"""
        INSERT INTO "CustomerCollectionProfiles"
            ("Id", "BusinessUnitId", "CustomerId", "CurrencyId", "DunningPolicyId", "Locale",
             "TimeZoneId", "AutomaticDeliveryAllowed", "IsOnHold", "Version", "CreatedBy", "CreatedOn")
        VALUES ({id}, {TenantId}, {CustomerId}, {SqlLong(currencyId)}, {policyId}, 'en',
                'America/New_York', false, false, 1, 'profile-maker', timestamp '2026-07-23')
        """;

    private static string ControlInsertSql(long id, string status, string? resolvedBy) => $"""
        INSERT INTO "CollectionControls"
            ("Id", "BusinessUnitId", "CustomerId", "ControlType", "Status", "ReasonCode", "Reason",
             "EvidenceReference", "EffectiveFrom", "IdempotencyKey", "RequestHash", "Version", "CreatedBy",
             "CreatedOn", "ResolvedBy", "ResolvedOn", "ResolutionReason", "ResolutionEvidenceReference")
        VALUES ({id}, {TenantId}, {CustomerId}, 'LegalHold', '{status}', 'LEGAL',
                'Certification legal hold', 'evidence:{id}', timestamp '2026-07-01', 'pg-control-{id}',
                repeat('3', 64), 1, 'control-maker', timestamp '2026-07-23', {SqlText(resolvedBy)},
                {(resolvedBy is null ? "NULL" : "timestamp '2026-07-23 12:00:00'")},
                {SqlText(resolvedBy is null ? null : "resolved directly")},
                {SqlText(resolvedBy is null ? null : $"resolution:{id}")})
        """;

    private static string CaseInsertSql(
        long id,
        long policyId,
        long statementId,
        string status,
        long? currencyId = CurrencyId,
        long customerId = CustomerId) => $"""
        INSERT INTO "DunningCases"
            ("Id", "BusinessUnitId", "CustomerId", "CurrencyId", "DunningPolicyId",
             "CustomerStatementId", "Status", "CurrentStage", "ExposureAtOpen", "CurrentExposure",
             "OldestDueDate", "NextActionOn", "IdempotencyKey", "RequestHash", "Version", "CreatedBy",
             "CreatedOn", "StatusReason", "EvidenceReference")
        VALUES ({id}, {TenantId}, {customerId}, {SqlLong(currencyId)}, {policyId}, {statementId}, '{status}',
                0, 100, 100, timestamp '2026-06-01', timestamp '2026-07-24', 'pg-case-{id}',
                repeat('4', 64), 1, 'case-maker', timestamp '2026-07-23',
                {(status == "Open" ? "NULL" : "'direct non-initial state'")},
                {(status == "Open" ? "NULL" : $"'case-evidence:{id}'")})
        """;

    private static string PromiseInsertSql(long id, long caseId, string status) => $"""
        INSERT INTO "PromisesToPay"
            ("Id", "BusinessUnitId", "DunningCaseId", "Amount", "PromisedOn", "DueOn", "Status",
             "EvidenceReference", "IdempotencyKey", "RequestHash", "Version", "CreatedBy", "CreatedOn",
             "ClosedBy", "ClosedOn", "ClosureEvidenceReference")
        VALUES ({id}, {TenantId}, {caseId}, 25, timestamp '2026-07-23', timestamp '2026-07-30',
                '{status}', 'promise-evidence:{id}', 'pg-promise-{id}', repeat('5', 64), 1,
                'promise-maker', timestamp '2026-07-23',
                {(status == "Open" ? "NULL" : "'promise-closer'")},
                {(status == "Open" ? "NULL" : "timestamp '2026-07-23 12:00:00'")},
                {(status == "Open" ? "NULL" : $"'promise-close-evidence:{id}'")})
        """;

    private static string RunInsertSql(long id, long policyId, string status) => $"""
        INSERT INTO "DunningRuns"
            ("Id", "BusinessUnitId", "DunningPolicyId", "CutoffAt", "Status", "CandidateCount",
             "NoticeCount", "SuppressedCount", "FailedCount", "IdempotencyKey", "RequestHash", "Version",
             "CreatedBy", "CreatedOn", "CompletedOn", "CompletionEvidenceReference")
        VALUES ({id}, {TenantId}, {policyId}, timestamp '2026-07-23', '{status}', 1, 1, 0, 0,
                'pg-run-{id}', repeat('6', 64), 1, 'run-maker', timestamp '2026-07-23',
                {(status == "Pending" ? "NULL" : "timestamp '2026-07-23 12:00:00'")},
                {(status == "Pending" ? "NULL" : $"'run-evidence:{id}'")})
        """;

    private static string NoticeInsertSql(
        long id,
        long caseId,
        long statementId,
        long contactId,
        string status,
        string? approvedBy = null,
        string? releasedBy = null,
        string? artifactContent = null,
        string? artifactHash = null)
    {
        artifactContent ??= "<html><body>Governed collection notice</body></html>";
        artifactHash ??= NoticeArtifactHash(artifactContent);
        return $"""
            INSERT INTO "DunningNotices"
                ("Id", "BusinessUnitId", "DunningCaseId", "CustomerStatementId",
                 "FinanceCommunicationContactId", "Stage", "Status", "SnapshotExposure", "SnapshotHash",
                 "TemplateVersion", "Locale", "Subject", "ArtifactMediaType", "ArtifactContent", "ArtifactHash",
                 "IdempotencyKey", "RequestHash", "Version", "CreatedBy", "CreatedOn", "ApprovedBy",
                 "ApprovedOn", "ReleasedBy", "ReleasedOn")
            VALUES ({id}, {TenantId}, {caseId}, {statementId}, {contactId}, 1, '{status}', 100,
                    repeat('7', 64), 'v1', 'en', 'Certification collection notice', 'text/html',
                    '{artifactContent}', '{artifactHash}', 'pg-notice-{id}', repeat('8', 64), 1,
                    'notice-maker', timestamp '2026-07-23', {SqlText(approvedBy)},
                    {(approvedBy is null ? "NULL" : "timestamp '2026-07-23 10:00:00'")},
                    {SqlText(releasedBy)},
                    {(releasedBy is null ? "NULL" : "timestamp '2026-07-23 10:30:00'")})
            """;
    }

    private static string DeliveryAttemptInsertSql(
        long id, long noticeId, Guid providerEventId, string artifactHash,
        string? providerReference = null, DateTime? providerOccurredAt = null,
        string? providerSignature = null)
    {
        var currentSecond = DateTime.UtcNow;
        currentSecond = currentSecond.AddTicks(-(currentSecond.Ticks % TimeSpan.TicksPerSecond));
        var providerOccurredOn = providerOccurredAt ?? currentSecond;
        var provider = providerReference ?? $"provider:{id}";
        var signedEvidence = $"signed-evidence:{id}";
        return $"""
        INSERT INTO "DunningDeliveryAttempts"
            ("Id", "BusinessUnitId", "DunningNoticeId", "ProviderEventId", "AttemptNumber", "Status",
             "MaskedDestination", "ArtifactHash", "TemplateVersion", "ProviderReference", "ProviderOccurredOn",
             "SignedEvidenceReference", "ProviderSignature", "OccurredOn", "RecordedBy")
        VALUES ({id}, {TenantId}, {noticeId}, '{providerEventId}', 1, 'Delivered', 'r***@example.com',
                '{artifactHash}', 'v1', '{provider}', timestamp '{providerOccurredOn:yyyy-MM-dd HH:mm:ss}',
                '{signedEvidence}', '{providerSignature ?? DeliveryProviderSignature(noticeId, providerEventId, provider,
                    providerOccurredOn, signedEvidence)}', timestamp '{providerOccurredOn:yyyy-MM-dd HH:mm:ss}' + interval '1 second',
                'delivery-recorder')
        """;
    }

    private static string SqlLong(long? value) => value?.ToString() ?? "NULL";

    private static string SqlText(string? value) => value is null
        ? "NULL"
        : $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string NoticeArtifactHash(string content) =>
        Sha256(string.Join('\n', "Certification collection notice", "text/html", "en", content));

    private static string ContactProviderSignature(long id, long customerId)
    {
        var effective = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        var canonical = string.Join('\n', TenantId, customerId, "Collections", "Email", $"vault:contact:{id}",
            "r***@example.com", new DateTimeOffset(effective).ToUnixTimeMilliseconds(), string.Empty,
            $"provider-evidence:{id}", GuidFromId(id).ToString("D"));
        return Hmac(ContactProviderSecret, canonical);
    }

    private static string DeliveryProviderSignature(
        long noticeId, Guid providerEventId, string providerReference,
        DateTime providerOccurredOn, string signedEvidence)
    {
        var canonical = string.Join('\n', TenantId, noticeId, "true", providerEventId.ToString("D"),
            providerReference, new DateTimeOffset(DateTime.SpecifyKind(providerOccurredOn, DateTimeKind.Utc))
                .ToUnixTimeMilliseconds(), string.Empty, signedEvidence);
        return Hmac(DeliveryProviderSecret, canonical);
    }

    private static string Hmac(string secret, string canonical) => Convert.ToHexString(
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();

    private static string AuditActorSignature(string actor)
        => Hmac(AuditActorSecret, $"{TenantId}\n{actor}");

    private static Guid GuidFromId(long id) => new($"00000000-0000-0000-{id % 10_000:0000}-{id % 1_000_000_000_000:000000000000}");

    private sealed record CertificationGraph(
        long Seed,
        long StatementId,
        long PolicyId,
        long ContactId,
        long CaseId,
        long NoticeId,
        string NoticeArtifactHash);
}
