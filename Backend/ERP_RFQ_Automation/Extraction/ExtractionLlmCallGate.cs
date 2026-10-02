namespace ERP_RFQ_Automation.Extraction;

/// <summary>
/// Process-wide admission for actual extraction-model calls. The old worker gate surrounded an
/// entire document, so a document with several chunks still called the provider strictly in
/// series and the configured LLM concurrency was never available to shorten that document's
/// latency. Keeping the permit at the network-call boundary makes the setting truthful while
/// still bounding provider pressure across every worker.
/// </summary>
public interface IExtractionLlmCallGate
{
    Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct);
}

public sealed class ExtractionLlmCallGate : IExtractionLlmCallGate, IDisposable
{
    private readonly SemaphoreSlim _permits;

    public ExtractionLlmCallGate(int maximumConcurrency)
    {
        if (maximumConcurrency <= 0) throw new ArgumentOutOfRangeException(nameof(maximumConcurrency));
        _permits = new SemaphoreSlim(maximumConcurrency, maximumConcurrency);
    }

    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await _permits.WaitAsync(ct);
        try
        {
            return await operation(ct);
        }
        finally
        {
            _permits.Release();
        }
    }

    public void Dispose() => _permits.Dispose();
}

internal sealed class UnrestrictedExtractionLlmCallGate : IExtractionLlmCallGate
{
    public static UnrestrictedExtractionLlmCallGate Instance { get; } = new();

    public Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return operation(ct);
    }
}
