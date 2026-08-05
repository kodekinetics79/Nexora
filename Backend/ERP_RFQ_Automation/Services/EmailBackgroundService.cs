using ERP_RFQ_Automation.HealthChecks;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ERP_RFQ_Automation.Services.Interfaces;
namespace ERP_RFQ_Automation.Services
{
    /// <summary>
    /// Polls the configured IMAP mailboxes and sweeps the watched folders.
    ///
    /// SINGLE-INSTANCE BY CONSTRUCTION. There is no per-message lease anywhere in the
    /// IMAP path, so N instances polling the same mailbox concurrently fetch, extract
    /// and ingest the same messages. The cycle therefore runs only inside a PostgreSQL
    /// advisory lock (<see cref="PostgresAdvisoryLease"/>); instances that do not win
    /// the lock stand by and retry next tick, and the lock is released automatically if
    /// the holder dies. This makes the poller safe to scale horizontally: extra
    /// instances serve HTTP and take over polling the moment the leader disappears.
    /// </summary>
    public class EmailBackgroundService : BackgroundService
    {
        internal const string PollLockName = "nexora:email-poller";
        private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(300);

        private readonly IServiceProvider _services;
        private readonly IBackgroundWorkerHeartbeats? _heartbeats;
        private readonly ILogger<EmailBackgroundService> _logger;

        public EmailBackgroundService(
            IServiceProvider services,
            ILogger<EmailBackgroundService> logger,
            IBackgroundWorkerHeartbeats? heartbeats = null)
        {
            _services = services;
            _logger = logger;
            _heartbeats = heartbeats;
            _heartbeats?.Register(BackgroundWorkerNames.EmailPoller, DefaultPollInterval);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Email Background Service started.");
            _heartbeats?.Beat(BackgroundWorkerNames.EmailPoller, DefaultPollInterval);

            while (!stoppingToken.IsCancellationRequested)
            {
                var interval = DefaultPollInterval;
                using (var scope = _services.CreateScope())
                {
                    var dbContext = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();

                    PostgresAdvisoryLease? lease = null;
                    try
                    {
                        lease = await PostgresAdvisoryLease.TryAcquireAsync(dbContext, PollLockName, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        // Never poll without the lock: a transient failure to acquire it must
                        // degrade to "skip this cycle", not to "poll concurrently".
                        _logger.LogError(ex, "Could not evaluate the email-poller lock; skipping this cycle.");
                    }

                    if (lease is null)
                    {
                        _logger.LogDebug(
                            "Another instance holds the email-poller lock; standing by for {Interval}s.",
                            interval.TotalSeconds);
                    }
                    else
                    {
                        await using (lease)
                        {
                            try
                            {
                                await RunPollCycleAsync(scope.ServiceProvider, stoppingToken);
                            }
                            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                            {
                                break;
                            }
                            catch (Exception ex)
                            {
                                // BackgroundServiceExceptionBehavior is Ignore, so an escaping
                                // exception would kill the poller for the lifetime of the process.
                                _logger.LogError(ex, "Email poll cycle failed; retrying next interval.");
                            }

                            interval = await ResolvePollIntervalAsync(dbContext, stoppingToken);
                        }
                    }
                }

                _heartbeats?.Beat(BackgroundWorkerNames.EmailPoller, interval);

                try { await Task.Delay(interval, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
            _logger.LogInformation("Email Background Service stopped.");
        }

        private async Task RunPollCycleAsync(IServiceProvider scopedServices, CancellationToken stoppingToken)
        {
            var emailService = scopedServices.GetRequiredService<IEmailService>();
            try
            {
                _logger.LogInformation("Starting email fetch...");
                await emailService.FetchAndSaveLeadsAsync();
                _logger.LogInformation("Email fetch completed successfully.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Critical error in email background service");
            }

            // Folder discovery is filesystem-only; the sweep itself writes tenant data and
            // therefore runs inside a pushed tenant scope so it executes as
            // nexora_tenant_app under RLS instead of the BYPASSRLS pipeline role.
            var tenantScope = scopedServices.GetRequiredService<ITenantScopeAccessor>();
            var discovery = scopedServices.GetRequiredService<FolderService>();
            foreach (var businessUnitId in discovery.DiscoverTenantFolderIds())
            {
                try
                {
                    using var tenant = tenantScope.Push(businessUnitId);
                    using var tenantServices = _services.CreateScope();
                    var folderService = tenantServices.ServiceProvider.GetRequiredService<FolderService>();
                    var report = await folderService.ProcessAllFoldersAsync(businessUnitId, stoppingToken);
                    if (report.Enqueued + report.Duplicates + report.Rejected + report.Failed > 0)
                    {
                        _logger.LogInformation(
                            "Folder sweep {BatchId} for BU {BusinessUnitId}: {Enqueued} enqueued, {Duplicates} duplicates, {Rejected} rejected, {Failed} retrying.",
                            report.BatchId, businessUnitId, report.Enqueued, report.Duplicates, report.Rejected, report.Failed);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Folder sweep failed for BU {BusinessUnitId}; continuing with other tenants.", businessUnitId);
                }
            }
        }

        // DATA-01: this DB read must NOT be able to fault ExecuteAsync — a single
        // transient DB error here would otherwise (with the default StopHost
        // behavior) take down the whole API host. Guard it and fall back to a
        // safe default polling interval so the loop always survives.
        private async Task<TimeSpan> ResolvePollIntervalAsync(
            ErpRfqAutomationContext dbContext, CancellationToken stoppingToken)
        {
            try
            {
                var intervals = await dbContext.EmailConfigurations
                    .Where(e => e.IsActive)
                    .Select(e => e.PollingInterval)
                    .ToListAsync(stoppingToken);
                if (intervals.Any())
                    return TimeSpan.FromSeconds(Math.Max(intervals.Min(), 1));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // fall through to the default
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to read polling interval; using default {DefaultInterval}s",
                    DefaultPollInterval.TotalSeconds);
            }

            return DefaultPollInterval;
        }
    }
}
