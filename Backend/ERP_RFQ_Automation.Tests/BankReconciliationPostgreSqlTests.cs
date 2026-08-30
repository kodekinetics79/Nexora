using System.Security.Cryptography;
using System.Text;
using ERP_RFQ_Automation.BankReconciliation;
using ERP_RFQ_Automation.BankReconciliation.Parsing;
using ERP_RFQ_Automation.BankReconciliation.Services;
using ERP_RFQ_Automation.GeneralLedger;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class BankReconciliationPostgreSqlTests(PostgreSqlTestDatabase database)
{
    private readonly PostgreSqlTestDatabase _database = database;

    [Fact]
    [Trait("Category", "PostgreSQL")]
    // Squash note: this method opened by asserting that the three bank-reconciliation migration
    // ids ('..._GovernBankReconciliation', '..._CompleteBankReconciliationEvidence',
    // '..._GovernTreasuryRulesAdjustmentsAndCashBridge') were present in "__EFMigrationsHistory".
    // 20260811033109_SquashedSchemaBaseline erased all three. Their combined product — eleven
    // forced-RLS tables, the twelve evidence and cash-bridge columns, and everything below — is
    // asserted against the live catalogue, which is the property the ids were standing in for.
    public async Task BankReconciliationSchemaIsInstalledWithForcedTenantIsolation()
    {
        await using var connection = await _database.OpenConnectionAsync();

        await using var tables = connection.CreateCommand();
        tables.CommandText = """
            SELECT count(*) FROM pg_class
            WHERE relnamespace = 'public'::regnamespace
              AND relname = ANY(ARRAY[
                  'BankAccounts', 'BankStatementImports', 'BankStatements', 'BankStatementLines',
                  'ReconciliationRuns', 'ReconciliationMatches', 'ReconciliationAllocations',
                  'BankMatchingRules', 'ReconciliationRunRules', 'BankAdjustments',
                  'BankAdjustmentDistributions'])
              AND relrowsecurity AND relforcerowsecurity
            """;
        Assert.Equal(11L, (long)(await tables.ExecuteScalarAsync())!);

        await using var columns = connection.CreateCommand();
        columns.CommandText = """
            SELECT count(*) FROM information_schema.columns
            WHERE (table_name, column_name) IN (
                ('ReconciliationRuns','RuleSetHash'), ('ReconciliationRuns','RuleSetSnapshotOn'),
                ('ReconciliationMatches','BankMatchingRuleId'), ('ReconciliationMatches','RuleDefinitionHash'),
                ('LedgerBooks','ReceivablesControlAccountId'), ('LedgerBooks','UnappliedCashAccountId'),
                ('CustomerPayments','BankAccountId'), ('CustomerPayments','AccountingBridgeRequired'),
                ('CustomerPayments','JournalEntryId'), ('CustomerPayments','ReversalJournalEntryId'),
                ('CustomerRefunds','BankAccountId'), ('CustomerRefunds','JournalEntryId'))
            """;
        Assert.Equal(12L, (long)(await columns.ExecuteScalarAsync())!);

        await using var triggers = connection.CreateCommand();
        triggers.CommandText = """
            SELECT count(*) FROM pg_trigger
            WHERE NOT tgisinternal AND tgname = ANY(ARRAY[
                'trg_bankmatchingrules_guard', 'trg_reconciliationrunrules_guard',
                'trg_reconciliationruns_rules', 'trg_reconciliationmatches_rule',
                'trg_bankadjustments_guard', 'trg_bankadjustmentdistributions_guard',
                'trg_bankadjustments_validate', 'trg_customerpayments_cash_bridge',
                'trg_customerrefunds_cash_bridge', 'trg_bankmatchingrules_evidence',
                'trg_bankadjustments_evidence'])
            """;
        Assert.Equal(11L, (long)(await triggers.ExecuteScalarAsync())!);

        await using var parity = _database.ContextFor(null);
        Assert.Empty(await parity.Database.GetPendingMigrationsAsync());
    }

    /// <summary>
    /// SQUASH NOTE — this replaces UpgradeFrom02000_BackfillsProtectedRunAndExactMatchRuleEvidence.
    ///
    /// That test built a database at 20260724002000_CompleteBankReconciliationEvidence, wrote a
    /// reconciliation run and a DeterministicExact match from before rule-set evidence existed, and
    /// upgraded to 20260724003000_GovernTreasuryRulesAdjustmentsAndCashBridge to prove the
    /// migration reconstructed the evidence chain — run to rule snapshot to rule to match, with
    /// matching definition hashes — rather than leaving matches that claimed a rule nobody could
    /// name. It then asserted the DOWN path REFUSED, with "cannot downgrade treasury governance".
    ///
    /// 20260811033109_SquashedSchemaBaseline erased both ids. The reconstruction and the
    /// downgrade refusal are migration-time behaviour and cannot survive a squash. The RULE the
    /// reconstruction was restoring is not migration-time at all — it is a CHECK constraint, and it
    /// is asserted here:
    ///
    ///   * a run always carries a rule-set hash and a snapshot time (NOT NULL, no exceptions)
    ///     — asserted on the catalogue;
    ///   * a DeterministicExact match MUST name the rule it fired and the definition hash it fired
    ///     against — asserted BY WRITING one that does not, and watching the database refuse it;
    ///   * and, symmetrically, a Manual match must NOT carry them, so a human decision cannot be
    ///     dressed up as an automated one — this direction is asserted only by the constraint being
    ///     present and convalidated, NOT by an attempted write. It is the weaker of the two.
    ///
    /// The snapshot chain itself — that a run can only snapshot ACTIVE rules belonging to its own
    /// bank account — is RuleSnapshot_RejectsDraftAndWrongBankAccountRules below.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Deterministic_matches_must_name_the_rule_they_fired()
    {
        var fixture = await SeedFixtureAsync(991_110, 991_210, "USD", createJournal: false);
        long runId;
        await using (var context = _database.ContextFor(null))
        {
            runId = (await new BankReconciliationService(context).CreateRunAsync(fixture.TenantId,
                "rule-evidence-run", new(fixture.Statement.Id, DateTime.UtcNow.Date), "preparer@test")).Id;
        }

        await using var connection = await _database.OpenConnectionAsync();
        await using (var schema = connection.CreateCommand())
        {
            schema.CommandText = """
                SELECT
                    (SELECT count(*)::int FROM information_schema.columns
                     WHERE table_schema = 'public' AND table_name = 'ReconciliationRuns'
                       AND column_name IN ('RuleSetHash', 'RuleSetSnapshotOn')
                       AND is_nullable = 'NO') = 2,
                    (SELECT count(*)::int FROM information_schema.columns
                     WHERE table_schema = 'public' AND table_name = 'ReconciliationMatches'
                       AND column_name IN ('BankMatchingRuleId', 'RuleDefinitionHash')) = 2,
                    EXISTS (SELECT 1 FROM pg_constraint
                        WHERE conname = 'CK_ReconciliationMatches_ManualEvidence' AND convalidated);
                """;
            await using var reader = await schema.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            for (var index = 0; index < 3; index++)
                Assert.True(reader.GetBoolean(index), $"Rule evidence assertion {index + 1} failed.");
        }

        await using var transaction = await connection.BeginTransactionAsync();
        await using var unevidenced = connection.CreateCommand();
        unevidenced.Transaction = transaction;
        unevidenced.CommandText = $"""
            INSERT INTO "ReconciliationMatches"
                ("BusinessUnitId","ReconciliationRunId","MatchType","Confidence","RuleCode",
                 "RuleVersion","MatchReason","EvidenceReference","BankMatchingRuleId","RuleDefinitionHash",
                 "Status","IdempotencyKey","RequestHash","Version","CreatedBy","CreatedOn")
            VALUES ({fixture.TenantId},{runId},'DeterministicExact',1,'EXACT_AMOUNT_DIRECTION_V1',1,
                    NULL,NULL,NULL,NULL,'Proposed','unevidenced-exact-match',repeat('9',64),1,
                    'matcher@test',now());
            """;
        var error = await Assert.ThrowsAsync<PostgresException>(() => unevidenced.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        await transaction.RollbackAsync();
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task StatementEvidence_IsImmutableAndTenantCompositeKeysAndRlsBlockCrossTenantAccess()
    {
        var fixture = await SeedFixtureAsync(991_101, 991_201, "USD", createJournal: false);
        await SeedTenantAsync(991_102, 991_202, "USD");

        await using var connection = await _database.OpenConnectionAsync();
        await using (var update = connection.CreateCommand())
        {
            update.CommandText = "UPDATE \"BankStatements\" SET \"StatementReference\" = 'forged' WHERE \"Id\" = @id";
            update.Parameters.AddWithValue("id", fixture.Statement.Id);
            Assert.Equal(PostgresErrorCodes.ObjectNotInPrerequisiteState,
                (await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync())).SqlState);
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.CommandText = "DELETE FROM \"BankStatementLines\" WHERE \"Id\" = @id";
            delete.Parameters.AddWithValue("id", fixture.Statement.Lines.Single().Id);
            Assert.Equal(PostgresErrorCodes.ObjectNotInPrerequisiteState,
                (await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync())).SqlState);
        }

        await using (var truncate = connection.CreateCommand())
        {
            truncate.CommandText = "TRUNCATE TABLE \"BankStatementLines\"";
            Assert.Contains((await Assert.ThrowsAsync<PostgresException>(() => truncate.ExecuteNonQueryAsync())).SqlState,
                new[] { PostgresErrorCodes.ObjectNotInPrerequisiteState, PostgresErrorCodes.FeatureNotSupported });
        }

        await using (var crossTenantInsert = connection.CreateCommand())
        {
            crossTenantInsert.CommandText = """
                INSERT INTO "BankStatementLines"
                    ("BusinessUnitId", "BankStatementId", "BankAccountId", "SourceOrdinal", "BookingDate",
                     "ValueDate", "SignedAmount", "Direction", "OriginalAmountText", "LineFingerprint")
                VALUES (@otherTenant, @statementId, @bankAccountId, 2, @bookingDate, @bookingDate,
                        10, 'Credit', '10.00', @fingerprint)
                """;
            crossTenantInsert.Parameters.AddWithValue("otherTenant", 991_102L);
            crossTenantInsert.Parameters.AddWithValue("statementId", fixture.Statement.Id);
            crossTenantInsert.Parameters.AddWithValue("bankAccountId", fixture.BankAccount.Id);
            crossTenantInsert.Parameters.AddWithValue("bookingDate", DateTime.UtcNow.Date);
            crossTenantInsert.Parameters.AddWithValue("fingerprint", new string('9', 64));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation,
                (await Assert.ThrowsAsync<PostgresException>(() => crossTenantInsert.ExecuteNonQueryAsync())).SqlState);
        }

        await using var rlsTransaction = await connection.BeginTransactionAsync();
        await using var rls = connection.CreateCommand();
        rls.Transaction = rlsTransaction;
        rls.CommandText = $"""
            SET LOCAL ROLE nexora_tenant_app;
            SET LOCAL nexora.business_unit_id = '991102';
            SELECT count(*) FROM "BankStatements" WHERE "Id" = {fixture.Statement.Id};
            """;
        Assert.Equal(0L, (long)(await rls.ExecuteScalarAsync())!);
        await rlsTransaction.RollbackAsync();
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task DirectForgedApproval_IsRejectedByDatabaseCertification()
    {
        var fixture = await SeedFixtureAsync(991_103, 991_203, "USD", createJournal: false);
        ReconciliationRunDto draft;
        await using (var context = _database.ContextFor(null))
        {
            var service = new BankReconciliationService(context);
            draft = await service.CreateRunAsync(fixture.TenantId, "forged-approval-run",
                new(fixture.Statement.Id, DateTime.UtcNow.Date), "preparer@test");
        }

        await using var connection = await _database.OpenConnectionAsync();
        await using (var submit = connection.CreateCommand())
        {
            submit.CommandText = """
                UPDATE "ReconciliationRuns" SET "Status" = 'InReview', "Version" = "Version" + 1,
                    "SubmittedBy" = 'submitter@test', "SubmittedOn" = now() WHERE "Id" = @id
                """;
            submit.Parameters.AddWithValue("id", draft.Id);
            Assert.Equal(1, await submit.ExecuteNonQueryAsync());
        }
        await using var forged = connection.CreateCommand();
        forged.CommandText = """
            UPDATE "ReconciliationRuns"
            SET "Status" = 'Approved', "Version" = "Version" + 1,
                "ApprovedBy" = 'forged-approver@test', "ApprovedOn" = now(),
                "ApprovalReason" = 'Forged direct database approval',
                "EvidenceReference" = 'FORGED-EVIDENCE',
                "CertificateHash" = repeat('f', 64), "CertificateLineCount" = 1,
                "CertificateJournalCount" = 1, "UnexplainedDifference" = 0
            WHERE "Id" = @id AND "Version" = @version
            """;
        forged.Parameters.AddWithValue("id", draft.Id);
        forged.Parameters.AddWithValue("version", draft.Version + 1);
        Assert.Equal(PostgresErrorCodes.CheckViolation,
            (await Assert.ThrowsAsync<PostgresException>(() => forged.ExecuteNonQueryAsync())).SqlState);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ConcurrentConfirmedMatches_CannotOverAllocateSameBankOrJournalLine()
    {
        var fixture = await SeedFixtureAsync(991_104, 991_204, "USD", createJournal: true);
        ReconciliationMatchDto first;
        ReconciliationMatchDto second;
        await using (var context = _database.ContextFor(null))
        {
            var service = new BankReconciliationService(context);
            var run = await service.CreateRunAsync(fixture.TenantId, "race-run",
                new(fixture.Statement.Id, DateTime.UtcNow.Date), "preparer@test");
            var allocation = new ReconciliationAllocationRequest(fixture.Statement.Lines.Single().Id,
                fixture.CashJournalLineId!.Value, 75m, 75m);
            first = await service.CreateMatchAsync(fixture.TenantId, "race-match-one",
                new(run.Id, "Concurrent allocation test evidence", "TEST-RACE-EVIDENCE-ONE", [allocation]),
                "matcher-one@test");
            second = await service.CreateMatchAsync(fixture.TenantId, "race-match-two",
                new(run.Id, "Concurrent allocation test evidence", "TEST-RACE-EVIDENCE-TWO", [allocation]),
                "matcher-two@test");
        }

        var outcomes = await Task.WhenAll(ConfirmDirectAsync(first.Id), ConfirmDirectAsync(second.Id));
        Assert.Single(outcomes, x => x is null);
        var failure = Assert.Single(outcomes, x => x is not null)!;
        Assert.Equal(PostgresErrorCodes.CheckViolation, failure.SqlState);

        await using var verify = _database.ContextFor(null);
        var confirmed = await verify.ReconciliationMatches.IgnoreQueryFilters()
            .CountAsync(x => x.BusinessUnitId == fixture.TenantId && x.Status == BankMatchStatuses.Confirmed);
        var allocated = await (from allocation in verify.ReconciliationAllocations.IgnoreQueryFilters()
                               join match in verify.ReconciliationMatches.IgnoreQueryFilters()
                                   on new { allocation.BusinessUnitId, Id = allocation.ReconciliationMatchId }
                                   equals new { match.BusinessUnitId, match.Id }
                               where allocation.BusinessUnitId == fixture.TenantId
                                     && match.Status == BankMatchStatuses.Confirmed
                               select allocation.BankAmount).SumAsync();
        Assert.Equal(1, confirmed);
        Assert.Equal(75m, allocated);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task RuleSnapshot_RejectsDraftAndWrongBankAccountRules()
    {
        var fixture = await SeedFixtureAsync(991_105, 991_205, "USD", createJournal: false);
        long runId;
        BankMatchingRuleDto draftRule;
        BankMatchingRuleDto wrongAccountRule;
        await using (var context = _database.ContextFor(null))
        {
            var bank = new BankReconciliationService(context);
            runId = (await bank.CreateRunAsync(fixture.TenantId, "snapshot-guard-run",
                new(fixture.Statement.Id, DateTime.UtcNow.Date), "preparer@test")).Id;
            draftRule = await bank.CreateMatchingRuleAsync(fixture.TenantId, "snapshot-draft-rule",
                ExactRule("DRAFT_SNAPSHOT_RULE", fixture.BankAccount.Id), "rule-maker@test");
            var ledger = new GeneralLedgerService(context);
            var otherCash = await ledger.CreateAccountAsync(fixture.TenantId, "snapshot-other-cash",
                new("1010", "Other bank cash", LedgerAccountCategories.Asset, LedgerNormalBalances.Debit,
                    991_205, false, true), "account-maker@test");
            var otherBank = await bank.CreateBankAccountAsync(fixture.TenantId, "snapshot-other-bank",
                new("Other operating account", "Integration Test Bank", "****9999", "snapshot-other-account",
                    991_205, otherCash.Id, DateTime.UtcNow.Date), "treasury-maker@test");
            var wrongDraft = await bank.CreateMatchingRuleAsync(fixture.TenantId, "snapshot-wrong-account-rule",
                ExactRule("WRONG_ACCOUNT_RULE", otherBank.Id), "other-rule-maker@test");
            var approved = await bank.TransitionMatchingRuleAsync(fixture.TenantId, wrongDraft.Id, "approve",
                RuleAction(wrongDraft.RecordVersion), "other-rule-checker@test");
            wrongAccountRule = await bank.TransitionMatchingRuleAsync(fixture.TenantId, approved.Id, "activate",
                RuleAction(approved.RecordVersion), "other-rule-checker@test");
        }

        await AssertSnapshotRejectedAsync(fixture.TenantId, runId, draftRule, 2);
        await AssertSnapshotRejectedAsync(fixture.TenantId, runId, wrongAccountRule, 2);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task TreasuryLifecycle_RejectsActorForgeryDespiteValidSignedEnvelope()
    {
        var fixture = await SeedFixtureAsync(991_106, 991_206, "USD", createJournal: false);
        BankMatchingRuleDto rule;
        await using (var context = _database.ContextFor(null))
        {
            rule = await new BankReconciliationService(context).CreateMatchingRuleAsync(fixture.TenantId,
                "actor-forgery-rule", ExactRule("ACTOR_FORGERY_RULE", fixture.BankAccount.Id), "rule-maker@test");
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO public."FinanceProviderSecrets" ("Name", "Secret", "UpdatedOn")
                VALUES ({"AuditActor"}, {ActorSecret}, now())
                ON CONFLICT ("Name") DO UPDATE SET "Secret" = EXCLUDED."Secret", "UpdatedOn" = EXCLUDED."UpdatedOn"
                """);
        }

        const string signedActor = "signed-rule-checker@test";
        var envelope = ActorEnvelope(fixture.TenantId, signedActor);
        await using var connection = await _database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var forged = connection.CreateCommand();
        forged.Transaction = transaction;
        forged.CommandText = $"""
            SET LOCAL ROLE nexora_tenant_app;
            SET LOCAL nexora.business_unit_id = '{fixture.TenantId}';
            SET LOCAL nexora.actor_id = '{signedActor}';
            SET LOCAL nexora.actor_signature = '{Signature(fixture.TenantId, signedActor)}';
            SET LOCAL nexora.gl_issued_at = '{envelope.IssuedAt}';
            SET LOCAL nexora.gl_expires_at = '{envelope.ExpiresAt}';
            SET LOCAL nexora.gl_nonce = '{envelope.Nonce}';
            SET LOCAL nexora.gl_signature = '{envelope.Signature}';
            UPDATE "BankMatchingRules"
            SET "Status" = 'Approved', "RecordVersion" = "RecordVersion" + 1,
                "ApprovedBy" = 'forged-rule-checker@test', "ApprovedOn" = now(),
                "LifecycleReason" = 'Forged lifecycle actor should be rejected',
                "EvidenceReference" = 'evidence/forged-actor'
            WHERE "Id" = {rule.Id};
            """;
        Assert.Equal(PostgresErrorCodes.ObjectNotInPrerequisiteState,
            (await Assert.ThrowsAsync<PostgresException>(() => forged.ExecuteNonQueryAsync())).SqlState);
        await transaction.RollbackAsync();
    }

    [Theory]
    [InlineData("approve", 991_107L, 991_207L)]
    [InlineData("reject", 991_108L, 991_208L)]
    [InlineData("reverse", 991_109L, 991_209L)]
    [Trait("Category", "PostgreSQL")]
    public async Task BankAdjustment_MixedCaseMakerCannotPerformIndependentTransition(
        string action, long tenantId, long currencyId)
    {
        const string maker = "Adjustment.Maker@Test";
        const string mixedCaseMaker = "adjustment.maker@test";
        var fixture = await SeedFixtureAsync(tenantId, currencyId, "USD", createJournal: false);
        long adjustmentId;
        long expectedVersion;
        SourceJournalResult? journal = null;
        SourceJournalResult? reversal = null;
        await using (var context = _database.ContextFor(null))
        {
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO public."FinanceProviderSecrets" ("Name", "Secret", "UpdatedOn")
                VALUES ({"AuditActor"}, {ActorSecret}, now())
                ON CONFLICT ("Name") DO UPDATE SET "Secret" = EXCLUDED."Secret", "UpdatedOn" = EXCLUDED."UpdatedOn"
                """);
            var period = await context.AccountingPeriods.SingleAsync(x => x.BusinessUnitId == tenantId);
            var distribution = await context.LedgerAccounts.SingleAsync(x =>
                x.BusinessUnitId == tenantId && x.Code == "4000");
            var service = new BankAdjustmentService(context, new InternalSourceJournalPostingService(context));
            var draft = await service.CreateAsync(tenantId, $"mixed-case-{action}",
                new(fixture.BankAccount.Id, fixture.Statement.Lines.Single().Id, period.Id,
                    DateTime.UtcNow.Date, "BankFee", "Mixed-case maker governance adjustment", 25m,
                    "evidence/mixed-case-maker", [new(distribution.Id, 25m, "Governed distribution")]), maker);
            var submitted = await service.TransitionAsync(tenantId, draft.Id, "submit",
                new(draft.Version), maker);
            adjustmentId = submitted.Id;
            expectedVersion = submitted.Version;
            if (action == "approve")
            {
                var entity = await LoadAdjustmentAsync(context, tenantId, adjustmentId);
                journal = await new InternalSourceJournalPostingService(context)
                    .CreateAndPostBankAdjustmentAsync(entity, entity.BankAccount, mixedCaseMaker, default);
            }
            else if (action == "reverse")
            {
                var posted = await service.TransitionAsync(tenantId, submitted.Id, "approve",
                    new(submitted.Version), "independent-checker@test");
                expectedVersion = posted.Version;
                context.ChangeTracker.Clear();
                var entity = await LoadAdjustmentAsync(context, tenantId, adjustmentId);
                reversal = await new InternalSourceJournalPostingService(context).ReverseBankAdjustmentAsync(entity,
                    mixedCaseMaker, "Mixed-case maker attempted governed reversal",
                    "evidence/mixed-case-reversal", default);
            }
        }

        var failure = await ExecuteMixedCaseAdjustmentTransitionAsync(tenantId, adjustmentId,
            expectedVersion, action, mixedCaseMaker, journal, reversal);

        Assert.NotNull(failure);
        Assert.Contains(failure.SqlState,
            new[] { PostgresErrorCodes.CheckViolation, PostgresErrorCodes.ObjectNotInPrerequisiteState });
    }

    private async Task<PostgresException?> ConfirmDirectAsync(long matchId)
    {
        await using var connection = await _database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE "ReconciliationMatches"
                SET "Status" = 'Confirmed', "Version" = "Version" + 1,
                    "ConfirmedBy" = 'race-checker@test', "ConfirmedOn" = now()
                WHERE "Id" = @id
                """;
            command.Parameters.AddWithValue("id", matchId);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
            await transaction.CommitAsync();
            return null;
        }
        catch (PostgresException exception)
        {
            return exception;
        }
    }

    private async Task AssertSnapshotRejectedAsync(long tenantId, long runId,
        BankMatchingRuleDto rule, int evaluationOrder)
    {
        await using var connection = await _database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO "ReconciliationRunRules"
                ("BusinessUnitId","ReconciliationRunId","BankMatchingRuleId","EvaluationOrder","DefinitionHash")
            VALUES (@tenant,@run,@rule,@order,@hash)
            """;
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("run", runId);
        command.Parameters.AddWithValue("rule", rule.Id);
        command.Parameters.AddWithValue("order", evaluationOrder);
        command.Parameters.AddWithValue("hash", rule.DefinitionHash);
        Assert.Equal(PostgresErrorCodes.CheckViolation,
            (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState);
    }

    private static Task<BankAdjustment> LoadAdjustmentAsync(ErpRfqAutomationContext context,
        long tenantId, long adjustmentId)
        => context.BankAdjustments.Include(x => x.Distributions).Include(x => x.BankAccount)
            .Include(x => x.BankStatementLine).SingleAsync(x =>
                x.BusinessUnitId == tenantId && x.Id == adjustmentId);

    private async Task<PostgresException?> ExecuteMixedCaseAdjustmentTransitionAsync(long tenantId,
        long adjustmentId, long expectedVersion, string action, string actor,
        SourceJournalResult? journal, SourceJournalResult? reversal)
    {
        var envelope = ActorEnvelope(tenantId, actor);
        await using var connection = await _database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                SET LOCAL ROLE nexora_tenant_app;
                SET LOCAL nexora.business_unit_id = '{tenantId}';
                SET LOCAL nexora.actor_id = '{actor}';
                SET LOCAL nexora.actor_signature = '{Signature(tenantId, actor)}';
                SET LOCAL nexora.gl_issued_at = '{envelope.IssuedAt}';
                SET LOCAL nexora.gl_expires_at = '{envelope.ExpiresAt}';
                SET LOCAL nexora.gl_nonce = '{envelope.Nonce}';
                SET LOCAL nexora.gl_signature = '{envelope.Signature}';
                {AdjustmentTransitionSql(action)}
                """;
            command.Parameters.AddWithValue("id", adjustmentId);
            command.Parameters.AddWithValue("version", expectedVersion);
            command.Parameters.AddWithValue("actor", actor);
            command.Parameters.AddWithValue("journal", journal?.JournalEntryId ?? 0L);
            command.Parameters.AddWithValue("journalLine", journal?.BankJournalEntryLineId ?? 0L);
            command.Parameters.AddWithValue("reversal", reversal?.JournalEntryId ?? 0L);
            command.Parameters.AddWithValue("reversalLine", reversal?.BankJournalEntryLineId ?? 0L);
            await command.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
            return null;
        }
        catch (PostgresException exception) { return exception; }
    }

    private static string AdjustmentTransitionSql(string action) => action switch
    {
        "approve" => """
            UPDATE "BankAdjustments" SET "Status"='Posted',"Version"="Version"+1,
                "ApprovedBy"=@actor,"ApprovedOn"=now(),"JournalEntryId"=@journal,
                "BankJournalEntryLineId"=@journalLine WHERE "Id"=@id AND "Version"=@version;
            """,
        "reject" => """
            UPDATE "BankAdjustments" SET "Status"='Rejected',"Version"="Version"+1,
                "RejectedBy"=@actor,"RejectedOn"=now(),
                "RejectionReason"='Mixed-case maker attempted governed rejection'
            WHERE "Id"=@id AND "Version"=@version;
            """,
        "reverse" => """
            UPDATE "BankAdjustments" SET "Status"='Reversed',"Version"="Version"+1,
                "ReversedBy"=@actor,"ReversedOn"=now(),
                "ReversalReason"='Mixed-case maker attempted governed reversal',
                "ReversalEvidenceReference"='evidence/mixed-case-reversal',
                "ReversalJournalEntryId"=@reversal,"ReversalBankJournalEntryLineId"=@reversalLine
            WHERE "Id"=@id AND "Version"=@version;
            """,
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };

    private async Task<Fixture> SeedFixtureAsync(long tenantId, long currencyId, string currencyCode,
        bool createJournal)
    {
        await SeedTenantAsync(tenantId, currencyId, currencyCode);
        await using var context = _database.ContextFor(null);
        var ledger = new GeneralLedgerService(context);
        var period = await ledger.CreatePeriodAsync(tenantId, $"period-{tenantId}", CurrentPeriod(), "period-maker@test");
        var cash = await ledger.CreateAccountAsync(tenantId, $"cash-{tenantId}",
            new("1000", "Bank cash", LedgerAccountCategories.Asset, LedgerNormalBalances.Debit,
                currencyId, false, true), "account-maker@test");
        var offset = await ledger.CreateAccountAsync(tenantId, $"offset-{tenantId}",
            new("4000", "Reconciliation offset", LedgerAccountCategories.Revenue, LedgerNormalBalances.Credit,
                currencyId, false, true), "account-maker@test");

        long? cashJournalLineId = null;
        if (createJournal)
        {
            var draft = await ledger.CreateManualJournalAsync(tenantId, $"journal-{tenantId}",
                new(period.Id, currencyId, DateTime.UtcNow.Date, "Bank reconciliation test journal",
                [
                    new(cash.Id, "Cash receipt", currencyId, 1m, 100m, 0m),
                    new(offset.Id, "Receipt offset", currencyId, 1m, 0m, 100m)
                ]), "journal-maker@test");
            var posted = await ledger.PostJournalAsync(tenantId, draft.Id, new(draft.Version), "journal-checker@test");
            cashJournalLineId = posted.Lines.Single(x => x.LedgerAccountId == cash.Id).Id;
        }

        var bank = new BankReconciliationService(context);
        var accountIdentifier = $"account-{tenantId}";
        var bankAccount = await bank.CreateBankAccountAsync(tenantId, $"bank-{tenantId}",
            new("Operating account", "Integration Test Bank", "****0104", accountIdentifier,
                currencyId, cash.Id, DateTime.UtcNow.Date), "treasury-maker@test");
        var date = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var csv = "StatementReference,AccountIdentifier,Currency,PeriodStart,PeriodEnd,OpeningBalance,ClosingBalance,Ordinal,BookingDate,ValueDate,Amount,Direction,ExternalTransactionId,BankReference,TransactionCode,Counterparty,RemittanceText\n"
            + $"STMT-{tenantId},{accountIdentifier},{currencyCode},{date},{date},0.00,100.00,1,{date},{date},100.00,CREDIT,TX-{tenantId},REF-{tenantId},CREDIT,Test customer,Test receipt\n";
        var payload = Encoding.UTF8.GetBytes(csv);
        var sourceHash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var parsed = new StrictCsvBankStatementParser().Parse(csv);
        var statement = await bank.ImportStatementAsync(tenantId, $"statement-{tenantId}", bankAccount.Id,
            "CSV", $"statement-{tenantId}.csv", $"sha256:{sourceHash}", sourceHash, "test-parser-1",
            payload, parsed, "statement-importer@test");
        return new(tenantId, bankAccount, statement, cashJournalLineId);
    }

    private async Task SeedTenantAsync(long tenantId, long currencyId, string currencyCode)
    {
        await using var context = _database.ContextFor(null);
        Seed.EnsureBusinessUnit(context, tenantId);
        context.Currencies.Add(new Currency
        {
            Id = currencyId,
            BusinessUnitId = tenantId,
            Code = currencyCode,
            CurrencyName = $"Bank reconciliation currency {tenantId}",
            Symbol = currencyCode[..1],
            ExchangeRate = 1m,
            IsBaseCurrency = true,
            IsActive = true,
            CreatedBy = "tests",
            CreatedOn = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
        await new GeneralLedgerService(context).CreateBookAsync(tenantId, $"book-{tenantId}",
            new($"Primary book {tenantId}", currencyId, "UTC", 1), "controller@test");
    }

    private static CreateAccountingPeriodRequest CurrentPeriod()
        => new(DateTime.UtcNow.Year, DateTime.UtcNow.Month, "Current bank reconciliation period",
            new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1),
            new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(1).AddDays(-1));

    private static CreateBankMatchingRuleRequest ExactRule(string code, long bankAccountId)
        => new(code, bankAccountId, $"Exact rule {code}", BankMatchingRuleTypes.ExactAmountDirection,
            10, 0m, 31, BankMatchingReferenceModes.Ignore, true);

    private static BankMatchingRuleActionRequest RuleAction(long version)
        => new(version, "Independent matching rule lifecycle review", "evidence/rule-review");

    private static string Signature(long tenantId, string actor)
        => Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(ActorSecret),
            Encoding.UTF8.GetBytes($"{tenantId}\n{actor}"))).ToLowerInvariant();

    private static SignedEnvelope ActorEnvelope(long tenantId, string actor)
    {
        var issuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var expiresAt = issuedAt + 30;
        var nonce = Guid.NewGuid().ToString("D");
        var canonical = $"{tenantId}\n{actor}\n{issuedAt}\n{expiresAt}\n{nonce}";
        var signature = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(ActorSecret),
            Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return new(issuedAt, expiresAt, nonce, signature);
    }

    private sealed record SignedEnvelope(long IssuedAt, long ExpiresAt, string Nonce, string Signature);
    private const string ActorSecret = PostgreSqlTestDatabase.AuditActorSecret;

    private sealed record Fixture(long TenantId, BankAccountDto BankAccount, BankStatementDto Statement,
        long? CashJournalLineId);
}
