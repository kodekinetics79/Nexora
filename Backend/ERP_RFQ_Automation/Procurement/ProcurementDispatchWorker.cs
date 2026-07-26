using System.Data;
using System.Text.Json;
using ERP_RFQ_Automation.Agent.Models;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Notifications;
using ERP_RFQ_Automation.Platform.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ERP_RFQ_Automation.Procurement;

public sealed class ProcurementDispatchWorker : BackgroundService
{
    private const int MaxAttempts = 5;
    private static readonly TimeSpan ClaimTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DefaultProviderCallTimeout = TimeSpan.FromMinutes(2);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ProcurementDispatchWorker> _logger;
    private readonly ITenantScopeAccessor _tenantScope;
    private readonly IProcurementDispatchHeartbeat _heartbeat;
    private readonly TimeSpan _providerCallTimeout;

    public ProcurementDispatchWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<ProcurementDispatchWorker> logger,
        ITenantScopeAccessor tenantScope,
        IProcurementDispatchHeartbeat heartbeat,
        TimeSpan? providerCallTimeout = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _tenantScope = tenantScope;
        _heartbeat = heartbeat;
        _providerCallTimeout = providerCallTimeout ?? DefaultProviderCallTimeout;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessOneAsync(stoppingToken);
                _heartbeat.RecordCycleSuccess();
                if (!processed) await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _heartbeat.RecordCycleFailure();
                _logger.LogError(ex, "Procurement dispatch cycle failed.");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }

    public async Task<bool> ProcessOneAsync(CancellationToken ct)
    {
        _heartbeat.Beat();
        var candidate = await DiscoverCandidateAsync(ct);
        if (candidate is null) return false;

        using var tenant = _tenantScope.Push(candidate.BusinessUnitId);
        var claim = await ClaimOneAsync(candidate, ct);
        if (claim is null) return false;
        if (claim.ReconciledOnly) return true;

        SolicitationDispatchPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<SolicitationDispatchPayload>(claim.PayloadJson)
                ?? throw new JsonException("The supplier RFQ outbox payload is empty.");
            if (payload.SolicitationId != claim.SupplierSolicitationId
                || payload.BusinessUnitId != claim.BusinessUnitId
                || payload.RfqId <= 0
                || string.IsNullOrWhiteSpace(payload.ToEmail)
                || string.IsNullOrWhiteSpace(payload.SupplierName)
                || string.IsNullOrWhiteSpace(payload.RfqNumber))
                throw new JsonException("The supplier RFQ outbox payload does not match its governed envelope.");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Procurement dispatch payload {MessageId} is invalid.", claim.MessageId);
            await FinishAsync(claim, DispatchOutcome.PermanentFailure, "INVALID_DISPATCH_PAYLOAD", ct);
            return true;
        }

        bool sent;
        var providerInvoked = false;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var notification = scope.ServiceProvider.GetRequiredService<INotificationService>();
            using var providerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            providerCts.CancelAfter(_providerCallTimeout);
            providerInvoked = true;
            _heartbeat.BeginProviderCall(_providerCallTimeout);
            try
            {
                sent = await notification.SendRfqToSupplierAsync(new RfqToSupplierNotification
                {
                    ToEmail = payload.ToEmail,
                    ToName = payload.SupplierName,
                    TenantId = payload.BusinessUnitId.ToString(),
                    BusinessUnitId = payload.BusinessUnitId.ToString(),
                    SupplierName = payload.SupplierName,
                    RfqNumber = payload.RfqNumber,
                    RfqTitle = $"Request for quotation {payload.RfqNumber}",
                    ItemSummary = payload.ItemSummary,
                    DueDate = payload.DueOn?.ToString("yyyy-MM-dd") ?? "Please respond promptly",
                    CtaPath = $"/procurement/rfqs/{payload.RfqId}/sourcing"
                }, providerCts.Token);
            }
            finally
            {
                _heartbeat.EndProviderCall();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The provider may already have accepted the message. Leave the claim in
            // PROCESSING so stale-claim reconciliation marks it uncertain, never retryable.
            throw;
        }
        catch (OperationCanceledException ex)
        {
            _heartbeat.RecordDeliveryFailure();
            _logger.LogError(
                "Supplier RFQ provider call timed out; delivery is uncertain for message {MessageId}, attempt {Attempt}, category {ErrorCategory}.",
                claim.MessageId, claim.AttemptCount, ex.GetType().Name);
            await FinishAsync(claim, DispatchOutcome.Uncertain, "DELIVERY_TIMEOUT_UNCERTAIN", ct);
            return true;
        }
        catch (Exception ex) when (!providerInvoked)
        {
            _heartbeat.RecordDeliveryFailure();
            _logger.LogError(
                "Supplier RFQ dispatch setup failed before provider invocation for message {MessageId}, attempt {Attempt}, category {ErrorCategory}.",
                claim.MessageId, claim.AttemptCount, ex.GetType().Name);
            await FinishAsync(claim, DispatchOutcome.RetriableFailure, "DISPATCH_SETUP_FAILED", ct);
            return true;
        }
        catch (Exception ex)
        {
            _heartbeat.RecordDeliveryFailure();
            _logger.LogError(
                "Supplier RFQ delivery outcome is uncertain for message {MessageId}, attempt {Attempt}, category {ErrorCategory}.",
                claim.MessageId, claim.AttemptCount, ex.GetType().Name);
            await FinishAsync(claim, DispatchOutcome.Uncertain, "DELIVERY_UNCERTAIN", ct);
            return true;
        }

        if (!sent) _heartbeat.RecordDeliveryFailure();
        await FinishAsync(
            claim,
            sent ? DispatchOutcome.Sent : DispatchOutcome.Uncertain,
            sent ? null : "DELIVERY_REJECTED_OR_UNCERTAIN",
            ct);
        return true;
    }

    private async Task<DispatchCandidate?> DiscoverCandidateAsync(CancellationToken ct)
    {
        List<long> businessUnitIds;
        await using (var controlScope = _scopeFactory.CreateAsyncScope())
        {
            var controlDb = controlScope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
            businessUnitIds = await controlDb.Set<Tenant>().AsNoTracking()
                .Where(x => x.Status == TenantStatus.Active && x.PrimaryBusinessUnitId != null)
                .OrderBy(x => x.Id)
                .Select(x => x.PrimaryBusinessUnitId!.Value)
                .Distinct()
                .ToListAsync(ct);
        }

        DispatchCandidate? selected = null;
        var now = DateTime.UtcNow;
        foreach (var businessUnitId in businessUnitIds)
        {
            using var tenant = _tenantScope.Push(businessUnitId);
            await using var tenantScope = _scopeFactory.CreateAsyncScope();
            var db = tenantScope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
            var candidate = await db.ProcurementOutboxMessages.AsNoTracking()
                .Where(x =>
                    x.Status == ProcurementOutboxStatuses.Processing && x.UpdatedOn <= now - ClaimTimeout
                    || x.Status == ProcurementOutboxStatuses.Pending && x.NextAttemptOn <= now)
                .OrderBy(x => x.Status == ProcurementOutboxStatuses.Processing ? 0 : 1)
                .ThenBy(x => x.NextAttemptOn)
                .ThenBy(x => x.Id)
                .Select(x => new DispatchCandidate(
                    x.Id,
                    x.BusinessUnitId,
                    x.Status == ProcurementOutboxStatuses.Processing ? 0 : 1,
                    x.NextAttemptOn))
                .FirstOrDefaultAsync(ct);

            if (candidate is not null && (selected is null
                || candidate.Priority < selected.Priority
                || candidate.Priority == selected.Priority && candidate.DueOn < selected.DueOn
                || candidate.Priority == selected.Priority && candidate.DueOn == selected.DueOn
                    && candidate.MessageId < selected.MessageId))
                selected = candidate;
        }

        return selected;
    }

    private async Task<DispatchClaim?> ClaimOneAsync(DispatchCandidate candidate, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(
                db.Database.IsNpgsql() ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable, ct);
            var now = DateTime.UtcNow;

            var message = await SelectCandidateForUpdateAsync(db, candidate, ct);
            if (message is null)
            {
                await tx.CommitAsync(ct);
                return null;
            }

            if (message.Status == ProcurementOutboxStatuses.Processing)
            {
                if (message.UpdatedOn > now - ClaimTimeout)
                {
                    await tx.CommitAsync(ct);
                    return null;
                }

                await MarkStaleClaimUncertainAsync(db, message, now, ct);
                await tx.CommitAsync(ct);
                _logger.LogError(
                    "Stale supplier RFQ claim {MessageId} was fenced as delivery uncertain for tenant {BusinessUnitId}.",
                    message.Id, message.BusinessUnitId);
                return new DispatchClaim(message.Id, message.BusinessUnitId,
                    message.SupplierSolicitationId, message.AttemptCount,
                    message.PayloadJson, ReconciledOnly: true);
            }

            if (message.Status != ProcurementOutboxStatuses.Pending || message.NextAttemptOn > now)
            {
                await tx.CommitAsync(ct);
                return null;
            }

            message.Status = ProcurementOutboxStatuses.Processing;
            message.AttemptCount++;
            message.UpdatedOn = now;
            var solicitation = await db.Set<SupplierSolicitation>()
                .SingleAsync(x => x.Id == message.SupplierSolicitationId, ct);
            solicitation.Status = SolicitationStatus.Dispatching;
            solicitation.UpdatedOn = now;
            solicitation.Version++;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new DispatchClaim(message.Id, message.BusinessUnitId, message.SupplierSolicitationId,
                message.AttemptCount, message.PayloadJson, ReconciledOnly: false);
        });
    }

    private static async Task<ProcurementOutboxMessage?> SelectCandidateForUpdateAsync(
        ErpRfqAutomationContext db,
        DispatchCandidate candidate,
        CancellationToken ct)
    {
        if (db.Database.IsNpgsql())
        {
            const string sql = """
                SELECT * FROM public.procurement_outbox
                WHERE "Id" = {0} AND "BusinessUnitId" = {1}
                FOR UPDATE SKIP LOCKED
                """;
            return (await db.ProcurementOutboxMessages
                .FromSqlRaw(sql, candidate.MessageId, candidate.BusinessUnitId)
                .ToListAsync(ct)).SingleOrDefault();
        }

        return await db.ProcurementOutboxMessages
            .SingleOrDefaultAsync(x => x.Id == candidate.MessageId
                && x.BusinessUnitId == candidate.BusinessUnitId, ct);
    }

    private static async Task MarkStaleClaimUncertainAsync(
        ErpRfqAutomationContext db,
        ProcurementOutboxMessage message,
        DateTime now,
        CancellationToken ct)
    {
        message.Status = ProcurementOutboxStatuses.Failed;
        message.LastErrorCode = "DELIVERY_UNCERTAIN";
        message.UpdatedOn = now;
        var solicitation = await db.Set<SupplierSolicitation>()
            .SingleAsync(x => x.Id == message.SupplierSolicitationId, ct);
        solicitation.Status = SolicitationStatus.DeliveryFailed;
        solicitation.UpdatedOn = now;
        solicitation.Version++;
        AddEvent(db, message, solicitation, "SUPPLIER_SOLICITATION_DELIVERY_UNCERTAIN", now);
        await db.SaveChangesAsync(ct);
    }

    private async Task FinishAsync(
        DispatchClaim claim,
        DispatchOutcome outcome,
        string? errorCode,
        CancellationToken ct)
    {
        if (claim.ReconciledOnly) return;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var message = await db.ProcurementOutboxMessages.SingleAsync(x => x.Id == claim.MessageId, ct);
            if (message.Status != ProcurementOutboxStatuses.Processing
                || message.AttemptCount != claim.AttemptCount)
                throw new ProcurementConflictException("The procurement outbox claim is no longer owned by this dispatch attempt.");

            var solicitation = await db.Set<SupplierSolicitation>()
                .SingleAsync(x => x.Id == message.SupplierSolicitationId, ct);
            var now = DateTime.UtcNow;
            var terminalFailure = outcome is DispatchOutcome.PermanentFailure or DispatchOutcome.Uncertain
                || (outcome == DispatchOutcome.RetriableFailure && message.AttemptCount >= MaxAttempts);

            if (outcome == DispatchOutcome.Sent)
            {
                message.Status = ProcurementOutboxStatuses.Sent;
                message.SentOn = now;
                message.ProviderReference = $"legacy-notification-acceptance:{message.Id}:{message.AttemptCount}";
                message.LastErrorCode = null;
                solicitation.Status = SolicitationStatus.Sent;
                solicitation.SentOn = now;
            }
            else if (terminalFailure)
            {
                message.Status = ProcurementOutboxStatuses.Failed;
                message.LastErrorCode = errorCode;
                solicitation.Status = SolicitationStatus.DeliveryFailed;
            }
            else
            {
                message.Status = ProcurementOutboxStatuses.Pending;
                message.NextAttemptOn = now.AddSeconds(Math.Min(300, 15 * (1 << (message.AttemptCount - 1))));
                message.LastErrorCode = errorCode;
                solicitation.Status = SolicitationStatus.PendingDispatch;
            }

            message.UpdatedOn = now;
            solicitation.UpdatedOn = now;
            solicitation.Version++;
            var eventType = outcome == DispatchOutcome.Sent
                ? "SUPPLIER_SOLICITATION_SENT"
                : outcome == DispatchOutcome.Uncertain
                    ? "SUPPLIER_SOLICITATION_DELIVERY_UNCERTAIN"
                    : terminalFailure
                        ? "SUPPLIER_SOLICITATION_DELIVERY_FAILED"
                        : "SUPPLIER_SOLICITATION_RETRY_SCHEDULED";
            AddEvent(db, message, solicitation, eventType, now);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            if (outcome == DispatchOutcome.Sent)
            {
                _heartbeat.RecordSuccess();
                _logger.LogInformation(
                    "Supplier RFQ message {MessageId} was delivered for tenant {BusinessUnitId} on attempt {Attempt}.",
                    message.Id, message.BusinessUnitId, message.AttemptCount);
            }
            else
            {
                _logger.LogWarning(
                    "Supplier RFQ message {MessageId} completed attempt {Attempt} with status {Status} and code {ErrorCode}.",
                    message.Id, message.AttemptCount, message.Status, message.LastErrorCode);
            }
        });
    }

    private static void AddEvent(
        ErpRfqAutomationContext db,
        ProcurementOutboxMessage message,
        SupplierSolicitation solicitation,
        string eventType,
        DateTime occurredOn)
    {
        db.ProcurementEvents.Add(new ProcurementEvent
        {
            BusinessUnitId = message.BusinessUnitId,
            AggregateType = "SupplierSolicitation",
            AggregateId = solicitation.Id,
            AggregateVersion = solicitation.Version,
            EventType = eventType,
            Actor = "procurement-dispatch-worker",
            CorrelationId = $"dispatch:{message.Id}:attempt:{message.AttemptCount}",
            IdempotencyKey = $"dispatch:{message.Id}:attempt:{message.AttemptCount}:{eventType}",
            PayloadJson = JsonSerializer.Serialize(new { message.AttemptCount, message.Status, message.NextAttemptOn, message.LastErrorCode }),
            OccurredOn = occurredOn
        });
    }

    private sealed record DispatchClaim(
        long MessageId,
        long BusinessUnitId,
        long SupplierSolicitationId,
        int AttemptCount,
        string PayloadJson,
        bool ReconciledOnly);

    private sealed record DispatchCandidate(
        long MessageId,
        long BusinessUnitId,
        int Priority,
        DateTime DueOn);

    private enum DispatchOutcome
    {
        Sent,
        RetriableFailure,
        PermanentFailure,
        Uncertain
    }
}

