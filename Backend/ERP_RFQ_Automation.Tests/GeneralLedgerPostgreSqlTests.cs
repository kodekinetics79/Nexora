using System.Security.Cryptography;
using System.Text;
using ERP_RFQ_Automation.GeneralLedger;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class GeneralLedgerPostgreSqlTests(PostgreSqlTestDatabase database)
{
    private readonly PostgreSqlTestDatabase _database = database;

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task LedgerLifecycle_UsesDatabaseNumberingExactReversalAndTransactionalEvidence()
    {
        await using var context = _database.ContextFor(null);
        await SeedTenantAsync(context, LifecycleTenantId, LifecycleCurrencyId);
        var service = new GeneralLedgerService(context);
        var period = await service.CreatePeriodAsync(LifecycleTenantId, "pg-period-lifecycle",
            CurrentPeriod(), "period-maker@test");
        var cash = await service.CreateAccountAsync(LifecycleTenantId, "pg-account-cash",
            Account("1000", "Cash", LedgerAccountCategories.Asset, LedgerNormalBalances.Debit), "account-maker@test");
        var revenue = await service.CreateAccountAsync(LifecycleTenantId, "pg-account-revenue",
            Account("4000", "Revenue", LedgerAccountCategories.Revenue, LedgerNormalBalances.Credit), "account-maker@test");
        var draft = await service.CreateManualJournalAsync(LifecycleTenantId, "pg-journal-lifecycle",
            Journal(period.Id, LifecycleCurrencyId, cash.Id, revenue.Id), "journal-maker@test");
        var posted = await service.PostJournalAsync(LifecycleTenantId, draft.Id,
            new JournalActionRequest(draft.Version), "journal-checker@test");
        var reversal = await service.ReverseJournalAsync(LifecycleTenantId, posted.Id, "pg-journal-reversal",
            new JournalActionRequest(posted.Version, "Approved duplicate journal correction",
                "CASE-PG-GL-REV-001", DateTime.UtcNow), "journal-controller@test");

        Assert.Matches($"^JRN-{DateTime.UtcNow.Year}-[0-9]{{8}}$", posted.EntryNumber!);
        Assert.Matches($"^JRN-{DateTime.UtcNow.Year}-[0-9]{{8}}$", reversal.EntryNumber!);
        Assert.NotEqual(posted.EntryNumber, reversal.EntryNumber);
        Assert.Equal(posted.Id, reversal.ReversesJournalEntryId);
        Assert.All(reversal.Lines, line =>
        {
            var original = posted.Lines.Single(x => x.Sequence == line.Sequence);
            Assert.Equal(original.FunctionalDebit, line.FunctionalCredit);
            Assert.Equal(original.FunctionalCredit, line.FunctionalDebit);
        });

        var originalAfterReversal = await service.GetJournalAsync(LifecycleTenantId, posted.Id);
        Assert.Equal(JournalEntryStatuses.Reversed, originalAfterReversal.Status);
        Assert.True(await context.CommercialFinanceAudits.CountAsync(x => x.BusinessUnitId == LifecycleTenantId
            && x.AggregateType == "JournalEntry") >= 4);
        Assert.True(await context.FinanceOutboxMessages.CountAsync(x => x.BusinessUnitId == LifecycleTenantId
            && x.AggregateType == "JournalEntry") >= 4);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task DatabaseControls_RejectOverlapImbalanceAndPostedLineMutation()
    {
        await using var context = _database.ContextFor(null);
        await SeedTenantAsync(context, GuardTenantId, GuardCurrencyId);
        var service = new GeneralLedgerService(context);
        var period = await service.CreatePeriodAsync(GuardTenantId, "pg-period-guard", CurrentPeriod(), "period-maker@test");
        await Assert.ThrowsAsync<GeneralLedgerConflictException>(() => service.CreatePeriodAsync(
            GuardTenantId, "pg-period-overlap", CurrentPeriod() with { Name = "Overlapping period", PeriodNumber = 99 },
            "period-maker@test"));
        var debit = await service.CreateAccountAsync(GuardTenantId, "pg-account-debit",
            Account("1100", "Clearing", LedgerAccountCategories.Asset, LedgerNormalBalances.Debit), "account-maker@test");
        var credit = await service.CreateAccountAsync(GuardTenantId, "pg-account-credit",
            Account("4100", "Other revenue", LedgerAccountCategories.Revenue, LedgerNormalBalances.Credit), "account-maker@test");
        var draft = await service.CreateManualJournalAsync(GuardTenantId, "pg-journal-guard",
            Journal(period.Id, GuardCurrencyId, debit.Id, credit.Id), "journal-maker@test");
        var posted = await service.PostJournalAsync(GuardTenantId, draft.Id,
            new JournalActionRequest(draft.Version), "journal-checker@test");

        await using var connection = await _database.OpenConnectionAsync();
        await using (var mutate = connection.CreateCommand())
        {
            mutate.CommandText = "UPDATE public.\"JournalEntryLines\" SET \"FunctionalDebit\" = 1 WHERE \"JournalEntryId\" = @id";
            mutate.Parameters.AddWithValue("id", posted.Id);
            Assert.Equal(PostgresErrorCodes.ObjectNotInPrerequisiteState,
                (await Assert.ThrowsAsync<PostgresException>(() => mutate.ExecuteNonQueryAsync())).SqlState);
        }

        var invalid = new JournalEntry
        {
            BusinessUnitId = GuardTenantId, AccountingPeriodId = period.Id, FunctionalCurrencyId = GuardCurrencyId,
            AccountingDate = DateTime.UtcNow.Date, Description = "Unbalanced direct SQL test", SourceType = "Manual",
            TotalDebit = 100m, TotalCredit = 100m, IdempotencyKey = "pg-journal-unbalanced-direct",
            RequestHash = new string('b', 64), CreatedBy = "hostile-maker@test", CreatedOn = DateTime.UtcNow,
            Lines =
            [
                Line(GuardTenantId, 1, debit.Id, GuardCurrencyId, 100m, 0m),
                Line(GuardTenantId, 2, credit.Id, GuardCurrencyId, 0m, 90m)
            ]
        };
        context.JournalEntries.Add(invalid);
        await context.SaveChangesAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var forcePost = connection.CreateCommand();
        forcePost.Transaction = transaction;
        forcePost.CommandText = """
            UPDATE public."JournalEntries"
            SET "Status" = 'Posted', "PostedBy" = 'hostile-checker@test', "PostedOn" = now(), "Version" = "Version" + 1
            WHERE "Id" = @id
            """;
        forcePost.Parameters.AddWithValue("id", invalid.Id);
        await forcePost.ExecuteNonQueryAsync();
        var deferredFailure = await Assert.ThrowsAsync<PostgresException>(() => transaction.CommitAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, deferredFailure.SqlState);

        var sourceDraft = await service.CreateManualJournalAsync(GuardTenantId, "pg-draft-reversal-source",
            Journal(period.Id, GuardCurrencyId, debit.Id, credit.Id), "journal-maker@test");
        context.JournalEntries.Add(new JournalEntry
        {
            BusinessUnitId = GuardTenantId, AccountingPeriodId = period.Id,
            FunctionalCurrencyId = GuardCurrencyId, AccountingDate = DateTime.UtcNow.Date,
            Description = "Hostile reversal of an unposted draft", SourceType = "JournalReversal",
            SourceVersion = sourceDraft.Version, TotalDebit = 100m, TotalCredit = 100m,
            ReversesJournalEntryId = sourceDraft.Id, IdempotencyKey = "pg-hostile-draft-reversal",
            RequestHash = new string('f', 64), CreatedBy = "system:journal-reversal", CreatedOn = DateTime.UtcNow,
            Lines =
            [
                Line(GuardTenantId, 1, debit.Id, GuardCurrencyId, 0m, 100m),
                Line(GuardTenantId, 2, credit.Id, GuardCurrencyId, 100m, 0m)
            ]
        });
        var draftReversalFailure = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation,
            Assert.IsType<PostgresException>(draftReversalFailure.InnerException).SqlState);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task TenantRole_RequiresSignedActorAndCannotReadAnotherTenant()
    {
        await using (var context = _database.ContextFor(null))
        {
            await SeedTenantAsync(context, RlsTenantId, RlsCurrencyId);
            await SeedTenantAsync(context, OtherTenantId, OtherCurrencyId);
            await new GeneralLedgerService(context).CreateAccountAsync(OtherTenantId, "other-account",
                Account("1999", "Other tenant cash", LedgerAccountCategories.Asset, LedgerNormalBalances.Debit), "other@test");
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO public."FinanceProviderSecrets" ("Name", "Secret", "UpdatedOn")
                VALUES ({"AuditActor"}, {ActorSecret}, now())
                ON CONFLICT ("Name") DO UPDATE SET "Secret" = EXCLUDED."Secret", "UpdatedOn" = EXCLUDED."UpdatedOn"
                """);
        }

        await using var connection = await _database.OpenConnectionAsync();
        var envelope = ActorEnvelope(RlsTenantId, "ledger-maker@test");
        await using (var forged = await connection.BeginTransactionAsync())
        {
            await using var command = connection.CreateCommand();
            command.Transaction = forged;
            command.CommandText = $"""
                SET LOCAL ROLE nexora_tenant_app;
                SET LOCAL nexora.business_unit_id = '{RlsTenantId}';
                SET LOCAL nexora.actor_id = 'ledger-maker@test';
                SET LOCAL nexora.actor_signature = 'forged';
                INSERT INTO public."LedgerAccounts"
                    ("BusinessUnitId","Code","Name","Category","NormalBalance","IsControlAccount",
                     "AllowsManualPosting","IsActive","IdempotencyKey","RequestHash","Version","CreatedBy","CreatedOn")
                VALUES ({RlsTenantId},'1000','Forged cash','Asset','Debit',false,true,true,
                    'forged-account','{new string('c', 64)}',1,'ledger-maker@test',now());
                """;
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,
                (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState);
            await forged.RollbackAsync();
        }

        await using (var signed = await connection.BeginTransactionAsync())
        {
            await using var command = connection.CreateCommand();
            command.Transaction = signed;
            command.CommandText = $"""
                SET LOCAL ROLE nexora_tenant_app;
                SET LOCAL nexora.business_unit_id = '{RlsTenantId}';
                SET LOCAL nexora.actor_id = 'ledger-maker@test';
                SET LOCAL nexora.actor_signature = '{Signature(RlsTenantId, "ledger-maker@test")}';
                SET LOCAL nexora.gl_issued_at = '{envelope.IssuedAt}';
                SET LOCAL nexora.gl_expires_at = '{envelope.ExpiresAt}';
                SET LOCAL nexora.gl_nonce = '{envelope.Nonce}';
                SET LOCAL nexora.gl_signature = '{envelope.Signature}';
                INSERT INTO public."LedgerAccounts"
                    ("BusinessUnitId","Code","Name","Category","NormalBalance","IsControlAccount",
                     "AllowsManualPosting","IsActive","IdempotencyKey","RequestHash","Version","CreatedBy","CreatedOn")
                VALUES ({RlsTenantId},'1000','Tenant cash','Asset','Debit',false,true,true,
                    'signed-account','{new string('d', 64)}',1,'ledger-maker@test',now());
                SELECT count(*) FROM public."LedgerAccounts";
                """;
            Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
            await signed.CommitAsync();
        }

        await using (var replay = await connection.BeginTransactionAsync())
        {
            await using var command = connection.CreateCommand();
            command.Transaction = replay;
            command.CommandText = $"""
                SET LOCAL ROLE nexora_tenant_app;
                SET LOCAL nexora.business_unit_id = '{RlsTenantId}';
                SET LOCAL nexora.actor_id = 'ledger-maker@test';
                SET LOCAL nexora.gl_issued_at = '{envelope.IssuedAt}';
                SET LOCAL nexora.gl_expires_at = '{envelope.ExpiresAt}';
                SET LOCAL nexora.gl_nonce = '{envelope.Nonce}';
                SET LOCAL nexora.gl_signature = '{envelope.Signature}';
                INSERT INTO public."LedgerAccounts"
                    ("BusinessUnitId","Code","Name","Category","NormalBalance","IsControlAccount",
                     "AllowsManualPosting","IsActive","IdempotencyKey","RequestHash","Version","CreatedBy","CreatedOn")
                VALUES ({RlsTenantId},'1001','Replay cash','Asset','Debit',false,true,true,
                    'replay-account','{new string('e', 64)}',1,'ledger-maker@test',now());
                """;
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,
                (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState);
            await replay.RollbackAsync();
        }

        await using (var counterTamper = await connection.BeginTransactionAsync())
        {
            await using var command = connection.CreateCommand();
            command.Transaction = counterTamper;
            command.CommandText = $"""
                SET LOCAL ROLE nexora_tenant_app;
                SET LOCAL nexora.business_unit_id = '{RlsTenantId}';
                INSERT INTO public."LegalDocumentCounters" ("BusinessUnitId","DocumentType","FiscalYear","NextNumber")
                VALUES ({RlsTenantId},'Journal',2099,1);
                """;
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,
                (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState);
            await counterTamper.RollbackAsync();
        }

        await using var verify = connection.CreateCommand();
        verify.CommandText = "SELECT count(*) FROM pg_class WHERE relname IN ('LedgerAccounts','AccountingPeriods','JournalEntries','JournalEntryLines') AND relrowsecurity AND relforcerowsecurity";
        Assert.Equal(4L, (long)(await verify.ExecuteScalarAsync())!);
        await using var parity = _database.ContextFor(null);
        Assert.Empty(await parity.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task PeriodConcurrencyAndCertifiedEvidence_AreDatabaseEnforced()
    {
        await using (var seed = _database.ContextFor(null))
            await SeedTenantAsync(seed, ConcurrencyTenantId, ConcurrencyCurrencyId);

        await using var firstContext = _database.ContextFor(null);
        await using var secondContext = _database.ContextFor(null);
        var first = new GeneralLedgerService(firstContext).CreatePeriodAsync(ConcurrencyTenantId,
            "pg-concurrent-period-a", CurrentPeriod(), "period-maker-a@test");
        var second = new GeneralLedgerService(secondContext).CreatePeriodAsync(ConcurrencyTenantId,
            "pg-concurrent-period-b", CurrentPeriod() with { PeriodNumber = 98, Name = "Concurrent overlap" },
            "period-maker-b@test");
        try { await Task.WhenAll(first, second); } catch { }
        Assert.Equal(1, new[] { first, second }.Count(task => task.IsCompletedSuccessfully));

        var created = first.IsCompletedSuccessfully ? first.Result : second.Result;
        await using var closeContext = _database.ContextFor(null);
        var service = new GeneralLedgerService(closeContext);
        var softClosed = await service.TransitionPeriodAsync(ConcurrencyTenantId, created.Id, "soft-close",
            new AccountingPeriodActionRequest(created.Version), "period-checker@test");
        var closed = await service.TransitionPeriodAsync(ConcurrencyTenantId, created.Id, "close",
            new AccountingPeriodActionRequest(softClosed.Version,
                "Certified PostgreSQL period close evidence", "PG-CLOSE-PACK-001"), "controller@test");
        Assert.Equal(64, closed.CloseTrialBalanceHash?.Length);
        Assert.Equal(closed.CloseTotalDebit, closed.CloseTotalCredit);

        var previousStart = created.StartsOn.AddMonths(-1);
        await Assert.ThrowsAsync<GeneralLedgerConflictException>(() => service.CreatePeriodAsync(
            ConcurrencyTenantId, "pg-backdated-after-close",
            new(previousStart.Year, 97, "Backdated period after certified close", previousStart,
                created.StartsOn.AddDays(-1)), "period-maker@test"));

        await using var connection = await _database.OpenConnectionAsync();
        await using var tamper = connection.CreateCommand();
        tamper.CommandText = """
            UPDATE public."AccountingPeriods"
            SET "CloseReason" = 'Tampered certified close evidence', "Version" = "Version" + 1
            WHERE "Id" = @id
            """;
        tamper.Parameters.AddWithValue("id", created.Id);
        Assert.Equal(PostgresErrorCodes.ObjectNotInPrerequisiteState,
            (await Assert.ThrowsAsync<PostgresException>(() => tamper.ExecuteNonQueryAsync())).SqlState);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task HardCloseAndBackdatedPeriodInsert_CannotBothCommit()
    {
        await using (var seed = _database.ContextFor(null))
        {
            await SeedTenantAsync(seed, HorizonRaceTenantId, HorizonRaceCurrencyId);
            var setup = new GeneralLedgerService(seed);
            var period = await setup.CreatePeriodAsync(HorizonRaceTenantId, "pg-horizon-race-current",
                CurrentPeriod(), "period-maker@test");
            await setup.TransitionPeriodAsync(HorizonRaceTenantId, period.Id, "soft-close",
                new AccountingPeriodActionRequest(period.Version), "period-checker@test");
        }

        await using var closeContext = _database.ContextFor(null);
        await using var insertContext = _database.ContextFor(null);
        var softClosed = await closeContext.AccountingPeriods.IgnoreQueryFilters()
            .SingleAsync(x => x.BusinessUnitId == HorizonRaceTenantId);
        var previousStart = softClosed.StartsOn.AddMonths(-1);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task RunAfterGate(Func<Task> action) { await gate.Task; await action(); }
        var close = RunAfterGate(async () => await new GeneralLedgerService(closeContext).TransitionPeriodAsync(
            HorizonRaceTenantId, softClosed.Id, "close", new AccountingPeriodActionRequest(softClosed.Version,
                "Concurrent certified close horizon test", "PG-HORIZON-RACE-001"), "controller@test"));
        var insert = RunAfterGate(async () => await new GeneralLedgerService(insertContext).CreatePeriodAsync(
            HorizonRaceTenantId, "pg-horizon-race-backdated",
            new(previousStart.Year, 97, "Concurrent backdated period", previousStart,
                softClosed.StartsOn.AddDays(-1)), "period-maker-2@test"));
        gate.SetResult();
        try { await Task.WhenAll(close, insert); } catch { }

        Assert.False(close.IsCompletedSuccessfully && insert.IsCompletedSuccessfully);
        await using var verify = _database.ContextFor(null);
        var persisted = await verify.AccountingPeriods.IgnoreQueryFilters()
            .Where(x => x.BusinessUnitId == HorizonRaceTenantId).ToListAsync();
        Assert.False(persisted.Any(x => x.Status == AccountingPeriodStatuses.Closed) &&
            persisted.Any(x => x.StartsOn < softClosed.StartsOn && x.Status != AccountingPeriodStatuses.Closed));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ReceivablesPostingProfile_ConfiguresOnceAndRejectsMutationOrInvalidDirectProfile()
    {
        await using var context = _database.ContextFor(null);
        await SeedTenantAsync(context, ProfileTenantId, ProfileCurrencyId);
        var service = new GeneralLedgerService(context);
        var receivables = await service.CreateAccountAsync(ProfileTenantId, "pg-profile-ar",
            new("1200", "Trade receivables", LedgerAccountCategories.Asset,
                LedgerNormalBalances.Debit, null, true, false), "account-maker@test");
        var unapplied = await service.CreateAccountAsync(ProfileTenantId, "pg-profile-unapplied",
            new("2200", "Unapplied cash", LedgerAccountCategories.Liability,
                LedgerNormalBalances.Credit, null, false, false), "account-maker@test");
        var invalidOffset = await service.CreateAccountAsync(ProfileTenantId, "pg-profile-invalid",
            Account("4200", "Invalid revenue offset", LedgerAccountCategories.Revenue,
                LedgerNormalBalances.Credit), "account-maker@test");
        var book = await service.GetBookAsync(ProfileTenantId);

        var configured = await service.ConfigureReceivablesPostingAsync(ProfileTenantId,
            new(book.Version, receivables.Id, unapplied.Id), "controller@test");

        Assert.Equal(receivables.Id, configured.ReceivablesControlAccountId);
        Assert.Equal(unapplied.Id, configured.UnappliedCashAccountId);
        var secondConfiguration = await Assert.ThrowsAnyAsync<Exception>(() =>
            service.ConfigureReceivablesPostingAsync(ProfileTenantId,
                new(configured.Version, receivables.Id, unapplied.Id), "controller@test"));
        Assert.Equal(PostgresErrorCodes.ObjectNotInPrerequisiteState,
            Assert.IsType<PostgresException>(secondConfiguration.GetBaseException()).SqlState);

        await using var connection = await _database.OpenConnectionAsync();
        await using var mutate = connection.CreateCommand();
        mutate.CommandText = """
            UPDATE "LedgerBooks"
            SET "ReceivablesControlAccountId" = @invalid, "Version" = "Version" + 1
            WHERE "BusinessUnitId" = @tenant
            """;
        mutate.Parameters.AddWithValue("invalid", invalidOffset.Id);
        mutate.Parameters.AddWithValue("tenant", ProfileTenantId);
        Assert.Equal(PostgresErrorCodes.ObjectNotInPrerequisiteState,
            (await Assert.ThrowsAsync<PostgresException>(() => mutate.ExecuteNonQueryAsync())).SqlState);
    }

    private static async Task SeedTenantAsync(ErpRfqAutomationContext context, long tenantId, long currencyId)
    {
        if (!await context.BusinessUnits.IgnoreQueryFilters().AnyAsync(x => x.Id == tenantId))
            Seed.EnsureBusinessUnit(context, tenantId);
        if (!await context.Currencies.IgnoreQueryFilters().AnyAsync(x => x.Id == currencyId))
            context.Currencies.Add(new Currency
            {
                Id = currencyId, BusinessUnitId = tenantId, Code = $"T{tenantId % 1000:000}",
                CurrencyName = "Ledger test currency", Symbol = "L", ExchangeRate = 1m,
                IsBaseCurrency = true, IsActive = true, CreatedBy = "test", CreatedOn = DateTime.UtcNow
            });
        await context.SaveChangesAsync();
        if (!await context.LedgerBooks.IgnoreQueryFilters().AnyAsync(x => x.BusinessUnitId == tenantId))
            await new GeneralLedgerService(context).CreateBookAsync(tenantId, $"ledger-book-{tenantId}",
                new("Primary accounting book", currencyId, "UTC", 1), "controller@test");
    }

    private static CreateAccountingPeriodRequest CurrentPeriod()
        => new(DateTime.UtcNow.Year, DateTime.UtcNow.Month, "Current governed period",
            new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1),
            new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(1).AddDays(-1));

    private static CreateLedgerAccountRequest Account(string code, string name, string category, string normal)
        => new(code, name, category, normal, null, false, true);

    private static CreateJournalEntryRequest Journal(long periodId, long currencyId, long debitId, long creditId)
        => new(periodId, currencyId, DateTime.UtcNow.Date, "Governed PostgreSQL journal",
        [
            new(debitId, "Debit", currencyId, 1m, 100m, 0m),
            new(creditId, "Credit", currencyId, 1m, 0m, 100m)
        ]);

    private static JournalEntryLine Line(long tenantId, int sequence, long accountId, long currencyId, decimal debit, decimal credit)
        => new()
        {
            BusinessUnitId = tenantId, Sequence = sequence, LedgerAccountId = accountId,
            Description = $"Direct line {sequence}", TransactionCurrencyId = currencyId, ExchangeRate = 1m,
            TransactionDebit = debit, TransactionCredit = credit, FunctionalDebit = debit, FunctionalCredit = credit
        };

    private static string Signature(long tenantId, string actor)
        => Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(ActorSecret),
            Encoding.UTF8.GetBytes($"{tenantId}\n{actor}"))).ToLowerInvariant();

    private sealed record SignedEnvelope(long IssuedAt, long ExpiresAt, string Nonce, string Signature);

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

    private const string ActorSecret = "ledger-test-audit-secret-32-bytes-minimum";
    private const long LifecycleTenantId = 97_101;
    private const long LifecycleCurrencyId = 97_102;
    private const long GuardTenantId = 97_201;
    private const long GuardCurrencyId = 97_202;
    private const long RlsTenantId = 97_301;
    private const long RlsCurrencyId = 97_302;
    private const long OtherTenantId = 97_401;
    private const long OtherCurrencyId = 97_402;
    private const long ConcurrencyTenantId = 97_501;
    private const long ConcurrencyCurrencyId = 97_502;
    private const long HorizonRaceTenantId = 97_601;
    private const long HorizonRaceCurrencyId = 97_602;
    private const long ProfileTenantId = 97_701;
    private const long ProfileCurrencyId = 97_702;
}
