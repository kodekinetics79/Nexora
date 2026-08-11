using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ERP_RFQ_Automation.GeneralLedger;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.BankReconciliation.Services;

public interface IBankAdjustmentService
{
    Task<BankAdjustmentDto> CreateAsync(long businessUnitId, string idempotencyKey,
        CreateBankAdjustmentRequest request, string actor, CancellationToken ct = default);
    Task<BankAdjustmentDto> GetAsync(long businessUnitId, long adjustmentId, CancellationToken ct = default);
    Task<IReadOnlyList<BankAdjustmentDto>> GetAllAsync(long businessUnitId, string? status, CancellationToken ct = default);
    Task<BankAdjustmentDto> TransitionAsync(long businessUnitId, long adjustmentId, string action,
        BankAdjustmentActionRequest request, string actor, CancellationToken ct = default);
}

public sealed class BankAdjustmentService(ErpRfqAutomationContext context,
    IInternalSourceJournalPostingService journalWriter) : IBankAdjustmentService
{
    private readonly ErpRfqAutomationContext _context = context;
    private readonly IInternalSourceJournalPostingService _journalWriter = journalWriter;

    public async Task<BankAdjustmentDto> CreateAsync(long businessUnitId, string idempotencyKey,
        CreateBankAdjustmentRequest request, string actor, CancellationToken ct = default)
    {
        ValidateKey(idempotencyKey);
        if (request.Amount <= 0m || decimal.Round(request.Amount, 2) != request.Amount)
            throw new ArgumentException("A positive two-decimal adjustment amount is required.");
        if (request.Distributions is null || request.Distributions.Count == 0)
            throw new ArgumentException("At least one adjustment distribution is required.");
        var distributions = request.Distributions.Select((x, index) => new
        {
            Sequence = index + 1, x.LedgerAccountId, Amount = decimal.Round(x.Amount, 2),
            Description = Token(x.Description, "distribution description", 500)
        }).ToArray();
        if (distributions.Any(x => x.Amount <= 0m) || distributions.Sum(x => x.Amount) != request.Amount)
            throw new ArgumentException("Positive adjustment distributions must equal the adjustment amount.");
        var normalized = new { request.BankAccountId, request.BankStatementLineId, request.AccountingPeriodId,
            AccountingDate = request.AccountingDate.Date, AdjustmentType = Token(request.AdjustmentType, "adjustment type", 50),
            Description = Token(request.Description, "adjustment description", 500), request.Amount,
            EvidenceReference = Evidence(request.EvidenceReference), Distributions = distributions };
        var requestHash = Hash(normalized);
        return await InSerializableTransactionAsync(async token =>
        {
            var replay = await _context.BankAdjustments.Include(x => x.Distributions).SingleOrDefaultAsync(x =>
                x.BusinessUnitId == businessUnitId && x.IdempotencyKey == idempotencyKey.Trim(), token);
            if (replay is not null)
            {
                if (replay.RequestHash != requestHash) throw new BankReconciliationConflictException("Idempotency key was reused with different adjustment data.");
                return Map(replay);
            }
            var account = await _context.BankAccounts.SingleOrDefaultAsync(x =>
                x.BusinessUnitId == businessUnitId && x.Id == request.BankAccountId, token)
                ?? throw new KeyNotFoundException("Bank account not found.");
            if (account.Status != BankAccountStatuses.Active)
                throw new BankReconciliationConflictException("Bank adjustments require an active bank account.");
            var statementLine = await _context.BankStatementLines.SingleOrDefaultAsync(x =>
                x.BusinessUnitId == businessUnitId && x.Id == request.BankStatementLineId &&
                x.BankAccountId == account.Id, token) ?? throw new KeyNotFoundException("Bank statement line not found.");
            if (request.Amount > Math.Abs(statementLine.SignedAmount))
                throw new ArgumentException("Adjustment amount cannot exceed the statement line amount.");
            var ledgerIds = distributions.Select(x => x.LedgerAccountId).Distinct().ToArray();
            var ledgers = await _context.LedgerAccounts.Where(x => x.BusinessUnitId == businessUnitId &&
                ledgerIds.Contains(x.Id)).ToListAsync(token);
            if (ledgers.Count != ledgerIds.Length || ledgers.Any(x => !x.IsActive || x.IsControlAccount || x.Id == account.LedgerAccountId))
                throw new ArgumentException("Adjustment distributions require active non-control accounts distinct from cash.");
            // Constructed INSIDE the delegate: an entity built outside stays tracked as Added after
            // a failed attempt and would be inserted twice by the retry.
            var adjustment = new BankAdjustment
            {
                BusinessUnitId = businessUnitId, BankAccountId = account.Id, BankStatementLineId = statementLine.Id,
                AccountingPeriodId = request.AccountingPeriodId, AccountingDate = normalized.AccountingDate,
                AdjustmentType = normalized.AdjustmentType, Description = normalized.Description, Amount = request.Amount,
                EvidenceReference = normalized.EvidenceReference, IdempotencyKey = idempotencyKey.Trim(),
                RequestHash = requestHash, PreparedBy = Actor(actor), PreparedOn = DateTime.UtcNow,
                Distributions = distributions.Select(x => new BankAdjustmentDistribution
                {
                    BusinessUnitId = businessUnitId, Sequence = x.Sequence, LedgerAccountId = x.LedgerAccountId,
                    Amount = x.Amount, Description = x.Description
                }).ToList()
            };
            _context.BankAdjustments.Add(adjustment); await _context.SaveChangesAsync(token);
            return Map(adjustment);
        }, ct);
    }

    /// <summary>
    /// Owns the SERIALIZABLE transaction through the configured execution strategy.
    /// <c>EnableRetryOnFailure</c> (Program.cs) installs <c>NpgsqlRetryingExecutionStrategy</c>,
    /// which refuses a transaction opened outside its delegate — both adjustment write paths threw
    /// against PostgreSQL before this existed.
    /// </summary>
    private Task<T> InSerializableTransactionAsync<T>(Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        var strategy = _context.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(async () =>
        {
            _context.ChangeTracker.Clear();
            await using var transaction = await _context.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken);
            var result = await action(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        });
    }

    public async Task<BankAdjustmentDto> GetAsync(long businessUnitId, long adjustmentId, CancellationToken ct = default)
        => Map(await Query().AsNoTracking().SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == adjustmentId, ct)
            ?? throw new KeyNotFoundException("Bank adjustment not found."));

    public async Task<IReadOnlyList<BankAdjustmentDto>> GetAllAsync(long businessUnitId, string? status, CancellationToken ct = default)
    {
        var query = Query().AsNoTracking().Where(x => x.BusinessUnitId == businessUnitId);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status.Trim());
        return (await query.OrderByDescending(x => x.PreparedOn).ToListAsync(ct)).Select(Map).ToArray();
    }

    public async Task<BankAdjustmentDto> TransitionAsync(long businessUnitId, long adjustmentId, string action,
        BankAdjustmentActionRequest request, string actor, CancellationToken ct = default)
    {
        var targetAction = Token(action, "bank adjustment action", 20).ToLowerInvariant();
        var trustedActor = Actor(actor);
        return await InSerializableTransactionAsync(async _ =>
        {
        var adjustment = await LockAsync(businessUnitId, adjustmentId, ct);
        if (IsTransitionReplay(adjustment, targetAction, request, trustedActor))
        {
            return Map(adjustment);
        }
        if (adjustment.Version != request.ExpectedVersion)
            throw new BankReconciliationConflictException("The bank adjustment changed; reload it before continuing.");
        var now = DateTime.UtcNow;
        if (targetAction == "submit" && adjustment.Status == BankAdjustmentStatuses.Draft)
        {
            adjustment.Status = BankAdjustmentStatuses.InReview; adjustment.SubmittedBy = trustedActor; adjustment.SubmittedOn = now;
        }
        else if (targetAction == "cancel" && adjustment.Status == BankAdjustmentStatuses.Draft)
        {
            adjustment.Status = BankAdjustmentStatuses.Cancelled; adjustment.CancelledBy = trustedActor;
            adjustment.CancelledOn = now; adjustment.CancellationReason = Reason(request.Reason, "adjustment cancellation reason");
        }
        else if (targetAction == "reject" && adjustment.Status == BankAdjustmentStatuses.InReview)
        {
            if (Same(trustedActor, adjustment.PreparedBy) || Same(trustedActor, adjustment.SubmittedBy))
                throw new BankReconciliationConflictException("The adjustment preparer or submitter cannot reject it as independent reviewer.");
            adjustment.Status = BankAdjustmentStatuses.Rejected; adjustment.RejectedBy = trustedActor;
            adjustment.RejectedOn = now; adjustment.RejectionReason = Reason(request.Reason, "adjustment rejection reason");
        }
        else if (targetAction == "approve" && adjustment.Status == BankAdjustmentStatuses.InReview)
        {
            if (Same(trustedActor, adjustment.PreparedBy) || Same(trustedActor, adjustment.SubmittedBy))
                throw new BankReconciliationConflictException("The adjustment preparer or submitter cannot approve it.");
            var alreadyPosted = await _context.BankAdjustments.Where(x => x.BusinessUnitId == businessUnitId &&
                x.BankStatementLineId == adjustment.BankStatementLineId && x.Status == BankAdjustmentStatuses.Posted)
                .SumAsync(x => (decimal?)x.Amount, ct) ?? 0m;
            if (alreadyPosted + adjustment.Amount > Math.Abs(adjustment.BankStatementLine.SignedAmount))
                throw new BankReconciliationConflictException("Posted adjustments would over-allocate the statement line.");
            var posted = await _journalWriter.CreateAndPostBankAdjustmentAsync(adjustment, adjustment.BankAccount, trustedActor, ct);
            adjustment.Status = BankAdjustmentStatuses.Posted; adjustment.ApprovedBy = trustedActor;
            adjustment.ApprovedOn = now; adjustment.JournalEntryId = posted.JournalEntryId;
            adjustment.BankJournalEntryLineId = posted.BankJournalEntryLineId;
        }
        else if (targetAction == "reverse" && adjustment.Status == BankAdjustmentStatuses.Posted)
        {
            if (Same(trustedActor, adjustment.PreparedBy) || Same(trustedActor, adjustment.ApprovedBy))
                throw new BankReconciliationConflictException("Adjustment reversal requires an independent controller.");
            var reason = Reason(request.Reason, "adjustment reversal reason"); var evidence = Evidence(request.EvidenceReference);
            if (await _context.ReconciliationAllocations.AnyAsync(x => x.BusinessUnitId == businessUnitId &&
                    x.JournalEntryLineId == adjustment.BankJournalEntryLineId && x.Match.Status == BankMatchStatuses.Confirmed &&
                    (x.Match.Run.Status == ReconciliationStatuses.InReview || x.Match.Run.Status == ReconciliationStatuses.Approved), ct))
                throw new BankReconciliationConflictException("Reopen and void the governed reconciliation before reversing this adjustment.");
            var reversal = await _journalWriter.ReverseBankAdjustmentAsync(adjustment, trustedActor, reason, evidence, ct);
            adjustment.Status = BankAdjustmentStatuses.Reversed; adjustment.ReversedBy = trustedActor;
            adjustment.ReversedOn = now; adjustment.ReversalReason = reason; adjustment.ReversalEvidenceReference = evidence;
            adjustment.ReversalJournalEntryId = reversal.JournalEntryId; adjustment.ReversalBankJournalEntryLineId = reversal.BankJournalEntryLineId;
        }
        else throw new BankReconciliationConflictException("The requested bank adjustment transition is not allowed.");
        adjustment.Version++; await _context.SaveChangesAsync(ct); return Map(adjustment);
        }, ct);
    }

    private static bool IsTransitionReplay(BankAdjustment adjustment, string action,
        BankAdjustmentActionRequest request, string actor)
    {
        var reason = request.Reason?.Trim();
        var evidence = request.EvidenceReference?.Trim();
        return action switch
        {
            "submit" => adjustment.Status == BankAdjustmentStatuses.InReview && Same(actor, adjustment.SubmittedBy),
            "cancel" => adjustment.Status == BankAdjustmentStatuses.Cancelled && Same(actor, adjustment.CancelledBy) &&
                string.Equals(reason, adjustment.CancellationReason, StringComparison.Ordinal),
            "reject" => adjustment.Status == BankAdjustmentStatuses.Rejected && Same(actor, adjustment.RejectedBy) &&
                string.Equals(reason, adjustment.RejectionReason, StringComparison.Ordinal),
            "approve" => adjustment.Status == BankAdjustmentStatuses.Posted && Same(actor, adjustment.ApprovedBy),
            "reverse" => adjustment.Status == BankAdjustmentStatuses.Reversed && Same(actor, adjustment.ReversedBy) &&
                string.Equals(reason, adjustment.ReversalReason, StringComparison.Ordinal) &&
                string.Equals(evidence, adjustment.ReversalEvidenceReference, StringComparison.Ordinal),
            _ => false
        };
    }

    private IQueryable<BankAdjustment> Query() => _context.BankAdjustments.Include(x => x.Distributions)
        .Include(x => x.BankAccount).Include(x => x.BankStatementLine);
    private async Task<BankAdjustment> LockAsync(long businessUnitId, long id, CancellationToken ct)
    {
        if (_context.Database.IsNpgsql()) await _context.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM \"BankAdjustments\" WHERE \"BusinessUnitId\" = {businessUnitId} AND \"Id\" = {id} FOR UPDATE", ct);
        return await Query().SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == id, ct)
            ?? throw new KeyNotFoundException("Bank adjustment not found.");
    }
    private static BankAdjustmentDto Map(BankAdjustment x) => new(x.Id, x.BankAccountId, x.BankStatementLineId,
        x.AccountingPeriodId, x.AccountingDate, x.AdjustmentType, x.Description, x.Amount, x.EvidenceReference,
        x.Status, x.JournalEntryId, x.BankJournalEntryLineId, x.ReversalJournalEntryId, x.Version,
        x.PreparedBy, x.SubmittedBy, x.ApprovedBy, x.ReversedBy, x.Distributions.OrderBy(d => d.Sequence)
            .Select(d => new BankAdjustmentDistributionDto(d.Id, d.Sequence, d.LedgerAccountId, d.Amount, d.Description)).ToArray());
    private static string Actor(string value) => Token(value, "authenticated actor", 255);
    private static string Evidence(string? value) { var v = Token(value, "evidence reference", 500); if (v.Length < 8) throw new ArgumentException("Evidence reference requires at least eight characters."); return v; }
    private static string Reason(string? value, string field) { var v = Token(value, field, 500); if (v.Length < 20) throw new ArgumentException($"{field} requires at least 20 characters."); return v; }
    private static string Token(string? value, string field, int max) { value = value?.Trim() ?? ""; if (value.Length == 0 || value.Length > max) throw new ArgumentException($"A valid {field} is required."); return value; }
    private static void ValidateKey(string value) { if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > 128) throw new ArgumentException("A valid Idempotency-Key is required."); }
    private static bool Same(string left, string? right) => right is not null && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
    private static string Hash(object value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)))).ToLowerInvariant();
}
