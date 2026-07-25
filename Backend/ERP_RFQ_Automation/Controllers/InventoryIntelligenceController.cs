using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Inventory;
using ERP_RFQ_Automation.Inventory.Commercial;
using ERP_RFQ_Automation.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Controllers;

[ApiController]
[Authorize]
[Route("api/inventory-intelligence")]
public sealed class InventoryIntelligenceController(
    ErpRfqAutomationContext db,
    ICommercialLineResolutionApplicationService lineResolution) : ControllerBase
{
    [HttpPost("leads/{leadId:long}/resolve")]
    [RequireModulePermission("Leads", PermissionAction.Edit)]
    public async Task<ActionResult> ResolveLead(long leadId, [FromQuery] int limit = 10, CancellationToken ct = default)
        => Ok((await lineResolution.ResolveLeadAsync(TenantId(), leadId, limit, ct)).Select(ResolutionRow));

    [HttpGet("leads/{leadId:long}/resolutions")]
    [RequireModulePermission("Leads", PermissionAction.View)]
    public async Task<ActionResult> LeadResolutions(long leadId, CancellationToken ct)
        => Ok((await db.Set<LeadLineCommercialResolution>().AsNoTracking()
            .Where(x => x.BusinessUnitId == TenantId() && x.LeadId == leadId)
            .OrderBy(x => x.LeadLineId).ToListAsync(ct)).Select(ResolutionRow));

    [HttpGet("rfqs/{rfqId:long}/resolutions")]
    [RequireModulePermission("RFQ", PermissionAction.View)]
    public async Task<ActionResult> RfqResolutions(long rfqId, CancellationToken ct)
        => Ok((await db.Set<LeadLineCommercialResolution>().AsNoTracking()
            .Where(x => x.BusinessUnitId == TenantId() && x.RfqId == rfqId)
            .OrderBy(x => x.LeadLineId).ToListAsync(ct)).Select(ResolutionRow));

    [HttpGet("quotes/{quoteId:long}/resolutions")]
    [RequireModulePermission("Quotation", PermissionAction.View)]
    public async Task<ActionResult> QuoteResolutions(long quoteId, CancellationToken ct)
    {
        var tenant = TenantId();
        var rfqId = await db.Quotes.AsNoTracking().Where(x => x.BusinessUnitId == tenant && x.Id == quoteId)
            .Select(x => x.Rfqid).SingleOrDefaultAsync(ct);
        if (!rfqId.HasValue) return NotFound();
        return Ok((await db.Set<LeadLineCommercialResolution>().AsNoTracking()
            .Where(x => x.BusinessUnitId == tenant && x.RfqId == rfqId.Value)
            .OrderBy(x => x.LeadLineId).ToListAsync(ct)).Select(ResolutionRow));
    }

    [HttpGet("overview")]
    [RequireModulePermission("Products", PermissionAction.View)]
    public async Task<ActionResult> Overview(CancellationToken ct)
    {
        var rows = await AvailabilityRows(null, null, ct);
        var exceptions = rows.Where(x => x.Available <= x.ReorderPoint).Select(x => new {
            id = $"inventory-{x.InventoryId}", productId = (long?)x.ProductId, x.PartNumber, x.ProductName,
            x.WarehouseName, exceptionType = x.Available <= 0 ? "OutOfStock" : "BelowReorderPoint",
            availableQuantity = x.Available, requiredQuantity = (decimal?)x.ReorderPoint, dueAt = (DateTime?)null
        }).ToArray();
        return Ok(new { generatedAt = DateTime.UtcNow, metrics = new[] {
            Metric("sku-count", "Stocked products", rows.Select(x => x.ProductId).Distinct().Count()),
            Metric("on-hand", "On hand", rows.Sum(x => x.OnHand)),
            Metric("available", "Available to promise", rows.Sum(x => x.Available)),
            Metric("exceptions", "Stock exceptions", exceptions.Length)
        }, exceptions });
    }

    [HttpGet("availability")]
    [RequireModulePermission("Products", PermissionAction.View)]
    public async Task<ActionResult> Availability([FromQuery] string? search, [FromQuery] long? warehouseId, CancellationToken ct)
        => Ok((await AvailabilityRows(search, warehouseId, ct)).Select(x => new { x.ProductId, x.PartNumber, x.ProductName,
            x.WarehouseId, x.WarehouseName, x.OnHand, x.Reserved, x.Available, x.Incoming,
            reorderPoint = (decimal?)x.ReorderPoint, x.LeadTimeDays }));

    [HttpGet("warehouses")]
    [RequireModulePermission("Products", PermissionAction.View)]
    public async Task<ActionResult> Warehouses(CancellationToken ct)
    {
        var tenant = TenantId();
        var warehouses = await db.Set<Warehouse>().AsNoTracking().Where(x => x.BusinessUnitId == tenant).OrderBy(x => x.WarehouseName).ToListAsync(ct);
        var availability = await AvailabilityRows(null, null, ct);
        return Ok(warehouses.Select(x => { var rows = availability.Where(a => a.WarehouseId == x.Id).ToArray(); return new {
            warehouseId = x.Id, code = x.WarehouseCode, name = x.WarehouseName, x.Location, active = x.IsActive != false,
            skuCount = rows.Select(r => r.ProductId).Distinct().Count(), onHandUnits = rows.Sum(r => r.OnHand),
            reservedUnits = rows.Sum(r => r.Reserved), availableUnits = rows.Sum(r => r.Available),
            exceptionCount = rows.Count(r => r.Available <= r.ReorderPoint) }; }));
    }

    [HttpGet("reservations")]
    [RequireModulePermission("Products", PermissionAction.View)]
    public async Task<ActionResult> Reservations([FromQuery] string? status, CancellationToken ct)
    {
        var tenant = TenantId();
        var query = from reservation in db.Set<StockReservation>().AsNoTracking()
            join inventory in db.Set<Models.Inventory>().AsNoTracking() on reservation.InventoryId equals inventory.Id
            join product in db.Products.AsNoTracking() on inventory.ProductId equals product.Id into products
            from product in products.DefaultIfEmpty()
            join warehouse in db.Set<Warehouse>().AsNoTracking() on inventory.WarehouseId equals warehouse.Id into warehouses
            from warehouse in warehouses.DefaultIfEmpty()
            where reservation.BusinessUnitId == tenant && inventory.Buid == tenant &&
                (string.IsNullOrWhiteSpace(status) || status != "active" || reservation.Status == StockReservationStatus.Active)
            orderby reservation.CreatedOn descending
            select new { reservation.Id, productId = inventory.ProductId ?? 0, partNumber = product != null ? product.PartNo : inventory.PartNo,
                productName = product != null ? product.ProductName : inventory.ProductName, warehouseName = warehouse != null ? warehouse.WarehouseName : "Unassigned",
                reservation.Quantity, status = reservation.Status.ToString(), demandType = reservation.OrderId.HasValue ? "Order" : "Hold",
                demandReference = reservation.OrderId.HasValue ? $"Order {reservation.OrderId}" : reservation.IdempotencyKey,
                nexoraSerial = (string?)null, requiredAt = (DateTime?)null, version = reservation.Version };
        return Ok(await query.Take(250).ToListAsync(ct));
    }

    [HttpPost("reservations/{id:long}/release")]
    [RequireModulePermission("Products", PermissionAction.Edit)]
    public async Task<ActionResult> Release(long id, VersionRequest request, CancellationToken ct)
    {
        _ = RequiredIdempotencyKey();
        var tenant = TenantId();
        var reservation = await db.Set<StockReservation>().SingleOrDefaultAsync(x => x.BusinessUnitId == tenant && x.Id == id, ct);
        if (reservation == null) return NotFound();
        if (reservation.Version != request.ExpectedVersion) return Conflict(new { error = "Reservation changed. Refresh and retry." });
        if (reservation.Status == StockReservationStatus.Released) return NoContent();
        if (reservation.Status != StockReservationStatus.Active) return Conflict(new { error = "Only an active reservation can be released." });
        db.Entry(reservation).Property(x => x.Version).OriginalValue = reservation.Version;
        reservation.Status = StockReservationStatus.Released; reservation.ReleasedOn = DateTime.UtcNow;
        await db.SaveChangesAsync(ct); return NoContent();
    }

    [HttpGet("incoming")]
    [RequireModulePermission("Products", PermissionAction.View)]
    public async Task<ActionResult> Incoming([FromQuery] string? status, CancellationToken ct)
    {
        var tenant = TenantId();
        var query = from incoming in db.IncomingInventory.AsNoTracking()
            join product in db.Products.AsNoTracking() on incoming.ProductId equals product.Id
            join warehouse in db.Set<Warehouse>().AsNoTracking() on incoming.WarehouseId equals warehouse.Id
            where incoming.BusinessUnitId == tenant && (string.IsNullOrWhiteSpace(status) || incoming.Status.ToString().ToLower() == status.ToLower())
            orderby incoming.ExpectedOn
            select new { incoming.Id, purchaseOrderId = (long?)null, purchaseOrderNumber = incoming.SourceId,
                supplierName = "Not linked", partNumber = product.PartNo, productName = product.ProductName,
                warehouseName = warehouse.WarehouseName, incoming.OrderedQuantity, incoming.ReceivedQuantity,
                expectedAt = (DateOnly?)incoming.ExpectedOn, status = incoming.Status.ToString() };
        return Ok(await query.Take(250).ToListAsync(ct));
    }

    [HttpGet("movements")]
    [RequireModulePermission("Products", PermissionAction.View)]
    public async Task<ActionResult> Movements([FromQuery(Name = "from")] DateTime? fromUtc, [FromQuery(Name = "to")] DateTime? toUtc, CancellationToken ct)
    {
        var tenant = TenantId();
        var query = from movement in db.InventoryMovements.AsNoTracking()
            join product in db.Products.AsNoTracking() on movement.ProductId equals product.Id
            join warehouse in db.Set<Warehouse>().AsNoTracking() on movement.WarehouseId equals warehouse.Id
            where movement.BusinessUnitId == tenant && (!fromUtc.HasValue || movement.OccurredOn >= fromUtc) && (!toUtc.HasValue || movement.OccurredOn < toUtc)
            orderby movement.OccurredOn descending
            select new { movement.Id, occurredAt = movement.OccurredOn, movementType = movement.Type.ToString(),
                partNumber = product.PartNo, productName = product.ProductName, warehouseName = warehouse.WarehouseName,
                movement.Quantity, referenceType = movement.SourceType, reference = movement.SourceId, actorName = movement.CreatedBy };
        return Ok(await query.Take(500).ToListAsync(ct));
    }

    [HttpGet("demand")]
    [RequireModulePermission("Products", PermissionAction.View)]
    public async Task<ActionResult> Demand([FromQuery] string? search, CancellationToken ct)
    {
        var rows = await AvailabilityRows(search, null, ct);
        return Ok(rows.GroupBy(x => new { x.ProductId, x.PartNumber, x.ProductName }).Select(group => {
            var available = group.Sum(x => x.Available); var reorder = group.Sum(x => x.ReorderPoint);
            var openDemand = Math.Max(0, reorder - available); return new { group.Key.ProductId, group.Key.PartNumber,
                group.Key.ProductName, openDemand, available, shortfall = openDemand, incoming = group.Sum(x => x.Incoming),
                earliestNeedAt = (DateTime?)null, demandSources = openDemand > 0 ? 1 : 0 }; }).Where(x => x.openDemand > 0));
    }

    [HttpGet("related-resources")]
    [RequireModulePermission("Products", PermissionAction.View)]
    public async Task<ActionResult> RelatedResources(CancellationToken ct)
    {
        var tenant = TenantId();
        return Ok(new[] {
            Resource("aliases", "Product aliases", "Approved customer, supplier, legacy, and manufacturer identifiers.", await db.ProductAliases.CountAsync(x => x.BusinessUnitId == tenant, ct), "/inventory/products"),
            Resource("supersessions", "Product supersessions", "Approved replacement and form-fit-function relationships.", await db.ProductSupersessions.CountAsync(x => x.BusinessUnitId == tenant, ct), "/inventory/products"),
            Resource("supplier-history", "Supplier quote history", "Tenant-local sourcing evidence for known and unknown products.", await db.SupplierQuotedItems.CountAsync(x => x.BusinessUnitId == tenant, ct), "/suppliers/quoted-items")
        });
    }

    private async Task<List<AvailabilityRow>> AvailabilityRows(string? search, long? warehouseId, CancellationToken ct)
    {
        var tenant = TenantId();
        var inventory = await (from stock in db.Set<Models.Inventory>().AsNoTracking()
            join product in db.Products.AsNoTracking() on stock.ProductId equals product.Id
            join warehouse in db.Set<Warehouse>().AsNoTracking() on stock.WarehouseId equals warehouse.Id
            where stock.Buid == tenant && product.Buid == tenant && warehouse.BusinessUnitId == tenant &&
                (!warehouseId.HasValue || warehouse.Id == warehouseId) &&
                (string.IsNullOrWhiteSpace(search) || EF.Functions.ILike(product.PartNo, $"%{search}%") || EF.Functions.ILike(product.ProductName ?? "", $"%{search}%"))
            select new { stock, product, warehouse }).ToListAsync(ct);
        var ids = inventory.Select(x => x.stock.Id).ToArray();
        var reserved = await db.Set<StockReservation>().AsNoTracking().Where(x => x.BusinessUnitId == tenant && ids.Contains(x.InventoryId) && x.Status == StockReservationStatus.Active)
            .GroupBy(x => x.InventoryId).Select(x => new { Id = x.Key, Quantity = x.Sum(r => r.Quantity) }).ToDictionaryAsync(x => x.Id, x => x.Quantity, ct);
        var productIds = inventory.Select(x => x.product.Id).Distinct().ToArray();
        var incoming = await db.IncomingInventory.AsNoTracking().Where(x => x.BusinessUnitId == tenant && productIds.Contains(x.ProductId) && x.Status != IncomingInventoryStatus.Received && x.Status != IncomingInventoryStatus.Cancelled)
            .ToListAsync(ct);
        return inventory.Select(x => { var held = reserved.GetValueOrDefault(x.stock.Id); var available = Math.Max(0, x.stock.QtyOnHand - held - x.stock.AllocatedQuantity - x.stock.QuarantineQuantity - x.stock.DamagedQuantity - x.stock.ExpiredQuantity - x.stock.SafetyStockQuantity);
            return new AvailabilityRow(x.stock.Id, x.product.Id, x.product.PartNo, x.product.ProductName ?? x.product.PartNo,
                x.warehouse.Id, x.warehouse.WarehouseName, x.stock.QtyOnHand, held, available,
                incoming.Where(i => i.ProductId == x.product.Id && i.WarehouseId == x.warehouse.Id).Sum(i => i.OpenQuantity),
                x.stock.ReorderPoint, x.product.LeadTime); }).ToList();
    }

    private long TenantId() => long.TryParse(User.FindFirst("businessUnitId")?.Value, out var id) && id > 0 ? id : throw new InvalidOperationException("Business Unit ID is required.");
    private string RequiredIdempotencyKey() => Request.Headers.TryGetValue("Idempotency-Key", out var value) && !string.IsNullOrWhiteSpace(value) ? value.ToString() : throw new ArgumentException("Idempotency-Key header is required.");
    private static object Metric(string key, string label, decimal value) => new { key, label, value, unit = "count" };
    private static object Resource(string key, string label, string description, int count, string route) => new { key, label, description, recordCount = count, route, requiredModule = "Products" };
    private static object ResolutionRow(LeadLineCommercialResolution x) => new {
        x.Id, x.LeadId, x.LeadRevisionId, x.LeadLineId, x.RfqId, x.ProductId,
        x.RequestedPartNumber, x.RequestedQuantity, classification = x.Classification.ToString(),
        x.AvailableToPromise, x.IncomingAvailable,
        fulfilment = System.Text.Json.JsonSerializer.Deserialize<object>(x.FulfilmentJson),
        relatedResources = System.Text.Json.JsonSerializer.Deserialize<object>(x.RelatedResourcesJson),
        productResolution = System.Text.Json.JsonSerializer.Deserialize<object>(x.ProductResolutionJson),
        x.ResolutionMethod, x.EvidenceReference, x.InventoryAsOfUtc, x.ResolvedOn,
        externalDiscoveryUsed = false
    };
}

public sealed record VersionRequest(uint ExpectedVersion);
public sealed record AvailabilityRow(long InventoryId, long ProductId, string PartNumber, string ProductName,
    long WarehouseId, string WarehouseName, decimal OnHand, decimal Reserved, decimal Available,
    decimal Incoming, decimal ReorderPoint, int? LeadTimeDays);
