using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ERP_RFQ_Automation.Inventory.Commercial;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Procurement;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.InboundLogistics;

/// <summary>
/// FR-MAS-01..05. The write paths for inbound supplier shipments.
///
/// <para>Every mutation is serializable, idempotent against the shared <c>procurement_events</c>
/// ledger, and optimistically concurrent on the aggregate's own <c>Version</c>. That is not a
/// stylistic choice: these rows are read by an SLA sweep that emails buyers and by an availability
/// calculation that customer promises are built on, so a retried request that recorded a second
/// departure or a second cancellation would produce a real, external, wrong consequence.</para>
///
/// <para><b>No new event table.</b> Milestones append to <c>ProcurementEvent</c> with
/// <c>AggregateType = "SupplierShipment"</c>, using the same idempotency-key/correlation-id/actor
/// discipline as <c>ProcurementApplicationService</c>. The existing
/// <c>(BusinessUnitId, AggregateType, AggregateId, OccurredOn)</c> index is exactly the milestone
/// timeline query, and the existing unique
/// <c>(BusinessUnitId, EventType, IdempotencyKey)</c> index is exactly the replay guard.</para>
/// </summary>
public sealed class InboundShipmentApplicationService(ErpRfqAutomationContext db)
    : IInboundShipmentApplicationService
{
    public const string AggregateType = "SupplierShipment";

    /// <summary>
    /// Reference-data and policy events carry their OWN aggregate type.
    ///
    /// <para>They used to be written as <c>SupplierShipment</c>, which is wrong in a way that only
    /// shows up once ids collide: the shipment timeline reader selects on
    /// <c>(AggregateType, AggregateId)</c>, and a port row whose id happened to match a shipment id
    /// would surface inside that shipment's milestone history as if it had happened to the
    /// shipment.</para>
    /// </summary>
    public const string PortAggregateType = "PortOfEntry";

    public const string PolicyAggregateType = "InboundLogisticsPolicy";
    public const string ShipmentCreatedEvent = "INBOUND_SHIPMENT_CREATED";
    public const string MilestoneRecordedEvent = "INBOUND_SHIPMENT_MILESTONE_RECORDED";
    public const string TrackingUpdatedEvent = "INBOUND_SHIPMENT_TRACKING_UPDATED";
    public const string PortCreatedEvent = "PORT_OF_ENTRY_CREATED";
    public const string PortActivationEvent = "PORT_OF_ENTRY_ACTIVATION_CHANGED";
    public const string PortCatalogueImportedEvent = "SAUDI_PORT_CATALOGUE_IMPORTED";
    public const string PolicyUpdatedEvent = "INBOUND_LOGISTICS_POLICY_UPDATED";

    /// <summary>
    /// Purchase order statuses a shipment may be raised against: the order is with the supplier and
    /// not yet fully settled.
    ///
    /// <para>DRAFT and APPROVED are excluded because the supplier has not seen the order — a
    /// shipment against an order nobody has placed is a fiction. RECEIVED, CLOSED and CANCELLED are
    /// excluded because there is nothing left inbound.</para>
    /// </summary>
    public static readonly IReadOnlySet<string> ShippableOrderStatuses = new HashSet<string>(StringComparer.Ordinal)
    {
        SupplierPurchaseOrderStatuses.Sent,
        SupplierPurchaseOrderStatuses.Issued,
        SupplierPurchaseOrderStatuses.Acknowledged,
        SupplierPurchaseOrderStatuses.InProduction,
        SupplierPurchaseOrderStatuses.Shipped,
        SupplierPurchaseOrderStatuses.PartiallyReceived
    };

    // ==========================================================================================
    // Shipments
    // ==========================================================================================

    public async Task<InboundShipmentView> CreateShipmentAsync(
        CreateInboundShipmentCommand command, CancellationToken ct = default)
    {
        ValidateCommand(command.BusinessUnitId, command.IdempotencyKey, command.Actor, command.CorrelationId);
        if (command.PurchaseOrderId <= 0)
            throw new InboundLogisticsValidationException("A purchase order is required.");
        if (command.Lines.Count == 0)
            throw new InboundLogisticsValidationException(
                "A shipment must carry at least one purchase order line; a shipment with no quantity "
                + "reconciles against nothing.");
        if (command.Lines.Select(x => x.PurchaseOrderLineId).Distinct().Count() != command.Lines.Count)
            throw new InboundLogisticsValidationException(
                "Each purchase order line may appear only once on a shipment.");
        if (command.Lines.Any(x => x.Quantity <= 0))
            throw new InboundLogisticsValidationException("Every shipped quantity must be greater than zero.");

        var carrier = Trimmed(command.CarrierName, 255, "The carrier name");
        var tracking = Trimmed(command.TrackingReference, 160, "The tracking reference");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var readyOn = command.ReadyAtFactoryOn ?? today;
        if (readyOn > today)
            throw new InboundLogisticsValidationException("A ready-at-factory date cannot be in the future.");
        if (command.EtaDate is { } eta && eta < readyOn)
            throw new InboundLogisticsValidationException("The ETA cannot be before the goods were ready to ship.");

        var hash = Hash(new
        {
            command.PurchaseOrderId, carrier, tracking, command.EtaDate, command.PortOfEntryId, readyOn,
            lines = command.Lines.OrderBy(x => x.PurchaseOrderLineId)
                .Select(x => new { x.PurchaseOrderLineId, x.Quantity }).ToArray()
        });

        var shipmentId = await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            if (await ReplayedAggregateIdAsync(command.BusinessUnitId, ShipmentCreatedEvent,
                    command.IdempotencyKey, hash, ct) is { } replayed)
            {
                await tx.CommitAsync(ct);
                return replayed;
            }

            var order = await db.SupplierPurchaseOrders.Include(x => x.Lines).SingleOrDefaultAsync(
                             x => x.BusinessUnitId == command.BusinessUnitId && x.Id == command.PurchaseOrderId, ct)
                         ?? throw new InboundLogisticsValidationException("Purchase order was not found.");
            if (!ShippableOrderStatuses.Contains(order.Status))
                throw new InboundLogisticsConflictException(
                    "A shipment can only be recorded against a purchase order that is with the supplier "
                    + "and not yet fully received.");

            var port = await ResolveActivePortAsync(command.BusinessUnitId, command.PortOfEntryId, ct);
            var now = DateTime.UtcNow;

            // The sequence is taken inside the serializable transaction and backed by the unique
            // (BusinessUnitId, ShipmentNumber) index, so two concurrent creates cannot mint the same
            // number: the loser fails the insert rather than silently sharing an identity.
            var sequence = await db.Set<SupplierShipment>().CountAsync(
                x => x.BusinessUnitId == command.BusinessUnitId
                     && x.SupplierPurchaseOrderId == order.Id, ct) + 1;

            var shipment = new SupplierShipment
            {
                BusinessUnitId = command.BusinessUnitId,
                SupplierPurchaseOrderId = order.Id,
                ShipmentNumber = $"{order.PurchaseOrderNumber}-S{sequence}",
                Milestone = InboundShipmentMilestones.ReadyAtFactory,
                MilestoneOccurredOn = readyOn,
                ReadyAtFactoryOn = readyOn,
                PortOfEntryId = port?.Id,
                CarrierName = carrier,
                TrackingReference = tracking,
                TrackingSource = InboundShipmentTrackingSources.Manual,
                EtaDate = command.EtaDate,
                EtaUpdatedOn = command.EtaDate is null ? null : now,
                EtaUpdatedBy = command.EtaDate is null ? null : command.Actor.Trim(),
                IdempotencyKey = command.IdempotencyKey.Trim(),
                RequestHash = hash,
                CreatedOn = now,
                CreatedBy = command.Actor.Trim()
            };
            db.Set<SupplierShipment>().Add(shipment);
            await db.SaveChangesAsync(ct);

            foreach (var requested in command.Lines)
            {
                var orderLine = order.Lines.SingleOrDefault(x => x.Id == requested.PurchaseOrderLineId)
                                ?? throw new InboundLogisticsValidationException(
                                    $"Purchase order line {requested.PurchaseOrderLineId} does not belong to this order.");

                // FR-MAS-05 reconciliation, enforced HERE and not only in a validator. The running
                // total on the purchase order line is the thing the database CHECK constraint
                // guards, so a second write path added later cannot quietly over-ship a line.
                var newShipped = orderLine.ShippedQuantity + requested.Quantity;
                if (newShipped > orderLine.OrderedQuantity)
                    throw new InboundLogisticsValidationException(
                        $"Shipping {requested.Quantity} would take purchase order line {orderLine.Id} to "
                        + $"{newShipped} against {orderLine.OrderedQuantity} ordered. A line cannot be "
                        + "shipped for more than it was ordered for.");

                orderLine.ShippedQuantity = newShipped;
                orderLine.Version++;
                shipment.Lines.Add(new SupplierShipmentLine
                {
                    BusinessUnitId = command.BusinessUnitId,
                    SupplierShipmentId = shipment.Id,
                    SupplierPurchaseOrderLineId = orderLine.Id,
                    ProductId = orderLine.ProductId,
                    ShippedQuantity = requested.Quantity
                });
            }

            var policy = await ResolvePolicyAsync(command.BusinessUnitId, ct);
            MaterialAvailabilityCalculator.Apply(shipment,
                MaterialAvailabilityCalculator.Derive(shipment, policy), now);

            AddEvent(command, ShipmentCreatedEvent, shipment.Id, shipment.Version, now, new
            {
                requestHash = hash,
                shipmentNumber = shipment.ShipmentNumber,
                purchaseOrderId = order.Id,
                milestone = shipment.Milestone,
                occurredOn = readyOn,
                carrier,
                tracking,
                etaDate = shipment.EtaDate,
                portOfEntryId = shipment.PortOfEntryId,
                materialAvailableDate = shipment.MaterialAvailableDate,
                lines = command.Lines.OrderBy(x => x.PurchaseOrderLineId)
                    .Select(x => new { x.PurchaseOrderLineId, x.Quantity }).ToArray()
            });

            await db.SaveChangesAsync(ct);
            await ProjectIncomingInventoryAsync(command.BusinessUnitId, order.Id, ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return shipment.Id;
        });

        return await RequireShipmentViewAsync(command.BusinessUnitId, shipmentId, ct);
    }

    public async Task<InboundShipmentView> RecordMilestoneAsync(
        RecordInboundShipmentMilestoneCommand command, CancellationToken ct = default)
    {
        ValidateCommand(command.BusinessUnitId, command.IdempotencyKey, command.Actor, command.CorrelationId);
        var milestone = (command.Milestone ?? string.Empty).Trim().ToUpperInvariant();
        if (!InboundShipmentMilestones.All.Contains(milestone))
            throw new InboundLogisticsValidationException(
                "Unknown milestone. Use one of: " + string.Join(", ", InboundShipmentMilestones.All) + ".");

        var note = Trimmed(command.Note, 1000, "The note");
        if (milestone == InboundShipmentMilestones.Cancelled && note is null)
            throw new InboundLogisticsValidationException(
                "Cancelling a shipment requires a reason: the quantity it holds is released back to the "
                + "purchase order line, and nothing else records why.");

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (command.OccurredOn > today)
            throw new InboundLogisticsValidationException(
                "A milestone cannot be recorded with a future date. Record it when it happens.");

        var hash = Hash(new
        {
            command.ShipmentId, command.ExpectedVersion, milestone, command.OccurredOn,
            command.PortOfEntryId, command.EtaDate, note
        });

        var shipmentId = await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            if (await ReplayedAggregateIdAsync(command.BusinessUnitId, MilestoneRecordedEvent,
                    command.IdempotencyKey, hash, ct) is { } replayed)
            {
                await tx.CommitAsync(ct);
                return replayed;
            }

            var shipment = await db.Set<SupplierShipment>().Include(x => x.Lines).SingleOrDefaultAsync(
                               x => x.BusinessUnitId == command.BusinessUnitId && x.Id == command.ShipmentId, ct)
                           ?? throw new InboundLogisticsValidationException("Shipment was not found.");
            if (shipment.Version != command.ExpectedVersion)
                throw new InboundLogisticsConflictException(
                    "The shipment changed; refresh before recording a milestone.");
            if (shipment.IsTerminal)
                throw new InboundLogisticsConflictException(
                    $"This shipment is already {shipment.Milestone.Replace('_', ' ').ToLowerInvariant()} "
                    + "and cannot move again.");
            if (!InboundShipmentMilestones.CanTransition(shipment.Milestone, milestone))
                throw new InboundLogisticsConflictException(
                    $"A shipment cannot move from {shipment.Milestone} to {milestone}. Inbound milestones "
                    + "only move forward.");

            var latest = shipment.LatestRecordedMilestoneDate();
            if (latest is { } previous && command.OccurredOn < previous)
                throw new InboundLogisticsValidationException(
                    $"This milestone is dated {command.OccurredOn:yyyy-MM-dd}, before the last recorded "
                    + $"milestone on {previous:yyyy-MM-dd}. A shipment cannot clear customs before it arrived.");

            var now = DateTime.UtcNow;
            var port = await ResolveActivePortAsync(command.BusinessUnitId, command.PortOfEntryId, ct);
            if (port is not null) shipment.PortOfEntryId = port.Id;
            if (milestone == InboundShipmentMilestones.ArrivedSaudiPort && shipment.PortOfEntryId is null)
                throw new InboundLogisticsValidationException(
                    "Name the Saudi port or entry location this shipment arrived at. FR-MAS-01 exists so "
                    + "clearance performance can be reported by location, and free text cannot be grouped.");

            if (command.EtaDate is { } eta)
            {
                shipment.EtaDate = eta;
                shipment.EtaUpdatedOn = now;
                shipment.EtaUpdatedBy = command.Actor.Trim();
            }

            var previousMilestone = shipment.Milestone;
            shipment.Milestone = milestone;
            shipment.MilestoneOccurredOn = command.OccurredOn;
            shipment.StampMilestoneDate(milestone, command.OccurredOn);

            if (milestone == InboundShipmentMilestones.Cancelled)
            {
                // A shipment a goods receipt has already booked in cannot be cancelled: cancellation
                // releases the shipped quantity back to the purchase order line, and doing that for
                // units already counted into stock would let the line be re-shipped for material
                // that is physically on the shelf. Reverse the receipt instead.
                if (shipment.ReceiptedQuantity > 0m)
                    throw new InboundLogisticsConflictException(
                        $"{shipment.ShipmentNumber} has {shipment.ReceiptedQuantity:0.####} unit(s) already "
                        + "booked in by a goods receipt and cannot be cancelled. Reverse the goods receipt "
                        + "first, or record the shortfall against the purchase order.");
                shipment.CancellationReason = note;
                await ReleaseShippedQuantityAsync(shipment, ct);
            }

            var policy = await ResolvePolicyAsync(command.BusinessUnitId, ct);
            MaterialAvailabilityCalculator.Apply(shipment,
                MaterialAvailabilityCalculator.Derive(shipment, policy), now);

            shipment.Version++;
            shipment.ModifiedOn = now;
            shipment.ModifiedBy = command.Actor.Trim();

            AddEvent(command.BusinessUnitId, command.IdempotencyKey, command.Actor, command.CorrelationId,
                MilestoneRecordedEvent, shipment.Id, shipment.Version, now, new
                {
                    requestHash = hash,
                    previousMilestone,
                    milestone,
                    occurredOn = command.OccurredOn,
                    portOfEntryId = shipment.PortOfEntryId,
                    etaDate = shipment.EtaDate,
                    note,
                    materialAvailableDate = shipment.MaterialAvailableDate,
                    materialAvailableBasisKind = shipment.MaterialAvailableBasisKind,
                    materialAvailableBasisDate = shipment.MaterialAvailableBasisDate,
                    customsClearanceDays = shipment.AppliedCustomsClearanceDays,
                    putawayDays = shipment.AppliedPutawayDays
                });

            await db.SaveChangesAsync(ct);
            await ProjectIncomingInventoryAsync(command.BusinessUnitId, shipment.SupplierPurchaseOrderId, ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return shipment.Id;
        });

        return await RequireShipmentViewAsync(command.BusinessUnitId, shipmentId, ct);
    }

    /// <summary>
    /// FR-MAS-02, manual path. Maintains carrier, tracking reference and ETA.
    ///
    /// <para>Patch semantics: an omitted field is unchanged. The lesson is Gate 4's — a partial
    /// amendment that treated omission as deletion let a one-field correction wipe the terms the
    /// rest of the record depended on.</para>
    /// </summary>
    public async Task<InboundShipmentView> UpdateTrackingAsync(
        UpdateInboundShipmentTrackingCommand command, CancellationToken ct = default)
    {
        ValidateCommand(command.BusinessUnitId, command.IdempotencyKey, command.Actor, command.CorrelationId);
        if (command.EtaDate is not null && command.ClearEta)
            throw new InboundLogisticsValidationException(
                "Send either a new ETA or the instruction to clear it, not both.");

        var carrier = Trimmed(command.CarrierName, 255, "The carrier name");
        var tracking = Trimmed(command.TrackingReference, 160, "The tracking reference");
        var note = Trimmed(command.Note, 1000, "The note");
        if (carrier is null && tracking is null && command.EtaDate is null && !command.ClearEta)
            throw new InboundLogisticsValidationException(
                "Nothing to update. Provide a carrier, a tracking reference or an ETA.");

        var hash = Hash(new
        {
            command.ShipmentId, command.ExpectedVersion, carrier, tracking, command.EtaDate,
            command.ClearEta, note
        });

        var shipmentId = await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            if (await ReplayedAggregateIdAsync(command.BusinessUnitId, TrackingUpdatedEvent,
                    command.IdempotencyKey, hash, ct) is { } replayed)
            {
                await tx.CommitAsync(ct);
                return replayed;
            }

            var shipment = await db.Set<SupplierShipment>().SingleOrDefaultAsync(
                               x => x.BusinessUnitId == command.BusinessUnitId && x.Id == command.ShipmentId, ct)
                           ?? throw new InboundLogisticsValidationException("Shipment was not found.");
            if (shipment.Version != command.ExpectedVersion)
                throw new InboundLogisticsConflictException("The shipment changed; refresh before updating it.");
            if (shipment.IsTerminal)
                throw new InboundLogisticsConflictException(
                    "A shipment that has been received or cancelled no longer has a status or an ETA to maintain.");

            var now = DateTime.UtcNow;
            if (carrier is not null) shipment.CarrierName = carrier;
            if (tracking is not null) shipment.TrackingReference = tracking;
            if (command.ClearEta)
            {
                shipment.EtaDate = null;
                shipment.EtaUpdatedOn = now;
                shipment.EtaUpdatedBy = command.Actor.Trim();
            }
            else if (command.EtaDate is { } eta)
            {
                var earliest = shipment.LatestRecordedMilestoneDate();
                if (earliest is { } floor && eta < floor)
                    throw new InboundLogisticsValidationException(
                        $"An ETA of {eta:yyyy-MM-dd} is before this shipment's last recorded milestone on "
                        + $"{floor:yyyy-MM-dd}.");
                shipment.EtaDate = eta;
                shipment.EtaUpdatedOn = now;
                shipment.EtaUpdatedBy = command.Actor.Trim();
            }

            var policy = await ResolvePolicyAsync(command.BusinessUnitId, ct);
            MaterialAvailabilityCalculator.Apply(shipment,
                MaterialAvailabilityCalculator.Derive(shipment, policy), now);

            shipment.Version++;
            shipment.ModifiedOn = now;
            shipment.ModifiedBy = command.Actor.Trim();

            AddEvent(command.BusinessUnitId, command.IdempotencyKey, command.Actor, command.CorrelationId,
                TrackingUpdatedEvent, shipment.Id, shipment.Version, now, new
                {
                    requestHash = hash,
                    carrier = shipment.CarrierName,
                    tracking = shipment.TrackingReference,
                    // MANUAL until a carrier integration exists. Recorded on every update so a later
                    // API-sourced row is distinguishable from a keyed one without a schema change.
                    trackingSource = shipment.TrackingSource,
                    etaDate = shipment.EtaDate,
                    etaCleared = command.ClearEta,
                    note,
                    materialAvailableDate = shipment.MaterialAvailableDate
                });

            await db.SaveChangesAsync(ct);
            await ProjectIncomingInventoryAsync(command.BusinessUnitId, shipment.SupplierPurchaseOrderId, ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return shipment.Id;
        });

        return await RequireShipmentViewAsync(command.BusinessUnitId, shipmentId, ct);
    }

    // ==========================================================================================
    // Reads
    // ==========================================================================================

    public async Task<IReadOnlyCollection<InboundShipmentView>> GetPurchaseOrderShipmentsAsync(
        long businessUnitId, long purchaseOrderId, CancellationToken ct = default)
    {
        ValidateTenant(businessUnitId);
        var shipments = await db.Set<SupplierShipment>().AsNoTracking().Include(x => x.Lines)
            .Where(x => x.BusinessUnitId == businessUnitId && x.SupplierPurchaseOrderId == purchaseOrderId)
            .OrderBy(x => x.Id).ToListAsync(ct);
        return shipments.Count == 0 ? [] : await ProjectAsync(businessUnitId, shipments, ct);
    }

    public async Task<IReadOnlyCollection<PortOfEntryView>> GetPortsOfEntryAsync(
        long businessUnitId, bool includeInactive, CancellationToken ct = default)
    {
        ValidateTenant(businessUnitId);
        return await db.Set<PortOfEntry>().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && (includeInactive || x.IsActive))
            .OrderBy(x => x.Kind).ThenBy(x => x.Name)
            .Select(x => new PortOfEntryView(x.Id, x.Code, x.Name, x.Kind, x.CountryCode, x.City, x.IsActive, x.Version))
            .ToListAsync(ct);
    }

    // ==========================================================================================
    // Port master (FR-MAS-01 reference data)
    // ==========================================================================================

    public async Task<PortOfEntryView> CreatePortOfEntryAsync(
        CreatePortOfEntryCommand command, CancellationToken ct = default)
    {
        ValidateCommand(command.BusinessUnitId, command.IdempotencyKey, command.Actor, command.CorrelationId);
        var code = NormalizeCode(command.Code);
        var name = Trimmed(command.Name, 200, "The location name")
                   ?? throw new InboundLogisticsValidationException("A location name is required.");
        var kind = (command.Kind ?? string.Empty).Trim().ToUpperInvariant();
        if (!PortOfEntryKinds.All.Contains(kind))
            throw new InboundLogisticsValidationException(
                "A location kind must be one of: " + string.Join(", ", PortOfEntryKinds.All) + ".");
        var country = (command.CountryCode ?? "SA").Trim().ToUpperInvariant();
        if (country.Length != 2 || !country.All(char.IsAsciiLetterUpper))
            throw new InboundLogisticsValidationException("The country code must be a 2-letter ISO code.");
        var city = Trimmed(command.City, 120, "The city");

        var hash = Hash(new { code, name, kind, country, city });
        var portId = await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            if (await ReplayedAggregateIdAsync(command.BusinessUnitId, PortCreatedEvent,
                    command.IdempotencyKey, hash, ct) is { } replayed)
            {
                await tx.CommitAsync(ct);
                return replayed;
            }

            if (await db.Set<PortOfEntry>().AnyAsync(
                    x => x.BusinessUnitId == command.BusinessUnitId && x.Code == code, ct))
                throw new InboundLogisticsConflictException($"A location with code {code} already exists.");

            var now = DateTime.UtcNow;
            var port = new PortOfEntry
            {
                BusinessUnitId = command.BusinessUnitId,
                Code = code,
                Name = name,
                Kind = kind,
                CountryCode = country,
                City = city,
                IsActive = true,
                CreatedOn = now,
                CreatedBy = command.Actor.Trim()
            };
            db.Set<PortOfEntry>().Add(port);
            await db.SaveChangesAsync(ct);

            AddEvent(command.BusinessUnitId, command.IdempotencyKey, command.Actor, command.CorrelationId,
                PortCreatedEvent, port.Id, port.Version, now,
                new { requestHash = hash, code, name, kind, country, city }, PortAggregateType);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return port.Id;
        });

        return await RequirePortViewAsync(command.BusinessUnitId, portId, ct);
    }

    public async Task<PortOfEntryView> SetPortOfEntryActiveAsync(
        SetPortOfEntryActiveCommand command, CancellationToken ct = default)
    {
        ValidateCommand(command.BusinessUnitId, command.IdempotencyKey, command.Actor, command.CorrelationId);
        var hash = Hash(new { command.PortOfEntryId, command.IsActive, command.ExpectedVersion });

        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            if (await ReplayedAggregateIdAsync(command.BusinessUnitId, PortActivationEvent,
                    command.IdempotencyKey, hash, ct) is not null)
            {
                await tx.CommitAsync(ct);
                return;
            }

            var port = await db.Set<PortOfEntry>().SingleOrDefaultAsync(
                           x => x.BusinessUnitId == command.BusinessUnitId && x.Id == command.PortOfEntryId, ct)
                       ?? throw new InboundLogisticsValidationException("Location was not found.");
            if (port.Version != command.ExpectedVersion)
                throw new InboundLogisticsConflictException("The location changed; refresh before saving.");

            var now = DateTime.UtcNow;
            port.IsActive = command.IsActive;
            port.Version++;
            port.ModifiedOn = now;
            port.ModifiedBy = command.Actor.Trim();

            AddEvent(command.BusinessUnitId, command.IdempotencyKey, command.Actor, command.CorrelationId,
                PortActivationEvent, port.Id, port.Version, now,
                new { requestHash = hash, port.Code, isActive = command.IsActive }, PortAggregateType);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });

        return await RequirePortViewAsync(command.BusinessUnitId, command.PortOfEntryId, ct);
    }

    /// <summary>
    /// Loads <see cref="SaudiPortCatalogue"/> into the tenant on an explicit operator request.
    ///
    /// <para>Codes the tenant already holds are skipped entirely, including any the tenant has
    /// renamed or deactivated — a re-import must not undo a local decision.</para>
    /// </summary>
    public async Task<IReadOnlyCollection<PortOfEntryView>> ImportStandardSaudiPortsAsync(
        ImportStandardSaudiPortsCommand command, CancellationToken ct = default)
    {
        ValidateCommand(command.BusinessUnitId, command.IdempotencyKey, command.Actor, command.CorrelationId);
        var hash = Hash(new { catalogue = SaudiPortCatalogue.Entries.Select(x => x.Code).ToArray() });

        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            if (await ReplayedAggregateIdAsync(command.BusinessUnitId, PortCatalogueImportedEvent,
                    command.IdempotencyKey, hash, ct) is not null)
            {
                await tx.CommitAsync(ct);
                return;
            }

            var existing = await db.Set<PortOfEntry>()
                .Where(x => x.BusinessUnitId == command.BusinessUnitId)
                .Select(x => x.Code).ToListAsync(ct);
            var known = new HashSet<string>(existing, StringComparer.Ordinal);
            var now = DateTime.UtcNow;
            var added = new List<string>();

            foreach (var entry in SaudiPortCatalogue.Entries)
            {
                if (!known.Add(entry.Code)) continue;
                db.Set<PortOfEntry>().Add(new PortOfEntry
                {
                    BusinessUnitId = command.BusinessUnitId,
                    Code = entry.Code,
                    Name = entry.Name,
                    Kind = entry.Kind,
                    CountryCode = "SA",
                    City = entry.City,
                    IsActive = true,
                    CreatedOn = now,
                    CreatedBy = command.Actor.Trim()
                });
                added.Add(entry.Code);
            }

            await db.SaveChangesAsync(ct);
            AddEvent(command.BusinessUnitId, command.IdempotencyKey, command.Actor, command.CorrelationId,
                // A tenant-wide load has no single aggregate, so the tenant itself is the subject.
                PortCatalogueImportedEvent, command.BusinessUnitId, 1, now,
                new { requestHash = hash, added, skipped = SaudiPortCatalogue.Entries.Count - added.Count },
                PortAggregateType);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });

        return await GetPortsOfEntryAsync(command.BusinessUnitId, includeInactive: false, ct);
    }

    // ==========================================================================================
    // Policy (FR-MAS-03 inputs)
    // ==========================================================================================

    public async Task<InboundLogisticsPolicyView> GetPolicyAsync(long businessUnitId, CancellationToken ct = default)
    {
        ValidateTenant(businessUnitId);
        var stored = await db.Set<InboundLogisticsPolicy>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId, ct);
        return MapPolicy(stored ?? InboundLogisticsPolicy.DefaultFor(businessUnitId), stored is null);
    }

    public async Task<InboundLogisticsPolicyView> UpdatePolicyAsync(
        UpdateInboundLogisticsPolicyCommand command, CancellationToken ct = default)
    {
        ValidateCommand(command.BusinessUnitId, command.IdempotencyKey, command.Actor, command.CorrelationId);
        if (command.ClearLeadTimes &&
            (command.CustomsClearanceLeadDays is not null || command.PutawayLeadDays is not null))
            throw new InboundLogisticsValidationException(
                "Send either lead times or the instruction to clear them, not both.");
        BoundedDays(command.CustomsClearanceLeadDays, "The customs-clearance lead time");
        BoundedDays(command.PutawayLeadDays, "The putaway lead time");
        if (!command.ClearLeadTimes
            && command.CustomsClearanceLeadDays is null && command.PutawayLeadDays is null)
            throw new InboundLogisticsValidationException("Nothing to update.");

        var hash = Hash(new
        {
            command.CustomsClearanceLeadDays, command.PutawayLeadDays, command.ClearLeadTimes
        });

        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            if (await ReplayedAggregateIdAsync(command.BusinessUnitId, PolicyUpdatedEvent,
                    command.IdempotencyKey, hash, ct) is not null)
            {
                await tx.CommitAsync(ct);
                return;
            }

            var now = DateTime.UtcNow;
            var policy = await db.Set<InboundLogisticsPolicy>()
                .SingleOrDefaultAsync(x => x.BusinessUnitId == command.BusinessUnitId, ct);
            var before = policy is null ? null : MapPolicy(policy, false);
            if (policy is null)
            {
                // Create on demand: the read path never writes, so the first edit is exactly when a
                // row should start to exist.
                policy = InboundLogisticsPolicy.DefaultFor(command.BusinessUnitId);
                policy.CreatedOn = now;
                db.Set<InboundLogisticsPolicy>().Add(policy);
            }

            if (command.ClearLeadTimes)
            {
                policy.CustomsClearanceLeadDays = null;
                policy.PutawayLeadDays = null;
            }
            else
            {
                if (command.CustomsClearanceLeadDays is { } customs) policy.CustomsClearanceLeadDays = customs;
                if (command.PutawayLeadDays is { } putaway) policy.PutawayLeadDays = putaway;
            }

            policy.Version++;
            policy.ModifiedOn = now;
            policy.ModifiedBy = command.Actor.Trim();
            await db.SaveChangesAsync(ct);

            AddEvent(command.BusinessUnitId, command.IdempotencyKey, command.Actor, command.CorrelationId,
                PolicyUpdatedEvent, policy.Id, policy.Version, now, new
                {
                    requestHash = hash,
                    // Before AND after. "The clearance allowance is 5" tells a reviewer nothing;
                    // "it was 2, this person made it 5 on this date" is what explains why a
                    // shipment quoted last week carries a different availability date from one
                    // quoted today.
                    before,
                    after = MapPolicy(policy, false)
                }, PolicyAggregateType);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });

        // Every shipment's Material Available Date was derived from the OLD lead times. Leaving them
        // stale would show a tenant a date that no longer matches the policy it just set, which is
        // the specific way a derived figure loses its credibility.
        await RederiveOpenShipmentsAsync(command.BusinessUnitId, ct);
        return await GetPolicyAsync(command.BusinessUnitId, ct);
    }

    /// <summary>
    /// Recomputes every non-terminal shipment in the tenant after a policy change, and re-projects
    /// the inbound expectation each one drives.
    /// </summary>
    private async Task RederiveOpenShipmentsAsync(long businessUnitId, CancellationToken ct)
    {
        var policy = await ResolvePolicyAsync(businessUnitId, ct);
        var open = await db.Set<SupplierShipment>()
            .Where(x => x.BusinessUnitId == businessUnitId
                        && x.Milestone != InboundShipmentMilestones.Cancelled
                        && x.Milestone != InboundShipmentMilestones.ReceivedAtWarehouse)
            .ToListAsync(ct);
        if (open.Count == 0) return;

        var now = DateTime.UtcNow;
        foreach (var shipment in open)
            MaterialAvailabilityCalculator.Apply(shipment,
                MaterialAvailabilityCalculator.Derive(shipment, policy), now);
        await db.SaveChangesAsync(ct);

        foreach (var orderId in open.Select(x => x.SupplierPurchaseOrderId).Distinct())
            await ProjectIncomingInventoryAsync(businessUnitId, orderId, ct);
        await db.SaveChangesAsync(ct);
    }

    // ==========================================================================================
    // Projection onto IncomingInventory
    // ==========================================================================================

    /// <summary>
    /// FR-MAS-03, the propagation half. Pushes the shipment-derived availability onto the inbound
    /// supply rows that ATP and the sourcing net-requirement calculation already read.
    ///
    /// <para><b>The rule is conservative on purpose.</b> <c>IncomingInventory</c> carries ONE date
    /// for a whole purchase order line, while this gate knows the line may be split across several
    /// shipments landing weeks apart. Taking the earliest shipment's date would tell ATP the entire
    /// line lands then, and a promise made on that is a promise broken. So the date written is the
    /// LATEST material-available date among the shipments still in flight for that line, and the
    /// purchase order's own expected date stays in the running whenever some of the open quantity is
    /// on no shipment at all, or a shipment has no derivable date. The per-shipment detail — which
    /// is what a partial promise should actually be made against — is on the shipment itself.</para>
    ///
    /// <para><b>Status is never written backwards over a receipt.</b> Goods receipt owns
    /// PARTIALLY_RECEIVED, RECEIVED and CANCELLED; this only ever moves a row that is still
    /// Planned/Ordered/Confirmed forward to InTransit once something has physically departed.</para>
    /// </summary>
    private async Task ProjectIncomingInventoryAsync(long businessUnitId, long purchaseOrderId, CancellationToken ct)
    {
        var order = await db.SupplierPurchaseOrders.Include(x => x.Lines)
            .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == purchaseOrderId, ct);
        if (order is null) return;

        var shipments = await db.Set<SupplierShipment>().Include(x => x.Lines)
            .Where(x => x.BusinessUnitId == businessUnitId && x.SupplierPurchaseOrderId == purchaseOrderId)
            .ToListAsync(ct);

        var inFlight = shipments.Where(x =>
            x.Milestone != InboundShipmentMilestones.Cancelled
            && x.Milestone != InboundShipmentMilestones.ReceivedAtWarehouse).ToList();

        foreach (var line in order.Lines)
        {
            if (line.IncomingInventoryId is null) continue;
            var incoming = await db.IncomingInventory.SingleOrDefaultAsync(
                x => x.BusinessUnitId == businessUnitId && x.Id == line.IncomingInventoryId.Value, ct);
            if (incoming is null) continue;
            if (incoming.Status is IncomingInventoryStatus.Received
                or IncomingInventoryStatus.PartiallyReceived
                or IncomingInventoryStatus.Cancelled) continue;

            var covering = inFlight
                .Select(shipment => new
                {
                    Shipment = shipment,
                    Quantity = shipment.Lines.Where(l => l.SupplierPurchaseOrderLineId == line.Id)
                        .Sum(l => l.ShippedQuantity)
                })
                .Where(x => x.Quantity > 0m)
                .ToList();
            if (covering.Count == 0) continue;

            var inFlightQuantity = covering.Sum(x => x.Quantity);
            var openQuantity = Math.Max(0m, line.OrderedQuantity - line.ReceivedQuantity);
            var unshipped = openQuantity - inFlightQuantity;
            var anyUndated = covering.Any(x => x.Shipment.MaterialAvailableDate is null);

            // The purchase order's own expectation stays in play whenever it is the only date some
            // of the open quantity has.
            DateOnly? candidate = unshipped > 0m || anyUndated ? order.ExpectedOn : null;
            foreach (var known in covering
                         .Select(x => x.Shipment.MaterialAvailableDate)
                         .Where(x => x is not null)
                         .Select(x => x!.Value))
            {
                if (candidate is null || known > candidate) candidate = known;
            }

            if (candidate is { } expected && expected != incoming.ExpectedOn)
                db.Entry(incoming).Property(x => x.ExpectedOn).CurrentValue = expected;

            if (covering.Any(x => InboundShipmentMilestones.Rank(x.Shipment.Milestone)
                                  >= InboundShipmentMilestones.Rank(InboundShipmentMilestones.DepartedOrigin))
                && incoming.Status != IncomingInventoryStatus.InTransit)
            {
                db.Entry(incoming).Property(x => x.Status).CurrentValue = IncomingInventoryStatus.InTransit;
            }
        }
    }

    /// <summary>Gives a cancelled shipment's quantity back to the purchase order lines it held.</summary>
    private async Task ReleaseShippedQuantityAsync(SupplierShipment shipment, CancellationToken ct)
    {
        var lineIds = shipment.Lines.Select(x => x.SupplierPurchaseOrderLineId).ToArray();
        if (lineIds.Length == 0) return;
        var orderLines = await db.SupplierPurchaseOrderLines
            .Where(x => x.BusinessUnitId == shipment.BusinessUnitId && lineIds.Contains(x.Id))
            .ToListAsync(ct);
        foreach (var shipmentLine in shipment.Lines)
        {
            var orderLine = orderLines.SingleOrDefault(x => x.Id == shipmentLine.SupplierPurchaseOrderLineId);
            if (orderLine is null) continue;
            orderLine.ShippedQuantity = Math.Max(0m, orderLine.ShippedQuantity - shipmentLine.ShippedQuantity);
            orderLine.Version++;
        }
    }

    // ==========================================================================================
    // Projection helpers
    // ==========================================================================================

    private async Task<InboundShipmentView> RequireShipmentViewAsync(
        long businessUnitId, long shipmentId, CancellationToken ct)
    {
        var shipment = await db.Set<SupplierShipment>().AsNoTracking().Include(x => x.Lines)
                           .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == shipmentId, ct)
                       ?? throw new InboundLogisticsValidationException("Shipment was not found.");
        return (await ProjectAsync(businessUnitId, [shipment], ct)).Single();
    }

    private async Task<PortOfEntryView> RequirePortViewAsync(long businessUnitId, long portId, CancellationToken ct)
    {
        var port = await db.Set<PortOfEntry>().AsNoTracking()
                       .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == portId, ct)
                   ?? throw new InboundLogisticsValidationException("Location was not found.");
        return new PortOfEntryView(port.Id, port.Code, port.Name, port.Kind, port.CountryCode, port.City,
            port.IsActive, port.Version);
    }

    private async Task<IReadOnlyCollection<InboundShipmentView>> ProjectAsync(
        long businessUnitId, IReadOnlyCollection<SupplierShipment> shipments, CancellationToken ct)
    {
        var orderIds = shipments.Select(x => x.SupplierPurchaseOrderId).Distinct().ToArray();
        var orders = await db.SupplierPurchaseOrders.AsNoTracking().Include(x => x.Lines)
            .Where(x => x.BusinessUnitId == businessUnitId && orderIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, ct);
        var orderLines = orders.Values.SelectMany(x => x.Lines).ToDictionary(x => x.Id);
        var rfqItemIds = orderLines.Values.Select(x => x.RfqItemId).Distinct().ToArray();
        var rfqItems = await db.Rfqitems.AsNoTracking()
            .Where(x => rfqItemIds.Contains(x.Id))
            .Select(x => new
            {
                x.Id, x.ProductShortDescription, x.ProductShortName, x.CommodityProduct,
                x.ItemMaterialCode, x.RequiredDesiredDate
            })
            .ToDictionaryAsync(x => x.Id, ct);
        var portIds = shipments.Where(x => x.PortOfEntryId.HasValue)
            .Select(x => x.PortOfEntryId!.Value).Distinct().ToArray();
        var ports = await db.Set<PortOfEntry>().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && portIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Name, ct);

        var shipmentIds = shipments.Select(x => x.Id).ToArray();
        var events = await db.ProcurementEvents.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId
                        && x.AggregateType == AggregateType
                        && shipmentIds.Contains(x.AggregateId))
            .OrderBy(x => x.OccurredOn).ThenBy(x => x.Id)
            .Select(x => new { x.AggregateId, x.EventType, x.Actor, x.OccurredOn, x.PayloadJson })
            .ToListAsync(ct);

        var views = new List<InboundShipmentView>(shipments.Count);
        foreach (var shipment in shipments)
        {
            var order = orders.GetValueOrDefault(shipment.SupplierPurchaseOrderId);
            var lines = shipment.Lines.OrderBy(x => x.Id).Select(line =>
            {
                var orderLine = orderLines.GetValueOrDefault(line.SupplierPurchaseOrderLineId);
                var rfqItem = orderLine is null ? null : rfqItems.GetValueOrDefault(orderLine.RfqItemId);
                var description = rfqItem?.ProductShortDescription ?? rfqItem?.ProductShortName
                    ?? rfqItem?.CommodityProduct ?? rfqItem?.ItemMaterialCode
                    ?? $"Purchase order line {line.SupplierPurchaseOrderLineId}";
                return new InboundShipmentLineView(line.Id, line.SupplierPurchaseOrderLineId, line.ProductId,
                    description, line.ShippedQuantity, orderLine?.OrderedQuantity ?? 0m,
                    orderLine?.ShippedQuantity ?? 0m, orderLine?.ReceivedQuantity ?? 0m,
                    line.ReceivedQuantity, line.OutstandingReceiptQuantity);
            }).ToArray();

            DateOnly? requiredOn = null;
            foreach (var line in shipment.Lines)
            {
                var orderLine = orderLines.GetValueOrDefault(line.SupplierPurchaseOrderLineId);
                var rfqItem = orderLine is null ? null : rfqItems.GetValueOrDefault(orderLine.RfqItemId);
                if (rfqItem?.RequiredDesiredDate is not { } required) continue;
                var candidate = DateOnly.FromDateTime(required);
                // The EARLIEST required date across the covered demand: the shipment is at risk as
                // soon as it misses the tightest promise on it, not the loosest.
                if (requiredOn is null || candidate < requiredOn) requiredOn = candidate;
            }

            var history = events.Where(x => x.AggregateId == shipment.Id)
                .Select(x => ToHistory(x.EventType, x.Actor, x.OccurredOn, x.PayloadJson))
                .ToArray();

            views.Add(new InboundShipmentView(
                shipment.Id, shipment.SupplierPurchaseOrderId, order?.PurchaseOrderNumber ?? string.Empty,
                shipment.ShipmentNumber, shipment.Milestone, shipment.MilestoneOccurredOn,
                shipment.ReadyAtFactoryOn, shipment.DepartedOriginOn, shipment.InTransitOn,
                shipment.ArrivedSaudiPortOn, shipment.CustomsClearanceOn, shipment.ReceivedAtWarehouseOn,
                shipment.CancelledOn, shipment.CancellationReason,
                shipment.PortOfEntryId,
                shipment.PortOfEntryId is { } portId ? ports.GetValueOrDefault(portId) : null,
                shipment.CarrierName, shipment.TrackingReference, shipment.TrackingSource,
                shipment.EtaDate, shipment.EtaUpdatedOn, shipment.EtaUpdatedBy,
                new MaterialAvailabilityView(shipment.MaterialAvailableDate, shipment.MaterialAvailableBasisKind,
                    shipment.MaterialAvailableBasisDate, shipment.AppliedCustomsClearanceDays,
                    shipment.AppliedPutawayDays, shipment.MaterialAvailableComputedOn,
                    shipment.MaterialAvailableUnavailableReason),
                order?.CommittedShipDate, requiredOn,
                shipment.ReceiptState, shipment.ReceiptedQuantity, shipment.OutstandingReceiptQuantity,
                shipment.Version, lines, history,
                InboundShipmentMilestones.All
                    .Where(next => InboundShipmentMilestones.CanTransition(shipment.Milestone, next))
                    .ToArray()));
        }

        return views;
    }

    private static InboundShipmentMilestoneEventView ToHistory(
        string eventType, string actor, DateTime occurredOn, string payloadJson)
    {
        string? milestone = null;
        DateOnly? on = null;
        string? note = null;
        try
        {
            using var payload = JsonDocument.Parse(payloadJson);
            var root = payload.RootElement;
            if (root.TryGetProperty("milestone", out var milestoneValue))
                milestone = milestoneValue.GetString();
            if (root.TryGetProperty("occurredOn", out var occurred)
                && DateOnly.TryParse(occurred.GetString(), out var parsed))
                on = parsed;
            if (root.TryGetProperty("note", out var noteValue) && noteValue.ValueKind == JsonValueKind.String)
                note = noteValue.GetString();
        }
        catch (JsonException)
        {
            // A malformed payload must never make an entire shipment unreadable. The row itself —
            // event type, actor, timestamp — is still true and still worth showing.
        }

        return new InboundShipmentMilestoneEventView(eventType, milestone, on, actor, occurredOn, note);
    }

    // ==========================================================================================
    // Shared helpers
    // ==========================================================================================

    internal async Task<InboundLogisticsPolicy> ResolvePolicyAsync(long businessUnitId, CancellationToken ct)
        => await db.Set<InboundLogisticsPolicy>().AsNoTracking()
               .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId, ct)
           ?? InboundLogisticsPolicy.DefaultFor(businessUnitId);

    private async Task<PortOfEntry?> ResolveActivePortAsync(long businessUnitId, long? portId, CancellationToken ct)
    {
        if (portId is not { } id) return null;
        var port = await db.Set<PortOfEntry>().AsNoTracking()
                       .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == id, ct)
                   ?? throw new InboundLogisticsValidationException("The named entry location was not found.");
        if (!port.IsActive)
            throw new InboundLogisticsValidationException(
                $"{port.Name} is no longer an active entry location for this business unit.");
        return port;
    }

    /// <summary>
    /// Returns the aggregate id an earlier identical request produced, or null when this is the
    /// first time the key has been seen. A key reused for a DIFFERENT request is a conflict, not a
    /// replay — silently returning the first result would hide a client bug behind a success.
    /// </summary>
    private async Task<long?> ReplayedAggregateIdAsync(
        long businessUnitId, string eventType, string idempotencyKey, string hash, CancellationToken ct)
    {
        var existing = await db.ProcurementEvents.AsNoTracking().SingleOrDefaultAsync(
            x => x.BusinessUnitId == businessUnitId && x.EventType == eventType
                 && x.IdempotencyKey == idempotencyKey.Trim(), ct);
        if (existing is null) return null;

        using var payload = JsonDocument.Parse(existing.PayloadJson);
        var recorded = payload.RootElement.TryGetProperty("requestHash", out var value) ? value.GetString() : null;
        if (!string.Equals(recorded, hash, StringComparison.Ordinal))
            throw new InboundLogisticsConflictException(
                "The idempotency key was already used for a different request.");
        return existing.AggregateId;
    }

    private void AddEvent(CreateInboundShipmentCommand command, string eventType, long aggregateId,
        long version, DateTime now, object payload)
        => AddEvent(command.BusinessUnitId, command.IdempotencyKey, command.Actor, command.CorrelationId,
            eventType, aggregateId, version, now, payload);

    private void AddEvent(long businessUnitId, string idempotencyKey, string actor, string correlationId,
        string eventType, long aggregateId, long version, DateTime now, object payload,
        string aggregateType = AggregateType)
        => db.ProcurementEvents.Add(new ProcurementEvent
        {
            BusinessUnitId = businessUnitId,
            AggregateType = aggregateType,
            AggregateId = aggregateId,
            AggregateVersion = version,
            EventType = eventType,
            Actor = actor.Trim(),
            CorrelationId = correlationId.Trim(),
            IdempotencyKey = idempotencyKey.Trim(),
            PayloadJson = JsonSerializer.Serialize(payload),
            OccurredOn = now
        });

    private static InboundLogisticsPolicyView MapPolicy(InboundLogisticsPolicy policy, bool isDefault)
        => new(policy.BusinessUnitId, policy.CustomsClearanceLeadDays, policy.PutawayLeadDays,
            policy.IsConfigured, policy.Version, policy.ModifiedOn, policy.ModifiedBy, isDefault);

    private static string Hash(object value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))))
            .ToLowerInvariant();

    private static string NormalizeCode(string? value)
    {
        var code = (value ?? string.Empty).Trim().ToUpperInvariant();
        if (code.Length is 0 or > 40)
            throw new InboundLogisticsValidationException("A location code of up to 40 characters is required.");
        if (!code.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new InboundLogisticsValidationException(
                "A location code may contain only letters, digits, hyphens and underscores.");
        return code;
    }

    private static string? Trimmed(string? value, int maxLength, string subject)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
            throw new InboundLogisticsValidationException($"{subject} must be {maxLength} characters or fewer.");
        return trimmed;
    }

    /// <summary>
    /// A lead time is a count of working days. Negative is meaningless and 365 is a ceiling that
    /// catches a mis-keyed date being typed into a day box.
    /// </summary>
    private static void BoundedDays(int? value, string subject)
    {
        if (value is null) return;
        if (value < 0 || value > 365)
            throw new InboundLogisticsValidationException($"{subject} must be between 0 and 365 days.");
    }

    private static void ValidateCommand(long businessUnitId, string idempotencyKey, string actor, string correlationId)
    {
        ValidateTenant(businessUnitId);
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Trim().Length > 130)
            throw new InboundLogisticsValidationException("An idempotency key up to 130 characters is required.");
        if (string.IsNullOrWhiteSpace(actor) || actor.Trim().Length > 255)
            throw new InboundLogisticsValidationException("An actor up to 255 characters is required.");
        if (string.IsNullOrWhiteSpace(correlationId) || correlationId.Trim().Length > 160)
            throw new InboundLogisticsValidationException("A correlation ID up to 160 characters is required.");
    }

    private static void ValidateTenant(long businessUnitId)
    {
        if (businessUnitId <= 0)
            throw new InboundLogisticsValidationException("A valid authenticated business unit is required.");
    }
}