public interface IProcurementDispatchHeartbeat
{
    DateTimeOffset? LastSeen { get; }
    DateTimeOffset? LastSuccess { get; }
    DateTimeOffset? ProviderCallDeadline { get; }
    int ConsecutiveCycleFailures { get; }
    int ConsecutiveDeliveryFailures { get; }
    void Beat();
    void RecordSuccess();
    void RecordCycleSuccess();
    void RecordCycleFailure();
    void RecordDeliveryFailure();
    void BeginProviderCall(TimeSpan timeout);
    void EndProviderCall();
}

public sealed class ProcurementDispatchHeartbeat : IProcurementDispatchHeartbeat
{
    private long _lastSeenTicks;
    private long _lastSuccessTicks;
    private long _providerCallDeadlineTicks;
    private int _consecutiveCycleFailures;
    private int _consecutiveDeliveryFailures;

    public DateTimeOffset? LastSeen => Read(_lastSeenTicks);
    public DateTimeOffset? LastSuccess => Read(_lastSuccessTicks);
    public DateTimeOffset? ProviderCallDeadline => Read(_providerCallDeadlineTicks);
    public int ConsecutiveCycleFailures => Volatile.Read(ref _consecutiveCycleFailures);
    public int ConsecutiveDeliveryFailures => Volatile.Read(ref _consecutiveDeliveryFailures);

