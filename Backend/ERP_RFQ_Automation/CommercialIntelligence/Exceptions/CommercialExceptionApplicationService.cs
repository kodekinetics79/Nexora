using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ERP_RFQ_Automation.CommercialIntelligence.Sales;
using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.CommercialIntelligence.Exceptions;

public sealed record CommercialExceptionAccessScope(bool TenantWide, long? OwnerUserId)
{
    public static CommercialExceptionAccessScope ForTenant() => new(true, null);

    public static CommercialExceptionAccessScope ForOwner(long ownerUserId)
        => ownerUserId > 0
            ? new CommercialExceptionAccessScope(false, ownerUserId)
            : throw new ArgumentOutOfRangeException(nameof(ownerUserId));
}

public interface ICommercialExceptionApplicationService
{
    Task<CommercialExceptionPage> QueryAsync(
        long businessUnitId,
        CommercialExceptionQuery query,
        CommercialExceptionAccessScope scope,
        CancellationToken cancellationToken);

    Task<RefreshCommercialExceptionsResult> RefreshAsync(
        long businessUnitId,
        RefreshCommercialExceptionsCommand command,
        CancellationToken cancellationToken);

    Task<CommercialExceptionItem> TransitionAsync(
        long businessUnitId,
        long commercialExceptionId,
        TransitionCommercialExceptionCommand command,
        CommercialExceptionAccessScope scope,
        CancellationToken cancellationToken);
}

