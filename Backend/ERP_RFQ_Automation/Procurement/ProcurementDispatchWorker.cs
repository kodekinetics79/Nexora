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
    private readonly IProcurementDeliveryConfiguration _deliveryConfiguration;
    private readonly TimeSpan _providerCallTimeout;
    private readonly string _workerId = $"procurement-dispatch-{Environment.MachineName}-{Guid.NewGuid():N}";

    public ProcurementDispatchWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<ProcurementDispatchWorker> logger,
        ITenantScopeAccessor tenantScope,
        IProcurementDispatchHeartbeat heartbeat,
        TimeSpan? providerCallTimeout = null,
        IProcurementDeliveryConfiguration? deliveryConfiguration = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _tenantScope = tenantScope;
        _heartbeat = heartbeat;
        _providerCallTimeout = providerCallTimeout ?? DefaultProviderCallTimeout;
        _deliveryConfiguration = deliveryConfiguration ?? TestDeliveryConfiguration.Instance;
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
            await FinishAsync(claim, DispatchOutcome.PermanentFailure, "INVALID_DISPATCH_PAYLOAD", null, ct);
            return true;
        }

        var governanceBlock = await CurrentDispatchBlockAsync(claim, payload, ct);
        if (governanceBlock is not null)
        {
            _logger.LogWarning(
                "Supplier RFQ dispatch {MessageId} was blocked by current Supplier governance: {ReasonCode}.",
                claim.MessageId, governanceBlock);
            await FinishAsync(claim, DispatchOutcome.PermanentFailure, governanceBlock, null, ct);
            return true;
        }

        // Per TENANT, not per deployment: the tenant's own verified mailbox counts as configured
        // (issue #54). Resolved through the same authority the sender itself will use, so this
        // gate cannot disagree with the send that follows it.
        var readiness = await _deliveryConfiguration.ResolveAsync(claim.BusinessUnitId, ct);
        if (!readiness.IsConfigured)
        {
            _logger.LogWarning(
                "Supplier RFQ dispatch {MessageId} for tenant {BusinessUnitId} has no transmitting sender (origin {Origin}, provider {Provider}).",
                claim.MessageId, claim.BusinessUnitId, readiness.Origin, readiness.ProviderName);
            await FinishAsync(claim, DispatchOutcome.PermanentFailure, "DELIVERY_PROVIDER_NOT_CONFIGURED", null, ct);
            return true;
        }

        NotificationDispatchReceipt receipt;
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
                receipt = await notification.SendRfqToSupplierWithReceiptAsync(new RfqToSupplierNotification
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
            await FinishAsync(claim, DispatchOutcome.Uncertain, "DELIVERY_TIMEOUT_UNCERTAIN", null, ct);
            return true;
        }
        catch (Exception ex) when (!providerInvoked)
        {
            _heartbeat.RecordDeliveryFailure();
            _logger.LogError(
                "Supplier RFQ dispatch setup failed before provider invocation for message {MessageId}, attempt {Attempt}, category {ErrorCategory}.",
                claim.MessageId, claim.AttemptCount, ex.GetType().Name);
            await FinishAsync(claim, DispatchOutcome.RetriableFailure, "DISPATCH_SETUP_FAILED", null, ct);
            return true;
        }
        catch (Exception ex)
        {
            _heartbeat.RecordDeliveryFailure();
            _logger.LogError(
                "Supplier RFQ delivery outcome is uncertain for message {MessageId}, attempt {Attempt}, category {ErrorCategory}.",
                claim.MessageId, claim.AttemptCount, ex.GetType().Name);
            await FinishAsync(claim, DispatchOutcome.Uncertain, "DELIVERY_UNCERTAIN", null, ct);
            return true;
        }

        var accepted = receipt.Accepted
            && !string.IsNullOrWhiteSpace(receipt.Provider)
            && !string.IsNullOrWhiteSpace(receipt.AcceptanceReference);
        if (!accepted) _heartbeat.RecordDeliveryFailure();
        await FinishAsync(
            claim,
            accepted ? DispatchOutcome.Sent : DispatchOutcome.Uncertain,
            accepted ? null : "DELIVERY_ACCEPTANCE_EVIDENCE_MISSING",
            accepted ? receipt : null,
            ct);
        return true;
    }

    private async Task<string?> CurrentDispatchBlockAsync(
        DispatchClaim claim,
        SolicitationDispatchPayload payload,
        CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
        var current = await (
            from solicitation in db.Set<SupplierSolicitation>().AsNoTracking()
            join supplier in db.Suppliers.AsNoTracking()
                on new { solicitation.SupplierId, Buid = (long?)solicitation.BusinessUnitId }
                equals new { SupplierId = supplier.Id, supplier.Buid }
            where solicitation.BusinessUnitId == claim.BusinessUnitId
                && solicitation.Id == claim.SupplierSolicitationId
            select new { Solicitation = solicitation, Supplier = supplier })
            .SingleOrDefaultAsync(ct);
        if (current is null || current.Solicitation.Status != SolicitationStatus.Dispatching)
            return "SUPPLIER_RFQ_STATE_INVALID";
        if (payload.SolicitationId != current.Solicitation.Id
            || payload.BusinessUnitId != current.Solicitation.BusinessUnitId
            || payload.RfqId != current.Solicitation.RfqId)
            return "SUPPLIER_RFQ_ENVELOPE_MISMATCH";
        if (current.Supplier.IsActive != true)
            return "SUPPLIER_INACTIVE";
        if (!string.Equals(current.Supplier.ContactEmail?.Trim(), payload.ToEmail.Trim(),
                StringComparison.OrdinalIgnoreCase))
            return "SUPPLIER_CONTACT_CHANGED";
        if (current.Supplier.GovernanceStatus is not (SupplierGovernanceStatuses.Approved
                or SupplierGovernanceStatuses.Preferred or SupplierGovernanceStatuses.Provisional)
            || current.Supplier.VerificationStatus != SupplierVerificationStatuses.Verified
            || current.Supplier.ComplianceStatus != SupplierComplianceStatuses.Cleared
            || current.Supplier.RiskStatus is SupplierRiskStatuses.High or SupplierRiskStatuses.Blocked
            || current.Supplier.ReadinessStatus != SupplierReadinessStatuses.Ready)
            return "SUPPLIER_GOVERNANCE_NOT_READY";
        return null;
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
                    x.Status == ProcurementOutboxStatuses.Processing
                        && (x.LeaseUntil == null || x.LeaseUntil <= now)
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
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(
                db.Database.IsNpgsql() ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable, ct);
            var now = await DatabaseUtcNowAsync(db, ct);

            var message = await SelectCandidateForUpdateAsync(db, candidate, ct);
            if (message is null)
            {
                await tx.CommitAsync(ct);
                return null;
            }

            if (message.Status == ProcurementOutboxStatuses.Processing)
            {
                if (message.LeaseUntil.HasValue && message.LeaseUntil > now)
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
                    message.PayloadJson, Guid.Empty, ReconciledOnly: true);
            }

            if (message.Status != ProcurementOutboxStatuses.Pending || message.NextAttemptOn > now)
            {
                await tx.CommitAsync(ct);
                return null;
            }

            message.Status = ProcurementOutboxStatuses.Processing;
            message.AttemptCount++;
            message.LeaseOwner = _workerId;
            message.LeaseToken = Guid.NewGuid();
            message.LeaseUntil = now.Add(ClaimTimeout);
            message.ProviderName = _deliveryConfiguration.ProviderName;
            message.UpdatedOn = now;
            var solicitation = await db.Set<SupplierSolicitation>()
                .SingleAsync(x => x.Id == message.SupplierSolicitationId, ct);
            solicitation.Status = SolicitationStatus.Dispatching;
            solicitation.UpdatedOn = now;
            solicitation.Version++;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new DispatchClaim(message.Id, message.BusinessUnitId, message.SupplierSolicitationId,
                message.AttemptCount, message.PayloadJson, message.LeaseToken.Value, ReconciledOnly: false);
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
        message.LeaseOwner = null;
        message.LeaseToken = null;
        message.LeaseUntil = null;
        message.UpdatedOn = now;
        var solicitation = await db.Set<SupplierSolicitation>()
            .SingleAsync(x => x.Id == message.SupplierSolicitationId, ct);
        solicitation.Status = SolicitationStatus.DeliveryFailed;
        solicitation.UpdatedOn = now;
        solicitation.Version++;

        // FENCING THE SAME CLAIM TWICE IS THE SAME FACT, NOT A NEW ONE — and writing it twice
        // used to halt every outbound RFQ in the system.
        //
        // The event's idempotency key is message + attempt + type, and fencing does not
        // increment the attempt: a fence is not a delivery attempt. So a second fence of the
        // same stale claim produced an identical key, violated
        // IX_procurement_events_BusinessUnitId_EventType_IdempotencyKey, and took down the whole
        // SaveChanges. The message therefore stayed PROCESSING with an expired lease, the next
        // cycle found the same stale claim and fenced it again, and ProcessOneAsync threw on
        // every pass — roughly every twelve seconds, for ever.
        //
        // Nothing else could be dispatched behind it. The worker processes one message per
        // cycle, so a single poisoned row stopped supplier RFQs and customer quotes for the
        // entire deployment, with no alert saying so. Observed in production on 2026-08-19:
        // solicitation 1 wedged in DISPATCHING while a healthy solicitation 2 sat in
        // PENDINGDISPATCH behind it and was never touched.
        //
        // The recovery path must therefore be able to run twice. Skipping the duplicate lets
        // the status and lease release commit, which is the part that actually frees the queue.
        await AddEventIfNewAsync(db, message, solicitation, "SUPPLIER_SOLICITATION_DELIVERY_UNCERTAIN", now, ct);

        await UpdateSourcingCaseLifecycleAsync(db, message, solicitation, sent: false, terminalFailure: true, now, ct);
        await db.SaveChangesAsync(ct);
    }

    private async Task FinishAsync(
        DispatchClaim claim,
        DispatchOutcome outcome,
        string? errorCode,
        NotificationDispatchReceipt? receipt,
        CancellationToken ct)
    {
        if (claim.ReconciledOnly) return;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            // EnableRetryOnFailure re-runs this delegate on the SAME DbContext. Without the
            // clear, a 40001 from SaveChangesAsync below would leave the message tracked with
            // Status already flipped to Sent, the re-query would hand that tracked instance
            // back, and the ownership guard would throw — leaving the outbox row PROCESSING to
            // be fenced as DELIVERY_UNCERTAIN and inviting a human to re-send an RFQ the
            // supplier already has. Everything this delegate touches is re-read below.
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var message = await db.ProcurementOutboxMessages.SingleAsync(x => x.Id == claim.MessageId, ct);
            var now = await DatabaseUtcNowAsync(db, ct);
            if (message.Status != ProcurementOutboxStatuses.Processing
                || message.AttemptCount != claim.AttemptCount
                || message.LeaseOwner != _workerId
                || message.LeaseToken != claim.LeaseToken
                || message.LeaseUntil <= now)
                throw new ProcurementConflictException("The procurement outbox claim is no longer owned by this dispatch attempt.");

            var solicitation = await db.Set<SupplierSolicitation>()
                .SingleAsync(x => x.Id == message.SupplierSolicitationId, ct);
            var terminalFailure = outcome is DispatchOutcome.PermanentFailure or DispatchOutcome.Uncertain
                || (outcome == DispatchOutcome.RetriableFailure && message.AttemptCount >= MaxAttempts);

            if (outcome == DispatchOutcome.Sent)
            {
                message.Status = ProcurementOutboxStatuses.Sent;
                message.SentOn = now;
                message.ProviderName = receipt!.Provider;
                message.ProviderReference = receipt.AcceptanceReference;
                message.LastErrorCode = null;
                solicitation.Status = SolicitationStatus.Sent;
                solicitation.SentOn = now;
            }
            else if (terminalFailure)
            {
                message.Status = outcome == DispatchOutcome.Uncertain
                    ? ProcurementOutboxStatuses.Failed
                    : ProcurementOutboxStatuses.DeadLettered;
                message.DeadLetteredOn = outcome == DispatchOutcome.Uncertain ? null : now;
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

            message.LeaseOwner = null;
            message.LeaseToken = null;
            message.LeaseUntil = null;
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
            // A manual retry resets the attempt counter (RetrySolicitationAsync), so a retry
            // round recomputes keys the first round already wrote. Same gate as the fence and
            // the sourcing-case event below: re-writing the same fact is skipped, because the
            // failed insert would take the lease release down with it and wedge the queue.
            await AddEventIfNewAsync(db, message, solicitation, eventType, now, ct);
            await UpdateSourcingCaseLifecycleAsync(db, message, solicitation,
                outcome == DispatchOutcome.Sent, terminalFailure, now, ct);
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

    private static async Task UpdateSourcingCaseLifecycleAsync(
        ErpRfqAutomationContext db,
        ProcurementOutboxMessage message,
        SupplierSolicitation solicitation,
        bool sent,
        bool terminalFailure,
        DateTime now,
        CancellationToken ct)
    {
        if (!solicitation.SourcingCaseId.HasValue) return;
        var sourcingCase = await db.SourcingCases.SingleOrDefaultAsync(x =>
            x.BusinessUnitId == message.BusinessUnitId && x.Id == solicitation.SourcingCaseId.Value, ct);
        if (sourcingCase is null) return;
        var anotherWasSent = await db.Set<SupplierSolicitation>().AsNoTracking().AnyAsync(x =>
            x.BusinessUnitId == message.BusinessUnitId && x.SourcingCaseId == sourcingCase.Id
            && x.Id != solicitation.Id
            && (x.Status == SolicitationStatus.Sent || x.Status == SolicitationStatus.Responded), ct);

        if (sent)
        {
            sourcingCase.Status = SourcingCaseStatuses.OutreachSent;
            sourcingCase.NextAction = "Track Supplier response";
        }
        else if (terminalFailure)
        {
            sourcingCase.Status = anotherWasSent
                ? SourcingCaseStatuses.OutreachSent
                : SourcingCaseStatuses.OutreachReady;
            sourcingCase.NextAction = anotherWasSent
                ? "Track responses and review failed Supplier delivery"
                : "Review failed Supplier RFQ delivery";
        }
        else
        {
            sourcingCase.Status = anotherWasSent
                ? SourcingCaseStatuses.OutreachSent
                : SourcingCaseStatuses.OutreachReady;
            sourcingCase.NextAction = anotherWasSent
                ? "Track responses; another Supplier RFQ delivery will retry automatically"
                : "Supplier RFQ delivery will retry automatically";
        }

        sourcingCase.Version++;
        sourcingCase.UpdatedOn = now;
        sourcingCase.UpdatedBy = "procurement-dispatch-worker";
        // Same gate as the solicitation event, for the same reason: fencing does not increment
        // the attempt, so a second fence recomputes this exact key. #56 guarded only the
        // solicitation event and the loop continued on THIS one — which is why the check now
        // lives in one place that every dispatch event passes through.
        var caseEventType = sent ? "SOURCING_CASE_OUTREACH_SENT"
            : terminalFailure ? "SOURCING_CASE_DELIVERY_REVIEW_REQUIRED"
            : "SOURCING_CASE_DELIVERY_RETRY_SCHEDULED";
        var caseKey = $"{DispatchIdentity(message)}:attempt:{message.AttemptCount}:sourcing-case";
        if (await EventAlreadyRecordedAsync(db, message.BusinessUnitId, caseEventType, caseKey, ct))
            return;

        db.ProcurementEvents.Add(new ProcurementEvent
        {
            BusinessUnitId = message.BusinessUnitId,
            AggregateType = "SourcingCase",
            AggregateId = sourcingCase.Id,
            AggregateVersion = sourcingCase.Version,
            EventType = caseEventType,
            Actor = "procurement-dispatch-worker",
            CorrelationId = message.OriginCorrelationId ?? $"{DispatchIdentity(message)}:attempt:{message.AttemptCount}",
            IdempotencyKey = caseKey,
            PayloadJson = JsonSerializer.Serialize(new
            {
                SupplierSolicitationId = solicitation.Id,
                solicitation.SupplierRfqNumber,
                sourcingCase.Status,
                sourcingCase.NextAction,
                message.LastErrorCode
            }),
            OccurredOn = now
        });
    }

    /// <summary>
    /// Has this exact dispatch event already been recorded?
    ///
    /// <para>EVERY event this worker writes is keyed on message + attempt + type, and fencing a
    /// stale claim does not increment the attempt — correctly, because a fence is not a delivery
    /// attempt. So any path that can run twice recomputes a key it has already written, the
    /// insert violates
    /// <c>IX_procurement_events_BusinessUnitId_EventType_IdempotencyKey</c>, and the whole
    /// SaveChanges fails — including the lease release that would have freed the row. The
    /// message stays PROCESSING, the next cycle fences it again, and the worker crash-loops.
    /// Because it handles one message per cycle, that single row stops ALL outbound procurement
    /// mail for every tenant.</para>
    ///
    /// <para><b>Why this is a shared gate rather than another guard.</b> #56 fixed exactly one
    /// event, <c>SUPPLIER_SOLICITATION_DELIVERY_UNCERTAIN</c>, and the loop continued in
    /// production on the sourcing-case event written a few lines later — same flaw, same
    /// key shape, missed because the fix was aimed at the symptom that had been read in the
    /// logs. Guarding events one at a time leaves the next one to reintroduce it. Everything
    /// this worker records now passes through here.</para>
    /// </summary>
    private static Task<bool> EventAlreadyRecordedAsync(
        ErpRfqAutomationContext db, long businessUnitId, string eventType, string idempotencyKey,
        CancellationToken ct)
        => db.ProcurementEvents.AsNoTracking().AnyAsync(
            x => x.BusinessUnitId == businessUnitId
              && x.EventType == eventType
              && x.IdempotencyKey == idempotencyKey, ct);

    /// <summary>Records a solicitation event unless that exact fact is already on the ledger.</summary>
    private static async Task AddEventIfNewAsync(
        ErpRfqAutomationContext db, ProcurementOutboxMessage message, SupplierSolicitation solicitation,
        string eventType, DateTime occurredOn, CancellationToken ct)
    {
        if (await EventAlreadyRecordedAsync(db, message.BusinessUnitId, eventType, EventKey(message, eventType), ct))
            return;
        AddEvent(db, message, solicitation, eventType, occurredOn);
    }

    /// <summary>
    /// The event's identity: message, round, attempt and type. Deliberately carries nothing
    /// that varies between two writes of the SAME fact, which is what makes it an idempotency
    /// key — and is exactly why re-writing that fact has to be handled rather than attempted.
    ///
    /// <para>The round is load-bearing: both retry surfaces (RetrySolicitationAsync, platform
    /// dead-letter recovery) reset the attempt counter, so attempt numbers repeat across
    /// rounds and a redelivery's outcome is a DIFFERENT fact than the first round's — it gets
    /// its own key rather than being swallowed by the duplicate gate. Round zero keeps the
    /// historical key shape so events already on the ledger stay recognised.</para>
    /// </summary>
    private static string EventKey(ProcurementOutboxMessage message, string eventType)
        => $"{DispatchIdentity(message)}:attempt:{message.AttemptCount}:{eventType}";

    /// <summary>"dispatch:{id}" on the original round, "dispatch:{id}:round:{r}" after.</summary>
    private static string DispatchIdentity(ProcurementOutboxMessage message)
        => message.DispatchRound == 0
            ? $"dispatch:{message.Id}"
            : $"dispatch:{message.Id}:round:{message.DispatchRound}";

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
            CorrelationId = message.OriginCorrelationId ?? $"{DispatchIdentity(message)}:attempt:{message.AttemptCount}",
            IdempotencyKey = EventKey(message, eventType),
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
        Guid LeaseToken,
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

    private static async Task<DateTime> DatabaseUtcNowAsync(ErpRfqAutomationContext db, CancellationToken ct)
        => db.Database.IsNpgsql()
            ? await db.Database.SqlQueryRaw<DateTime>(
                "SELECT (CURRENT_TIMESTAMP AT TIME ZONE 'UTC') AS \"Value\"").SingleAsync(ct)
            : DateTime.UtcNow;

    private sealed class TestDeliveryConfiguration : IProcurementDeliveryConfiguration
    {
        public static readonly TestDeliveryConfiguration Instance = new();
        public bool IsConfigured => true;
        public string ProviderName => "test";
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
