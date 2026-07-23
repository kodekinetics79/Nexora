using System.Collections.Concurrent;

namespace ERP_RFQ_Automation.Security;

public sealed class TenantSmtpConcurrencyGate
{
    private const int MaximumConcurrentSendsPerTenant = 2;
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _tenantGates = new();

    public async ValueTask<IAsyncDisposable?> TryAcquireAsync(long businessUnitId, CancellationToken cancellationToken)
    {
        var gate = _tenantGates.GetOrAdd(
            businessUnitId, _ => new SemaphoreSlim(MaximumConcurrentSendsPerTenant, MaximumConcurrentSendsPerTenant));
        if (!await gate.WaitAsync(0, cancellationToken)) return null;
        return new Lease(gate);
    }

    private sealed class Lease(SemaphoreSlim gate) : IAsyncDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _gate, null)?.Release();
            return ValueTask.CompletedTask;
        }
    }
}