public sealed class CommercialExceptionApplicationService(
    ErpRfqAutomationContext db,
    ITenantContext tenantContext) : ICommercialExceptionApplicationService
{
    public const string RuleVersion = "commercial-exceptions-v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly CommercialExceptionMetricDefinitions MetricDefinitions = new(
        "Records matching the current filters.",
        "Open or acknowledged records in the current access scope, independent of filters.",
        "Active critical-severity records in the current access scope, independent of filters.",
        "Active records past their SLA due time in the current access scope, independent of filters.");

    public async Task<CommercialExceptionPage> QueryAsync(
        long businessUnitId,
        CommercialExceptionQuery query,
        CommercialExceptionAccessScope scope,
        CancellationToken cancellationToken)
    {
        businessUnitId = RequireTenant(businessUnitId);
        ValidateScope(scope);
        ArgumentNullException.ThrowIfNull(query);
        if (query.PageNumber < 1)
            throw new ArgumentOutOfRangeException(nameof(query.PageNumber), "Page number must be at least one.");
        if (query.PageSize is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(query.PageSize), "Page size must be between one and 100.");

        var generatedAtUtc = DateTime.UtcNow;
        var sourceCoverage = await SourceCoverageAsync(businessUnitId, cancellationToken);
        var availableSourceCount = sourceCoverage.Count(x => x.IsAvailable);
        // Counted against the sources that ACTUALLY EXIST, not against a literal. This read
        // "availableSourceCount switch { 0 => Unavailable, 2 => Complete, _ => Partial }", so adding
        // the FR-DLM-07 delivery source silently downgraded a fully healthy tenant to "Partial" —
        // wiring-contract failure #9, an allowed-value set that one guard was never told about. The
        // count now derives from the same list the panel renders.
        var coverageStatus = availableSourceCount == 0
            ? "Unavailable"
            : availableSourceCount == sourceCoverage.Count
                ? "Complete"
                : "Partial";
        var scoped = db.CommercialExceptionCases.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId);
        if (!scope.TenantWide)
            scoped = scoped.Where(x => x.OwnerUserId == scope.OwnerUserId!.Value);

        var active = scoped.Where(x =>
            x.Status == CommercialExceptionStatus.Open ||
            x.Status == CommercialExceptionStatus.Acknowledged);
        var activeCount = await active.CountAsync(cancellationToken);
        var critical = await active.CountAsync(
            x => x.Severity == CommercialExceptionSeverity.Critical, cancellationToken);
        var overdue = await active.CountAsync(x => x.SlaDueAtUtc < generatedAtUtc, cancellationToken);

        var filtered = scoped;
        if (query.Status.HasValue)
            filtered = filtered.Where(x => x.Status == query.Status.Value);
        if (query.Type.HasValue)
            filtered = filtered.Where(x => x.ExceptionType == query.Type.Value);
        if (query.MinimumSeverity.HasValue)
        {
            var severities = Enum.GetValues<CommercialExceptionSeverity>()
                .Where(x => x >= query.MinimumSeverity.Value)
                .ToArray();
            filtered = filtered.Where(x => severities.Contains(x.Severity));
        }
        if (query.OverdueOnly)
            filtered = filtered.Where(x =>
                (x.Status == CommercialExceptionStatus.Open ||
                 x.Status == CommercialExceptionStatus.Acknowledged) &&
                x.SlaDueAtUtc < generatedAtUtc);

        var total = await filtered.CountAsync(cancellationToken);
        var rows = await filtered
            .OrderByDescending(x => x.Severity == CommercialExceptionSeverity.Critical)
            .ThenByDescending(x => x.Severity == CommercialExceptionSeverity.High)
            .ThenBy(x => x.SlaDueAtUtc)
            .ThenBy(x => x.Id)
            .Skip((query.PageNumber - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);
        var ownerNames = await OwnerNamesAsync(
            businessUnitId,
            rows.Where(x => x.OwnerUserId.HasValue).Select(x => x.OwnerUserId!.Value),
            cancellationToken);

        return new CommercialExceptionPage(
            rows.Select(x => ToItem(
                x,
                generatedAtUtc,
                x.OwnerUserId.HasValue && ownerNames.TryGetValue(x.OwnerUserId.Value, out var name)
                    ? name
                    : null)).ToArray(),
            total,
            activeCount,
            critical,
            overdue,
            generatedAtUtc,
            RuleVersion,
            scope.TenantWide ? "tenant" : "assigned_to_me",
            query.PageNumber,
            query.PageSize,
            coverageStatus,
            sourceCoverage,
            MetricDefinitions);
    }

    public Task<RefreshCommercialExceptionsResult> RefreshAsync(
        long businessUnitId,
        RefreshCommercialExceptionsCommand command,
        CancellationToken cancellationToken)
    {
        businessUnitId = RequireTenant(businessUnitId);
        ValidateRefresh(command);
        var normalized = command with
        {
            CorrelationId = command.CorrelationId.Trim(),
            IdempotencyKey = command.IdempotencyKey.Trim(),
            ActorId = command.ActorId.Trim()
        };
        var requestHash = Hash(new
        {
            operation = "refresh",
            businessUnitId,
            normalized.ActorId
        });
        var strategy = db.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted, cancellationToken);
            await AcquireLockAsync($"commercial-exceptions:{businessUnitId}", cancellationToken);

            var replay = await db.CommercialExceptionOperations.AsNoTracking()
                .SingleOrDefaultAsync(x =>
                    x.BusinessUnitId == businessUnitId &&
                    x.IdempotencyKey == normalized.IdempotencyKey,
                    cancellationToken);
            if (replay is not null)
            {
                EnsureOperationReplay(replay, "Refresh", null, requestHash);
                var replayResult = DeserializeResult<RefreshCommercialExceptionsResult>(replay.ResultJson);
                await transaction.CommitAsync(cancellationToken);
                return replayResult;
            }

            var reconciledAtUtc = DateTime.UtcNow;
            var candidates = await DetectAsync(businessUnitId, reconciledAtUtc, cancellationToken);
            var existing = await db.CommercialExceptionCases
                .Where(x => x.BusinessUnitId == businessUnitId)
                .OrderBy(x => x.ExceptionKey)
                .ToListAsync(cancellationToken);
            var byKey = existing.ToDictionary(x => x.ExceptionKey, StringComparer.Ordinal);
            var currentKeys = candidates.Select(x => x.ExceptionKey).ToHashSet(StringComparer.Ordinal);
            var pendingEvents = new List<PendingEvent>();
            var detected = 0;
            var reopened = 0;
            var refreshed = 0;
            var resolved = 0;

            foreach (var candidate in candidates)
            {
                if (!byKey.TryGetValue(candidate.ExceptionKey, out var exception))
                {
                    exception = NewException(businessUnitId, candidate, reconciledAtUtc);
                    db.CommercialExceptionCases.Add(exception);
                    byKey.Add(candidate.ExceptionKey, exception);
                    pendingEvents.Add(new PendingEvent(
                        exception, null, 0, CommercialExceptionStatus.Open, 1,
                        "DETECTED", "Authoritative source condition detected.",
                        "CommercialException.Detected"));
                    detected++;
                    continue;
                }

                EnsureSourceLineage(exception, candidate);
                var previousStatus = exception.Status;
                var previousVersion = exception.Version;
                var wasReopened = previousStatus is CommercialExceptionStatus.Resolved or CommercialExceptionStatus.Dismissed;
                var materiallyChanged = !MateriallyMatches(exception, candidate);
                if (wasReopened || materiallyChanged)
                {
                    ApplyCandidate(exception, candidate, reconciledAtUtc);
                    exception.Status = wasReopened ? CommercialExceptionStatus.Open : previousStatus;
                    exception.ResolvedAtUtc = null;
                    exception.Version = previousVersion + 1;
                    pendingEvents.Add(new PendingEvent(
                        exception, previousStatus, previousVersion, exception.Status, exception.Version,
                        wasReopened ? "REOPENED" : "REFRESHED",
                        wasReopened
                            ? "Authoritative source condition is active again."
                            : "Authoritative source material changed while the condition remained active.",
                        wasReopened ? "CommercialException.Reopened" : "CommercialException.Refreshed"));
                }

                if (wasReopened) reopened++;
                else refreshed++;
            }

            foreach (var exception in existing.Where(x =>
                         x.Status is CommercialExceptionStatus.Open or CommercialExceptionStatus.Acknowledged &&
                         !currentKeys.Contains(x.ExceptionKey)))
            {
                var previousStatus = exception.Status;
                var previousVersion = exception.Version;
                exception.Status = CommercialExceptionStatus.Resolved;
                exception.ResolvedAtUtc = reconciledAtUtc;
                exception.Version = previousVersion + 1;
                pendingEvents.Add(new PendingEvent(
                    exception, previousStatus, previousVersion,
                    CommercialExceptionStatus.Resolved, exception.Version,
                    "SOURCE_RESOLVED", "Authoritative source condition is no longer active.",
                    "CommercialException.Resolved"));
                resolved++;
            }

            var result = new RefreshCommercialExceptionsResult(
                detected, reopened, refreshed, resolved, reconciledAtUtc, RuleVersion);

            for (var index = 0; index < pendingEvents.Count; index++)
            {
                var pending = pendingEvents[index];
                var eventIdempotencyKey = index == 0
                    ? normalized.IdempotencyKey
                    : DerivedIdempotencyKey(normalized.IdempotencyKey, pending.Exception.ExceptionKey);
                AppendEvent(
                    pending.Exception,
                    pending.FromStatus,
                    pending.FromVersion,
                    pending.ToStatus,
                    pending.ToVersion,
                    pending.ActionCode,
                    pending.Reason,
                    normalized.ActorId,
                    reconciledAtUtc,
                    normalized.CorrelationId,
                    eventIdempotencyKey,
                    requestHash,
                    pending.EventType);
            }

            AppendOperation(
                businessUnitId,
                "Refresh",
                null,
                normalized.IdempotencyKey,
                requestHash,
                normalized.CorrelationId,
                normalized.ActorId,
                result,
                reconciledAtUtc);

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                throw new CommercialExceptionConflictException(
                    "Commercial exceptions changed during reconciliation. Refresh and retry.");
            }
            catch (DbUpdateException)
            {
                throw new CommercialExceptionConflictException(
                    "Commercial exception reconciliation conflicted with another request.");
            }

            return result;
        });
    }

    public Task<CommercialExceptionItem> TransitionAsync(
        long businessUnitId,
        long commercialExceptionId,
        TransitionCommercialExceptionCommand command,
        CommercialExceptionAccessScope scope,
        CancellationToken cancellationToken)
    {
        businessUnitId = RequireTenant(businessUnitId);
        if (commercialExceptionId <= 0)
            throw new ArgumentOutOfRangeException(nameof(commercialExceptionId));
        ValidateScope(scope);
        ValidateTransition(command);
        var normalized = command with
        {
            ActionCode = command.ActionCode.Trim(),
            Reason = command.Reason.Trim(),
            CorrelationId = command.CorrelationId.Trim(),
            IdempotencyKey = command.IdempotencyKey.Trim(),
            ActorId = command.ActorId.Trim()
        };
        var requestHash = Hash(new
        {
            operation = "transition",
            businessUnitId,
            commercialExceptionId,
            normalized.ExpectedVersion,
            targetStatus = normalized.TargetStatus.ToString(),
            normalized.ActionCode,
            normalized.Reason,
            normalized.ActorId
        });
        var strategy = db.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted, cancellationToken);
            await AcquireLockAsync($"commercial-exceptions:{businessUnitId}", cancellationToken);

            var exceptionQuery = db.CommercialExceptionCases
                .Where(x => x.BusinessUnitId == businessUnitId && x.Id == commercialExceptionId);
            if (!scope.TenantWide)
                exceptionQuery = exceptionQuery.Where(x => x.OwnerUserId == scope.OwnerUserId!.Value);
            var exception = await exceptionQuery.SingleOrDefaultAsync(cancellationToken)
                ?? throw new CommercialExceptionNotFoundException("Commercial exception was not found in the permitted scope.");

            var replay = await db.CommercialExceptionOperations.AsNoTracking()
                .SingleOrDefaultAsync(x =>
                    x.BusinessUnitId == businessUnitId &&
                    x.IdempotencyKey == normalized.IdempotencyKey,
                    cancellationToken);
            if (replay is not null)
            {
                EnsureOperationReplay(replay, "Transition", commercialExceptionId, requestHash);
                var replayItem = DeserializeResult<CommercialExceptionItem>(replay.ResultJson);
                await transaction.CommitAsync(cancellationToken);
                return replayItem;
            }

            if (exception.Version != normalized.ExpectedVersion)
                throw new CommercialExceptionConflictException(
                    "Commercial exception changed. Refresh and retry with the current version.");
            if (!Allowed(exception.Status, normalized.TargetStatus))
                throw new CommercialExceptionConflictException(
                    $"Transition from {exception.Status} to {normalized.TargetStatus} is not allowed.");

            var occurredAtUtc = DateTime.UtcNow;
            var fromStatus = exception.Status;
            var fromVersion = exception.Version;
            exception.Status = normalized.TargetStatus;
            exception.Version = fromVersion + 1;
            exception.ResolvedAtUtc = normalized.TargetStatus is CommercialExceptionStatus.Resolved or CommercialExceptionStatus.Dismissed
                ? occurredAtUtc
                : null;
            AppendEvent(
                exception,
                fromStatus,
                fromVersion,
                exception.Status,
                exception.Version,
                normalized.ActionCode,
                normalized.Reason,
                normalized.ActorId,
                occurredAtUtc,
                normalized.CorrelationId,
                normalized.IdempotencyKey,
                requestHash,
                "CommercialException.StatusChanged");
            var result = await ToItemAsync(exception, occurredAtUtc, cancellationToken);
            AppendOperation(
                businessUnitId,
                "Transition",
                exception.Id,
                normalized.IdempotencyKey,
                requestHash,
                normalized.CorrelationId,
                normalized.ActorId,
                result,
                occurredAtUtc);

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                throw new CommercialExceptionConflictException(
                    "Commercial exception changed. Refresh and retry with the current version.");
            }
            catch (DbUpdateException)
            {
                throw new CommercialExceptionConflictException(
                    "Commercial exception transition conflicted with another request.");
            }

            return result;
        });
    }

    private async Task<IReadOnlyList<Detection>> DetectAsync(
        long businessUnitId,
        DateTime reconciledAtUtc,
        CancellationToken cancellationToken)
    {
        var detections = new List<Detection>();
        var unassigned = await (
                from workItem in db.Set<UnassignedWorkItem>().AsNoTracking()
                join lead in db.Leads.AsNoTracking()
                    on new { workItem.BusinessUnitId, Id = workItem.LeadId }
                    equals new { lead.BusinessUnitId, lead.Id }
                where workItem.BusinessUnitId == businessUnitId &&
                      (workItem.Status == WorkItemStatus.Open || workItem.Status == WorkItemStatus.Claimed)
                select new
                {
                    WorkItem = workItem,
                    lead.CommercialCaseId,
                    lead.CommercialCaseReference
                })
            .ToListAsync(cancellationToken);

        foreach (var row in unassigned)
        {
            if (row.CommercialCaseId <= 0 || string.IsNullOrWhiteSpace(row.CommercialCaseReference))
                throw new CommercialExceptionConflictException(
                    $"Unassigned work item {row.WorkItem.Id} has no canonical commercial case identity.");
            detections.Add(UnassignedDetection(
                row.WorkItem, row.CommercialCaseId, row.CommercialCaseReference, reconciledAtUtc));
        }

        var followUps = await db.FollowUpTasks.AsNoTracking()
            .Where(x =>
                x.BusinessUnitId == businessUnitId &&
                (x.Status == FollowUpStatus.Open || x.Status == FollowUpStatus.InProgress) &&
                x.DueAtUtc < reconciledAtUtc)
            .ToListAsync(cancellationToken);
        var supported = followUps
            .Select(x => (Task: x, Type: SupportedAggregateType(x.AggregateType)))
            .Where(x => x.Type is not null)
            .ToArray();
        var leadIds = supported.Where(x => x.Type == CommercialAggregateType.Lead)
            .Select(x => x.Task.AggregateId).Distinct().ToArray();
        var rfqIds = supported.Where(x => x.Type == CommercialAggregateType.Rfq)
            .Select(x => x.Task.AggregateId).Distinct().ToArray();
        var quoteIds = supported.Where(x => x.Type == CommercialAggregateType.Quote)
            .Select(x => x.Task.AggregateId).Distinct().ToArray();
        var orderIds = supported.Where(x => x.Type == CommercialAggregateType.Order)
            .Select(x => x.Task.AggregateId).Distinct().ToArray();
        var leadIdentities = await db.Leads.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && leadIds.Contains(x.Id))
            .Select(x => new AggregateIdentity(x.Id, x.CommercialCaseId, x.CommercialCaseReference))
            .ToDictionaryAsync(x => x.AggregateId, cancellationToken);
        var rfqIdentities = await db.Rfqs.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && rfqIds.Contains(x.Id))
            .Select(x => new AggregateIdentity(x.Id, x.CommercialCaseId ?? 0, x.NexoraSerial ?? string.Empty))
            .ToDictionaryAsync(x => x.AggregateId, cancellationToken);
        var quoteIdentities = await db.Quotes.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && quoteIds.Contains(x.Id))
            .Select(x => new AggregateIdentity(x.Id, x.CommercialCaseId ?? 0, x.NexoraSerial ?? string.Empty))
            .ToDictionaryAsync(x => x.AggregateId, cancellationToken);
        var orderIdentities = await db.Orders.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && orderIds.Contains(x.Id))
            .Select(x => new AggregateIdentity(x.Id, x.CommercialCaseId ?? 0, x.NexoraSerial ?? string.Empty))
            .ToDictionaryAsync(x => x.AggregateId, cancellationToken);
        var shipmentIds = supported.Where(x => x.Type == CommercialAggregateType.Shipment)
            .Select(x => x.Task.AggregateId).Distinct().ToArray();
        var shipmentIdentities = await db.Shipments.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && shipmentIds.Contains(x.Id))
            .Select(x => new AggregateIdentity(x.Id, x.CommercialCaseId ?? 0, x.NexoraSerial ?? string.Empty))
            .ToDictionaryAsync(x => x.AggregateId, cancellationToken);

        foreach (var row in supported)
        {
            var identities = row.Type switch
            {
                CommercialAggregateType.Lead => leadIdentities,
                CommercialAggregateType.Rfq => rfqIdentities,
                CommercialAggregateType.Quote => quoteIdentities,
                CommercialAggregateType.Order => orderIdentities,
                CommercialAggregateType.Shipment => shipmentIdentities,
                _ => throw new InvalidOperationException("Unsupported aggregate type reached reconciliation.")
            };
            if (!identities.TryGetValue(row.Task.AggregateId, out var identity) ||
                identity.CommercialCaseId <= 0 || string.IsNullOrWhiteSpace(identity.NexoraSerial))
                throw new CommercialExceptionConflictException(
                    $"Follow-up task {row.Task.Id} cannot be resolved to a canonical {row.Type} commercial case.");
            detections.Add(FollowUpDetection(row.Task, row.Type, identity, reconciledAtUtc));
        }

        detections.AddRange(await DetectDeliveryShortfallsAsync(
            businessUnitId, reconciledAtUtc, cancellationToken));

        var caseIds = detections.Select(x => x.CommercialCaseId).Distinct().ToArray();
        var commercialCases = await db.CommercialCases.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && caseIds.Contains(x.Id))
            .Select(x => new { x.Id, x.MasterReference })
            .ToDictionaryAsync(x => x.Id, x => x.MasterReference, cancellationToken);
        foreach (var detection in detections)
        {
            if (!commercialCases.TryGetValue(detection.CommercialCaseId, out var serial) ||
                !string.Equals(serial, detection.NexoraSerial, StringComparison.Ordinal))
                throw new CommercialExceptionConflictException(
                    $"Source {detection.SourceType} {detection.SourceId} has an invalid commercial case identity.");
        }

        return detections.OrderBy(x => x.ExceptionKey, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Gate 7 / FR-DLM-07. A customer took fewer units than the delivery note declared, and nobody
    /// has decided whether to re-supply or credit.
    ///
    /// <para><b>The undecided-ness is the condition, not the shortfall.</b> A shortfall is a
    /// permanent historical fact — the customer refused two of ten cartons on the 14th and always
    /// will have. If that were the detection condition the case could never close: the refresh
    /// sweep would reopen it the moment anybody resolved it, and the exception centre would fill
    /// with rows nobody could clear. So the condition is "a shortfall with no commercial decision
    /// recorded against it", which stops being true exactly when somebody records one. The framework
    /// then closes the case itself with SOURCE_RESOLVED, and "resolved" means what the product owner
    /// said it means: the commercial decision was taken. There is no investigation to conclude and
    /// no remediation to chase.</para>
    ///
    /// <para><b>Deliberately not detected: liability, cause or carrier fault.</b> Damage in transit
    /// is the carrier's process, run outside this system. What is recorded here is the commercial
    /// consequence — the units are not invoiceable, the order line is not fulfilled, and somebody
    /// has to choose.</para>
    ///
    /// <para><b>Named gap.</b> A shipment whose order never came from a lead carries no commercial
    /// case (<c>Shipment.CommercialCaseId</c> is nullable by design), and this framework's case
    /// requires one. Those shortfalls are skipped here rather than given an invented case id; they
    /// are still recorded in full on the proof line and still cap the invoice, so nothing is lost
    /// from the commercial record — only from this queue. Recorded in the gate report.</para>
    /// </summary>
    private async Task<IReadOnlyList<Detection>> DetectDeliveryShortfallsAsync(
        long businessUnitId, DateTime reconciledAtUtc, CancellationToken cancellationToken)
    {
        var shortfalls = await (
                from line in db.Set<Delivery.DeliveryProofLine>().AsNoTracking()
                join shipment in db.Shipments.AsNoTracking()
                    on new { line.BusinessUnitId, Id = line.ShipmentId }
                    equals new { shipment.BusinessUnitId, shipment.Id }
                where line.BusinessUnitId == businessUnitId
                      && line.AcceptedQuantity < line.DespatchedQuantity
                      && !db.Set<Delivery.DeliveryShortfallDecision>().Any(d =>
                          d.BusinessUnitId == businessUnitId && d.DeliveryProofLineId == line.Id)
                select new
                {
                    Line = line,
                    shipment.ShipmentNo,
                    shipment.CommercialCaseId,
                    shipment.NexoraSerial,
                    RecordedOn = db.Set<Delivery.DeliveryProof>()
                        .Where(p => p.BusinessUnitId == businessUnitId && p.Id == line.DeliveryProofId)
                        .Select(p => p.ReceivedOn).FirstOrDefault()
                })
            .ToListAsync(cancellationToken);

        var detections = new List<Detection>();
        foreach (var row in shortfalls)
        {
            if (row.CommercialCaseId is not > 0 || string.IsNullOrWhiteSpace(row.NexoraSerial))
                continue; // named gap — see the remarks

            var refused = row.Line.DespatchedQuantity - row.Line.AcceptedQuantity;
            // A whole consignment line refused is a different conversation from two units short.
            var severity = row.Line.AcceptedQuantity == 0
                ? CommercialExceptionSeverity.Critical
                : row.Line.ExceptionReasonCode == Delivery.DeliveryExceptionReasons.Rejected
                    ? CommercialExceptionSeverity.High
                    : CommercialExceptionSeverity.Medium;

            detections.Add(new Detection(
                $"DeliveryShortfall:{row.Line.Id}",
                CommercialExceptionType.DeliveryShortfall,
                "DeliveryProofLine",
                row.Line.Id,
                1,
                row.CommercialCaseId.Value,
                row.NexoraSerial,
                null,
                null,
                null,
                row.Line.Id,
                severity,
                row.Line.ExceptionReasonCode ?? Delivery.DeliveryExceptionReasons.ShortShipment,
                "Delivery shortfall awaiting a commercial decision",
                $"{refused} of {row.Line.DespatchedQuantity} units on delivery note {row.ShipmentNo} "
                + $"were not accepted ({row.Line.ExceptionReasonCode}). The order line is not "
                + "fulfilled and the shortfall is not invoiceable.",
                "DECIDE_RESUPPLY_OR_CREDIT",
                // Three working days. A customer waiting to hear whether they get goods or money
                // back is the clock that matters, not an internal process one.
                (row.RecordedOn == default ? reconciledAtUtc : row.RecordedOn).AddDays(3),
                JsonSerializer.Serialize(new
                {
                    authoritative = true,
                    sourceType = "DeliveryProofLine",
                    sourceId = row.Line.Id,
                    shipmentId = row.Line.ShipmentId,
                    shipmentNo = row.ShipmentNo,
                    orderId = row.Line.OrderId,
                    orderItemId = row.Line.OrderItemId,
                    despatchedQuantity = row.Line.DespatchedQuantity,
                    acceptedQuantity = row.Line.AcceptedQuantity,
                    refusedQuantity = refused,
                    reasonCode = row.Line.ExceptionReasonCode,
                    reasonNote = row.Line.ExceptionNote
                }, JsonOptions)));
        }

        return detections;
    }

    private static Detection UnassignedDetection(
        UnassignedWorkItem source,
        long commercialCaseId,
        string nexoraSerial,
        DateTime reconciledAtUtc)
        => new(
            $"UnassignedLead:{source.Id}",
            CommercialExceptionType.UnassignedLead,
            "UnassignedWorkItem",
            source.Id,
            source.Version,
            commercialCaseId,
            nexoraSerial,
            null,
            null,
            source.Id,
            null,
            source.SlaDueOn < reconciledAtUtc
                ? CommercialExceptionSeverity.High
                : CommercialExceptionSeverity.Medium,
            string.IsNullOrWhiteSpace(source.ReasonCode) ? "UNASSIGNED_LEAD" : source.ReasonCode,
            "Lead requires assignment",
            $"Lead {nexoraSerial} remains in the governed unassigned routing queue.",
            "ASSIGN_OWNER",
            source.SlaDueOn,
            JsonSerializer.Serialize(new
            {
                authoritative = true,
                sourceType = "UnassignedWorkItem",
                sourceId = source.Id,
                sourceVersion = source.Version,
                sourceStatus = source.Status.ToString(),
                source.LeadId,
                source.RoutingDecisionId,
                source.EnteredOn,
                source.SlaDueOn,
                source.Priority,
                source.ReasonCode,
                source.RequiredAction
            }, JsonOptions));

    private static Detection FollowUpDetection(
        FollowUpTask source,
        string aggregateType,
        AggregateIdentity identity,
        DateTime reconciledAtUtc)
    {
        var overdueBy = reconciledAtUtc - source.DueAtUtc;
        var severity = overdueBy >= TimeSpan.FromDays(7)
            ? CommercialExceptionSeverity.Critical
            : overdueBy >= TimeSpan.FromDays(2)
                ? CommercialExceptionSeverity.High
                : CommercialExceptionSeverity.Medium;
        return new Detection(
            $"OverdueFollowUp:{source.Id}",
            CommercialExceptionType.OverdueFollowUp,
            "FollowUpTask",
            source.Id,
            source.Version,
            identity.CommercialCaseId,
            identity.NexoraSerial,
            source.AssignedToUserId,
            source.Id,
            null,
            null,
            severity,
            "FOLLOW_UP_OVERDUE",
            "Follow-up is overdue",
            $"{aggregateType} follow-up for {identity.NexoraSerial} is overdue.",
            "COMPLETE_OR_RESCHEDULE_FOLLOW_UP",
            source.DueAtUtc,
            JsonSerializer.Serialize(new
            {
                authoritative = true,
                sourceType = "FollowUpTask",
                sourceId = source.Id,
                sourceVersion = source.Version,
                sourceStatus = source.Status.ToString(),
                aggregateType,
                source.AggregateId,
                source.AssignedToUserId,
                source.DueAtUtc,
                source.Priority,
                source.PurposeCode
            }, JsonOptions));
    }

    private static CommercialExceptionCase NewException(
        long businessUnitId,
        Detection candidate,
        DateTime detectedAtUtc)
    {
        var exception = new CommercialExceptionCase
        {
            BusinessUnitId = businessUnitId,
            ExceptionKey = candidate.ExceptionKey,
            ExceptionType = candidate.ExceptionType,
            Status = CommercialExceptionStatus.Open,
            FirstDetectedAtUtc = detectedAtUtc,
            Version = 1
        };
        ApplyCandidate(exception, candidate, detectedAtUtc);
        return exception;
    }

    private static void ApplyCandidate(
        CommercialExceptionCase exception,
        Detection candidate,
        DateTime detectedAtUtc)
    {
        exception.CommercialCaseId = candidate.CommercialCaseId;
        exception.NexoraSerial = candidate.NexoraSerial;
        exception.SourceType = candidate.SourceType;
        exception.SourceId = candidate.SourceId;
        exception.SourceVersion = candidate.SourceVersion;
        exception.FollowUpTaskId = candidate.FollowUpTaskId;
        exception.UnassignedWorkItemId = candidate.UnassignedWorkItemId;
        exception.DeliveryProofLineId = candidate.DeliveryProofLineId;
        exception.OwnerUserId = candidate.OwnerUserId;
        exception.Severity = candidate.Severity;
        exception.ReasonCode = candidate.ReasonCode;
        exception.Title = candidate.Title;
        exception.Summary = candidate.Summary;
        exception.RecommendedActionCode = candidate.RecommendedActionCode;
        exception.EvidenceJson = candidate.EvidenceJson;
        exception.RuleVersion = RuleVersion;
        exception.LastDetectedAtUtc = detectedAtUtc;
        exception.SlaDueAtUtc = candidate.SlaDueAtUtc;
    }

    private static void EnsureSourceLineage(CommercialExceptionCase exception, Detection candidate)
    {
        if (exception.ExceptionType != candidate.ExceptionType ||
            !string.Equals(exception.SourceType, candidate.SourceType, StringComparison.Ordinal) ||
            exception.SourceId != candidate.SourceId ||
            exception.FollowUpTaskId != candidate.FollowUpTaskId ||
            exception.UnassignedWorkItemId != candidate.UnassignedWorkItemId ||
            exception.DeliveryProofLineId != candidate.DeliveryProofLineId ||
            exception.CommercialCaseId != candidate.CommercialCaseId ||
            !string.Equals(exception.NexoraSerial, candidate.NexoraSerial, StringComparison.Ordinal))
            throw new CommercialExceptionConflictException(
                $"Commercial exception {exception.Id} has contradictory source lineage.");
    }

    private static bool MateriallyMatches(CommercialExceptionCase exception, Detection candidate)
        => exception.SourceVersion == candidate.SourceVersion
           && exception.OwnerUserId == candidate.OwnerUserId
           && exception.Severity == candidate.Severity
           && string.Equals(exception.ReasonCode, candidate.ReasonCode, StringComparison.Ordinal)
           && string.Equals(exception.Title, candidate.Title, StringComparison.Ordinal)
           && string.Equals(exception.Summary, candidate.Summary, StringComparison.Ordinal)
           && string.Equals(exception.RecommendedActionCode, candidate.RecommendedActionCode, StringComparison.Ordinal)
           && string.Equals(exception.EvidenceJson, candidate.EvidenceJson, StringComparison.Ordinal)
           && string.Equals(exception.RuleVersion, RuleVersion, StringComparison.Ordinal)
           && exception.SlaDueAtUtc == candidate.SlaDueAtUtc;

    private void AppendEvent(
        CommercialExceptionCase exception,
        CommercialExceptionStatus? fromStatus,
        long fromVersion,
        CommercialExceptionStatus toStatus,
        long toVersion,
        string actionCode,
        string reason,
        string actorId,
        DateTime occurredAtUtc,
        string correlationId,
        string idempotencyKey,
        string requestHash,
        string eventType)
    {
        var exceptionEvent = new CommercialExceptionEvent
        {
            BusinessUnitId = exception.BusinessUnitId,
            CommercialExceptionCase = exception,
            FromStatus = fromStatus,
            ToStatus = toStatus,
            FromVersion = fromVersion,
            ToVersion = toVersion,
            ActionCode = actionCode,
            Reason = reason,
            ActorId = actorId,
            OccurredAtUtc = occurredAtUtc,
            CorrelationId = correlationId,
            IdempotencyKey = idempotencyKey,
            RequestHash = requestHash
        };
        var outbox = new CommercialExceptionOutboxMessage
        {
            BusinessUnitId = exception.BusinessUnitId,
            CommercialExceptionEvent = exceptionEvent,
            EventType = eventType,
            Payload = JsonSerializer.Serialize(new
            {
                exception.ExceptionKey,
                exception.ExceptionType,
                exception.CommercialCaseId,
                exception.NexoraSerial,
                exception.SourceType,
                exception.SourceId,
                fromStatus,
                toStatus,
                fromVersion,
                toVersion,
                actionCode,
                actorId,
                occurredAtUtc,
                correlationId,
                ruleVersion = exception.RuleVersion
            }, JsonOptions),
            OccurredAtUtc = occurredAtUtc,
            AvailableAtUtc = occurredAtUtc,
            AttemptCount = 0
        };
        db.CommercialExceptionEvents.Add(exceptionEvent);
        db.CommercialExceptionOutboxMessages.Add(outbox);
    }

    private void AppendOperation<T>(
        long businessUnitId,
        string operationType,
        long? commercialExceptionCaseId,
        string idempotencyKey,
        string requestHash,
        string correlationId,
        string actorId,
        T result,
        DateTime occurredAtUtc)
    {
        db.Set<CommercialExceptionOperation>().Add(new CommercialExceptionOperation
        {
            BusinessUnitId = businessUnitId,
            OperationType = operationType,
            CommercialExceptionCaseId = commercialExceptionCaseId,
            IdempotencyKey = idempotencyKey,
            RequestHash = requestHash,
            CorrelationId = correlationId,
            ActorId = actorId,
            ResultJson = JsonSerializer.Serialize(result, JsonOptions),
            OccurredAtUtc = occurredAtUtc
        });
    }

    private async Task<CommercialExceptionItem> ToItemAsync(
        CommercialExceptionCase exception,
        DateTime generatedAtUtc,
        CancellationToken cancellationToken)
    {
        string? ownerName = null;
        if (exception.OwnerUserId.HasValue)
        {
            ownerName = await db.Users.AsNoTracking()
                .Where(x => x.Buid == exception.BusinessUnitId && x.Id == exception.OwnerUserId.Value)
                .Select(x => (x.FirstName + " " + x.LastName).Trim())
                .SingleOrDefaultAsync(cancellationToken);
        }
        return ToItem(exception, generatedAtUtc, ownerName);
    }

    private static CommercialExceptionItem ToItem(
        CommercialExceptionCase exception,
        DateTime generatedAtUtc,
        string? ownerName)
        => new(
            exception.Id,
            exception.CommercialCaseId,
            exception.NexoraSerial,
            exception.ExceptionType,
            exception.Severity,
            exception.Status,
            exception.Title,
            exception.Summary,
            exception.ReasonCode,
            exception.RecommendedActionCode,
            exception.SourceType,
            exception.SourceId,
            exception.SourceVersion,
            exception.OwnerUserId,
            ownerName,
            exception.FirstDetectedAtUtc,
            exception.LastDetectedAtUtc,
            exception.SlaDueAtUtc,
            exception.SlaDueAtUtc < generatedAtUtc &&
            exception.Status is CommercialExceptionStatus.Open or CommercialExceptionStatus.Acknowledged,
            exception.EvidenceJson,
            exception.RuleVersion,
            exception.Version);

    private async Task<Dictionary<long, string>> OwnerNamesAsync(
        long businessUnitId,
        IEnumerable<long> ownerUserIds,
        CancellationToken cancellationToken)
    {
        var ids = ownerUserIds.Distinct().ToArray();
        return await db.Users.AsNoTracking()
            .Where(x => x.Buid == businessUnitId && ids.Contains(x.Id))
            .Select(x => new { x.Id, Name = (x.FirstName + " " + x.LastName).Trim() })
            .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);
    }

    private async Task<IReadOnlyList<CommercialExceptionSourceCoverage>> SourceCoverageAsync(
        long businessUnitId,
        CancellationToken cancellationToken)
    {
        var unassignedAvailable = await CanReadSourceAsync(
            db.Set<UnassignedWorkItem>().AsNoTracking().Where(x => x.BusinessUnitId == businessUnitId),
            cancellationToken);
        var followUpAvailable = await CanReadSourceAsync(
            db.FollowUpTasks.AsNoTracking().Where(x => x.BusinessUnitId == businessUnitId),
            cancellationToken);
        // Gate 7 / FR-DLM-07. Reported alongside the other two rather than assumed reachable: the
        // delivery tables are new, and a coverage panel that stays silent about a source it cannot
        // read is the "blank that reads like a loading state" the wiring contract names.
        var deliveryAvailable = await CanReadSourceAsync(
            db.Set<Delivery.DeliveryProofLine>().AsNoTracking().Where(x => x.BusinessUnitId == businessUnitId),
            cancellationToken);
        return
        [
            Coverage("UnassignedWorkItem", unassignedAvailable,
                "Governed unassigned-routing work items are reachable.",
                "The governed unassigned-routing source could not be queried."),
            Coverage("FollowUpTask", followUpAvailable,
                "Governed commercial follow-up tasks are reachable.",
                "The governed follow-up source could not be queried."),
            Coverage("DeliveryProofLine", deliveryAvailable,
                "Confirmed delivery lines are reachable.",
                "The delivery confirmation source could not be queried.")
        ];
    }

    private static async Task<bool> CanReadSourceAsync<T>(
        IQueryable<T> source,
        CancellationToken cancellationToken)
    {
        try
        {
            _ = await source.Select(_ => 1).Take(1).ToListAsync(cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException)
        {
            return false;
        }
    }

    private static CommercialExceptionSourceCoverage Coverage(
        string sourceType,
        bool isAvailable,
        string availableDetail,
        string unavailableDetail)
        => new(sourceType, isAvailable, isAvailable ? "available" : "unavailable",
            isAvailable ? availableDetail : unavailableDetail);

    private async Task AcquireLockAsync(string lockKey, CancellationToken cancellationToken)
    {
        if (db.Database.IsNpgsql())
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))",
                cancellationToken);
    }

    private long RequireTenant(long requestedBusinessUnitId)
    {
        if (!tenantContext.BusinessUnitId.HasValue || tenantContext.BusinessUnitId.Value <= 0)
            throw new UnauthorizedAccessException("An authenticated tenant context is required.");
        if (requestedBusinessUnitId <= 0 || requestedBusinessUnitId != tenantContext.BusinessUnitId.Value)
            throw new UnauthorizedAccessException("Requested business unit does not match the authenticated tenant.");
        return requestedBusinessUnitId;
    }

    private static void ValidateScope(CommercialExceptionAccessScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.TenantWide && scope.OwnerUserId.HasValue)
            throw new ArgumentException("Tenant-wide scope cannot specify an owner user ID.", nameof(scope));
        if (!scope.TenantWide && (!scope.OwnerUserId.HasValue || scope.OwnerUserId.Value <= 0))
            throw new ArgumentException("Individual scope requires a positive owner user ID.", nameof(scope));
    }

    private static void ValidateRefresh(RefreshCommercialExceptionsCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        Required(command.CorrelationId, nameof(command.CorrelationId), 100);
        Required(command.IdempotencyKey, nameof(command.IdempotencyKey), 160);
        Required(command.ActorId, nameof(command.ActorId), 160);
    }

    private static void ValidateTransition(TransitionCommercialExceptionCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.ExpectedVersion < 0)
            throw new ArgumentOutOfRangeException(nameof(command.ExpectedVersion));
        if (!Enum.IsDefined(command.TargetStatus))
            throw new ArgumentOutOfRangeException(nameof(command.TargetStatus));
        Required(command.ActionCode, nameof(command.ActionCode), 100);
        var expectedActionCode = ExpectedActionCode(command.TargetStatus);
        if (!string.Equals(command.ActionCode.Trim(), expectedActionCode, StringComparison.Ordinal))
            throw new ArgumentException(
                $"ActionCode must be {expectedActionCode} for target status {command.TargetStatus}.",
                nameof(command.ActionCode));
        if (command.TargetStatus is CommercialExceptionStatus.Resolved or CommercialExceptionStatus.Dismissed)
            Required(command.Reason, nameof(command.Reason), 1000);
        else if (command.Reason?.Length > 1000)
            throw new ArgumentException("Reason cannot exceed 1000 characters.", nameof(command.Reason));
        Required(command.CorrelationId, nameof(command.CorrelationId), 100);
        Required(command.IdempotencyKey, nameof(command.IdempotencyKey), 160);
        Required(command.ActorId, nameof(command.ActorId), 160);
    }

    private static void Required(string? value, string name, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{name} is required.", name);
        if (value.Trim().Length > maximumLength)
            throw new ArgumentException($"{name} cannot exceed {maximumLength} characters.", name);
    }

    private static bool Allowed(CommercialExceptionStatus from, CommercialExceptionStatus to)
        => (from, to) switch
        {
            (CommercialExceptionStatus.Open, CommercialExceptionStatus.Acknowledged or
                CommercialExceptionStatus.Resolved or CommercialExceptionStatus.Dismissed) => true,
            (CommercialExceptionStatus.Acknowledged, CommercialExceptionStatus.Open or
                CommercialExceptionStatus.Resolved or CommercialExceptionStatus.Dismissed) => true,
            (CommercialExceptionStatus.Resolved or CommercialExceptionStatus.Dismissed,
                CommercialExceptionStatus.Open) => true,
            _ => false
        };

    private static string ExpectedActionCode(CommercialExceptionStatus targetStatus)
        => targetStatus switch
        {
            CommercialExceptionStatus.Open => "REOPEN",
            CommercialExceptionStatus.Acknowledged => "ACKNOWLEDGE",
            CommercialExceptionStatus.Resolved => "RESOLVE",
            CommercialExceptionStatus.Dismissed => "DISMISS",
            _ => throw new ArgumentOutOfRangeException(nameof(targetStatus))
        };

    private static string? SupportedAggregateType(string aggregateType)
    {
        if (string.IsNullOrWhiteSpace(aggregateType)) return null;
        return aggregateType.Trim().ToUpperInvariant() switch
        {
            "LEAD" => CommercialAggregateType.Lead,
            "RFQ" => CommercialAggregateType.Rfq,
            "QUOTE" => CommercialAggregateType.Quote,
            "ORDER" => CommercialAggregateType.Order,
            // Gate 7. A follow-up on a despatch is now resolvable, because the shipment carries its
            // own commercial case; before this it fell through to null and the task was silently
            // dropped from the sweep — a follow-up nobody was ever chased for.
            "SHIPMENT" => CommercialAggregateType.Shipment,
            _ => null
        };
    }

    private static string DerivedIdempotencyKey(string baseKey, string exceptionKey)
    {
        var digest = Hash(new { baseKey, exceptionKey });
        var readablePrefix = baseKey.Length <= 95 ? baseKey : baseKey[..95];
        return $"{readablePrefix}:{digest}";
    }

    private static string Hash(object value)
        => Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JsonOptions))))
            .ToLowerInvariant();

    private static void EnsureReplayHash(string persistedHash, string requestHash)
    {
        var persisted = Encoding.ASCII.GetBytes(persistedHash);
        var requested = Encoding.ASCII.GetBytes(requestHash);
        if (persisted.Length != requested.Length ||
            !CryptographicOperations.FixedTimeEquals(persisted, requested))
            throw new CommercialExceptionConflictException(
                "The idempotency key was already used for a different request.");
    }

    private static void EnsureOperationReplay(
        CommercialExceptionOperation operation,
        string operationType,
        long? commercialExceptionCaseId,
        string requestHash)
    {
        EnsureReplayHash(operation.RequestHash, requestHash);
        if (!string.Equals(operation.OperationType, operationType, StringComparison.Ordinal) ||
            operation.CommercialExceptionCaseId != commercialExceptionCaseId)
            throw new CommercialExceptionConflictException(
                "The idempotency key was already used for a different commercial-exception operation.");
    }

    private static T DeserializeResult<T>(string resultJson)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(resultJson, JsonOptions)
                   ?? throw new JsonException("The result was empty.");
        }
        catch (JsonException)
        {
            throw new CommercialExceptionConflictException(
                "The persisted idempotency receipt could not be replayed safely.");
        }
    }

    private sealed record AggregateIdentity(long AggregateId, long CommercialCaseId, string NexoraSerial);

    private sealed record Detection(
        string ExceptionKey,
        CommercialExceptionType ExceptionType,
        string SourceType,
        long SourceId,
        long SourceVersion,
        long CommercialCaseId,
        string NexoraSerial,
        long? OwnerUserId,
        long? FollowUpTaskId,
        long? UnassignedWorkItemId,
        long? DeliveryProofLineId,
        CommercialExceptionSeverity Severity,
        string ReasonCode,
        string Title,
        string Summary,
        string RecommendedActionCode,
        DateTime SlaDueAtUtc,
        string EvidenceJson);

    private sealed record PendingEvent(
        CommercialExceptionCase Exception,
        CommercialExceptionStatus? FromStatus,
        long FromVersion,
        CommercialExceptionStatus ToStatus,
        long ToVersion,
        string ActionCode,
        string Reason,
        string EventType);
}
