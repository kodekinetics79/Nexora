using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ERP_RFQ_Automation.Billing.Accounting;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Billing;

public sealed record ProposeSubscriptionTaxRule(
    string JurisdictionCode, string BuyerCountryCode, string Currency, string Treatment,
    decimal RatePercent, string LegalAuthorityReference, string EvidenceSha256,
    DateTime EffectiveFromUtc, DateTime? EffectiveToUtc);

public sealed record SubscriptionTaxDetermination(
    long RuleId, long RuleVersion, string JurisdictionCode, string Treatment,
    decimal RatePercent, string EvidenceJson, string EvidenceSha256, DateTime DeterminedAtUtc);

public sealed class SubscriptionTaxService(ErpRfqAutomationContext db)
{
    public async Task<SubscriptionTaxRule> ProposeAsync(ProposeSubscriptionTaxRule command,
        long actorPlatformUserId, CancellationToken ct = default)
    {
        if (db.Database.IsRelational() && db.Database.CurrentTransaction is null)
            return await InTransactionAsync(() => ProposeAsync(command, actorPlatformUserId, ct), ct);
        ValidateActor(actorPlatformUserId);
        if (command.RatePercent is < 0 or > 100 || command.EffectiveFromUtc.Kind != DateTimeKind.Utc
            || command.EffectiveToUtc is DateTime end && (end.Kind != DateTimeKind.Utc || end <= command.EffectiveFromUtc))
            throw new BillingConflictException("A bounded tax rate and valid UTC effective interval are required.");
        Required(command.JurisdictionCode, 64, "jurisdiction code");
        Required(command.BuyerCountryCode, 2, "buyer country code");
        Required(command.Currency, 3, "currency");
        Required(command.Treatment, 128, "tax treatment");
        Required(command.LegalAuthorityReference, 1000, "legal authority reference");
        Sha(command.EvidenceSha256);
        await LockAsync($"tax-propose|{command.JurisdictionCode}|{command.BuyerCountryCode}|{command.Currency}", ct);
        var rule = new SubscriptionTaxRule
        {
            JurisdictionCode = command.JurisdictionCode.Trim().ToUpperInvariant(),
            BuyerCountryCode = command.BuyerCountryCode.Trim().ToUpperInvariant(),
            Currency = command.Currency.Trim().ToUpperInvariant(), Treatment = command.Treatment.Trim(),
            RatePercent = command.RatePercent, LegalAuthorityReference = command.LegalAuthorityReference.Trim(),
            EvidenceSha256 = command.EvidenceSha256.Trim().ToLowerInvariant(),
            EffectiveFromUtc = command.EffectiveFromUtc, EffectiveToUtc = command.EffectiveToUtc,
            Status = SubscriptionTaxRuleStatus.Draft, ProposedByPlatformUserId = actorPlatformUserId,
            ProposedAtUtc = DateTime.UtcNow
        };
        db.Add(rule); await db.SaveChangesAsync(ct); return rule;
    }

    public async Task<SubscriptionTaxRule> ApproveAsync(long id, long actorPlatformUserId, CancellationToken ct = default)
    {
        if (db.Database.IsRelational() && db.Database.CurrentTransaction is null)
            return await InTransactionAsync(() => ApproveAsync(id, actorPlatformUserId, ct), ct);
        ValidateActor(actorPlatformUserId);
        var rule = await db.Set<SubscriptionTaxRule>().SingleOrDefaultAsync(x => x.Id == id, ct)
                   ?? throw new BillingNotFoundException("Tax rule does not exist.");
        await LockAsync($"tax-approve|{rule.JurisdictionCode}|{rule.BuyerCountryCode}|{rule.Currency}", ct);
        if (rule.Status == SubscriptionTaxRuleStatus.Approved) return rule;
        if (rule.Status != SubscriptionTaxRuleStatus.Draft)
            throw new BillingConflictException("Only a draft tax rule can be approved.");
        if (rule.ProposedByPlatformUserId == actorPlatformUserId)
            throw new BillingConflictException("The tax-rule maker cannot approve the same rule.");
        var overlap = await db.Set<SubscriptionTaxRule>().AsNoTracking().AnyAsync(x => x.Id != id
            && x.Status == SubscriptionTaxRuleStatus.Approved
            && x.JurisdictionCode == rule.JurisdictionCode && x.BuyerCountryCode == rule.BuyerCountryCode
            && x.Currency == rule.Currency && (x.EffectiveToUtc == null || x.EffectiveToUtc > rule.EffectiveFromUtc)
            && (rule.EffectiveToUtc == null || x.EffectiveFromUtc < rule.EffectiveToUtc), ct);
        if (overlap) throw new BillingConflictException("An approved tax rule already overlaps this jurisdiction interval.");
        rule.Status = SubscriptionTaxRuleStatus.Approved; rule.ApprovedByPlatformUserId = actorPlatformUserId;
        // Version identifies the immutable legal rule revision and participates in the invoice's
        // frozen evidence reference. Approval changes workflow state, not the rule revision.
        rule.ApprovedAtUtc = DateTime.UtcNow; await db.SaveChangesAsync(ct); return rule;
    }

