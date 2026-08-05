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
    ICommercialLineResolutionApplicationService lineResolution,
    IInventoryAvailabilityService inventoryAvailability,
    IOrderStockReservationService orderStock) : ControllerBase
{
    /// <summary>
    /// The stock ledger is constructed here rather than injected because Program.cs is owned by a
    /// concurrent workstream during this change; it depends on nothing but the DbContext, so this
    /// is behaviourally identical to a scoped DI registration. See the module report for the
    /// one-line registration that should replace this.
    /// </summary>
    private readonly IStockLedgerService _ledger = new StockLedgerService(db);

    /// <summary>The largest number of rows any list endpoint will return in one response.</summary>
    private const int MaxRows = 500;

    [HttpPost("leads/{leadId:long}/resolve")]
    [RequireModulePermission("Leads", PermissionAction.Edit)]
    public async Task<ActionResult> ResolveLead(long leadId, [FromQuery] int limit = 10, CancellationToken ct = default)
        => Ok((await lineResolution.ResolveLeadAsync(TenantId(), leadId, limit, ct)).Select(ResolutionRow));

    [HttpGet("leads/{leadId:long}/resolutions")]
    [RequireModulePermission("Leads", PermissionAction.View)]
    public async Task<ActionResult> LeadResolutions(long leadId, CancellationToken ct)
        => Ok((await db.Set<LeadLineCommercialResolution>().AsNoTracking()
            .Where(x => x.BusinessUnitId == TenantId() && x.LeadId == leadId)
            .OrderBy(x => x.LeadLineId).ThenByDescending(x => x.ResolvedOn).ToListAsync(ct))
            .GroupBy(x => x.LeadLineId).Select(x => ResolutionRow(x.First())));

    [HttpGet("rfqs/{rfqId:long}/resolutions")]
    [RequireModulePermission("RFQ Management", PermissionAction.View)]
    public async Task<ActionResult> RfqResolutions(long rfqId, CancellationToken ct)
        => Ok((await db.Set<LeadLineCommercialResolution>().AsNoTracking()
            .Where(x => x.BusinessUnitId == TenantId() && x.RfqId == rfqId)
            .OrderBy(x => x.LeadLineId).ThenByDescending(x => x.ResolvedOn).ToListAsync(ct))
            .GroupBy(x => x.LeadLineId).Select(x => ResolutionRow(x.First())));

    [HttpGet("quotes/{quoteId:long}/resolutions")]
    [RequireModulePermission("Quotations", PermissionAction.View)]
    public async Task<ActionResult> QuoteResolutions(long quoteId, CancellationToken ct)
    {
        var tenant = TenantId();
        var rfqId = await db.Quotes.AsNoTracking().Where(x => x.BusinessUnitId == tenant && x.Id == quoteId)
            .Select(x => x.Rfqid).SingleOrDefaultAsync(ct);
        if (!rfqId.HasValue) return NotFound();
        return Ok((await db.Set<LeadLineCommercialResolution>().AsNoTracking()
            .Where(x => x.BusinessUnitId == tenant && x.RfqId == rfqId.Value)
            .OrderBy(x => x.LeadLineId).ThenByDescending(x => x.ResolvedOn).ToListAsync(ct))
            .GroupBy(x => x.LeadLineId).Select(x => ResolutionRow(x.First())));
    }

    [HttpGet("overview")]
    [RequireModulePermission("Products", PermissionAction.View)]
    public async Task<ActionResult> Overview(CancellationToken ct)
    {
        var rows = await AvailabilityRows(null, null, ct);
        var allExceptions = rows.Where(x => x.Available <= x.ReorderPoint).ToArray();
        // The exception LIST is capped so a tenant with tens of thousands of low-stock rows cannot
        // render an unbounded table; the exception COUNT below stays exact, because a truncated
        // headline metric would be worse than a truncated list.
        var exceptions = allExceptions.OrderBy(x => x.Available).ThenBy(x => x.PartNumber)
            .Take(MaxRows).Select(x => new {
                id = $"inventory-{x.InventoryId}", productId = (long?)x.ProductId, x.PartNumber, x.ProductName,
                x.WarehouseName, exceptionType = x.Available <= 0 ? "OutOfStock" : "BelowReorderPoint",
                availableQuantity = x.Available, requiredQuantity = (decimal?)x.ReorderPoint, dueAt = (DateTime?)null
            }).ToArray();
        return Ok(new { generatedAt = DateTime.UtcNow, metrics = new[] {
            Metric("sku-count", "Stocked products", rows.Select(x => x.ProductId).Distinct().Count()),
            Metric("on-hand", "On hand", rows.Sum(x => x.OnHand)),
            Metric("available", "Available to promise", rows.Sum(x => x.Available)),
            Metric("exceptions", "Stock exceptions", allExceptions.Length)
        }, exceptions, exceptionsTruncated = allExceptions.Length > exceptions.Length });
    }

    [HttpGet("availability")]
    [RequireModulePermission("Products", PermissionAction.View)]
    public async Task<ActionResult> Availability([FromQuery] string? search, [FromQuery] long? warehouseId, CancellationToken ct)
        // Bounded: this used to materialise every inventory row in the tenant and render all of
        // them. Narrow with search/warehouseId rather than scrolling.
        => Ok((await AvailabilityRows(search, warehouseId, ct)).Take(MaxRows).Select(x => new { x.ProductId, x.PartNumber, x.ProductName,
            x.WarehouseId, x.WarehouseName, x.OnHand, x.Reserved, x.Available, x.Incoming,
            reorderPoint = (decimal?)x.ReorderPoint, x.LeadTimeDays }));

    /// <summary>
    /// Records a counted physical quantity for a product in a warehouse, creating the inventory
    /// row on first use. This is the route for opening stock and cycle counts: before it existed
    /// the only way an inventory row could come into being was issuing a supplier purchase order,
    /// so a client's existing stock had no way into the authoritative ledger.
    /// </summary>
    [HttpPost("stock/count")]
    [RequireModulePermission("Products", PermissionAction.Edit)]
    public Task<ActionResult> RecordCount(StockCountRequest request, CancellationToken ct)
        => LedgerAction(() => _ledger.RecordCountAsync(TenantId(), request.ProductId, request.WarehouseId,
            request.CountedQuantity, RequiredIdempotencyKey(), Actor(), request.Reason, ct));

    [HttpPost("stock/adjust")]
    [RequireModulePermission("Products", PermissionAction.Edit)]
    public Task<ActionResult> Adjust(StockAdjustRequest request, CancellationToken ct)
        => LedgerAction(() => _ledger.AdjustAsync(TenantId(), request.ProductId, request.WarehouseId,
            request.Delta, RequiredIdempotencyKey(), Actor(), request.Reason, ct));

    /// <summary>Moves stock into or out of the quarantine / damaged / expired buckets.</summary>
    [HttpPost("stock/reclassify")]
    [RequireModulePermission("Products", PermissionAction.Edit)]
    public Task<ActionResult> Reclassify(StockReclassifyRequest request, CancellationToken ct)
        => LedgerAction(() => _ledger.ReclassifyAsync(TenantId(), request.ProductId, request.WarehouseId,
            request.Bucket, request.Quantity, RequiredIdempotencyKey(), Actor(), request.Reason, ct));

    [HttpPost("stock/safety-stock")]
    [RequireModulePermission("Products", PermissionAction.Edit)]
    public Task<ActionResult> SetSafetyStock(SafetyStockRequest request, CancellationToken ct)
        => LedgerAction(() => _ledger.SetSafetyStockAsync(TenantId(), request.ProductId, request.WarehouseId,
            request.SafetyStock, Actor(), ct));

    /// <summary>Moves physical stock between two of the tenant's warehouses.</summary>
    [HttpPost("stock/transfer")]
    [RequireModulePermission("Products", PermissionAction.Edit)]
    public async Task<ActionResult> Transfer(StockTransferRequest request, CancellationToken ct)
    {
        try
        {
            var (from, to) = await _ledger.TransferAsync(TenantId(), request.ProductId, request.FromWarehouseId,
                request.ToWarehouseId, request.Quantity, RequiredIdempotencyKey(), Actor(), request.Reason, ct);
            return Ok(new { from, to });
        }
        catch (KeyNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (StockLedgerException ex) { return Conflict(new { error = ex.Message }); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
    }

    /// <summary>
    /// Reconciles every on-hand balance against the movement ledger. A non-empty result means some
    /// code path changed stock without posting a movement, and the tenant's stock figures cannot
    /// be trusted until it is explained.
    /// </summary>
    [HttpGet("stock/reconciliation")]
    [RequireModulePermission("Products", PermissionAction.View)]
    public async Task<ActionResult> Reconciliation([FromQuery] bool driftOnly = true, CancellationToken ct = default)
    {
        var rows = await inventoryAvailability.ReconcileLedgerAsync(TenantId(), driftOnly, ct);
        return Ok(new { generatedAt = DateTime.UtcNow, balanced = rows.Count == 0, driftCount = rows.Count,
            rows = rows.Take(MaxRows).Select(x => new { x.InventoryId, x.ProductId, x.WarehouseId,
                x.RecordedOnHand, x.LedgerOnHand, x.Drift, x.MovementCount }) });
    }

    /// <summary>
    /// Recovers stock held by abandoned reservations: holds older than <paramref name="olderThanHours"/>
    /// and holds whose owning order has been deleted. Both leak available-to-promise permanently
    /// otherwise, because nothing else can ever release them.
    /// </summary>
    [HttpPost("reservations/sweep")]
    [RequireModulePermission("Products", PermissionAction.Edit)]
    public async Task<ActionResult> SweepReservations([FromQuery] int olderThanHours = 72, CancellationToken ct = default)
    {
        if (olderThanHours < 1) return BadRequest(new { error = "olderThanHours must be at least 1." });
        var tenant = TenantId();
        var orphaned = await orderStock.ReleaseOrphanedAsync(tenant, Actor(), ct);
        var expired = await inventoryAvailability.ExpireStaleAsync(
            tenant, DateTime.UtcNow.AddHours(-olderThanHours), Actor(), ct);
        return Ok(new { orphanedReleased = orphaned, expiredReleased = expired, olderThanHours });
    }

    private async Task<ActionResult> LedgerAction(Func<Task<StockLedgerResult>> action)
    {
        try { return Ok(await action()); }
        catch (KeyNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (StockLedgerException ex) { return Conflict(new { error = ex.Message }); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
    }

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
        StockReservationStatus? requestedStatus = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<StockReservationStatus>(status, ignoreCase: true, out var parsed))
                return BadRequest(new { error = "status must be Active, Released, or Consumed." });
            requestedStatus = parsed;
        }
        var query = from reservation in db.Set<StockReservation>().AsNoTracking()
            join inventory in db.Set<Models.Inventory>().AsNoTracking() on reservation.InventoryId equals inventory.Id
            join product in db.Products.AsNoTracking() on inventory.ProductId equals product.Id into products
            from product in products.DefaultIfEmpty()
            join warehouse in db.Set<Warehouse>().AsNoTracking() on inventory.WarehouseId equals warehouse.Id into warehouses
            from warehouse in warehouses.DefaultIfEmpty()
            // BUGFIX: the old predicate was `status != "active" || Status == Active`, which made
            // every filter other than "active" a no-op — asking for released holds returned all of
            // them, including active ones. Parse the requested status and compare it properly.
            where reservation.BusinessUnitId == tenant && inventory.Buid == tenant &&
                (requestedStatus == null || reservation.Status == requestedStatus)
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
        try
        {
            await inventoryAvailability.ReleaseAsync(TenantId(), id, request.ExpectedVersion,
                RequiredIdempotencyKey(), Actor(), ct);
            return NoContent();
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (DbUpdateConcurrencyException ex) { return Conflict(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }
    }

    [HttpGet("incoming")]
    [RequireModulePermission("Products", PermissionAction.View)]
    public async Task<ActionResult> Incoming([FromQuery] string? status, CancellationToken ct)
    {
        var tenant = TenantId();
        // The filter used to be `incoming.Status.ToString().ToLower() == status.ToLower()`, which
        // is not reliably translatable and forces a client-side evaluation of the whole table.
        // Parse once, compare on the mapped column.
        IncomingInventoryStatus? requestedStatus = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<IncomingInventoryStatus>(status, ignoreCase: true, out var parsed))
                return BadRequest(new { error = "status is not a known incoming inventory status." });
            requestedStatus = parsed;
        }
        var query = from incoming in db.IncomingInventory.AsNoTracking()
            join product in db.Products.AsNoTracking() on incoming.ProductId equals product.Id
            join warehouse in db.Set<Warehouse>().AsNoTracking() on incoming.WarehouseId equals warehouse.Id
            where incoming.BusinessUnitId == tenant && product.Buid == tenant && warehouse.BusinessUnitId == tenant
                && (requestedStatus == null || incoming.Status == requestedStatus)
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
            where movement.BusinessUnitId == tenant && product.Buid == tenant && warehouse.BusinessUnitId == tenant
                && (!fromUtc.HasValue || movement.OccurredOn >= fromUtc) && (!toUtc.HasValue || movement.OccurredOn < toUtc)
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
                earliestNeedAt = (DateTime?)null, demandSources = openDemand > 0 ? 1 : 0 }; })
            .Where(x => x.openDemand > 0)
            // Bounded, worst-first: this list drives buying decisions, so the biggest shortfalls
            // must be the ones that survive the cap.
            .OrderByDescending(x => x.openDemand).ThenBy(x => x.PartNumber).Take(MaxRows));
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
            // Project the twelve scalars this method actually reads. Selecting the whole
            // stock/product/warehouse entity graph pulled every description, dimension and audit
            // column for every inventory row on each dashboard load, for no benefit.
            select new
            {
                InventoryId = stock.Id, stock.QtyOnHand, stock.AllocatedQuantity, stock.QuarantineQuantity,
                stock.DamagedQuantity, stock.ExpiredQuantity, stock.SafetyStockQuantity, stock.ReorderPoint,
                ProductId = product.Id, product.PartNo, product.ProductName, product.LeadTime,
                WarehouseId = warehouse.Id, warehouse.WarehouseName
            }).ToListAsync(ct);
        var ids = inventory.Select(x => x.InventoryId).ToArray();
        var reserved = await db.Set<StockReservation>().AsNoTracking().Where(x => x.BusinessUnitId == tenant && ids.Contains(x.InventoryId) && x.Status == StockReservationStatus.Active)
            .GroupBy(x => x.InventoryId).Select(x => new { Id = x.Key, Quantity = x.Sum(r => r.Quantity) }).ToDictionaryAsync(x => x.Id, x => x.Quantity, ct);
        var productIds = inventory.Select(x => x.ProductId).Distinct().ToArray();
        // "Incoming" means OPEN COMMITTED supply only. Planned supply used to be counted here but
        // nowhere else (CommercialInventoryServices and ProductRepository both exclude it), so the
        // same product showed a larger incoming figure on this screen than on the quote it fed.
        var incoming = await db.IncomingInventory.AsNoTracking().Where(x => x.BusinessUnitId == tenant
                && productIds.Contains(x.ProductId)
                && (x.Status == IncomingInventoryStatus.Ordered || x.Status == IncomingInventoryStatus.Confirmed
                    || x.Status == IncomingInventoryStatus.InTransit || x.Status == IncomingInventoryStatus.PartiallyReceived))
            .ToListAsync(ct);
        return inventory.Select(x => { var held = reserved.GetValueOrDefault(x.InventoryId);
            var available = InventoryQuantityMath.AvailableToPromise(x.QtyOnHand, held,
                x.AllocatedQuantity, x.QuarantineQuantity, x.DamagedQuantity,
                x.ExpiredQuantity, x.SafetyStockQuantity);
            return new AvailabilityRow(x.InventoryId, x.ProductId, x.PartNo, x.ProductName ?? x.PartNo,
                x.WarehouseId, x.WarehouseName, x.QtyOnHand, held, available,
                incoming.Where(i => i.ProductId == x.ProductId && i.WarehouseId == x.WarehouseId).Sum(i => i.OpenQuantity),
                x.ReorderPoint, x.LeadTime); }).ToList();
    }

    private long TenantId() => long.TryParse(User.FindFirst("businessUnitId")?.Value, out var id) && id > 0 ? id : throw new InvalidOperationException("Business Unit ID is required.");
    private string Actor() => User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
        ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
        ?? throw new InvalidOperationException("Authenticated actor identity is required.");
    private string RequiredIdempotencyKey() => Request.Headers.TryGetValue("Idempotency-Key", out var value) && !string.IsNullOrWhiteSpace(value) ? value.ToString() : throw new ArgumentException("Idempotency-Key header is required.");
    private static object Metric(string key, string label, decimal value) => new { key, label, value, unit = "count" };
    private static object Resource(string key, string label, string description, int count, string route) => new { key, label, description, recordCount = count, route, requiredModule = "Products" };
    private static object ResolutionRow(LeadLineCommercialResolution x) => new {
        x.Id, x.LeadId, x.LeadRevisionId, x.LeadLineId, x.RfqId, x.RfqItemId, x.ProductId,
        x.RequestedPartNumber, x.RequestedQuantity, classification = x.Classification.ToString(),
        x.AvailableToPromise, x.IncomingAvailable, x.ProjectedShortage, x.LeadTimeDays,
        x.ExpectedAvailableOn, x.UnitCost, x.CostCurrencyCode,
        fulfilment = System.Text.Json.JsonSerializer.Deserialize<object>(x.FulfilmentJson),
        relatedResources = System.Text.Json.JsonSerializer.Deserialize<object>(x.RelatedResourcesJson),
        productResolution = System.Text.Json.JsonSerializer.Deserialize<object>(x.ProductResolutionJson),
        x.ResolutionBatchId, x.ResourceLimit, x.ResolutionMethod, x.EvidenceReference, x.InventoryAsOfUtc, x.ResolvedOn,
        externalDiscoveryUsed = false
    };
}

public sealed record VersionRequest(uint ExpectedVersion);
public sealed record StockCountRequest(long ProductId, long WarehouseId, decimal CountedQuantity, string? Reason);
public sealed record StockAdjustRequest(long ProductId, long WarehouseId, decimal Delta, string? Reason);
public sealed record StockReclassifyRequest(long ProductId, long WarehouseId, StockBucket Bucket, decimal Quantity, string? Reason);
public sealed record SafetyStockRequest(long ProductId, long WarehouseId, decimal SafetyStock);
public sealed record StockTransferRequest(long ProductId, long FromWarehouseId, long ToWarehouseId, decimal Quantity, string? Reason);
public sealed record AvailabilityRow(long InventoryId, long ProductId, string PartNumber, string ProductName,
    long WarehouseId, string WarehouseName, decimal OnHand, decimal Reserved, decimal Available,
    decimal Incoming, decimal ReorderPoint, int? LeadTimeDays);
