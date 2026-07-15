using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ERP_RFQ_Automation.HealthChecks
{
    /// <summary>
    /// Readiness probe: reports Unhealthy when the (remote) SQL Server is
    /// unreachable, so an operator can detect a DB outage before/ during a pilot
    /// instead of discovering it through failing requests. (DATA-05)
    /// </summary>
    public class DatabaseHealthCheck : IHealthCheck
    {
        private readonly ErpRfqAutomationContext _context;

        public DatabaseHealthCheck(ErpRfqAutomationContext context) => _context = context;

        public async Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var canConnect = await _context.Database.CanConnectAsync(cancellationToken);
                return canConnect
                    ? HealthCheckResult.Healthy("Database reachable")
                    : HealthCheckResult.Unhealthy("Database not reachable");
            }
            catch (Exception ex)
            {
                return HealthCheckResult.Unhealthy("Database check failed", ex);
            }
        }
    }
}
