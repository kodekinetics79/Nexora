namespace ERP_RFQ_Automation.Extraction;

/// <summary>
/// Keeps an extraction job's lease alive while its results are persisted (XS-09, pilot audit
/// 2026-09-28).
///
/// <para>The independent heartbeat is stopped before the persist transaction, and the lease was
/// renewed exactly once, sized by line count. Under CPU starvation a 23-line SEC document still
/// took longer than that lease to persist: the fenced completion failed, the transaction rolled
/// back and the whole job — reading, header AI call and all — ran again, three times.</para>
///
/// <para>The persist now renews the lease at its own checkpoints (after each reconciled lead and
/// each evidence-ledger batch, and just before completion) whenever a third of the lease has
/// passed since the last renewal. The renewal runs on the persist transaction's own connection,
/// which already holds the queue row lock, so it cannot race a reclaim; it only moves the expiry
/// the fenced completion checks. A renewal that finds the lease already gone stops the persist at
/// once instead of writing everything and failing at the end.</para>
/// </summary>
public sealed class PersistLeaseKeepAlive
{
    private readonly Func<CancellationToken, Task<bool>> _renew;
    private readonly TimeSpan _renewEvery;
    private readonly TimeProvider _time;
    private readonly long _jobId;
    private DateTimeOffset _lastRenewedAt;

    public PersistLeaseKeepAlive(long jobId, TimeSpan lease, Func<CancellationToken, Task<bool>> renew,
        TimeProvider? time = null)
    {
        _jobId = jobId;
        _renew = renew;
        _time = time ?? TimeProvider.System;
        _renewEvery = TimeSpan.FromTicks(Math.Max(TimeSpan.FromSeconds(1).Ticks, lease.Ticks / 3));
        _lastRenewedAt = _time.GetUtcNow();
    }

    /// <summary>Renewals made after the initial one; for tests and logs.</summary>
    public int Renewals { get; private set; }

    public async Task RenewIfDueAsync(CancellationToken ct)
    {
        var startedAt = _time.GetUtcNow();
        if (startedAt - _lastRenewedAt < _renewEvery) return;
        if (!await _renew(ct))
            throw new InvalidOperationException(
                $"Fenced completion failed for extraction job {_jobId}: its lease expired while the results were being saved.");
        _lastRenewedAt = startedAt;
        Renewals++;
    }
}