    public void Beat() => Interlocked.Exchange(ref _lastSeenTicks, DateTimeOffset.UtcNow.UtcTicks);

    public void RecordSuccess()
    {
        var now = DateTimeOffset.UtcNow.UtcTicks;
        Interlocked.Exchange(ref _lastSeenTicks, now);
        Interlocked.Exchange(ref _lastSuccessTicks, now);
        Interlocked.Exchange(ref _consecutiveCycleFailures, 0);
        Interlocked.Exchange(ref _consecutiveDeliveryFailures, 0);
    }

    public void RecordCycleSuccess()
    {
        Beat();
        Interlocked.Exchange(ref _consecutiveCycleFailures, 0);
    }

    public void RecordCycleFailure()
    {
        Beat();
        Interlocked.Increment(ref _consecutiveCycleFailures);
    }

    public void RecordDeliveryFailure()
    {
        Beat();
        Interlocked.Increment(ref _consecutiveDeliveryFailures);
    }

    public void BeginProviderCall(TimeSpan timeout)
    {
        Beat();
        var deadline = DateTimeOffset.UtcNow.Add(timeout).UtcTicks;
        Interlocked.Exchange(ref _providerCallDeadlineTicks, deadline);
    }

    public void EndProviderCall()
        => Interlocked.Exchange(ref _providerCallDeadlineTicks, 0);

