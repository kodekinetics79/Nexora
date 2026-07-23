using System.Data;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.CommercialFinance;

public interface IFinanceOutboxStore
{
    Task<IReadOnlyList<FinanceOutboxEnvelope>> ClaimAsync(
        string workerId,
        int batchSize,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    Task CompleteAsync(
        long messageId,
        string workerId,
        Guid leaseToken,
        CancellationToken cancellationToken);

    Task FailAsync(
        long messageId,
        string workerId,
        Guid leaseToken,
        string error,
        TimeSpan retryDelay,
        int maxAttempts,
        CancellationToken cancellationToken);
}

public sealed class FinanceOutboxStore : IFinanceOutboxStore
{
    private const int MaximumBatchSize = 200;
    private const int MaximumAttempts = 100;
    private const int MaximumErrorLength = 2000;
    private static readonly TimeSpan MinimumLease = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaximumLease = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MinimumRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromHours(24);

    private readonly ErpRfqAutomationContext _db;

    public FinanceOutboxStore(ErpRfqAutomationContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<FinanceOutboxEnvelope>> ClaimAsync(
        string workerId,
        int batchSize,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        var owner = NormalizeWorkerId(workerId);
        batchSize = Math.Clamp(batchSize, 1, MaximumBatchSize);
        ValidateDuration(leaseDuration, MinimumLease, MaximumLease, nameof(leaseDuration));
        var claimToken = Guid.NewGuid();

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var transaction = await _db.Database.BeginTransactionAsync(
                _db.Database.IsNpgsql() ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable,
                cancellationToken);

            var existingClaim = await _db.Set<FinanceOutboxMessage>()
                .Where(x => x.ProcessedOn == null
                    && x.DeadLetteredOn == null
                    && x.LeaseOwner == owner
                    && x.LeaseToken == claimToken)
                .OrderBy(x => x.Id)
                .ToListAsync(cancellationToken);
            if (existingClaim.Count > 0)
            {
                await transaction.CommitAsync(cancellationToken);
                return existingClaim.Select(ToEnvelope).ToArray();
            }

            var now = await DatabaseUtcNowAsync(cancellationToken);
            var leaseUntil = now.Add(leaseDuration);
            var tenantId = _db.ScopedTenantId ?? 0;
            var ready = _db.Database.IsNpgsql()
                ? _db.Set<FinanceOutboxMessage>().FromSqlInterpolated($$"""
                    SELECT * FROM public."FinanceOutboxMessages"
                    WHERE "ProcessedOn" IS NULL
                      AND "DeadLetteredOn" IS NULL
                      AND ({{tenantId}} = 0 OR "BusinessUnitId" = {{tenantId}})
                      AND "AvailableOn" <= {{now}}
                      AND ("LeaseUntil" IS NULL OR "LeaseUntil" <= {{now}})
                    ORDER BY "AvailableOn", "OccurredOn", "Id"
                    FOR UPDATE SKIP LOCKED
                    LIMIT {{batchSize}}
                    """)
                : _db.Set<FinanceOutboxMessage>().Where(x => x.ProcessedOn == null
                    && x.DeadLetteredOn == null
                    && x.AvailableOn <= now
                    && (x.LeaseUntil == null || x.LeaseUntil <= now));
            var candidates = await ready
                .OrderBy(x => x.AvailableOn)
                .ThenBy(x => x.OccurredOn)
                .ThenBy(x => x.Id)
                .Take(batchSize)
                .ToListAsync(cancellationToken);

            foreach (var message in candidates)
            {
                message.LeaseOwner = owner;
                message.LeaseUntil = leaseUntil;
                message.LeaseToken = claimToken;
                message.AttemptCount++;
            }

            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return candidates.Select(ToEnvelope).ToArray();
        });
    }

    public async Task CompleteAsync(
        long messageId,
        string workerId,
        Guid leaseToken,
        CancellationToken cancellationToken)
    {
        ValidateMessageAndToken(messageId, leaseToken);
        var owner = NormalizeWorkerId(workerId);
        var now = await DatabaseUtcNowAsync(cancellationToken);
        var affected = await _db.Set<FinanceOutboxMessage>()
            .Where(x => x.Id == messageId
                && x.ProcessedOn == null
                && x.DeadLetteredOn == null
                && x.LeaseOwner == owner
                && x.LeaseToken == leaseToken
                && x.LeaseUntil > now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.ProcessedOn, now)
                .SetProperty(x => x.LeaseOwner, (string?)null)
                .SetProperty(x => x.LeaseUntil, (DateTime?)null)
                .SetProperty(x => x.LeaseToken, (Guid?)null)
                .SetProperty(x => x.LastError, (string?)null), cancellationToken);

