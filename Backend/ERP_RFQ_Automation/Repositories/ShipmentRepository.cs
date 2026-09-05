using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ERP_RFQ_Automation.Repositories
{
    public class ShipmentRepository : IShipmentRepository
    {
        private readonly ErpRfqAutomationContext _context;

        public ShipmentRepository(ErpRfqAutomationContext context)
        {
            _context = context;
        }

        public async Task<IEnumerable<Shipment>> GetAllShipmentsAsync(long businessUnitId)
        {
            return await _context.Shipments
                .Where(s => s.BusinessUnitId == businessUnitId && s.IsActive)
                .Include(s => s.Order).ThenInclude(o => o.Currency)
                .Include(s => s.Status)
                // The lines travel with the list. Without them the list and the by-order read
                // answered `items: []` for every shipment while the detail answered the real lines,
                // and OrderViewPage summed those empty lists to decide whether anything was left to
                // ship — so a fully despatched order kept offering "Create Shipment".
                .Include(s => s.ShipmentItems).ThenInclude(si => si.OrderItem).ThenInclude(oi => oi.Product)
                // FR-DLM-01. The governed region travels with the shipment so the list and the
                // note read the same mapping rather than each deriving one.
                .Include(s => s.DeliveryCity).ThenInclude(c => c!.State)
                .OrderByDescending(s => s.CreatedOn)
                .ToListAsync();
        }

        public async Task<Shipment?> GetShipmentByIdAsync(long id, long businessUnitId)
        {
            return await _context.Shipments
                .Include(s => s.Order).ThenInclude(o => o.Currency)
                .Include(s => s.Status)
                .Include(s => s.ShipmentStatusHistories)
                    .ThenInclude(h => h.PreviousStatus)
                .Include(s => s.ShipmentStatusHistories)
                    .ThenInclude(h => h.NewStatus)
                .Include(s => s.ShipmentItems)
                    .ThenInclude(si => si.OrderItem)
                        .ThenInclude(oi => oi.Product)
                .Include(s => s.DeliveryCity).ThenInclude(c => c!.State)
                .FirstOrDefaultAsync(s => s.Id == id && s.BusinessUnitId == businessUnitId && s.IsActive);
        }

        public async Task<Shipment> CreateShipmentAsync(Shipment shipment)
        {
            _context.Shipments.Add(shipment);
            await _context.SaveChangesAsync();
            return shipment;
        }

        public async Task<Shipment> UpdateShipmentAsync(Shipment shipment, long businessUnitId)
        {
            var existing = await _context.Shipments.AnyAsync(s => s.Id == shipment.Id && s.BusinessUnitId == businessUnitId);
            if (!existing)
                throw new KeyNotFoundException($"Shipment with ID {shipment.Id} not found.");

            _context.Entry(shipment).State = EntityState.Modified;
            await _context.SaveChangesAsync();
            return shipment;
        }

        /// <summary>
        /// Withdraws a shipment that never despatched. Refuses everything else.
        ///
        /// <para><b>What this used to be.</b> Four lines: find the row, set <c>IsActive = false</c>,
        /// save. No status check, no proof-of-delivery check, no reason and no attribution, on a
        /// <c>Shipments:Delete</c> permission a warehouse role plausibly holds. A signed delivery —
        /// stock issued, lots consumed, a POD with a name, a signature and a GPS fix behind it —
        /// disappeared from every list and every despatch total on one click, leaving the accepted
        /// quantities it evidenced still capping the invoice.
        /// <c>DeliveryStatuses.Cancellable</c> had said since Gate 7 that a confirmed shipment must
        /// not be reversed; this path had never been told. Wiring-contract failure #9.</para>
        ///
        /// <para><b>Two independent witnesses, not one.</b> The status must be in
        /// <c>DeliveryStatuses.Withdrawable</c> AND no proof of delivery may exist. The POD check is
        /// not redundant: it does not trust <c>DeliveryStatus</c> to be the only account of whether
        /// a customer signed, and a signed document is the one record whose disappearance loses a
        /// dispute.</para>
        ///
        /// <para><b>Attribution.</b> A destructive verb states who and why, like every other one in
        /// this codebase. It is recorded as a <c>ShipmentStatusHistory</c> row — the same place the
        /// delivery ladder records its own transitions, written with the status unchanged — because
        /// the honest home for it, three columns on <c>Shipments</c>, is a schema change and the
        /// migration is somebody else's to author. That delta is reported rather than half-applied;
        /// until it lands the reason lives in <c>Notes</c>, enforced here rather than by a CHECK.</para>
        /// </summary>
        /// <exception cref="ArgumentException">The reason or the actor is missing.</exception>
        /// <exception cref="KeyNotFoundException">No such shipment in this tenant.</exception>
        /// <exception cref="InvalidOperationException">
        /// The shipment despatched, or carries a proof of delivery.
        /// </exception>
        public async Task DeleteShipmentAsync(long id, long businessUnitId, string reason, string actor)
        {
            var withdrawalReason = reason?.Trim();
            if (string.IsNullOrWhiteSpace(withdrawalReason))
                throw new ArgumentException(
                    "A reason is required to withdraw a shipment. A despatch note that vanished "
                    + "with no sentence behind it tells the next person reading the order nothing.");
            if (withdrawalReason.Length > 500)
                throw new ArgumentException(
                    "The withdrawal reason may be up to 500 characters.");

            var withdrawnBy = actor?.Trim();
            if (string.IsNullOrWhiteSpace(withdrawnBy))
                throw new ArgumentException(
                    "An authenticated actor is required to withdraw a shipment.");

            var shipment = await _context.Shipments
                .FirstOrDefaultAsync(s => s.Id == id && s.BusinessUnitId == businessUnitId)
                ?? throw new KeyNotFoundException(
                    $"Shipment {id} was not found in this business unit.");

            // Already withdrawn. Idempotent, and deliberately not an error: a retried request must
            // not read as a refusal.
            if (!shipment.IsActive) return;

            if (!ERP_RFQ_Automation.Delivery.DeliveryStatuses.Withdrawable.Contains(shipment.DeliveryStatus))
                throw new InvalidOperationException(
                    $"A {shipment.DeliveryStatus} shipment cannot be deleted. The goods have left "
                    + "the warehouse: this row is the only account of a goods issue that really "
                    + "happened, and removing it would drop the quantity out of the despatch total "
                    + "while the stock stays gone. Only a shipment that never despatched can be "
                    + "withdrawn.");

            var proved = await _context.DeliveryProofs.AsNoTracking()
                .AnyAsync(p => p.BusinessUnitId == businessUnitId && p.ShipmentId == id);
            if (proved)
                throw new InvalidOperationException(
                    "This shipment carries a proof of delivery. A signed delivery is evidence, not "
                    + "a row: it is the document a payment dispute turns on, and it cannot be "
                    + "deleted. A quantity the customer signed for and then returned is a credit "
                    + "note, which exists.");

            shipment.IsActive = false; // Soft delete based on project pattern
            shipment.ModifiedBy = withdrawnBy;
            shipment.ModifiedOn = DateTime.Now;
            _context.Entry(shipment).State = EntityState.Modified;

            _context.ShipmentStatusHistories.Add(new ShipmentStatusHistory
            {
                ShipmentId = shipment.Id,
                PreviousStatusId = shipment.StatusId,
                NewStatusId = shipment.StatusId,
                ChangedBy = withdrawnBy,
                ChangedOn = DateTime.Now,
                Notes = $"Shipment withdrawn from {shipment.DeliveryStatus}: {withdrawalReason}"
            });

            await _context.SaveChangesAsync();
        }

        public async Task<string> GetNextShipmentNumberAsync(long businessUnitId)
        {
            var now = DateTime.Now;
            var prefix = $"SHP-{now:MM}{now:yy}-";
            
            var lastShipment = await _context.Shipments
                .Where(s => s.BusinessUnitId == businessUnitId && s.ShipmentNo.StartsWith(prefix))
                .OrderByDescending(s => s.ShipmentNo)
                .Select(s => s.ShipmentNo)
                .FirstOrDefaultAsync();

            int nextSequence = 1;

            if (lastShipment != null)
            {
                var parts = lastShipment.Split('-');
                if (parts.Length == 3 && int.TryParse(parts[2], out int lastSeq))
                {
                    nextSequence = lastSeq + 1;
                }
            }

            return $"{prefix}{nextSequence:D6}";
        }

        public async Task<IEnumerable<Shipment>> GetShipmentsByOrderIdAsync(long orderId, long businessUnitId)
        {
            return await _context.Shipments
                .Where(s => s.OrderId == orderId && s.BusinessUnitId == businessUnitId && s.IsActive)
                .Include(s => s.Status)
                .Include(s => s.ShipmentItems).ThenInclude(si => si.OrderItem).ThenInclude(oi => oi.Product)
                .ToListAsync();
        }
    }
}
