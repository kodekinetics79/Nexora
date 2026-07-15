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

                    // _logger.LogInformation("Starting shared folder fetch...");
                    // var folderService = scope.ServiceProvider.GetRequiredService<FolderService>();
                    // await folderService.ProcessSharedFolderAsync();
                    // _logger.LogInformation("Shared folder fetch completed successfully.");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Critical error in email background service");
                }
                var intervals = await dbContext.EmailConfigurations
                    .Where(e => e.IsActive)
                    .Select(e => e.PollingInterval)
                    .ToListAsync(stoppingToken);
                int minInterval = intervals.Any() ? intervals.Min() : 300; // Polling interval is in seconds
                await Task.Delay(TimeSpan.FromSeconds(minInterval), stoppingToken);
            }
            _logger.LogInformation("Email Background Service stopped.");
        }
    }
}