        EnsureLeaseWasOwned(affected);
    }

    public async Task FailAsync(
        long messageId,
        string workerId,
        Guid leaseToken,
        string error,
        TimeSpan retryDelay,
        int maxAttempts,
        CancellationToken cancellationToken)
    {
        ValidateMessageAndToken(messageId, leaseToken);
        var owner = NormalizeWorkerId(workerId);
        if (string.IsNullOrWhiteSpace(error))
            throw new ArgumentException("Error is required.", nameof(error));
        if (maxAttempts is < 1 or > MaximumAttempts)
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), $"Maximum attempts must be between 1 and {MaximumAttempts}.");
        ValidateDuration(retryDelay, MinimumRetryDelay, MaximumRetryDelay, nameof(retryDelay));

        var now = await DatabaseUtcNowAsync(cancellationToken);
        var availableOn = now.Add(retryDelay);
        var normalizedError = error.Trim();
        if (normalizedError.Length > MaximumErrorLength)
            normalizedError = normalizedError[..MaximumErrorLength];

        var affected = await _db.Set<FinanceOutboxMessage>()
            .Where(x => x.Id == messageId
                && x.ProcessedOn == null
                && x.DeadLetteredOn == null
                && x.LeaseOwner == owner
                && x.LeaseToken == leaseToken
                && x.LeaseUntil > now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.LastError, normalizedError)
                .SetProperty(x => x.LeaseOwner, (string?)null)
                .SetProperty(x => x.LeaseUntil, (DateTime?)null)
                .SetProperty(x => x.LeaseToken, (Guid?)null)
                .SetProperty(x => x.DeadLetteredOn,
                    x => x.AttemptCount >= maxAttempts ? now : x.DeadLetteredOn)
                .SetProperty(x => x.AvailableOn,
                    x => x.AttemptCount >= maxAttempts ? x.AvailableOn : availableOn), cancellationToken);

        EnsureLeaseWasOwned(affected);
    }

    private static FinanceOutboxEnvelope ToEnvelope(FinanceOutboxMessage message)
        => new(
            message.Id,
            message.BusinessUnitId,
            message.EventId,
            message.AggregateType,
            message.AggregateId,
            message.AggregateVersion,
            message.EventType,
            message.Payload,
            message.SchemaVersion,
            message.OccurredOn,
            message.AttemptCount,
            message.LeaseToken!.Value,
            message.LeaseUntil!.Value);

    private static string NormalizeWorkerId(string workerId)
    {
        if (string.IsNullOrWhiteSpace(workerId))
            throw new ArgumentException("Worker ID is required.", nameof(workerId));

        var normalized = workerId.Trim();
        if (normalized.Length > 200)
            throw new ArgumentException("Worker ID cannot exceed 200 characters.", nameof(workerId));
        return normalized;
    }

    private static void ValidateMessageAndToken(long messageId, Guid leaseToken)
    {
        if (messageId <= 0)
            throw new ArgumentOutOfRangeException(nameof(messageId));
        if (leaseToken == Guid.Empty)
            throw new ArgumentException("A non-empty lease token is required.", nameof(leaseToken));
    }

    private static void ValidateDuration(TimeSpan value, TimeSpan minimum, TimeSpan maximum, string parameterName)
    {
        if (value < minimum || value > maximum)
            throw new ArgumentOutOfRangeException(parameterName, $"Duration must be between {minimum} and {maximum}.");
    }

    private static void EnsureLeaseWasOwned(int affected)
    {
        if (affected != 1)
            throw new FinanceOutboxLeaseConflictException(
                "The finance outbox lease is missing, expired, fenced by a newer claim, or owned by another worker.");
    }

    private async Task<DateTime> DatabaseUtcNowAsync(CancellationToken cancellationToken)
    {
        if (!_db.Database.IsNpgsql())
            return DateTime.UtcNow;

        return await _db.Database
            .SqlQueryRaw<DateTime>("SELECT (CURRENT_TIMESTAMP AT TIME ZONE 'UTC') AS \"Value\"")
            .SingleAsync(cancellationToken);
    }
}
