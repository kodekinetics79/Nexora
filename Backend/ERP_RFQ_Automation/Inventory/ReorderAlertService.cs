using ERP_RFQ_Automation.Inventory.Commercial;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Inventory;

/// <summary>Raised when a reorder-alert write would break an invariant.</summary>
public sealed class ReorderAlertException(string message) : InvalidOperationException(message);

/// <summary>
/// FR-INV-04. One stock row's position against the levels configured for it, computed but not yet
/// persisted. The sweep turns these into alerts; the screen renders them as the live picture.
/// </summary>
/// <param name="Kind">The single worst condition this row qualifies for, or null when it is healthy
/// — or, far more often, when nothing has been configured for it at all.</param>
public sealed record ReorderCondition(
    long InventoryId,
    long ProductId,
    long WarehouseId,
    string PartNumber,
    string ProductName,
    string WarehouseName,
    decimal OnHand,
    decimal Available,
    decimal Incoming,
    decimal? MinimumLevel,
    decimal? MaximumLevel,
    decimal ReorderPoint,
    string? Kind,
    decimal ThresholdQuantity,
    decimal ShortfallQuantity)
{
    /// <summary>Available plus committed incoming supply — what the buyer will have if nothing else
    /// is done. This, not on-hand, is what the shortage rungs are measured against.</summary>
    public decimal Projected => Available + Incoming;

    /// <summary>True when at least one level is configured for this row. A row with none is not
    /// healthy and not unhealthy — it is unmeasured, and the screen must say so.</summary>
    public bool IsConfigured => MinimumLevel.HasValue || MaximumLevel.HasValue || ReorderPoint > 0m;
}

/// <summary>What one sweep pass did for one tenant.</summary>
public sealed record ReorderSweepResult(int Raised, int Resolved, int Suppressed);

public interface IReorderAlertService
{
    /// <summary>
    /// The live condition of every stock row in the tenant, whether or not it breaches anything.
    /// Read-only: this is what the levels screen renders.
    /// </summary>
    Task<IReadOnlyList<ReorderCondition>> EvaluateAsync(long businessUnitId, CancellationToken ct = default);

    /// <summary>
    /// Reconciles the alert ledger against the live conditions: raises what is newly breached,
    /// resolves what has recovered, and leaves everything else alone. Returns the alerts RAISED by
    /// this call, so the caller can notify exactly those and no others.
    /// </summary>
    Task<(ReorderSweepResult Summary, IReadOnlyList<InventoryReorderAlert> Raised)> ReconcileAsync(
        long businessUnitId, CancellationToken ct = default);

    /// <summary>Records that a named person has taken an open alert, with their reason.</summary>
    Task<InventoryReorderAlert> AcknowledgeAsync(long businessUnitId, long alertId, uint expectedVersion,
        string actor, string reason, CancellationToken ct = default);
}

/// <summary>
/// FR-INV-04. Turns configured minimum/maximum/reorder levels into an actionable alert ledger.
///
/// <para><b>The whole design question here is what makes an alert worth reading.</b> A signal that
/// fires on everything carries no information: if every stock row alerts on the first sweep, the
/// channel is filtered within a day and every real alert is lost with it. Four rules keep the
/// population small, and each of them is a rule about the BUSINESS, not a threshold on the noise:</para>
///
/// <list type="number">
///   <item><description><b>A level must have been configured.</b> <c>MinimumLevel</c> and
///   <c>MaximumLevel</c> are NULL by default and NULL means "not set"; <c>ReorderPoint</c> is
///   non-nullable, so a non-positive value means the same thing. A tenant that deploys this and
///   configures nothing gets no alerts at all, which is the correct and only safe first-sweep
///   behaviour. This is the same rule <c>SlaSweepWorker</c> applies to a non-positive policy
///   window, for the same reason.</description></item>
///   <item><description><b>Measured on available-to-promise, not on-hand.</b> On-hand counts units
///   that are reserved for somebody else, quarantined, damaged or expired. A warehouse holding 500
///   units of which 500 are on a customer's order can supply nobody, and an alert that reads
///   on-hand would call it healthy. Available-to-promise is what tells you whether you can actually
///   supply, and it is taken from <see cref="InventoryQuantityMath.AvailableToPromise"/> — the one
///   definition — rather than re-derived here.</description></item>
///   <item><description><b>Committed incoming supply suppresses the alert.</b> A row below its
///   minimum with a purchase order already covering the gap is not actionable: the buyer has
///   already acted, and telling them again every half hour until the goods land is exactly how an
///   alert channel is trained to be ignored. The shortage test is therefore
///   <c>available + incoming</c>, and a row rescued by an order in flight is counted as SUPPRESSED
///   rather than raised, so the number is visible instead of being an absence.</description></item>
///   <item><description><b>One alert per row, at the worst rung.</b> A row at zero available is
///   below its minimum and below its reorder point simultaneously. Raising all three would triple
///   the traffic and say nothing extra. The ladder is walked worst-first and stops at the first
///   match; deterioration from one rung to the next resolves the old alert and raises the new one,
///   because "this got worse" is new information and "this is still bad" is not.</description></item>
/// </list>
///
/// <para>Overstock is measured on ON-HAND rather than on the projected figure, and deliberately:
/// maximum level is a statement about capital and shelf space, and units that are reserved for a
/// customer are still occupying the shelf and still on the balance sheet. Measuring overstock on
/// available would declare a full warehouse empty the moment its stock was promised.</para>
/// </summary>
public sealed class ReorderAlertService(ErpRfqAutomationContext db) : IReorderAlertService
{
    private readonly ErpRfqAutomationContext _db = db;

