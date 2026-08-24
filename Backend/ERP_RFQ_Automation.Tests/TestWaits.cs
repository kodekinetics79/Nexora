namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Timeouts for tests that must wait on a background worker reaching an observable point.
/// </summary>
/// <remarks>
/// <para>
/// These waits are LIVENESS GUARDS, not timing assertions. They exist so a test that would
/// otherwise hang forever fails with a stack trace instead of stalling the run. Nothing about
/// the product is being asserted by the number, so the number should be large.
/// </para>
/// <para>
/// It was not. <c>ExtractionWorkerLeaseTests.HungHeartbeatCancelsWorkByKnownLeaseDeadline</c>
/// waited 10 seconds for a worker that reaches the same point in about one second locally, and
/// on 2026-08-11 it timed out on the GitHub runner and turned <c>main</c> red. The tree was not
/// broken: the identical commit had passed twice minutes earlier, and the test passed six times
/// out of six locally at ~1s. What changed was contention.
/// </para>
/// <para>
/// The mechanism is thread-pool starvation, and this suite is unusually good at causing it. The
/// runner has 2 cores, xUnit runs collections in parallel, and the suite contains Testcontainers
/// fixtures that hold pool threads while a PostgreSQL container starts. A worker loop dispatched
/// with <c>Task.Run</c>, and every <c>Task.Delay</c> continuation inside its heartbeat, needs a
/// free pool thread to make progress. Under starvation the pool grows by roughly one thread per
/// second, so a sub-second sequence of hops can take tens of seconds of wall clock. The work is
/// correct throughout; only the scheduling is late.
/// </para>
/// <para>
/// So a short liveness timeout does not test promptness — it tests how busy the runner was, and
/// reports the answer as a product defect. That failure is worse than useless: it is
/// indistinguishable at a glance from a real hang, it blocks the merge queue, and here it also
/// blocked deployment, because render.yaml sets <c>autoDeployTrigger: checksPass</c> and Render
/// correctly refused to ship a red commit.
/// </para>
/// <para>
/// A real hang is a deadlock or a lost wake-up and never completes, so it is caught just as well
/// by a 60-second bound as by a 2-second one; the only thing a short bound buys is a faster
/// report of a failure that is not real. Where promptness genuinely IS the contract — the lease
/// deadline in <c>ExtractionWorkerLeaseTests</c> — assert it explicitly on a measured elapsed
/// time, anchored to the event that starts the clock rather than to the start of the test, and
/// leave this timeout out of it.
/// </para>
/// </remarks>
internal static class TestWaits
{
    /// <summary>
    /// How long to wait for a background worker to reach an observable point before declaring it
    /// hung. Deliberately generous; see the remarks on <see cref="TestWaits"/>.
    /// </summary>
    // 120, not 60. 60 was itself a raise from 10, and it ALSO timed out on the runner
    // (2026-08-24) while the identical commit passed 5561/5561 on the other trigger. The real
    // remedy is <see cref="TestThreadPool"/>, which removes the starvation rather than waiting
    // it out; this bound is the backstop behind it. Per the remarks above, a real hang never
    // completes, so a larger bound costs a genuine failure nothing and costs a contended runner
    // a false red.
    public static readonly TimeSpan Liveness = TimeSpan.FromSeconds(120);
}
