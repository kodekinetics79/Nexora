using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Traceability;

/// <summary>
/// FR-MTR-01. Turns one goods receipt into the lots that explain it.
///
/// <para><b>Every received unit lands in a lot. Always.</b> There is no "untracked" escape hatch
/// that produces no row, because that would be stock with no supplier purchase order behind it and
/// no certificate to hang off — which is precisely the material a customs officer or a recall
/// notice asks about. What varies is who supplies the identifier, not whether one exists:</para>
/// <list type="bullet">
///   <item>SERIAL — the operator lists one serial per unit; each becomes a lot of quantity 1.</item>
///   <item>LOT — the operator gives the supplier's batch number; one lot for the line.</item>
///   <item>UNTRACKED — nobody is asked anything; the lot number is derived from the receipt.</item>
/// </list>
///
/// <para>The mode comes from the item master's existing <c>SerialTracking</c>/<c>BatchTracking</c>
/// switches, which until now had no reader anywhere in the product.</para>
/// </summary>
public sealed class MaterialLotRecorder(ErpRfqAutomationContext db) : IMaterialLotRecorder
{
    private readonly ErpRfqAutomationContext _db = db;

    public async Task<IReadOnlyList<MaterialLot>> RecordReceiptLotsAsync(
        ReceiptLotContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.BusinessUnitId <= 0)
            throw new MaterialTraceabilityValidationException("A valid tenant is required to record a lot.");
        if (context.Lines.Count == 0) return [];

        var productIds = context.Lines.Select(x => x.ProductId).Distinct().ToArray();
        // Read under the tenant predicate. The product ids arrive from the caller's own purchase
        // order, but re-proving ownership here costs one query and removes any dependence on the
        // caller having done it.
        var products = await _db.Products.AsNoTracking()
            .Where(x => x.Buid == context.BusinessUnitId && productIds.Contains(x.Id))
            .Select(x => new { x.Id, x.SerialTracking, x.BatchTracking, x.CountryOfOrigin })
            .ToDictionaryAsync(x => x.Id, cancellationToken);

        // The RFQ line is the only place a manufacturer name is captured today, so it is the
        // honest default for FR-MTR-04's "manufacturer per line". Product carries no manufacturer
        // column, and inventing one on the catalogue would be a master-data change this gate has
        // no mandate for.
        var rfqItemIds = context.Lines.Where(x => x.RfqItemId.HasValue)
            .Select(x => x.RfqItemId!.Value).Distinct().ToArray();
        var rfqManufacturers = rfqItemIds.Length == 0
            ? new Dictionary<long, (string? Name, string? PartNumber)>()
            : await _db.Rfqitems.AsNoTracking()
                .Where(x => rfqItemIds.Contains(x.Id))
                .Select(x => new { x.Id, x.ManufacturerName, x.ManufacturerPartNumber })
                .ToDictionaryAsync(x => x.Id, x => (Name: x.ManufacturerName, PartNumber: x.ManufacturerPartNumber), cancellationToken);

        var now = DateTime.UtcNow;
        var actor = context.Actor.Trim();
        var created = new List<MaterialLot>();

