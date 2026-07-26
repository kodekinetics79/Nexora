using System.Data;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.QuoteDelivery;

public sealed record QuoteDeliveryEnvelope(
    long Id, long BusinessUnitId, long QuoteId, string RecipientEmail, string Subject,
    string Body, string? FromEmail, string AttachmentFileName, int AttemptCount, Guid LeaseToken);

public interface IQuoteDeliveryStore
{
    Task<IReadOnlyList<QuoteDeliveryEnvelope>> ClaimAsync(string workerId, int batchSize, TimeSpan lease, CancellationToken ct);
    Task CompleteAsync(long id, string workerId, Guid leaseToken, CancellationToken ct);
    Task FailAsync(long id, string workerId, Guid leaseToken, string errorCode, int maxAttempts, CancellationToken ct);
    Task MarkOutcomeUncertainAsync(long id, string workerId, Guid leaseToken, string errorCode, CancellationToken ct);
}

public sealed class QuoteDeliveryStore(ErpRfqAutomationContext db) : IQuoteDeliveryStore
{
    public async Task<IReadOnlyList<QuoteDeliveryEnvelope>> ClaimAsync(
        string workerId, int batchSize, TimeSpan lease, CancellationToken ct)
    {
        ValidateWorker(workerId);
        batchSize = Math.Clamp(batchSize, 1, 100);
        if (lease < TimeSpan.FromSeconds(10) || lease > TimeSpan.FromMinutes(15))
            throw new ArgumentOutOfRangeException(nameof(lease));

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var now = DateTime.UtcNow;
            var abandoned = await db.Set<QuoteDeliveryRequest>()
                .Where(x => x.CompletedOn == null && x.DeadLetteredOn == null && x.AttemptCount > 0
                    && x.LastErrorCode == null && x.LeaseUntil < now).ToListAsync(ct);
            foreach (var item in abandoned)
            {
                item.LeaseOwner = null;
                item.LeaseToken = null;
                item.LeaseUntil = null;
                item.DeadLetteredOn = now;
                item.LastErrorCode = "DeliveryOutcomeUncertain";
                item.Version++;
            }
            var candidates = await db.Set<QuoteDeliveryRequest>()
                .Where(x => x.CompletedOn == null && x.DeadLetteredOn == null && x.AvailableOn <= now)
                .Where(x => x.AttemptCount == 0 || x.LastErrorCode != null)
                .Where(x => x.LeaseUntil == null || x.LeaseUntil < now)
                .OrderBy(x => x.AvailableOn).ThenBy(x => x.Id)
                .Take(batchSize).ToListAsync(ct);
            foreach (var item in candidates)
            {
                item.LeaseOwner = workerId.Trim();
                item.LeaseToken = Guid.NewGuid();
                item.LeaseUntil = now.Add(lease);
                item.LastAttemptOn = now;
                item.AttemptCount++;
                item.Version++;
            }
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return candidates.Select(x => new QuoteDeliveryEnvelope(
                x.Id, x.BusinessUnitId, x.QuoteId, x.RecipientEmail, x.Subject, x.Body,
                x.FromEmail, x.AttachmentFileName, x.AttemptCount, x.LeaseToken!.Value)).ToArray();
        });
    }

    public Task CompleteAsync(long id, string workerId, Guid leaseToken, CancellationToken ct) =>
        MutateLeaseAsync(id, workerId, leaseToken, null, 0, true, ct);

    public Task FailAsync(long id, string workerId, Guid leaseToken, string errorCode, int maxAttempts, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(errorCode)) throw new ArgumentException("Error code is required.", nameof(errorCode));
        if (maxAttempts is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        return MutateLeaseAsync(id, workerId, leaseToken, errorCode.Trim(), maxAttempts, false, ct);
    }

    public Task MarkOutcomeUncertainAsync(long id, string workerId, Guid leaseToken, string errorCode,
        CancellationToken ct) => MutateLeaseAsync(id, workerId, leaseToken,
            $"DeliveryOutcomeUncertain:{errorCode}", 1, false, ct);

    private async Task MutateLeaseAsync(long id, string workerId, Guid leaseToken, string? errorCode,
        int maxAttempts, bool complete, CancellationToken ct)
    {
        ValidateWorker(workerId);
        var now = DateTime.UtcNow;
        var item = await db.Set<QuoteDeliveryRequest>().SingleOrDefaultAsync(x =>
            x.Id == id && x.CompletedOn == null && x.DeadLetteredOn == null &&
            x.LeaseOwner == workerId.Trim() && x.LeaseToken == leaseToken && x.LeaseUntil > now, ct)
            ?? throw new InvalidOperationException("Quote delivery lease is missing, expired, or fenced.");
        item.LeaseOwner = null;
        item.LeaseToken = null;
        item.LeaseUntil = null;
        item.Version++;
        if (complete)
        {
            item.CompletedOn = now;
            item.LastErrorCode = null;
        }
        else
        {
            item.LastErrorCode = errorCode!.Length <= 160 ? errorCode : errorCode[..160];
            if (item.AttemptCount >= maxAttempts) item.DeadLetteredOn = now;
            else item.AvailableOn = now.AddSeconds(Math.Min(300, Math.Pow(2, Math.Min(item.AttemptCount, 8))));
        }
        await db.SaveChangesAsync(ct);
    }

    private static void ValidateWorker(string workerId)
    {
        if (string.IsNullOrWhiteSpace(workerId) || workerId.Trim().Length > 200)
            throw new ArgumentException("A worker ID up to 200 characters is required.", nameof(workerId));
    }
}
