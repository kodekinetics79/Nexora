using ERP_RFQ_Automation.Extraction;

namespace ERP_RFQ_Automation.Tests;

public sealed class ExtractionWorkerCapacityPolicyTests
{
    private const long MiB = 1024L * 1024L;

    [Fact]
    public void Five_hundred_twelve_megabyte_plan_is_clamped_to_one_job_at_a_time()
    {
        var result = ExtractionWorkerCapacityPolicy.Apply(Requested(), 512 * MiB);

        Assert.Equal(1, result.WorkerCount);
        Assert.Equal(1, result.MaxConcurrentLlmCalls);
        Assert.Equal(1, result.PerTenantConcurrencyCap);
        Assert.True(result.CapacityWasClamped);
        Assert.Equal(512 * MiB, result.DetectedMemoryLimitBytes);
        Assert.Equal(4, result.RequestedWorkerCount);
        Assert.Equal(8, result.RequestedMaxConcurrentLlmCalls);
        Assert.Equal(4, result.RequestedPerTenantConcurrencyCap);
    }

    [Fact]
    public void One_gigabyte_plan_is_clamped_to_two_jobs_at_a_time()
    {
        var result = ExtractionWorkerCapacityPolicy.Apply(Requested(), 1024 * MiB);

        Assert.Equal(2, result.WorkerCount);
        Assert.Equal(2, result.MaxConcurrentLlmCalls);
        Assert.Equal(2, result.PerTenantConcurrencyCap);
        Assert.True(result.CapacityWasClamped);
    }

    [Fact]
    public void Larger_plan_keeps_explicit_operator_configuration()
    {
        var result = ExtractionWorkerCapacityPolicy.Apply(Requested(), 4096 * MiB);

        Assert.Equal(4, result.WorkerCount);
        Assert.Equal(8, result.MaxConcurrentLlmCalls);
        Assert.Equal(4, result.PerTenantConcurrencyCap);
        Assert.False(result.CapacityWasClamped);
    }

    [Fact]
    public void Unknown_memory_ceiling_keeps_explicit_configuration()
    {
        var result = ExtractionWorkerCapacityPolicy.Apply(Requested(), 0);

        Assert.Equal(4, result.WorkerCount);
        Assert.Equal(8, result.MaxConcurrentLlmCalls);
        Assert.Equal(4, result.PerTenantConcurrencyCap);
        Assert.False(result.CapacityWasClamped);
    }

    [Fact]
    public void Policy_never_increases_operator_limits_and_preserves_timing()
    {
        var requested = new ExtractionWorkerOptions
        {
            WorkerCount = 1,
            MaxConcurrentLlmCalls = 1,
            PerTenantConcurrencyCap = 1,
            LeaseDuration = TimeSpan.FromMinutes(9),
            IdlePollDelay = TimeSpan.FromSeconds(7)
        };

        var result = ExtractionWorkerCapacityPolicy.Apply(requested, 1024 * MiB);

        Assert.Equal(1, result.WorkerCount);
        Assert.Equal(1, result.MaxConcurrentLlmCalls);
        Assert.Equal(1, result.PerTenantConcurrencyCap);
        Assert.Equal(requested.LeaseDuration, result.LeaseDuration);
        Assert.Equal(requested.IdlePollDelay, result.IdlePollDelay);
        Assert.False(result.CapacityWasClamped);
    }

    private static ExtractionWorkerOptions Requested() => new()
    {
        WorkerCount = 4,
        MaxConcurrentLlmCalls = 8,
        PerTenantConcurrencyCap = 4,
        LeaseDuration = TimeSpan.FromMinutes(5),
        IdlePollDelay = TimeSpan.FromSeconds(2)
    };
}
