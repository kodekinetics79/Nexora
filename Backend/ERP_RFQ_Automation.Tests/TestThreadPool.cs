using System.Runtime.CompilerServices;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Pre-provisions thread-pool threads for the whole test assembly.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TestWaits"/> documents why a short liveness bound is a load test in disguise, and
/// raised its bound from 10 seconds to 60 to stop reporting runner contention as a product
/// defect. On 2026-08-24 the same test — <c>HungHeartbeatCancelsWorkByKnownLeaseDeadline</c> —
/// timed out at SIXTY seconds on the GitHub runner while the identical commit passed 5561/5561
/// on the other trigger of the same push. Raising the bound again would be the third guess at a
/// number, and the number was never the defect.
/// </para>
/// <para>
/// This removes the mechanism instead. The starvation is not that the pool is too small, it is
/// that it GROWS too slowly: above the minimum, .NET injects roughly one thread per second, so a
/// worker loop dispatched with <c>Task.Run</c> and each <c>Task.Delay</c> continuation inside its
/// heartbeat queues behind that drip. A sub-second sequence of hops then takes tens of seconds of
/// wall clock — the work is correct throughout, only the scheduling is late. Setting the MINIMUM
/// high means those threads exist before the first Testcontainers fixture parks one waiting on a
/// PostgreSQL container, so the drip never governs.
/// </para>
/// <para>
/// This raises a floor, never a cap: <c>SetMinThreads</c> does not limit the pool, allocate
/// anything eagerly, or change any product behaviour. It applies only to this test assembly, and
/// only affects how fast threads are made available to work that already exists. It is also the
/// standard remedy for pool starvation in test hosts, not a local invention.
/// </para>
/// <para>
/// Deliberately a floor, not an override: if the host already offers more (a many-core developer
/// machine), that larger value is kept.
/// </para>
/// </remarks>
internal static class TestThreadPool
{
    // 2-core CI runners default to a minimum of 2 worker threads. The suite routinely has more
    // than that parked at once: Testcontainers fixtures waiting on containers, background
    // workers under test, and their heartbeat continuations.
    private const int MinimumWorkerThreads = 64;
    private const int MinimumCompletionPortThreads = 64;

    [ModuleInitializer]
    internal static void EnsureThreadsAreAvailableBeforeTheFirstTestRuns()
    {
        ThreadPool.GetMinThreads(out var workerThreads, out var completionPortThreads);
        ThreadPool.SetMinThreads(
            Math.Max(workerThreads, MinimumWorkerThreads),
            Math.Max(completionPortThreads, MinimumCompletionPortThreads));
    }
}
