using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ERP_RFQ_Automation.BankReconciliation.Parsing;
using ERP_RFQ_Automation.GeneralLedger;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ERP_RFQ_Automation.BankReconciliation.Services;

public sealed class BankReconciliationService(ErpRfqAutomationContext context) : IBankReconciliationService
{
    private const decimal AmountTolerance = 0.005m;
    private readonly ErpRfqAutomationContext _context = context;

    public Task<BankMatchingRuleDto> CreateMatchingRuleAsync(long businessUnitId, string idempotencyKey,
        CreateBankMatchingRuleRequest request, string actor, CancellationToken cancellationToken = default)
    {
        RequireTenant(businessUnitId); ValidateKey(idempotencyKey);
        var code = MatchingRuleCode(request.Code);
        var name = Token(request.Name, "matching rule name", 160);
        var evaluator = Token(request.EvaluatorType, "matching rule evaluator", 40);
        var referenceMode = Token(request.ReferenceMode, "matching reference mode", 30);
        var tolerance = Round(request.AmountTolerance);
        ValidateRuleDefinition(evaluator, request.Priority, tolerance, request.BookingDateToleranceDays,
            referenceMode, request.RequireUniquePair);
        var definition = new { request.BankAccountId, Code = code, Name = name, EvaluatorType = evaluator,
            request.Priority, AmountTolerance = tolerance, request.BookingDateToleranceDays,
            ReferenceMode = referenceMode, request.RequireUniquePair };
        var requestHash = Hash(definition);
        return InSerializableTransactionAsync(async ct =>
        {
            var replay = await _context.BankMatchingRules.SingleOrDefaultAsync(x =>
                x.BusinessUnitId == businessUnitId && x.IdempotencyKey == idempotencyKey.Trim(), ct);
            if (replay is not null) { EnsureReplay(replay.RequestHash, requestHash); return Map(replay); }
            if (request.BankAccountId.HasValue && !await _context.BankAccounts.AnyAsync(x =>
                    x.BusinessUnitId == businessUnitId && x.Id == request.BankAccountId, ct))
                throw new ArgumentException("The matching-rule bank account does not belong to this tenant.");
            var prior = await _context.BankMatchingRules.Where(x => x.BusinessUnitId == businessUnitId &&
                    x.Code == code && x.BankAccountId == request.BankAccountId)
                .OrderByDescending(x => x.RuleVersion).FirstOrDefaultAsync(ct);
            var ruleVersion = (prior?.RuleVersion ?? 0) + 1;
            var rule = new BankMatchingRule
            {
                BusinessUnitId = businessUnitId, BankAccountId = request.BankAccountId, Code = code,
                RuleVersion = ruleVersion, SupersedesRuleId = prior?.Id,
                Name = name, EvaluatorType = evaluator, Priority = request.Priority,
                AmountTolerance = tolerance, BookingDateToleranceDays = request.BookingDateToleranceDays,
                ReferenceMode = referenceMode, RequireUniquePair = request.RequireUniquePair,
                DefinitionHash = RuleDefinitionHash(request.BankAccountId, code, ruleVersion, name, evaluator,
                    request.Priority, tolerance, request.BookingDateToleranceDays, referenceMode,
                    request.RequireUniquePair), Status = BankMatchingRuleStatuses.Draft,
                IdempotencyKey = idempotencyKey.Trim(), RequestHash = requestHash,
                CreatedBy = Actor(actor), CreatedOn = DateTime.UtcNow
            };
            _context.BankMatchingRules.Add(rule); await _context.SaveChangesAsync(ct); return Map(rule);
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<BankMatchingRuleDto>> GetMatchingRulesAsync(long businessUnitId,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(businessUnitId);
        return (await _context.BankMatchingRules.AsNoTracking().Where(x => x.BusinessUnitId == businessUnitId)
            .OrderBy(x => x.Code).ThenByDescending(x => x.RuleVersion).ToListAsync(cancellationToken))
            .Select(Map).ToArray();
    }

    public Task<BankMatchingRuleDto> TransitionMatchingRuleAsync(long businessUnitId, long ruleId,
        string action, BankMatchingRuleActionRequest request, string actor,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(businessUnitId);
        var normalizedAction = Token(action, "matching rule action", 20).ToLowerInvariant();
        var trustedActor = Actor(actor); var reason = Reason(request.Reason, "matching rule lifecycle reason");
        var evidence = Evidence(request.EvidenceReference);
        return InSerializableTransactionAsync(async ct =>
        {
            var rule = await LockMatchingRuleAsync(businessUnitId, ruleId, ct);
            if (IsMatchingRuleTransitionReplay(rule, normalizedAction, trustedActor, reason, evidence))
                return Map(rule);
            Expected(rule.RecordVersion, request.ExpectedVersion, "matching rule");
            var now = DateTime.UtcNow;
            if (normalizedAction == "approve" && rule.Status == BankMatchingRuleStatuses.Draft)
            {
                if (SameActor(trustedActor, rule.CreatedBy))
                    throw new BankReconciliationConflictException("The rule creator cannot approve the same rule.");
                rule.Status = BankMatchingRuleStatuses.Approved; rule.ApprovedBy = trustedActor; rule.ApprovedOn = now;
            }
            else if (normalizedAction == "activate" && rule.Status == BankMatchingRuleStatuses.Approved)
            {
                if (SameActor(trustedActor, rule.CreatedBy))
                    throw new BankReconciliationConflictException("The rule creator cannot activate the same rule.");
                var active = await _context.BankMatchingRules.SingleOrDefaultAsync(x =>
                    x.BusinessUnitId == businessUnitId && x.Code == rule.Code &&
                    x.BankAccountId == rule.BankAccountId && x.Status == BankMatchingRuleStatuses.Active, ct);
                if (active is not null)
                {
                    active.Status = BankMatchingRuleStatuses.Retired; active.RetiredBy = trustedActor;
                    active.RetiredOn = now; active.LifecycleReason = reason;
                    active.EvidenceReference = evidence; active.RecordVersion++;
                    await _context.SaveChangesAsync(ct);
                }
                rule.Status = BankMatchingRuleStatuses.Active; rule.ActivatedBy = trustedActor; rule.ActivatedOn = now;
            }
            else if (normalizedAction == "retire" && rule.Status == BankMatchingRuleStatuses.Active)
            {
                rule.Status = BankMatchingRuleStatuses.Retired; rule.RetiredBy = trustedActor; rule.RetiredOn = now;
            }
            else throw new BankReconciliationConflictException("The requested matching-rule transition is not allowed.");
            rule.LifecycleReason = reason; rule.EvidenceReference = evidence; rule.RecordVersion++;
            await _context.SaveChangesAsync(ct); return Map(rule);
        }, cancellationToken);
    }

    private static bool IsMatchingRuleTransitionReplay(BankMatchingRule rule, string action, string actor,
        string reason, string evidence)
    {
        var lifecycleMatches = string.Equals(rule.LifecycleReason, reason, StringComparison.Ordinal) &&
            string.Equals(rule.EvidenceReference, evidence, StringComparison.Ordinal);
        return lifecycleMatches && action switch
        {
            "approve" => rule.Status == BankMatchingRuleStatuses.Approved && SameActor(actor, rule.ApprovedBy),
            "activate" => rule.Status == BankMatchingRuleStatuses.Active && SameActor(actor, rule.ActivatedBy),
            "retire" => rule.Status == BankMatchingRuleStatuses.Retired && SameActor(actor, rule.RetiredBy),
            _ => false
        };
    }

    public Task<BankAccountDto> CreateBankAccountAsync(long businessUnitId, string idempotencyKey,
        CreateBankAccountRequest request, string actor, CancellationToken cancellationToken = default)
    {
        RequireTenant(businessUnitId);
        ValidateKey(idempotencyKey);
        var normalized = new
        {
            Name = Token(request.Name, "bank account name", 160),
            InstitutionName = Token(request.InstitutionName, "institution name", 160),
            MaskedAccountNumber = Token(request.MaskedAccountNumber, "masked account number", 64),
            AccountFingerprint = HashText(NormalizeAccountIdentifier(
                Token(request.AccountIdentifier, "account identifier", 128))),
            request.CurrencyId,
            request.LedgerAccountId,
            OpeningDate = request.OpeningDate.Date
        };
        var requestHash = Hash(normalized);
        return InSerializableTransactionAsync(async ct =>
        {
            var replay = await _context.BankAccounts.SingleOrDefaultAsync(x =>
                x.BusinessUnitId == businessUnitId && x.IdempotencyKey == idempotencyKey, ct);
            if (replay is not null)
            {
                EnsureReplay(replay.RequestHash, requestHash);
                return Map(replay);
            }

            if (!await _context.Currencies.AnyAsync(x => x.Id == normalized.CurrencyId &&
                    x.BusinessUnitId == businessUnitId && x.IsActive == true, ct))
                throw new ArgumentException("The bank account currency is not active for this tenant.");
            var book = await _context.LedgerBooks.AsNoTracking().SingleOrDefaultAsync(x =>
                x.BusinessUnitId == businessUnitId, ct)
                ?? throw new ArgumentException("A governed accounting book is required before creating a bank account.");
            if (book.FunctionalCurrencyId != normalized.CurrencyId)
                throw new ArgumentException("Bank reconciliation currently requires the accounting book functional currency.");
            var ledgerAccount = await _context.LedgerAccounts.SingleOrDefaultAsync(x =>
                x.BusinessUnitId == businessUnitId && x.Id == normalized.LedgerAccountId, ct)
                ?? throw new ArgumentException("The linked ledger account does not belong to this tenant.");
            if (!ledgerAccount.IsActive || ledgerAccount.Category != LedgerAccountCategories.Asset)
                throw new ArgumentException("A bank account requires an active asset ledger account.");
            if (ledgerAccount.CurrencyId.HasValue && ledgerAccount.CurrencyId != normalized.CurrencyId)
                throw new ArgumentException("The bank and ledger account currencies must agree.");

            var entity = new BankAccount
            {
                BusinessUnitId = businessUnitId,
                Name = normalized.Name,
                InstitutionName = normalized.InstitutionName,
                MaskedAccountNumber = normalized.MaskedAccountNumber,
                AccountFingerprint = normalized.AccountFingerprint,
                CurrencyId = normalized.CurrencyId,
                LedgerAccountId = normalized.LedgerAccountId,
                Status = BankAccountStatuses.Active,
                OpeningDate = normalized.OpeningDate,
                IdempotencyKey = idempotencyKey.Trim(),
                RequestHash = requestHash,
                CreatedBy = Actor(actor),
                CreatedOn = DateTime.UtcNow
            };
            _context.BankAccounts.Add(entity);
            await _context.SaveChangesAsync(ct);
            return Map(entity);
        }, cancellationToken);
    }

    public async Task<BankAccountDto> GetBankAccountAsync(long businessUnitId, long bankAccountId,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(businessUnitId);
        return Map(await _context.BankAccounts.AsNoTracking().SingleOrDefaultAsync(x =>
            x.BusinessUnitId == businessUnitId && x.Id == bankAccountId, cancellationToken)
            ?? throw new KeyNotFoundException("Bank account not found."));
    }

    public async Task<IReadOnlyList<BankAccountDto>> GetBankAccountsAsync(long businessUnitId, bool includeClosed,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(businessUnitId);
        var accounts = await _context.BankAccounts.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && (includeClosed || x.Status != BankAccountStatuses.Closed))
            .OrderBy(x => x.Name).ThenBy(x => x.Id).ToListAsync(cancellationToken);
        return accounts.Select(Map).ToArray();
    }

    public Task<BankAccountDto> TransitionBankAccountAsync(long businessUnitId, long bankAccountId, string action,
        BankAccountActionRequest request, string actor, CancellationToken cancellationToken = default)
    {
        RequireTenant(businessUnitId);
        var normalizedAction = Token(action, "bank account action", 20).ToLowerInvariant();
        var reason = Reason(request.Reason, "bank account status reason");
        return InSerializableTransactionAsync(async ct =>
        {
            var account = await LockBankAccountAsync(businessUnitId, bankAccountId, ct);
            var target = normalizedAction switch
            {
                "activate" when account.Status == BankAccountStatuses.Suspended => BankAccountStatuses.Active,
                "suspend" when account.Status == BankAccountStatuses.Active => BankAccountStatuses.Suspended,
                "close" when account.Status is BankAccountStatuses.Active or BankAccountStatuses.Suspended => BankAccountStatuses.Closed,
                "activate" when account.Status == BankAccountStatuses.Active => account.Status,
                "suspend" when account.Status == BankAccountStatuses.Suspended => account.Status,
                "close" when account.Status == BankAccountStatuses.Closed => account.Status,
                _ => throw new BankReconciliationConflictException("The requested bank account transition is not allowed.")
            };
            if (target == account.Status) return Map(account);
            Expected(account.Version, request.ExpectedVersion, "bank account");
            if (target == BankAccountStatuses.Closed && await _context.ReconciliationRuns.AnyAsync(x =>
                    x.BusinessUnitId == businessUnitId && x.BankAccountId == account.Id &&
                    x.Status != ReconciliationStatuses.Approved, ct))
                throw new BankReconciliationConflictException("A bank account with an unfinished reconciliation cannot be closed.");
            account.Status = target;
            account.StatusChangedBy = Actor(actor);
            account.StatusChangedOn = DateTime.UtcNow;
            account.StatusReason = reason;
            account.Version++;
            await _context.SaveChangesAsync(ct);
            return Map(account);
        }, cancellationToken);
    }

    public Task<BankStatementDto> ImportStatementAsync(long businessUnitId, string idempotencyKey,
        ImportBankStatementRequest request, string actor, CancellationToken cancellationToken = default)
        => ImportCoreAsync(businessUnitId, idempotencyKey, request, null, null, actor, cancellationToken);

    public Task<BankStatementDto> ImportStatementAsync(long businessUnitId, string idempotencyKey,
        long bankAccountId, string sourceType, string originalFileName, string rawObjectReference,
        string sourceHash, string parserVersion, byte[] rawPayload, ParsedBankStatement statement, string actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(rawPayload);
        if (rawPayload.Length == 0 || rawPayload.Length > 10 * 1024 * 1024)
            throw new ArgumentException("Raw statement evidence must contain data and cannot exceed 10 MiB.");
        var calculatedSourceHash = Convert.ToHexString(SHA256.HashData(rawPayload)).ToLowerInvariant();
        if (!FixedTimeEqual(sourceHash, calculatedSourceHash))
            throw new ArgumentException("Raw statement evidence does not match its SHA-256 digest.");
        var request = new ImportBankStatementRequest(bankAccountId, sourceType, originalFileName,
            rawObjectReference, sourceHash, parserVersion, statement.StatementReference,
            statement.PeriodStart.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            statement.PeriodEnd.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            statement.OpeningBalance, statement.ClosingBalance,
            statement.Lines.Select(x => new ImportBankStatementLineRequest(x.Ordinal,
                x.BookingDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                x.ValueDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), x.SignedAmount,
                x.OriginalAmountText, x.ExternalTransactionId, x.BankReference, x.TransactionCode,
                x.Counterparty, x.RemittanceText)).ToArray());
        return ImportCoreAsync(businessUnitId, idempotencyKey, request, statement, rawPayload, actor, cancellationToken);
    }

    private Task<BankStatementDto> ImportCoreAsync(long businessUnitId, string idempotencyKey,
        ImportBankStatementRequest request, ParsedBankStatement? canonical, byte[]? rawPayload, string actor,
        CancellationToken cancellationToken)
    {
        RequireTenant(businessUnitId);
        ValidateKey(idempotencyKey);
        var sourceType = Token(request.SourceType, "statement source type", 30);
        var fileName = Token(request.OriginalFileName, "original file name", 255);
        var objectReference = Token(request.RawObjectReference, "raw object reference", 500);
        var sourceHash = HexHash(request.SourceHash, "source hash");
        var parserVersion = Token(request.ParserVersion, "parser version", 50);
        var statementReference = Token(request.StatementReference, "statement reference", 200);
        if (request.PeriodEnd.Date < request.PeriodStart.Date)
            throw new ArgumentException("Statement period end cannot precede its start.");
        if (request.Lines.Count == 0 || request.Lines.Count > 100_000)
            throw new ArgumentException("A statement requires between one and 100,000 lines.");
        if (request.Lines.Select(x => x.SourceOrdinal).Distinct().Count() != request.Lines.Count ||
            request.Lines.Any(x => x.SourceOrdinal <= 0 || Round(x.SignedAmount) == 0m))
            throw new ArgumentException("Statement line ordinals must be unique and amounts must be non-zero.");

        var normalizedLines = request.Lines.OrderBy(x => x.SourceOrdinal).Select(line => new NormalizedImportLine(
            line.SourceOrdinal, line.BookingDate.Date, line.ValueDate.Date, Round(line.SignedAmount),
            Token(line.OriginalAmountText, "original amount text", 80), Optional(line.ExternalTransactionId, 200),
            Optional(line.BankReference, 200), Optional(line.TransactionCode, 80), Optional(line.Counterparty, 255),
            Optional(line.RemittanceText, 1000))).ToArray();
        if (normalizedLines.Any(x => x.BookingDate < request.PeriodStart.Date || x.BookingDate > request.PeriodEnd.Date))
            throw new ArgumentException("Every booking date must fall within the statement period.");
        var calculatedClosing = Round(request.OpeningBalance + normalizedLines.Sum(x => x.SignedAmount));
        if (calculatedClosing != Round(request.ClosingBalance))
            throw new ArgumentException("The imported statement does not reconcile to its closing balance.");

        var requestHash = Hash(new { request.BankAccountId, sourceType, fileName, objectReference, sourceHash,
            parserVersion, statementReference, PeriodStart = request.PeriodStart.Date,
            PeriodEnd = request.PeriodEnd.Date, OpeningBalance = Round(request.OpeningBalance),
            ClosingBalance = Round(request.ClosingBalance), Lines = normalizedLines });
        return InSerializableTransactionAsync(async ct =>
        {
            var replay = await _context.BankStatementImports.Include(x => x.Statement).ThenInclude(x => x.Lines)
                .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.IdempotencyKey == idempotencyKey, ct);
            if (replay is not null)
            {
                EnsureReplay(replay.RequestHash, requestHash);
                return Map(replay.Statement);
            }

            var account = await LockBankAccountAsync(businessUnitId, request.BankAccountId, ct);
            if (account.Status != BankAccountStatuses.Active)
                throw new BankReconciliationConflictException("Statements can only be imported for an active bank account.");
            var currency = await _context.Currencies.AsNoTracking().SingleAsync(x =>
                x.BusinessUnitId == businessUnitId && x.Id == account.CurrencyId, ct);
            if (canonical is not null)
            {
                if (!string.Equals(currency.Code, canonical.Currency, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("The parsed statement currency does not match the bank account.");
                var parsedFingerprint = HashText(NormalizeAccountIdentifier(canonical.AccountIdentifier));
                if (!FixedTimeEqual(account.AccountFingerprint, parsedFingerprint))
                    throw new ArgumentException("The parsed statement account does not match the bank account fingerprint.");
            }

            var duplicateSource = await _context.BankStatementImports.AsNoTracking().AnyAsync(x =>
                x.BusinessUnitId == businessUnitId && x.BankAccountId == account.Id && x.SourceHash == sourceHash, ct);
            if (duplicateSource)
                throw new BankReconciliationConflictException("This immutable statement source was already imported.");

            var lines = normalizedLines.Select(line => new BankStatementLine
            {
                BusinessUnitId = businessUnitId,
                BankAccountId = account.Id,
                SourceOrdinal = line.SourceOrdinal,
                BookingDate = line.BookingDate,
                ValueDate = line.ValueDate,
                SignedAmount = line.SignedAmount,
                Direction = line.SignedAmount > 0m ? "Credit" : "Debit",
                OriginalAmountText = line.OriginalAmountText,
                ExternalTransactionId = line.ExternalTransactionId,
                BankReference = line.BankReference,
                TransactionCode = line.TransactionCode,
                Counterparty = line.Counterparty,
                RemittanceText = line.RemittanceText,
                NormalizedReference = NormalizeReference(line.ExternalTransactionId, line.BankReference, line.RemittanceText),
                LineFingerprint = canonical?.Lines.Single(x => x.Ordinal == line.SourceOrdinal).Fingerprint
                    ?? BuildLineFingerprint(account.AccountFingerprint, currency.Code, line)
            }).ToArray();
            if (lines.Select(x => x.LineFingerprint).Distinct(StringComparer.Ordinal).Count() != lines.Length)
                throw new ArgumentException("The statement contains duplicate transaction fingerprints.");
            var fingerprints = lines.Select(x => x.LineFingerprint).ToArray();
            if (await _context.BankStatementLines.AsNoTracking().AnyAsync(x =>
                    x.BusinessUnitId == businessUnitId && x.BankAccountId == account.Id &&
                    fingerprints.Contains(x.LineFingerprint), ct))
                throw new BankReconciliationConflictException("One or more immutable statement lines were already imported.");

            var import = new BankStatementImport
            {
                BusinessUnitId = businessUnitId,
                BankAccountId = account.Id,
                SourceType = sourceType,
                OriginalFileName = fileName,
                RawObjectReference = objectReference,
                RawPayload = rawPayload,
                SourceHash = sourceHash,
                ParserVersion = parserVersion,
                Status = BankImportStatuses.Validated,
                IdempotencyKey = idempotencyKey.Trim(),
                RequestHash = requestHash,
                ImportedBy = Actor(actor),
                ImportedOn = DateTime.UtcNow,
                Statement = new BankStatement
                {
                    BusinessUnitId = businessUnitId,
                    BankAccountId = account.Id,
                    CurrencyId = account.CurrencyId,
                    StatementReference = statementReference,
                    PeriodStart = request.PeriodStart.Date,
                    PeriodEnd = request.PeriodEnd.Date,
                    OpeningBalance = Round(request.OpeningBalance),
                    ClosingBalance = Round(request.ClosingBalance),
                    CalculatedClosingBalance = calculatedClosing,
                    ContentHash = Hash(new { statementReference, PeriodStart = request.PeriodStart.Date,
                        PeriodEnd = request.PeriodEnd.Date,
                        Opening = Round(request.OpeningBalance), Closing = Round(request.ClosingBalance),
                        Lines = lines.Select(x => new { x.SourceOrdinal, x.LineFingerprint }).ToArray() }),
                    Lines = lines
                }
            };
            _context.BankStatementImports.Add(import);
            await _context.SaveChangesAsync(ct);
            return Map(import.Statement);
        }, cancellationToken);
    }

    public async Task<BankStatementDto> GetStatementAsync(long businessUnitId, long statementId,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(businessUnitId);
        var statement = await _context.BankStatements.AsNoTracking().Include(x => x.Lines)
            .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == statementId, cancellationToken)
            ?? throw new KeyNotFoundException("Bank statement not found.");
        return Map(statement);
    }

    public async Task<BankStatementSourceDto> GetStatementSourceAsync(long businessUnitId, long statementId,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(businessUnitId);
        var source = await (from statement in _context.BankStatements.AsNoTracking()
                            join import in _context.BankStatementImports.AsNoTracking()
                                on new { statement.BusinessUnitId, Id = statement.BankStatementImportId }
                                equals new { import.BusinessUnitId, import.Id }
                            where statement.BusinessUnitId == businessUnitId && statement.Id == statementId
                            select new { import.OriginalFileName, import.SourceType, import.SourceHash, import.RawPayload })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException("Bank statement source not found.");
        if (source.RawPayload is null || source.RawPayload.Length == 0)
            throw new BankReconciliationConflictException("The retained source evidence is unavailable for this legacy import.");
        return new BankStatementSourceDto(source.OriginalFileName, source.SourceType, source.SourceHash,
            source.RawPayload.ToArray());
    }

    public Task<ReconciliationRunDto> CreateRunAsync(long businessUnitId, string idempotencyKey,
        CreateReconciliationRunRequest request, string actor, CancellationToken cancellationToken = default)
    {
        RequireTenant(businessUnitId);
        ValidateKey(idempotencyKey);
        var through = request.ReconciliationThrough.Date;
        var requestHash = Hash(new { request.BankStatementId, ReconciliationThrough = through });
        return InSerializableTransactionAsync(async ct =>
        {
            var replay = await LoadRunByIdempotencyAsync(businessUnitId, idempotencyKey, ct);
            if (replay is not null)
            {
                EnsureReplay(replay.RequestHash, requestHash);
                return Map(replay);
            }
            var statement = await _context.BankStatements.AsNoTracking().SingleOrDefaultAsync(x =>
                x.BusinessUnitId == businessUnitId && x.Id == request.BankStatementId, ct)
                ?? throw new KeyNotFoundException("Bank statement not found.");
            if (through < statement.PeriodEnd.Date)
                throw new ArgumentException("Reconciliation-through date cannot precede the statement period end.");
            var account = await LockBankAccountAsync(businessUnitId, statement.BankAccountId, ct);
            if (account.Status == BankAccountStatuses.Closed)
                throw new BankReconciliationConflictException("A reconciliation cannot be created for a closed bank account.");
            if (await _context.ReconciliationRuns.AnyAsync(x => x.BusinessUnitId == businessUnitId &&
                    x.BankStatementId == statement.Id, ct))
                throw new BankReconciliationConflictException("This statement already has a reconciliation run.");
            var rules = await EffectiveMatchingRulesAsync(businessUnitId, account.Id, ct);
            if (rules.Count == 0)
                rules = [await CreateSystemDefaultRuleAsync(businessUnitId, ct)];
            var ruleSetHash = HashText(string.Join('|', rules.Select(x => x.DefinitionHash)));
            var bookBalance = await BookBalanceAsync(businessUnitId, account.LedgerAccountId, through, ct);
            var run = new ReconciliationRun
            {
                BusinessUnitId = businessUnitId,
                BankAccountId = account.Id,
                BankStatementId = statement.Id,
                ReconciliationThrough = through,
                Status = ReconciliationStatuses.Draft,
                BankClosingBalance = statement.ClosingBalance,
                BookClosingBalance = bookBalance,
                MatchedAmount = 0m,
                UnexplainedDifference = Round(statement.ClosingBalance - bookBalance),
                IdempotencyKey = idempotencyKey.Trim(),
                RequestHash = requestHash,
                PreparedBy = Actor(actor),
                PreparedOn = DateTime.UtcNow,
                RuleSetHash = ruleSetHash,
                RuleSetSnapshotOn = DateTime.UtcNow,
                Rules = rules.Select((rule, index) => new ReconciliationRunRule
                {
                    BusinessUnitId = businessUnitId, BankMatchingRuleId = rule.Id,
                    EvaluationOrder = index + 1, DefinitionHash = rule.DefinitionHash
                }).ToList()
            };
            _context.ReconciliationRuns.Add(run);
            await _context.SaveChangesAsync(ct);
            return Map(run);
        }, cancellationToken);
    }

    public async Task<ReconciliationRunDto> GetRunAsync(long businessUnitId, long runId,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(businessUnitId);
        return Map(await LoadRunAsync(businessUnitId, runId, false, cancellationToken));
    }

    public Task<IReadOnlyList<ReconciliationMatchDto>> GenerateExactCandidatesAsync(long businessUnitId,
        long runId, string idempotencyKey, string actor, CancellationToken cancellationToken = default)
    {
        RequireTenant(businessUnitId);
        ValidateKey(idempotencyKey);
        return InSerializableTransactionAsync<IReadOnlyList<ReconciliationMatchDto>>(async ct =>
        {
            var run = await LockRunAsync(businessUnitId, runId, ct);
            EnsureEditable(run);
            var snapshots = run.Rules.OrderBy(x => x.EvaluationOrder).ToArray();
            if (snapshots.Length == 0)
                throw new BankReconciliationConflictException("The reconciliation has no immutable matching-rule snapshot.");
            var keyPrefix = DerivedKeyPrefix(idempotencyKey);
            var replay = await _context.ReconciliationMatches.Include(x => x.Allocations).Where(x =>
                    x.BusinessUnitId == businessUnitId && x.ReconciliationRunId == run.Id &&
                    x.IdempotencyKey.StartsWith(keyPrefix))
                .OrderBy(x => x.Id).ToListAsync(ct);
            if (replay.Count > 0) return replay.Select(Map).ToArray();
            var account = await LockBankAccountAsync(businessUnitId, run.BankAccountId, ct);
            var usedBankIds = await ActiveAllocatedBankLineIdsAsync(businessUnitId, run.Id, ct);
            var usedJournalIds = await ActiveAllocatedJournalLineIdsAsync(businessUnitId, account.Id, ct);
            var bankLines = await _context.BankStatementLines.AsNoTracking()
                .Where(x => x.BusinessUnitId == businessUnitId && x.BankStatementId == run.BankStatementId &&
                    !usedBankIds.Contains(x.Id)).OrderBy(x => x.Id).ToListAsync(ct);
            var journalLines = await EligibleJournalLinesAsync(businessUnitId, account, run.ReconciliationThrough,
                usedJournalIds, ct);

            var created = new List<ReconciliationMatch>();
            foreach (var snapshot in snapshots)
            {
                var rule = await _context.BankMatchingRules.AsNoTracking().SingleAsync(x =>
                    x.BusinessUnitId == businessUnitId && x.Id == snapshot.BankMatchingRuleId, ct);
                var potential = (from bankLine in bankLines
                    where !usedBankIds.Contains(bankLine.Id)
                    from journalLine in journalLines
                    where !usedJournalIds.Contains(journalLine.Id)
                    let cashImpact = CashImpact(journalLine)
                    where Math.Sign(bankLine.SignedAmount) == Math.Sign(cashImpact)
                          && Math.Abs(Math.Abs(bankLine.SignedAmount) - Math.Abs(cashImpact)) <= rule.AmountTolerance
                          && Math.Abs((bankLine.BookingDate.Date - journalLine.JournalEntry.AccountingDate.Date).Days) <= rule.BookingDateToleranceDays
                          && (rule.ReferenceMode == BankMatchingReferenceModes.Ignore ||
                              (!string.IsNullOrWhiteSpace(bankLine.NormalizedReference) &&
                               string.Equals(bankLine.NormalizedReference, journalLine.SourceReference,
                                   StringComparison.OrdinalIgnoreCase)))
                    select new { BankLine = bankLine, JournalLine = journalLine }).ToArray();
                var uniqueBank = potential.GroupBy(x => x.BankLine.Id).Where(x => x.Count() == 1)
                    .Select(x => x.Key).ToHashSet();
                var uniqueJournal = potential.GroupBy(x => x.JournalLine.Id).Where(x => x.Count() == 1)
                    .Select(x => x.Key).ToHashSet();
                foreach (var pair in potential.Where(x => uniqueBank.Contains(x.BankLine.Id) &&
                             uniqueJournal.Contains(x.JournalLine.Id)).OrderBy(x => x.BankLine.LineFingerprint,
                             StringComparer.Ordinal).ThenBy(x => x.JournalLine.Id))
                {
                    var bankLine = pair.BankLine;
                    var journalLine = pair.JournalLine;
                    var candidateKey = DerivedKey(idempotencyKey, bankLine.Id, journalLine.Id);
                    var existing = await _context.ReconciliationMatches.Include(x => x.Allocations)
                        .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.IdempotencyKey == candidateKey, ct);
                    if (existing is not null)
                    {
                        created.Add(existing); usedBankIds.Add(bankLine.Id); usedJournalIds.Add(journalLine.Id);
                        continue;
                    }
                    var amount = Math.Abs(bankLine.SignedAmount);
                    var match = NewMatch(businessUnitId, run.Id, candidateKey,
                        Hash(new { RunId = run.Id, RuleDefinitionHash = rule.DefinitionHash,
                            BankLineId = bankLine.Id, JournalLineId = journalLine.Id, amount }),
                        "DeterministicExact", 1m, rule.Code, rule.RuleVersion, rule.Id, rule.DefinitionHash,
                        actor, null, null, [new ReconciliationAllocation
                        {
                            BusinessUnitId = businessUnitId, BankStatementLineId = bankLine.Id,
                            JournalEntryLineId = journalLine.Id, BankAmount = amount,
                            FunctionalAmount = Math.Abs(CashImpact(journalLine))
                        }]);
                    _context.ReconciliationMatches.Add(match); created.Add(match);
                    usedBankIds.Add(bankLine.Id); usedJournalIds.Add(journalLine.Id);
                }
            }
            await _context.SaveChangesAsync(ct);
            return created.OrderBy(x => x.Id).Select(Map).ToArray();
        }, cancellationToken);
    }

