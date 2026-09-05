using ERP_RFQ_Automation.HealthChecks;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.Billing;

public sealed class SubscriptionDunningOptions
{
    public const string SectionName = "PlatformBilling:Dunning";
    public bool Enabled { get; set; }
    public int PollMinutes { get; set; } = 60;
}

/// <summary>Creates one durable, idempotent dunning occurrence per overdue invoice per UTC day.</summary>
public sealed class SubscriptionDunningWorker : BackgroundService
{
    private readonly IServiceScopeFactory scopes;
    private readonly IOptions<SubscriptionDunningOptions> options;
    private readonly ILogger<SubscriptionDunningWorker> log;
    private readonly IBackgroundWorkerHeartbeats? _heartbeats;

    public SubscriptionDunningWorker(
        IServiceScopeFactory scopes, IOptions<SubscriptionDunningOptions> options,
        ILogger<SubscriptionDunningWorker> log, IBackgroundWorkerHeartbeats? heartbeats = null)
    {
        this.scopes = scopes;
        this.options = options;
        this.log = log;
        _heartbeats = heartbeats;
        // The loop turns whether or not dunning is enabled (a disabled cycle is a no-op plus
        // the delay), so liveness is meaningful either way and is registered unconditionally.
        _heartbeats?.Register(BackgroundWorkerNames.SubscriptionDunning, Period(options.Value));
    }

    private static TimeSpan Period(SubscriptionDunningOptions options)
        => TimeSpan.FromMinutes(Math.Clamp(options.PollMinutes, 1, 1440));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _heartbeats?.Beat(BackgroundWorkerNames.SubscriptionDunning, Period(options.Value));
        while (!stoppingToken.IsCancellationRequested)
        {
            if (options.Value.Enabled)
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
                    var service = scope.ServiceProvider.GetRequiredService<SubscriptionRevenueControlService>();
                    var today = DateTime.UtcNow.Date;
                    var candidates = await db.Set<SubscriptionInvoice>().AsNoTracking()
                        .Where(x => x.DueAtUtc < today && x.Status != SubscriptionInvoiceStatus.Draft
                            && x.Status != SubscriptionInvoiceStatus.Void
                            && x.TotalAmount - x.CreditedAmount
                               - (x.PaidAmount - x.RefundedAmount - x.ReversedPaymentAmount)
                               - x.WrittenOffAmount > 0)
                        .OrderBy(x => x.DueAtUtc).Take(500).Select(x => new { x.Id, x.Currency }).ToListAsync(stoppingToken);
                    foreach (var invoice in candidates)
                    {
                        var key = $"dunning:{invoice.Id}:{today:yyyyMMdd}";
                        await service.ProposeAsync(invoice.Id, new(SubscriptionRevenueActionKind.Dunning,
                            0, invoice.Currency, "Automated overdue receivable dunning occurrence",
                            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                                System.Text.Encoding.UTF8.GetBytes(key))).ToLowerInvariant(), null, key), 0, stoppingToken);
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    log.LogError(exception, "Subscription dunning cycle failed.");
                }
            }
            _heartbeats?.Beat(BackgroundWorkerNames.SubscriptionDunning, Period(options.Value));
            await Task.Delay(Period(options.Value), stoppingToken);
        }
    }
}
