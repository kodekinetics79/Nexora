namespace ERP_RFQ_Automation.Extraction;

public interface IExtractionHeavyWorkAdmission
{
    ValueTask<IAsyncDisposable> EnterAsync(CancellationToken ct);
}

/// <summary>
/// Process-wide admission for operations that materialize or decode evidence. It is shared by
/// HTTP recovery and workers, because worker concurrency alone cannot prevent those two paths
/// from overlapping inside a small container.
/// </summary>
public sealed class ExtractionHeavyWorkAdmission : IExtractionHeavyWorkAdmission, IDisposable
{
    private const long MiB = 1024L * 1024L;
    private readonly SemaphoreSlim _permits;

    public ExtractionHeavyWorkAdmission(long availableMemoryBytes)
        : this(availableMemoryBytes <= 0 || availableMemoryBytes <= 768 * MiB ? 1 : 2)
    {
    }

    internal ExtractionHeavyWorkAdmission(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _permits = new SemaphoreSlim(capacity, capacity);
    }

    public async ValueTask<IAsyncDisposable> EnterAsync(CancellationToken ct)
    {
        await _permits.WaitAsync(ct);
        return new Releaser(_permits);
    }

    public void Dispose() => _permits.Dispose();

    private sealed class Releaser(SemaphoreSlim permits) : IAsyncDisposable
    {
        private SemaphoreSlim? _permits = permits;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _permits, null)?.Release();
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class UnrestrictedExtractionHeavyWorkAdmission : IExtractionHeavyWorkAdmission
{
    public static UnrestrictedExtractionHeavyWorkAdmission Instance { get; } = new();
    public ValueTask<IAsyncDisposable> EnterAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IAsyncDisposable>(Noop.Instance);
    }

    private sealed class Noop : IAsyncDisposable
    {
        public static Noop Instance { get; } = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