    public Task<ReconciliationMatchDto> CreateMatchAsync(long businessUnitId, string idempotencyKey,
        CreateReconciliationMatchRequest request, string actor, CancellationToken cancellationToken = default)
    {
        RequireTenant(businessUnitId);
        ValidateKey(idempotencyKey);
        if (request.Allocations.Count == 0)
            throw new ArgumentException("A reconciliation match requires at least one allocation.");
        var matchReason = Reason(request.Reason, "manual match reason");
        var evidenceReference = Evidence(request.EvidenceReference);
        var allocations = request.Allocations.OrderBy(x => x.BankStatementLineId).ThenBy(x => x.JournalEntryLineId)
            .Select(x => new ReconciliationAllocationRequest(x.BankStatementLineId, x.JournalEntryLineId,
                Round(x.BankAmount), Round(x.FunctionalAmount))).ToArray();
        if (allocations.Any(x => x.BankAmount <= 0m || x.FunctionalAmount <= 0m) ||
            allocations.Select(x => (x.BankStatementLineId, x.JournalEntryLineId)).Distinct().Count() != allocations.Length)
            throw new ArgumentException("Allocations require unique line pairs and positive amounts.");
        var requestHash = Hash(new { request.ReconciliationRunId, matchReason, evidenceReference, allocations });
        return InSerializableTransactionAsync(async ct =>
        {
            var replay = await _context.ReconciliationMatches.Include(x => x.Allocations).SingleOrDefaultAsync(x =>
                x.BusinessUnitId == businessUnitId && x.IdempotencyKey == idempotencyKey, ct);
            if (replay is not null)
            {
                EnsureReplay(replay.RequestHash, requestHash);
                return Map(replay);
            }
            var run = await LockRunAsync(businessUnitId, request.ReconciliationRunId, ct);
            EnsureEditable(run);
            var account = await LockBankAccountAsync(businessUnitId, run.BankAccountId, ct);
            await ValidateAllocationGraphAsync(businessUnitId, run, account, allocations, false, null, ct);
            var match = NewMatch(businessUnitId, run.Id, idempotencyKey.Trim(), requestHash, "Manual",
                1m, "MANUAL_REVIEWED_V1", 1, null, null, actor, matchReason, evidenceReference, allocations.Select(x =>
                    new ReconciliationAllocation
                    {
                        BusinessUnitId = businessUnitId,
                        BankStatementLineId = x.BankStatementLineId,
                        JournalEntryLineId = x.JournalEntryLineId,
                        BankAmount = x.BankAmount,
                        FunctionalAmount = x.FunctionalAmount
                    }).ToArray());
            _context.ReconciliationMatches.Add(match);
            await _context.SaveChangesAsync(ct);
            return Map(match);
        }, cancellationToken);
    }

