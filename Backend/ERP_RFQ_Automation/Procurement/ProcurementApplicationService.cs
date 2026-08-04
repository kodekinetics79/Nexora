using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ERP_RFQ_Automation.Agent.Models;
using ERP_RFQ_Automation.Inventory;
using ERP_RFQ_Automation.Inventory.Commercial;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ERP_RFQ_Automation.Procurement;

public sealed class ProcurementApplicationService : IProcurementApplicationService
{
    private readonly ErpRfqAutomationContext _db;

    public ProcurementApplicationService(ErpRfqAutomationContext db) => _db = db;

    public async Task<SourcingCaseView> CreateOrOpenSourcingCaseAsync(
        CreateSourcingCaseCommand command, CancellationToken ct = default)
    {
        ValidateCommand(command.BusinessUnitId, command.IdempotencyKey, command.Actor, command.CorrelationId);
        ValidateSearchLimit(command.SearchLimit);
        if (command.RfqId <= 0 || command.RfqItemId <= 0)
            throw new ProcurementValidationException("RFQ and RFQ line identifiers must be positive.");

        var requestHash = Hash(new
        {
            command.RfqId,
            command.RfqItemId,
            command.SearchLimit,
            command.SourceEntireQuantity
        });
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var replay = await _db.SourcingCases.Include(x => x.Candidates).SingleOrDefaultAsync(x =>
                x.BusinessUnitId == command.BusinessUnitId
                && x.IdempotencyKey == command.IdempotencyKey.Trim(), ct);
            if (replay is not null)
            {
                EnsureReplay(replay.RequestHash, requestHash);
                var replayView = await ToSourcingCaseViewAsync(replay, ct);
                await tx.CommitAsync(ct);
                return replayView;
            }

            var rfq = await _db.Rfqs.SingleOrDefaultAsync(x => x.BusinessUnitId == command.BusinessUnitId
                && x.Id == command.RfqId, ct)
                ?? throw new ProcurementValidationException("RFQ was not found in the authenticated tenant.");
            if (string.IsNullOrWhiteSpace(rfq.NexoraSerial))
                throw new ProcurementConflictException("The RFQ must have an immutable Nexora Serial before sourcing.");
            var item = await _db.Rfqitems.SingleOrDefaultAsync(x => x.Id == command.RfqItemId
                && x.Rfqid == command.RfqId, ct)
                ?? throw new ProcurementValidationException("RFQ line was not found in the authenticated tenant.");
            var shortfall = await GetNetSourcingRequirementAsync(command.BusinessUnitId, item, ct);
            if (shortfall <= 0)
                throw new ProcurementConflictException("This RFQ line is fully covered and does not require a Sourcing Case.");

            var requested = (decimal)item.Quantity;
            var stock = Math.Max(0m, requested - shortfall);
            var unfulfilled = command.SourceEntireQuantity ? requested : shortfall;
            var identityKey = $"rfq:{rfq.Id}:line:{item.Id}";
            var demandLine = await _db.CommercialDemandLines.SingleOrDefaultAsync(x =>
                x.BusinessUnitId == command.BusinessUnitId && x.RfqItemId == item.Id, ct);
            var now = DateTime.UtcNow;
            if (demandLine is null)
            {
                demandLine = new CommercialDemandLine
                {
                    BusinessUnitId = command.BusinessUnitId,
                    RfqId = rfq.Id,
                    RfqItemId = item.Id,
                    NexoraSerial = rfq.NexoraSerial,
                    IdentityKey = identityKey,
                    CreatedOn = now,
                    CreatedBy = command.Actor.Trim()
                };
                _db.CommercialDemandLines.Add(demandLine);
                await _db.SaveChangesAsync(ct);
                AddEvent(command.BusinessUnitId, "CommercialDemandLine", demandLine.Id, 1,
                    "COMMERCIAL_DEMAND_LINE_ESTABLISHED", command.Actor, command.CorrelationId,
                    command.IdempotencyKey, JsonSerializer.Serialize(new
                    {
                        demandLine.RfqId,
                        demandLine.RfqItemId,
                        demandLine.NexoraSerial,
                        demandLine.IdentityKey
                    }), now);
            }
            else if (demandLine.RfqId != rfq.Id || demandLine.NexoraSerial != rfq.NexoraSerial
                || demandLine.IdentityKey != identityKey)
            {
                throw new ProcurementConflictException("The immutable commercial Demand Line identity does not match this RFQ line.");
            }

            var shortageDecisionKey = Hash(new
            {
                demandLine.Id,
                RequestedQuantity = requested,
                StockQuantity = stock,
                UnfulfilledQuantity = unfulfilled,
                item.ProductId,
                item.RequiredDesiredDate,
                command.SourceEntireQuantity
            });
            var existingDecision = await _db.SourcingCases.Include(x => x.Candidates).SingleOrDefaultAsync(x =>
                x.BusinessUnitId == command.BusinessUnitId
                && x.CommercialDemandLineId == demandLine.Id
                && x.ShortageDecisionKey == shortageDecisionKey, ct);
            if (existingDecision is not null)
            {
                var existingView = await ToSourcingCaseViewAsync(existingDecision, ct);
                await tx.CommitAsync(ct);
                return existingView;
            }

            var sourcingCase = new SourcingCase
            {
                BusinessUnitId = command.BusinessUnitId,
                CommercialDemandLineId = demandLine.Id,
                RfqId = rfq.Id,
                RfqItemId = item.Id,
                LeadId = rfq.LeadId,
                CustomerId = rfq.CustomerId,
                ProductId = item.ProductId,
                NexoraSerial = rfq.NexoraSerial,
                RequestedPartNumber = item.ManufacturerPartNumber ?? item.ItemMaterialCode,
                Manufacturer = item.ManufacturerName,
                Description = item.ProductShortDescription ?? item.ProductShortName ?? item.CommodityProduct
                    ?? item.ItemMaterialCode ?? $"RFQ line {item.Id}",
                UnitOfMeasure = item.UnitOfMeasure,
                RequestedQuantity = requested,
                StockQuantity = stock,
                UnfulfilledQuantity = unfulfilled,
                RequiredOn = item.RequiredDesiredDate,
                DeliveryLocation = item.StorageLocation,
                SearchLimit = command.SearchLimit,
                Status = SourcingCaseStatuses.InternalSearch,
                NextAction = "Review known supplier candidates",
                ShortageDecisionKey = shortageDecisionKey,
                IdempotencyKey = command.IdempotencyKey.Trim(),
                RequestHash = requestHash,
                CreatedOn = now,
                CreatedBy = command.Actor.Trim(),
                UpdatedOn = now,
                UpdatedBy = command.Actor.Trim()
            };
            _db.SourcingCases.Add(sourcingCase);
            await _db.SaveChangesAsync(ct);
            await RefreshCandidatesAsync(sourcingCase, command.SearchLimit, now, ct);
            sourcingCase.Status = sourcingCase.Candidates.Count == 0
                ? SourcingCaseStatuses.DiscoveryRequired : SourcingCaseStatuses.InternalSearch;
            sourcingCase.NextAction = sourcingCase.Candidates.Count == 0
                ? "Review discovery options" : "Select suppliers for outreach";
            AddEvent(command.BusinessUnitId, "SourcingCase", sourcingCase.Id, sourcingCase.Version,
                "SOURCING_CASE_CREATED", command.Actor, command.CorrelationId, command.IdempotencyKey,
                JsonSerializer.Serialize(new
                {
                    sourcingCase.CommercialDemandLineId,
                    sourcingCase.RfqId,
                    sourcingCase.RfqItemId,
                    sourcingCase.NexoraSerial,
                    sourcingCase.UnfulfilledQuantity,
                    CandidateCount = sourcingCase.Candidates.Count
                }), now);
            await _db.SaveChangesAsync(ct);
            var view = await ToSourcingCaseViewAsync(sourcingCase, ct);
            await tx.CommitAsync(ct);
            return view;
        });
    }

    public async Task<SourcingCaseView> GetSourcingCaseAsync(
        long businessUnitId, long sourcingCaseId, CancellationToken ct = default)
    {
        ValidateTenant(businessUnitId);
        var sourcingCase = await _db.SourcingCases.AsNoTracking().Include(x => x.Candidates)
            .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == sourcingCaseId, ct)
            ?? throw new ProcurementValidationException("Sourcing Case was not found in the authenticated tenant.");
        return await ToSourcingCaseViewAsync(sourcingCase, ct);
    }

    public async Task<SourcingCandidateSearchResult> SearchSourcingCandidatesAsync(
        SearchSourcingCandidatesCommand command, CancellationToken ct = default)
    {
        ValidateCommand(command.BusinessUnitId, command.IdempotencyKey, command.Actor, command.CorrelationId);
        ValidateSearchLimit(command.Limit);
        var requestHash = Hash(new { command.SourcingCaseId, command.Limit, command.ExpectedVersion });
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var sourcingCase = await _db.SourcingCases.Include(x => x.Candidates).SingleOrDefaultAsync(x =>
                x.BusinessUnitId == command.BusinessUnitId && x.Id == command.SourcingCaseId, ct)
                ?? throw new ProcurementValidationException("Sourcing Case was not found in the authenticated tenant.");
            var priorEvent = await _db.ProcurementEvents.AsNoTracking().SingleOrDefaultAsync(x =>
                x.BusinessUnitId == command.BusinessUnitId && x.EventType == "SUPPLIER_DISCOVERY_STARTED"
                && x.IdempotencyKey == command.IdempotencyKey.Trim(), ct);
            if (priorEvent is not null)
            {
                if (priorEvent.AggregateId != sourcingCase.Id || ExtractRequestHash(priorEvent.PayloadJson) != requestHash)
                    throw new ProcurementConflictException("The idempotency key was already used for a different candidate search.");
                var replaySnapshot = ExtractCandidateSearchSnapshot(priorEvent.PayloadJson)
                    ?? throw new ProcurementConflictException("The candidate-search audit snapshot is unavailable.");
                var replayCandidates = await ToCandidateViewsAsync(command.BusinessUnitId,
                    replaySnapshot.Candidates.Select(ToCandidateEntity), ct);
                await tx.CommitAsync(ct);
                return new SourcingCandidateSearchResult(sourcingCase.Id, replaySnapshot.RequestedLimit,
                    replayCandidates.Count, replaySnapshot.Version, true, replayCandidates);
            }
            if (sourcingCase.Version != command.ExpectedVersion)
                throw new ProcurementConflictException("The Sourcing Case changed; refresh before searching again.");
            if (sourcingCase.Status is SourcingCaseStatuses.Closed or SourcingCaseStatuses.Cancelled)
                throw new ProcurementConflictException("A closed or cancelled Sourcing Case cannot be searched.");
            if (await _db.Set<SupplierSolicitation>().AnyAsync(x =>
                    x.BusinessUnitId == command.BusinessUnitId && x.SourcingCaseId == sourcingCase.Id, ct))
                throw new ProcurementConflictException(
                    "Supplier candidates cannot be refreshed after outreach preparation has started.");

            var now = DateTime.UtcNow;
            var previousCandidates = sourcingCase.Candidates.Select(ToCandidateSnapshot).ToArray();
            _db.SourcingCaseCandidates.RemoveRange(sourcingCase.Candidates);
            sourcingCase.Candidates.Clear();
            await RefreshCandidatesAsync(sourcingCase, command.Limit, now, ct);
            sourcingCase.SearchLimit = command.Limit;
            sourcingCase.Status = sourcingCase.Candidates.Count == 0
                ? SourcingCaseStatuses.DiscoveryRequired : SourcingCaseStatuses.InternalSearch;
            sourcingCase.NextAction = sourcingCase.Candidates.Count == 0
                ? "Review discovery options" : "Select suppliers for outreach";
            sourcingCase.Version++;
            sourcingCase.UpdatedOn = now;
            sourcingCase.UpdatedBy = command.Actor.Trim();
            await _db.SaveChangesAsync(ct);
            AddEvent(command.BusinessUnitId, "SourcingCase", sourcingCase.Id, sourcingCase.Version,
                "SUPPLIER_DISCOVERY_STARTED", command.Actor, command.CorrelationId, command.IdempotencyKey,
                JsonSerializer.Serialize(new
                {
                    requestHash,
                    RequestedLimit = command.Limit,
                    ResultCount = sourcingCase.Candidates.Count,
                    Version = sourcingCase.Version,
                    PreviousCandidates = previousCandidates,
                    Candidates = sourcingCase.Candidates.Select(ToCandidateSnapshot).ToArray()
                }), now);
            await _db.SaveChangesAsync(ct);
            var candidates = await ToCandidateViewsAsync(command.BusinessUnitId, sourcingCase.Candidates, ct);
            await tx.CommitAsync(ct);
            return new SourcingCandidateSearchResult(sourcingCase.Id, command.Limit, candidates.Count,
                sourcingCase.Version, false, candidates);
        });
    }

    public async Task<PreparedSupplierRfqResult> PrepareSupplierRfqAsync(
        PrepareSupplierRfqCommand command, CancellationToken ct = default)
    {
        ValidateCommand(command.BusinessUnitId, command.IdempotencyKey, command.Actor, command.CorrelationId);
        ValidateSolicitationDueOn(command.DueOn);
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var sourcingCase = await _db.SourcingCases.Include(x => x.Candidates).SingleOrDefaultAsync(x =>
                x.BusinessUnitId == command.BusinessUnitId && x.Id == command.SourcingCaseId, ct)
                ?? throw new ProcurementValidationException("Sourcing Case was not found in the authenticated tenant.");
            var solicitationKey = $"{command.IdempotencyKey.Trim()}:supplier-rfq";
            var hash = Hash(new
            {
                command.SourcingCaseId,
                command.SupplierId,
                command.DueOn,
                command.ExpectedVersion
            });
            var replay = await _db.Set<SupplierSolicitation>().SingleOrDefaultAsync(x =>
                x.BusinessUnitId == command.BusinessUnitId && x.IdempotencyKey == solicitationKey, ct);
            if (replay is not null)
            {
                EnsureReplay(replay.RequestHash, hash);
                var replayEvent = await _db.ProcurementEvents.AsNoTracking().SingleOrDefaultAsync(x =>
                    x.BusinessUnitId == command.BusinessUnitId
                    && x.AggregateType == "SourcingCase"
                    && x.AggregateId == sourcingCase.Id
                    && x.EventType == "SOURCING_CASE_OUTREACH_READY"
                    && x.IdempotencyKey == $"{command.IdempotencyKey.Trim()}:case", ct);
                var snapshot = replayEvent is null
                    ? null
                    : ExtractPreparedSupplierRfqSnapshot(replayEvent.PayloadJson);
                if (snapshot is null || snapshot.SupplierSolicitationId != replay.Id)
                    throw new ProcurementConflictException(
                        "The original Supplier RFQ preparation snapshot is unavailable.");
                await tx.CommitAsync(ct);
                return new PreparedSupplierRfqResult(sourcingCase.Id, replay.Id, replay.Status.ToString(),
                    snapshot.SourcingCaseVersion, snapshot.SolicitationVersion, true);
            }
            if (sourcingCase.Version != command.ExpectedVersion)
                throw new ProcurementConflictException("The Sourcing Case changed; refresh before preparing Supplier RFQ.");
            var candidate = sourcingCase.Candidates.SingleOrDefault(x => x.SupplierId == command.SupplierId)
                ?? throw new ProcurementValidationException("Supplier is not a persisted candidate for this Sourcing Case.");
            var supplier = await RequireSupplierAsync(command.BusinessUnitId, command.SupplierId, ct);
            var supplierBlockers = SupplierRfqBlockingReasons(supplier);
            if (supplierBlockers.Count > 0)
                throw new ProcurementValidationException(
                    $"The selected supplier is not ready for a Supplier RFQ: {string.Join("; ", supplierBlockers)}");
            var existingPrepared = await _db.Set<SupplierSolicitation>()
                .OrderByDescending(x => x.Id)
                .FirstOrDefaultAsync(x =>
                x.BusinessUnitId == command.BusinessUnitId
                && x.SourcingCaseId == sourcingCase.Id
                && x.SupplierId == command.SupplierId, ct);
            if (existingPrepared is not null)
            {
                if (existingPrepared.Status != SolicitationStatus.PendingDispatch)
                    throw new ProcurementConflictException(
                        "Supplier outreach already exists for this candidate. Review its current delivery or response status.");
                var alreadyQueued = await _db.ProcurementOutboxMessages.AnyAsync(x =>
                    x.BusinessUnitId == command.BusinessUnitId
                    && x.SupplierSolicitationId == existingPrepared.Id, ct);
                if (alreadyQueued)
                    throw new ProcurementConflictException(
                        "A Supplier RFQ for this candidate is already queued for governed delivery.");
                var preparationEvent = await _db.ProcurementEvents.AsNoTracking()
                    .Where(x => x.BusinessUnitId == command.BusinessUnitId
                        && x.AggregateType == "SupplierSolicitation"
                        && x.AggregateId == existingPrepared.Id
                        && x.EventType == "SUPPLIER_RFQ_CREATED")
                    .OrderByDescending(x => x.Id)
                    .FirstOrDefaultAsync(ct);
                var originalPayload = preparationEvent is null
                    ? null
                    : JsonSerializer.Deserialize<SolicitationDispatchPayload>(preparationEvent.PayloadJson);
                if (originalPayload is null || originalPayload.DueOn != command.DueOn)
                    throw new ProcurementConflictException(
                        "A prepared Supplier RFQ already exists with different delivery terms. Review or cancel it before preparing another.");
                await tx.CommitAsync(ct);
                return new PreparedSupplierRfqResult(sourcingCase.Id, existingPrepared.Id,
                    existingPrepared.Status.ToString(), sourcingCase.Version, existingPrepared.Version, true);
            }
            var rfq = await RequireRfqAsync(command.BusinessUnitId, sourcingCase.RfqId, ct);
            var rfqItem = await _db.Rfqitems.SingleAsync(x => x.Id == sourcingCase.RfqItemId
                && x.Rfqid == sourcingCase.RfqId, ct);
            var currentShortfall = await GetNetSourcingRequirementAsync(command.BusinessUnitId, rfqItem, ct);
            if (currentShortfall <= 0)
                throw new ProcurementConflictException(
                    "This demand line is now fully covered. Refresh the RFQ before preparing supplier outreach.");
            var now = DateTime.UtcNow;
            sourcingCase.UnfulfilledQuantity = currentShortfall;
            sourcingCase.StockQuantity = Math.Max(0m, sourcingCase.RequestedQuantity - currentShortfall);
            var solicitation = new SupplierSolicitation
            {
                BusinessUnitId = command.BusinessUnitId,
                RfqId = sourcingCase.RfqId,
                SupplierId = command.SupplierId,
                SourcingCaseId = sourcingCase.Id,
                CommercialDemandLineId = sourcingCase.CommercialDemandLineId,
                NexoraSerial = sourcingCase.NexoraSerial,
                IdempotencyKey = solicitationKey,
                RequestHash = hash,
                RequestedRfqItemIdsJson = JsonSerializer.Serialize(new[] { sourcingCase.RfqItemId }),
                Status = SolicitationStatus.PendingDispatch,
                Channel = "Email",
                DueOn = command.DueOn,
                Notes = command.DueOn is null ? $"Sourcing Case {sourcingCase.Id}"
                    : $"Sourcing Case {sourcingCase.Id}; Due {command.DueOn.Value:O}",
                CreatedOn = now,
                UpdatedOn = now
            };
            _db.Add(solicitation);
            await _db.SaveChangesAsync(ct);
            solicitation.SupplierRfqNumber = $"SRFQ-{command.BusinessUnitId:D4}-{solicitation.Id:D8}";
            var payload = JsonSerializer.Serialize(new SolicitationDispatchPayload(
                solicitation.Id, command.BusinessUnitId, rfq.Id, supplier.ContactEmail!, supplier.Name,
                solicitation.SupplierRfqNumber, $"{sourcingCase.RfqItemId}: {sourcingCase.UnfulfilledQuantity:0.####}", command.DueOn));
            candidate.Selected = true;
            candidate.UpdatedOn = now;
            sourcingCase.Status = SourcingCaseStatuses.OutreachReady;
            sourcingCase.NextAction = "Review and dispatch prepared Supplier RFQ";
            sourcingCase.Version++;
            sourcingCase.UpdatedOn = now;
            sourcingCase.UpdatedBy = command.Actor.Trim();
            AddEvent(command.BusinessUnitId, "SupplierSolicitation", solicitation.Id, solicitation.Version,
                "SUPPLIER_RFQ_CREATED", command.Actor, command.CorrelationId, command.IdempotencyKey, payload, now);
            AddEvent(command.BusinessUnitId, "SourcingCase", sourcingCase.Id, sourcingCase.Version,
                "SOURCING_CASE_OUTREACH_READY", command.Actor, command.CorrelationId,
                $"{command.IdempotencyKey.Trim()}:case", JsonSerializer.Serialize(new
                {
                    sourcingCase.CommercialDemandLineId,
                    sourcingCase.NexoraSerial,
                    SupplierId = supplier.Id,
                    SupplierSolicitationId = solicitation.Id,
                    SourcingCaseVersion = sourcingCase.Version,
                    SolicitationVersion = solicitation.Version
                }), now);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new PreparedSupplierRfqResult(sourcingCase.Id, solicitation.Id,
                solicitation.Status.ToString(), sourcingCase.Version, solicitation.Version, false);
        });
    }

    public async Task<QueuedSupplierRfqResult> QueuePreparedSupplierRfqAsync(
        QueuePreparedSupplierRfqCommand command, CancellationToken ct = default)
    {
        ValidateCommand(command.BusinessUnitId, command.IdempotencyKey, command.Actor, command.CorrelationId);
        if (command.SourcingCaseId <= 0 || command.SupplierSolicitationId <= 0)
            throw new ProcurementValidationException("Sourcing Case and Supplier RFQ identifiers must be positive.");

        var requestHash = Hash(new
        {
            command.SourcingCaseId,
            command.SupplierSolicitationId,
            command.ExpectedSourcingCaseVersion,
            command.ExpectedSolicitationVersion
        });
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var replay = await _db.ProcurementEvents.AsNoTracking().SingleOrDefaultAsync(x =>
                x.BusinessUnitId == command.BusinessUnitId
                && x.EventType == "SUPPLIER_RFQ_DISPATCH_QUEUED"
                && x.IdempotencyKey == command.IdempotencyKey.Trim(), ct);
            if (replay is not null)
            {
                using var replayPayload = JsonDocument.Parse(replay.PayloadJson);
                if (replay.AggregateId != command.SupplierSolicitationId
                    || replayPayload.RootElement.GetProperty("requestHash").GetString() != requestHash)
                    throw new ProcurementConflictException(
                        "The idempotency key was already used for a different Supplier RFQ dispatch.");
                await tx.CommitAsync(ct);
                return new QueuedSupplierRfqResult(command.SourcingCaseId, command.SupplierSolicitationId,
                    replayPayload.RootElement.GetProperty("Status").GetString()!,
                    replayPayload.RootElement.GetProperty("SourcingCaseVersion").GetInt64(),
                    replayPayload.RootElement.GetProperty("SolicitationVersion").GetInt64(), true);
            }

            var sourcingCase = await _db.SourcingCases.SingleOrDefaultAsync(x =>
                x.BusinessUnitId == command.BusinessUnitId && x.Id == command.SourcingCaseId, ct)
                ?? throw new ProcurementValidationException("Sourcing Case was not found in the authenticated tenant.");
            var solicitation = await _db.Set<SupplierSolicitation>().SingleOrDefaultAsync(x =>
                x.BusinessUnitId == command.BusinessUnitId && x.Id == command.SupplierSolicitationId
                && x.SourcingCaseId == sourcingCase.Id, ct)
                ?? throw new ProcurementValidationException(
                    "The prepared Supplier RFQ does not belong to this Sourcing Case.");
            if (sourcingCase.Version != command.ExpectedSourcingCaseVersion
                || solicitation.Version != command.ExpectedSolicitationVersion)
                throw new ProcurementConflictException(
                    "The Sourcing Case or Supplier RFQ changed; refresh before approving dispatch.");
            if (solicitation.Status != SolicitationStatus.PendingDispatch)
                throw new ProcurementConflictException("Only a prepared Supplier RFQ can be queued for dispatch.");
            if (await _db.ProcurementOutboxMessages.AnyAsync(x =>
                    x.BusinessUnitId == command.BusinessUnitId
                    && x.SupplierSolicitationId == solicitation.Id, ct))
                throw new ProcurementConflictException("This Supplier RFQ already has a dispatch record.");

            var supplier = await RequireSupplierAsync(command.BusinessUnitId, solicitation.SupplierId, ct);
            var blockers = SupplierRfqBlockingReasons(supplier);
            if (blockers.Count > 0)
                throw new ProcurementValidationException(
                    $"The Supplier is no longer ready for outreach: {string.Join("; ", blockers)}");
            var rfqItem = await _db.Rfqitems.SingleAsync(x => x.Id == sourcingCase.RfqItemId
                && x.Rfqid == sourcingCase.RfqId, ct);
            var currentShortfall = await GetNetSourcingRequirementAsync(command.BusinessUnitId, rfqItem, ct);
            if (currentShortfall <= 0)
                throw new ProcurementConflictException(
                    "This demand line is now fully covered. Supplier outreach was not queued.");

            var createdEvent = await _db.ProcurementEvents.AsNoTracking()
                .Where(x => x.BusinessUnitId == command.BusinessUnitId
                    && x.AggregateType == "SupplierSolicitation"
                    && x.AggregateId == solicitation.Id
                    && x.EventType == "SUPPLIER_RFQ_CREATED")
                .OrderByDescending(x => x.Id)
                .FirstOrDefaultAsync(ct)
                ?? throw new ProcurementConflictException("The prepared Supplier RFQ audit evidence is unavailable.");
            var dispatchPayload = JsonSerializer.Deserialize<SolicitationDispatchPayload>(createdEvent.PayloadJson)
                ?? throw new ProcurementConflictException("The prepared Supplier RFQ dispatch payload is unavailable.");
            if (dispatchPayload.SolicitationId != solicitation.Id
                || dispatchPayload.BusinessUnitId != command.BusinessUnitId
                || !string.Equals(dispatchPayload.ToEmail, supplier.ContactEmail, StringComparison.OrdinalIgnoreCase))
                throw new ProcurementConflictException(
                    "Supplier dispatch details changed after preparation. Prepare a new Supplier RFQ after governance review.");

            var now = DateTime.UtcNow;
            _db.ProcurementOutboxMessages.Add(new ProcurementOutboxMessage
            {
                BusinessUnitId = command.BusinessUnitId,
                SupplierSolicitationId = solicitation.Id,
                Status = ProcurementOutboxStatuses.Pending,
                PayloadJson = createdEvent.PayloadJson,
                NextAttemptOn = now,
                OriginCorrelationId = command.CorrelationId.Trim(),
                CreatedOn = now,
                UpdatedOn = now
            });
            sourcingCase.UnfulfilledQuantity = currentShortfall;
            sourcingCase.StockQuantity = Math.Max(0m, sourcingCase.RequestedQuantity - currentShortfall);
            sourcingCase.NextAction = "Supplier RFQ queued for governed delivery";
            sourcingCase.Version++;
            sourcingCase.UpdatedOn = now;
            sourcingCase.UpdatedBy = command.Actor.Trim();
            AddEvent(command.BusinessUnitId, "SupplierSolicitation", solicitation.Id, solicitation.Version,
                "SUPPLIER_RFQ_DISPATCH_QUEUED", command.Actor, command.CorrelationId,
                command.IdempotencyKey, JsonSerializer.Serialize(new
                {
                    requestHash,
                    SourcingCaseId = sourcingCase.Id,
                    solicitation.SupplierRfqNumber,
                    sourcingCase.NexoraSerial,
                    sourcingCase.UnfulfilledQuantity,
                    Status = solicitation.Status.ToString(),
                    SourcingCaseVersion = sourcingCase.Version,
                    SolicitationVersion = solicitation.Version
                }), now);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new QueuedSupplierRfqResult(sourcingCase.Id, solicitation.Id,
                solicitation.Status.ToString(), sourcingCase.Version, solicitation.Version, false);
        });
    }

    public async Task<IReadOnlyCollection<SupplierPurchaseOrderSummary>> SearchPurchaseOrdersAsync(
        long businessUnitId, string? search, int limit, CancellationToken ct = default)
    {
        ValidateTenant(businessUnitId);
        var boundedLimit = Math.Clamp(limit, 1, 100);
        var term = search?.Trim();
        var query =
            from po in _db.SupplierPurchaseOrders.AsNoTracking()
            join rfq in _db.Rfqs.AsNoTracking() on new { po.BusinessUnitId, Id = po.RfqId }
                equals new { rfq.BusinessUnitId, rfq.Id }
            join supplier in _db.Suppliers.AsNoTracking() on po.SupplierId equals supplier.Id
            join currency in _db.Currencies.AsNoTracking() on po.CurrencyId equals currency.Id
            where po.BusinessUnitId == businessUnitId
                && supplier.Buid == businessUnitId
                && currency.BusinessUnitId == businessUnitId
                && (term == null || term == ""
                    || po.PurchaseOrderNumber.Contains(term)
                    || rfq.Rfqno.Contains(term)
                    || (rfq.NexoraSerial != null && rfq.NexoraSerial.Contains(term))
                    || supplier.Name.Contains(term)
                    || po.Status.Contains(term))
            orderby po.CreatedOn descending, po.Id descending
            select new SupplierPurchaseOrderSummary(
                po.Id, po.PurchaseOrderNumber, po.RfqId, rfq.Rfqno, rfq.NexoraSerial,
                po.SupplierId, supplier.Name, currency.Code, po.Status, po.TotalValue,
                po.ExpectedOn, po.CreatedOn, po.Lines.Count,
                po.Lines.Sum(line => line.OrderedQuantity - line.ReceivedQuantity));

        return await query.Take(boundedLimit).ToArrayAsync(ct);
    }

    public async Task<ProcurementWorkbench> GetWorkbenchAsync(long businessUnitId, long rfqId, CancellationToken ct = default)
    {
        ValidateTenant(businessUnitId);
        var rfq = await _db.Rfqs.AsNoTracking().Include(x => x.Customer)
            .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == rfqId, ct)
            ?? throw new ProcurementValidationException("RFQ was not found in the authenticated tenant.");
        var items = await _db.Rfqitems.AsNoTracking().Where(x => x.Rfqid == rfqId).OrderBy(x => x.Id).ToListAsync(ct);
        var itemIds = items.Select(x => x.Id).ToArray();
        var productIds = items.Where(x => x.ProductId.HasValue).Select(x => x.ProductId!.Value).Distinct().ToArray();
        var inventory = await _db.Set<Models.Inventory>().AsNoTracking()
            .Where(x => x.Buid == businessUnitId && x.ProductId != null && productIds.Contains(x.ProductId.Value))
            .ToListAsync(ct);
        var inventoryIds = inventory.Select(x => x.Id).ToArray();
        var reservations = await _db.Set<StockReservation>().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && inventoryIds.Contains(x.InventoryId)
                && x.Status == StockReservationStatus.Active)
            .GroupBy(x => x.InventoryId).Select(x => new { InventoryId = x.Key, Quantity = x.Sum(v => v.Quantity) })
            .ToDictionaryAsync(x => x.InventoryId, x => x.Quantity, ct);
        var quoteRows = await _db.SupplierQuotedItems.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.RfqItemId != null && itemIds.Contains(x.RfqItemId.Value) && x.IsActive)
            .ToListAsync(ct);
        var workbenchRevisionIds = quoteRows.Where(x => x.SourceSupplierQuoteRevisionId.HasValue)
            .Select(x => x.SourceSupplierQuoteRevisionId!.Value).Distinct().ToArray();
        var workbenchRevisions = await _db.SupplierQuoteRevisions.AsNoTracking()
            .Include(x => x.SupplierQuote).Include(x => x.Lines)
            .Include(x => x.Evidence).Include(x => x.ReviewDecisions)
            .Where(x => x.BusinessUnitId == businessUnitId && workbenchRevisionIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, ct);
        var awards = await _db.Set<SourcingAward>().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.RfqId == rfqId
                && x.Status != "CANCELLED" && x.Status != "REJECTED")
            .ToListAsync(ct);
        var solicitations = await _db.Set<SupplierSolicitation>().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.RfqId == rfqId).OrderByDescending(x => x.CreatedOn).ToListAsync(ct);
        var purchaseOrders = await _db.SupplierPurchaseOrders.AsNoTracking().Include(x => x.Lines)
            .Where(x => x.BusinessUnitId == businessUnitId && x.RfqId == rfqId).OrderByDescending(x => x.CreatedOn).ToListAsync(ct);
        var customerDraft = await _db.Quotes.AsNoTracking().Include(x => x.Status).Include(x => x.QuoteItems)
            .Where(x => x.BusinessUnitId == businessUnitId && x.Rfqid == rfqId &&
                (x.Status!.SetupCode == "DRAFT" || x.StatusId == 42))
            .OrderByDescending(x => x.CreatedDate).ThenByDescending(x => x.Id).FirstOrDefaultAsync(ct);
        var supplierIds = solicitations.Select(x => x.SupplierId).Concat(quoteRows.Select(x => x.SupplierId))
            .Concat(purchaseOrders.Select(x => x.SupplierId)).Distinct().ToArray();
        var supplierNames = await _db.Suppliers.AsNoTracking().Where(x => x.Buid == businessUnitId && supplierIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, ct);
        var currencyIds = quoteRows.Where(x => x.CurrencyId.HasValue).Select(x => x.CurrencyId!.Value)
            .Concat(purchaseOrders.Select(x => x.CurrencyId)).Distinct().ToArray();
        var currencyCodes = await _db.Currencies.AsNoTracking().Where(x => x.BusinessUnitId == businessUnitId && currencyIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Code, ct);
        var outbox = await _db.ProcurementOutboxMessages.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && solicitations.Select(s => s.Id).Contains(x.SupplierSolicitationId))
            .ToDictionaryAsync(x => x.SupplierSolicitationId, ct);
        var poByAward = purchaseOrders.SelectMany(x => x.Lines.Select(line => new { line.SourcingAwardId, PurchaseOrderId = x.Id }))
            .ToDictionary(x => x.SourcingAwardId, x => x.PurchaseOrderId);
        var awardQuoteIds = awards.Where(x => x.SupplierQuotedItemId.HasValue).Select(x => x.SupplierQuotedItemId!.Value).ToHashSet();

        var incomingByItem = purchaseOrders.SelectMany(x => x.Lines)
            .GroupBy(x => x.RfqItemId)
            .ToDictionary(x => x.Key, x => x.Sum(line => Math.Max(0m, line.OrderedQuantity - line.ReceivedQuantity)));
        var authoritativeOfferIds = quoteRows.Where(x =>
                x.SourceSupplierQuoteRevisionId.HasValue &&
                workbenchRevisions.TryGetValue(x.SourceSupplierQuoteRevisionId.Value, out var revision) &&
                HasCurrentCanonicalAuthorization(x, revision))
            .Select(x => x.Id).ToHashSet();
        var approvedNotOrderedByItem = awards.Where(x => !poByAward.ContainsKey(x.Id) &&
                x.SupplierQuotedItemId.HasValue && authoritativeOfferIds.Contains(x.SupplierQuotedItemId.Value))
            .GroupBy(x => x.RfqItemId ?? 0)
            .ToDictionary(x => x.Key, x => x.Sum(award => Math.Max(0m, award.Quantity ?? 0m)));
        var allocatedStockByItem = new Dictionary<long, decimal>();
        foreach (var productGroup in items.Where(x => x.ProductId.HasValue).GroupBy(x => x.ProductId!.Value))
        {
            var stock = inventory.Where(x => x.ProductId == productGroup.Key).ToArray();
            var remainingStock = stock.Sum(x => Math.Max(0m, x.QtyOnHand - reservations.GetValueOrDefault(x.Id)
                - x.AllocatedQuantity - x.QuarantineQuantity - x.DamagedQuantity - x.ExpiredQuantity - x.SafetyStockQuantity));
            foreach (var item in productGroup.OrderBy(x => x.Id))
            {
                var demandAfterCommittedSupply = Math.Max(0m, (decimal)item.Quantity
                    - incomingByItem.GetValueOrDefault(item.Id)
                    - approvedNotOrderedByItem.GetValueOrDefault(item.Id));
                var allocated = Math.Min(remainingStock, demandAfterCommittedSupply);
                allocatedStockByItem[item.Id] = allocated;
                remainingStock -= allocated;
            }
        }

        var sourcingCaseIds = await _db.SourcingCases.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.RfqId == rfqId)
            .GroupBy(x => x.RfqItemId)
            .ToDictionaryAsync(x => x.Key, x => (long?)x.OrderByDescending(c => c.UpdatedOn).Select(c => c.Id).First(), ct);
        var checkedOn = DateTime.UtcNow;
        var lines = items.Select(item =>
        {
            var stock = item.ProductId.HasValue ? inventory.Where(x => x.ProductId == item.ProductId).ToArray() : [];
            var reserved = stock.Sum(x => reservations.GetValueOrDefault(x.Id));
            var available = allocatedStockByItem.GetValueOrDefault(item.Id);
            var openIncoming = incomingByItem.GetValueOrDefault(item.Id);
            var approvedNotOrdered = approvedNotOrderedByItem.GetValueOrDefault(item.Id);
            var requested = (decimal)item.Quantity;
            var shortfall = Math.Max(0m, requested - available - openIncoming - approvedNotOrdered);
            var resolution = item.ProductId is null ? "UNKNOWN" : available >= requested ? "IN_STOCK"
                : shortfall == 0 ? "COVERED"
                : available > 0 ? "PARTIAL" : openIncoming > 0 ? "INCOMING" : "SHORTAGE";
            return new SourcingLineView(item.Id, rfqId, item.ProductId, sourcingCaseIds.GetValueOrDefault(item.Id),
                item.ManufacturerPartNumber ?? item.ItemMaterialCode, item.ProductShortDescription ?? item.ProductShortName
                    ?? item.CommodityProduct ?? item.ItemMaterialCode ?? $"RFQ line {item.Id}", requested, available,
                reserved, shortfall, item.RequiredDesiredDate, resolution, checkedOn);
        }).ToArray();
        var offerViews = quoteRows.Select(row =>
        {
            var supplier = supplierNames.GetValueOrDefault(row.SupplierId);
            var sourceLine = lines.SingleOrDefault(x => x.Id == row.RfqItemId);
            var comparison = ToComparisonLine(row, sourceLine?.ShortfallQuantity ?? 0m, supplier,
                workbenchRevisions.GetValueOrDefault(row.SourceSupplierQuoteRevisionId ?? 0),
                sourceLine?.RequiredOn);
            return new SupplierOfferView(row.Id, row.SupplierSolicitationId ?? 0, row.RfqItemId ?? 0, row.SupplierId,
                supplier?.Name ?? $"Supplier {row.SupplierId}", row.QuoteReference, row.QuoteRevision,
                row.CurrencyId ?? 0, currencyCodes.GetValueOrDefault(row.CurrencyId ?? 0) ?? "N/A", row.Quantity,
                row.AvailableQuantity, row.UnitPrice ?? 0, row.FreightCost, row.DutyCost, row.OtherCost,
                row.LandedUnitCost, row.LeadTimeDays, row.ReliabilitySnapshot, row.ValidUntil, comparison.Eligible,
                comparison.Blockers, awardQuoteIds.Contains(row.Id), row.Version);
        }).ToArray();
        var awardViews = awards.Select(award => new SourcingAwardView(award.Id, award.RfqItemId ?? 0,
            award.SupplierQuotedItemId ?? 0, award.SupplierId,
            supplierNames.GetValueOrDefault(award.SupplierId)?.Name ?? $"Supplier {award.SupplierId}", award.Quantity ?? 0,
            award.LandedUnitCost ?? award.UnitPrice, award.CurrencyId ?? 0,
            currencyCodes.GetValueOrDefault(award.CurrencyId ?? 0) ?? "N/A", award.Status, award.Rationale,
            poByAward.GetValueOrDefault(award.Id) is var poId && poId > 0 ? poId : null, award.Version)).ToArray();
        var poViews = purchaseOrders.Select(po => new SupplierPurchaseOrderView(po.Id, po.RfqId,
            po.PurchaseOrderNumber, po.SupplierId, supplierNames.GetValueOrDefault(po.SupplierId)?.Name ?? $"Supplier {po.SupplierId}",
            po.CurrencyId, currencyCodes.GetValueOrDefault(po.CurrencyId) ?? "N/A", po.Status, po.TotalValue, po.ExpectedOn,
            po.Version, po.Lines.Select(line => new SupplierPurchaseOrderLineView(line.Id, line.RfqItemId, line.ProductId,
                items.Single(x => x.Id == line.RfqItemId).ProductShortDescription ?? $"RFQ line {line.RfqItemId}",
                line.OrderedQuantity, line.ReceivedQuantity, Math.Max(0m, line.OrderedQuantity - line.ReceivedQuantity),
                line.UnitCost, line.LandedUnitCost, line.WarehouseId)).ToArray())).ToArray();

        return new ProcurementWorkbench(rfq.Id, rfq.Rfqno, rfq.NexoraSerial, rfq.Customer?.Name,
            currencyCodes.Values.FirstOrDefault(), lines,
            solicitations.Select(x =>
            {
                var supplier = supplierNames.GetValueOrDefault(x.SupplierId);
                var delivery = outbox.GetValueOrDefault(x.Id);
                var solicitedLineIds = JsonSerializer.Deserialize<long[]>(x.RequestedRfqItemIdsJson) ?? [];
                return new SolicitationView(x.Id, x.RfqId, x.SupplierId, supplier?.Name ?? $"Supplier {x.SupplierId}",
                    supplier?.ContactEmail, solicitedLineIds, x.Status.ToString().ToUpperInvariant(), x.Channel,
                    delivery?.AttemptCount ?? 0,
                    delivery?.ProviderReference, delivery?.LastErrorCode, x.SentOn == default ? null : x.SentOn,
                    x.RespondedOn, x.UpdatedOn, x.Version, x.DueOn);
            }).ToArray(), offerViews, awardViews, poViews,
            customerDraft is null ? null : new CustomerQuoteDraftView(customerDraft.Id, customerDraft.QuoteNo,
                customerDraft.CurrencyId, customerDraft.QuoteItems.Where(x => x.RfqitemId.HasValue)
                    .Select(x => new CustomerQuoteDraftLineView(x.Id, x.RfqitemId!.Value, x.Quantity,
                        x.UnitPrice, x.TotalAmount)).ToArray()));
    }

    public async Task<SolicitationResult> CreateSolicitationAsync(CreateSolicitationCommand command, CancellationToken ct = default)
    {
        ValidateCommand(command.BusinessUnitId, command.IdempotencyKey, command.Actor, command.CorrelationId);
        ValidateSolicitationDueOn(command.DueOn);
        if (command.RfqItemIds.Count == 0 || command.RfqItemIds.Distinct().Count() != command.RfqItemIds.Count)
            throw new ProcurementValidationException("At least one distinct RFQ line is required.");
        var hash = Hash(new { command.RfqId, command.SupplierId, Lines = command.RfqItemIds.Order(), command.DueOn });
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var existing = await _db.Set<SupplierSolicitation>()
                .SingleOrDefaultAsync(x => x.BusinessUnitId == command.BusinessUnitId && x.IdempotencyKey == command.IdempotencyKey.Trim(), ct);
            if (existing is not null)
            {
                EnsureReplay(existing.RequestHash, hash);
                await tx.CommitAsync(ct);
                return new SolicitationResult(existing.Id, existing.Status.ToString(), true);
            }

            var rfq = await RequireRfqAsync(command.BusinessUnitId, command.RfqId, ct);
            var supplier = await RequireSupplierAsync(command.BusinessUnitId, command.SupplierId, ct);
            var supplierBlockers = SupplierRfqBlockingReasons(supplier);
            if (supplierBlockers.Count > 0)
                throw new ProcurementValidationException(
                    $"The selected Supplier is not eligible for RFQ outreach: {string.Join("; ", supplierBlockers)}.");
            var requestedLines = await _db.Rfqitems.Where(x => x.Rfqid == command.RfqId && command.RfqItemIds.Contains(x.Id))
                .OrderBy(x => x.Id).ToListAsync(ct);
            if (requestedLines.Count != command.RfqItemIds.Count)
                throw new ProcurementValidationException("One or more RFQ lines do not belong to the selected RFQ.");
            var requestedQuantities = new Dictionary<long, decimal>();
            foreach (var line in requestedLines)
            {
                var remaining = await GetNetSourcingRequirementAsync(command.BusinessUnitId, line, ct);
                if (remaining <= 0)
                    throw new ProcurementConflictException($"RFQ line {line.Id} is already fully covered by stock, incoming supply, or active awards.");
                requestedQuantities[line.Id] = remaining;
            }

            var now = DateTime.UtcNow;
            var sourcingCases = new List<SourcingCase>(requestedLines.Count);
            foreach (var requestedLine in requestedLines)
            {
                sourcingCases.Add(await EnsureCanonicalSourcingCaseAsync(
                    command.BusinessUnitId, rfq, requestedLine, requestedQuantities[requestedLine.Id],
                    command.Actor, command.CorrelationId, command.IdempotencyKey, now, ct));
            }
            var sourcingCase = sourcingCases[0];
            var solicitation = new SupplierSolicitation
            {
                BusinessUnitId = command.BusinessUnitId,
                RfqId = command.RfqId,
                SupplierId = command.SupplierId,
                SourcingCaseId = sourcingCase.Id,
                CommercialDemandLineId = sourcingCase.CommercialDemandLineId,
                NexoraSerial = sourcingCase.NexoraSerial,
                IdempotencyKey = command.IdempotencyKey.Trim(),
                RequestHash = hash,
                RequestedRfqItemIdsJson = JsonSerializer.Serialize(command.RfqItemIds.Order()),
                Status = SolicitationStatus.PendingDispatch,
                Channel = "Email",
                DueOn = command.DueOn,
                Notes = command.DueOn is null ? null : $"Due {command.DueOn.Value:O}",
                CreatedOn = now,
                UpdatedOn = now
            };
            _db.Add(solicitation);
            await _db.SaveChangesAsync(ct);

            var payload = JsonSerializer.Serialize(new SolicitationDispatchPayload(
                solicitation.Id, command.BusinessUnitId, rfq.Id, supplier.ContactEmail!, supplier.Name, rfq.Rfqno,
                string.Join(", ", requestedQuantities.OrderBy(x => x.Key).Select(x => $"{x.Key}: {x.Value:0.####}")), command.DueOn));
            _db.ProcurementOutboxMessages.Add(new ProcurementOutboxMessage
            {
                BusinessUnitId = command.BusinessUnitId,
                SupplierSolicitationId = solicitation.Id,
                Status = ProcurementOutboxStatuses.Pending,
                PayloadJson = payload,
                OriginCorrelationId = command.CorrelationId.Trim(),
                NextAttemptOn = now,
                CreatedOn = now,
                UpdatedOn = now
            });
            AddEvent(command.BusinessUnitId, "SupplierSolicitation", solicitation.Id, solicitation.Version,
                "SUPPLIER_SOLICITATION_QUEUED", command.Actor, command.CorrelationId, command.IdempotencyKey, payload, now);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new SolicitationResult(solicitation.Id, solicitation.Status.ToString(), false);
        });
    }

    private async Task<SourcingCase> EnsureCanonicalSourcingCaseAsync(
        long businessUnitId, Rfq rfq, Rfqitem item, decimal unfulfilledQuantity,
        string actor, string correlationId, string idempotencyKey, DateTime now, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rfq.NexoraSerial))
            throw new ProcurementConflictException("The RFQ must have an immutable Nexora Serial before supplier outreach.");

        var identityKey = $"rfq:{rfq.Id}:line:{item.Id}";
        var demandLine = await _db.CommercialDemandLines.SingleOrDefaultAsync(x =>
            x.BusinessUnitId == businessUnitId && x.RfqItemId == item.Id, ct);
        if (demandLine is null)
        {
            demandLine = new CommercialDemandLine
            {
                BusinessUnitId = businessUnitId,
                RfqId = rfq.Id,
                RfqItemId = item.Id,
                NexoraSerial = rfq.NexoraSerial,
                IdentityKey = identityKey,
                CreatedOn = now,
                CreatedBy = actor.Trim()
            };
            _db.CommercialDemandLines.Add(demandLine);
            await _db.SaveChangesAsync(ct);
        }
        else if (demandLine.RfqId != rfq.Id || demandLine.NexoraSerial != rfq.NexoraSerial
                 || demandLine.IdentityKey != identityKey)
        {
            throw new ProcurementConflictException(
                "The immutable commercial Demand Line identity does not match this RFQ line.");
        }

        var sourcingCase = await _db.SourcingCases.SingleOrDefaultAsync(x =>
            x.BusinessUnitId == businessUnitId && x.CommercialDemandLineId == demandLine.Id, ct);
        if (sourcingCase is not null) return sourcingCase;

        var requestedQuantity = (decimal)item.Quantity;
        var stockQuantity = Math.Max(0m, requestedQuantity - unfulfilledQuantity);
        var shortageDecisionKey = Hash(new
        {
            demandLine.Id,
            RequestedQuantity = requestedQuantity,
            StockQuantity = stockQuantity,
            UnfulfilledQuantity = unfulfilledQuantity,
            item.ProductId,
            item.RequiredDesiredDate,
            SourceEntireQuantity = false
        });
        sourcingCase = new SourcingCase
        {
            BusinessUnitId = businessUnitId,
            CommercialDemandLineId = demandLine.Id,
            RfqId = rfq.Id,
            RfqItemId = item.Id,
            LeadId = rfq.LeadId,
            CustomerId = rfq.CustomerId,
            ProductId = item.ProductId,
            NexoraSerial = rfq.NexoraSerial,
            RequestedPartNumber = item.ManufacturerPartNumber ?? item.ItemMaterialCode,
            Manufacturer = item.ManufacturerName,
            Description = item.ProductShortDescription ?? item.ProductShortName ?? item.CommodityProduct
                ?? item.ItemMaterialCode ?? $"RFQ line {item.Id}",
            UnitOfMeasure = item.UnitOfMeasure,
            RequestedQuantity = requestedQuantity,
            StockQuantity = stockQuantity,
            UnfulfilledQuantity = unfulfilledQuantity,
            RequiredOn = item.RequiredDesiredDate,
            DeliveryLocation = item.StorageLocation,
            SearchLimit = 10,
            Status = SourcingCaseStatuses.OutreachReady,
            NextAction = "Review and dispatch Supplier RFQ",
            ShortageDecisionKey = shortageDecisionKey,
            IdempotencyKey = $"{idempotencyKey.Trim()}:canonical-case:{item.Id}",
            RequestHash = Hash(new { RfqId = rfq.Id, RfqItemId = item.Id, unfulfilledQuantity }),
            CreatedOn = now,
            CreatedBy = actor.Trim(),
            UpdatedOn = now,
            UpdatedBy = actor.Trim()
        };
        _db.SourcingCases.Add(sourcingCase);
        await _db.SaveChangesAsync(ct);
        AddEvent(businessUnitId, "SourcingCase", sourcingCase.Id, sourcingCase.Version,
            "SOURCING_CASE_CREATED_FOR_OUTREACH", actor, correlationId,
            $"{idempotencyKey.Trim()}:canonical-case:{item.Id}", JsonSerializer.Serialize(new
            {
                sourcingCase.CommercialDemandLineId,
                sourcingCase.RfqId,
                sourcingCase.RfqItemId,
                sourcingCase.NexoraSerial,
                sourcingCase.UnfulfilledQuantity
            }), now);
        return sourcingCase;
    }

    public async Task<SolicitationResult> RetrySolicitationAsync(RetrySolicitationCommand command, CancellationToken ct = default)
    {
        ValidateCommand(command.BusinessUnitId, command.IdempotencyKey, command.Actor, command.CorrelationId);
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var existingEvent = await _db.ProcurementEvents.AsNoTracking().FirstOrDefaultAsync(x =>
                x.BusinessUnitId == command.BusinessUnitId && x.EventType == "SUPPLIER_SOLICITATION_RETRY_QUEUED"
                && x.IdempotencyKey == command.IdempotencyKey.Trim(), ct);
            var solicitation = await _db.Set<SupplierSolicitation>().SingleOrDefaultAsync(x =>
                x.BusinessUnitId == command.BusinessUnitId && x.Id == command.SolicitationId, ct)
                ?? throw new ProcurementValidationException("Solicitation was not found in the authenticated tenant.");
            if (existingEvent is not null)
            {
                if (existingEvent.AggregateId != solicitation.Id)
                    throw new ProcurementConflictException("The idempotency key was already used for another solicitation.");
                await tx.CommitAsync(ct);
                return new SolicitationResult(solicitation.Id, solicitation.Status.ToString(), true);
            }
            if (solicitation.Status != SolicitationStatus.DeliveryFailed)
                throw new ProcurementConflictException("Only a failed solicitation can be manually retried.");
            var outbox = await _db.ProcurementOutboxMessages.SingleOrDefaultAsync(x =>
                x.BusinessUnitId == command.BusinessUnitId && x.SupplierSolicitationId == solicitation.Id, ct)
                ?? throw new ProcurementValidationException("Solicitation delivery record was not found.");
            var now = DateTime.UtcNow;
            outbox.Status = ProcurementOutboxStatuses.Pending;
            outbox.AttemptCount = 0;
            outbox.NextAttemptOn = now;
            outbox.LeaseOwner = null;
            outbox.LeaseToken = null;
            outbox.LeaseUntil = null;
            outbox.DeadLetteredOn = null;
            outbox.LastErrorCode = null;
            outbox.UpdatedOn = now;
            solicitation.Status = SolicitationStatus.PendingDispatch;
            solicitation.UpdatedOn = now;
            solicitation.Version++;
            AddEvent(command.BusinessUnitId, "SupplierSolicitation", solicitation.Id, solicitation.Version,
                "SUPPLIER_SOLICITATION_RETRY_QUEUED", command.Actor, command.CorrelationId,
                command.IdempotencyKey, JsonSerializer.Serialize(new { solicitation.Id }), now);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new SolicitationResult(solicitation.Id, solicitation.Status.ToString(), false);
        });
    }

    public async Task<SupplierQuoteResult> CaptureSupplierQuoteAsync(CaptureSupplierQuoteCommand command, CancellationToken ct = default)
    {
        ValidateCommand(command.BusinessUnitId, command.IdempotencyKey, command.Actor, command.CorrelationId);
        if (command.Revision < 1 || command.Lines.Count == 0 || command.ValidUntil <= DateTime.UtcNow)
            throw new ProcurementValidationException("A positive revision, at least one line, and a future validity date are required.");
        if (command.Lines.Select(x => x.RfqItemId).Distinct().Count() != command.Lines.Count)
            throw new ProcurementValidationException("A supplier response may contain each RFQ line only once.");
        foreach (var line in command.Lines) ValidateQuoteLine(line);
        var hash = Hash(command with { Actor = string.Empty, CorrelationId = string.Empty });
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var responseKeyPrefix = $"{command.IdempotencyKey.Trim()}:";
            var replay = await _db.SupplierQuotedItems.Where(x => x.BusinessUnitId == command.BusinessUnitId
                && x.ResponseIdempotencyKey != null && x.ResponseIdempotencyKey.StartsWith(responseKeyPrefix)).OrderBy(x => x.Id).ToListAsync(ct);
            if (replay.Count > 0)
            {
                EnsureReplay(replay[0].RequestHash, hash);
                await tx.CommitAsync(ct);
                return new SupplierQuoteResult(replay.Select(x => x.Id).ToArray(), true);
            }

            var solicitation = await _db.Set<SupplierSolicitation>().SingleOrDefaultAsync(x => x.BusinessUnitId == command.BusinessUnitId
                && x.Id == command.SolicitationId, ct) ?? throw new ProcurementValidationException("Solicitation was not found in the authenticated tenant.");
            if (solicitation.Status is not (SolicitationStatus.Sent or SolicitationStatus.Responded))
                throw new ProcurementConflictException("A supplier response requires a sent solicitation with delivery evidence.");
            var solicitedLineIds = JsonSerializer.Deserialize<long[]>(solicitation.RequestedRfqItemIdsJson) ?? [];
            if (solicitedLineIds.Length > 0
                && command.Lines.Any(x => !solicitedLineIds.Contains(x.RfqItemId)))
                throw new ProcurementValidationException("Every quoted line must belong to the solicitation.");
            var rfqLines = await _db.Rfqitems.Where(x => x.Rfqid == solicitation.RfqId
                && command.Lines.Select(line => line.RfqItemId).Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
            if (rfqLines.Count != command.Lines.Count || command.Lines.Any(x =>
                    rfqLines[x.RfqItemId].ProductId.HasValue && rfqLines[x.RfqItemId].ProductId != x.ProductId))
                throw new ProcurementValidationException("Every quoted product and line must belong to the solicited RFQ.");
            if (rfqLines.Values.Any(x => string.IsNullOrWhiteSpace(x.UnitOfMeasure)))
                throw new ProcurementValidationException("Every quoted RFQ line requires a verified unit of measure.");
            await RequireSupplierAsync(command.BusinessUnitId, solicitation.SupplierId, ct);
            if (!solicitation.SourcingCaseId.HasValue || string.IsNullOrWhiteSpace(solicitation.NexoraSerial))
                throw new ProcurementValidationException("Canonical Sourcing Case and Nexora Serial lineage are required.");
            var sourcingCase = await _db.SourcingCases.AsNoTracking().SingleOrDefaultAsync(x =>
                x.BusinessUnitId == command.BusinessUnitId && x.Id == solicitation.SourcingCaseId.Value, ct)
                ?? throw new ProcurementValidationException("Canonical Sourcing Case was not found.");
            var demandLines = await _db.CommercialDemandLines.AsNoTracking().Where(x =>
                    x.BusinessUnitId == command.BusinessUnitId && x.RfqId == solicitation.RfqId &&
                    command.Lines.Select(line => line.RfqItemId).Contains(x.RfqItemId))
                .ToDictionaryAsync(x => x.RfqItemId, ct);
            if (demandLines.Count != command.Lines.Count)
                throw new ProcurementValidationException("Every Supplier Quote line requires canonical demand lineage.");
            var currencies = command.Lines.Select(x => x.CurrencyId).Distinct().ToArray();
            if (currencies.Length != 1)
                throw new ProcurementValidationException("One canonical Supplier Quote revision must use one currency.");

            var priorRows = await _db.SupplierQuotedItems.Where(x =>
                x.BusinessUnitId == command.BusinessUnitId
                && x.SupplierSolicitationId == solicitation.Id)
                .ToListAsync(ct);
            if (priorRows.Count > 0 && command.Revision <= priorRows.Max(x => x.QuoteRevision))
                throw new ProcurementConflictException("The supplier quote revision must be newer than the current revision.");

            var now = DateTime.UtcNow;
            var canonicalReference = await _db.SupplierQuotes.AsNoTracking()
                .Where(x => x.BusinessUnitId == command.BusinessUnitId
                    && x.SupplierSolicitationId == solicitation.Id)
                .Select(x => x.SupplierQuoteReference)
                .SingleOrDefaultAsync(ct) ?? command.SupplierQuoteReference;
            var canonical = await new SupplierQuotes.SupplierQuoteInboxService(_db).CaptureAsync(
                new SupplierQuotes.CaptureSupplierQuoteCommand(
                    command.BusinessUnitId, solicitation.SupplierId, solicitation.Id, sourcingCase.Id,
                    solicitation.NexoraSerial, canonicalReference, command.Revision,
                    SupplierQuotes.SupplierQuoteCaptureChannels.Manual, null,
                    $"procurement-workbench:{solicitation.Id}:revision:{command.Revision}", hash,
                    currencies[0], command.ValidUntil, null,
                    command.Lines.Sum(x => x.FreightCost + x.DutyCost + x.OtherCost),
                    command.Lines.Sum(x => x.TaxAmount), null, null,
                    command.Lines.Select((line, index) => new SupplierQuotes.CaptureSupplierQuoteLine(
                        index + 1, line.RfqItemId, demandLines[line.RfqItemId].Id,
                        rfqLines[line.RfqItemId].ManufacturerPartNumber ?? rfqLines[line.RfqItemId].ItemMaterialCode,
                        rfqLines[line.RfqItemId].ManufacturerName, null,
                        rfqLines[line.RfqItemId].ProductShortDescription ??
                            rfqLines[line.RfqItemId].ProductShortName ?? rfqLines[line.RfqItemId].ItemMaterialCode ??
                            $"RFQ line {line.RfqItemId}", line.Quantity, line.AvailableQuantity,
                        rfqLines[line.RfqItemId].UnitOfMeasure!, line.UnitPrice,
                        line.MinimumOrderQuantity, line.LeadTimeDays, null, null, null, false, null, [])).ToArray(),
                    [], command.IdempotencyKey, command.Actor, command.CorrelationId), ct);
            var canonicalLines = await _db.SupplierQuoteLines.AsNoTracking().Where(x =>
                    x.BusinessUnitId == command.BusinessUnitId && x.SupplierQuoteRevisionId == canonical.RevisionId)
                .ToDictionaryAsync(x => x.RfqItemId, ct);
            var revisedLineIds = command.Lines.Select(x => x.RfqItemId).ToHashSet();
            foreach (var priorRow in priorRows.Where(x => x.IsActive
                         && x.RfqItemId.HasValue && revisedLineIds.Contains(x.RfqItemId.Value)))
            {
                priorRow.IsActive = false;
                priorRow.ModifiedBy = command.Actor.Trim();
                priorRow.ModifiedDate = now;
                priorRow.Version++;
            }
            var rows = command.Lines.Select(line => new SupplierQuotedItem
            {
                BusinessUnitId = command.BusinessUnitId,
                SupplierId = solicitation.SupplierId,
                SupplierSolicitationId = solicitation.Id,
                RfqId = solicitation.RfqId,
                RfqItemId = line.RfqItemId,
                ProductId = line.ProductId,
                SourceSupplierQuoteId = canonical.SupplierQuoteId,
                SourceSupplierQuoteRevisionId = canonical.RevisionId,
                SourceSupplierQuoteLineId = canonicalLines[line.RfqItemId].Id,
                CommercialDemandLineId = demandLines[line.RfqItemId].Id,
                SourcingCaseId = sourcingCase.Id,
                ItemName = rfqLines[line.RfqItemId].ProductShortName ?? rfqLines[line.RfqItemId].ItemMaterialCode,
                Description = rfqLines[line.RfqItemId].ProductShortDescription,
                Quantity = line.Quantity,
                UnitPrice = line.UnitPrice,
                CurrencyId = line.CurrencyId,
                QuoteReference = command.SupplierQuoteReference.Trim(),
                LeadTimeDays = line.LeadTimeDays,
                AvailableQuantity = line.AvailableQuantity,
                FreightCost = line.FreightCost,
                DutyCost = line.DutyCost,
                OtherCost = line.OtherCost,
                TaxAmount = line.TaxAmount,
                DiscountAmount = line.DiscountAmount,
                MinimumOrderQuantity = line.MinimumOrderQuantity,
                ReliabilitySnapshot = line.ReliabilitySnapshot,
                LandedUnitCost = CalculateLandedUnitCost(line),
                QuoteRevision = command.Revision,
                ResponseIdempotencyKey = $"{command.IdempotencyKey.Trim()}:{line.RfqItemId}",
                RequestHash = hash,
                QuoteDate = now,
                ValidUntil = command.ValidUntil,
                CreatedBy = command.Actor.Trim(),
                CreatedDate = now,
                IsActive = true
            }).ToArray();
            _db.SupplierQuotedItems.AddRange(rows);
            solicitation.Status = SolicitationStatus.Responded;
            solicitation.RespondedOn = now;
            solicitation.UpdatedOn = now;
            solicitation.Version++;
            await _db.SaveChangesAsync(ct);
            AddEvent(command.BusinessUnitId, "SupplierSolicitation", solicitation.Id, solicitation.Version,
                "SUPPLIER_QUOTE_CAPTURED", command.Actor, command.CorrelationId, command.IdempotencyKey,
                JsonSerializer.Serialize(new { command.SupplierQuoteReference, command.Revision, LineIds = rows.Select(x => x.Id) }), now);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new SupplierQuoteResult(rows.Select(x => x.Id).ToArray(), false);
        });
    }

    public async Task<QuoteComparisonResult> CompareQuotesAsync(long businessUnitId, long rfqItemId, CancellationToken ct = default)
    {
        ValidateTenant(businessUnitId);
        var rfqItem = await _db.Rfqitems.AsNoTracking().Include(x => x.Rfq)
            .SingleOrDefaultAsync(x => x.Id == rfqItemId && x.Rfq.BusinessUnitId == businessUnitId, ct)
            ?? throw new ProcurementValidationException("RFQ line was not found in the authenticated tenant.");
        var rows = await _db.SupplierQuotedItems.AsNoTracking().Where(x => x.BusinessUnitId == businessUnitId
            && x.RfqId == rfqItem.Rfqid && x.RfqItemId == rfqItemId && x.IsActive).ToListAsync(ct);
        var remainingRequirement = await GetNetSourcingRequirementAsync(businessUnitId, rfqItem, ct);
        var supplierIds = rows.Select(x => x.SupplierId).Distinct().ToArray();
        var suppliers = await _db.Suppliers.AsNoTracking().Where(x => x.Buid == businessUnitId &&
            supplierIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var sourceRevisionIds = rows.Where(x => x.SourceSupplierQuoteRevisionId.HasValue)
            .Select(x => x.SourceSupplierQuoteRevisionId!.Value).Distinct().ToArray();
        var canonicalRevisions = await _db.SupplierQuoteRevisions.AsNoTracking()
            .Include(x => x.SupplierQuote).Include(x => x.Lines)
            .Include(x => x.Evidence).Include(x => x.ReviewDecisions)
            .Where(x => x.BusinessUnitId == businessUnitId && sourceRevisionIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, ct);
        var lines = rows.Select(row => ToComparisonLine(row, remainingRequirement,
                suppliers.GetValueOrDefault(row.SupplierId),
                canonicalRevisions.GetValueOrDefault(row.SourceSupplierQuoteRevisionId ?? 0),
                rfqItem.RequiredDesiredDate))
            .OrderBy(x => x.LandedUnitCost ?? decimal.MaxValue).ToArray();
        var eligible = lines.Where(x => x.Eligible).ToArray();
        var currencies = eligible.Select(x => x.CurrencyId).Distinct().ToArray();
        if (currencies.Length > 1)
            lines = lines.Select(line => line.Eligible
                ? line with { Blockers = line.Blockers.Append("currency not comparable without approved FX evidence").ToArray(), Eligible = false }
                : line).ToArray();
        eligible = lines.Where(x => x.Eligible).ToArray();
        currencies = eligible.Select(x => x.CurrencyId).Distinct().ToArray();
        var recommended = eligible.Length > 0 && currencies.Length == 1
            ? eligible.OrderByDescending(x => Math.Min(x.Quantity, x.AvailableQuantity ?? 0m) >= remainingRequirement)
                .ThenBy(x => x.LandedUnitCost).ThenBy(x => x.LeadTimeDays)
                .ThenBy(x => x.SupplierQuotedItemId).First().SupplierQuotedItemId
            : (long?)null;
        return new QuoteComparisonResult(rfqItemId, lines, recommended);
    }

    public async Task<AwardResult> ApproveAwardAsync(ApproveAwardCommand command, CancellationToken ct = default)
    {
        ValidateCommand(command.BusinessUnitId, command.IdempotencyKey, command.Actor, command.CorrelationId);
        if (command.Quantity <= 0) throw new ProcurementValidationException("Award quantity must be positive.");
        var hash = Hash(new { command.SupplierQuotedItemId, command.Quantity, command.ExpectedQuoteVersion, command.AwardedByUserId, command.Rationale });
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var replay = await _db.Set<SourcingAward>().SingleOrDefaultAsync(x => x.BusinessUnitId == command.BusinessUnitId
                && x.IdempotencyKey == command.IdempotencyKey.Trim(), ct);
            if (replay is not null)
            {
                EnsureReplay(replay.RequestHash, hash);
                await tx.CommitAsync(ct);
                return new AwardResult(replay.Id, replay.Status, replay.LandedUnitCost ?? replay.UnitPrice, true);
            }
            var quote = await _db.SupplierQuotedItems.SingleOrDefaultAsync(x => x.BusinessUnitId == command.BusinessUnitId
                && x.Id == command.SupplierQuotedItemId && x.IsActive, ct) ?? throw new ProcurementValidationException("Supplier quote was not found.");
            if (string.IsNullOrWhiteSpace(command.Rationale))
                throw new ProcurementValidationException("An evidence-based award rationale is required.");
            if (quote.Version != command.ExpectedQuoteVersion) throw new ProcurementConflictException("The supplier quote changed; refresh before awarding.");
            var rfqItem = await _db.Rfqitems.SingleAsync(x => x.Rfqid == quote.RfqId && x.Id == quote.RfqItemId, ct);
            var remainingRequirement = await GetNetSourcingRequirementAsync(command.BusinessUnitId, rfqItem, ct);
            var supplier = await RequireSupplierAsync(command.BusinessUnitId, quote.SupplierId, ct);
            var canonicalRevision = quote.SourceSupplierQuoteRevisionId.HasValue
                ? await _db.SupplierQuoteRevisions.AsNoTracking().Include(x => x.SupplierQuote).Include(x => x.Lines)
                    .Include(x => x.Evidence).Include(x => x.ReviewDecisions)
                    .SingleOrDefaultAsync(x => x.BusinessUnitId == command.BusinessUnitId &&
                        x.Id == quote.SourceSupplierQuoteRevisionId.Value, ct)
                : null;
            var comparison = ToComparisonLine(quote, remainingRequirement, supplier, canonicalRevision,
                rfqItem.RequiredDesiredDate);
            if (!comparison.Eligible) throw new ProcurementValidationException($"The supplier quote cannot be awarded: {string.Join(", ", comparison.Blockers)}.");
            var comparableCurrencies = await _db.SupplierQuotedItems.AsNoTracking().Where(x =>
                    x.BusinessUnitId == command.BusinessUnitId && x.RfqItemId == quote.RfqItemId && x.IsActive &&
                    x.SourceSupplierQuoteRevisionId.HasValue && x.CurrencyId.HasValue)
                .Select(x => x.CurrencyId!.Value).Distinct().ToArrayAsync(ct);
            if (comparableCurrencies.Length > 1)
                throw new ProcurementValidationException(
                    "Supplier offers use different currencies; an approved FX comparison is required before award.");
            if (command.Quantity > quote.Quantity || command.Quantity > quote.AvailableQuantity)
                throw new ProcurementValidationException("Award quantity exceeds the quoted or available quantity.");
            if (quote.MinimumOrderQuantity is > 0 && command.Quantity < quote.MinimumOrderQuantity)
                throw new ProcurementValidationException("Award quantity is below the supplier minimum order quantity.");
            var liveAwards = await _db.Set<SourcingAward>().Where(x => x.BusinessUnitId == command.BusinessUnitId
                && x.RfqItemId == quote.RfqItemId && x.Status != "CANCELLED" && x.Status != "REJECTED").ToListAsync(ct);
            if (command.Quantity > remainingRequirement)
                throw new ProcurementValidationException("Award quantity exceeds the remaining net sourcing requirement.");
            var awardedFromQuote = liveAwards.Where(x => x.SupplierQuotedItemId == quote.Id).Sum(x => x.Quantity ?? 0m);
            var quoteCapacity = Math.Min(quote.Quantity, quote.AvailableQuantity ?? 0m);
            if (awardedFromQuote + command.Quantity > quoteCapacity)
                throw new ProcurementValidationException("Award quantity exceeds the supplier quote's remaining availability.");
            foreach (var existingAward in liveAwards.Where(x => x.Status == "APPROVED"))
            {
                existingAward.Status = "SPLIT_APPROVED";
                existingAward.Version++;
            }
            var now = DateTime.UtcNow;
            var awardLandedUnitCost = CalculateAwardLandedUnitCost(
                quote, command.Quantity, includeFixedCharges: awardedFromQuote == 0);
            var award = new SourcingAward
            {
                BusinessUnitId = command.BusinessUnitId,
                RfqId = quote.RfqId!.Value,
                RfqItemId = quote.RfqItemId,
                SupplierId = quote.SupplierId,
                SupplierQuotedItemId = quote.Id,
                CurrencyId = quote.CurrencyId,
                Status = "APPROVED",
                IdempotencyKey = command.IdempotencyKey.Trim(),
                RequestHash = hash,
                UnitPrice = quote.UnitPrice!.Value,
                Quantity = command.Quantity,
                TotalValue = awardLandedUnitCost * command.Quantity,
                LandedUnitCost = awardLandedUnitCost,
                Rationale = command.Rationale?.Trim(),
                AwardedByUserId = command.AwardedByUserId,
                AwardedByAgent = false,
                CreatedOn = now
            };
            _db.Add(award);
            await _db.SaveChangesAsync(ct);
            AddEvent(command.BusinessUnitId, "SourcingAward", award.Id, award.Version, "SOURCING_AWARD_APPROVED",
                command.Actor, command.CorrelationId, command.IdempotencyKey, JsonSerializer.Serialize(new { quote.Id, command.Quantity }), now);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new AwardResult(award.Id, award.Status, award.LandedUnitCost.Value, false);
        });
    }

    public async Task<PurchaseOrderResult> CreatePurchaseOrderAsync(CreatePurchaseOrderCommand command, CancellationToken ct = default)
    {
        ValidateCommand(command.BusinessUnitId, command.IdempotencyKey, command.Actor, command.CorrelationId);
        if (command.AwardIds.Count == 0 || command.AwardIds.Distinct().Count() != command.AwardIds.Count)
            throw new ProcurementValidationException("At least one distinct award is required.");
        var hash = Hash(command with { Actor = string.Empty, CorrelationId = string.Empty });
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var replay = await _db.SupplierPurchaseOrders.SingleOrDefaultAsync(x => x.BusinessUnitId == command.BusinessUnitId
                && x.IdempotencyKey == command.IdempotencyKey.Trim(), ct);
            if (replay is not null)
            {
                EnsureReplay(replay.RequestHash, hash);
                await tx.CommitAsync(ct);
                return new PurchaseOrderResult(replay.Id, replay.PurchaseOrderNumber, replay.Status, true);
            }
            await RequireRfqAsync(command.BusinessUnitId, command.RfqId, ct);
            var supplier = await RequireSupplierAsync(command.BusinessUnitId, command.SupplierId, ct);
            var supplierBlockers = SupplierRfqBlockingReasons(supplier);
            if (supplierBlockers.Count > 0)
                throw new ProcurementValidationException(
                    $"The PO supplier is not eligible: {string.Join("; ", supplierBlockers)}.");
            var warehouse = await _db.Warehouses.AsNoTracking().SingleOrDefaultAsync(x => x.Id == command.WarehouseId
                && x.BusinessUnitId == command.BusinessUnitId, ct) ?? throw new ProcurementValidationException("Warehouse was not found in the authenticated tenant.");
            var awards = await _db.Set<SourcingAward>().Where(x => x.BusinessUnitId == command.BusinessUnitId
                && command.AwardIds.Contains(x.Id)).ToListAsync(ct);
            if (awards.Count != command.AwardIds.Count || awards.Any(x => x.Status is not ("APPROVED" or "SPLIT_APPROVED") || x.RfqId != command.RfqId
                || x.SupplierId != command.SupplierId || x.CurrencyId != command.CurrencyId || x.SupplierQuotedItemId is null
                || x.RfqItemId is null || x.Quantity is null || x.LandedUnitCost is null))
                throw new ProcurementValidationException("Every PO award must be approved and match the RFQ, supplier, and currency.");
            var quoteIds = awards.Select(x => x.SupplierQuotedItemId!.Value).ToArray();
            var quotes = await _db.SupplierQuotedItems.Where(x => x.BusinessUnitId == command.BusinessUnitId && quoteIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, ct);
            var sourceQuoteIds = quotes.Values.Where(x => x.SourceSupplierQuoteId.HasValue)
                .Select(x => x.SourceSupplierQuoteId!.Value).Distinct().ToArray();
            var sourceRevisionIds = quotes.Values.Where(x => x.SourceSupplierQuoteRevisionId.HasValue)
                .Select(x => x.SourceSupplierQuoteRevisionId!.Value).Distinct().ToArray();
            var canonicalRevisions = await _db.SupplierQuoteRevisions.AsNoTracking()
                .Include(x => x.SupplierQuote).Include(x => x.Lines)
                .Include(x => x.Evidence).Include(x => x.ReviewDecisions)
                .Where(x => x.BusinessUnitId == command.BusinessUnitId &&
                    sourceRevisionIds.Contains(x.Id) && sourceQuoteIds.Contains(x.SupplierQuoteId))
                .ToDictionaryAsync(x => x.Id, ct);
            var latestCorrectionByRevision = canonicalRevisions.ToDictionary(
                x => x.Key, x => LatestProjectionCorrectionOn(x.Value));
            var rfqItemIds = awards.Select(x => x.RfqItemId!.Value).Distinct().ToArray();
            var requiredDates = await _db.Rfqitems.AsNoTracking().Where(x => x.Rfq.BusinessUnitId == command.BusinessUnitId &&
                    rfqItemIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.RequiredDesiredDate, ct);
            if (quotes.Count != quoteIds.Distinct().Count() || requiredDates.Count != rfqItemIds.Length ||
                quotes.Values.Any(x => !x.IsActive || x.ProductId is null ||
                    x.ValidUntil <= DateTime.UtcNow ||
                    !canonicalRevisions.TryGetValue(x.SourceSupplierQuoteRevisionId ?? 0, out var revision) ||
                    !HasCurrentCanonicalAuthorization(x, revision) ||
                    latestCorrectionByRevision.GetValueOrDefault(x.SourceSupplierQuoteRevisionId!.Value) >
                        (x.ModifiedDate ?? x.CreatedDate) ||
                    requiredDates[x.RfqItemId!.Value].HasValue &&
                    (!x.QuoteDate.HasValue || !x.LeadTimeDays.HasValue ||
                     x.QuoteDate.Value.AddDays(x.LeadTimeDays.Value) > requiredDates[x.RfqItemId.Value]!.Value)))
                throw new ProcurementValidationException("Every award must retain a valid authoritative supplier quote and product.");
            var existingAwardLines = await _db.SupplierPurchaseOrderLines.AnyAsync(x => x.BusinessUnitId == command.BusinessUnitId
                && command.AwardIds.Contains(x.SourcingAwardId), ct);
            if (existingAwardLines) throw new ProcurementConflictException("One or more awards have already been converted to a purchase order.");
            var now = DateTime.UtcNow;
            if (command.ExpectedOn <= DateOnly.FromDateTime(now))
                throw new ProcurementValidationException("Expected delivery must be after the purchase order creation UTC date.");
            var po = new SupplierPurchaseOrder
            {
                BusinessUnitId = command.BusinessUnitId, RfqId = command.RfqId, SupplierId = command.SupplierId,
                CurrencyId = command.CurrencyId, PurchaseOrderNumber = $"PENDING-{hash[..32]}", Status = SupplierPurchaseOrderStatuses.Draft,
                TotalValue = awards.Sum(x => x.LandedUnitCost!.Value * x.Quantity!.Value), ExpectedOn = command.ExpectedOn,
                IdempotencyKey = command.IdempotencyKey.Trim(), RequestHash = hash, CreatedOn = now, CreatedBy = command.Actor.Trim()
            };
            _db.SupplierPurchaseOrders.Add(po);
            await _db.SaveChangesAsync(ct);
            po.PurchaseOrderNumber = $"PO-{now:yyyy}-{po.Id:D10}";
            await _db.SaveChangesAsync(ct);
            foreach (var award in awards)
            {
                var quote = quotes[award.SupplierQuotedItemId!.Value];
                var rfqItemId = award.RfqItemId ?? throw new ProcurementValidationException("The award lost its RFQ line identity.");
                var productId = quote.ProductId ?? throw new ProcurementValidationException("The Supplier quote lost its Product identity.");
                var orderedQuantity = award.Quantity ?? throw new ProcurementValidationException("The award lost its quantity.");
                var inventory = await _db.Set<Models.Inventory>().SingleOrDefaultAsync(x => x.Buid == command.BusinessUnitId
                    && x.ProductId == productId && x.WarehouseId == command.WarehouseId, ct);
                if (inventory is null)
                {
                    var product = await _db.Products.AsNoTracking().SingleAsync(x => x.Id == productId
                        && x.Buid == command.BusinessUnitId, ct);
                    inventory = new Models.Inventory
                    {
                        Buid = command.BusinessUnitId,
                        ProductId = product.Id,
                        WarehouseId = command.WarehouseId,
                        PartNo = product.PartNo,
                        ProductName = product.ProductName,
                        Description = product.Description,
                        QtyOnHand = 0,
                        ReorderPoint = product.ReorderPoint,
                        UnitCost = quote.LandedUnitCost,
                        CreatedBy = command.Actor.Trim(),
                        CreatedOn = now
                    };
                    _db.Add(inventory);
                    await _db.SaveChangesAsync(ct);
                }
                po.Lines.Add(new SupplierPurchaseOrderLine
                {
                    BusinessUnitId = command.BusinessUnitId, SourcingAwardId = award.Id, SupplierQuotedItemId = quote.Id,
                    RfqId = command.RfqId, RfqItemId = rfqItemId, ProductId = productId,
                    WarehouseId = command.WarehouseId, InventoryId = inventory?.Id,
                    OrderedQuantity = orderedQuantity, UnitCost = quote.UnitPrice!.Value, LandedUnitCost = award.LandedUnitCost!.Value
                });
                award.Status = "CONVERTED_TO_PO";
                award.Version++;
            }
            AddEvent(command.BusinessUnitId, "SupplierPurchaseOrder", po.Id, po.Version, "SUPPLIER_PO_CREATED",
                command.Actor, command.CorrelationId, command.IdempotencyKey, JsonSerializer.Serialize(new { command.AwardIds, command.WarehouseId }), now);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new PurchaseOrderResult(po.Id, po.PurchaseOrderNumber, po.Status, false);
        });
    }

    public async Task<PurchaseOrderResult> IssuePurchaseOrderAsync(IssuePurchaseOrderCommand command, CancellationToken ct = default)
    {
        ValidateCommand(command.BusinessUnitId, command.IdempotencyKey, command.Actor, command.CorrelationId);
        var evidenceReference = command.DeliveryEvidenceReference?.Trim() ?? string.Empty;
        var evidenceSha256 = command.DeliveryEvidenceSha256?.Trim().ToLowerInvariant();
        var deliveredOn = command.DeliveredOn;
        ValidateDeliveryEvidence(evidenceReference, evidenceSha256, deliveredOn);
        var hash = Hash(new
        {
            command.PurchaseOrderId,
            command.ExpectedVersion,
            deliveryEvidenceReference = evidenceReference,
            deliveryEvidenceSha256 = evidenceSha256,
            deliveredOn
        });
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var replayEvent = await _db.ProcurementEvents.AsNoTracking().SingleOrDefaultAsync(x =>
                x.BusinessUnitId == command.BusinessUnitId && x.EventType == "SUPPLIER_PO_ISSUED"
                && x.IdempotencyKey == command.IdempotencyKey.Trim(), ct);
            if (replayEvent is not null)
            {
                using var payload = JsonDocument.Parse(replayEvent.PayloadJson);
                EnsureReplay(payload.RootElement.GetProperty("requestHash").GetString(), hash);
                var replayPo = await _db.SupplierPurchaseOrders.SingleAsync(x => x.BusinessUnitId == command.BusinessUnitId
                    && x.Id == replayEvent.AggregateId, ct);
                await tx.CommitAsync(ct);
                return new PurchaseOrderResult(replayPo.Id, replayPo.PurchaseOrderNumber, replayPo.Status, true);
            }

            var po = await _db.SupplierPurchaseOrders.Include(x => x.Lines).SingleOrDefaultAsync(x => x.BusinessUnitId == command.BusinessUnitId
                && x.Id == command.PurchaseOrderId, ct) ?? throw new ProcurementValidationException("Purchase order was not found.");
            if (po.Version != command.ExpectedVersion)
                throw new ProcurementConflictException("The purchase order changed; refresh before issuing.");
            if (po.Status != SupplierPurchaseOrderStatuses.Draft)
                throw new ProcurementConflictException("Only a draft purchase order can be issued.");
            var now = DateTime.UtcNow;
            if (deliveredOn!.Value < po.CreatedOn || deliveredOn.Value > now.AddMinutes(5))
                throw new ProcurementValidationException("The delivery evidence timestamp must be UTC, after PO creation, and not future-dated.");
            if (po.ExpectedOn is null || po.ExpectedOn <= DateOnly.FromDateTime(now))
                throw new ProcurementValidationException("Expected delivery must still be after the purchase order issuance UTC date.");
            var quoteIds = po.Lines.Select(x => x.SupplierQuotedItemId).Distinct().ToArray();
            var validQuoteCount = await _db.SupplierQuotedItems.AsNoTracking().CountAsync(x =>
                x.BusinessUnitId == command.BusinessUnitId && quoteIds.Contains(x.Id)
                && x.ValidUntil != null && x.ValidUntil > now, ct);
            if (validQuoteCount != quoteIds.Length)
                throw new ProcurementValidationException("Every purchase order line must retain an unexpired authoritative supplier quote at issuance.");
            po.Status = SupplierPurchaseOrderStatuses.Issued;
            po.Version++;
            po.ModifiedOn = now;
            po.ModifiedBy = command.Actor.Trim();
            foreach (var line in po.Lines)
            {
                if (line.IncomingInventoryId is not null)
                    throw new ProcurementConflictException("The purchase order already has incoming inventory evidence.");
                var incoming = new IncomingInventory
                {
                    BusinessUnitId = command.BusinessUnitId,
                    ProductId = line.ProductId,
                    InventoryId = line.InventoryId,
                    WarehouseId = line.WarehouseId,
                    OrderedQuantity = line.OrderedQuantity,
                    ReceivedQuantity = 0,
                    // SUPPLY COMMITMENT: this was hard-coded to 0 and nothing ever incremented it,
                    // so IncomingInventory.OpenQuantity subtracted nothing and the same inbound
                    // container read as free supply to every customer line that asked — two
                    // customers could be promised the same units.
                    //
                    // Every IncomingInventory row is created here, and only here, from a PO line
                    // whose SourcingAward carries the customer RFQ line it was raised to cover
                    // (SupplierPurchaseOrderLine.RfqItemId). The inbound quantity is therefore
                    // already spoken for on arrival: it is committed supply, not free supply.
                    // Marking it allocated makes OpenQuantity report the truth (zero uncommitted
                    // units) so no second line can be promised against it. The RFQ line that owns
                    // it is unaffected — GetNetSourcingRequirementAsync nets its coverage from the
                    // purchase order lines themselves, not from this bucket.
                    AllocatedQuantity = line.OrderedQuantity,
                    ExpectedOn = po.ExpectedOn ?? throw new ProcurementValidationException("An expected delivery date is required before issuing."),
                    Status = IncomingInventoryStatus.Ordered,
                    SourceType = "SupplierPurchaseOrderLine",
                    SourceId = $"{po.Id}:{line.SourcingAwardId}"
                };
                _db.IncomingInventory.Add(incoming);
                await _db.SaveChangesAsync(ct);
                line.IncomingInventoryId = incoming.Id;
                line.Version++;
            }
            AddEvent(command.BusinessUnitId, "SupplierPurchaseOrder", po.Id, po.Version, "SUPPLIER_PO_ISSUED",
                command.Actor, command.CorrelationId, command.IdempotencyKey,
                JsonSerializer.Serialize(new
                {
                    requestHash = hash,
                    deliveryEvidenceReference = evidenceReference,
                    deliveryEvidenceSha256 = evidenceSha256,
                    deliveredOn
                }), deliveredOn.Value);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new PurchaseOrderResult(po.Id, po.PurchaseOrderNumber, po.Status, false);
        });
    }

    /// <summary>
    /// Cancels a purchase order that will never be received and gives its RFQ line back to
    /// sourcing.
    ///
    /// <para>Nothing in this codebase ever assigned <see cref="SupplierPurchaseOrderStatuses.Cancelled"/>
    /// before this method existed — only Draft, Issued, PartiallyReceived and Received. A draft PO
    /// whose supplier quotes had expired could not be issued (issuance requires unexpired quotes)
    /// and could not be cancelled, but it still counted as committed supply, so
    /// <c>GetNetSourcingRequirementAsync</c> returned zero and <c>CreateSolicitationAsync</c> threw
    /// "already fully covered". The customer line was unfulfillable forever.</para>
    ///
    /// <para>Cancellation is refused once any goods have physically arrived: those units are
    /// already on hand under the stock ledger and cancelling their paperwork would strand them.</para>
    /// </summary>
    public async Task<PurchaseOrderResult> CancelPurchaseOrderAsync(CancelPurchaseOrderCommand command, CancellationToken ct = default)
    {
        ValidateCommand(command.BusinessUnitId, command.IdempotencyKey, command.Actor, command.CorrelationId);
        var reason = command.Reason?.Trim() ?? string.Empty;
        if (reason.Length is 0 or > 500)
            throw new ProcurementValidationException("A cancellation reason of up to 500 characters is required.");
        var hash = Hash(new { command.PurchaseOrderId, command.ExpectedVersion, reason });
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var replayEvent = await _db.ProcurementEvents.AsNoTracking().SingleOrDefaultAsync(x =>
                x.BusinessUnitId == command.BusinessUnitId && x.EventType == "SUPPLIER_PO_CANCELLED"
                && x.IdempotencyKey == command.IdempotencyKey.Trim(), ct);
            if (replayEvent is not null)
            {
                using var payload = JsonDocument.Parse(replayEvent.PayloadJson);
                EnsureReplay(payload.RootElement.GetProperty("requestHash").GetString(), hash);
                var replayPo = await _db.SupplierPurchaseOrders.SingleAsync(x => x.BusinessUnitId == command.BusinessUnitId
                    && x.Id == replayEvent.AggregateId, ct);
                await tx.CommitAsync(ct);
                return new PurchaseOrderResult(replayPo.Id, replayPo.PurchaseOrderNumber, replayPo.Status, true);
            }

            var po = await _db.SupplierPurchaseOrders.Include(x => x.Lines)
                .SingleOrDefaultAsync(x => x.BusinessUnitId == command.BusinessUnitId && x.Id == command.PurchaseOrderId, ct)
                ?? throw new ProcurementValidationException("Purchase order was not found.");
            if (po.Version != command.ExpectedVersion)
                throw new ProcurementConflictException("The purchase order changed; refresh before cancelling.");
            if (po.Status == SupplierPurchaseOrderStatuses.Cancelled)
                throw new ProcurementConflictException("The purchase order is already cancelled.");
            // The only remaining statuses are PARTIALLY_RECEIVED and RECEIVED, both of which mean
            // stock has physically landed and is already governed by the reservation ledger;
            // cancelling their paperwork would strand it. The line-level check is the same rule
            // stated against the quantities, so a future status transition cannot bypass it.
            if (po.Status is not (SupplierPurchaseOrderStatuses.Draft or SupplierPurchaseOrderStatuses.Issued)
                || po.Lines.Any(x => x.ReceivedQuantity > 0m))
                throw new ProcurementConflictException(
                    "Goods have already been received against this purchase order; only a draft or "
                    + "issued purchase order can be cancelled.");

            var now = DateTime.UtcNow;
            po.Status = SupplierPurchaseOrderStatuses.Cancelled;
            po.Version++;
            po.ModifiedOn = now;
            po.ModifiedBy = command.Actor.Trim();

            // The inbound supply this PO promised no longer exists. Cancelling the IncomingInventory
            // row drops it out of OpenQuantity, every incoming-supply projection, and the
            // commercial resolution classifier in one move.
            var incomingIds = po.Lines.Where(x => x.IncomingInventoryId is not null)
                .Select(x => x.IncomingInventoryId!.Value).Distinct().ToArray();
            if (incomingIds.Length > 0)
            {
                var incomingRows = await _db.IncomingInventory
                    .Where(x => x.BusinessUnitId == command.BusinessUnitId && incomingIds.Contains(x.Id))
                    .ToListAsync(ct);
                foreach (var incoming in incomingRows)
                {
                    _db.Entry(incoming).Property(x => x.Status).CurrentValue = IncomingInventoryStatus.Cancelled;
                    _db.Entry(incoming).Property(x => x.AllocatedQuantity).CurrentValue = 0m;
                }
            }

            // The awards die with the purchase order they were converted into: the PO lines stay
            // for audit, so an award can never be re-converted, and leaving them "CONVERTED_TO_PO"
            // would claim a commitment that no longer exists. The RFQ line is re-sourced from
            // scratch, which is exactly what a cancelled order requires.
            var awardIds = po.Lines.Select(x => x.SourcingAwardId).Distinct().ToArray();
            var awards = await _db.Set<SourcingAward>()
                .Where(x => x.BusinessUnitId == command.BusinessUnitId && awardIds.Contains(x.Id))
                .ToListAsync(ct);
            foreach (var award in awards)
            {
                award.Status = "CANCELLED";
                award.Version++;
            }

            AddEvent(command.BusinessUnitId, "SupplierPurchaseOrder", po.Id, po.Version, "SUPPLIER_PO_CANCELLED",
                command.Actor, command.CorrelationId, command.IdempotencyKey,
                JsonSerializer.Serialize(new { requestHash = hash, reason, awardIds, incomingIds }), now);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new PurchaseOrderResult(po.Id, po.PurchaseOrderNumber, po.Status, false);
        });
    }

    public async Task<GoodsReceiptResult> PostGoodsReceiptAsync(PostGoodsReceiptCommand command, CancellationToken ct = default)
    {
        ValidateCommand(command.BusinessUnitId, command.IdempotencyKey, command.Actor, command.CorrelationId);
        if (command.Lines.Count == 0 || command.Lines.Select(x => x.PurchaseOrderLineId).Distinct().Count() != command.Lines.Count
            || command.Lines.Any(x => x.Quantity <= 0) || string.IsNullOrWhiteSpace(command.ReceiptNumber))
            throw new ProcurementValidationException("A receipt number and distinct positive receipt lines are required.");
        var hash = ReceiptBusinessHash(command);
        var strategy = _db.Database.CreateExecutionStrategy();
        try
        {
            return await strategy.ExecuteAsync(async () =>
            {
            await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var replay = await _db.GoodsReceipts.SingleOrDefaultAsync(x => x.BusinessUnitId == command.BusinessUnitId
                && x.IdempotencyKey == command.IdempotencyKey.Trim(), ct);
            if (replay is not null)
            {
                EnsureReplay(replay.RequestHash, hash);
                var replayPoStatus = await _db.SupplierPurchaseOrders.Where(x => x.BusinessUnitId == command.BusinessUnitId
                    && x.Id == replay.SupplierPurchaseOrderId).Select(x => x.Status).SingleAsync(ct);
                await tx.CommitAsync(ct);
                return new GoodsReceiptResult(replay.Id, replay.ReceiptNumber, replayPoStatus, true);
            }
            var normalizedReceiptNumber = command.ReceiptNumber.Trim().ToUpperInvariant();
            var sameBusinessReceipt = await _db.GoodsReceipts.SingleOrDefaultAsync(x =>
                x.BusinessUnitId == command.BusinessUnitId && x.ReceiptNumber.ToUpper() == normalizedReceiptNumber, ct);
            if (sameBusinessReceipt is not null)
            {
                EnsureReplay(sameBusinessReceipt.RequestHash, hash);
                var existingPoStatus = await _db.SupplierPurchaseOrders.Where(x => x.BusinessUnitId == command.BusinessUnitId
                    && x.Id == sameBusinessReceipt.SupplierPurchaseOrderId).Select(x => x.Status).SingleAsync(ct);
                await tx.CommitAsync(ct);
                return new GoodsReceiptResult(sameBusinessReceipt.Id, sameBusinessReceipt.ReceiptNumber, existingPoStatus, true);
            }
            var po = await _db.SupplierPurchaseOrders.Include(x => x.Lines).SingleOrDefaultAsync(x => x.BusinessUnitId == command.BusinessUnitId
                && x.Id == command.PurchaseOrderId, ct) ?? throw new ProcurementValidationException("Purchase order was not found.");
            if (po.Version != command.ExpectedPurchaseOrderVersion) throw new ProcurementConflictException("The purchase order changed; refresh before receiving.");
            if (po.Status is not (SupplierPurchaseOrderStatuses.Issued or SupplierPurchaseOrderStatuses.PartiallyReceived))
                throw new ProcurementConflictException("Only an issued or partially received purchase order is open for receipt.");
            var now = DateTime.UtcNow;
            var issuedOn = await _db.ProcurementEvents.AsNoTracking()
                .Where(x => x.BusinessUnitId == command.BusinessUnitId
                    && x.AggregateType == "SupplierPurchaseOrder" && x.AggregateId == po.Id
                    && x.EventType == "SUPPLIER_PO_ISSUED")
                .OrderByDescending(x => x.AggregateVersion)
                .Select(x => (DateTime?)x.OccurredOn)
                .FirstOrDefaultAsync(ct)
                ?? throw new ProcurementConflictException("The purchase order has no authoritative issuance event.");
            if (command.ReceivedOn.Date < issuedOn.Date || command.ReceivedOn > now.AddMinutes(5))
                throw new ProcurementValidationException("Receipt date cannot precede the PO issuance date or be future-dated.");
            if (po.Lines.Any(x => x.WarehouseId != command.WarehouseId) || command.Lines.Any(x => po.Lines.All(line => line.Id != x.PurchaseOrderLineId)))
                throw new ProcurementValidationException("Every receipt line must belong to this PO and warehouse.");
            var receipt = new GoodsReceipt
            {
                BusinessUnitId = command.BusinessUnitId, SupplierPurchaseOrderId = po.Id, WarehouseId = command.WarehouseId,
                ReceiptNumber = command.ReceiptNumber.Trim(), ReceivedOn = command.ReceivedOn, IdempotencyKey = command.IdempotencyKey.Trim(),
                RequestHash = hash, CreatedOn = now, CreatedBy = command.Actor.Trim()
            };
            _db.GoodsReceipts.Add(receipt);
            await _db.SaveChangesAsync(ct);
            foreach (var requested in command.Lines)
            {
                var line = po.Lines.Single(x => x.Id == requested.PurchaseOrderLineId);
                if (line.ReceivedQuantity + requested.Quantity > line.OrderedQuantity)
                    throw new ProcurementValidationException($"Receipt quantity exceeds the open quantity for PO line {line.Id}.");
                var inventory = line.InventoryId is not null
                    ? await _db.Set<Models.Inventory>().SingleOrDefaultAsync(x => x.Id == line.InventoryId && x.Buid == command.BusinessUnitId, ct)
                    : await _db.Set<Models.Inventory>().SingleOrDefaultAsync(x => x.Buid == command.BusinessUnitId
                        && x.WarehouseId == command.WarehouseId && x.ProductId == line.ProductId, ct);
                if (inventory is null) throw new ProcurementValidationException($"No tenant inventory record exists for PO line {line.Id}.");
                var movement = new InventoryMovement
                {
                    BusinessUnitId = command.BusinessUnitId, ProductId = line.ProductId, InventoryId = inventory.Id,
                    WarehouseId = command.WarehouseId, Type = InventoryMovementType.Receipt, Quantity = requested.Quantity,
                    OccurredOn = command.ReceivedOn, IdempotencyKey = $"{command.IdempotencyKey.Trim()}:line:{line.Id}",
                    SourceType = "GoodsReceipt", SourceId = receipt.Id.ToString(), CreatedBy = command.Actor.Trim(), CreatedOn = now
                };
                _db.InventoryMovements.Add(movement);
                await _db.SaveChangesAsync(ct);
                receipt.Lines.Add(new GoodsReceiptLine
                {
                    BusinessUnitId = command.BusinessUnitId, SupplierPurchaseOrderLineId = line.Id, ProductId = line.ProductId,
                    InventoryId = inventory.Id, WarehouseId = command.WarehouseId, InventoryMovementId = movement.Id,
                    ReceivedQuantity = requested.Quantity
                });
                inventory.QtyOnHand += requested.Quantity;
                inventory.ModifiedBy = command.Actor.Trim();
                inventory.ModifiedOn = now;
                line.InventoryId = inventory.Id;
                line.ReceivedQuantity += requested.Quantity;
                line.Version++;
                if (line.IncomingInventoryId is not null)
                {
                    var incoming = await _db.IncomingInventory.SingleAsync(x => x.BusinessUnitId == command.BusinessUnitId
                        && x.Id == line.IncomingInventoryId.Value, ct);
                    var received = incoming.ReceivedQuantity + requested.Quantity;
                    _db.Entry(incoming).Property(x => x.ReceivedQuantity).CurrentValue = received;
                    // Units that have physically landed are no longer inbound commitments: they
                    // are now on-hand stock governed by the reservation ledger. Releasing the
                    // matching allocation keeps the invariant Allocated + Received <= Ordered,
                    // which is what stops OpenQuantity's non-negativity guard from tripping.
                    _db.Entry(incoming).Property(x => x.AllocatedQuantity).CurrentValue =
                        Math.Max(0m, Math.Min(incoming.AllocatedQuantity, incoming.OrderedQuantity - received));
                    _db.Entry(incoming).Property(x => x.Status).CurrentValue = received >= incoming.OrderedQuantity
                        ? IncomingInventoryStatus.Received : IncomingInventoryStatus.PartiallyReceived;
                }
            }
            po.Status = po.Lines.All(x => x.ReceivedQuantity >= x.OrderedQuantity)
                ? SupplierPurchaseOrderStatuses.Received : SupplierPurchaseOrderStatuses.PartiallyReceived;
            po.Version++;
            po.ModifiedOn = now;
            po.ModifiedBy = command.Actor.Trim();
            AddEvent(command.BusinessUnitId, "GoodsReceipt", receipt.Id, receipt.Version, "GOODS_RECEIPT_POSTED",
                command.Actor, command.CorrelationId, command.IdempotencyKey, JsonSerializer.Serialize(new { po.Id, command.Lines }), now);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
                return new GoodsReceiptResult(receipt.Id, receipt.ReceiptNumber, po.Status, false);
            });
        }
        catch (Exception exception) when (IsConcurrentWriteFailure(exception))
        {
            throw new ProcurementConflictException("The purchase order was updated concurrently; refresh before receiving.");
        }
    }

    private async Task RefreshCandidatesAsync(SourcingCase sourcingCase, int limit, DateTime now, CancellationToken ct)
    {
        var suppliers = await _db.Suppliers.AsNoTracking()
            .Where(x => x.Buid == sourcingCase.BusinessUnitId && x.IsActive == true)
            .Select(x => new { x.Id, x.Name, x.Tags })
            .ToListAsync(ct);
        var supplierIds = suppliers.Select(x => x.Id).ToHashSet();
        var evidence = new Dictionary<long, CandidateEvidence>();

        if (sourcingCase.ProductId.HasValue)
        {
            var product = await _db.Products.AsNoTracking().Where(x => x.Buid == sourcingCase.BusinessUnitId
                    && x.Id == sourcingCase.ProductId.Value)
                .Select(x => new { x.Id, x.PreferredSupplierId })
                .SingleOrDefaultAsync(ct);
            if (product?.PreferredSupplierId is long preferredId && supplierIds.Contains(preferredId))
                AddCandidateEvidence(evidence, preferredId, SourcingCandidateEvidenceTypes.PreferredSupplier,
                    "Preferred supplier recorded on the matched Product", 100m, null,
                    new { productId = product.Id, preferredSupplierId = preferredId });

            var quotes = await _db.SupplierQuotedItems.AsNoTracking()
                .Where(x => x.BusinessUnitId == sourcingCase.BusinessUnitId
                    && x.ProductId == sourcingCase.ProductId.Value && x.IsActive
                    && supplierIds.Contains(x.SupplierId))
                .GroupBy(x => x.SupplierId)
                .Select(group => new
                {
                    SupplierId = group.Key,
                    Count = group.Count(),
                    FreshOn = group.Max(x => x.CreatedDate),
                    LastQuoteId = group.Max(x => x.Id)
                }).ToListAsync(ct);
            foreach (var quote in quotes)
                AddCandidateEvidence(evidence, quote.SupplierId, SourcingCandidateEvidenceTypes.PriorSupplierQuote,
                    "Prior persisted Supplier Quote for this Product", 90m, quote.FreshOn,
                    new { productId = sourcingCase.ProductId.Value, quote.Count, quote.LastQuoteId });

            var purchaseOrders = await (
                from line in _db.SupplierPurchaseOrderLines.AsNoTracking()
                join po in _db.SupplierPurchaseOrders.AsNoTracking()
                    on new { line.BusinessUnitId, Id = line.SupplierPurchaseOrderId }
                    equals new { po.BusinessUnitId, po.Id }
                where line.BusinessUnitId == sourcingCase.BusinessUnitId
                    && line.ProductId == sourcingCase.ProductId.Value
                    && po.Status != SupplierPurchaseOrderStatuses.Cancelled
                    && supplierIds.Contains(po.SupplierId)
                group new { line, po } by po.SupplierId into grouped
                select new
                {
                    SupplierId = grouped.Key,
                    Count = grouped.Count(),
                    FreshOn = grouped.Max(x => x.po.CreatedOn),
                    LastPurchaseOrderId = grouped.Max(x => x.po.Id)
                }).ToListAsync(ct);
            foreach (var purchaseOrder in purchaseOrders)
                AddCandidateEvidence(evidence, purchaseOrder.SupplierId, SourcingCandidateEvidenceTypes.PurchaseOrderHistory,
                    "Prior persisted Supplier Purchase Order for this Product", 70m, purchaseOrder.FreshOn,
                    new { productId = sourcingCase.ProductId.Value, purchaseOrder.Count, purchaseOrder.LastPurchaseOrderId });
        }

        var searchTerms = new[] { sourcingCase.RequestedPartNumber, sourcingCase.Manufacturer }
            .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (searchTerms.Length > 0)
        {
            foreach (var supplier in suppliers.Where(supplier => searchTerms.Any(term =>
                         ContainsInvariant(supplier.Tags, term) || ContainsInvariant(supplier.Name, term))))
                AddCandidateEvidence(evidence, supplier.Id, SourcingCandidateEvidenceTypes.SupplierMetadata,
                    "Persisted supplier metadata matches the requested part or manufacturer", 50m,
                    null, new { matchedTerms = searchTerms });
        }

        var ranked = evidence.Values.OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.FreshOn).ThenBy(x => x.SupplierId).Take(limit).ToArray();
        for (var index = 0; index < ranked.Length; index++)
        {
            var row = ranked[index];
            sourcingCase.Candidates.Add(new SourcingCaseCandidate
            {
                BusinessUnitId = sourcingCase.BusinessUnitId,
                SupplierId = row.SupplierId,
                Rank = index + 1,
                EvidenceType = row.EvidenceType,
                RecommendationReason = row.Reason,
                EvidenceJson = row.EvidenceJson,
                EvidenceScore = row.Score,
                EvidenceFreshOn = row.FreshOn,
                CreatedOn = now,
                UpdatedOn = now
            });
        }
    }

    private static void AddCandidateEvidence(Dictionary<long, CandidateEvidence> evidence, long supplierId,
        string evidenceType, string reason, decimal score, DateTime? freshOn, object detail)
    {
        var candidate = new CandidateEvidence(supplierId, evidenceType, reason, score, freshOn,
            JsonSerializer.Serialize(detail));
        if (!evidence.TryGetValue(supplierId, out var existing) || candidate.Score > existing.Score
            || candidate.Score == existing.Score && candidate.FreshOn > existing.FreshOn)
            evidence[supplierId] = candidate;
    }

    private async Task<SourcingCaseView> ToSourcingCaseViewAsync(SourcingCase sourcingCase, CancellationToken ct)
    {
        var candidates = await ToCandidateViewsAsync(sourcingCase.BusinessUnitId, sourcingCase.Candidates, ct);
        return new SourcingCaseView(sourcingCase.Id, sourcingCase.CommercialDemandLineId,
            sourcingCase.RfqId, sourcingCase.RfqItemId, sourcingCase.NexoraSerial, sourcingCase.ProductId,
            sourcingCase.RequestedPartNumber, sourcingCase.Description, sourcingCase.RequestedQuantity,
            sourcingCase.StockQuantity, sourcingCase.UnfulfilledQuantity, sourcingCase.RequiredOn,
            sourcingCase.SearchLimit, sourcingCase.Status, sourcingCase.NextAction, sourcingCase.Version, candidates);
    }

    private async Task<IReadOnlyCollection<SourcingCandidateView>> ToCandidateViewsAsync(
        long businessUnitId, IEnumerable<SourcingCaseCandidate> candidateRows, CancellationToken ct)
    {
        var rows = candidateRows.OrderBy(x => x.Rank).ToArray();
        var supplierIds = rows.Select(x => x.SupplierId).Distinct().ToArray();
        var suppliers = await _db.Suppliers.AsNoTracking()
            .Where(x => x.Buid == businessUnitId && supplierIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, ct);
        return rows.Where(x => suppliers.ContainsKey(x.SupplierId)).Select(x =>
        {
            var supplier = suppliers[x.SupplierId];
            var blockers = SupplierRfqBlockingReasons(supplier);
            return new SourcingCandidateView(x.Id, x.SupplierId, supplier.Name, supplier.ContactEmail,
                x.Rank, x.EvidenceType, x.RecommendationReason, x.EvidenceScore, x.EvidenceFreshOn, x.Selected,
                supplier.GovernanceStatus, supplier.VerificationStatus, supplier.ComplianceStatus,
                supplier.RiskStatus, supplier.ReadinessStatus, blockers.Count == 0, blockers);
        }).ToArray();
    }

    private static IReadOnlyCollection<string> SupplierRfqBlockingReasons(Supplier supplier)
    {
        var reasons = new List<string>();
        if (supplier.IsActive != true)
            reasons.Add("Supplier must be active");
        if (string.IsNullOrWhiteSpace(supplier.ContactEmail))
            reasons.Add("A verified dispatch contact is required");
        if (supplier.GovernanceStatus is not (SupplierGovernanceStatuses.Approved
            or SupplierGovernanceStatuses.Preferred or SupplierGovernanceStatuses.Provisional))
            reasons.Add("Supplier approval or explicit provisional approval is required");
        if (supplier.VerificationStatus != SupplierVerificationStatuses.Verified)
            reasons.Add("Supplier verification status must be VERIFIED");
        if (supplier.ReadinessStatus != SupplierReadinessStatuses.Ready)
            reasons.Add("Supplier outreach readiness must be READY");
        if (supplier.ComplianceStatus != SupplierComplianceStatuses.Cleared)
            reasons.Add("Supplier compliance status must be CLEARED");
        if (supplier.RiskStatus is "BLOCKED" or "HIGH")
            reasons.Add("Supplier risk status blocks outreach");
        return reasons;
    }

    private static string? ExtractRequestHash(string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.TryGetProperty("requestHash", out var value) ? value.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record CandidateSearchSnapshot(
        int RequestedLimit, long Version, CandidateEvidenceSnapshot[] Candidates);

    private sealed record PreparedSupplierRfqSnapshot(
        long SupplierSolicitationId, long SourcingCaseVersion, long SolicitationVersion);

    private sealed record CandidateEvidenceSnapshot(
        long Id, long SourcingCaseId, long SupplierId, int Rank, string EvidenceType,
        string RecommendationReason, string EvidenceJson, decimal EvidenceScore,
        DateTime? EvidenceFreshOn, bool Selected, DateTime CreatedOn, DateTime UpdatedOn);

    private static CandidateEvidenceSnapshot ToCandidateSnapshot(SourcingCaseCandidate candidate) => new(
        candidate.Id, candidate.SourcingCaseId, candidate.SupplierId, candidate.Rank,
        candidate.EvidenceType, candidate.RecommendationReason, candidate.EvidenceJson,
        candidate.EvidenceScore, candidate.EvidenceFreshOn, candidate.Selected,
        candidate.CreatedOn, candidate.UpdatedOn);

    private static SourcingCaseCandidate ToCandidateEntity(CandidateEvidenceSnapshot candidate) => new()
    {
        Id = candidate.Id,
        SourcingCaseId = candidate.SourcingCaseId,
        SupplierId = candidate.SupplierId,
        Rank = candidate.Rank,
        EvidenceType = candidate.EvidenceType,
        RecommendationReason = candidate.RecommendationReason,
        EvidenceJson = candidate.EvidenceJson,
        EvidenceScore = candidate.EvidenceScore,
        EvidenceFreshOn = candidate.EvidenceFreshOn,
        Selected = candidate.Selected,
        CreatedOn = candidate.CreatedOn,
        UpdatedOn = candidate.UpdatedOn
    };

    private static CandidateSearchSnapshot? ExtractCandidateSearchSnapshot(string payloadJson)
    {
        try
        {
            return JsonSerializer.Deserialize<CandidateSearchSnapshot>(payloadJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static PreparedSupplierRfqSnapshot? ExtractPreparedSupplierRfqSnapshot(string payloadJson)
    {
        try
        {
            return JsonSerializer.Deserialize<PreparedSupplierRfqSnapshot>(payloadJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool ContainsInvariant(string? source, string value)
        => !string.IsNullOrWhiteSpace(source)
            && source.Contains(value, StringComparison.OrdinalIgnoreCase);

    private static void ValidateSearchLimit(int limit)
    {
        if (limit is not (10 or 20 or 50))
            throw new ProcurementValidationException("Supplier candidate limit must be 10, 20, or 50.");
    }

    /// <summary>
    /// A supplier response deadline is a commitment the buyer makes to the supplier, so it is
    /// persisted rather than discarded. Reject anything that could not be honoured: a non-UTC
    /// instant (the column and the dispatch payload are both UTC) or a deadline already past.
    /// </summary>
    private static void ValidateSolicitationDueOn(DateTime? dueOn)
    {
        if (dueOn is null) return;
        if (dueOn.Value.Kind == DateTimeKind.Local)
            throw new ProcurementValidationException("A Supplier RFQ response deadline must be supplied in UTC.");
        if (dueOn.Value <= DateTime.UtcNow)
            throw new ProcurementValidationException("A Supplier RFQ response deadline must be in the future.");
    }

    private static QuoteComparisonLine ToComparisonLine(SupplierQuotedItem row, decimal remainingRequirement,
        Supplier? supplier, SupplierQuotes.SupplierQuoteRevision? canonicalRevision, DateTime? requiredOn)
    {
        var blockers = new List<string>();
        if (canonicalRevision is null || !HasCurrentCanonicalLineage(row, canonicalRevision))
            blockers.Add("canonical evidence missing or unresolved");
        else
        {
            var canonicalLine = canonicalRevision.Lines.Single(x => x.Id == row.SourceSupplierQuoteLineId);
            if (canonicalLine.IsAlternate &&
                !HasAcceptedAlternateAuthorization(canonicalRevision, canonicalLine.Id))
                blockers.Add("alternate approval is unresolved");
        }
        if (supplier is null || SupplierRfqBlockingReasons(supplier).Count > 0)
            blockers.Add("supplier is not award eligible");
        if (row.ProductId is null or <= 0) blockers.Add("product unresolved");
        if (row.UnitPrice is null or <= 0) blockers.Add("unit price missing");
        if (row.CurrencyId is null or <= 0) blockers.Add("currency missing");
        if (row.LeadTimeDays is null or < 0) blockers.Add("lead time missing");
        var quoteCapacity = Math.Min(row.Quantity, row.AvailableQuantity ?? 0m);
        if (row.AvailableQuantity is null || quoteCapacity <= 0) blockers.Add("available quantity insufficient or unknown");
        if (remainingRequirement <= 0) blockers.Add("sourcing requirement already covered");
        if (row.MinimumOrderQuantity is > 0
            && (remainingRequirement < row.MinimumOrderQuantity || quoteCapacity < row.MinimumOrderQuantity))
            blockers.Add("minimum order quantity cannot be satisfied");
        if (row.ValidUntil is null || row.ValidUntil <= DateTime.UtcNow) blockers.Add("quote expired or validity missing");
        if (row.LandedUnitCost is null or <= 0) blockers.Add("landed cost unavailable");
        if (requiredOn.HasValue && row.QuoteDate.HasValue && row.LeadTimeDays.HasValue &&
            row.QuoteDate.Value.AddDays(row.LeadTimeDays.Value) > requiredOn.Value)
            blockers.Add("delivery date missed");
        return new QuoteComparisonLine(row.Id, row.SupplierId, row.Quantity, row.AvailableQuantity, row.UnitPrice ?? 0,
            row.LandedUnitCost, row.CurrencyId ?? 0, row.LeadTimeDays, row.ReliabilitySnapshot, row.ValidUntil, blockers, blockers.Count == 0);
    }

    private static bool HasCanonicalLineage(SupplierQuotedItem row) => row.SourceSupplierQuoteId.HasValue &&
        row.SourceSupplierQuoteRevisionId.HasValue && row.SourceSupplierQuoteLineId.HasValue &&
        row.CommercialDemandLineId.HasValue && row.SourcingCaseId.HasValue;

    private static bool HasCurrentCanonicalAuthorization(SupplierQuotedItem row,
        SupplierQuotes.SupplierQuoteRevision revision)
    {
        if (!HasCurrentCanonicalLineage(row, revision)) return false;

        var line = revision.Lines.Single(x => x.Id == row.SourceSupplierQuoteLineId);
        return !line.IsAlternate || HasAcceptedAlternateAuthorization(revision, line.Id);
    }

    private static bool HasCurrentCanonicalLineage(SupplierQuotedItem row,
        SupplierQuotes.SupplierQuoteRevision revision) =>
        HasCanonicalLineage(row) && revision.Id == row.SourceSupplierQuoteRevisionId &&
            revision.SupplierQuoteId == row.SourceSupplierQuoteId &&
            revision.SupplierQuote.SupplierId == row.SupplierId &&
            revision.SupplierQuote.RfqId == row.RfqId &&
            revision.RevisionNumber == row.QuoteRevision &&
            revision.SupplierQuote.CurrentRevisionNumber == revision.RevisionNumber &&
            revision.SupplierQuote.InboxStatus == SupplierQuotes.SupplierQuoteInboxStatuses.ReadyForComparison &&
            revision.Lines.Any(x => x.Id == row.SourceSupplierQuoteLineId && x.RfqItemId == row.RfqItemId);

    private static bool HasAcceptedAlternateAuthorization(
        SupplierQuotes.SupplierQuoteRevision revision, long lineId)
    {
        var latestDecisions = revision.ReviewDecisions.GroupBy(x => x.SupplierQuoteFieldEvidenceId)
            .ToDictionary(x => x.Key, x => x.OrderByDescending(y => y.ReviewedOn)
                .ThenByDescending(y => y.Id).First());
        return revision.Evidence.Where(x => x.SupplierQuoteLineId == lineId &&
                NormalizeAlternateField(x.FieldName) is "ALTERNATEAUTHORIZATION" or
                    "ALTERNATEAPPROVAL" or "APPROVEDALTERNATE")
            .Any(x => latestDecisions.TryGetValue(x.Id, out var decision) &&
                IsAffirmativeAlternateValue(decision.Status switch
                {
                    SupplierQuotes.SupplierQuoteReviewStatuses.Accepted => x.NormalizedValue,
                    SupplierQuotes.SupplierQuoteReviewStatuses.Corrected => decision.CorrectedValue,
                    _ => null
                }));
    }

    private static string NormalizeAlternateField(string value) => new(value
        .Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static bool IsAffirmativeAlternateValue(string? value) =>
        value?.Trim().ToUpperInvariant() is "TRUE" or "YES" or "APPROVED" or "AUTHORIZED" or "1";

    private static DateTime? LatestProjectionCorrectionOn(
        SupplierQuotes.SupplierQuoteRevision revision)
    {
        var approvalEvidence = revision.Evidence.Where(x =>
                NormalizeAlternateField(x.FieldName) is "ALTERNATEAUTHORIZATION" or
                    "ALTERNATEAPPROVAL" or "APPROVEDALTERNATE")
            .Select(x => x.Id).ToHashSet();
        return revision.ReviewDecisions.GroupBy(x => x.SupplierQuoteFieldEvidenceId)
            .Select(x => x.OrderByDescending(y => y.ReviewedOn).ThenByDescending(y => y.Id).First())
            .Where(x => x.Status == SupplierQuotes.SupplierQuoteReviewStatuses.Corrected &&
                !approvalEvidence.Contains(x.SupplierQuoteFieldEvidenceId))
            .Select(x => (DateTime?)x.ReviewedOn).Max();
    }

    private static void ValidateDeliveryEvidence(string evidenceReference, string? evidenceSha256, DateTime? deliveredOn)
    {
        var controlledPrefixes = new[] { "provider-receipt:", "evidence-object:", "immutable-object:" };
        if (string.IsNullOrWhiteSpace(evidenceReference) || evidenceReference.Length > 500
            || !controlledPrefixes.Any(prefix => evidenceReference.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            || evidenceReference.EndsWith(':') || evidenceReference.Any(char.IsWhiteSpace)
            || evidenceReference.Contains('?') || evidenceReference.Contains('#'))
            throw new ProcurementValidationException(
                "A controlled provider-receipt, evidence-object, or immutable-object reference up to 500 characters is required.");
        if (evidenceSha256 is null || evidenceSha256.Length != 64 || evidenceSha256.Any(character => !Uri.IsHexDigit(character)))
            throw new ProcurementValidationException("A 64-character SHA-256 delivery evidence hash is required.");
        if (deliveredOn is null || deliveredOn.Value.Kind != DateTimeKind.Utc)
            throw new ProcurementValidationException("A UTC deliveredOn timestamp is required for delivery evidence.");
    }

    private async Task<decimal> GetNetSourcingRequirementAsync(long businessUnitId, Rfqitem rfqItem, CancellationToken ct)
    {
        var demand = (decimal)rfqItem.Quantity;
        if (!rfqItem.ProductId.HasValue) return demand;

        var inventory = await _db.Set<Models.Inventory>().AsNoTracking()
            .Where(x => x.Buid == businessUnitId && x.ProductId == rfqItem.ProductId)
            .Select(x => new
            {
                x.Id,
                Available = x.QtyOnHand - x.AllocatedQuantity - x.QuarantineQuantity - x.DamagedQuantity
                    - x.ExpiredQuantity - x.SafetyStockQuantity
            }).ToListAsync(ct);
        var inventoryIds = inventory.Select(x => x.Id).ToArray();
        var reserved = await _db.Set<StockReservation>().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && inventoryIds.Contains(x.InventoryId)
                && x.Status == StockReservationStatus.Active)
            .GroupBy(x => x.InventoryId).Select(x => new { InventoryId = x.Key, Quantity = x.Sum(v => v.Quantity) })
            .ToDictionaryAsync(x => x.InventoryId, x => x.Quantity, ct);
        var available = inventory.Sum(x => Math.Max(0m, x.Available - reserved.GetValueOrDefault(x.Id)));
        var siblingItems = await _db.Rfqitems.AsNoTracking()
            .Where(x => x.Rfqid == rfqItem.Rfqid && x.ProductId == rfqItem.ProductId)
            .OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.Quantity })
            .ToListAsync(ct);
        var siblingIds = siblingItems.Select(x => x.Id).ToArray();
        // COMMITTED SUPPLY: this used to count every status except CANCELLED, which included
        // DRAFT. A draft purchase order is an internal intention, not a supplier commitment —
        // and until CancelPurchaseOrderAsync existed, nothing could ever assign CANCELLED, so a
        // draft whose supplier quotes had lapsed suppressed this line's net requirement forever
        // and CreateSolicitationAsync refused to re-source it as "already fully covered". Only an
        // order actually placed with a supplier reduces what still has to be bought.
        var activePurchaseOrderIds = _db.SupplierPurchaseOrders.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId
                && (x.Status == SupplierPurchaseOrderStatuses.Issued
                    || x.Status == SupplierPurchaseOrderStatuses.PartiallyReceived))
            .Select(x => x.Id);
        var incomingByItem = await _db.SupplierPurchaseOrderLines.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && siblingIds.Contains(x.RfqItemId)
                && activePurchaseOrderIds.Contains(x.SupplierPurchaseOrderId))
            .GroupBy(x => x.RfqItemId)
            .Select(x => new { RfqItemId = x.Key, Quantity = x.Sum(line => line.OrderedQuantity - line.ReceivedQuantity) })
            .ToDictionaryAsync(x => x.RfqItemId, x => Math.Max(0m, x.Quantity), ct);
        var unconvertedAwards = await _db.Set<SourcingAward>().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.RfqItemId != null && siblingIds.Contains(x.RfqItemId.Value)
                && x.Status != "CANCELLED" && x.Status != "REJECTED"
                && !_db.SupplierPurchaseOrderLines.Any(line => line.BusinessUnitId == businessUnitId
                    && line.SourcingAwardId == x.Id))
            .ToArrayAsync(ct);
        var awardedOfferIds = unconvertedAwards.Where(x => x.SupplierQuotedItemId.HasValue)
            .Select(x => x.SupplierQuotedItemId!.Value).Distinct().ToArray();
        var awardedOffers = await _db.SupplierQuotedItems.AsNoTracking().Where(x =>
                x.BusinessUnitId == businessUnitId && awardedOfferIds.Contains(x.Id) && x.IsActive)
            .ToDictionaryAsync(x => x.Id, ct);
        var awardedRevisionIds = awardedOffers.Values.Where(x => x.SourceSupplierQuoteRevisionId.HasValue)
            .Select(x => x.SourceSupplierQuoteRevisionId!.Value).Distinct().ToArray();
        var awardedRevisions = await _db.SupplierQuoteRevisions.AsNoTracking()
            .Include(x => x.SupplierQuote).Include(x => x.Lines)
            .Include(x => x.Evidence).Include(x => x.ReviewDecisions)
            .Where(x => x.BusinessUnitId == businessUnitId && awardedRevisionIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, ct);
        var unconvertedAwardsByItem = unconvertedAwards.Where(award =>
                award.SupplierQuotedItemId.HasValue &&
                awardedOffers.TryGetValue(award.SupplierQuotedItemId.Value, out var offer) &&
                offer.SourceSupplierQuoteRevisionId.HasValue &&
                awardedRevisions.TryGetValue(offer.SourceSupplierQuoteRevisionId.Value, out var revision) &&
                HasCurrentCanonicalAuthorization(offer, revision))
            .GroupBy(x => x.RfqItemId!.Value)
            .ToDictionary(x => x.Key, x => Math.Max(0m, x.Sum(award => award.Quantity ?? 0m)));

        var remainingStock = available;
        foreach (var sibling in siblingItems)
        {
            var demandAfterCommittedSupply = Math.Max(0m, (decimal)sibling.Quantity
                - incomingByItem.GetValueOrDefault(sibling.Id)
                - unconvertedAwardsByItem.GetValueOrDefault(sibling.Id));
            var allocated = Math.Min(remainingStock, demandAfterCommittedSupply);
            if (sibling.Id == rfqItem.Id) return demandAfterCommittedSupply - allocated;
            remainingStock -= allocated;
        }

        return demand;
    }

    private static string ReceiptBusinessHash(PostGoodsReceiptCommand command)
        => Hash(new
        {
            command.BusinessUnitId,
            command.PurchaseOrderId,
            command.WarehouseId,
            ReceiptNumber = command.ReceiptNumber.Trim().ToUpperInvariant(),
            command.ReceivedOn,
            Lines = command.Lines.OrderBy(x => x.PurchaseOrderLineId)
                .Select(x => new { x.PurchaseOrderLineId, x.Quantity }).ToArray()
        });

    private static decimal CalculateLandedUnitCost(CaptureSupplierQuoteLine line)
    {
        var total = line.UnitPrice * line.Quantity + line.FreightCost + line.DutyCost + line.OtherCost + line.TaxAmount - line.DiscountAmount;
        if (total <= 0) throw new ProcurementValidationException("Calculated landed cost must be positive.");
        return decimal.Round(total / line.Quantity, 4, MidpointRounding.AwayFromZero);
    }

    private static decimal CalculateAwardLandedUnitCost(
        SupplierQuotedItem quote, decimal awardedQuantity, bool includeFixedCharges)
    {
        var fixedCharges = includeFixedCharges
            ? quote.FreightCost + quote.DutyCost + quote.OtherCost
                + (quote.TaxAmount ?? 0m) - (quote.DiscountAmount ?? 0m)
            : 0m;
        var total = (quote.UnitPrice ?? 0m) * awardedQuantity + fixedCharges;
        if (total <= 0) throw new ProcurementValidationException("Calculated award landed cost must be positive.");
        return decimal.Round(total / awardedQuantity, 4, MidpointRounding.AwayFromZero);
    }

    private static void ValidateQuoteLine(CaptureSupplierQuoteLine line)
    {
        if (line.RfqItemId <= 0 || line.ProductId is <= 0 || line.Quantity <= 0 || line.UnitPrice <= 0 || line.CurrencyId <= 0)
            throw new ProcurementValidationException("Quote line identifiers, quantity, price, and currency must be positive when supplied.");
        if (line.LeadTimeDays is < 0 || line.AvailableQuantity is < 0 || line.FreightCost < 0 || line.DutyCost < 0
            || line.OtherCost < 0 || line.TaxAmount < 0 || line.DiscountAmount < 0 || line.MinimumOrderQuantity is < 0
            || line.ReliabilitySnapshot is < 0 or > 100)
            throw new ProcurementValidationException("Quote costs, quantities, lead time, and reliability must be within valid ranges.");
    }

    private async Task<Rfq> RequireRfqAsync(long businessUnitId, long rfqId, CancellationToken ct)
        => await _db.Rfqs.SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == rfqId, ct)
            ?? throw new ProcurementValidationException("RFQ was not found in the authenticated tenant.");

    private async Task<Supplier> RequireSupplierAsync(long businessUnitId, long supplierId, CancellationToken ct)
    {
        var supplier = await _db.Suppliers.SingleOrDefaultAsync(x => x.Buid == businessUnitId &&
            x.Id == supplierId && x.IsActive != false, ct)
            ?? throw new ProcurementValidationException("Supplier was not found in the authenticated tenant.");
        var blockers = SupplierRfqBlockingReasons(supplier);
        if (blockers.Count > 0)
            throw new ProcurementValidationException($"Supplier is not eligible: {string.Join("; ", blockers)}.");
        return supplier;
    }

    private void AddEvent(long businessUnitId, string aggregateType, long aggregateId, long version, string eventType,
        string actor, string correlationId, string idempotencyKey, string payload, DateTime now)
        => _db.ProcurementEvents.Add(new ProcurementEvent
        {
            BusinessUnitId = businessUnitId, AggregateType = aggregateType, AggregateId = aggregateId,
            AggregateVersion = version, EventType = eventType, Actor = actor.Trim(), CorrelationId = correlationId.Trim(),
            IdempotencyKey = idempotencyKey.Trim(), PayloadJson = payload, OccurredOn = now
        });

    private static string Hash(object value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)))).ToLowerInvariant();

    private static void EnsureReplay(string? existingHash, string requestedHash)
    {
        if (!string.Equals(existingHash, requestedHash, StringComparison.Ordinal))
            throw new ProcurementConflictException("The idempotency key was already used for a different request.");
    }

    private static bool IsConcurrentWriteFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is DbUpdateConcurrencyException)
                return true;
            if (current is PostgresException postgres && postgres.SqlState is
                PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected)
                return true;
        }

        return false;
    }

    private static void ValidateCommand(long businessUnitId, string idempotencyKey, string actor, string correlationId)
    {
        ValidateTenant(businessUnitId);
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Trim().Length > 130)
            throw new ProcurementValidationException("An idempotency key up to 130 characters is required.");
        if (string.IsNullOrWhiteSpace(actor) || actor.Trim().Length > 255)
            throw new ProcurementValidationException("An actor up to 255 characters is required.");
        if (string.IsNullOrWhiteSpace(correlationId) || correlationId.Trim().Length > 160)
            throw new ProcurementValidationException("A correlation ID up to 160 characters is required.");
    }

    private static void ValidateTenant(long businessUnitId)
    {
        if (businessUnitId <= 0) throw new ProcurementValidationException("A valid authenticated business unit is required.");
    }
}

internal sealed record SolicitationDispatchPayload(
    long SolicitationId,
    long BusinessUnitId,
    long RfqId,
    string ToEmail,
    string SupplierName,
    string RfqNumber,
    string ItemSummary,
    DateTime? DueOn);

internal sealed record CandidateEvidence(
    long SupplierId,
    string EvidenceType,
    string Reason,
    decimal Score,
    DateTime? FreshOn,
    string EvidenceJson);
