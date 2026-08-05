using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Services.Interfaces;
using ERP_RFQ_Automation.HealthChecks;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.QuoteDelivery;

public interface IQuoteDeliverySender
{
    Task SendAsync(QuoteDeliveryEnvelope request, CancellationToken ct);
}

public sealed class QuoteDeliveryPreSendException(string errorCode, Exception innerException)
    : Exception(errorCode, innerException);

public sealed class QuoteDeliverySender(IQuoteService quotes, IEmailService email) : IQuoteDeliverySender
{
    public async Task SendAsync(QuoteDeliveryEnvelope request, CancellationToken ct)
    {
        byte[] pdf;
        try
        {
            pdf = await quotes.GenerateQuotePdfAsync(request.QuoteId, request.BusinessUnitId);
        }
        catch (Exception exception)
        {
            throw new QuoteDeliveryPreSendException(exception.GetType().Name, exception);
        }
        await email.SendEmailAsync(request.RecipientEmail, request.Subject, request.Body,
            new List<(string FileName, byte[] FileContent, string ContentType)>
            {
                (request.AttachmentFileName, pdf, "application/pdf")
            }, request.FromEmail, request.BusinessUnitId);
    }
}

/// <summary>
/// Drains the quote-delivery outbox.
///
/// Tenant isolation
/// ----------------
/// This used to claim in ONE unscoped batch: <c>QuoteDeliveryRequest</c>'s global query filter is
/// <c>CurrentTenantId == null || BusinessUnitId == CurrentTenantId</c>, and a background worker
/// has no ambient tenant, so the filter degraded to "do not filter" and a single claim took the
/// oldest ten rows across EVERY tenant, leasing and then processing them under the bypass role.
///
/// It now follows the SlaSweepWorker shape (Sla/SlaSweepWorker.cs:136-175): resolve the tenant
/// ids ONCE — the only query that runs unscoped, and it reads nothing but business unit ids —
/// then do all claiming and all sending inside a pushed tenant scope, with a fail-closed guard
/// that refuses to run a tenant's batch if the DbContext did not pick the scope up. One
/// misbehaving tenant is logged and skipped rather than stopping the others.
/// </summary>
public sealed class QuoteDeliveryDispatcher(
    IServiceScopeFactory scopes,
    ILogger<QuoteDeliveryDispatcher> logger,
    ITenantScopeAccessor tenantScope)
{
    private readonly string _workerId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    public async Task<int> DispatchOnceAsync(CancellationToken ct)
    {
        var businessUnits = await ResolvePendingBusinessUnitsAsync(ct);
        var dispatched = 0;

        foreach (var businessUnitId in businessUnits)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                dispatched += await DispatchTenantAsync(businessUnitId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                logger.LogError(exception,
                    "Quote delivery dispatch failed for BU {BusinessUnitId}; continuing with next tenant.",
                    businessUnitId);
            }
        }

        return dispatched;
    }

    /// <summary>
    /// The ONLY query in this worker that runs without a tenant scope (and therefore under the
    /// BYPASSRLS pipeline role): the distinct business units with a deliverable row. It reads
    /// nothing but tenant ids, and the predicate mirrors the store's own claim eligibility so a
    /// tenant with nothing to send is never scoped up at all.
    /// </summary>
    private async Task<IReadOnlyList<long>> ResolvePendingBusinessUnitsAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
        var now = DateTime.UtcNow;
        return await db.Set<QuoteDeliveryRequest>().AsNoTracking().IgnoreQueryFilters()
            .Where(x => x.CompletedOn == null && x.DeadLetteredOn == null)
            .Where(x => x.AvailableOn <= now || x.LeaseUntil != null)
            .Select(x => x.BusinessUnitId)
            .Distinct()
            .OrderBy(id => id)
            .ToListAsync(ct);
    }

    private async Task<int> DispatchTenantAsync(long businessUnitId, CancellationToken ct)
    {
        using var tenant = tenantScope.Push(businessUnitId);

        IReadOnlyList<QuoteDeliveryEnvelope> requests;
        await using (var scope = scopes.CreateAsyncScope())
        {
            EnsureScoped(scope.ServiceProvider, businessUnitId);
            requests = await scope.ServiceProvider.GetRequiredService<IQuoteDeliveryStore>()
                .ClaimAsync(_workerId, 10, TimeSpan.FromMinutes(2), ct);
        }

        foreach (var request in requests)
        {
            await using var scope = scopes.CreateAsyncScope();
            EnsureScoped(scope.ServiceProvider, businessUnitId);
            var store = scope.ServiceProvider.GetRequiredService<IQuoteDeliveryStore>();

            // A claim that came back for another tenant means the scope was not honoured
            // somewhere; refuse to act on it rather than sending one tenant's quote under
            // another tenant's identity.
            if (request.BusinessUnitId != businessUnitId)
                throw new InvalidOperationException(
                    $"Quote delivery {request.Id} was claimed for BU {businessUnitId} but belongs to BU " +
                    $"{request.BusinessUnitId}; refusing to dispatch cross-tenant work.");

            try
            {
                await scope.ServiceProvider.GetRequiredService<IQuoteDeliverySender>().SendAsync(request, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (QuoteDeliveryPreSendException exception)
            {
                var errorCode = exception.Message;
                await store.FailAsync(request.Id, _workerId, request.LeaseToken, errorCode, 8, ct);
                logger.LogWarning("Quote delivery {DeliveryId} failed before external send with {ErrorCode} on attempt {AttemptCount}.",
                    request.Id, errorCode, request.AttemptCount);
                continue;
            }
            catch (Exception exception)
            {
                var errorCode = exception.GetType().Name;
                await store.MarkOutcomeUncertainAsync(request.Id, _workerId, request.LeaseToken, errorCode, ct);
                logger.LogCritical("Quote delivery {DeliveryId} has an uncertain SMTP outcome after {ErrorCode} on attempt {AttemptCount}.",
                    request.Id, errorCode, request.AttemptCount);
                continue;
            }

            try
            {
                await scope.ServiceProvider.GetRequiredService<IQuoteService>()
                    .FinalizeQuoteDeliveryAsync(request.QuoteId, request.BusinessUnitId, ct);
                await store.CompleteAsync(request.Id, _workerId, request.LeaseToken, ct);
                logger.LogInformation("Quote delivery {DeliveryId} completed on attempt {AttemptCount}.",
                    request.Id, request.AttemptCount);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                var errorCode = exception.GetType().Name;
                await store.MarkOutcomeUncertainAsync(request.Id, _workerId, request.LeaseToken, errorCode, ct);
                logger.LogCritical("Quote delivery {DeliveryId} has an uncertain external outcome after {ErrorCode} on attempt {AttemptCount}.",
                    request.Id, errorCode, request.AttemptCount);
            }
        }
        return requests.Count;
    }

    /// <summary>
    /// Fail closed. If the DbContext in this scope did not resolve the pushed tenant, every
    /// query below it would silently run cross-tenant under the bypass role again — which is
    /// exactly the defect being fixed, so it must be an error and not a warning.
    /// </summary>
    private static void EnsureScoped(IServiceProvider provider, long businessUnitId)
    {
        var db = provider.GetRequiredService<ErpRfqAutomationContext>();
        if (db.ScopedTenantId == businessUnitId) return;
        throw new InvalidOperationException(
            $"Quote delivery dispatch refused to run for BU {businessUnitId}: the DbContext resolved tenant " +
            $"{db.ScopedTenantId?.ToString() ?? "<none>"}. Tenant scope is mandatory for this worker.");
    }
}

public sealed class QuoteDeliveryWorker(
    QuoteDeliveryDispatcher dispatcher,
    ILogger<QuoteDeliveryWorker> logger,
    IQuoteDeliveryWorkerHeartbeat heartbeat)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Quote delivery worker started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            heartbeat.Beat();
            try
            {
                var count = await dispatcher.DispatchOnceAsync(stoppingToken);
                heartbeat.RecordSuccess();
                if (count == 0) await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                heartbeat.RecordFailure();
                logger.LogError("Quote delivery worker cycle failed with {ErrorCode}.", exception.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }
}
