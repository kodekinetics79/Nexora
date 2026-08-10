using System.Data;
using System.Text.Json;
using ERP_RFQ_Automation.Inventory;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Procurement;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Traceability;

/// <summary>
/// Gate 5 Module 5 — the read and command surface for material traceability.
///
/// <para><b>Three things this class refuses to do.</b></para>
///
/// <para>It does not create lots. That is <see cref="IMaterialLotRecorder"/>, called from inside
/// the goods-receipt transaction, so no lot can exist without a receipt behind it.</para>
///
/// <para>It does not write <c>Inventory.QuarantineQuantity</c>. <see cref="IStockLedgerService"/>
/// is the one sanctioned writer of the stock buckets and posts a balancing movement in the same
/// transaction; a second writer here would be a second way for the on-hand balance to drift from
/// the ledger, which is the failure this product has already paid for once.</para>
///
/// <para>It does not derive where-used from foreign keys. Membership comes from
/// <see cref="MaterialLotConsumption"/> — the row a fulfilment writes to state which lot it used.
/// A join from order to product to inventory to "lots that happen to sit there" answers which lots
/// COULD have shipped and presents it as which lots DID; where the declaration is missing, this
/// class reports a gap instead of guessing. Same rule, and the same reason, as
/// <c>CommercialCaseQueryService</c>.</para>
/// </summary>
public sealed class MaterialTraceabilityService(
    ErpRfqAutomationContext db,
    IStockLedgerService stockLedger,
    IInventoryAvailabilityService availability) : IMaterialTraceabilityService
{
    private readonly ErpRfqAutomationContext _db = db;
    private readonly IStockLedgerService _stockLedger = stockLedger;
    private readonly IInventoryAvailabilityService _availability = availability;

    // -----------------------------------------------------------------------------------------
    // Reads
    // -----------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<LotSummaryView>> SearchLotsAsync(
        long businessUnitId, LotSearchQuery query, CancellationToken ct = default)
    {
        RequireTenant(businessUnitId);
        ArgumentNullException.ThrowIfNull(query);
        var limit = Math.Clamp(query.Limit, 1, 200);
        var today = Today();

        var lots = _db.Set<MaterialLot>().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId);

        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            var status = query.Status.Trim().ToUpperInvariant();
            if (!MaterialLotStatuses.All.Contains(status))
                throw new MaterialTraceabilityValidationException($"Unknown lot status '{query.Status}'.");
            lots = lots.Where(x => x.Status == status);
        }

        if (query.SupplierId is > 0) lots = lots.Where(x => x.SupplierId == query.SupplierId);
        if (query.ProductId is > 0) lots = lots.Where(x => x.ProductId == query.ProductId);
        if (query.WarehouseId is > 0) lots = lots.Where(x => x.WarehouseId == query.WarehouseId);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToLower();
            lots = lots.Where(x =>
                x.LotNumber.ToLower().Contains(term)
                || (x.ManufacturerName != null && x.ManufacturerName.ToLower().Contains(term))
                || (x.ManufacturerPartNumber != null && x.ManufacturerPartNumber.ToLower().Contains(term))
                || (x.CountryOfOrigin != null && x.CountryOfOrigin.ToLower().Contains(term))
                || (x.SupplierBatchReference != null && x.SupplierBatchReference.ToLower().Contains(term))
                || (x.NexoraSerial != null && x.NexoraSerial.ToLower().Contains(term)));
        }

        if (query.ExpiredCertificatesOnly)
            lots = lots.Where(x => _db.Set<MaterialLotCertificate>().Any(c =>
                c.BusinessUnitId == businessUnitId && c.MaterialLotId == x.Id
                && c.ExpiresOn != null && c.ExpiresOn < today));

        var rows = await lots
            .OrderByDescending(x => x.ReceivedOn).ThenByDescending(x => x.Id)
            .Take(limit)
            .Select(x => new
            {
                x.Id, x.LotNumber, x.TrackingMode, x.Status, x.ProductId, x.WarehouseId,
                x.QuantityReceived, x.QuantityConsumed, x.CountryOfOrigin, x.ManufacturerName,
                x.SupplierId, x.SupplierPurchaseOrderId, x.ReceivedOn, x.CommercialCaseId,
                x.NexoraSerial, x.Version,
                PartNumber = _db.Products.Where(p => p.Buid == businessUnitId && p.Id == x.ProductId)
                    .Select(p => p.PartNo).FirstOrDefault(),
                ProductName = _db.Products.Where(p => p.Buid == businessUnitId && p.Id == x.ProductId)
                    .Select(p => p.ProductName).FirstOrDefault(),
                WarehouseName = _db.Warehouses
                    .Where(w => w.BusinessUnitId == businessUnitId && w.Id == x.WarehouseId)
                    .Select(w => w.WarehouseName).FirstOrDefault(),
                SupplierName = _db.Suppliers.Where(s => s.Buid == businessUnitId && s.Id == x.SupplierId)
                    .Select(s => s.Name).FirstOrDefault(),
                PurchaseOrderNumber = _db.SupplierPurchaseOrders
                    .Where(p => p.BusinessUnitId == businessUnitId && p.Id == x.SupplierPurchaseOrderId)
                    .Select(p => p.PurchaseOrderNumber).FirstOrDefault(),
            })
            .ToListAsync(ct);

        var lotIds = rows.Select(x => x.Id).ToArray();
        var certificates = await _db.Set<MaterialLotCertificate>().AsNoTracking()
            .Where(c => c.BusinessUnitId == businessUnitId && lotIds.Contains(c.MaterialLotId))
            .Select(c => new { c.MaterialLotId, c.CertificateType, c.ExpiresOn })
            .ToListAsync(ct);
        var byLot = certificates.GroupBy(c => c.MaterialLotId)
            .ToDictionary(g => g.Key, g => g.ToArray());

        return rows.Select(x =>
        {
            var lotCertificates = byLot.GetValueOrDefault(x.Id, []);
            return new LotSummaryView(
                x.Id, x.LotNumber, x.TrackingMode, x.Status, x.ProductId, x.PartNumber, x.ProductName,
                x.WarehouseId, x.WarehouseName, x.QuantityReceived, x.QuantityConsumed,
                x.QuantityReceived - x.QuantityConsumed, x.CountryOfOrigin, x.ManufacturerName,
                x.SupplierId, x.SupplierName, x.SupplierPurchaseOrderId, x.PurchaseOrderNumber,
                x.ReceivedOn,
                CertificateState(lotCertificates.Select(c => (c.CertificateType, c.ExpiresOn)), today),
                lotCertificates.Length,
                lotCertificates.Where(c => c.ExpiresOn.HasValue)
                    .Select(c => (DateOnly?)c.ExpiresOn!.Value).Min(),
                x.CommercialCaseId, x.NexoraSerial, x.Version);
        }).ToList();
    }

    /// <summary>FR-MTR-03 where-from, plus the lot's certificates and its declared onward uses.</summary>
    public async Task<LotWhereFromView> GetLotAsync(
        long businessUnitId, long materialLotId, CancellationToken ct = default)
    {
        RequireTenant(businessUnitId);
        var today = Today();

        var lot = await _db.Set<MaterialLot>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == materialLotId, ct)
            ?? throw new MaterialTraceabilityValidationException("Material lot was not found in this tenant.");

        var origin = await _db.SupplierPurchaseOrders.AsNoTracking()
            .Where(po => po.BusinessUnitId == businessUnitId && po.Id == lot.SupplierPurchaseOrderId)
            .Select(po => new { po.PurchaseOrderNumber, po.DemandSource, po.RfqId })
            .SingleOrDefaultAsync(ct);
        var poLine = await _db.SupplierPurchaseOrderLines.AsNoTracking()
            .Where(l => l.BusinessUnitId == businessUnitId && l.Id == lot.SupplierPurchaseOrderLineId)
            .Select(l => new { l.HsCode, l.CountryOfOrigin })
            .SingleOrDefaultAsync(ct);
        var receiptNumber = await _db.GoodsReceipts.AsNoTracking()
            .Where(r => r.BusinessUnitId == businessUnitId && r.Id == lot.GoodsReceiptId)
            .Select(r => r.ReceiptNumber).FirstOrDefaultAsync(ct);
        var supplierName = await _db.Suppliers.AsNoTracking()
            .Where(s => s.Buid == businessUnitId && s.Id == lot.SupplierId)
            .Select(s => s.Name).FirstOrDefaultAsync(ct);
        var product = await _db.Products.AsNoTracking()
            .Where(p => p.Buid == businessUnitId && p.Id == lot.ProductId)
            .Select(p => new { p.PartNo, p.ProductName }).FirstOrDefaultAsync(ct);
        var warehouseName = await _db.Warehouses.AsNoTracking()
            .Where(w => w.BusinessUnitId == businessUnitId && w.Id == lot.WarehouseId)
            .Select(w => w.WarehouseName).FirstOrDefaultAsync(ct);
        var rfqNumber = origin?.RfqId is { } rfqId
            ? await _db.Rfqs.AsNoTracking()
                .Where(r => r.BusinessUnitId == businessUnitId && r.Id == rfqId)
                .Select(r => r.Rfqno).FirstOrDefaultAsync(ct)
            : null;

        var certificates = await ReadCertificatesAsync(businessUnitId, [materialLotId], today, ct);
        var fulfilments = await ReadFulfilmentsAsync(businessUnitId, materialLotId, ct);

        var gaps = new List<MaterialTraceGap>();
        var certificateState = CertificateState(
            certificates.Select(c => (c.CertificateType, c.ExpiresOn)), today);

        if (certificates.Count == 0)
            gaps.Add(new MaterialTraceGap(MaterialTraceGapKinds.NoCertificate, "Lot", lot.Id, lot.LotNumber,
                lot.QuantityReceived,
                "No manufacturer, origin or conformity certificate is held against this lot. "
                + "A customs or customer compliance request cannot be answered from the record."));

        foreach (var expired in certificates.Where(c => c.ExpiryState == CertificateExpiryStates.Expired))
            gaps.Add(new MaterialTraceGap(MaterialTraceGapKinds.CertificateExpired, "Certificate", expired.Id,
                $"{expired.CertificateType} {expired.CertificateNumber ?? expired.FileName}", null,
                $"Expired on {expired.ExpiresOn:yyyy-MM-dd}. The lot can still be quarantined or shipped "
                + "under a recorded override, but it is not compliant as it stands."));

        if (lot.OriginDiffersFromOrder)
            gaps.Add(new MaterialTraceGap(MaterialTraceGapKinds.OriginMismatch, "Lot", lot.Id, lot.LotNumber,
                lot.QuantityReceived,
                $"Ordered from {lot.OrderedCountryOfOrigin} but received as {lot.CountryOfOrigin}. "
                + "The clearance file and the certificate of origin must agree with what arrived, not with the order."));

        if (origin is not null
            && origin.DemandSource != SupplierPurchaseOrderDemandSources.Stock
            && !lot.CommercialCaseId.HasValue)
            gaps.Add(new MaterialTraceGap(MaterialTraceGapKinds.UnlinkedCase, "Lot", lot.Id, lot.LotNumber, null,
                $"Purchase order {origin.PurchaseOrderNumber} was raised against customer demand but this lot "
                + "states no commercial case, so it cannot be reached from the case timeline."));

        if (lot.Status == MaterialLotStatuses.Quarantined && fulfilments.Count > 0)
            gaps.Add(new MaterialTraceGap(MaterialTraceGapKinds.ShippedLotQuarantined, "Lot", lot.Id, lot.LotNumber,
                fulfilments.Sum(f => f.Quantity),
                $"This lot is quarantined and has already been declared against {fulfilments.Count} customer "
                + "fulfilment(s). Those customers are inside the recall population."));

        return new LotWhereFromView(
            lot.Id, lot.LotNumber, lot.TrackingMode, lot.Status, lot.ProductId, product?.PartNo,
            product?.ProductName, lot.WarehouseId, warehouseName, lot.QuantityReceived, lot.QuantityConsumed,
            lot.QuantityRemaining, lot.ReceivedOn, lot.GoodsReceiptId, receiptNumber,
            lot.SupplierPurchaseOrderId, origin?.PurchaseOrderNumber, origin?.DemandSource,
            lot.SupplierPurchaseOrderLineId, lot.SupplierId, supplierName, origin?.RfqId, rfqNumber,
            lot.CommercialCaseId, lot.NexoraSerial, lot.CountryOfOrigin, lot.OrderedCountryOfOrigin,
            lot.ManufacturerName, lot.ManufacturerPartNumber, lot.SupplierBatchReference,
            lot.ManufactureDate, lot.ExpiryDate, poLine?.HsCode, lot.QuarantineReasonCode,
            lot.QuarantineReason, lot.QuarantinedOn, lot.QuarantinedBy, lot.ReleasedOn, lot.ReleasedBy,
            lot.ReleaseReason, certificateState, certificates, fulfilments, gaps, lot.Version);
    }

    /// <summary>
    /// FR-MTR-03 where-used: every lot that fulfilled a sales order, and — just as importantly —
    /// every unit that left the building without one.
    ///
    /// <para>The declared lot links are the membership. The shipped quantity per line is
    /// reconciliation evidence only: it never adds a lot to the answer, it decides how much of the
    /// line is <b>untraced</b>. That number is the whole point of the screen — an order showing
    /// "100 shipped, 60 traced" is a recall the business cannot execute, and it has to be visible
    /// rather than inferable.</para>
    /// </summary>
    public async Task<OrderWhereUsedView> GetOrderTraceAsync(
        long businessUnitId, long orderId, CancellationToken ct = default)
    {
        RequireTenant(businessUnitId);
        var today = Today();

        var order = await _db.Orders.AsNoTracking()
            .Where(o => o.BusinessUnitId == businessUnitId && o.Id == orderId)
            .Select(o => new { o.Id, o.OrderNo, o.CommercialCaseId, o.NexoraSerial })
            .SingleOrDefaultAsync(ct)
            ?? throw new MaterialTraceabilityValidationException("Order was not found in this tenant.");

        // OrderItem carries no tenant column — isolation is parent-derived — so lines are reached
        // only through an order already proven to belong to the caller.
        var lines = await _db.Set<OrderItem>().AsNoTracking()
            .Where(i => i.OrderId == orderId && i.IsActive)
            .OrderBy(i => i.Id)
            .Select(i => new
            {
                i.Id, i.ProductId, i.Description, i.Quantity,
                PartNumber = _db.Products.Where(p => p.Buid == businessUnitId && p.Id == i.ProductId)
                    .Select(p => p.PartNo).FirstOrDefault(),
            })
            .ToListAsync(ct);

        var shippedByLine = await _db.ShipmentItems.AsNoTracking()
            .Where(si => si.IsActive && si.Shipment.OrderId == orderId
                         && si.Shipment.BusinessUnitId == businessUnitId && si.Shipment.IsActive)
            .GroupBy(si => si.OrderItemId)
            .Select(g => new { OrderItemId = g.Key, Quantity = g.Sum(x => x.Quantity) })
            .ToDictionaryAsync(x => x.OrderItemId, x => x.Quantity, ct);

        // MEMBERSHIP: what the fulfilment DECLARED. Never a join through the product.
        var declared = await _db.Set<MaterialLotConsumption>().AsNoTracking()
            .Where(c => c.BusinessUnitId == businessUnitId && c.OrderId == orderId)
            .OrderBy(c => c.OrderItemId).ThenBy(c => c.Id)
            .Select(c => new
            {
                c.Id, c.MaterialLotId, c.OrderItemId, c.ShipmentId, c.Quantity,
                c.ComplianceStateAtDeclaration, c.CommercialCaseId,
                ShipmentNumber = _db.Shipments
                    .Where(s => s.BusinessUnitId == businessUnitId && s.Id == c.ShipmentId)
                    .Select(s => s.ShipmentNo).FirstOrDefault(),
            })
            .ToListAsync(ct);

        var lotIds = declared.Select(x => x.MaterialLotId).Distinct().ToArray();
        var lots = await _db.Set<MaterialLot>().AsNoTracking()
            .Where(l => l.BusinessUnitId == businessUnitId && lotIds.Contains(l.Id))
            .Select(l => new
            {
                l.Id, l.LotNumber, l.TrackingMode, l.Status, l.SupplierId, l.SupplierPurchaseOrderId,
                l.CountryOfOrigin, l.ManufacturerName,
                SupplierName = _db.Suppliers.Where(s => s.Buid == businessUnitId && s.Id == l.SupplierId)
                    .Select(s => s.Name).FirstOrDefault(),
                PurchaseOrderNumber = _db.SupplierPurchaseOrders
                    .Where(p => p.BusinessUnitId == businessUnitId && p.Id == l.SupplierPurchaseOrderId)
                    .Select(p => p.PurchaseOrderNumber).FirstOrDefault(),
            })
            .ToDictionaryAsync(x => x.Id, ct);

        var certificatesByLot = (await _db.Set<MaterialLotCertificate>().AsNoTracking()
                .Where(c => c.BusinessUnitId == businessUnitId && lotIds.Contains(c.MaterialLotId))
                .Select(c => new { c.MaterialLotId, c.CertificateType, c.ExpiresOn })
                .ToListAsync(ct))
            .GroupBy(c => c.MaterialLotId)
            .ToDictionary(g => g.Key, g => g.Select(c => (c.CertificateType, c.ExpiresOn)).ToArray());

        var gaps = new List<MaterialTraceGap>();
        var lineViews = new List<OrderLineTraceView>(lines.Count);

        foreach (var line in lines)
        {
            var lineDeclarations = declared.Where(d => d.OrderItemId == line.Id).ToList();
            var declaredQuantity = lineDeclarations.Sum(d => d.Quantity);
            var shipped = shippedByLine.GetValueOrDefault(line.Id, 0m);
            var untraced = Math.Max(0m, shipped - declaredQuantity);

            if (untraced > 0m)
                gaps.Add(new MaterialTraceGap(MaterialTraceGapKinds.UndeclaredFulfilment, "OrderLine", line.Id,
                    line.PartNumber ?? line.Description ?? $"Line {line.Id}", untraced,
                    $"{untraced} unit(s) have shipped against this line with no material lot stated. "
                    + "They cannot be traced to a supplier, a certificate or a recall."));
            if (declaredQuantity > shipped)
                gaps.Add(new MaterialTraceGap(MaterialTraceGapKinds.OverDeclared, "OrderLine", line.Id,
                    line.PartNumber ?? line.Description ?? $"Line {line.Id}", declaredQuantity - shipped,
                    $"{declaredQuantity} unit(s) of lot material are declared against this line but only "
                    + $"{shipped} have shipped. The declaration is ahead of the goods, or a shipment was reversed."));

            var lotViews = new List<OrderLineLotView>(lineDeclarations.Count);
            foreach (var declaration in lineDeclarations)
            {
                if (!lots.TryGetValue(declaration.MaterialLotId, out var lot)) continue;

                var certificateStateNow = CertificateState(
                    certificatesByLot.GetValueOrDefault(lot.Id, []), today);

                if (lot.Status == MaterialLotStatuses.Quarantined)
                    gaps.Add(new MaterialTraceGap(MaterialTraceGapKinds.ShippedLotQuarantined, "Lot", lot.Id,
                        lot.LotNumber, declaration.Quantity,
                        $"Lot {lot.LotNumber} was used on this order and is now quarantined. "
                        + "This customer is inside the recall population."));

                if (declaration.ComplianceStateAtDeclaration == MaterialLotComplianceStates.CertificateExpired)
                    gaps.Add(new MaterialTraceGap(MaterialTraceGapKinds.ShippedOnExpiredCertificate, "Lot", lot.Id,
                        lot.LotNumber, declaration.Quantity,
                        $"Lot {lot.LotNumber} was declared against this order while its certificates had lapsed. "
                        + "The override reason is on the declaration."));

                if (declaration.CommercialCaseId != order.CommercialCaseId)
                    gaps.Add(new MaterialTraceGap(MaterialTraceGapKinds.ConflictingCase, "Consumption",
                        declaration.Id, lot.LotNumber, declaration.Quantity,
                        $"The lot declaration states commercial case "
                        + $"{declaration.CommercialCaseId?.ToString() ?? "none"} while the order states "
                        + $"{order.CommercialCaseId?.ToString() ?? "none"}."));

                lotViews.Add(new OrderLineLotView(
                    declaration.Id, lot.Id, lot.LotNumber, lot.TrackingMode, lot.Status, declaration.Quantity,
                    lot.SupplierPurchaseOrderId, lot.PurchaseOrderNumber, lot.SupplierId, lot.SupplierName,
                    lot.CountryOfOrigin, lot.ManufacturerName, declaration.ShipmentId,
                    declaration.ShipmentNumber, declaration.ComplianceStateAtDeclaration, certificateStateNow));
            }

            lineViews.Add(new OrderLineTraceView(
                line.Id, line.ProductId, line.PartNumber, line.Description, line.Quantity, shipped,
                declaredQuantity, untraced, lotViews));
        }

        return new OrderWhereUsedView(
            order.Id, order.OrderNo, order.CommercialCaseId, order.NexoraSerial, lineViews,
            gaps.OrderBy(g => g.Kind).ThenBy(g => g.SubjectId).ToList());
    }

    // -----------------------------------------------------------------------------------------
    // FR-MTR-05 — quarantine and authorized release
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Holds a lot and makes its remaining stock unallocatable.
    ///
    /// <para>Two things happen, and the second is the one that matters. The lot's status flips —
    /// which is what the fulfilment declaration checks — and the lot's remaining quantity is
    /// reclassified into the inventory row's quarantine bucket through
    /// <see cref="IStockLedgerService"/>, which every available-to-promise computation in the
    /// product already subtracts. A flag on its own would be advisory; the bucket is what makes
    /// <c>ReserveAsync</c> refuse.</para>
    ///
    /// <para>Holds that are already outstanding against that stock are released first, newest
    /// first, because a goods issue consumes an existing hold without re-reading availability — so
    /// a lot reserved before it was recalled would otherwise still ship. The displaced orders come
    /// back in the result rather than being logged, because somebody now has to re-source them.</para>
    /// </summary>
    public Task<LotQuarantineResult> QuarantineAsync(
        QuarantineLotCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        Validate(command.BusinessUnitId, command.Actor, command.CorrelationId, command.IdempotencyKey);
        var reasonCode = (command.ReasonCode ?? string.Empty).Trim().ToUpperInvariant();
        if (!MaterialLotQuarantineReasons.All.Contains(reasonCode))
            throw new MaterialTraceabilityValidationException(
                $"'{command.ReasonCode}' is not a recognised quarantine reason code.");
        var reason = (command.Reason ?? string.Empty).Trim();
        if (reason.Length == 0)
            throw new MaterialTraceabilityValidationException(
                "A quarantine must state why. A hold nobody can explain later is not a control.");
        if (reason.Length > 1000)
            throw new MaterialTraceabilityValidationException("A quarantine reason must be 1000 characters or fewer.");

        return TransactAsync(async () =>
        {
            var lot = await LoadForUpdateAsync(command.BusinessUnitId, command.MaterialLotId, ct);

            if (lot.Status == MaterialLotStatuses.Quarantined)
            {
                // Idempotent replay: the same key re-applied returns the existing hold rather than
                // stacking a second quarantine quantity onto the same units.
                var replayed = await WasRecordedAsync(command.BusinessUnitId, lot.Id,
                    "MATERIAL_LOT_QUARANTINED", command.IdempotencyKey, ct);
                if (!replayed)
                    throw new MaterialTraceabilityConflictException(
                        "This lot is already quarantined. Release it before quarantining it again.");
                return await SnapshotAsync(command.BusinessUnitId, lot, [], ct, replayed: true);
            }

            if (lot.Version != command.ExpectedVersion)
                throw new MaterialTraceabilityConflictException(
                    "The lot changed; refresh before quarantining it.");

            var held = lot.QuantityRemaining;
            var displaced = new List<DisplacedReservation>();

            if (held > 0m)
            {
                // GATE 6. Release the holds that name THIS lot first. Gate 5 could only free stock
                // by quantity — newest hold first on the inventory row — because a hold named no
                // lot, so quarantining lot A could displace an order that was holding lot B while
                // leaving the order actually holding A untouched. Now that holds name lots, the
                // orders that must be re-sourced are exactly the ones holding the recalled
                // material, and every other customer's promise survives the recall.
                var onLot = await _availability.ReleaseHoldsOnLotAsync(
                    command.BusinessUnitId, lot.Id, command.Actor.Trim(), ct);
                displaced.AddRange(onLot.Select(r =>
                    new DisplacedReservation(r.Id, r.OrderId, r.OrderItemId, r.Quantity)));

                // The bucket cannot exceed what is physically present, and existing holds count
                // against the same total. Free exactly enough, then reclassify.
                //
                // The quantity-based sweep still runs, and still has to: the un-lotted balance
                // (opening stock, adjustments, transfers) is held by reservations that name no lot
                // at all, so no lot-scoped release can reach it. Where every hold on the row names
                // a lot, the loop above has already freed the shortfall and this finds nothing —
                // which is the correct outcome, not a redundant call.
                var before = await _availability.GetAvailabilityAsync(
                    command.BusinessUnitId, lot.InventoryId, ct);
                var headroom = Math.Max(0m, before.OnHand - before.Reserved - before.Allocated
                    - before.Quarantine - before.Damaged - before.Expired);
                var shortfall = held - headroom;
                if (shortfall > 0m)
                {
                    var released = await _availability.ReleaseForQuarantineAsync(
                        command.BusinessUnitId, lot.InventoryId, shortfall, command.Actor.Trim(), ct);
                    displaced.AddRange(released.Select(r =>
                        new DisplacedReservation(r.Id, r.OrderId, r.OrderItemId, r.Quantity)));
                }

                await _stockLedger.ReclassifyAsync(
                    command.BusinessUnitId, lot.ProductId, lot.WarehouseId, StockBucket.Quarantine,
                    held, QuarantineLedgerKey(lot.Id, lot.Version), command.Actor.Trim(),
                    $"Lot {lot.LotNumber} quarantined: {reasonCode}", ct);
            }

            var now = DateTime.UtcNow;
            lot.Status = MaterialLotStatuses.Quarantined;
            lot.QuarantineReasonCode = reasonCode;
            lot.QuarantineReason = reason;
            lot.QuarantinedOn = now;
            lot.QuarantinedBy = command.Actor.Trim();
            // The previous release stays readable as history; only the live state moves.
            lot.ReleasedOn = null;
            lot.ReleasedBy = null;
            lot.ReleaseReason = null;
            lot.Version++;
            lot.ModifiedOn = now;
            lot.ModifiedBy = command.Actor.Trim();

            AddEvent(command, lot, "MATERIAL_LOT_QUARANTINED", new
            {
                lot.LotNumber, reasonCode, reason, quantity = held,
                displacedReservations = displaced,
            }, now);

            await _db.SaveChangesAsync(ct);
            return await SnapshotAsync(command.BusinessUnitId, lot, displaced, ct);
        }, ct);
    }

    /// <summary>
    /// FR-MTR-05's "authorized release". Attributable, reasoned and audited — the release is as
    /// governed as the hold, because a hold that anyone can silently undo is a suggestion.
    /// </summary>
    public Task<LotQuarantineResult> ReleaseAsync(ReleaseLotCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        Validate(command.BusinessUnitId, command.Actor, command.CorrelationId, command.IdempotencyKey);
        var reason = (command.Reason ?? string.Empty).Trim();
        if (reason.Length == 0)
            throw new MaterialTraceabilityValidationException(
                "A release must state the authority for it. FR-MTR-05 requires an authorized release, "
                + "and an unexplained one is not authorization.");
        if (reason.Length > 1000)
            throw new MaterialTraceabilityValidationException("A release reason must be 1000 characters or fewer.");

        return TransactAsync(async () =>
        {
            var lot = await LoadForUpdateAsync(command.BusinessUnitId, command.MaterialLotId, ct);

            if (lot.Status == MaterialLotStatuses.Available)
            {
                var replayed = await WasRecordedAsync(command.BusinessUnitId, lot.Id,
                    "MATERIAL_LOT_RELEASED", command.IdempotencyKey, ct);
                if (!replayed)
                    throw new MaterialTraceabilityConflictException("This lot is not quarantined.");
                return await SnapshotAsync(command.BusinessUnitId, lot, [], ct, replayed: true);
            }

            if (lot.Version != command.ExpectedVersion)
                throw new MaterialTraceabilityConflictException("The lot changed; refresh before releasing it.");

            var held = lot.QuantityRemaining;
            if (held > 0m)
                await _stockLedger.ReclassifyAsync(
                    command.BusinessUnitId, lot.ProductId, lot.WarehouseId, StockBucket.Quarantine,
                    -held, ReleaseLedgerKey(lot.Id, lot.Version), command.Actor.Trim(),
                    $"Lot {lot.LotNumber} released from quarantine", ct);

            var now = DateTime.UtcNow;
            lot.Status = MaterialLotStatuses.Available;
            lot.ReleasedOn = now;
            lot.ReleasedBy = command.Actor.Trim();
            lot.ReleaseReason = reason;
            lot.Version++;
            lot.ModifiedOn = now;
            lot.ModifiedBy = command.Actor.Trim();

            AddEvent(command, lot, "MATERIAL_LOT_RELEASED", new
            {
                lot.LotNumber, reason, quantity = held,
                heldSince = lot.QuarantinedOn, heldBy = lot.QuarantinedBy, lot.QuarantineReasonCode,
            }, now);

            await _db.SaveChangesAsync(ct);
            return await SnapshotAsync(command.BusinessUnitId, lot, [], ct);
        }, ct);
    }

    // -----------------------------------------------------------------------------------------
    // FR-MTR-01 forward link — and the second quarantine enforcement point
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// States that this lot fulfilled this order line, and (once despatched) went out on this
    /// delivery note. This is the row where-used trace reads.
    ///
    /// <para><b>A quarantined lot is refused here outright.</b> Reducing available-to-promise stops
    /// the next reservation; this stops the material physically leaving under a picker's hand. The
    /// two together are what "block allocation until authorized release" means in a warehouse.</para>
    ///
    /// <para><b>An expired certificate is refused unless someone signs for it.</b> The rationale is
    /// in <see cref="MaterialLotComplianceStates"/>: a recall is a quality decision that only a
    /// quality release can reverse, whereas lapsed paperwork is a documented risk a despatch
    /// supervisor may legitimately accept — provided their name and reason are on the record. A
    /// missing certificate is reported, not blocked: nothing in this product knows which certificate
    /// types a given material requires, and inventing that matrix would either block legitimate
    /// despatches or, configured leniently, block nothing at all.</para>
    /// </summary>
    public Task<LotConsumptionResult> DeclareConsumptionAsync(
        DeclareLotConsumptionCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        Validate(command.BusinessUnitId, command.Actor, command.CorrelationId, command.IdempotencyKey);
        if (command.Quantity <= 0m)
            throw new MaterialTraceabilityValidationException("A lot declaration must state a positive quantity.");

        return TransactAsync(async () =>
        {
            var key = command.IdempotencyKey.Trim();
            var replay = await _db.Set<MaterialLotConsumption>()
                .SingleOrDefaultAsync(x => x.BusinessUnitId == command.BusinessUnitId
                                           && x.IdempotencyKey == key, ct);
            if (replay is not null)
            {
                if (replay.MaterialLotId != command.MaterialLotId || replay.OrderItemId != command.OrderItemId
                    || replay.Quantity != command.Quantity)
                    throw new MaterialTraceabilityConflictException(
                        "The idempotency key was already used for a different lot declaration.");
                var replayLot = await _db.Set<MaterialLot>().AsNoTracking()
                    .SingleAsync(x => x.BusinessUnitId == command.BusinessUnitId
                                      && x.Id == replay.MaterialLotId, ct);
                return new LotConsumptionResult(replay.Id, replayLot.Id, replayLot.LotNumber, replay.OrderId,
                    replay.OrderItemId, replay.ShipmentId, replay.Quantity, replayLot.QuantityRemaining,
                    replay.ComplianceStateAtDeclaration, true);
            }

            var lot = await LoadForUpdateAsync(command.BusinessUnitId, command.MaterialLotId, ct);

            // ENFORCEMENT POINT 2. Availability already refuses to promise quarantined stock; this
            // is what stops material that was reserved BEFORE the hold from being declared onto a
            // despatch afterwards.
            if (lot.Status == MaterialLotStatuses.Quarantined)
                throw new MaterialTraceabilityConflictException(
                    $"Lot {lot.LotNumber} is quarantined ({lot.QuarantineReasonCode}) and cannot be used to "
                    + "fulfil an order until it is released by an authorized user.");

            if (command.Quantity > lot.QuantityRemaining)
                throw new MaterialTraceabilityValidationException(
                    $"Lot {lot.LotNumber} has {lot.QuantityRemaining} unit(s) remaining; "
                    + $"{command.Quantity} were declared.");

            var order = await _db.Orders.AsNoTracking()
                .Where(o => o.BusinessUnitId == command.BusinessUnitId && o.Id == command.OrderId)
                .Select(o => new { o.Id, o.CommercialCaseId, o.NexoraSerial })
                .SingleOrDefaultAsync(ct)
                ?? throw new MaterialTraceabilityValidationException("Order was not found in this tenant.");

            // OrderItem ids are global and the table carries no tenant column, so the line is
            // proved to belong to THIS order rather than merely to exist.
            var lineExists = await _db.Set<OrderItem>().AsNoTracking()
                .AnyAsync(i => i.Id == command.OrderItemId && i.OrderId == command.OrderId, ct);
            if (!lineExists)
                throw new MaterialTraceabilityValidationException(
                    $"Order line {command.OrderItemId} does not belong to order {command.OrderId}.");

            if (command.ShipmentId is { } shipmentId)
            {
                var shipmentBelongs = await _db.Shipments.AsNoTracking()
                    .AnyAsync(s => s.BusinessUnitId == command.BusinessUnitId && s.Id == shipmentId
                                   && s.OrderId == command.OrderId, ct);
                if (!shipmentBelongs)
                    throw new MaterialTraceabilityValidationException(
                        $"Delivery note {shipmentId} does not belong to order {command.OrderId}.");
            }

            var today = Today();
            var certificates = await _db.Set<MaterialLotCertificate>().AsNoTracking()
                .Where(c => c.BusinessUnitId == command.BusinessUnitId && c.MaterialLotId == lot.Id)
                .Select(c => new { c.CertificateType, c.ExpiresOn })
                .ToListAsync(ct);
            var state = ComplianceState(
                certificates.Select(c => (c.CertificateType, c.ExpiresOn)), today);

            var override_ = (command.ComplianceOverrideReason ?? string.Empty).Trim();
            if (state == MaterialLotComplianceStates.CertificateExpired && override_.Length == 0)
                throw new MaterialTraceabilityConflictException(
                    $"Lot {lot.LotNumber} has an expired certificate. Upload the renewed document, or record "
                    + "a reason for despatching against the lapsed one — that reason is kept on the record.");
            if (state != MaterialLotComplianceStates.CertificateExpired && override_.Length > 0)
                throw new MaterialTraceabilityValidationException(
                    "A compliance override was supplied for a lot whose certificates are in date. "
                    + "An override that is always present stops meaning anything.");
            if (override_.Length > 1000)
                throw new MaterialTraceabilityValidationException(
                    "A compliance override reason must be 1000 characters or fewer.");

            var now = DateTime.UtcNow;
            var consumption = new MaterialLotConsumption
            {
                BusinessUnitId = command.BusinessUnitId,
                MaterialLotId = lot.Id,
                OrderId = command.OrderId,
                OrderItemId = command.OrderItemId,
                ShipmentId = command.ShipmentId,
                CommercialCaseId = order.CommercialCaseId,
                NexoraSerial = order.NexoraSerial,
                Quantity = command.Quantity,
                ComplianceStateAtDeclaration = state,
                ComplianceOverrideReason = override_.Length == 0 ? null : override_,
                ComplianceOverrideBy = override_.Length == 0 ? null : command.Actor.Trim(),
                IdempotencyKey = key,
                DeclaredOn = now,
                DeclaredBy = command.Actor.Trim(),
            };
            _db.Set<MaterialLotConsumption>().Add(consumption);

            lot.QuantityConsumed += command.Quantity;
            lot.Version++;
            lot.ModifiedOn = now;
            lot.ModifiedBy = command.Actor.Trim();

            AddEvent(command.BusinessUnitId, command.Actor, command.CorrelationId, command.IdempotencyKey,
                lot, "MATERIAL_LOT_CONSUMED", new
                {
                    lot.LotNumber, command.OrderId, command.OrderItemId, command.ShipmentId,
                    command.Quantity, complianceState = state,
                    overrideReason = consumption.ComplianceOverrideReason,
                }, now);

            await _db.SaveChangesAsync(ct);
            return new LotConsumptionResult(consumption.Id, lot.Id, lot.LotNumber, consumption.OrderId,
                consumption.OrderItemId, consumption.ShipmentId, consumption.Quantity,
                lot.QuantityRemaining, state, false);
        }, ct);
    }

    // -----------------------------------------------------------------------------------------
    // Shared
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// The lot's overall certificate position. Per TYPE, because a lot with a valid certificate of
    /// origin and a lapsed certificate of conformity is not compliant — taking the best of the two
    /// would hide the one that matters. Renewal therefore needs no workflow: uploading a fresh
    /// certificate of the same type moves that type's latest expiry forward.
    /// </summary>
    internal static string CertificateState(
        IEnumerable<(string CertificateType, DateOnly? ExpiresOn)> certificates, DateOnly today)
    {
        var byType = certificates
            .GroupBy(c => c.CertificateType, StringComparer.Ordinal)
            .Select(g => g.Select(c => c.ExpiresOn).ToArray())
            .ToArray();
        if (byType.Length == 0) return CertificateExpiryStates.NotApplicable;

        var states = byType.Select(expiries =>
        {
            var dated = expiries.Where(x => x.HasValue).Select(x => x!.Value).ToArray();
            if (dated.Length == 0) return CertificateExpiryStates.NotApplicable;
            var latest = dated.Max();
            if (latest < today) return CertificateExpiryStates.Expired;
            return latest.DayNumber - today.DayNumber <= MaterialLotCertificate.ExpiringSoonWindowDays
                ? CertificateExpiryStates.ExpiringSoon
                : CertificateExpiryStates.Valid;
        }).ToArray();

        if (states.Contains(CertificateExpiryStates.Expired)) return CertificateExpiryStates.Expired;
        if (states.Contains(CertificateExpiryStates.ExpiringSoon)) return CertificateExpiryStates.ExpiringSoon;
        return states.Contains(CertificateExpiryStates.Valid)
            ? CertificateExpiryStates.Valid
            : CertificateExpiryStates.NotApplicable;
    }

    internal static string ComplianceState(
        IEnumerable<(string CertificateType, DateOnly? ExpiresOn)> certificates, DateOnly today)
    {
        var materialised = certificates.ToArray();
        if (materialised.Length == 0) return MaterialLotComplianceStates.NoCertificate;
        return CertificateState(materialised, today) == CertificateExpiryStates.Expired
            ? MaterialLotComplianceStates.CertificateExpired
            : MaterialLotComplianceStates.Compliant;
    }

    private async Task<IReadOnlyList<LotCertificateView>> ReadCertificatesAsync(
        long businessUnitId, long[] lotIds, DateOnly today, CancellationToken ct)
    {
        var rows = await _db.Set<MaterialLotCertificate>().AsNoTracking()
            .Where(c => c.BusinessUnitId == businessUnitId && lotIds.Contains(c.MaterialLotId))
            .OrderBy(c => c.CertificateType).ThenByDescending(c => c.ExpiresOn).ThenBy(c => c.Id)
            .ToListAsync(ct);

        return rows.Select(c => new LotCertificateView(
            c.Id, c.MaterialLotId, c.CertificateType, c.CertificateNumber, c.IssuedBy, c.IssuedOn,
            c.ExpiresOn, c.ExpiryState(today),
            c.ExpiresOn.HasValue ? c.ExpiresOn.Value.DayNumber - today.DayNumber : null,
            c.AttachmentId, c.FileName, c.ContentSha256, c.Notes, c.UploadedOn, c.UploadedBy)).ToList();
    }

    private async Task<IReadOnlyList<LotFulfilmentView>> ReadFulfilmentsAsync(
        long businessUnitId, long materialLotId, CancellationToken ct)
        => await _db.Set<MaterialLotConsumption>().AsNoTracking()
            .Where(c => c.BusinessUnitId == businessUnitId && c.MaterialLotId == materialLotId)
            .OrderByDescending(c => c.DeclaredOn).ThenBy(c => c.Id)
            .Select(c => new LotFulfilmentView(
                c.Id, c.OrderId,
                _db.Orders.Where(o => o.BusinessUnitId == businessUnitId && o.Id == c.OrderId)
                    .Select(o => o.OrderNo).FirstOrDefault(),
                c.OrderItemId, c.ShipmentId,
                _db.Shipments.Where(s => s.BusinessUnitId == businessUnitId && s.Id == c.ShipmentId)
                    .Select(s => s.ShipmentNo).FirstOrDefault(),
                c.CommercialCaseId, c.NexoraSerial, c.Quantity, c.ComplianceStateAtDeclaration,
                c.ComplianceOverrideReason, c.ComplianceOverrideBy, c.DeclaredOn, c.DeclaredBy))
            .ToListAsync(ct);

    private async Task<MaterialLot> LoadForUpdateAsync(long businessUnitId, long lotId, CancellationToken ct)
    {
        if (_db.Database.IsNpgsql())
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({$"material-lot:{businessUnitId}:{lotId}"}, 0))", ct);
        return await _db.Set<MaterialLot>()
            .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == lotId, ct)
            ?? throw new MaterialTraceabilityValidationException("Material lot was not found in this tenant.");
    }

    private async Task<LotQuarantineResult> SnapshotAsync(
        long businessUnitId, MaterialLot lot, IReadOnlyList<DisplacedReservation> displaced,
        CancellationToken ct, bool replayed = false)
    {
        var stock = await _availability.GetAvailabilityAsync(businessUnitId, lot.InventoryId, ct);
        return new LotQuarantineResult(lot.Id, lot.LotNumber, lot.Status,
            lot.Status == MaterialLotStatuses.Quarantined ? lot.QuantityRemaining : 0m,
            stock.Quarantine, stock.Available, displaced, lot.Version, replayed);
    }

    private async Task<bool> WasRecordedAsync(
        long businessUnitId, long lotId, string eventType, string idempotencyKey, CancellationToken ct)
        => await _db.ProcurementEvents.AsNoTracking().AnyAsync(x =>
            x.BusinessUnitId == businessUnitId && x.AggregateType == MaterialLotAggregate
            && x.AggregateId == lotId && x.EventType == eventType
            && x.IdempotencyKey == idempotencyKey.Trim(), ct);

    private const string MaterialLotAggregate = "MaterialLot";

    private void AddEvent(
        QuarantineLotCommand command, MaterialLot lot, string eventType, object payload, DateTime occurredOn)
        => AddEvent(command.BusinessUnitId, command.Actor, command.CorrelationId, command.IdempotencyKey,
            lot, eventType, payload, occurredOn);

    private void AddEvent(
        ReleaseLotCommand command, MaterialLot lot, string eventType, object payload, DateTime occurredOn)
        => AddEvent(command.BusinessUnitId, command.Actor, command.CorrelationId, command.IdempotencyKey,
            lot, eventType, payload, occurredOn);

    /// <summary>
    /// Audit lands in <c>procurement_events</c> — the existing append-only, tenant-scoped,
    /// UPDATE/DELETE-revoked ledger. A fourth table would have needed its own policy, its own grant
    /// and its own immutability trigger to earn the same guarantees this one already has.
    /// </summary>
    private void AddEvent(
        long businessUnitId, string actor, string correlationId, string idempotencyKey,
        MaterialLot lot, string eventType, object payload, DateTime occurredOn)
        => _db.ProcurementEvents.Add(new ProcurementEvent
        {
            BusinessUnitId = businessUnitId,
            AggregateType = MaterialLotAggregate,
            AggregateId = lot.Id,
            AggregateVersion = lot.Version,
            EventType = eventType,
            Actor = actor.Trim(),
            CorrelationId = correlationId.Trim(),
            IdempotencyKey = idempotencyKey.Trim(),
            PayloadJson = JsonSerializer.Serialize(payload),
            OccurredOn = occurredOn,
        });

    /// <summary>
    /// Deterministic per (lot, version) so a retried quarantine reuses the ledger key and the stock
    /// service short-circuits it, rather than reclassifying the same units twice.
    /// </summary>
    private static string QuarantineLedgerKey(long lotId, long version) => $"material-lot-quarantine:{lotId}:{version}";

    private static string ReleaseLedgerKey(long lotId, long version) => $"material-lot-release:{lotId}:{version}";

    private static DateOnly Today() => DateOnly.FromDateTime(DateTime.UtcNow);

    private static void RequireTenant(long businessUnitId)
    {
        if (businessUnitId <= 0)
            throw new UnauthorizedAccessException("A valid authenticated tenant is required.");
    }

    private static void Validate(long businessUnitId, string actor, string correlationId, string idempotencyKey)
    {
        RequireTenant(businessUnitId);
        if (string.IsNullOrWhiteSpace(actor) || actor.Trim().Length > 255)
            throw new MaterialTraceabilityValidationException("An actor of at most 255 characters is required.");
        if (string.IsNullOrWhiteSpace(correlationId) || correlationId.Trim().Length > 160)
            throw new MaterialTraceabilityValidationException("A correlation id of at most 160 characters is required.");
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Trim().Length > 160)
            throw new MaterialTraceabilityValidationException("An idempotency key of at most 160 characters is required.");
    }

    private async Task<T> TransactAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        if (_db.Database.CurrentTransaction is not null) return await action();

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            var isolation = _db.Database.IsNpgsql() ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable;
            await using var transaction = await _db.Database.BeginTransactionAsync(isolation, ct);
            var result = await action();
            await transaction.CommitAsync(ct);
            return result;
        });
    }
}
