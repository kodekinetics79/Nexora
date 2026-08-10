using ERP_RFQ_Automation.Inventory;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Traceability;

namespace ERP_RFQ_Automation.Tests.Support;

/// <summary>
/// Composes the inventory write stack the way <c>Program.cs</c> composes it, so a test never
/// exercises a different object graph from production.
///
/// <para>The lot declarer is deliberately NOT defaulted to a no-op on the service itself. A
/// constructor that quietly accepted null would let a goods issue stop declaring the lots it moved
/// with no compile error and no test failure — where-used trace would simply go incomplete, which
/// is the exact class of gap Gate 6 was opened to close. Composing the real adapter here costs one
/// line and keeps that impossible.</para>
/// </summary>
public static class InventoryServices
{
    public static IInventoryAvailabilityService Availability(ErpRfqAutomationContext context)
        => new InventoryAvailabilityService(context);

    public static IStockLedgerService Ledger(ErpRfqAutomationContext context)
        => new StockLedgerService(context);

    public static IMaterialTraceabilityService Traceability(ErpRfqAutomationContext context)
        => new MaterialTraceabilityService(context, Ledger(context), Availability(context));

    public static ILotFulfilmentDeclarer Declarer(ErpRfqAutomationContext context)
        => new MaterialLotFulfilmentDeclarer(context, Traceability(context));

    public static OrderStockReservationService OrderStock(ErpRfqAutomationContext context)
        => new(context, Availability(context), Declarer(context));
}
