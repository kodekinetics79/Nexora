using ERP_RFQ_Automation.Infrastructure.Storage;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ERP_RFQ_Automation.HealthChecks;

/// <summary>
/// Free space on the storage volume.
///
/// <para>
/// This exists because running out of disk is not graceful here: <see cref="LocalFileStorage"/>
/// write-probes its root in its constructor, so a full volume means the service refuses to
/// start. Nothing anywhere else measures free space, which means the first signal of a full
/// disk was a process that would not boot. This turns that into a warning with room to act.
/// </para>
///
/// <para>
/// Registered with the <c>ready</c> tag ONLY, never <c>live</c>. A full disk must take the
/// instance out of rotation; it must not make the platform kill and reschedule the process,
/// because the replacement lands on the same full disk and the result is a restart loop that
/// destroys the very window an operator needs to free space.
/// </para>
///
/// <para>
/// Percentage AND absolute floors are both applied. On a small volume a percentage alone is
/// too coarse (5% of 5 GB is 250 MB, which one large upload consumes); on a large one an
/// absolute alone never fires.
/// </para>
/// </summary>
public sealed class StorageCapacityHealthCheck : IHealthCheck
{
    internal const double DegradedFraction = 0.25;
    internal const long DegradedBytes = 750L * 1024 * 1024;
    internal const double UnhealthyFraction = 0.08;
    internal const long UnhealthyBytes = 250L * 1024 * 1024;

    private readonly IFileStorage _files;

    public StorageCapacityHealthCheck(IFileStorage files) => _files = files;

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        DriveInfo drive;
        long free;
        long total;
        try
        {
            drive = new DriveInfo(_files.RootPath);
            free = drive.AvailableFreeSpace;
            total = drive.TotalSize;
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or ArgumentException)
        {
            // Not knowing the free space is not the same as being out of it. Degraded, so a
            // container filesystem that hides drive statistics cannot take the service down.
            return Task.FromResult(HealthCheckResult.Degraded(
                "Storage capacity could not be measured for the configured storage root.", exception));
        }

        var result = Evaluate(free, total);
        var data = new Dictionary<string, object>
        {
            ["rootPath"] = _files.RootPath,
            ["availableBytes"] = free,
            ["totalBytes"] = total,
            ["availablePercent"] = total > 0 ? Math.Round(100.0 * free / total, 2) : 0
        };

        return Task.FromResult(result switch
        {
            HealthStatus.Unhealthy => HealthCheckResult.Unhealthy(Describe(free, total, result), data: data),
            HealthStatus.Degraded => HealthCheckResult.Degraded(Describe(free, total, result), data: data),
            _ => HealthCheckResult.Healthy(Describe(free, total, result), data)
        });
    }

    internal static HealthStatus Evaluate(long availableBytes, long totalBytes)
    {
        if (totalBytes <= 0)
            return HealthStatus.Degraded;
        var fraction = (double)availableBytes / totalBytes;
        if (availableBytes <= UnhealthyBytes || fraction <= UnhealthyFraction)
            return HealthStatus.Unhealthy;
        if (availableBytes <= DegradedBytes || fraction <= DegradedFraction)
            return HealthStatus.Degraded;
        return HealthStatus.Healthy;
    }

    private static string Describe(long free, long total, HealthStatus status)
    {
        var percent = total > 0 ? 100.0 * free / total : 0;
        var headline = $"{free / (1024.0 * 1024):N0} MB free ({percent:N1}%) on the storage volume.";
        return status switch
        {
            HealthStatus.Unhealthy => headline
                + " Uploads will start failing and the service will refuse to restart on this volume."
                + " Resize the disk or run an evidence retention purge now.",
            HealthStatus.Degraded => headline
                + " Free space is low. Resize the disk or reclaim space with an evidence retention purge.",
            _ => headline
        };
    }
}
