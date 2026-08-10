using ERP_RFQ_Automation.Inventory.Commercial;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using System.Data;

namespace ERP_RFQ_Automation.Inventory;

/// <summary>Raised when a stock write would break an inventory invariant.</summary>
public sealed class StockLedgerException(string message) : InvalidOperationException(message);

/// <summary>The state of one inventory row after a ledger write.</summary>
/// <param name="BookQuantity">
/// FR-INV-05. What the system believed was on hand immediately BEFORE this write. Null for writes
/// that are not a count.
/// </param>
/// <param name="CountedQuantity">What the counter physically found. Null unless this was a count.</param>
public sealed record StockLedgerResult(
    long InventoryId,
    long ProductId,
    long WarehouseId,
    decimal OnHand,
    decimal Quarantine,
    decimal Damaged,
    decimal Expired,
    decimal SafetyStock,
    long? MovementId,
    decimal? BookQuantity = null,
    decimal? CountedQuantity = null)
{
    /// <summary>
    /// FR-INV-05. Counted minus book: positive means more stock was found than the system knew
    /// about, negative means stock is missing.
    ///
    /// <para><b>Why this is returned and not merely implied.</b> <c>RecordCountAsync</c> computed
    /// exactly this number in order to size the adjustment and then threw it away — the only
    /// surviving trace was the quantity on an <c>AdjustmentIncrease</c>/<c>AdjustmentDecrease</c>
    /// movement carrying the free-text reason "Physical count", indistinguishable from any other
    /// adjustment. A stock take whose variance cannot be reported is a stock take that has not been
    /// done, which is the whole of FR-INV-05's second clause.</para>
    /// </summary>
    public decimal? Variance => CountedQuantity.HasValue && BookQuantity.HasValue
        ? CountedQuantity.Value - BookQuantity.Value
        : null;

    /// <summary>True when the count agreed with the book value. A null variance is not agreement.</summary>
    public bool CountAgreed => Variance == 0m;
}

/// <summary>
/// FR-INV-05. One counted line in the variance report: what the book said, what the counter found,
/// and the gap between them.
/// </summary>
public sealed record StockCountVariance(
    long InventoryId,
    long ProductId,
    string PartNumber,
    string ProductName,
    long WarehouseId,
    string WarehouseName,
    decimal BookQuantity,
    decimal CountedQuantity,
    DateTime CountedOn,
    string CountedBy,
    string? Reason)
{
    public decimal Variance => CountedQuantity - BookQuantity;

    /// <summary>
    /// Variance as a share of the book value. Null when the book value was zero — a count that
    /// finds 5 units where the system knew of none is an infinite percentage, and reporting it as
    /// 0% or as 500% would both be inventions. The absolute variance is the honest number there.
    /// </summary>
    public decimal? VariancePercent => BookQuantity == 0m
        ? null
        : Math.Round(Variance / BookQuantity * 100m, 2);
}

/// <summary>
/// FR-INV-06. One inventory row's ageing position: how long since anything physically moved, and
/// which slow-moving/obsolete band that puts it in.
/// </summary>
public sealed record StockAgeingRow(
    long InventoryId,
    long ProductId,
    string PartNumber,
    string ProductName,
    long WarehouseId,
    string WarehouseName,
    decimal OnHand,
    decimal UnitCost,
    DateTime? LastReceiptOn,
    DateTime? LastIssueOn,
    int? DaysSinceLastIssue,
    int? DaysSinceLastReceipt,
    string Band)
{
    /// <summary>Capital tied up in this row at its recorded unit cost.</summary>
    public decimal CarryingValue => Math.Round(OnHand * UnitCost, 2);
}

/// <summary>
/// FR-INV-06. The ageing bands.
///
/// <para><b>Measured from the last ISSUE, not the last receipt.</b> A line that is received every
/// month and never sold is the definition of obsolete, and receipt-based ageing would report it as
/// the freshest stock in the warehouse. Where a row has never issued at all, its first receipt is
/// the clock — otherwise stock that has sat untouched since the day it arrived would have no age
/// at all and disappear from the report that exists to find it.</para>
/// </summary>
public static class StockAgeingBands
{
    public const string Current = "CURRENT";       // moved within 90 days
    public const string Slow = "SLOW_MOVING";      // 90–180 days
    public const string VerySlow = "VERY_SLOW";    // 180–365 days
    public const string Obsolete = "OBSOLETE";     // over a year
    /// <summary>Stock is present but the ledger holds no movement to date it from.</summary>
    public const string Undated = "UNDATED";

