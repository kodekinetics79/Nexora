using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using System.Data;

namespace ERP_RFQ_Automation.Inventory;

/// <summary>On-hand / reserved / available snapshot for one inventory item.</summary>
public readonly record struct StockAvailability(long InventoryId, decimal OnHand, decimal Reserved)
{
    /// <summary>Available-to-promise: physical on-hand minus everything actively held. Never negative.</summary>
    public decimal Available => Math.Max(0m, OnHand - Reserved);
}

/// <summary>Raised when a reservation cannot be honoured because insufficient stock is available.</summary>
public sealed class InsufficientStockException(long inventoryId, decimal requested, decimal available)
    : InvalidOperationException(
        $"Cannot reserve {requested} of inventory {inventoryId}: only {available} available.")
{
    public long InventoryId { get; } = inventoryId;
    public decimal Requested { get; } = requested;
    public decimal Available { get; } = available;
}

public interface IInventoryAvailabilityService
{
    /// <summary>Computes on-hand, reserved and available-to-promise for one inventory item.</summary>
    Task<StockAvailability> GetAvailabilityAsync(long businessUnitId, long inventoryId, CancellationToken ct = default);

    /// <summary>
    /// Atomically holds <paramref name="quantity"/> of an inventory item for an order. Idempotent on
    /// <paramref name="idempotencyKey"/> — a replay returns the existing hold. Throws
    /// <see cref="InsufficientStockException"/> if available-to-promise is below the request.
    /// </summary>
    Task<StockReservation> ReserveAsync(
        long businessUnitId, long inventoryId, decimal quantity, string idempotencyKey,
        long? orderId = null, long? orderItemId = null, string? actor = null, CancellationToken ct = default);

    /// <summary>Releases every ACTIVE hold for an order (e.g. on cancellation); availability is restored.</summary>
    Task<int> ReleaseForOrderAsync(long businessUnitId, long orderId, string? actor = null, CancellationToken ct = default);

    /// <summary>
    /// Consumes a hold on goods issue/delivery: the reservation becomes Consumed and the physical
    /// on-hand quantity is decremented in the same transaction. Idempotent — a consumed hold is a no-op.
    /// </summary>
    Task ConsumeAsync(long businessUnitId, long reservationId, string? actor = null, CancellationToken ct = default);
}

/// <summary>
/// Turns the single <see cref="Models.Inventory.QtyOnHand"/> column into a proper
/// on-hand / reserved / available model backed by an append-only reservation ledger, so two orders
/// can never promise the same physical stock. On-hand is only ever decremented by an authorised
/// consume (delivery) — never by a quote or a supplier response.
/// </summary>
public sealed class InventoryAvailabilityService(ErpRfqAutomationContext db) : IInventoryAvailabilityService
{
    private readonly ErpRfqAutomationContext _db = db;

    public async Task<StockAvailability> GetAvailabilityAsync(long businessUnitId, long inventoryId, CancellationToken ct = default)
    {
        var onHand = await _db.Set<Models.Inventory>()
            .Where(i => i.Id == inventoryId && i.Buid == businessUnitId)
            .Select(i => i.QtyOnHand)
            .FirstOrDefaultAsync(ct);

        var reserved = await ActiveReservedAsync(businessUnitId, inventoryId, ct);
        return new StockAvailability(inventoryId, onHand, reserved);
    }

    public async Task<StockReservation> ReserveAsync(
        long businessUnitId, long inventoryId, decimal quantity, string idempotencyKey,
        long? orderId = null, long? orderItemId = null, string? actor = null, CancellationToken ct = default)
    {
        if (quantity <= 0m)
            throw new ArgumentOutOfRangeException(nameof(quantity), "Reservation quantity must be positive.");
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("An idempotency key is required.", nameof(idempotencyKey));

        await using var transaction = _db.Database.CurrentTransaction == null
            ? await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct)
            : null;
        if (_db.Database.IsNpgsql())
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock({businessUnitId}, {inventoryId})", ct);

        // The lock and replay check share one transaction, fencing concurrent holds.
        var existing = await _db.Set<StockReservation>()
            .FirstOrDefaultAsync(r => r.BusinessUnitId == businessUnitId && r.IdempotencyKey == idempotencyKey, ct);
        if (existing != null)
            return existing;

        var availability = await GetAvailabilityAsync(businessUnitId, inventoryId, ct);
        if (availability.Available < quantity)
            throw new InsufficientStockException(inventoryId, quantity, availability.Available);

        var reservation = new StockReservation
        {
            BusinessUnitId = businessUnitId,
            InventoryId = inventoryId,
            OrderId = orderId,
            OrderItemId = orderItemId,
            Quantity = quantity,
            Status = StockReservationStatus.Active,
            IdempotencyKey = idempotencyKey,
            CreatedBy = actor ?? "system",
            CreatedOn = DateTime.UtcNow,
        };
        _db.Set<StockReservation>().Add(reservation);
        await _db.SaveChangesAsync(ct);
        if (transaction != null) await transaction.CommitAsync(ct);
        return reservation;
    }

    public async Task<int> ReleaseForOrderAsync(long businessUnitId, long orderId, string? actor = null, CancellationToken ct = default)
    {
        var active = await _db.Set<StockReservation>()
            .Where(r => r.BusinessUnitId == businessUnitId && r.OrderId == orderId
                        && r.Status == StockReservationStatus.Active)
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        foreach (var r in active)
        {
            r.Status = StockReservationStatus.Released;
            r.ReleasedOn = now;
        }
        if (active.Count > 0)
            await _db.SaveChangesAsync(ct);
        return active.Count;
    }

    public async Task ConsumeAsync(long businessUnitId, long reservationId, string? actor = null, CancellationToken ct = default)
    {
        var reservation = await _db.Set<StockReservation>()
            .FirstOrDefaultAsync(r => r.BusinessUnitId == businessUnitId && r.Id == reservationId, ct)
            ?? throw new InvalidOperationException($"Reservation {reservationId} was not found.");

        if (reservation.Status == StockReservationStatus.Consumed)
            return; // idempotent
        if (reservation.Status == StockReservationStatus.Released)
            throw new InvalidOperationException($"Reservation {reservationId} was released and cannot be consumed.");

        var inventory = await _db.Set<Models.Inventory>()
            .FirstOrDefaultAsync(i => i.Id == reservation.InventoryId && i.Buid == businessUnitId, ct)
            ?? throw new InvalidOperationException($"Inventory {reservation.InventoryId} was not found.");

        // Physical stock leaves the building only here, on an authorised goods issue.
        inventory.QtyOnHand -= reservation.Quantity;
        inventory.ModifiedBy = actor ?? "system";
        inventory.ModifiedOn = DateTime.UtcNow;

        reservation.Status = StockReservationStatus.Consumed;
        reservation.ConsumedOn = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
    }

    private async Task<decimal> ActiveReservedAsync(long businessUnitId, long inventoryId, CancellationToken ct)
    {
        var sum = await _db.Set<StockReservation>()
            .Where(r => r.BusinessUnitId == businessUnitId && r.InventoryId == inventoryId
                        && r.Status == StockReservationStatus.Active)
            .SumAsync(r => (decimal?)r.Quantity, ct);
        return sum ?? 0m;
    }
}
