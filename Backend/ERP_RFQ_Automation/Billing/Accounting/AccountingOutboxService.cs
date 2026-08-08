using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Billing.Accounting;

public sealed class AccountingOutboxException(string message) : InvalidOperationException(message);

public sealed class AccountingOutboxService(ErpRfqAutomationContext db)
{
    public async Task<AccountingOutboxMessage> EnqueueInvoiceExportAsync(
        SubscriptionInvoice invoice, CancellationToken ct = default)
    {
        if (invoice.Status != SubscriptionInvoiceStatus.Finalized)
            throw new AccountingOutboxException("Only a finalized invoice can enter the accounting outbox.");
        var key = $"subscription-invoice.finalized:{invoice.Id}:{invoice.Version}";
        var existing = await db.Set<AccountingOutboxMessage>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.IdempotencyKey == key, ct);
        if (existing is not null) return existing;

        var payload = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            invoiceId = invoice.Id.ToString(),
            invoice.TenantId,
            invoice.InvoiceNumber,
            invoice.Currency,
            invoice.Subtotal,
            invoice.TaxRatePercent,
            invoice.TaxAmount,
            invoice.TotalAmount,
            invoice.IssuedAtUtc,
            invoice.DueAtUtc,
            invoice.TaxTreatment,
            invoice.SellerSnapshotJson,
            invoice.BuyerSnapshotJson,
            invoice.SourceEvidenceSha256
        });
        var now = DateTime.UtcNow;
        var message = new AccountingOutboxMessage
        {
            Id = Guid.NewGuid(), TenantId = invoice.TenantId, SubscriptionInvoiceId = invoice.Id,
            MessageType = "subscription-invoice.finalized", IdempotencyKey = key,
            PayloadJson = payload, PayloadSha256 = Sha256(payload), Status = AccountingOutboxStatus.Pending,
            ReconciliationStatus = AccountingReconciliationStatus.NotSent,
            CreatedAtUtc = now, AvailableAtUtc = now
        };
        db.Add(message);
        return message;
    }

    public async Task<IReadOnlyList<AccountingOutboxMessage>> ClaimAsync(
        string workerId, int maximum = 25, TimeSpan? lease = null, CancellationToken ct = default)
    {
        Required(workerId, 128, "worker identifier");
        if (maximum is < 1 or > 100) throw new AccountingOutboxException("Claim size must be between 1 and 100.");
        var leaseFor = lease ?? TimeSpan.FromMinutes(2);
        if (leaseFor < TimeSpan.FromSeconds(15) || leaseFor > TimeSpan.FromMinutes(15))
            throw new AccountingOutboxException("Lease must be between 15 seconds and 15 minutes.");
        var now = DateTime.UtcNow;
        var token = Guid.NewGuid();

        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($$"""
                UPDATE platform."AccountingOutbox"
                SET "Status" = 'Poison', "ReconciliationStatus" = 'Exception',
                    "LeaseExpiresAtUtc" = NULL, "LeaseToken" = NULL, "WorkerId" = NULL,
                    "LastFailureCode" = COALESCE("LastFailureCode", 'LEASE_EXHAUSTED')
                WHERE "Status" = 'InFlight' AND "LeaseExpiresAtUtc" <= {{now}}
                  AND "AttemptCount" >= "MaxAttempts"
                """, ct);
            return await db.Set<AccountingOutboxMessage>().FromSqlInterpolated($$"""
                WITH candidates AS (
                    SELECT "Id" FROM platform."AccountingOutbox"
                    WHERE (("Status" IN ('Pending', 'RetryScheduled') AND "AvailableAtUtc" <= {{now}})
                       OR ("Status" = 'InFlight' AND "LeaseExpiresAtUtc" <= {{now}}
                           AND "AttemptCount" < "MaxAttempts"))
                    ORDER BY "CreatedAtUtc", "Id"
                    FOR UPDATE SKIP LOCKED
                    LIMIT {{maximum}}
                )
                UPDATE platform."AccountingOutbox" AS message
                SET "Status" = 'InFlight', "ReconciliationStatus" = 'AwaitingAcknowledgement',
                    "AttemptCount" = message."AttemptCount" + 1, "LastAttemptAtUtc" = {{now}},
                    "LeaseExpiresAtUtc" = {{now.Add(leaseFor)}}, "LeaseToken" = {{token}}, "WorkerId" = {{workerId.Trim()}}
                FROM candidates WHERE message."Id" = candidates."Id"
                RETURNING message.*
                """).AsNoTracking().ToListAsync(ct);
        }

        var candidates = await db.Set<AccountingOutboxMessage>()
            .Where(x => (x.Status == AccountingOutboxStatus.Pending || x.Status == AccountingOutboxStatus.RetryScheduled)
                        && x.AvailableAtUtc <= now
                        || x.Status == AccountingOutboxStatus.InFlight && x.LeaseExpiresAtUtc <= now)
            .OrderBy(x => x.CreatedAtUtc).Take(maximum).ToListAsync(ct);
        foreach (var message in candidates)
        {
            message.Status = AccountingOutboxStatus.InFlight;
            message.ReconciliationStatus = AccountingReconciliationStatus.AwaitingAcknowledgement;
            message.AttemptCount++;
            message.LastAttemptAtUtc = now;
            message.LeaseExpiresAtUtc = now.Add(leaseFor);
            message.LeaseToken = token;
            message.WorkerId = workerId.Trim();
        }
        await db.SaveChangesAsync(ct);
        return candidates;
    }

    public async Task AcknowledgeAsync(Guid id, Guid leaseToken, string externalReference,
        string receiptSha256, string actor, CancellationToken ct = default)
    {
        if (db.Database.IsRelational() && db.Database.CurrentTransaction is null)
        {
            await InTransactionAsync(() => AcknowledgeAsync(id, leaseToken, externalReference, receiptSha256, actor, ct), ct);
            return;
        }
        Required(externalReference, 256, "external reference");
        Required(actor, 256, "acknowledging actor");
        if (receiptSha256?.Length != 64 || receiptSha256.Any(c => !Uri.IsHexDigit(c)))
            throw new AccountingOutboxException("A SHA-256 external receipt hash is required.");
        var message = await LockedAsync(id, ct);
        if (message.Status == AccountingOutboxStatus.Acknowledged)
        {
            if (message.ExternalReference != externalReference.Trim()
                || message.ExternalReceiptSha256 != receiptSha256.ToLowerInvariant())
                throw new AccountingOutboxException("The outbox acknowledgement conflicts with the recorded receipt.");
            return;
        }
        RequireLease(message, leaseToken);
        message.Status = AccountingOutboxStatus.Acknowledged;
        message.ReconciliationStatus = AccountingReconciliationStatus.Reconciled;
        message.ExternalReference = externalReference.Trim();
        message.ExternalReceiptSha256 = receiptSha256.ToLowerInvariant();
        message.AcknowledgedAtUtc = DateTime.UtcNow;
        message.AcknowledgedBy = actor.Trim();
        message.LeaseExpiresAtUtc = null;
        message.LeaseToken = null;
        message.WorkerId = null;
        await db.SaveChangesAsync(ct);
    }

    public async Task FailAsync(Guid id, Guid leaseToken, string stableFailureCode,
        TimeSpan retryAfter, CancellationToken ct = default)
    {
        if (db.Database.IsRelational() && db.Database.CurrentTransaction is null)
        {
            await InTransactionAsync(() => FailAsync(id, leaseToken, stableFailureCode, retryAfter, ct), ct);
            return;
        }
        Required(stableFailureCode, 64, "stable failure code");
        if (retryAfter < TimeSpan.Zero || retryAfter > TimeSpan.FromDays(7))
            throw new AccountingOutboxException("Retry delay must be between zero and seven days.");
        var message = await LockedAsync(id, ct);
        RequireLease(message, leaseToken);
        message.LastFailureCode = stableFailureCode.Trim();
        message.LeaseExpiresAtUtc = null;
        message.LeaseToken = null;
        message.WorkerId = null;
        if (message.AttemptCount >= message.MaxAttempts)
        {
            message.Status = AccountingOutboxStatus.Poison;
            message.ReconciliationStatus = AccountingReconciliationStatus.Exception;
        }
        else
        {
            message.Status = AccountingOutboxStatus.RetryScheduled;
            message.AvailableAtUtc = DateTime.UtcNow.Add(retryAfter);
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task RedriveAsync(Guid id, string actor, string reason, CancellationToken ct = default)
    {
        if (db.Database.IsRelational() && db.Database.CurrentTransaction is null)
        {
            await InTransactionAsync(() => RedriveAsync(id, actor, reason, ct), ct);
            return;
        }
        Required(actor, 256, "redrive actor");
        Required(reason, 1000, "redrive reason");
        if (reason.Trim().Length < 10) throw new AccountingOutboxException("A redrive reason of at least 10 characters is required.");
        var message = await LockedAsync(id, ct);
        if (message.Status != AccountingOutboxStatus.Poison)
            throw new AccountingOutboxException("Only a poison accounting message can be redriven.");
        message.Status = AccountingOutboxStatus.Pending;
        message.ReconciliationStatus = AccountingReconciliationStatus.NotSent;
        message.AttemptCount = 0;
        message.AvailableAtUtc = DateTime.UtcNow;
        message.RedrivenAtUtc = DateTime.UtcNow;
        message.RedrivenBy = actor.Trim();
        message.RedriveReason = reason.Trim();
        message.LastFailureCode = null;
        await db.SaveChangesAsync(ct);
    }

    public async Task<AccountingOutboxHealth> HealthAsync(CancellationToken ct = default)
    {
        var values = await db.Set<AccountingOutboxMessage>().AsNoTracking().ToListAsync(ct);
        var open = values.Where(x => x.Status != AccountingOutboxStatus.Acknowledged).ToList();
        return new AccountingOutboxHealth(
            values.Count(x => x.Status == AccountingOutboxStatus.Pending),
            values.Count(x => x.Status == AccountingOutboxStatus.InFlight),
            values.Count(x => x.Status == AccountingOutboxStatus.RetryScheduled),
            values.Count(x => x.Status == AccountingOutboxStatus.Poison),
            open.Count == 0 ? null : open.Min(x => x.CreatedAtUtc));
    }

    private async Task<AccountingOutboxMessage> LockedAsync(Guid id, CancellationToken ct)
    {
        if (db.Database.IsNpgsql())
            return await db.Set<AccountingOutboxMessage>().FromSqlInterpolated(
                    $"SELECT * FROM platform.\"AccountingOutbox\" WHERE \"Id\" = {id} FOR UPDATE")
                .SingleOrDefaultAsync(ct) ?? throw new AccountingOutboxException("Accounting outbox message not found.");
        return await db.Set<AccountingOutboxMessage>().SingleOrDefaultAsync(x => x.Id == id, ct)
               ?? throw new AccountingOutboxException("Accounting outbox message not found.");
    }

    private static void RequireLease(AccountingOutboxMessage message, Guid token)
    {
        if (message.Status != AccountingOutboxStatus.InFlight || message.LeaseToken != token
            || message.LeaseExpiresAtUtc?.ToUniversalTime() <= DateTime.UtcNow)
            throw new AccountingOutboxException(
                $"The accounting delivery lease is missing, stale or owned by another worker " +
                $"(status={message.Status}, tokenMatch={message.LeaseToken == token}, expires={message.LeaseExpiresAtUtc:O}).");
    }

    private static string Sha256(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private async Task InTransactionAsync(Func<Task> operation, CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await operation();
            await transaction.CommitAsync(ct);
        });
    }

    private static void Required(string? value, int maximum, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > maximum)
            throw new AccountingOutboxException($"A {name} of at most {maximum} characters is required.");
    }
}