    public const int SlowAfterDays = 90;
    public const int VerySlowAfterDays = 180;
    public const int ObsoleteAfterDays = 365;

    public static string For(int? daysSinceMovement) => daysSinceMovement switch
    {
        null => Undated,
        < SlowAfterDays => Current,
        < VerySlowAfterDays => Slow,
        < ObsoleteAfterDays => VerySlow,
        _ => Obsolete,
    };
}

public interface IStockLedgerService
{
    /// <summary>
    /// Sets the counted physical quantity for one product in one warehouse and posts the
    /// balancing adjustment movement. This is how opening stock and cycle counts enter the
    /// ledger — previously the ONLY way an inventory row could be created was issuing a
    /// supplier purchase order, so a client migrating their existing stock had no route in
    /// and every warehouse view rendered empty.
    /// </summary>
    Task<StockLedgerResult> RecordCountAsync(long businessUnitId, long productId, long warehouseId,
        decimal countedQuantity, string idempotencyKey, string actor, string? reason = null,
        CancellationToken ct = default);

    /// <summary>Applies a signed delta to on-hand and posts the matching adjustment movement.</summary>
    Task<StockLedgerResult> AdjustAsync(long businessUnitId, long productId, long warehouseId,
        decimal delta, string idempotencyKey, string actor, string? reason = null,
        CancellationToken ct = default);

    /// <summary>
    /// Reclassifies sellable stock into (or out of) a non-sellable bucket. On-hand does not
    /// move — the units are still in the building — but available-to-promise does.
    /// </summary>
    Task<StockLedgerResult> ReclassifyAsync(long businessUnitId, long productId, long warehouseId,
        StockBucket bucket, decimal quantity, string idempotencyKey, string actor, string? reason = null,
        CancellationToken ct = default);

    /// <summary>Sets the protected safety-stock buffer. A policy value, so it posts no movement.</summary>
    Task<StockLedgerResult> SetSafetyStockAsync(long businessUnitId, long productId, long warehouseId,
        decimal safetyStock, string actor, CancellationToken ct = default);

    /// <summary>
    /// Moves physical stock between two warehouses of the same tenant as one atomic pair of
    /// TransferOut/TransferIn movements. Without this, warehouses are decorative: stock could
    /// only ever enter the warehouse its purchase order named.
    /// </summary>
    Task<(StockLedgerResult From, StockLedgerResult To)> TransferAsync(long businessUnitId, long productId,
        long fromWarehouseId, long toWarehouseId, decimal quantity, string idempotencyKey, string actor,
        string? reason = null, CancellationToken ct = default);

    /// <summary>
    /// FR-INV-05. The variance report for counts posted in a date window: book value, counted
    /// value and the gap, per counted inventory row.
    ///
    /// <para>Driven off the count movements themselves rather than a separate stock-take table.
    /// The movement is written in the same transaction as the balance change and is append-only,
    /// so it cannot disagree with the stock it explains — whereas a parallel count-session table
    /// would be a second record of the same event with its own way of going stale. What that costs
    /// is named in the module report: there is no count SHEET, so a count is a single line, and
    /// blind/second-count workflow is not modelled.</para>
    /// </summary>
    Task<IReadOnlyList<StockCountVariance>> GetCountVarianceAsync(long businessUnitId,
        DateTime? from = null, DateTime? to = null, bool varianceOnly = true, CancellationToken ct = default);

    /// <summary>
    /// FR-INV-06. Stock ageing for slow-moving and obsolete identification, aged from the last
    /// physical issue. See <see cref="StockAgeingBands"/> for why it is issue-dated.
    /// </summary>
    Task<IReadOnlyList<StockAgeingRow>> GetStockAgeingAsync(long businessUnitId,
        long? warehouseId = null, string? band = null, CancellationToken ct = default);
}

/// <summary>The non-sellable buckets that reclassification can move stock into.</summary>
public enum StockBucket
{
    /// <summary>Held pending inspection. Positive quantity quarantines, negative releases.</summary>
    Quarantine,
    /// <summary>Written down as damaged.</summary>
    Damaged,
    /// <summary>Past its expiry date.</summary>
    Expired,
}