    public Task<ReconciliationMatchDto> ConfirmMatchAsync(long businessUnitId, long matchId,
        MatchActionRequest request, string actor, CancellationToken cancellationToken = default)
        => TransitionMatchAsync(businessUnitId, matchId, request, actor, true, cancellationToken);

    public Task<ReconciliationMatchDto> VoidMatchAsync(long businessUnitId, long matchId,
        MatchActionRequest request, string actor, CancellationToken cancellationToken = default)
        => TransitionMatchAsync(businessUnitId, matchId, request, actor, false, cancellationToken);

    private Task<ReconciliationMatchDto> TransitionMatchAsync(long businessUnitId, long matchId,
        MatchActionRequest request, string actor, bool confirm, CancellationToken cancellationToken)
    {
        RequireTenant(businessUnitId);
        var normalizedActor = Actor(actor);
        var reason = confirm ? null : Reason(request.Reason, "match void reason");
        return InSerializableTransactionAsync(async ct =>
        {
            var match = await LockMatchAsync(businessUnitId, matchId, ct);
            var run = await LockRunAsync(businessUnitId, match.ReconciliationRunId, ct);
            EnsureEditable(run);
            if (confirm && match.Status == BankMatchStatuses.Confirmed) return Map(match);
            if (!confirm && match.Status == BankMatchStatuses.Voided) return Map(match);
            Expected(match.Version, request.ExpectedVersion, "reconciliation match");
            if (confirm && match.Status != BankMatchStatuses.Proposed)
                throw new BankReconciliationConflictException("Only a proposed match can be confirmed.");
            if (!confirm && match.Status is not (BankMatchStatuses.Proposed or BankMatchStatuses.Confirmed))
                throw new BankReconciliationConflictException("Only a proposed or confirmed match can be voided.");
            if (confirm)
            {
                var account = await LockBankAccountAsync(businessUnitId, run.BankAccountId, ct);
                var requests = match.Allocations.Select(x => new ReconciliationAllocationRequest(
                    x.BankStatementLineId, x.JournalEntryLineId, x.BankAmount, x.FunctionalAmount)).ToArray();
                await LockEvidenceLinesAsync(businessUnitId, requests, ct);
                await ValidateAllocationGraphAsync(businessUnitId, run, account, requests, true, match.Id, ct);
                match.Status = BankMatchStatuses.Confirmed;
                match.ConfirmedBy = normalizedActor;
                match.ConfirmedOn = DateTime.UtcNow;
            }
            else
            {
                match.Status = BankMatchStatuses.Voided;
                match.VoidedBy = normalizedActor;
                match.VoidedOn = DateTime.UtcNow;
                match.VoidReason = reason;
            }
            match.Version++;
            await RefreshRunSnapshotAsync(run, ct);
            await _context.SaveChangesAsync(ct);
            return Map(match);
        }, cancellationToken);
    }