    public async Task<SubscriptionTaxDetermination> DetermineAsync(
        Tenant tenant, string currency, string? jurisdictionCode, DateTime taxPointUtc, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenant.CountryCode) || string.IsNullOrWhiteSpace(jurisdictionCode))
            throw new BillingConflictException("Tax determination is blocked: buyer country and jurisdiction are required.");
        var country = tenant.CountryCode.Trim().ToUpperInvariant();
        var jurisdiction = jurisdictionCode.Trim().ToUpperInvariant();
        var normalizedCurrency = currency.Trim().ToUpperInvariant();
        var matches = await db.Set<SubscriptionTaxRule>().AsNoTracking().Where(x =>
            x.Status == SubscriptionTaxRuleStatus.Approved && x.BuyerCountryCode == country
            && x.JurisdictionCode == jurisdiction && x.Currency == normalizedCurrency
            && x.EffectiveFromUtc <= taxPointUtc && (x.EffectiveToUtc == null || x.EffectiveToUtc > taxPointUtc))
            .ToListAsync(ct);
        if (matches.Count != 1)
            throw new BillingConflictException("Tax determination is blocked: the approved jurisdiction ruleset is missing or ambiguous.");
        var rule = matches[0];
        var determined = DateTime.UtcNow;
        var evidence = JsonSerializer.Serialize(new
        {
            schemaVersion = 1, rule.Id, rule.Version, rule.JurisdictionCode, rule.BuyerCountryCode,
            rule.Currency, rule.Treatment, rule.RatePercent, rule.LegalAuthorityReference,
            rule.EvidenceSha256, rule.EffectiveFromUtc, rule.EffectiveToUtc, taxPointUtc, determined
        });
        return new(rule.Id, rule.Version, rule.JurisdictionCode, rule.Treatment, rule.RatePercent,
            evidence, Hash(evidence), determined);
    }

    private static void ValidateActor(long id) { if (id <= 0) throw new BillingConflictException("A stable platform actor is required."); }
    private static void Required(string? value, int max, string name) { if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > max) throw new BillingConflictException($"A valid {name} is required."); }
    private static void Sha(string value) { if (value?.Length != 64 || value.Any(c => !Uri.IsHexDigit(c))) throw new BillingConflictException("A SHA-256 legal evidence hash is required."); }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private Task LockAsync(string value, CancellationToken ct) => db.Database.IsNpgsql()
        ? db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({StableLockKey(value)})", ct)
        : Task.CompletedTask;
    private static long StableLockKey(string value)
    {
        unchecked { ulong hash = 14695981039346656037UL; foreach (var c in value) { hash ^= c; hash *= 1099511628211UL; } return (long)hash; }
    }
    private async Task<T> InTransactionAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var result = await operation(); await transaction.CommitAsync(ct); return result;
        });
    }
}

public sealed record ProposeRevenueAction(
    SubscriptionRevenueActionKind Kind, decimal Amount, string Currency, string Reason,
    string EvidenceSha256, string? ExternalReference, string IdempotencyKey);

