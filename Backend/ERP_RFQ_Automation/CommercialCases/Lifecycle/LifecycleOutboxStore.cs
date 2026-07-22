using System.Data;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.CommercialCases.Lifecycle;

public sealed record LifecycleOutboxEnvelope(
    long Id,
    long BusinessUnitId,
    long LifecycleEventId,
    string EventType,
    int SchemaVersion,
    string Payload,
    DateTime OccurredOn,
    int AttemptCount);

public interface ILifecycleOutboxStore
{
    Task<IReadOnlyList<LifecycleOutboxEnvelope>> ClaimAsync(string workerId, int batchSize, TimeSpan lease, CancellationToken ct);
    Task CompleteAsync(long messageId, string workerId, CancellationToken ct);
    Task FailAsync(long messageId, string workerId, string error, int maxAttempts, CancellationToken ct);
}

public sealed class LifecycleOutboxStore : ILifecycleOutboxStore
{
    private readonly ErpRfqAutomationContext _db;

    public LifecycleOutboxStore(ErpRfqAutomationContext db) => _db = db;

    public async Task<IReadOnlyList<LifecycleOutboxEnvelope>> ClaimAsync(
        string workerId, int batchSize, TimeSpan lease, CancellationToken ct)
    {
        ValidateWorker(workerId);
        batchSize = Math.Clamp(batchSize, 1, 200);
        if (lease < TimeSpan.FromSeconds(5) || lease > TimeSpan.FromMinutes(15))
            throw new ArgumentOutOfRangeException(nameof(lease), "Lease must be between 5 seconds and 15 minutes.");
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var now = DateTime.UtcNow;
            var candidates = await _db.LifecycleOutboxMessages
                .Where(x => x.ProcessedOn == null && x.DeadLetteredOn == null && x.AvailableOn <= now)
                .Where(x => x.LockedUntil == null || x.LockedUntil < now)
                .OrderBy(x => x.AvailableOn).ThenBy(x => x.OccurredOn).ThenBy(x => x.Id)
                .Take(batchSize)
                .ToListAsync(ct);
            foreach (var item in candidates)
            {
                item.LockedBy = workerId.Trim();
                item.LockedUntil = now.Add(lease);
                item.AttemptCount++;
            }
            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return candidates.Select(ToEnvelope).ToArray();
        });
    }

    public async Task CompleteAsync(long messageId, string workerId, CancellationToken ct)
    {
        ValidateWorker(workerId);
        if (messageId <= 0) throw new ArgumentOutOfRangeException(nameof(messageId));
        var now = DateTime.UtcNow;
        var affected = await _db.LifecycleOutboxMessages
            .Where(x => x.Id == messageId && x.ProcessedOn == null && x.DeadLetteredOn == null
                && x.LockedBy == workerId.Trim() && x.LockedUntil > now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.ProcessedOn, now)
                .SetProperty(x => x.LockedBy, (string?)null)
                .SetProperty(x => x.LockedUntil, (DateTime?)null)
                .SetProperty(x => x.LastError, (string?)null), ct);
        if (affected != 1)
            throw new LifecycleConflictException("The outbox lease is missing, expired, or owned by another worker.");
    }

    public async Task FailAsync(long messageId, string workerId, string error, int maxAttempts, CancellationToken ct)
    {
        ValidateWorker(workerId);
        if (string.IsNullOrWhiteSpace(error)) throw new ArgumentException("Error is required.", nameof(error));
        if (maxAttempts is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        if (messageId <= 0) throw new ArgumentOutOfRangeException(nameof(messageId));
        var now = DateTime.UtcNow;
        var message = error.Trim().Length <= 2000 ? error.Trim() : error.Trim()[..2000];
        var retryOn = now.AddSeconds(30);
        var affected = await _db.LifecycleOutboxMessages
            .Where(x => x.Id == messageId && x.ProcessedOn == null && x.DeadLetteredOn == null
                && x.LockedBy == workerId.Trim() && x.LockedUntil > now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.LastError, message)
                .SetProperty(x => x.LockedBy, (string?)null)
                .SetProperty(x => x.LockedUntil, (DateTime?)null)
                .SetProperty(x => x.DeadLetteredOn, x => x.AttemptCount >= maxAttempts ? now : x.DeadLetteredOn)
                .SetProperty(x => x.AvailableOn, x => x.AttemptCount >= maxAttempts ? x.AvailableOn : retryOn), ct);
        if (affected != 1)
            throw new LifecycleConflictException("The outbox lease is missing, expired, or owned by another worker.");
    }

    private static LifecycleOutboxEnvelope ToEnvelope(LifecycleOutboxMessage item)
        => new(item.Id, item.BusinessUnitId, item.LifecycleEventId, item.EventType,
            item.SchemaVersion, item.Payload, item.OccurredOn, item.AttemptCount);

    private static void ValidateWorker(string workerId)
    {
        if (string.IsNullOrWhiteSpace(workerId) || workerId.Trim().Length > 200)
            throw new ArgumentException("A worker ID up to 200 characters is required.", nameof(workerId));
    }
}