    private static DateTimeOffset? Read(long ticks)
        => ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
}

public sealed class ProcurementDispatchHealthCheck : IHealthCheck
{
    private static readonly TimeSpan MaximumSilence = TimeSpan.FromSeconds(30);
    private readonly IProcurementDispatchHeartbeat _heartbeat;

    public ProcurementDispatchHealthCheck(IProcurementDispatchHeartbeat heartbeat) => _heartbeat = heartbeat;

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var lastSeen = _heartbeat.LastSeen;
        if (!lastSeen.HasValue)
            return Task.FromResult(HealthCheckResult.Unhealthy("Procurement dispatch worker has not started."));
        if (_heartbeat.ConsecutiveDeliveryFailures >= 3)
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "Procurement supplier delivery has failed repeatedly.",
                data: new Dictionary<string, object>
                {
                    ["consecutiveDeliveryFailures"] = _heartbeat.ConsecutiveDeliveryFailures
                }));
        if (_heartbeat.ProviderCallDeadline is { } deadline && DateTimeOffset.UtcNow <= deadline)
            return Task.FromResult(HealthCheckResult.Healthy("Procurement dispatch provider call is in progress."));
        if (_heartbeat.ConsecutiveCycleFailures >= 3)
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "Procurement dispatch claim loop has failed repeatedly.",
                data: new Dictionary<string, object> { ["consecutiveFailures"] = _heartbeat.ConsecutiveCycleFailures }));
        if (DateTimeOffset.UtcNow - lastSeen.Value > MaximumSilence)
            return Task.FromResult(HealthCheckResult.Unhealthy("Procurement dispatch worker heartbeat is stale."));

        var data = _heartbeat.LastSuccess is { } success
            ? new Dictionary<string, object> { ["lastSuccess"] = success }
            : null;
        return Task.FromResult(HealthCheckResult.Healthy(
            "Procurement dispatch claim loop is active.", data));
    }
}