public sealed class SubscriptionRevenueControlService(
    ErpRfqAutomationContext db, AccountingOutboxService outbox)
{
    public async Task<SubscriptionRevenueAction> ProposeAsync(long invoiceId, ProposeRevenueAction command,
        long actorPlatformUserId, CancellationToken ct = default)
    {
        if (db.Database.IsRelational() && db.Database.CurrentTransaction is null)
            return await InTransactionAsync(() => ProposeAsync(invoiceId, command, actorPlatformUserId, ct), ct);
        if (command.Kind == SubscriptionRevenueActionKind.Dunning
                ? actorPlatformUserId != 0
                : actorPlatformUserId <= 0)
            throw new BillingConflictException(command.Kind == SubscriptionRevenueActionKind.Dunning
                ? "Automated dunning must use the explicit system actor."
                : "A stable platform actor is required.");
        Required(command.IdempotencyKey, 128, "idempotency key"); Required(command.Reason, 1000, "reason");
        if (command.Reason.Trim().Length < 10) throw new BillingConflictException("An action reason of at least 10 characters is required.");
        if (command.EvidenceSha256?.Length != 64 || command.EvidenceSha256.Any(c => !Uri.IsHexDigit(c)))
            throw new BillingConflictException("A SHA-256 action evidence hash is required.");
        var key = command.IdempotencyKey.Trim();
        await LockAsync($"revenue-propose|{key}", ct);
        var replay = await db.Set<SubscriptionRevenueAction>().AsNoTracking().SingleOrDefaultAsync(x => x.IdempotencyKey == key, ct);
        if (replay is not null)
        {
            var expectedActor = command.Kind == SubscriptionRevenueActionKind.Dunning ? (long?)null : actorPlatformUserId;
            var expectedExternalReference = string.IsNullOrWhiteSpace(command.ExternalReference)
                ? null : command.ExternalReference.Trim();
            if (replay.SubscriptionInvoiceId != invoiceId || replay.Kind != command.Kind || replay.Amount != command.Amount
                || !string.Equals(replay.Currency, command.Currency.Trim(), StringComparison.OrdinalIgnoreCase)
                || replay.Reason != command.Reason.Trim()
                || !string.Equals(replay.EvidenceSha256, command.EvidenceSha256.Trim(), StringComparison.OrdinalIgnoreCase)
                || replay.ExternalReference != expectedExternalReference
                || replay.ProposedByPlatformUserId != expectedActor)
                throw new BillingConflictException("The action idempotency key was already used for different details.");
            return replay;
        }
        var invoice = await db.Set<SubscriptionInvoice>().SingleOrDefaultAsync(x => x.Id == invoiceId, ct)
                      ?? throw new BillingNotFoundException("Subscription invoice does not exist.");
        if (invoice.Status is SubscriptionInvoiceStatus.Draft or SubscriptionInvoiceStatus.Void)
            throw new BillingConflictException("A draft or void invoice cannot receive an AR action.");
        if (!string.Equals(command.Currency, invoice.Currency, StringComparison.OrdinalIgnoreCase))
            throw new BillingConflictException("Action currency must match invoice currency.");
        ValidateAmount(invoice, command);
        var action = new SubscriptionRevenueAction
        {
            TenantId = invoice.TenantId, SubscriptionInvoiceId = invoice.Id, Kind = command.Kind,
            Status = command.Kind == SubscriptionRevenueActionKind.Dunning
                ? SubscriptionRevenueActionStatus.Approved : SubscriptionRevenueActionStatus.Proposed,
            IdempotencyKey = key, Amount = command.Amount, Currency = invoice.Currency,
            Reason = command.Reason.Trim(), EvidenceSha256 = command.EvidenceSha256.ToLowerInvariant(),
            ExternalReference = string.IsNullOrWhiteSpace(command.ExternalReference) ? null : command.ExternalReference.Trim(),
            ProposedByPlatformUserId = command.Kind == SubscriptionRevenueActionKind.Dunning ? null : actorPlatformUserId,
            ProposedAtUtc = DateTime.UtcNow,
            ApprovedByPlatformUserId = null,
            ApprovedAtUtc = command.Kind == SubscriptionRevenueActionKind.Dunning ? DateTime.UtcNow : null
        };
        db.Add(action); await db.SaveChangesAsync(ct);
        if (command.Kind == SubscriptionRevenueActionKind.Dunning)
            await CompleteAsync(invoice, action, ct);
        return action;
    }

    public async Task<SubscriptionRevenueAction> ApproveAsync(long actionId, long actorPlatformUserId,
        CancellationToken ct = default)
    {
        if (db.Database.IsRelational() && db.Database.CurrentTransaction is null)
            return await InTransactionAsync(() => ApproveAsync(actionId, actorPlatformUserId, ct), ct);
        if (actorPlatformUserId <= 0) throw new BillingConflictException("A stable platform actor is required.");
        var action = await db.Set<SubscriptionRevenueAction>().SingleOrDefaultAsync(x => x.Id == actionId, ct)
                     ?? throw new BillingNotFoundException("Revenue action does not exist.");
        await LockAsync($"revenue-approve|{action.Id}", ct);
        // A concurrent checker may have completed this tracked row while this transaction waited
        // on the advisory lock. Reload under the lock before evaluating or emitting the outbox.
        if (db.Database.IsNpgsql()) await db.Entry(action).ReloadAsync(ct);
        if (action.Status == SubscriptionRevenueActionStatus.Completed) return action;
        if (action.Status != SubscriptionRevenueActionStatus.Proposed)
            throw new BillingConflictException("Only a proposed action can be approved.");
        if (action.ProposedByPlatformUserId == actorPlatformUserId)
            throw new BillingConflictException("The action maker cannot approve the same action.");
        var invoice = await db.Set<SubscriptionInvoice>().SingleAsync(x => x.Id == action.SubscriptionInvoiceId, ct);
        ValidateAmount(invoice, new(action.Kind, action.Amount, action.Currency, action.Reason,
            action.EvidenceSha256, action.ExternalReference, action.IdempotencyKey));
        action.Status = SubscriptionRevenueActionStatus.Approved;
        action.ApprovedByPlatformUserId = actorPlatformUserId; action.ApprovedAtUtc = DateTime.UtcNow;
        await CompleteAsync(invoice, action, ct); return action;
    }

    private async Task CompleteAsync(SubscriptionInvoice invoice, SubscriptionRevenueAction action, CancellationToken ct)
    {
        switch (action.Kind)
        {
            case SubscriptionRevenueActionKind.Void: invoice.Status = SubscriptionInvoiceStatus.Void; break;
            case SubscriptionRevenueActionKind.Refund: invoice.RefundedAmount += action.Amount; break;
            case SubscriptionRevenueActionKind.PaymentReversal: invoice.ReversedPaymentAmount += action.Amount; break;
            case SubscriptionRevenueActionKind.WriteOff: invoice.WrittenOffAmount += action.Amount; break;
        }
        invoice.Version++; action.Status = SubscriptionRevenueActionStatus.Completed; action.CompletedAtUtc = DateTime.UtcNow;
        await outbox.EnqueueRevenueActionAsync(invoice, action, ct); await db.SaveChangesAsync(ct);
    }

    private static void ValidateAmount(SubscriptionInvoice invoice, ProposeRevenueAction command)
    {
        var netPaid = invoice.PaidAmount - invoice.RefundedAmount - invoice.ReversedPaymentAmount;
        var outstanding = Math.Max(0m, invoice.TotalAmount - invoice.CreditedAmount - netPaid - invoice.WrittenOffAmount);
        // Refund and reversal are two different dispositions of cash already collected. Neither
        // depends on an invoice overpayment/credit balance; together they may never exceed the
        // gross cash receipts recorded against this invoice.
        var refundableCash = Math.Max(0m, invoice.PaidAmount - invoice.RefundedAmount - invoice.ReversedPaymentAmount);
        if (command.Kind == SubscriptionRevenueActionKind.Dunning)
        {
            if (command.Amount != 0 || outstanding <= 0 || invoice.DueAtUtc >= DateTime.UtcNow)
                throw new BillingConflictException("Dunning requires an overdue positive balance and a zero action amount.");
            return;
        }
        if (command.Amount <= 0) throw new BillingConflictException("A positive action amount is required.");
        if (command.Kind == SubscriptionRevenueActionKind.Void && (invoice.PaidAmount != 0 || invoice.CreditedAmount != 0
                || invoice.WrittenOffAmount != 0 || command.Amount != invoice.TotalAmount))
            throw new BillingConflictException("Void requires an entirely unsettled invoice and its exact total amount.");
        if (command.Kind == SubscriptionRevenueActionKind.Refund && command.Amount > refundableCash)
            throw new BillingConflictException("Refund exceeds unreversed and unrefunded collected cash.");
        if (command.Kind == SubscriptionRevenueActionKind.PaymentReversal && command.Amount > refundableCash)
            throw new BillingConflictException("Payment reversal exceeds unreversed received cash.");
        if (command.Kind == SubscriptionRevenueActionKind.WriteOff && command.Amount > outstanding)
            throw new BillingConflictException("Write-off exceeds the outstanding receivable.");
    }

    private static void Required(string? value, int max, string name) { if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > max) throw new BillingConflictException($"A valid {name} is required."); }
    private Task LockAsync(string value, CancellationToken ct) => db.Database.IsNpgsql()
        ? db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({StableLockKey(value)})", ct)
        : Task.CompletedTask;
    private static long StableLockKey(string value)
    {
        unchecked { ulong hash = 14695981039346656037UL; foreach (var c in value) { hash ^= c; hash *= 1099511628211UL; } return (long)hash; }
    }
    private async Task<T> InTransactionAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var result = await operation(); await transaction.CommitAsync(ct); return result;
        });
    }
}
