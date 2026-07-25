using ERP_RFQ_Automation.Inventory.Commercial;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Models;

public partial class ErpRfqAutomationContext
{
    public DbSet<ProductAlias> ProductAliases => Set<ProductAlias>();
    public DbSet<ProductSupersession> ProductSupersessions => Set<ProductSupersession>();
    public DbSet<InventoryMovement> InventoryMovements => Set<InventoryMovement>();
    public DbSet<IncomingInventory> IncomingInventory => Set<IncomingInventory>();
}
