using ERP_RFQ_Automation.Extraction;

namespace ERP_RFQ_Automation.Tests;

public sealed class ExtractionHeavyWorkAdmissionTests
{
    [Fact]
    public void Recovery_and_production_reader_accept_the_same_process_gate_contract()
    {
        Assert.Contains(typeof(ExtractionDeadLetterService).GetConstructors().Single().GetParameters(),
            parameter => parameter.ParameterType == typeof(IExtractionHeavyWorkAdmission));
        Assert.Contains(typeof(ProductionDocumentReader).GetConstructors(), constructor =>
            constructor.GetParameters().Any(parameter =>
                parameter.ParameterType == typeof(IExtractionHeavyWorkAdmission)));
    }

    [Fact]
    public async Task Low_memory_gate_allows_only_one_heavy_operation()
    {
        using var gate = new ExtractionHeavyWorkAdmission(512L * 1024 * 1024);
        var active = 0;
        var maximum = 0;

        var tasks = Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var lease = await gate.EnterAsync(CancellationToken.None);
            var current = Interlocked.Increment(ref active);
            maximum = Math.Max(maximum, current);
            await Task.Delay(10);
            Interlocked.Decrement(ref active);
        });

        await Task.WhenAll(tasks);
        Assert.Equal(1, maximum);
    }

    [Fact]
    public async Task Cancelled_waiter_does_not_consume_or_leak_a_permit()
    {
        using var gate = new ExtractionHeavyWorkAdmission(0L); // unknown production limit fails safe
        await using var held = await gate.EnterAsync(CancellationToken.None);
        using var cancelled = new CancellationTokenSource();
        var waiting = gate.EnterAsync(cancelled.Token).AsTask();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        await held.DisposeAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await using var acquired = await gate.EnterAsync(timeout.Token);
    }
}
