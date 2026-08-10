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
[ERP_RFQ_Automation.Platform.Entitlements.RequiresEntitlement(ERP_RFQ_Automation.Platform.Entitlements.TypedEntitlementCatalog.Inventory)]
public sealed class InventoryIntelligenceController(
    ErpRfqAutomationContext db,
    ICommercialLineResolutionApplicationService lineResolution,
    IInventoryAvailabilityService inventoryAvailability,
    IOrderStockReservationService orderStock,
    IStockLedgerService ledger,
    IReorderAlertService reorderAlerts) : ControllerBase
{
    private readonly IStockLedgerService _ledger = ledger;

    /// <summary>The largest number of rows any list endpoint will return in one response.</summary>
    private const int MaxRows = 500;

    /// <summary>
    /// Runs the commercial line resolution for a lead and returns the per-line result.
    ///
    /// <para><b>Why this has explicit error mapping.</b> It used to be a bare expression body, so
    /// every failure — a lead with no immutable current revision, a lead in another tenant, a
    /// product-resolver fault — left the pipeline as an unhandled 500. The screen renders a
    /// single fixed sentence on any error ("Inventory Check Unavailable. No product, stock, or
    /// supplier commitment was selected automatically."), which reads like an empty result rather
    /// than a fault and names no cause. Diagnosing a lead that could not be checked therefore
    /// meant reading server logs. The gate messages are written for operators and are safe to
    /// render, so they are returned instead of being swallowed.</para>
    /// </summary>
    [HttpPost("leads/{leadId:long}/resolve")]
    [RequireModulePermission("Leads", PermissionAction.Edit)]
    public async Task<ActionResult> ResolveLead(long leadId, [FromQuery] int limit = 10, CancellationToken ct = default)
    {
        try
        {
            var rows = await lineResolution.ResolveLeadAsync(TenantId(), leadId, limit, ct);
            return Ok(rows.Select(ResolutionRow));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(Problem(StatusCodes.Status404NotFound, "Lead not found", ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            // Domain preconditions — most commonly "The lead has no immutable current revision",
            // which is what a legacy lead created before the identity pipeline looks like.
            return Conflict(Problem(StatusCodes.Status409Conflict, "Inventory check not run", ex.Message));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(Problem(StatusCodes.Status400BadRequest, "Inventory check not run", ex.Message));
        }
    }

    /// <summary>RFC 7807 body carrying a traceId, matching ConversionIntelligenceController.</summary>
    private ProblemDetails Problem(int status, string title, string detail) => new()
    {
        Status = status,
        Title = title,
        Detail = detail,
        Extensions = { ["traceId"] = HttpContext.TraceIdentifier }
    };

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
        => Ok((await AvailabilityRows(search, warehouseId, ct)).Take(MaxRows).Select(x => new { x.InventoryId, x.ProductId, x.PartNumber, x.ProductName,
            x.WarehouseId, x.WarehouseName, x.OnHand, x.Reserved, x.Available, x.Incoming,
            reorderPoint = (decimal?)x.ReorderPoint,
            // FR-INV-04. Nullable all the way to the client: null is "not configured", and the
            // screen renders it as such rather than as a blank cell that reads like zero.
            x.MinimumLevel, x.MaximumLevel, x.SafetyStock, x.LeadTimeDays }));

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

    /// <summary>
    /// FR-INV-04. Sets the minimum and maximum stock levels for one item in one warehouse.
    ///
    /// <para>Both are nullable and null is a value the caller can send: it clears the level and
    /// leaves the item unmonitored for that side. There is no partial update — a request states
    /// both levels, because a payload that silently kept a stale maximum would be
    /// indistinguishable on screen from one that cleared it.</para>
    /// </summary>
    [HttpPost("stock/levels")]
    [RequireModulePermission("Products", PermissionAction.Edit)]
    public Task<ActionResult> SetStockLevels(StockLevelsRequest request, CancellationToken ct)
        => LedgerAction(() => _ledger.SetStockLevelsAsync(TenantId(), request.ProductId,
            request.WarehouseId, request.MinimumLevel, request.MaximumLevel, Actor(), ct));

    /// <summary>
    /// FR-INV-04. Every stock row's live position against the levels configured for it — including
    /// the rows with no level set at all, which are reported as UNMONITORED rather than as healthy.
    ///
    /// <para>This is the same computation the sweep raises alerts from, served by the same function,
    /// so the screen and the alert can never disagree about what is short.</para>
    /// </summary>
    [HttpGet("stock/levels")]
    [RequireModulePermission("Products", PermissionAction.View)]
    public async Task<ActionResult> StockLevels([FromQuery] bool breachedOnly = false, CancellationToken ct = default)
    {
        var rows = await reorderAlerts.EvaluateAsync(TenantId(), ct);
        var filtered = rows.Where(x => !breachedOnly || x.Kind is not null).ToArray();
        return Ok(new
        {
            generatedAt = DateTime.UtcNow,
            rowCount = rows.Count,
            configuredCount = rows.Count(x => x.IsConfigured),
            breachedCount = rows.Count(x => x.Kind is not null),
            // Named rather than implied. A tenant that has configured nothing gets no alerts, and
            // the honest description of that state is "nothing is being watched", not "all healthy".
            unmonitoredCount = rows.Count(x => !x.IsConfigured),
            rows = filtered
                .OrderBy(x => x.Kind is null ? 1 : 0).ThenBy(x => x.PartNumber)
                .Take(MaxRows)
                .Select(x => new
                {
                    x.InventoryId, x.ProductId, x.PartNumber, x.ProductName,
                    x.WarehouseId, x.WarehouseName,
                    x.OnHand, x.Available, x.Incoming, projected = x.Projected,
                    x.MinimumLevel, x.MaximumLevel, x.ReorderPoint,
                    breach = x.Kind, x.ThresholdQuantity, x.ShortfallQuantity,
                    monitored = x.IsConfigured,
                }),
        });
    }

    /// <summary>
    /// FR-INV-04. The reorder alert ledger. Open and acknowledged alerts by default, because a
    /// resolved alert is history and burying today's four alerts under a year of resolved ones is
    /// how an exception screen stops being read.
    /// </summary>
    [HttpGet("reorder-alerts")]
    [RequireModulePermission("Products", PermissionAction.View)]
    public async Task<ActionResult> ReorderAlerts(
        [FromQuery] string? status, [FromQuery] string? kind, CancellationToken ct = default)
    {
        if (status is not null && !ReorderAlertStatuses.IsKnown(status))
            return BadRequest(new { error = "status must be OPEN, ACKNOWLEDGED or RESOLVED." });
        if (kind is not null && !ReorderAlertKinds.IsKnown(kind))
            return BadRequest(new
            {
                error = "kind must be one of " + string.Join(", ", ReorderAlertKinds.AllForQuery) + ".",
            });

        var tenant = TenantId();
        // The status set is declared once on ReorderAlertStatuses and PROJECTED here for the query.
        // Consulting the set object itself inside the predicate would bind to the interface's own
        // Contains, which EF cannot translate and which throws only at runtime.
        var wanted = status is null ? ReorderAlertStatuses.LiveForQuery : [status];

        var rows = await (
            from alert in db.Set<InventoryReorderAlert>().AsNoTracking()
            join inventory in db.Set<Models.Inventory>().AsNoTracking() on alert.InventoryId equals inventory.Id
            join product in db.Products.AsNoTracking() on alert.ProductId equals product.Id into products
            from product in products.DefaultIfEmpty()
            join warehouse in db.Set<Warehouse>().AsNoTracking() on alert.WarehouseId equals warehouse.Id into warehouses
            from warehouse in warehouses.DefaultIfEmpty()
            where alert.BusinessUnitId == tenant && inventory.Buid == tenant
                  && wanted.Contains(alert.Status)
                  && (kind == null || alert.Kind == kind)
            orderby alert.Severity, alert.RaisedOn descending
            select new
            {
                alert.Id, alert.InventoryId, alert.ProductId, alert.WarehouseId,
                partNumber = product != null ? product.PartNo : inventory.PartNo,
                productName = product != null ? product.ProductName : inventory.ProductName,
                warehouseName = warehouse != null ? warehouse.WarehouseName : "Unassigned",
                alert.Kind, alert.Status, alert.Severity,
                alert.OnHandQuantity, alert.AvailableQuantity, alert.IncomingQuantity,
                alert.ProjectedQuantity, alert.ThresholdQuantity, alert.ShortfallQuantity,
                alert.RaisedOn, alert.NotifiedCount,
                alert.AcknowledgedOn, alert.AcknowledgedBy, alert.AcknowledgementReason,
                alert.ResolvedOn, alert.ResolutionReason, alert.Version,
            }).Take(MaxRows).ToListAsync(ct);

        return Ok(new { generatedAt = DateTime.UtcNow, alertCount = rows.Count, rows });
    }

    /// <summary>
    /// FR-INV-04. Records that a named person has taken an alert, with their reason. The
    /// acknowledgement does not silence the condition — the alert stays on the ledger and resolves
    /// itself when the stock recovers — it records that somebody owns it.
    /// </summary>
    [HttpPost("reorder-alerts/{id:long}/acknowledge")]
    [RequireModulePermission("Products", PermissionAction.Edit)]
    public async Task<ActionResult> AcknowledgeReorderAlert(
        long id, AcknowledgeAlertRequest request, CancellationToken ct)
    {
        try
        {
            var alert = await reorderAlerts.AcknowledgeAsync(TenantId(), id, request.ExpectedVersion,
                Actor(), request.Reason, ct);
            return Ok(new
            {
                alert.Id, alert.Status, alert.AcknowledgedOn, alert.AcknowledgedBy,
                alert.AcknowledgementReason, alert.Version,
            });
        }
        catch (KeyNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (DbUpdateConcurrencyException) { return Conflict(new { error = "This alert was changed by somebody else. Reload and try again." }); }
        catch (ReorderAlertException ex) { return Conflict(new { error = ex.Message }); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
    }

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
                // FR-INV-01. What the hold physically NAMES. Null is a real and visible state —
                // stock with no receipt behind it — and the screen must show it as an absent lot
                // rather than as a blank column, because a hold nobody can trace is the thing a
                // recall discovers too late.
                reservation.MaterialLotId,
                lotNumber = db.Set<ERP_RFQ_Automation.Traceability.MaterialLot>()
                    .Where(l => l.BusinessUnitId == tenant && l.Id == reservation.MaterialLotId)
                    .Select(l => l.LotNumber).FirstOrDefault(),
                nexoraSerial = (string?)null, requiredAt = (DateTime?)null, version = reservation.Version };
        return Ok(await query.Take(250).ToListAsync(ct));
    }

    /// <summary>
    /// FR-INV-01. The lots on one inventory row that a hold may still name, in the order the
    /// warehouse should pick them (first-expired-first-out). This is the same list the order
    /// allocator walks, served by the same function — a screen that showed a different pick order
    /// from the one the allocator uses would be worse than no screen.
    /// </summary>
    [HttpGet("inventory/{inventoryId:long}/lots")]
    [RequireModulePermission("Products", PermissionAction.View)]
    public async Task<ActionResult> ReservableLots(long inventoryId, CancellationToken ct)
    {
        var tenant = TenantId();
        var exists = await db.Set<Models.Inventory>().AsNoTracking()
            .AnyAsync(x => x.Id == inventoryId && x.Buid == tenant, ct);
        if (!exists) return NotFound(new { error = "Inventory row was not found in this business unit." });

        var lots = await inventoryAvailability.GetReservableLotsAsync(tenant, inventoryId, ct);
        var availability = await inventoryAvailability.GetAvailabilityAsync(tenant, inventoryId, ct);
        var lotCovered = lots.Sum(x => x.Remaining);

        return Ok(new
        {
            inventoryId,
            availability.OnHand,
            availability.Reserved,
            availability.Available,
            // The un-lotted balance is stated, never implied. Stock that entered by opening count,
            // adjustment or transfer has no receipt behind it and therefore no lot, so a hold
            // against it names nothing and a recall cannot reach it. Showing only the lots would
            // read as full coverage.
            lotControlledQuantity = lotCovered,
            unlottedQuantity = Math.Max(0m, availability.OnHand - availability.Quarantine
                - availability.Damaged - availability.Expired - lotCovered),
            lots = lots.Select(x => new
            {
                x.MaterialLotId, x.LotNumber, x.Remaining, x.Held, x.Reservable, x.ExpiryDate, x.ReceivedOn
            }),
        });
    }

    /// <summary>
    /// FR-INV-01. The recall population for one lot: the orders holding it now and the orders it
    /// has already been issued to.
    ///
    /// <para>This is the question lot-level reservation exists to answer. Before it, a recall could
    /// only ask "which orders hold this PRODUCT", which over-reaches on to customers whose material
    /// came from a sound lot — and under-reaches the moment an order holds stock across two
    /// warehouses.</para>
    /// </summary>
    [HttpGet("lots/{materialLotId:long}/commitments")]
    [RequireModulePermission("Products", PermissionAction.View)]
    public async Task<ActionResult> LotCommitments(long materialLotId, CancellationToken ct)
    {
        var tenant = TenantId();
        var lot = await db.Set<ERP_RFQ_Automation.Traceability.MaterialLot>().AsNoTracking()
            .Where(x => x.BusinessUnitId == tenant && x.Id == materialLotId)
            .Select(x => new { x.Id, x.LotNumber, x.Status, x.QuantityReceived, x.QuantityConsumed })
            .SingleOrDefaultAsync(ct);
        if (lot is null) return NotFound(new { error = "Material lot was not found in this business unit." });

        var commitments = await inventoryAvailability.GetLotCommitmentsAsync(tenant, materialLotId, ct);
        var orderIds = commitments.AffectedOrderIds;
        var orderNumbers = orderIds.Count == 0
            ? new Dictionary<long, string>()
            : await db.Orders.AsNoTracking()
                .Where(o => o.BusinessUnitId == tenant && orderIds.Contains(o.Id))
                .ToDictionaryAsync(o => o.Id, o => o.OrderNo, ct);

        return Ok(new
        {
            materialLotId, lot.LotNumber, lot.Status,
            lot.QuantityReceived, lot.QuantityConsumed,
            heldQuantity = commitments.HeldQuantity,
            consumedQuantity = commitments.ConsumedQuantity,
            affectedOrders = orderIds.Select(id => new { orderId = id, orderNo = orderNumbers.GetValueOrDefault(id) }),
            held = commitments.Held.Select(Commitment),
            consumed = commitments.Consumed.Select(Commitment),
        });

        object Commitment(LotCommitment x) => new
        {
            x.ReservationId, x.OrderId, x.OrderItemId, x.Quantity,
            status = x.Status.ToString(), x.CreatedOn,
        };
    }

    /// <summary>FR-INV-05. Counted versus book, per counted stock row.</summary>
    [HttpGet("stock/count-variance")]
    [RequireModulePermission("Products", PermissionAction.View)]
    public async Task<ActionResult> CountVariance(
        [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] bool varianceOnly = true,
        CancellationToken ct = default)
    {
        var rows = await _ledger.GetCountVarianceAsync(TenantId(), from, to, varianceOnly, ct);
        return Ok(new
        {
            countedRows = rows.Count,
            netVariance = rows.Sum(x => x.Variance),
            absoluteVariance = rows.Sum(x => Math.Abs(x.Variance)),
            rows = rows.Take(MaxRows).Select(x => new
            {
                x.InventoryId, x.ProductId, x.PartNumber, x.ProductName, x.WarehouseId, x.WarehouseName,
                x.BookQuantity, x.CountedQuantity, x.Variance, x.VariancePercent,
                x.CountedOn, x.CountedBy, x.Reason,
            }),
        });
    }

    /// <summary>FR-INV-06. Slow-moving and obsolete stock, aged from the last physical issue.</summary>
    [HttpGet("stock/ageing")]
    [RequireModulePermission("Products", PermissionAction.View)]
    public async Task<ActionResult> StockAgeing(
        [FromQuery] long? warehouseId, [FromQuery] string? band, CancellationToken ct = default)
    {
        var rows = await _ledger.GetStockAgeingAsync(TenantId(), warehouseId, band, ct);
        return Ok(new
        {
            rowCount = rows.Count,
            carryingValue = rows.Sum(x => x.CarryingValue),
            bands = rows.GroupBy(x => x.Band).OrderBy(g => g.Key).Select(g => new
            {
                band = g.Key, rowCount = g.Count(), units = g.Sum(x => x.OnHand),
                carryingValue = g.Sum(x => x.CarryingValue),
            }),
            rows = rows.Take(MaxRows).Select(x => new
            {
                x.InventoryId, x.ProductId, x.PartNumber, x.ProductName, x.WarehouseId, x.WarehouseName,
                x.OnHand, x.UnitCost, x.CarryingValue, x.LastReceiptOn, x.LastIssueOn,
                x.DaysSinceLastIssue, x.DaysSinceLastReceipt, x.Band,
            }),
        });
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
                stock.MinimumLevel, stock.MaximumLevel,
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
                x.ReorderPoint, x.LeadTime, x.MinimumLevel, x.MaximumLevel, x.SafetyStockQuantity); }).ToList();
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

/// <summary>
/// FR-INV-04. Both levels are nullable and both are always stated: null clears the level and means
/// "not configured", which is a different thing from zero and is rendered differently.
/// </summary>
public sealed record StockLevelsRequest(long ProductId, long WarehouseId, decimal? MinimumLevel, decimal? MaximumLevel);

/// <summary>FR-INV-04. The reason is required — an acknowledgement with no reason is a mute button
/// with nobody's name on it.</summary>
public sealed record AcknowledgeAlertRequest(uint ExpectedVersion, string Reason);

public sealed record AvailabilityRow(long InventoryId, long ProductId, string PartNumber, string ProductName,
    long WarehouseId, string WarehouseName, decimal OnHand, decimal Reserved, decimal Available,
    decimal Incoming, decimal ReorderPoint, int? LeadTimeDays,
    decimal? MinimumLevel = null, decimal? MaximumLevel = null, decimal SafetyStock = 0m);
