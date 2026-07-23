using System.Security.Cryptography;
using System.Text;
using ERP_RFQ_Automation.BankReconciliation;
using ERP_RFQ_Automation.CommercialFinance;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.GeneralLedger;

public sealed record SourceJournalResult(long JournalEntryId, long BankJournalEntryLineId);

public interface IInternalSourceJournalPostingService
{
    Task<SourceJournalResult> CreateAndPostBankAdjustmentAsync(BankAdjustment adjustment,
        BankAccount bankAccount, string approver, CancellationToken ct);
    Task<SourceJournalResult> ReverseBankAdjustmentAsync(BankAdjustment adjustment,
        string controller, string reason, string evidenceReference, CancellationToken ct);
    Task<long> CreateAndPostCustomerPaymentAsync(CustomerPayment payment, BankAccount bankAccount,
        decimal allocatedAmount, string actor, CancellationToken ct);
    Task<long> ReverseCustomerPaymentAsync(CustomerPayment payment, string controller,
        string reason, CancellationToken ct);
    Task<long> CreateAndPostCustomerRefundAsync(CustomerRefund refund, BankAccount bankAccount,
        string actor, CancellationToken ct);
}

public sealed class InternalSourceJournalPostingService(ErpRfqAutomationContext context)
    : IInternalSourceJournalPostingService
{
    private readonly ErpRfqAutomationContext _context = context;

    public async Task<SourceJournalResult> CreateAndPostBankAdjustmentAsync(BankAdjustment adjustment,
        BankAccount bankAccount, string approver, CancellationToken ct)
    {
        var period = await _context.AccountingPeriods.SingleOrDefaultAsync(x =>
            x.BusinessUnitId == adjustment.BusinessUnitId && x.Id == adjustment.AccountingPeriodId, ct)
            ?? throw new ArgumentException("The accounting period does not belong to this tenant.");
        if (period.Status != AccountingPeriodStatuses.Open || adjustment.AccountingDate.Date < period.StartsOn.Date ||
            adjustment.AccountingDate.Date > period.EndsOn.Date)
            throw new GeneralLedgerConflictException("Bank adjustment date must be in an open accounting period.");
        var book = await _context.LedgerBooks.SingleAsync(x => x.BusinessUnitId == adjustment.BusinessUnitId, ct);
        var accountIds = adjustment.Distributions.Select(x => x.LedgerAccountId).Append(bankAccount.LedgerAccountId)
            .Distinct().ToArray();
        var accounts = await _context.LedgerAccounts.Where(x => x.BusinessUnitId == adjustment.BusinessUnitId &&
            accountIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        if (accounts.Count != accountIds.Length) throw new ArgumentException("A bank adjustment ledger account is missing.");
        foreach (var distribution in adjustment.Distributions)
        {
            var account = accounts[distribution.LedgerAccountId];
            if (!account.IsActive || account.IsControlAccount || distribution.LedgerAccountId == bankAccount.LedgerAccountId)
                throw new GeneralLedgerConflictException("Adjustment distributions require active non-control accounts distinct from cash.");
        }

        var bankDebit = adjustment.BankStatementLine.SignedAmount > 0m ? adjustment.Amount : 0m;
        var bankCredit = adjustment.BankStatementLine.SignedAmount < 0m ? adjustment.Amount : 0m;
        var lines = new List<JournalEntryLine>
        {
            Line(adjustment.BusinessUnitId, 1, bankAccount.LedgerAccountId, "Bank adjustment cash", book.FunctionalCurrencyId,
                bankDebit, bankCredit, $"BADJ:{adjustment.Id}:BANK")
        };
        lines.AddRange(adjustment.Distributions.OrderBy(x => x.Sequence).Select(x =>
            Line(adjustment.BusinessUnitId, x.Sequence + 1, x.LedgerAccountId, x.Description,
                book.FunctionalCurrencyId, bankCredit > 0m ? x.Amount : 0m, bankDebit > 0m ? x.Amount : 0m,
                $"BADJ:{adjustment.Id}:DIST:{x.Sequence}")));
        var journal = new JournalEntry
        {
            BusinessUnitId = adjustment.BusinessUnitId, AccountingPeriodId = adjustment.AccountingPeriodId,
            FunctionalCurrencyId = book.FunctionalCurrencyId, AccountingDate = adjustment.AccountingDate.Date,
            Description = adjustment.Description, SourceType = "BankAdjustment",
            SourceReference = adjustment.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            SourceVersion = adjustment.Version, TotalDebit = adjustment.Amount, TotalCredit = adjustment.Amount,
            IdempotencyKey = $"bank-adjustment:{adjustment.Id}:post", RequestHash = Hash($"{adjustment.RequestHash}:post"),
            CreatedBy = "system:bank-adjustment", CreatedOn = DateTime.UtcNow, Lines = lines
        };
        _context.JournalEntries.Add(journal); await _context.SaveChangesAsync(ct);
        journal.Status = JournalEntryStatuses.Posted; journal.PostedBy = approver; journal.PostedOn = DateTime.UtcNow;
        journal.Version++;
        if (!_context.Database.IsNpgsql()) journal.EntryNumber = $"BADJ-{adjustment.Id:D10}";
        await _context.SaveChangesAsync(ct);
        return new SourceJournalResult(journal.Id, journal.Lines.Single(x => x.Sequence == 1).Id);
    }

    public async Task<SourceJournalResult> ReverseBankAdjustmentAsync(BankAdjustment adjustment,
        string controller, string reason, string evidenceReference, CancellationToken ct)
    {
        var original = await _context.JournalEntries.Include(x => x.Lines).SingleAsync(x =>
            x.BusinessUnitId == adjustment.BusinessUnitId && x.Id == adjustment.JournalEntryId, ct);
        var reversalDate = DateTime.UtcNow.Date;
        var reversalPeriod = await RequireOpenPeriodAsync(adjustment.BusinessUnitId, reversalDate, ct);
        var reversal = new JournalEntry
        {
            BusinessUnitId = original.BusinessUnitId, AccountingPeriodId = reversalPeriod.Id,
            FunctionalCurrencyId = original.FunctionalCurrencyId, AccountingDate = reversalDate,
            Description = $"Reversal: {original.Description}", SourceType = "JournalReversal",
            SourceReference = original.EntryNumber, SourceVersion = original.Version,
            ReversesJournalEntryId = original.Id, TotalDebit = original.TotalCredit, TotalCredit = original.TotalDebit,
            IdempotencyKey = $"bank-adjustment:{adjustment.Id}:reverse", RequestHash = Hash($"{adjustment.RequestHash}:reverse"),
            CreatedBy = "system:journal-reversal", CreatedOn = DateTime.UtcNow,
            Lines = original.Lines.OrderBy(x => x.Sequence).Select(x => new JournalEntryLine
            {
                BusinessUnitId = x.BusinessUnitId, Sequence = x.Sequence, LedgerAccountId = x.LedgerAccountId,
                Description = $"Reversal: {x.Description}", TransactionCurrencyId = x.TransactionCurrencyId,
                ExchangeRate = x.ExchangeRate, TransactionDebit = x.TransactionCredit,
                TransactionCredit = x.TransactionDebit, FunctionalDebit = x.FunctionalCredit,
                FunctionalCredit = x.FunctionalDebit, SourceReference = x.SourceReference
            }).ToList()
        };
        _context.JournalEntries.Add(reversal); await _context.SaveChangesAsync(ct);
        reversal.Status = JournalEntryStatuses.Posted; reversal.PostedBy = controller; reversal.PostedOn = DateTime.UtcNow;
        reversal.Version++;
        if (!_context.Database.IsNpgsql()) reversal.EntryNumber = $"BADJ-R-{adjustment.Id:D10}";
        await _context.SaveChangesAsync(ct);
        original.Status = JournalEntryStatuses.Reversed; original.ReversedBy = controller; original.ReversedOn = DateTime.UtcNow;
        original.ReversalReason = reason; original.ReversalEvidenceReference = evidenceReference; original.Version++;
        await _context.SaveChangesAsync(ct);
        return new SourceJournalResult(reversal.Id, reversal.Lines.Single(x => x.Sequence == 1).Id);
    }

    public async Task<long> CreateAndPostCustomerPaymentAsync(CustomerPayment payment, BankAccount bankAccount,
        decimal allocatedAmount, string actor, CancellationToken ct)
    {
        var book = await RequireReceivablesBookAsync(payment.BusinessUnitId, ct);
        var period = await RequireOpenPeriodAsync(payment.BusinessUnitId, payment.PaymentDate, ct);
        var lines = new List<JournalEntryLine>
        {
            Line(payment.BusinessUnitId, 1, bankAccount.LedgerAccountId, "Customer receipt cash",
                book.FunctionalCurrencyId, payment.Amount, 0m, $"PAY:{payment.Id}:BANK")
        };
        var sequence = 2;
        if (allocatedAmount > 0m) lines.Add(Line(payment.BusinessUnitId, sequence++, book.ReceivablesControlAccountId!.Value,
            "Customer receipt allocation", book.FunctionalCurrencyId, 0m, allocatedAmount, $"PAY:{payment.Id}:AR"));
        var unapplied = payment.Amount - allocatedAmount;
        if (unapplied > 0m) lines.Add(Line(payment.BusinessUnitId, sequence, book.UnappliedCashAccountId!.Value,
            "Customer receipt unapplied cash", book.FunctionalCurrencyId, 0m, unapplied, $"PAY:{payment.Id}:UNAPPLIED"));
        var journal = await CreateAndPostSourceAsync(payment.BusinessUnitId, period.Id, book.FunctionalCurrencyId,
            payment.PaymentDate, $"Customer receipt {payment.ReceiptNumber}", "CustomerPayment", payment.Id.ToString(),
            payment.Version, payment.Amount, $"ar-payment:{payment.Id}:v1", payment.RequestHash, lines, actor, ct);
        return journal.Id;
    }

    public async Task<long> ReverseCustomerPaymentAsync(CustomerPayment payment, string controller,
        string reason, CancellationToken ct)
    {
        var original = await _context.JournalEntries.Include(x => x.Lines).SingleAsync(x =>
            x.BusinessUnitId == payment.BusinessUnitId && x.Id == payment.JournalEntryId, ct);
        return (await ReverseSourceJournalAsync(original, $"ar-payment:{payment.Id}:reverse", controller,
            reason, $"payment:{payment.Id}:reversal", ct)).JournalEntryId;
    }

    public async Task<long> CreateAndPostCustomerRefundAsync(CustomerRefund refund, BankAccount bankAccount,
        string actor, CancellationToken ct)
    {
        var book = await RequireReceivablesBookAsync(refund.BusinessUnitId, ct);
        var period = await RequireOpenPeriodAsync(refund.BusinessUnitId, refund.RequestedExecutionDate, ct);
        var lines = new List<JournalEntryLine>
        {
            Line(refund.BusinessUnitId, 1, book.UnappliedCashAccountId!.Value, "Customer refund liability",
                book.FunctionalCurrencyId, refund.Amount, 0m, $"REF:{refund.Id}:UNAPPLIED"),
            Line(refund.BusinessUnitId, 2, bankAccount.LedgerAccountId, "Customer refund cash",
                book.FunctionalCurrencyId, 0m, refund.Amount, $"REF:{refund.Id}:BANK")
        };
        var journal = await CreateAndPostSourceAsync(refund.BusinessUnitId, period.Id, book.FunctionalCurrencyId,
            refund.RequestedExecutionDate, $"Customer refund {refund.RefundNumber}", "CustomerRefund",
            refund.Id.ToString(), refund.Version + 1, refund.Amount, $"ar-refund:{refund.Id}:settlement",
            refund.RequestHash, lines, actor, ct);
        return journal.Id;
    }

    private async Task<JournalEntry> CreateAndPostSourceAsync(long businessUnitId, long periodId, long currencyId,
        DateTime accountingDate, string description, string sourceType, string sourceReference, long sourceVersion,
        decimal amount, string idempotencyKey, string requestHash, List<JournalEntryLine> lines,
        string actor, CancellationToken ct)
    {
        var journal = new JournalEntry
        {
            BusinessUnitId = businessUnitId, AccountingPeriodId = periodId, FunctionalCurrencyId = currencyId,
            AccountingDate = accountingDate.Date, Description = description, SourceType = sourceType,
            SourceReference = sourceReference, SourceVersion = sourceVersion, TotalDebit = amount, TotalCredit = amount,
            IdempotencyKey = idempotencyKey, RequestHash = Hash($"{requestHash}:{sourceType}"),
            CreatedBy = $"system:{sourceType.ToLowerInvariant()}", CreatedOn = DateTime.UtcNow, Lines = lines
        };
        _context.JournalEntries.Add(journal); await _context.SaveChangesAsync(ct);
        journal.Status = JournalEntryStatuses.Posted; journal.PostedBy = actor; journal.PostedOn = DateTime.UtcNow;
        journal.Version++;
        if (!_context.Database.IsNpgsql()) journal.EntryNumber = $"{(sourceType == "CustomerPayment" ? "PAY" : "REF")}-{sourceReference}";
        await _context.SaveChangesAsync(ct); return journal;
    }

    private async Task<SourceJournalResult> ReverseSourceJournalAsync(JournalEntry original, string idempotencyKey,
        string controller, string reason, string evidenceReference, CancellationToken ct)
    {
        var reversal = new JournalEntry
        {
            BusinessUnitId = original.BusinessUnitId, AccountingPeriodId = original.AccountingPeriodId,
            FunctionalCurrencyId = original.FunctionalCurrencyId, AccountingDate = original.AccountingDate,
            Description = $"Reversal: {original.Description}", SourceType = "JournalReversal",
            SourceReference = original.EntryNumber, SourceVersion = original.Version,
            ReversesJournalEntryId = original.Id, TotalDebit = original.TotalCredit, TotalCredit = original.TotalDebit,
            IdempotencyKey = idempotencyKey, RequestHash = Hash($"{original.RequestHash}:reverse"),
            CreatedBy = "system:journal-reversal", CreatedOn = DateTime.UtcNow,
            Lines = original.Lines.OrderBy(x => x.Sequence).Select(x => new JournalEntryLine
            {
                BusinessUnitId = x.BusinessUnitId, Sequence = x.Sequence, LedgerAccountId = x.LedgerAccountId,
                Description = $"Reversal: {x.Description}", TransactionCurrencyId = x.TransactionCurrencyId,
                ExchangeRate = x.ExchangeRate, TransactionDebit = x.TransactionCredit,
                TransactionCredit = x.TransactionDebit, FunctionalDebit = x.FunctionalCredit,
                FunctionalCredit = x.FunctionalDebit, SourceReference = x.SourceReference
            }).ToList()
        };
        _context.JournalEntries.Add(reversal); await _context.SaveChangesAsync(ct);
        reversal.Status = JournalEntryStatuses.Posted; reversal.PostedBy = controller; reversal.PostedOn = DateTime.UtcNow;
        reversal.Version++; if (!_context.Database.IsNpgsql()) reversal.EntryNumber = $"REV-{original.Id}";
        await _context.SaveChangesAsync(ct);
        original.Status = JournalEntryStatuses.Reversed; original.ReversedBy = controller; original.ReversedOn = DateTime.UtcNow;
        original.ReversalReason = reason; original.ReversalEvidenceReference = evidenceReference; original.Version++;
        await _context.SaveChangesAsync(ct);
        return new SourceJournalResult(reversal.Id, reversal.Lines.OrderBy(x => x.Sequence).First().Id);
    }

    private async Task<LedgerBook> RequireReceivablesBookAsync(long businessUnitId, CancellationToken ct)
    {
        var book = await _context.LedgerBooks.SingleAsync(x => x.BusinessUnitId == businessUnitId, ct);
        if (!book.ReceivablesControlAccountId.HasValue || !book.UnappliedCashAccountId.HasValue)
            throw new GeneralLedgerConflictException("Configure the receivables posting accounts before posting cash.");
        return book;
    }

    private async Task<AccountingPeriod> RequireOpenPeriodAsync(long businessUnitId, DateTime date, CancellationToken ct)
        => await _context.AccountingPeriods.SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId &&
               x.Status == AccountingPeriodStatuses.Open && date.Date >= x.StartsOn.Date && date.Date <= x.EndsOn.Date, ct)
           ?? throw new GeneralLedgerConflictException("The cash accounting date is not in a unique open period.");

    private static JournalEntryLine Line(long businessUnitId, int sequence, long accountId, string description,
        long currencyId, decimal debit, decimal credit, string sourceReference) => new()
    {
        BusinessUnitId = businessUnitId, Sequence = sequence, LedgerAccountId = accountId,
        Description = description, TransactionCurrencyId = currencyId, ExchangeRate = 1m,
        TransactionDebit = debit, TransactionCredit = credit, FunctionalDebit = debit,
        FunctionalCredit = credit, SourceReference = sourceReference
    };
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
