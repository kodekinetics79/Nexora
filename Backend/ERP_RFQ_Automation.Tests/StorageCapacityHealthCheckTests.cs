using ERP_RFQ_Automation.HealthChecks;
using ERP_RFQ_Automation.Infrastructure.Storage;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Running out of disk is not graceful in this service: <see cref="LocalFileStorage"/>
/// write-probes its root in its constructor, so a full volume means the process refuses to
/// start. Nothing measured free space before, which made "the disk is full" a discovery
/// made by a boot failure. These tests pin the thresholds and the phrasing of the failure.
/// </summary>
public sealed class StorageCapacityHealthCheckTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "nexora-capacity-" + Guid.NewGuid().ToString("N"));

    private const long Gigabyte = 1024L * 1024 * 1024;

    [Theory]
    // Comfortable headroom.
    [InlineData(4 * Gigabyte, 5 * Gigabyte, HealthStatus.Healthy)]
    // Percentage floor: 20% of a 5 GB disk is a real warning even though 1 GB sounds fine.
    [InlineData(Gigabyte, 5 * Gigabyte, HealthStatus.Degraded)]
    // Absolute floor: 30% of a 2 GB disk is only 600 MB, which one batch of uploads eats.
    [InlineData(600L * 1024 * 1024, 2 * Gigabyte, HealthStatus.Degraded)]
    // Both floors fire well before the volume is actually full, leaving room to act.
    [InlineData(200L * 1024 * 1024, 5 * Gigabyte, HealthStatus.Unhealthy)]
    [InlineData(300L * 1024 * 1024, 5 * Gigabyte, HealthStatus.Unhealthy)]
    // On a large volume the absolute floor alone would never fire, so the percentage carries it.
    [InlineData(40L * Gigabyte, 1000L * Gigabyte, HealthStatus.Unhealthy)]
    // Unknown capacity is not the same as no capacity.
    [InlineData(0, 0, HealthStatus.Degraded)]
    public void Thresholds_apply_a_percentage_and_an_absolute_floor(
        long availableBytes, long totalBytes, HealthStatus expected)
    {
        Assert.Equal(expected, StorageCapacityHealthCheck.Evaluate(availableBytes, totalBytes));
    }

    [Fact]
    public async Task Check_reports_the_measured_volume_for_the_configured_storage_root()
    {
        Directory.CreateDirectory(_root);
        var check = new StorageCapacityHealthCheck(new LocalFileStorage(_root, _root));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.NotEqual(HealthStatus.Unhealthy, result.Status);
        Assert.True(result.Data.ContainsKey("availableBytes"));
        Assert.Equal(Path.GetFullPath(_root), result.Data["rootPath"]);
    }

    [Fact]
    public void A_full_volume_at_startup_names_itself_instead_of_leaving_an_errno()
    {
        // Boot must still fail — a read-write service on a full disk is broken — but the
        // operator must not have to decode "No space left on device" out of a probe write.
        var exhausted = new StorageCapacityExhaustedException("Storage volume '/var/data' is full.");
        Assert.IsAssignableFrom<IOException>(exhausted);
        Assert.Contains("full", exhausted.Message, StringComparison.OrdinalIgnoreCase);

        var enospc = new IOException("No space left on device", 28);
        Assert.Equal(!OperatingSystem.IsWindows(), LocalFileStorage.IsOutOfSpace(enospc));
        Assert.False(LocalFileStorage.IsOutOfSpace(new IOException("Permission denied", 13)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