    public Task<ReconciliationRunDto> SubmitRunAsync(long businessUnitId, long runId,
        ReconciliationActionRequest request, string actor, CancellationToken cancellationToken = default)
        => TransitionRunAsync(businessUnitId, runId, request, actor, RunAction.Submit, cancellationToken);

    public Task<ReconciliationRunDto> ApproveRunAsync(long businessUnitId, long runId,
        ReconciliationActionRequest request, string actor, CancellationToken cancellationToken = default)
        => TransitionRunAsync(businessUnitId, runId, request, actor, RunAction.Approve, cancellationToken);

    public Task<ReconciliationRunDto> ReopenRunAsync(long businessUnitId, long runId,
        ReconciliationActionRequest request, string actor, CancellationToken cancellationToken = default)
        => TransitionRunAsync(businessUnitId, runId, request, actor, RunAction.Reopen, cancellationToken);

    private Task<ReconciliationRunDto> TransitionRunAsync(long businessUnitId, long runId,
        ReconciliationActionRequest request, string actor, RunAction action, CancellationToken cancellationToken)
    {
        RequireTenant(businessUnitId);
        var normalizedActor = Actor(actor);
        var reason = action == RunAction.Submit ? Optional(request.Reason, 500) : Reason(request.Reason,
            action == RunAction.Approve ? "approval reason" : "reopen reason");
        var evidence = action == RunAction.Submit ? Optional(request.EvidenceReference, 500)
            : Evidence(request.EvidenceReference);
        return InSerializableTransactionAsync(async ct =>
        {
            var run = await LockRunAsync(businessUnitId, runId, ct);
            if (action == RunAction.Submit && run.Status == ReconciliationStatuses.InReview) return Map(run);
            if (action == RunAction.Approve && run.Status == ReconciliationStatuses.Approved) return Map(run);
            if (action == RunAction.Reopen && run.Status == ReconciliationStatuses.Reopened) return Map(run);
            Expected(run.Version, request.ExpectedVersion, "reconciliation run");
            if (action == RunAction.Submit)
            {
                EnsureEditable(run);
                await CertifyRunStateAsync(run, ct);
                run.Status = ReconciliationStatuses.InReview;
                run.SubmittedBy = normalizedActor;
                run.SubmittedOn = DateTime.UtcNow;
            }
            else if (action == RunAction.Approve)
            {
                if (run.Status != ReconciliationStatuses.InReview)
                    throw new BankReconciliationConflictException("Only an in-review reconciliation can be approved.");
                if (SameActor(normalizedActor, run.PreparedBy) || SameActor(normalizedActor, run.SubmittedBy))
                    throw new BankReconciliationConflictException("The preparer or submitter cannot approve this reconciliation.");
                var certificate = await CertifyRunStateAsync(run, ct);
                run.Status = ReconciliationStatuses.Approved;
                run.ApprovedBy = normalizedActor;
                run.ApprovedOn = DateTime.UtcNow;
                run.ApprovalReason = reason;
                run.EvidenceReference = evidence;
                run.CertificateHash = certificate.Hash;
                run.CertificateLineCount = certificate.LineCount;
                run.CertificateJournalCount = certificate.JournalCount;
            }
            else
            {
                if (run.Status != ReconciliationStatuses.Approved)
                    throw new BankReconciliationConflictException("Only an approved reconciliation can be reopened.");
                if (SameActor(normalizedActor, run.ApprovedBy))
                    throw new BankReconciliationConflictException("The approver cannot independently reopen their own approval.");
                run.Status = ReconciliationStatuses.Reopened;
                run.ReopenedBy = normalizedActor;
                run.ReopenedOn = DateTime.UtcNow;
                run.ReopenReason = reason;
                run.ReopenEvidenceReference = evidence;
            }
            run.Version++;
            await _context.SaveChangesAsync(ct);
            return Map(run);
        }, cancellationToken);
    }

