using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ERP_RFQ_Automation.Services.Interfaces;
namespace ERP_RFQ_Automation.Services
{
    public class EmailBackgroundService : BackgroundService
    {
        private readonly IServiceProvider _services;
        private readonly ILogger<EmailBackgroundService> _logger;
        public EmailBackgroundService(IServiceProvider services, ILogger<EmailBackgroundService> logger)
        {
            _services = services;
            _logger = logger;
        }
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Email Background Service started.");
            while (!stoppingToken.IsCancellationRequested)
            {
                using var scope = _services.CreateScope();
                var emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();
                var dbContext = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
                try
                {
                    _logger.LogInformation("Starting email fetch...");
                    await emailService.FetchAndSaveLeadsAsync();
                    _logger.LogInformation("Email fetch completed successfully.");
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Critical error in email background service");
                }

                var folderService = scope.ServiceProvider.GetRequiredService<FolderService>();
                foreach (var businessUnitId in folderService.DiscoverTenantFolderIds())
                {
                    try
                    {
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
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Folder sweep failed for BU {BusinessUnitId}; continuing with other tenants.", businessUnitId);
                    }
                }

                // DATA-01: this DB read must NOT be able to fault ExecuteAsync — a single
                // transient DB error here would otherwise (with the default StopHost
                // behavior) take down the whole API host. Guard it and fall back to a
                // safe default polling interval so the loop always survives.
                int minInterval = 300; // seconds
                try
                {
                    var intervals = await dbContext.EmailConfigurations
                        .Where(e => e.IsActive)
                        .Select(e => e.PollingInterval)
                        .ToListAsync(stoppingToken);
                    if (intervals.Any()) minInterval = intervals.Min();
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to read polling interval; using default {DefaultInterval}s", minInterval);
                }
                await Task.Delay(TimeSpan.FromSeconds(minInterval), stoppingToken);
            }
            _logger.LogInformation("Email Background Service stopped.");
        }
    }
}
