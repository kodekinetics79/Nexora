using ERP_RFQ_Automation.Inventory;
using ERP_RFQ_Automation.Models;

namespace ERP_RFQ_Automation.Tests.Support;

/// <summary>Seeds pre-release audit evidence without exercising a disabled reservation write.</summary>
public static class HistoricalReservations
{
    public static async Task<StockReservation> SeedAsync(ErpRfqAutomationContext context,
        long businessUnitId, long inventoryId, decimal quantity, string key,
        long? orderId = null, long? orderItemId = null, long? lotId = null,
        StockReservationStatus status = StockReservationStatus.Active)
    {
        var row = new StockReservation
        {
            BusinessUnitId = businessUnitId, InventoryId = inventoryId, Quantity = quantity,
            IdempotencyKey = key, OrderId = orderId, OrderItemId = orderItemId,
            MaterialLotId = lotId, Status = status, CreatedBy = "historical-fixture",
            CreatedOn = DateTime.UtcNow.AddDays(-30), Version = 1,
        };
        context.StockReservations.Add(row);
        await context.SaveChangesAsync();
        return row;
    }
}