        foreach (var line in context.Lines.OrderBy(x => x.PurchaseOrderLineId))
        {
            if (line.Quantity <= 0m)
                throw new MaterialTraceabilityValidationException(
                    $"Receipt line {line.PurchaseOrderLineId} must receive a positive quantity.");
            if (!products.TryGetValue(line.ProductId, out var product))
                throw new MaterialTraceabilityValidationException(
                    $"Product {line.ProductId} was not found in this tenant.");

            var mode = MaterialLotTrackingModes.FromProduct(product.SerialTracking, product.BatchTracking);
            var declaration = line.Declaration ?? new ReceiptLotDeclaration();
            rfqManufacturers.TryGetValue(line.RfqItemId ?? 0, out var manufacturer);

            foreach (var (lotNumber, quantity) in ResolveLotNumbers(
                         mode, declaration, line, context.ReceiptNumber))
            {
                var lot = new MaterialLot
                {
                    BusinessUnitId = context.BusinessUnitId,
                    LotNumber = lotNumber,
                    TrackingMode = mode,
                    ProductId = line.ProductId,
                    InventoryId = line.InventoryId,
                    WarehouseId = line.WarehouseId,
                    SupplierPurchaseOrderId = context.SupplierPurchaseOrderId,
                    SupplierPurchaseOrderLineId = line.PurchaseOrderLineId,
                    GoodsReceiptId = context.GoodsReceiptId,
                    SupplierId = context.SupplierId,
                    CommercialCaseId = context.CommercialCaseId,
                    NexoraSerial = context.NexoraSerial,
                    // FR-MTR-04 reconciliation. Ordered origin is the purchase-order line's, and is
                    // snapshotted so a later amendment cannot retro-fit agreement. Received origin
                    // defaults to it — an operator who says nothing is agreeing with the order —
                    // and falls back to the item master only when the order line itself is silent.
                    OrderedCountryOfOrigin = Trim(line.OrderedCountryOfOrigin, 100),
                    CountryOfOrigin = Trim(declaration.CountryOfOrigin, 100)
                        ?? Trim(line.OrderedCountryOfOrigin, 100)
                        ?? Trim(product.CountryOfOrigin, 100),
                    ManufacturerName = Trim(declaration.ManufacturerName, 255) ?? Trim(manufacturer.Name, 255),
                    ManufacturerPartNumber = Trim(declaration.ManufacturerPartNumber, 120)
                        ?? Trim(manufacturer.PartNumber, 120),
                    SupplierBatchReference = Trim(declaration.SupplierBatchReference, 120),
                    ManufactureDate = declaration.ManufactureDate,
                    ExpiryDate = declaration.ExpiryDate,
                    QuantityReceived = quantity,
                    QuantityConsumed = 0m,
                    Status = MaterialLotStatuses.Available,
                    ReceivedOn = context.ReceivedOn,
                    CreatedOn = now,
                    CreatedBy = actor,
                };

                if (lot.ManufactureDate.HasValue && lot.ExpiryDate.HasValue
                    && lot.ExpiryDate.Value < lot.ManufactureDate.Value)
                    throw new MaterialTraceabilityValidationException(
                        $"Lot {lotNumber} cannot expire before it was manufactured.");

                _db.Set<MaterialLot>().Add(lot);
                created.Add(lot);
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
        return created;
    }

    /// <summary>
    /// Splits one receipt line into (lot number, quantity) pairs according to the tracking mode.
    ///
    /// <para>The serial branch is the only one that can produce more than one lot, and the
    /// arithmetic is checked rather than trusted: a serial list that does not match the received
    /// count would otherwise silently create stock the lot rows do not account for.</para>
    /// </summary>
    private static IEnumerable<(string LotNumber, decimal Quantity)> ResolveLotNumbers(
        string mode, ReceiptLotDeclaration declaration, ReceiptLotLine line, string receiptNumber)
    {
        switch (mode)
        {
            case MaterialLotTrackingModes.Serial:
            {
                var serials = (declaration.SerialNumbers ?? [])
                    .Select(x => x?.Trim())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x!)
                    .ToArray();
                if (serials.Length == 0)
                    throw new MaterialTraceabilityValidationException(
                        $"Purchase order line {line.PurchaseOrderLineId} is serial-tracked; "
                        + "one serial number per received unit is required.");
                if (serials.Distinct(StringComparer.OrdinalIgnoreCase).Count() != serials.Length)
                    throw new MaterialTraceabilityValidationException(
                        $"Purchase order line {line.PurchaseOrderLineId} declares a duplicate serial number.");
                if (line.Quantity != serials.Length)
                    throw new MaterialTraceabilityValidationException(
                        $"Purchase order line {line.PurchaseOrderLineId} received {line.Quantity} unit(s) "
                        + $"but declared {serials.Length} serial number(s).");
                foreach (var serial in serials)
                {
                    if (serial.Length > 80)
                        throw new MaterialTraceabilityValidationException(
                            "A serial number must be 80 characters or fewer.");
                    yield return (serial, 1m);
                }
                yield break;
            }

            case MaterialLotTrackingModes.Lot:
            {
                var lotNumber = declaration.LotNumber?.Trim();
                if (string.IsNullOrWhiteSpace(lotNumber))
                    throw new MaterialTraceabilityValidationException(
                        $"Purchase order line {line.PurchaseOrderLineId} is batch-tracked; "
                        + "the supplier's lot or batch number is required.");
                if (lotNumber.Length > 80)
                    throw new MaterialTraceabilityValidationException(
                        "A lot number must be 80 characters or fewer.");
                yield return (lotNumber, line.Quantity);
                yield break;
            }

            default:
            {
                // Untracked material. The operator is asked for nothing; a receipt-derived
                // identifier keeps the lot addressable without pretending it carries a supplier
                // batch number it never had. An operator-supplied number is still honoured — a
                // warehouse that happens to know the batch should not be prevented from recording it.
                var supplied = declaration.LotNumber?.Trim();
                var lotNumber = string.IsNullOrWhiteSpace(supplied)
                    ? $"{receiptNumber.Trim().ToUpperInvariant()}-L{line.PurchaseOrderLineId}"
                    : supplied;
                if (lotNumber.Length > 80) lotNumber = lotNumber[..80];
                yield return (lotNumber, line.Quantity);
                yield break;
            }
        }
    }

    private static string? Trim(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}