    private async Task<Certificate> CertifyRunStateAsync(ReconciliationRun run, CancellationToken ct)
    {
        await RefreshRunSnapshotAsync(run, ct);
        if (run.UnexplainedDifference != 0m)
            throw new BankReconciliationConflictException("The bank and book closing balances must agree before certification.");
        if (await _context.ReconciliationMatches.AnyAsync(x => x.BusinessUnitId == run.BusinessUnitId &&
                x.ReconciliationRunId == run.Id && x.Status == BankMatchStatuses.Proposed, ct))
            throw new BankReconciliationConflictException("All proposed matches must be confirmed or voided before certification.");

        var statementLines = await _context.BankStatementLines.AsNoTracking().Where(x =>
            x.BusinessUnitId == run.BusinessUnitId && x.BankStatementId == run.BankStatementId)
            .OrderBy(x => x.Id).ToListAsync(ct);
        var allocations = await ConfirmedAllocationsQuery(run.BusinessUnitId, run.Id).AsNoTracking()
            .OrderBy(x => x.BankStatementLineId).ThenBy(x => x.JournalEntryLineId).ThenBy(x => x.Id)
            .ToListAsync(ct);
        await LockEvidenceLinesAsync(run.BusinessUnitId, allocations.Select(x =>
            new ReconciliationAllocationRequest(x.BankStatementLineId, x.JournalEntryLineId,
                x.BankAmount, x.FunctionalAmount)).ToArray(), ct);
        foreach (var line in statementLines)
        {
            var allocated = Round(allocations.Where(x => x.BankStatementLineId == line.Id).Sum(x => x.BankAmount));
            if (allocated != Math.Abs(line.SignedAmount))
                throw new BankReconciliationConflictException($"Statement line {line.SourceOrdinal} is not fully reconciled.");
        }
        var journalIds = allocations.Select(x => x.JournalEntryLineId).Distinct().OrderBy(x => x).ToArray();
        var journals = await _context.JournalEntryLines.AsNoTracking().Include(x => x.JournalEntry)
            .Where(x => x.BusinessUnitId == run.BusinessUnitId && journalIds.Contains(x.Id))
            .OrderBy(x => x.Id).ToListAsync(ct);
        if (journals.Count != journalIds.Length || journals.Any(x =>
                x.JournalEntry.Status != JournalEntryStatuses.Posted ||
                x.JournalEntry.AccountingDate.Date > run.ReconciliationThrough.Date))
            throw new BankReconciliationConflictException("A reconciled journal is no longer posted and eligible.");
        foreach (var journal in journals)
        {
            var allocated = Round(allocations.Where(x => x.JournalEntryLineId == journal.Id)
                .Sum(x => x.FunctionalAmount));
            if (allocated > Math.Abs(CashImpact(journal)) + AmountTolerance)
                throw new BankReconciliationConflictException("A journal line is over-allocated.");
        }
        var fingerprints = statementLines.ToDictionary(x => x.Id, x => x.LineFingerprint);
        var canonical = string.Join('|', allocations
            .OrderBy(x => fingerprints[x.BankStatementLineId], StringComparer.Ordinal)
            .ThenBy(x => x.JournalEntryLineId)
            .Select(x => string.Create(CultureInfo.InvariantCulture,
            $"{fingerprints[x.BankStatementLineId]}:{x.JournalEntryLineId}:{x.BankAmount:F2}:{x.FunctionalAmount:F2}")));
        return new Certificate(HashText(string.Create(CultureInfo.InvariantCulture,
                $"{canonical}:{run.BankClosingBalance:F2}")), statementLines.Count,
            journals.Select(x => x.JournalEntryId).Distinct().Count());
    }