    public async Task<IReadOnlyList<ReorderCondition>> EvaluateAsync(
        long businessUnitId, CancellationToken ct = default)
    {
        if (businessUnitId <= 0) throw new ArgumentOutOfRangeException(nameof(businessUnitId));

        var stock = await (
            from inventory in _db.Set<Models.Inventory>().AsNoTracking()
            join product in _db.Products.AsNoTracking() on inventory.ProductId equals product.Id
            join warehouse in _db.Warehouses.AsNoTracking() on inventory.WarehouseId equals warehouse.Id
            where inventory.Buid == businessUnitId && product.Buid == businessUnitId
                  && warehouse.BusinessUnitId == businessUnitId
            select new
            {
                InventoryId = inventory.Id,
                inventory.QtyOnHand, inventory.AllocatedQuantity, inventory.QuarantineQuantity,
                inventory.DamagedQuantity, inventory.ExpiredQuantity, inventory.SafetyStockQuantity,
                inventory.ReorderPoint, inventory.MinimumLevel, inventory.MaximumLevel,
                ProductId = product.Id, product.PartNo, product.ProductName,
                WarehouseId = warehouse.Id, warehouse.WarehouseName,
            }).ToListAsync(ct);
        if (stock.Count == 0) return [];

        var inventoryIds = stock.Select(x => x.InventoryId).ToArray();
        var reserved = await _db.Set<StockReservation>().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && inventoryIds.Contains(x.InventoryId)
                        && x.Status == StockReservationStatus.Active)
            .GroupBy(x => x.InventoryId)
            .Select(g => new { InventoryId = g.Key, Quantity = g.Sum(x => x.Quantity) })
            .ToDictionaryAsync(x => x.InventoryId, x => x.Quantity, ct);

