using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Procurement;

public sealed class ProcurementHandoffService(ErpRfqAutomationContext db) : IProcurementHandoffService
{
    private static readonly HashSet<string> SupportedStatuses =
    [
        ProcurementHandoffStatuses.ExternalPoCreated,
        ProcurementHandoffStatuses.SupplierConfirmed,
        ProcurementHandoffStatuses.Dispatched,
        ProcurementHandoffStatuses.Delivered,
        ProcurementHandoffStatuses.PartiallyReceived,
        ProcurementHandoffStatuses.Received,
        ProcurementHandoffStatuses.Cancelled
    ];
    private static readonly IReadOnlyDictionary<string, HashSet<string>> AllowedTransitions =
        new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            [ProcurementHandoffStatuses.Created] =
                [ProcurementHandoffStatuses.ExternalPoCreated, ProcurementHandoffStatuses.Cancelled],
            [ProcurementHandoffStatuses.ExternalPoCreated] =
                [ProcurementHandoffStatuses.ExternalPoCreated, ProcurementHandoffStatuses.SupplierConfirmed,
                    ProcurementHandoffStatuses.Cancelled],
            [ProcurementHandoffStatuses.SupplierConfirmed] =
                [ProcurementHandoffStatuses.SupplierConfirmed, ProcurementHandoffStatuses.Dispatched,
                    ProcurementHandoffStatuses.PartiallyReceived,
                    ProcurementHandoffStatuses.Received, ProcurementHandoffStatuses.Cancelled],
            [ProcurementHandoffStatuses.Dispatched] =
                [ProcurementHandoffStatuses.Dispatched, ProcurementHandoffStatuses.Delivered,
                    ProcurementHandoffStatuses.PartiallyReceived, ProcurementHandoffStatuses.Received,
                    ProcurementHandoffStatuses.Cancelled],
            [ProcurementHandoffStatuses.Delivered] =
                [ProcurementHandoffStatuses.Delivered, ProcurementHandoffStatuses.PartiallyReceived,
                    ProcurementHandoffStatuses.Received, ProcurementHandoffStatuses.Cancelled],
            [ProcurementHandoffStatuses.PartiallyReceived] =
                [ProcurementHandoffStatuses.PartiallyReceived, ProcurementHandoffStatuses.Received,
                    ProcurementHandoffStatuses.Cancelled],
            [ProcurementHandoffStatuses.Received] = [ProcurementHandoffStatuses.Received],
            [ProcurementHandoffStatuses.Cancelled] = [ProcurementHandoffStatuses.Cancelled]
        };

    public async Task<IReadOnlyCollection<ProcurementHandoffView>> SearchAsync(long businessUnitId,
        long? customerOrderId, string? search, int limit, CancellationToken cancellationToken = default)
    {
        EnsureTenant(businessUnitId);
        if (limit is < 1 or > 200) throw new ArgumentException("Limit must be between 1 and 200.");
        var term = search?.Trim().ToUpperInvariant();
        var query = from handoff in db.ProcurementHandoffs.AsNoTracking()
            join order in db.Orders.AsNoTracking() on handoff.CustomerOrderId equals order.Id
            join supplier in db.Suppliers.AsNoTracking() on handoff.SupplierId equals supplier.Id
            join currency in db.Currencies.AsNoTracking() on handoff.CurrencyId equals currency.Id
            where handoff.BusinessUnitId == businessUnitId && order.BusinessUnitId == businessUnitId
                && supplier.Buid == businessUnitId && currency.BusinessUnitId == businessUnitId
                && (!customerOrderId.HasValue || handoff.CustomerOrderId == customerOrderId.Value)
                && (string.IsNullOrWhiteSpace(term) || handoff.NexoraSerial.ToUpper().Contains(term)
                    || order.OrderNo.ToUpper().Contains(term)
                    || supplier.Name.ToUpper().Contains(term)
                    || (handoff.ExternalSupplierPoNumber != null
                        && handoff.ExternalSupplierPoNumber.ToUpper().Contains(term)))
            orderby handoff.CreatedOn descending, handoff.Id descending
            select Map(handoff, order.OrderNo, supplier.Name, currency.Code);
        return await query.Take(limit).ToListAsync(cancellationToken);
    }

    public async Task<ProcurementHandoffView> GetAsync(long businessUnitId, long id,
        CancellationToken cancellationToken = default)
    {
        var rows = await SearchByIdAsync(businessUnitId, id, cancellationToken);
        return rows ?? throw new KeyNotFoundException("Procurement handoff was not found in this tenant.");
    }

    public async Task<IReadOnlyCollection<ProcurementHandoffCandidateView>> CandidatesAsync(long businessUnitId,
        CancellationToken cancellationToken = default)
    {
        EnsureTenant(businessUnitId);
        return await (from orderLine in db.OrderItems.AsNoTracking()
            join order in db.Orders.AsNoTracking() on orderLine.OrderId equals order.Id
            join allocation in db.CustomerAwardLineAllocations.AsNoTracking()
                on orderLine.CustomerAwardLineAllocationId equals (long?)allocation.Id
            join decision in db.CustomerQuoteSourcingDecisions.AsNoTracking()
                on allocation.QuoteItemId equals decision.QuoteItemId
            join award in db.Set<ERP_RFQ_Automation.Agent.Models.SourcingAward>().AsNoTracking()
                on decision.SourcingAwardId equals award.Id
            join quotedItem in db.SupplierQuotedItems.AsNoTracking()
                on decision.SupplierQuotedItemId equals quotedItem.Id
            join supplier in db.Suppliers.AsNoTracking() on quotedItem.SupplierId equals supplier.Id
            join currency in db.Currencies.AsNoTracking() on decision.CurrencyId equals currency.Id
            where order.BusinessUnitId == businessUnitId && allocation.BusinessUnitId == businessUnitId
                && decision.BusinessUnitId == businessUnitId && award.BusinessUnitId == businessUnitId
                && quotedItem.BusinessUnitId == businessUnitId && supplier.Buid == businessUnitId
                && currency.BusinessUnitId == businessUnitId && order.SourceType == OrderSourceTypes.CustomerAward
                && (award.Status == "APPROVED" || award.Status == "SPLIT_APPROVED")
                && quotedItem.IsActive && decision.Quantity == orderLine.Quantity
                && award.RfqId == decision.RfqId && award.RfqItemId == decision.RfqItemId
                && award.SupplierQuotedItemId == decision.SupplierQuotedItemId
                && quotedItem.RfqId == decision.RfqId && quotedItem.RfqItemId == decision.RfqItemId
                && quotedItem.CommercialDemandLineId == decision.CommercialDemandLineId
                && quotedItem.CurrencyId == decision.CurrencyId
                && !db.ProcurementHandoffs.Any(x => x.BusinessUnitId == businessUnitId
                    && x.CustomerOrderLineId == orderLine.Id)
            orderby order.OrderDate, orderLine.Id
            select new ProcurementHandoffCandidateView(order.Id, order.OrderNo, orderLine.Id,
                decision.CommercialDemandLineId, decision.SourcingAwardId, decision.SupplierQuotedItemId,
                supplier.Id, supplier.Name, decision.NexoraSerial, orderLine.Quantity,
                decision.SupplierLandedUnitCost, decision.CurrencyId, currency.Code))
            .Take(100).ToListAsync(cancellationToken);
    }

    public async Task<ProcurementHandoffView> CreateAsync(long businessUnitId, string idempotencyKey,
        string correlationId, string actor, CreateProcurementHandoffCommand command,
        CancellationToken cancellationToken = default)
    {
        EnsureCommand(businessUnitId, idempotencyKey, correlationId, actor);
        var destination = command.DestinationType?.Trim().ToUpperInvariant();
        if (command.DeliveryLocation?.Trim().Length > 500)
            throw new ArgumentException("Delivery location cannot exceed 500 characters.");
        if (destination is not ("WAREHOUSE" or "DROP_SHIP"))
            throw new ArgumentException("Destination type must be WAREHOUSE or DROP_SHIP.");
        if (destination == "WAREHOUSE" && !command.WarehouseId.HasValue)
            throw new ArgumentException("A warehouse is required for a warehouse destination.");
        var hash = Hash(command);
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable,
                cancellationToken);
            var replay = await db.ProcurementHandoffs.AsNoTracking().SingleOrDefaultAsync(x =>
                x.BusinessUnitId == businessUnitId && x.IdempotencyKey == idempotencyKey.Trim(), cancellationToken);
            if (replay is not null)
            {
                EnsureHash(replay.RequestHash, hash);
                await transaction.CommitAsync(cancellationToken);
                return await GetAsync(businessUnitId, replay.Id, cancellationToken);
            }

            var orderLine = await db.OrderItems.AsNoTracking().Include(x => x.Order)
                .Include(x => x.CustomerAwardLineAllocation)
                .SingleOrDefaultAsync(x => x.Id == command.CustomerOrderLineId
                    && x.Order.BusinessUnitId == businessUnitId, cancellationToken)
                ?? throw new KeyNotFoundException("Customer Order line was not found in this tenant.");
            if (orderLine.Order.SourceType != OrderSourceTypes.CustomerAward
                || orderLine.CustomerAwardLineAllocation is null)
                throw new ArgumentException("Only a governed Client PO Customer Order line can create a handoff.");
            var allocation = orderLine.CustomerAwardLineAllocation;
            var decision = await db.CustomerQuoteSourcingDecisions.AsNoTracking().SingleOrDefaultAsync(x =>
                x.BusinessUnitId == businessUnitId && x.QuoteItemId == allocation.QuoteItemId,
                cancellationToken) ?? throw new ArgumentException(
                "This Customer Order line has no approved sourced quantity to hand off.");
            var award = await db.Set<ERP_RFQ_Automation.Agent.Models.SourcingAward>().AsNoTracking()
                .SingleAsync(x => x.BusinessUnitId == businessUnitId && x.Id == decision.SourcingAwardId,
                    cancellationToken);
            var quotedItem = await db.SupplierQuotedItems.AsNoTracking().SingleOrDefaultAsync(x =>
                x.BusinessUnitId == businessUnitId && x.Id == decision.SupplierQuotedItemId,
                cancellationToken) ?? throw new ArgumentException("The selected Supplier quote is unavailable.");
            if (award.Status is not ("APPROVED" or "SPLIT_APPROVED")
                || award.RfqId != decision.RfqId || award.RfqItemId != decision.RfqItemId
                || award.SupplierId != quotedItem.SupplierId
                || award.SupplierQuotedItemId != decision.SupplierQuotedItemId
                || !quotedItem.IsActive || quotedItem.RfqId != decision.RfqId
                || quotedItem.RfqItemId != decision.RfqItemId
                || quotedItem.CommercialDemandLineId != decision.CommercialDemandLineId
                || quotedItem.CurrencyId != decision.CurrencyId)
                throw new ArgumentException("The approved sourcing lineage is no longer commercially eligible.");
            if (decision.Quantity != orderLine.Quantity)
                throw new ArgumentException(
                    "The sourced quantity must fully cover the Customer Order line before procurement handoff.");
            if (command.WarehouseId.HasValue && !await db.Warehouses.AsNoTracking().AnyAsync(x =>
                x.BusinessUnitId == businessUnitId && x.Id == command.WarehouseId.Value, cancellationToken))
                throw new ArgumentException("Warehouse was not found in this tenant.");
            if (await db.ProcurementHandoffs.AnyAsync(x => x.BusinessUnitId == businessUnitId
                && x.CustomerOrderLineId == orderLine.Id, cancellationToken))
                throw new InvalidOperationException("This Customer Order line already has a procurement handoff.");

            var now = DateTime.UtcNow;
            var handoff = new ProcurementHandoff
            {
                BusinessUnitId = businessUnitId,
                CustomerOrderId = orderLine.OrderId,
                CustomerOrderLineId = orderLine.Id,
                CommercialDemandLineId = decision.CommercialDemandLineId,
                SourcingAwardId = decision.SourcingAwardId,
                SupplierQuotedItemId = decision.SupplierQuotedItemId,
                SupplierId = award.SupplierId,
                RfqId = decision.RfqId,
                RfqItemId = decision.RfqItemId,
                CurrencyId = decision.CurrencyId,
                NexoraSerial = decision.NexoraSerial,
                RequiredQuantity = orderLine.Quantity,
                SelectedUnitCost = decision.SupplierLandedUnitCost,
                RequiredOn = command.RequiredOn,
                DestinationType = destination,
                WarehouseId = command.WarehouseId,
                DeliveryLocation = command.DeliveryLocation?.Trim(),
                ExternalSystemTarget = "MANUAL",
                IdempotencyKey = idempotencyKey.Trim(),
                RequestHash = hash,
                CreatedOn = now,
                CreatedBy = actor.Trim()
            };
            db.ProcurementHandoffs.Add(handoff);
            await db.SaveChangesAsync(cancellationToken);
            AddEvent(handoff, "PROCUREMENT_HANDOFF_CREATED", actor, correlationId, idempotencyKey,
                new { handoff.CustomerOrderLineId, handoff.CommercialDemandLineId, handoff.SourcingAwardId,
                    handoff.SupplierQuotedItemId, handoff.RequiredQuantity, handoff.ExternalSystemTarget }, now);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return await GetAsync(businessUnitId, handoff.Id, cancellationToken);
        });
    }

    public async Task<ProcurementHandoffView> SynchronizeAsync(long businessUnitId, long id,
        string idempotencyKey, string correlationId, string actor,
        SynchronizeProcurementHandoffCommand command, CancellationToken cancellationToken = default)
    {
        EnsureCommand(businessUnitId, idempotencyKey, correlationId, actor);
        if (string.IsNullOrWhiteSpace(command.ExternalSupplierPoNumber)
            || string.IsNullOrWhiteSpace(command.ExternalSupplierPoLineNumber))
            throw new ArgumentException("External Supplier PO and line references are required.");
        if (command.ExternalSupplierPoNumber.Trim().Length > 160
            || command.ExternalSupplierPoLineNumber.Trim().Length > 80)
            throw new ArgumentException("External Supplier PO references exceed their allowed length.");
        if (command.OrderedQuantity <= 0 || command.ApprovedUnitCost < 0)
            throw new ArgumentException("External quantity and cost values are invalid.");
        var status = command.Status?.Trim().ToUpperInvariant();
        if (status is null || !SupportedStatuses.Contains(status))
            throw new ArgumentException("External status is not supported.");
        if (command.SynchronizedOn.Kind != DateTimeKind.Utc || command.SynchronizedOn > DateTime.UtcNow.AddMinutes(5))
            throw new ArgumentException("Synchronization time must be a non-future UTC value.");
        var hash = Hash(command);
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable,
                cancellationToken);
            var replayEvent = await db.ProcurementEvents.AsNoTracking().SingleOrDefaultAsync(x =>
                x.BusinessUnitId == businessUnitId && x.EventType == "PROCUREMENT_HANDOFF_SYNCHRONIZED"
                && x.IdempotencyKey == idempotencyKey.Trim(), cancellationToken);
            if (replayEvent is not null)
            {
                if (replayEvent.AggregateId != id)
                    throw new InvalidOperationException(
                        "The idempotency key was already used for a different procurement handoff.");
                using var payload = JsonDocument.Parse(replayEvent.PayloadJson);
                EnsureHash(payload.RootElement.GetProperty("requestHash").GetString(), hash);
                await transaction.CommitAsync(cancellationToken);
                return await GetAsync(businessUnitId, replayEvent.AggregateId, cancellationToken);
            }
            var handoff = await db.ProcurementHandoffs.SingleOrDefaultAsync(x =>
                x.BusinessUnitId == businessUnitId && x.Id == id, cancellationToken)
                ?? throw new KeyNotFoundException("Procurement handoff was not found in this tenant.");
            if (handoff.Version != command.ExpectedVersion)
                throw new InvalidOperationException("The procurement handoff changed; refresh before updating it.");
            if (handoff.IsAuthoritative)
                throw new InvalidOperationException(
                    "Provider-authoritative procurement state cannot be overwritten by a manual synchronization.");
            var observedStatus = handoff.ExternalStatus ?? handoff.Status;
            if (!AllowedTransitions.TryGetValue(observedStatus, out var transitions) || !transitions.Contains(status))
                throw new InvalidOperationException(
                    $"An external handoff observation cannot move from {observedStatus} to {status}.");
            if (command.OrderedQuantity != handoff.RequiredQuantity
                || command.ApprovedUnitCost != handoff.SelectedUnitCost)
                throw new InvalidOperationException(
                    "External quantity or cost differs from the governed handoff; resolve the variance before synchronization.");
            handoff.ExternalSupplierPoNumber = command.ExternalSupplierPoNumber.Trim();
            handoff.ExternalSupplierPoLineNumber = command.ExternalSupplierPoLineNumber.Trim();
            handoff.ExternalOrderedQuantity = command.OrderedQuantity;
            handoff.ExternalApprovedUnitCost = command.ApprovedUnitCost;
            handoff.ExternalExpectedOn = command.ExpectedOn;
            handoff.ExternalStatus = status;
            handoff.Status = status;
            handoff.LastSynchronizedOn = command.SynchronizedOn;
            handoff.SourceOfTruth = "Authorized manual entry";
            handoff.IsAuthoritative = false;
            handoff.Version++;
            handoff.ModifiedOn = DateTime.UtcNow;
            handoff.ModifiedBy = actor.Trim();
            AddEvent(handoff, "PROCUREMENT_HANDOFF_SYNCHRONIZED", actor, correlationId, idempotencyKey,
                new { requestHash = hash, handoff.ExternalSupplierPoNumber,
                    handoff.ExternalSupplierPoLineNumber, handoff.ExternalStatus,
                    handoff.LastSynchronizedOn, handoff.SourceOfTruth, handoff.IsAuthoritative },
                handoff.ModifiedOn.Value);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return await GetAsync(businessUnitId, handoff.Id, cancellationToken);
        });
    }

    private async Task<ProcurementHandoffView?> SearchByIdAsync(long businessUnitId, long id,
        CancellationToken cancellationToken)
    {
        EnsureTenant(businessUnitId);
        return await (from handoff in db.ProcurementHandoffs.AsNoTracking()
            join order in db.Orders.AsNoTracking() on handoff.CustomerOrderId equals order.Id
            join supplier in db.Suppliers.AsNoTracking() on handoff.SupplierId equals supplier.Id
            join currency in db.Currencies.AsNoTracking() on handoff.CurrencyId equals currency.Id
            where handoff.BusinessUnitId == businessUnitId && handoff.Id == id
                && order.BusinessUnitId == businessUnitId && supplier.Buid == businessUnitId
                && currency.BusinessUnitId == businessUnitId
            select Map(handoff, order.OrderNo, supplier.Name, currency.Code))
            .SingleOrDefaultAsync(cancellationToken);
    }

    private static ProcurementHandoffView Map(ProcurementHandoff value, string orderNumber,
        string supplierName, string currencyCode) => new(value.Id, value.CustomerOrderId, orderNumber,
        value.CustomerOrderLineId, value.CommercialDemandLineId, value.SourcingAwardId,
        value.SupplierQuotedItemId, value.SupplierId, supplierName, value.NexoraSerial,
        value.RequiredQuantity, value.SelectedUnitCost, value.CurrencyId, currencyCode,
        value.RequiredOn, value.DestinationType, value.WarehouseId, value.DeliveryLocation,
        value.ExternalSystemTarget, value.Status, value.ExternalSupplierPoNumber,
        value.ExternalSupplierPoLineNumber, value.ExternalOrderedQuantity,
        value.ExternalSalesOrderNumber, value.ExternalApprovedUnitCost, value.ExternalExpectedOn, value.ExternalStatus,
        value.SupplierConfirmedOn, value.DispatchedOn, value.DeliveredOn,
        value.LastExternalEventId, value.LastCorrelationId,
        value.LastSynchronizedOn, value.SourceOfTruth, value.IsAuthoritative, value.Version);

    private void AddEvent(ProcurementHandoff handoff, string eventType, string actor,
        string correlationId, string idempotencyKey, object payload, DateTime occurredOn)
        => db.ProcurementEvents.Add(new ProcurementEvent
        {
            BusinessUnitId = handoff.BusinessUnitId,
            AggregateType = "ProcurementHandoff",
            AggregateId = handoff.Id,
            AggregateVersion = handoff.Version,
            EventType = eventType,
            Actor = actor.Trim(),
            CorrelationId = correlationId.Trim(),
            IdempotencyKey = idempotencyKey.Trim(),
            PayloadJson = JsonSerializer.Serialize(payload),
            OccurredOn = occurredOn
        });

    private void EnsureTenant(long businessUnitId)
    {
        if (businessUnitId <= 0 || db.ScopedTenantId != businessUnitId)
            throw new ArgumentException("The authenticated tenant context is required.");
    }

    private static void EnsureCommand(long businessUnitId, string key, string correlationId, string actor)
    {
        if (businessUnitId <= 0 || string.IsNullOrWhiteSpace(key) || key.Trim().Length > 160
            || string.IsNullOrWhiteSpace(correlationId) || correlationId.Trim().Length > 160
            || string.IsNullOrWhiteSpace(actor) || actor.Trim().Length > 255)
            throw new ArgumentException("Tenant, idempotency, correlation, and actor values are required.");
    }

    private static string Hash(object value) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)))).ToLowerInvariant();

    private static void EnsureHash(string? persisted, string current)
    {
        if (!string.Equals(persisted, current, StringComparison.Ordinal))
            throw new InvalidOperationException("The idempotency key was already used for a different request.");
    }
}
