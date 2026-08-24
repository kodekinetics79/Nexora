using System.Runtime.CompilerServices;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Raises the thread-pool floor before any test runs.
///
/// <para><b>The failure this removes.</b> <c>ExtractionWorkerLeaseTests</c> and the other
/// background-worker tests wait for a worker loop to reach an observable point. That loop is
/// dispatched with <c>Task.Run</c>, and every <c>Task.Delay</c> continuation inside it needs a
/// free pool thread to make progress. When no thread is free, none of it is wrong — it is
/// merely late, and a test that waits on wall clock reports lateness as a product defect.</para>
///
/// <para><b>Why it happens here specifically.</b> The GitHub runner has 2 cores, so the pool's
/// minimum is 2. Past that minimum the pool injects threads at a throttled rate of roughly one
/// per second. This suite is unusually good at exhausting it: xUnit runs collections in
/// parallel, and the Testcontainers fixtures hold pool threads while PostgreSQL containers
/// start. A sub-second sequence of hops then takes tens of seconds of wall clock.</para>
///
/// <para><b>Why not simply wait longer.</b> That was tried. <see cref="TestWaits.Liveness"/>
/// was raised from 10 seconds to 60 on 2026-08-11 after this exact test turned <c>main</c> red;
/// on 2026-08-18 it exceeded 60 seconds and did it again, on a commit whose identical
/// <c>pull_request</c> run was green. Each raise buys time without touching the cause, and the
/// cost is real: <c>render.yaml</c> sets <c>autoDeployTrigger: checksPass</c>, so a flake of
/// this kind blocks deployment, and a red tree that is not a real failure teaches everyone
/// reading it to merge past red.</para>
///
/// <para><b>What this changes.</b> Below the floor the pool creates threads on demand with no
/// throttle, so the starvation window closes rather than being waited out. This affects
/// scheduling only — no test's assertions, timings, or product behaviour change, and the
/// promptness contract in <see cref="ExtractionWorkerLeaseTests"/> is still asserted explicitly
/// against the lease deadline it is named for.</para>
/// </summary>
internal static class TestThreadPool
{
    /// <summary>
    /// Enough headroom for every parallel collection to hold a thread through a container start
    /// while background workers still make progress. High enough to close the starvation window
    /// on a 2-core runner; far below anything that would exhaust runner memory.
    /// </summary>
    private const int MinimumThreads = 64;

    [ModuleInitializer]
    internal static void Raise()
    {
        ThreadPool.GetMinThreads(out var workers, out var completionPorts);

        // Never LOWER a floor the host has already set higher — a developer machine with more
        // cores, or a future runner, should keep its own larger minimum.
        ThreadPool.SetMinThreads(
            Math.Max(workers, MinimumThreads),
            Math.Max(completionPorts, MinimumThreads));
    }
}
