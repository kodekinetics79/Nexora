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
                .Include(s => s.Order)
                .Include(s => s.Status)
                .OrderByDescending(s => s.CreatedOn)
                .ToListAsync();
        }

        public async Task<Shipment?> GetShipmentByIdAsync(long id, long businessUnitId)
        {
            return await _context.Shipments
                .Include(s => s.Order)
                .Include(s => s.Status)
                .Include(s => s.ShipmentStatusHistories)
                    .ThenInclude(h => h.PreviousStatus)
                .Include(s => s.ShipmentStatusHistories)
                    .ThenInclude(h => h.NewStatus)
                .Include(s => s.ShipmentItems)
                    .ThenInclude(si => si.OrderItem)
                        .ThenInclude(oi => oi.Product)
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

        public async Task DeleteShipmentAsync(long id, long businessUnitId)
        {
            var shipment = await _context.Shipments.FirstOrDefaultAsync(s => s.Id == id && s.BusinessUnitId == businessUnitId);
            if (shipment != null)
            {
                shipment.IsActive = false; // Soft delete based on project pattern
                _context.Entry(shipment).State = EntityState.Modified;
                await _context.SaveChangesAsync();
            }
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
                .ToListAsync();
        }
    }
}