/// <summary>
/// The authoritative write path for physical stock. Every mutation is tenant-scoped, taken under
/// the same advisory lock the reservation engine uses, idempotent on a caller-supplied key, and
/// posts an <see cref="InventoryMovement"/> in the same transaction as the balance change — so
/// <c>Inventory.QtyOnHand</c> can never drift from the sum of its movements.
///
/// <para><b>Ledger authority.</b> <c>Inventory.QtyOnHand</c> (per product per warehouse) is the
/// authoritative physical balance. <c>Product.QtyOnHand</c> is a legacy denormalised column that
/// several older paths still write; it is NOT authoritative and must not drive availability. See
/// the module report for the remaining callers that need migrating.</para>
/// </summary>
public sealed class StockLedgerService(ErpRfqAutomationContext db) : IStockLedgerService
{
    private readonly ErpRfqAutomationContext _db = db;

    /// <summary>The source stamped on a count's balancing movement. See <see cref="CountSourceType"/>.</summary>
    internal const string CountSourceType = "StockCount";

    public async Task<StockLedgerResult> RecordCountAsync(long businessUnitId, long productId, long warehouseId,
        decimal countedQuantity, string idempotencyKey, string actor, string? reason = null,
        CancellationToken ct = default)
    {
        if (countedQuantity < 0m)
            throw new ArgumentOutOfRangeException(nameof(countedQuantity), "A counted quantity cannot be negative.");

        // FR-INV-05. The book value is captured BEFORE the mutation, because after it the balance
        // is the counted quantity and the variance is unrecoverable — which is exactly why this
        // number used to be computed, used to size the adjustment, and then lost. It is carried
        // out on the result and stamped on the movement, so the variance report can be rebuilt
        // from the ledger rather than depending on someone having kept the response.
        var book = 0m;
        var result = await WriteAsync(businessUnitId, productId, warehouseId, idempotencyKey, actor,
            CountSourceType, ct,
            inventory =>
            {
                book = inventory.QtyOnHand;
                return ApplyOnHandDelta(inventory, countedQuantity - inventory.QtyOnHand,
                    CountReason(book, countedQuantity, reason), actor);
            });

        // A count that matches the book value posts no movement (the delta is zero), so there is
        // no ledger row to read the book value back from. Reporting a zero variance is still the
        // truth about the count, and it is the one the counter needs to see.
        return result with { BookQuantity = book, CountedQuantity = countedQuantity };
    }

    /// <summary>
    /// The movement reason for a count. The book and counted figures are written into it verbatim
    /// so the variance is reconstructable from the append-only ledger alone — the movement quantity
    /// carries the absolute delta but not its sign relative to what was expected, and the
    /// <c>Inventory</c> balance has already moved on by the time anyone reads it.
    /// </summary>
    private static string CountReason(decimal book, decimal counted, string? reason)
        => $"Physical count: book {book}, counted {counted}"
           + (string.IsNullOrWhiteSpace(reason) ? "" : $" — {reason.Trim()}");

    public Task<StockLedgerResult> AdjustAsync(long businessUnitId, long productId, long warehouseId,
        decimal delta, string idempotencyKey, string actor, string? reason = null,
        CancellationToken ct = default)
    {
        if (delta == 0m)
            throw new ArgumentOutOfRangeException(nameof(delta), "A stock adjustment must be non-zero.");
        return WriteAsync(businessUnitId, productId, warehouseId, idempotencyKey, actor, ct,
            inventory => ApplyOnHandDelta(inventory, delta, reason ?? "Stock adjustment", actor));
    }