    private async Task ValidateAllocationGraphAsync(long businessUnitId, ReconciliationRun run,
        BankAccount account, IReadOnlyList<ReconciliationAllocationRequest> requests, bool confirmation,
        long? currentMatchId, CancellationToken ct)
    {
        var bankIds = requests.Select(x => x.BankStatementLineId).Distinct().ToArray();
        var journalIds = requests.Select(x => x.JournalEntryLineId).Distinct().ToArray();
        var bankLines = await _context.BankStatementLines.AsNoTracking().Where(x => x.BusinessUnitId == businessUnitId &&
            x.BankStatementId == run.BankStatementId && bankIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        if (bankLines.Count != bankIds.Length)
            throw new ArgumentException("Every allocated bank line must belong to the run's immutable statement.");
        var journalLines = await _context.JournalEntryLines.AsNoTracking().Include(x => x.JournalEntry)
            .Where(x => x.BusinessUnitId == businessUnitId && x.LedgerAccountId == account.LedgerAccountId &&
                journalIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        if (journalLines.Count != journalIds.Length)
            throw new ArgumentException("Every allocated journal line must belong to the configured bank ledger account.");
        foreach (var allocation in requests)
        {
            var bank = bankLines[allocation.BankStatementLineId];
            var journal = journalLines[allocation.JournalEntryLineId];
            var impact = CashImpact(journal);
            if (journal.JournalEntry.Status != JournalEntryStatuses.Posted ||
                journal.JournalEntry.AccountingDate.Date > run.ReconciliationThrough.Date)
                throw new BankReconciliationConflictException("Allocations require posted, unreversed journals within the run date.");
            if (Math.Sign(bank.SignedAmount) != Math.Sign(impact))
                throw new ArgumentException("Bank and cash-ledger allocation directions must agree.");
            if (allocation.BankAmount != allocation.FunctionalAmount)
                throw new ArgumentException("Functional-currency reconciliation requires equal bank and functional amounts.");
            if (allocation.BankAmount > Math.Abs(bank.SignedAmount) + AmountTolerance ||
                allocation.FunctionalAmount > Math.Abs(impact) + AmountTolerance)
                throw new ArgumentException("An allocation cannot exceed either source line amount.");
        }
        if (!confirmation) return;
        var confirmed = ConfirmedAllocationsQuery(businessUnitId, null);
        if (currentMatchId.HasValue)
            confirmed = confirmed.Where(x => x.ReconciliationMatchId != currentMatchId.Value);
        var existingBank = await confirmed.Where(x => bankIds.Contains(x.BankStatementLineId))
            .GroupBy(x => x.BankStatementLineId).Select(x => new { Id = x.Key, Amount = x.Sum(y => y.BankAmount) })
            .ToDictionaryAsync(x => x.Id, x => x.Amount, ct);
        var existingJournal = await confirmed.Where(x => journalIds.Contains(x.JournalEntryLineId))
            .GroupBy(x => x.JournalEntryLineId).Select(x => new { Id = x.Key, Amount = x.Sum(y => y.FunctionalAmount) })
            .ToDictionaryAsync(x => x.Id, x => x.Amount, ct);
        foreach (var bankId in bankIds)
        {
            var proposed = requests.Where(x => x.BankStatementLineId == bankId).Sum(x => x.BankAmount);
            if (Round(proposed + existingBank.GetValueOrDefault(bankId)) > Math.Abs(bankLines[bankId].SignedAmount))
                throw new BankReconciliationConflictException("Confirmation would over-allocate a bank statement line.");
        }
        foreach (var journalId in journalIds)
        {
            var proposed = requests.Where(x => x.JournalEntryLineId == journalId).Sum(x => x.FunctionalAmount);
            if (Round(proposed + existingJournal.GetValueOrDefault(journalId)) > Math.Abs(CashImpact(journalLines[journalId])))
                throw new BankReconciliationConflictException("Confirmation would over-allocate a journal line.");
        }
    }

    private async Task RefreshRunSnapshotAsync(ReconciliationRun run, CancellationToken ct)
    {
        var account = await _context.BankAccounts.AsNoTracking().SingleAsync(x =>
            x.BusinessUnitId == run.BusinessUnitId && x.Id == run.BankAccountId, ct);
        run.BookClosingBalance = await BookBalanceAsync(run.BusinessUnitId, account.LedgerAccountId,
            run.ReconciliationThrough, ct);
        run.UnexplainedDifference = Round(run.BankClosingBalance - run.BookClosingBalance);
        run.MatchedAmount = Round(await ConfirmedAllocationsQuery(run.BusinessUnitId, run.Id)
            .SumAsync(x => (decimal?)x.BankAmount, ct) ?? 0m);
    }

    private async Task<decimal> BookBalanceAsync(long businessUnitId, long ledgerAccountId, DateTime through,
        CancellationToken ct) => Round(await _context.JournalEntryLines.AsNoTracking().Where(x =>
            x.BusinessUnitId == businessUnitId && x.LedgerAccountId == ledgerAccountId &&
            x.JournalEntry.Status == JournalEntryStatuses.Posted && x.JournalEntry.AccountingDate.Date <= through.Date)
            .SumAsync(x => (decimal?)(x.FunctionalDebit - x.FunctionalCredit), ct) ?? 0m);

    private IQueryable<ReconciliationAllocation> ConfirmedAllocationsQuery(long businessUnitId, long? runId)
        => _context.ReconciliationAllocations.Where(x => x.BusinessUnitId == businessUnitId &&
            x.Match.Status == BankMatchStatuses.Confirmed &&
            (!runId.HasValue || x.Match.ReconciliationRunId == runId.Value));

    private async Task<HashSet<long>> ActiveAllocatedBankLineIdsAsync(long businessUnitId, long runId,
        CancellationToken ct) => (await _context.ReconciliationAllocations.AsNoTracking().Where(x =>
            x.BusinessUnitId == businessUnitId && x.Match.ReconciliationRunId == runId &&
            x.Match.Status != BankMatchStatuses.Voided).Select(x => x.BankStatementLineId).ToListAsync(ct)).ToHashSet();

    private async Task<HashSet<long>> ActiveAllocatedJournalLineIdsAsync(long businessUnitId, long bankAccountId,
        CancellationToken ct) => (await _context.ReconciliationAllocations.AsNoTracking().Where(x =>
            x.BusinessUnitId == businessUnitId && x.Match.Run.BankAccountId == bankAccountId &&
            x.Match.Status != BankMatchStatuses.Voided).Select(x => x.JournalEntryLineId).ToListAsync(ct)).ToHashSet();

    private async Task<List<JournalEntryLine>> EligibleJournalLinesAsync(long businessUnitId, BankAccount account,
        DateTime through, HashSet<long> excluded, CancellationToken ct) => await _context.JournalEntryLines.AsNoTracking()
        .Include(x => x.JournalEntry).Where(x => x.BusinessUnitId == businessUnitId &&
            x.LedgerAccountId == account.LedgerAccountId && x.JournalEntry.Status == JournalEntryStatuses.Posted &&
            x.JournalEntry.AccountingDate.Date <= through.Date && !excluded.Contains(x.Id) &&
            x.FunctionalDebit != x.FunctionalCredit).OrderBy(x => x.Id).ToListAsync(ct);

    private async Task<BankAccount> LockBankAccountAsync(long businessUnitId, long id, CancellationToken ct)
    {
        IQueryable<BankAccount> query = _context.BankAccounts;
        if (_context.Database.IsNpgsql()) query = _context.BankAccounts.FromSqlInterpolated(
            $"SELECT * FROM \"BankAccounts\" WHERE \"BusinessUnitId\" = {businessUnitId} AND \"Id\" = {id} FOR UPDATE");
        return await query.SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == id, ct)
            ?? throw new KeyNotFoundException("Bank account not found.");
    }

    private async Task<ReconciliationRun> LockRunAsync(long businessUnitId, long id, CancellationToken ct)
    {
        IQueryable<ReconciliationRun> query = _context.ReconciliationRuns;
        if (_context.Database.IsNpgsql()) query = _context.ReconciliationRuns.FromSqlInterpolated(
            $"SELECT * FROM \"ReconciliationRuns\" WHERE \"BusinessUnitId\" = {businessUnitId} AND \"Id\" = {id} FOR UPDATE");
        var run = await query.SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == id, ct)
            ?? throw new KeyNotFoundException("Reconciliation run not found.");
        await _context.Entry(run).Collection(x => x.Matches).Query().Include(x => x.Allocations).LoadAsync(ct);
        await _context.Entry(run).Collection(x => x.Rules).LoadAsync(ct);
        return run;
    }