        // OPEN COMMITTED supply only — the same four statuses the availability screen and the
        // quotation line resolver count. Planned supply is excluded: a plan is not a commitment,
        // and counting it here would suppress the alert that is supposed to cause the plan to
        // become an order.
        var productIds = stock.Select(x => x.ProductId).Distinct().ToArray();
        var incoming = await _db.IncomingInventory.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && productIds.Contains(x.ProductId)
                        && (x.Status == IncomingInventoryStatus.Ordered
                            || x.Status == IncomingInventoryStatus.Confirmed
                            || x.Status == IncomingInventoryStatus.InTransit
                            || x.Status == IncomingInventoryStatus.PartiallyReceived))
            .Select(x => new { x.ProductId, x.WarehouseId, x.OpenQuantity })
            .ToListAsync(ct);

        return stock.Select(row =>
        {
            var held = reserved.GetValueOrDefault(row.InventoryId);
            var available = InventoryQuantityMath.AvailableToPromise(
                row.QtyOnHand, held, row.AllocatedQuantity, row.QuarantineQuantity,
                row.DamagedQuantity, row.ExpiredQuantity, row.SafetyStockQuantity);
            var inbound = incoming
                .Where(x => x.ProductId == row.ProductId && x.WarehouseId == row.WarehouseId)
                .Sum(x => x.OpenQuantity);

            var (kind, threshold, shortfall) = Classify(
                available + inbound, row.QtyOnHand, row.MinimumLevel, row.MaximumLevel, row.ReorderPoint);

            return new ReorderCondition(row.InventoryId, row.ProductId, row.WarehouseId,
                row.PartNo, row.ProductName ?? row.PartNo, row.WarehouseName,
                row.QtyOnHand, available, inbound,
                row.MinimumLevel, row.MaximumLevel, row.ReorderPoint,
                kind, threshold, shortfall);
        }).ToList();
    }

    /// <summary>
    /// The rule, in one place so the screen and the sweep can never disagree about what is short.
    ///
    /// <para>Returns the WORST rung the row qualifies for and nothing else. The ordering is
    /// out-of-stock, then below-minimum, then reorder-point, and it stops at the first match: those
    /// three conditions are nested, not independent, so raising all three would say "short" three
    /// times over.</para>
    ///
    /// <para>A shortfall is always strictly positive — it is what must be bought to get back to the
    /// threshold, or the excess above the maximum. Zero would mean the row is exactly ON its
    /// threshold, which is the healthy state for a maximum and the boundary case for a minimum; the
    /// comparisons below are chosen so that state raises nothing.</para>
    /// </summary>
    internal static (string? Kind, decimal Threshold, decimal Shortfall) Classify(
        decimal projected, decimal onHand, decimal? minimum, decimal? maximum, decimal reorderPoint)
    {
        // Rung 1 and 2 both need a configured minimum. Without one there is no number to be below,
        // and "zero available" on an item nobody has set a minimum for is a perfectly ordinary
        // state for a trading business that buys to order.
        if (minimum is { } min && min > 0m)
        {
            if (projected <= 0m) return (ReorderAlertKinds.OutOfStock, min, min);
            if (projected < min) return (ReorderAlertKinds.BelowMinimum, min, min - projected);
        }

        // Rung 3. Non-positive is "not configured", never "alert instantly" — the same reading
        // SlaSweepWorker gives a non-positive policy window, and for the same reason: a zero read
        // as a threshold escalates the entire book on the first sweep.
        //
        // "At or below" rather than "below", matching the exception list the overview screen has
        // always drawn. Hitting the reorder point exactly is the moment the reorder point exists to
        // name, so its shortfall is legitimately zero — that is why the quantity CHECK on this
        // table permits zero here and the shortage rungs above it never produce one.
        if (reorderPoint > 0m && projected <= reorderPoint)
            return (ReorderAlertKinds.ReorderPoint, reorderPoint, Math.Max(reorderPoint - projected, 0m));

        // Overstock is measured on physical units, not on what can be promised: a maximum level is
        // about capital and shelf space, and stock already promised to a customer is still on both.
        if (maximum is { } max && max > 0m && onHand > max)
            return (ReorderAlertKinds.Overstock, max, onHand - max);

        return (null, 0m, 0m);
    }

    public async Task<(ReorderSweepResult Summary, IReadOnlyList<InventoryReorderAlert> Raised)>
        ReconcileAsync(long businessUnitId, CancellationToken ct = default)
    {
        var conditions = await EvaluateAsync(businessUnitId, ct);

        // Suppressed = would be short on what it can promise today, but a purchase order already
        // in flight covers it. Counted rather than merely skipped: "we chose not to tell you about
        // 40 rows" is information, and an absence is not.
        var suppressed = conditions.Count(x => x.Kind is null
                                               && x.Incoming > 0m
                                               && x.MinimumLevel is { } min && min > 0m
                                               && x.Available < min);

        var live = await _db.Set<InventoryReorderAlert>()
            .Where(x => x.BusinessUnitId == businessUnitId
                        && ReorderAlertStatuses.LiveForQuery.Contains(x.Status))
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        var raised = new List<InventoryReorderAlert>();
        var resolved = 0;

        var byInventory = conditions.ToDictionary(x => x.InventoryId);

        // 1. Resolve every live alert whose condition is no longer the one that raised it. That
        //    covers recovery, a level being changed, the row being emptied, AND deterioration to a
        //    worse rung — in the last case the new rung is raised below in the same pass.
        foreach (var alert in live)
        {
            var condition = byInventory.GetValueOrDefault(alert.InventoryId);
            if (condition is not null && condition.Kind == alert.Kind) continue;

            alert.Status = ReorderAlertStatuses.Resolved;
            alert.ResolvedOn = now;
            alert.ResolutionReason = condition is null
                ? "Stock row no longer readable"
                : condition.Kind is null
                    ? "Stock recovered above the configured level"
                    : $"Superseded by {condition.Kind}";
            resolved++;
        }

        var stillLive = live
            .Where(x => ReorderAlertStatuses.IsLive(x.Status))
            .Select(x => (x.InventoryId, x.Kind))
            .ToHashSet();

        // 2. Raise what is breached and not already live.
        foreach (var condition in conditions)
        {
            if (condition.Kind is null) continue;
            if (stillLive.Contains((condition.InventoryId, condition.Kind))) continue;

            var alert = new InventoryReorderAlert
            {
                BusinessUnitId = businessUnitId,
                InventoryId = condition.InventoryId,
                ProductId = condition.ProductId,
                WarehouseId = condition.WarehouseId,
                Kind = condition.Kind,
                Severity = ReorderAlertKinds.SeverityOf(condition.Kind),
                Status = ReorderAlertStatuses.Open,
                OnHandQuantity = condition.OnHand,
                AvailableQuantity = condition.Available,
                IncomingQuantity = condition.Incoming,
                ProjectedQuantity = condition.Projected,
                ThresholdQuantity = condition.ThresholdQuantity,
                ShortfallQuantity = condition.ShortfallQuantity,
                RaisedOn = now,
                CreatedOn = now,
            };
            _db.Set<InventoryReorderAlert>().Add(alert);
            raised.Add(alert);
        }

        if (resolved > 0 || raised.Count > 0)
        {
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                // Another instance of the sweep raised the same alert between our read and our
                // write. The partial unique index is the authority; drop our copies and let the
                // winner's alert stand, exactly as the SLA claim does on a 23505.
                foreach (var alert in raised) _db.Entry(alert).State = EntityState.Detached;
                raised.Clear();
                await _db.SaveChangesAsync(ct);
            }
        }

        return (new ReorderSweepResult(raised.Count, resolved, suppressed), raised);
    }

    public async Task<InventoryReorderAlert> AcknowledgeAsync(long businessUnitId, long alertId,
        uint expectedVersion, string actor, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actor))
            throw new ArgumentException("An authenticated actor is required.", nameof(actor));
        // An acknowledgement with no reason is a mute button with nobody's name on it, and a mute
        // button is how an alert channel dies quietly.
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException(
                "An acknowledgement must record why the alert is being taken.", nameof(reason));

        var alert = await _db.Set<InventoryReorderAlert>()
            .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == alertId, ct)
            ?? throw new KeyNotFoundException("Reorder alert was not found in this business unit.");

        if (alert.Status == ReorderAlertStatuses.Resolved)
            throw new ReorderAlertException(
                "This alert has already been resolved because the stock recovered; there is nothing to acknowledge.");
        if (alert.Status == ReorderAlertStatuses.Acknowledged)
            throw new ReorderAlertException(
                $"This alert was already acknowledged by {alert.AcknowledgedBy} on {alert.AcknowledgedOn:dd MMM yyyy HH:mm} UTC.");

        _db.Entry(alert).Property(x => x.Version).OriginalValue = expectedVersion;
        alert.Status = ReorderAlertStatuses.Acknowledged;
        alert.AcknowledgedOn = DateTime.UtcNow;
        alert.AcknowledgedBy = actor;
        alert.AcknowledgementReason = reason.Trim();
        await _db.SaveChangesAsync(ct);
        return alert;
    }

    /// <summary>PostgreSQL 23505 and its SQLite equivalent, without a compile-time dependency on
    /// either provider's exception type. Mirrors <c>SlaSweepWorker.IsUniqueViolation</c>.</summary>
    private static bool IsUniqueViolation(DbUpdateException exception)
    {
        for (Exception? error = exception; error is not null; error = error.InnerException)
        {
            if (error is System.Data.Common.DbException dbError
                && string.Equals(dbError.SqlState, "23505", StringComparison.Ordinal))
                return true;
            if (error.Message.Contains("duplicate key value violates unique constraint", StringComparison.OrdinalIgnoreCase)
                || error.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