    public Task<StockLedgerResult> ReclassifyAsync(long businessUnitId, long productId, long warehouseId,
        StockBucket bucket, decimal quantity, string idempotencyKey, string actor, string? reason = null,
        CancellationToken ct = default)
    {
        if (quantity == 0m)
            throw new ArgumentOutOfRangeException(nameof(quantity), "A reclassification must be non-zero.");
        if (bucket is not (StockBucket.Quarantine or StockBucket.Damaged or StockBucket.Expired))
            throw new ArgumentOutOfRangeException(nameof(bucket));
        return WriteAsync(businessUnitId, productId, warehouseId, idempotencyKey, actor, ct,
            inventory =>
            {
                var current = bucket switch
                {
                    StockBucket.Quarantine => inventory.QuarantineQuantity,
                    StockBucket.Damaged => inventory.DamagedQuantity,
                    _ => inventory.ExpiredQuantity,
                };
                var updated = current + quantity;
                if (updated < 0m)
                    throw new StockLedgerException(
                        $"Cannot release {-quantity} from the {bucket} bucket: only {current} is held there.");

                switch (bucket)
                {
                    case StockBucket.Quarantine: inventory.QuarantineQuantity = updated; break;
                    case StockBucket.Damaged: inventory.DamagedQuantity = updated; break;
                    default: inventory.ExpiredQuantity = updated; break;
                }

                // Reclassification never moves physical units, so the movement is on-hand neutral
                // (InventoryQuantityMath.OnHandDelta returns 0 for all four types).
                var type = bucket switch
                {
                    StockBucket.Quarantine => quantity > 0m
                        ? InventoryMovementType.Quarantine
                        : InventoryMovementType.QuarantineRelease,
                    StockBucket.Damaged => InventoryMovementType.Damage,
                    _ => InventoryMovementType.Expiration,
                };
                return (type, Math.Abs(quantity), reason ?? $"{bucket} reclassification");
            });
    }