    private async Task<BankMatchingRule> LockMatchingRuleAsync(long businessUnitId, long id, CancellationToken ct)
    {
        IQueryable<BankMatchingRule> query = _context.BankMatchingRules;
        if (_context.Database.IsNpgsql()) query = _context.BankMatchingRules.FromSqlInterpolated(
            $"SELECT * FROM \"BankMatchingRules\" WHERE \"BusinessUnitId\" = {businessUnitId} AND \"Id\" = {id} FOR UPDATE");
        return await query.SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == id, ct)
            ?? throw new KeyNotFoundException("Bank matching rule not found.");
    }

    private async Task<List<BankMatchingRule>> EffectiveMatchingRulesAsync(long businessUnitId,
        long bankAccountId, CancellationToken ct)
    {
        var active = await _context.BankMatchingRules.Where(x => x.BusinessUnitId == businessUnitId &&
            x.Status == BankMatchingRuleStatuses.Active &&
            (x.BankAccountId == null || x.BankAccountId == bankAccountId)).ToListAsync(ct);
        return active.GroupBy(x => x.Code).Select(group => group
                .OrderByDescending(x => x.BankAccountId == bankAccountId)
                .ThenByDescending(x => x.RuleVersion).First())
            .OrderBy(x => x.Priority).ThenBy(x => x.Code).ThenByDescending(x => x.RuleVersion)
            .ThenBy(x => x.Id).ToList();
    }

    private async Task<BankMatchingRule> CreateSystemDefaultRuleAsync(long businessUnitId, CancellationToken ct)
    {
        var definition = new { BankAccountId = (long?)null, Code = "EXACT_AMOUNT_DIRECTION",
            Name = "System exact amount and direction", EvaluatorType = BankMatchingRuleTypes.ExactAmountDirection,
            Priority = 1000, AmountTolerance = 0m, BookingDateToleranceDays = 31,
            ReferenceMode = BankMatchingReferenceModes.Ignore, RequireUniquePair = true };
        var rule = new BankMatchingRule
        {
            BusinessUnitId = businessUnitId, Code = definition.Code, RuleVersion = 1, Name = definition.Name,
            EvaluatorType = definition.EvaluatorType, Priority = definition.Priority,
            AmountTolerance = definition.AmountTolerance, BookingDateToleranceDays = definition.BookingDateToleranceDays,
            ReferenceMode = definition.ReferenceMode, RequireUniquePair = true,
            DefinitionHash = RuleDefinitionHash(null, definition.Code, 1, definition.Name,
                definition.EvaluatorType, definition.Priority, definition.AmountTolerance,
                definition.BookingDateToleranceDays, definition.ReferenceMode, true),
            Status = BankMatchingRuleStatuses.Active,
            IdempotencyKey = "system:default-exact-rule:v1", RequestHash = Hash(definition),
            CreatedBy = "system:bank-rule-bootstrap", CreatedOn = DateTime.UtcNow,
            ApprovedBy = "system:bank-rule-bootstrap", ApprovedOn = DateTime.UtcNow,
            ActivatedBy = "system:bank-rule-bootstrap", ActivatedOn = DateTime.UtcNow
        };
        _context.BankMatchingRules.Add(rule); await _context.SaveChangesAsync(ct); return rule;
    }

    private async Task<ReconciliationMatch> LockMatchAsync(long businessUnitId, long id, CancellationToken ct)
    {
        IQueryable<ReconciliationMatch> query = _context.ReconciliationMatches;
        if (_context.Database.IsNpgsql()) query = _context.ReconciliationMatches.FromSqlInterpolated(
            $"SELECT * FROM \"ReconciliationMatches\" WHERE \"BusinessUnitId\" = {businessUnitId} AND \"Id\" = {id} FOR UPDATE");
        var match = await query.SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == id, ct)
            ?? throw new KeyNotFoundException("Reconciliation match not found.");
        await _context.Entry(match).Collection(x => x.Allocations).LoadAsync(ct);
        return match;
    }

    private async Task LockEvidenceLinesAsync(long businessUnitId,
        IReadOnlyList<ReconciliationAllocationRequest> allocations, CancellationToken ct)
    {
        if (!_context.Database.IsNpgsql()) return;
        foreach (var id in allocations.Select(x => x.BankStatementLineId).Distinct().OrderBy(x => x))
            await _context.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM \"BankStatementLines\" WHERE \"BusinessUnitId\" = {businessUnitId} AND \"Id\" = {id} FOR UPDATE", ct);
        foreach (var id in allocations.Select(x => x.JournalEntryLineId).Distinct().OrderBy(x => x))
            await _context.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM \"JournalEntryLines\" WHERE \"BusinessUnitId\" = {businessUnitId} AND \"Id\" = {id} FOR UPDATE", ct);
    }

    private async Task<ReconciliationRun?> LoadRunByIdempotencyAsync(long businessUnitId, string key,
        CancellationToken ct) => await _context.ReconciliationRuns.Include(x => x.Matches).ThenInclude(x => x.Allocations)
        .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.IdempotencyKey == key, ct);

    private async Task<ReconciliationRun> LoadRunAsync(long businessUnitId, long id, bool tracking,
        CancellationToken ct)
    {
        var query = _context.ReconciliationRuns.Include(x => x.Matches).ThenInclude(x => x.Allocations).AsQueryable();
        if (!tracking) query = query.AsNoTracking();
        return await query.SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == id, ct)
            ?? throw new KeyNotFoundException("Reconciliation run not found.");
    }

    private async Task<T> InSerializableTransactionAsync<T>(Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable,
                cancellationToken);
            try
            {
                var result = await action(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return result;
            }
            catch (Exception ex) when (attempt < 3 && IsRetryable(ex))
            {
                await transaction.RollbackAsync(cancellationToken);
                _context.ChangeTracker.Clear();
                await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt), cancellationToken);
            }
        }
    }

    private static bool IsRetryable(Exception exception)
        => exception is DbUpdateConcurrencyException
           || exception is DbUpdateException { InnerException: PostgresException p } && p.SqlState is
               PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.UniqueViolation
           || exception is PostgresException direct && direct.SqlState is
               PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.UniqueViolation;

    private static ReconciliationMatch NewMatch(long businessUnitId, long runId, string idempotencyKey,
        string requestHash, string matchType, decimal confidence, string ruleCode, int ruleVersion,
        long? bankMatchingRuleId, string? ruleDefinitionHash,
        string actor, string? matchReason, string? evidenceReference,
        IReadOnlyCollection<ReconciliationAllocation> allocations) => new()
        {
            BusinessUnitId = businessUnitId,
            ReconciliationRunId = runId,
            MatchType = matchType,
            Confidence = confidence,
            RuleCode = ruleCode,
            RuleVersion = ruleVersion,
            BankMatchingRuleId = bankMatchingRuleId,
            RuleDefinitionHash = ruleDefinitionHash,
            MatchReason = matchReason,
            EvidenceReference = evidenceReference,
            Status = BankMatchStatuses.Proposed,
            IdempotencyKey = idempotencyKey,
            RequestHash = requestHash,
            CreatedBy = Actor(actor),
            CreatedOn = DateTime.UtcNow,
            Allocations = allocations.ToList()
        };

    private static BankAccountDto Map(BankAccount x) => new(x.Id, x.Name, x.InstitutionName,
        x.MaskedAccountNumber, x.CurrencyId, x.LedgerAccountId, x.Status, x.OpeningDate, x.Version);
    private static BankMatchingRuleDto Map(BankMatchingRule x) => new(x.Id, x.Code, x.RuleVersion,
        x.BankAccountId, x.Name, x.EvaluatorType, x.Priority, x.AmountTolerance,
        x.BookingDateToleranceDays, x.ReferenceMode, x.RequireUniquePair, x.DefinitionHash,
        x.Status, x.RecordVersion, x.CreatedBy, x.CreatedOn, x.ApprovedBy, x.ActivatedBy, x.RetiredBy);
    private static BankStatementDto Map(BankStatement x) => new(x.Id, x.BankStatementImportId,
        x.BankAccountId, x.CurrencyId, x.StatementReference, x.PeriodStart, x.PeriodEnd, x.OpeningBalance,
        x.ClosingBalance, x.CalculatedClosingBalance, x.ContentHash, x.Lines.OrderBy(line => line.SourceOrdinal)
            .Select(line => new BankStatementLineDto(line.Id, line.SourceOrdinal, line.BookingDate, line.ValueDate,
                line.SignedAmount, line.Direction, line.ExternalTransactionId, line.BankReference,
                line.TransactionCode, line.Counterparty, line.RemittanceText, line.NormalizedReference,
                line.LineFingerprint)).ToArray());
    private static ReconciliationMatchDto Map(ReconciliationMatch x) => new(x.Id, x.MatchType, x.Confidence,
        x.RuleCode, x.RuleVersion, x.BankMatchingRuleId, x.Status, x.Version, x.CreatedBy, x.ConfirmedBy,
        x.Allocations.OrderBy(a => a.Id).Select(a => new ReconciliationAllocationDto(a.Id,
            a.BankStatementLineId, a.JournalEntryLineId, a.BankAmount, a.FunctionalAmount)).ToArray());
    private static ReconciliationRunDto Map(ReconciliationRun x) => new(x.Id, x.BankAccountId,
        x.BankStatementId, x.ReconciliationThrough, x.Status, x.BankClosingBalance, x.BookClosingBalance,
        x.MatchedAmount, x.UnexplainedDifference, x.Version, x.PreparedBy, x.SubmittedBy, x.ApprovedBy,
        x.CertificateHash, x.CertificateLineCount, x.CertificateJournalCount,
        x.Matches.OrderBy(m => m.Id).Select(Map).ToArray());

    private static decimal CashImpact(JournalEntryLine line) => Round(line.FunctionalDebit - line.FunctionalCredit);
    private static (int Sign, decimal Amount) SignedMagnitudeKey(decimal value)
        => (Math.Sign(value), Math.Abs(Round(value)));
    private static decimal Round(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);
    private static string RuleDefinitionHash(long? bankAccountId, string code, int ruleVersion, string name,
        string evaluator, int priority, decimal amountTolerance, int dateToleranceDays,
        string referenceMode, bool requireUniquePair)
        => HashText(string.Join('|', bankAccountId?.ToString(CultureInfo.InvariantCulture) ?? "*", code,
            ruleVersion.ToString(CultureInfo.InvariantCulture), name, evaluator,
            priority.ToString(CultureInfo.InvariantCulture), amountTolerance.ToString("0.00", CultureInfo.InvariantCulture),
            dateToleranceDays.ToString(CultureInfo.InvariantCulture), referenceMode,
            requireUniquePair ? "true" : "false"));
    private static string Actor(string actor) => Token(actor, "authenticated actor", 255);
    private static string MatchingRuleCode(string? value)
    {
        var code = Token(value, "matching rule code", 80).ToUpperInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(code, "^[A-Z][A-Z0-9_]{2,79}$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new ArgumentException("Matching rule code must use uppercase letters, digits, and underscores.");
        return code;
    }
    private static void ValidateRuleDefinition(string evaluator, int priority, decimal amountTolerance,
        int dateToleranceDays, string referenceMode, bool requireUniquePair)
    {
        if (evaluator != BankMatchingRuleTypes.ExactAmountDirection)
            throw new ArgumentException("Only the governed ExactAmountDirection evaluator is supported.");
        if (priority is < 1 or > 10000) throw new ArgumentException("Matching rule priority must be between 1 and 10000.");
        if (amountTolerance < 0m) throw new ArgumentException("Matching amount tolerance cannot be negative.");
        if (dateToleranceDays is < 0 or > 31) throw new ArgumentException("Booking-date tolerance must be between zero and 31 days.");
        if (referenceMode is not (BankMatchingReferenceModes.Ignore or BankMatchingReferenceModes.NormalizedExact))
            throw new ArgumentException("Matching reference mode must be Ignore or NormalizedExact.");
        if (!requireUniquePair) throw new ArgumentException("Governed exact matching requires a unique candidate pair.");
    }
    private static void RequireTenant(long businessUnitId)
    { if (businessUnitId <= 0) throw new ArgumentException("A valid tenant is required.", nameof(businessUnitId)); }
    private static void EnsureEditable(ReconciliationRun run)
    {
        if (run.Status is not (ReconciliationStatuses.Draft or ReconciliationStatuses.Reopened))
            throw new BankReconciliationConflictException("Only a draft or reopened reconciliation can be changed.");
    }
    private static void Expected(long actual, long expected, string aggregate)
    { if (actual != expected) throw new BankReconciliationConflictException($"The {aggregate} changed; reload it before continuing."); }
    private static void ValidateKey(string key)
    { if (string.IsNullOrWhiteSpace(key) || key.Trim().Length > 128) throw new ArgumentException("A valid Idempotency-Key is required."); }
    private static string Token(string? value, string field, int max)
    {
        value = value?.Trim() ?? string.Empty;
        if (value.Length == 0 || value.Length > max) throw new ArgumentException($"A valid {field} is required.");
        return value;
    }
    private static string? Optional(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        if (value.Length > max) throw new ArgumentException("The optional value is too long.");
        return value;
    }
    private static string Reason(string? value, string field)
    {
        var reason = Token(value, field, 500);
        if (reason.Length < 20) throw new ArgumentException($"{field} requires at least 20 characters.");
        return reason;
    }
    private static string Evidence(string? value)
    {
        var evidence = Token(value, "evidence reference", 500);
        if (evidence.Length < 8) throw new ArgumentException("An evidence reference of at least eight characters is required.");
        return evidence;
    }
    private static string HexHash(string? value, string field)
    {
        value = Token(value, field, 64).ToLowerInvariant();
        if (value.Length != 64 || !value.All(Uri.IsHexDigit))
            throw new ArgumentException($"The {field} must be a 64-character SHA-256 hexadecimal value.");
        return value;
    }
    private static string Hash(object value) => HashText(JsonSerializer.Serialize(value));
    private static string HashText(string value) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static bool FixedTimeEqual(string left, string right)
    {
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right)); }
        catch (FormatException) { return false; }
    }
    private static void EnsureReplay(string stored, string current)
    {
        if (!FixedTimeEqual(stored, current))
            throw new BankReconciliationConflictException("Idempotency key was already used for a different request.");
    }
    private static bool SameActor(string left, string? right)
        => string.Equals(left, right?.Trim(), StringComparison.OrdinalIgnoreCase);
    private static string DerivedKey(string root, long bankLineId, long journalLineId)
    {
        var suffix = HashText($"{bankLineId}:{journalLineId}")[..16];
        return $"{DerivedKeyPrefix(root)}{suffix}";
    }
    private static string DerivedKeyPrefix(string root)
    {
        var prefix = root.Trim();
        if (prefix.Length > 111) prefix = prefix[..111];
        return $"{prefix}:";
    }
    private static string NormalizeAccountIdentifier(string value)
        => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static string? NormalizeReference(params string?[] values)
    {
        var value = values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        if (value is null) return null;
        var normalized = string.Join(' ', value.Trim().ToUpperInvariant().Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 500 ? normalized : normalized[..500];
    }
    private static string BuildLineFingerprint(string accountFingerprint, string currency,
        NormalizedImportLine line) => Hash(new
        {
            accountFingerprint,
            BookingDate = line.BookingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ValueDate = line.ValueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Amount = line.SignedAmount.ToString("F2", CultureInfo.InvariantCulture),
            Currency = currency.ToUpperInvariant(),
            line.ExternalTransactionId,
            line.BankReference,
            line.TransactionCode,
            line.Counterparty,
            line.RemittanceText
        });

    private sealed record NormalizedImportLine(int SourceOrdinal, DateTime BookingDate, DateTime ValueDate,
        decimal SignedAmount, string OriginalAmountText, string? ExternalTransactionId, string? BankReference,
        string? TransactionCode, string? Counterparty, string? RemittanceText);
    private sealed record Certificate(string Hash, int LineCount, int JournalCount);
    private enum RunAction { Submit, Approve, Reopen }
}