    public async Task<StockLedgerResult> SetSafetyStockAsync(long businessUnitId, long productId,
        long warehouseId, decimal safetyStock, string actor, CancellationToken ct = default)
    {
        if (safetyStock < 0m)
            throw new ArgumentOutOfRangeException(nameof(safetyStock), "Safety stock cannot be negative.");
        RequireActor(actor);
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(Isolation(), ct);
            var inventory = await ResolveInventoryAsync(businessUnitId, productId, warehouseId, actor, ct);
            await LockAsync(InventoryAvailabilityService.InventoryLock(businessUnitId, inventory.Id), ct);
            inventory.SafetyStockQuantity = safetyStock;
            Touch(inventory, actor);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Snapshot(inventory, null);
        });
    }

    public async Task<(StockLedgerResult From, StockLedgerResult To)> TransferAsync(long businessUnitId,
        long productId, long fromWarehouseId, long toWarehouseId, decimal quantity, string idempotencyKey,
        string actor, string? reason = null, CancellationToken ct = default)
    {
        if (quantity <= 0m)
            throw new ArgumentOutOfRangeException(nameof(quantity), "A transfer quantity must be positive.");
        if (fromWarehouseId == toWarehouseId)
            throw new StockLedgerException("A transfer must move stock between two different warehouses.");
        RequireActor(actor);
        RequireKey(idempotencyKey);

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(Isolation(), ct);

            var source = await ResolveInventoryAsync(businessUnitId, productId, fromWarehouseId, actor, ct);
            var destination = await ResolveInventoryAsync(businessUnitId, productId, toWarehouseId, actor, ct);
            // Deterministic lock ordering: two transfers moving stock in opposite directions
            // between the same warehouses would otherwise deadlock against each other.
            foreach (var id in new[] { source.Id, destination.Id }.OrderBy(x => x))
                await LockAsync(InventoryAvailabilityService.InventoryLock(businessUnitId, id), ct);

            var outKey = $"{idempotencyKey.Trim()}:out";
            var replay = await FindMovementAsync(businessUnitId, outKey, ct);
            if (replay is not null)
            {
                await tx.CommitAsync(ct);
                return (Snapshot(source, replay.Id), Snapshot(destination, null));
            }

            var reserved = await ActiveReservedAsync(businessUnitId, source.Id, ct);
            source.QtyOnHand -= quantity;
            EnsureInvariants(source, reserved);
            destination.QtyOnHand += quantity;
            Touch(source, actor);
            Touch(destination, actor);

            var now = DateTime.UtcNow;
            var text = reason ?? $"Warehouse transfer {fromWarehouseId} to {toWarehouseId}";
            var outbound = NewMovement(source, InventoryMovementType.TransferOut, quantity, outKey,
                "StockTransfer", idempotencyKey.Trim(), text, actor, now);
            var inbound = NewMovement(destination, InventoryMovementType.TransferIn, quantity,
                $"{idempotencyKey.Trim()}:in", "StockTransfer", idempotencyKey.Trim(), text, actor, now);
            _db.InventoryMovements.Add(outbound);
            _db.InventoryMovements.Add(inbound);

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return (Snapshot(source, outbound.Id), Snapshot(destination, inbound.Id));
        });
    }

    public async Task<IReadOnlyList<StockCountVariance>> GetCountVarianceAsync(long businessUnitId,
        DateTime? from = null, DateTime? to = null, bool varianceOnly = true, CancellationToken ct = default)
    {
        if (businessUnitId <= 0) throw new ArgumentOutOfRangeException(nameof(businessUnitId));

        var rows = await (
            from movement in _db.InventoryMovements.AsNoTracking()
            join inventory in _db.Set<Models.Inventory>().AsNoTracking()
                on movement.InventoryId equals inventory.Id
            join product in _db.Products.AsNoTracking() on movement.ProductId equals product.Id into products
            from product in products.DefaultIfEmpty()
            join warehouse in _db.Warehouses.AsNoTracking() on movement.WarehouseId equals warehouse.Id into warehouses
            from warehouse in warehouses.DefaultIfEmpty()
            where movement.BusinessUnitId == businessUnitId && inventory.Buid == businessUnitId
                  && movement.SourceType == CountSourceType
                  && (from == null || movement.OccurredOn >= from)
                  && (to == null || movement.OccurredOn <= to)
            orderby movement.OccurredOn descending, movement.Id descending
            select new
            {
                movement.InventoryId, movement.ProductId, movement.WarehouseId, movement.Type,
                movement.Quantity, movement.OccurredOn, movement.CreatedBy, movement.Reason,
                PartNumber = product != null ? product.PartNo : inventory.PartNo,
                ProductName = product != null ? product.ProductName : inventory.ProductName,
                WarehouseName = warehouse != null ? warehouse.WarehouseName : null,
            }).ToListAsync(ct);

        return rows.Select(x =>
        {
            // The signed variance is the movement's on-hand effect: an AdjustmentIncrease means
            // the counter found more than the book said. Derived through InventoryQuantityMath so
            // the sign convention cannot drift from the one the reconciliation uses.
            var variance = InventoryQuantityMath.OnHandDelta(x.Type) * x.Quantity;
            var (book, counted) = ParseCountReason(x.Reason, variance);
            return new StockCountVariance(x.InventoryId, x.ProductId, x.PartNumber ?? "",
                x.ProductName ?? x.PartNumber ?? "", x.WarehouseId, x.WarehouseName ?? "Unassigned",
                book, counted, x.OccurredOn, x.CreatedBy, x.Reason);
        })
        .Where(x => !varianceOnly || x.Variance != 0m)
        .ToList();
    }

    /// <summary>
    /// Recovers the book and counted figures from the reason text the count wrote, falling back to
    /// the signed variance alone when the text is not in the expected shape.
    ///
    /// <para>The fallback matters: counts posted before this gate carry the bare reason "Physical
    /// count", so their book value is genuinely unrecoverable. It reports book 0 and counted =
    /// variance, which keeps the VARIANCE — the number the report exists for — exactly right, and
    /// leaves the two absolute figures visibly implausible rather than quietly wrong.</para>
    /// </summary>
    private static (decimal Book, decimal Counted) ParseCountReason(string? reason, decimal variance)
    {
        const string bookMarker = "book ";
        const string countedMarker = ", counted ";
        if (reason is not null)
        {
            var bookAt = reason.IndexOf(bookMarker, StringComparison.Ordinal);
            var countedAt = reason.IndexOf(countedMarker, StringComparison.Ordinal);
            if (bookAt >= 0 && countedAt > bookAt)
            {
                var bookText = reason[(bookAt + bookMarker.Length)..countedAt];
                var rest = reason[(countedAt + countedMarker.Length)..];
                var end = rest.IndexOf(' ');
                var countedText = end < 0 ? rest : rest[..end];
                if (decimal.TryParse(bookText, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var book)
                    && decimal.TryParse(countedText, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var counted))
                    return (book, counted);
            }
        }
        return (0m, variance);
    }

    public async Task<IReadOnlyList<StockAgeingRow>> GetStockAgeingAsync(long businessUnitId,
        long? warehouseId = null, string? band = null, CancellationToken ct = default)
    {
        if (businessUnitId <= 0) throw new ArgumentOutOfRangeException(nameof(businessUnitId));

        var stock = await (
            from inventory in _db.Set<Models.Inventory>().AsNoTracking()
            join product in _db.Products.AsNoTracking() on inventory.ProductId equals product.Id into products
            from product in products.DefaultIfEmpty()
            join warehouse in _db.Warehouses.AsNoTracking() on inventory.WarehouseId equals warehouse.Id into warehouses
            from warehouse in warehouses.DefaultIfEmpty()
            where inventory.Buid == businessUnitId
                  && (warehouseId == null || inventory.WarehouseId == warehouseId)
                  // Ageing describes stock that is SITTING there. A row at zero on-hand is not
                  // slow-moving, it is empty, and including it would bury the rows that matter
                  // under every SKU the tenant has ever stocked.
                  && inventory.QtyOnHand > 0m
            select new
            {
                inventory.Id, inventory.ProductId, inventory.WarehouseId, inventory.QtyOnHand,
                inventory.UnitCost,
                PartNumber = product != null ? product.PartNo : inventory.PartNo,
                ProductName = product != null ? product.ProductName : inventory.ProductName,
                WarehouseName = warehouse != null ? warehouse.WarehouseName : null,
            }).ToListAsync(ct);
        if (stock.Count == 0) return [];

        var inventoryIds = stock.Select(x => x.Id).ToArray();
        var movements = await _db.InventoryMovements.AsNoTracking()
            .Where(m => m.BusinessUnitId == businessUnitId && inventoryIds.Contains(m.InventoryId)
                        && (m.Type == InventoryMovementType.Issue
                            || m.Type == InventoryMovementType.TransferOut
                            || m.Type == InventoryMovementType.Receipt
                            || m.Type == InventoryMovementType.TransferIn))
            .GroupBy(m => new { m.InventoryId, m.Type })
            .Select(g => new { g.Key.InventoryId, g.Key.Type, LastOn = g.Max(m => m.OccurredOn) })
            .ToListAsync(ct);

        var lastIssue = movements
            .Where(x => x.Type is InventoryMovementType.Issue or InventoryMovementType.TransferOut)
            .GroupBy(x => x.InventoryId).ToDictionary(g => g.Key, g => g.Max(x => x.LastOn));
        var lastReceipt = movements
            .Where(x => x.Type is InventoryMovementType.Receipt or InventoryMovementType.TransferIn)
            .GroupBy(x => x.InventoryId).ToDictionary(g => g.Key, g => g.Max(x => x.LastOn));

        var now = DateTime.UtcNow;
        var rows = stock.Select(x =>
        {
            var issuedOn = lastIssue.TryGetValue(x.Id, out var issue) ? issue : (DateTime?)null;
            var receivedOn = lastReceipt.TryGetValue(x.Id, out var receipt) ? receipt : (DateTime?)null;
            // Aged from the last issue; where nothing has ever issued, from the first arrival.
            // A row with neither is UNDATED rather than being given a fabricated age.
            var clock = issuedOn ?? receivedOn;
            var days = clock.HasValue ? (int)Math.Floor((now - clock.Value).TotalDays) : (int?)null;
            return new StockAgeingRow(x.Id, x.ProductId ?? 0, x.PartNumber ?? "",
                x.ProductName ?? x.PartNumber ?? "", x.WarehouseId ?? 0, x.WarehouseName ?? "Unassigned",
                x.QtyOnHand, x.UnitCost ?? 0m, receivedOn, issuedOn,
                issuedOn.HasValue ? (int)Math.Floor((now - issuedOn.Value).TotalDays) : null,
                receivedOn.HasValue ? (int)Math.Floor((now - receivedOn.Value).TotalDays) : null,
                StockAgeingBands.For(days));
        });

        if (!string.IsNullOrWhiteSpace(band))
        {
            var wanted = band.Trim().ToUpperInvariant();
            rows = rows.Where(x => x.Band == wanted);
        }

        return rows
            .OrderByDescending(x => x.DaysSinceLastIssue ?? x.DaysSinceLastReceipt ?? int.MaxValue)
            .ThenByDescending(x => x.CarryingValue)
            .ToList();
    }

    /// <summary>
    /// Shared write pipeline: transaction, advisory lock, idempotency replay check, mutation,
    /// invariant enforcement and movement posting — in that order, all in one transaction.
    ///
    /// <para>When the caller already holds a transaction the pipeline joins it rather than opening a
    /// second one. That is what lets a lot quarantine (FR-MTR-05) reclassify stock through this
    /// service — the sanctioned bucket writer — inside the same unit of work that flips the lot's
    /// own status. Writing <c>QuarantineQuantity</c> directly from the traceability module would
    /// have been the alternative, and it would have produced a second, unbalanced writer of the
    /// exact column this class exists to keep reconcilable against the movement ledger. Same guard,
    /// same reasoning, as <c>InventoryAvailabilityService.ReserveAsync</c>.</para>
    /// </summary>
    private Task<StockLedgerResult> WriteAsync(long businessUnitId, long productId, long warehouseId,
        string idempotencyKey, string actor, CancellationToken ct,
        Func<Models.Inventory, (InventoryMovementType Type, decimal Quantity, string Reason)?> mutate)
        => WriteAsync(businessUnitId, productId, warehouseId, idempotencyKey, actor, "StockLedger", ct, mutate);

    private async Task<StockLedgerResult> WriteAsync(long businessUnitId, long productId, long warehouseId,
        string idempotencyKey, string actor, string sourceType, CancellationToken ct,
        Func<Models.Inventory, (InventoryMovementType Type, decimal Quantity, string Reason)?> mutate)
    {
        RequireActor(actor);
        RequireKey(idempotencyKey);
        var key = idempotencyKey.Trim();

        async Task<StockLedgerResult> ApplyAsync()
        {
            var inventory = await ResolveInventoryAsync(businessUnitId, productId, warehouseId, actor, ct);
            await LockAsync(InventoryAvailabilityService.InventoryLock(businessUnitId, inventory.Id), ct);

            var replay = await FindMovementAsync(businessUnitId, key, ct);
            if (replay is not null)
                return Snapshot(inventory, replay.Id);

            var posting = mutate(inventory);
            if (posting is null)
                return Snapshot(inventory, null);

            var reserved = await ActiveReservedAsync(businessUnitId, inventory.Id, ct);
            EnsureInvariants(inventory, reserved);
            Touch(inventory, actor);

            var movement = NewMovement(inventory, posting.Value.Type, posting.Value.Quantity, key,
                sourceType, key, posting.Value.Reason, actor, DateTime.UtcNow);
            _db.InventoryMovements.Add(movement);
            await _db.SaveChangesAsync(ct);
            return Snapshot(inventory, movement.Id);
        }

        if (_db.Database.CurrentTransaction is not null)
            return await ApplyAsync();

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(Isolation(), ct);
            var result = await ApplyAsync();
            await tx.CommitAsync(ct);
            return result;
        });
    }

    private static (InventoryMovementType, decimal, string)? ApplyOnHandDelta(
        Models.Inventory inventory, decimal delta, string reason, string actor)
    {
        _ = actor;
        if (delta == 0m) return null; // a count that matches the book value posts nothing
        inventory.QtyOnHand += delta;
        return (delta > 0m ? InventoryMovementType.AdjustmentIncrease : InventoryMovementType.AdjustmentDecrease,
            Math.Abs(delta), reason);
    }

    /// <summary>
    /// The invariants that make stock figures trustworthy: physical stock is never negative, and
    /// the non-sellable plus committed buckets can never exceed what is physically present.
    /// Safety stock is deliberately excluded — it is a policy buffer that may legitimately be set
    /// above the current balance to signal a shortage.
    /// </summary>
    private static void EnsureInvariants(Models.Inventory inventory, decimal reserved)
    {
        if (inventory.QtyOnHand < 0m)
            throw new StockLedgerException(
                $"The change would drive on-hand stock for inventory {inventory.Id} negative ({inventory.QtyOnHand}).");
        var committed = reserved + inventory.AllocatedQuantity + inventory.QuarantineQuantity
            + inventory.DamagedQuantity + inventory.ExpiredQuantity;
        if (committed > inventory.QtyOnHand)
            throw new StockLedgerException(
                $"The change would leave {committed} committed or non-sellable units against only "
                + $"{inventory.QtyOnHand} on hand for inventory {inventory.Id}.");
    }

    /// <summary>
    /// Finds the tenant's inventory row for a product/warehouse pair, creating it on first use.
    /// Both the product and the warehouse are re-read under the caller's tenant, so a caller can
    /// never attach stock to another tenant's product or warehouse by passing its id.
    /// </summary>
    private async Task<Models.Inventory> ResolveInventoryAsync(long businessUnitId, long productId,
        long warehouseId, string actor, CancellationToken ct)
    {
        if (businessUnitId <= 0) throw new ArgumentOutOfRangeException(nameof(businessUnitId));

        var existing = await _db.Set<Models.Inventory>().SingleOrDefaultAsync(
            x => x.Buid == businessUnitId && x.ProductId == productId && x.WarehouseId == warehouseId, ct);
        if (existing is not null) return existing;

        var product = await _db.Products.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == productId && x.Buid == businessUnitId, ct)
            ?? throw new KeyNotFoundException($"Product {productId} was not found in this tenant.");
        var warehouseExists = await _db.Warehouses.AsNoTracking()
            .AnyAsync(x => x.Id == warehouseId && x.BusinessUnitId == businessUnitId, ct);
        if (!warehouseExists)
            throw new KeyNotFoundException($"Warehouse {warehouseId} was not found in this tenant.");

        var created = new Models.Inventory
        {
            Buid = businessUnitId,
            ProductId = product.Id,
            WarehouseId = warehouseId,
            PartNo = product.PartNo,
            ProductName = product.ProductName,
            Description = product.Description,
            QtyOnHand = 0m,
            ReorderPoint = product.ReorderPoint,
            UnitCost = product.UnitCost,
            SellingPrice = product.SellingPrice,
            CreatedBy = actor,
            CreatedOn = DateTime.UtcNow,
        };
        _db.Set<Models.Inventory>().Add(created);
        await _db.SaveChangesAsync(ct);
        return created;
    }

    private Task<InventoryMovement?> FindMovementAsync(long businessUnitId, string key, CancellationToken ct)
        => _db.InventoryMovements.AsNoTracking()
            .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.IdempotencyKey == key, ct)!;

    private async Task<decimal> ActiveReservedAsync(long businessUnitId, long inventoryId, CancellationToken ct)
        => await _db.Set<StockReservation>().AsNoTracking()
            .Where(r => r.BusinessUnitId == businessUnitId && r.InventoryId == inventoryId
                        && r.Status == StockReservationStatus.Active)
            .SumAsync(r => (decimal?)r.Quantity, ct) ?? 0m;

    private static InventoryMovement NewMovement(Models.Inventory inventory, InventoryMovementType type,
        decimal quantity, string idempotencyKey, string sourceType, string sourceId, string reason,
        string actor, DateTime now) => new()
        {
            BusinessUnitId = inventory.Buid!.Value,
            ProductId = inventory.ProductId!.Value,
            InventoryId = inventory.Id,
            WarehouseId = inventory.WarehouseId!.Value,
            Type = type,
            Quantity = quantity,
            OccurredOn = now,
            IdempotencyKey = idempotencyKey,
            SourceType = sourceType,
            SourceId = sourceId,
            Reason = reason,
            CreatedBy = actor,
            CreatedOn = now,
        };

    private static StockLedgerResult Snapshot(Models.Inventory inventory, long? movementId)
        => new(inventory.Id, inventory.ProductId ?? 0, inventory.WarehouseId ?? 0, inventory.QtyOnHand,
            inventory.QuarantineQuantity, inventory.DamagedQuantity, inventory.ExpiredQuantity,
            inventory.SafetyStockQuantity, movementId);

    private static void Touch(Models.Inventory inventory, string actor)
    {
        inventory.ModifiedBy = actor;
        inventory.ModifiedOn = DateTime.UtcNow;
    }

    private IsolationLevel Isolation()
        => _db.Database.IsNpgsql() ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable;

    private async Task LockAsync(string identity, CancellationToken ct)
    {
        if (_db.Database.IsNpgsql())
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({identity}, 0))", ct);
    }

    private static void RequireActor(string actor)
    {
        if (string.IsNullOrWhiteSpace(actor))
            throw new ArgumentException("An authenticated actor is required for a stock write.", nameof(actor));
    }

    private static void RequireKey(string idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("An idempotency key is required.", nameof(idempotencyKey));
    }
}
