using ERP_RFQ_Automation.MultiTenancy;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Models;

// Tenant isolation via EF Core global query filters (ADR-0005). Implemented in a
// partial so the large scaffolded context file stays untouched. Fail-closed:
// every authenticated request is transparently scoped to its business unit; a
// null tenant (login / anonymous / background worker) applies NO filter so those
// paths keep working. For legitimate cross-tenant reads (platform plane, worker
// sweeps) use .IgnoreQueryFilters().
public partial class ErpRfqAutomationContext
{
    // Set by the tenant-scoping constructor; null on the design-time /
    // parameterless / options-only paths.
    private readonly ITenantContext? _tenant;

    // Null when there is no tenant context -> filters become no-ops.
    private long? CurrentTenantId => _tenant?.BusinessUnitId;

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder)
    {
        // Commercial documents (non-nullable BusinessUnitId).
        modelBuilder.Entity<Lead>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<Rfq>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<Quote>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<Order>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<Shipment>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);

        // Master data (nullable Buid). Rows with a null Buid are treated as shared
        // reference data (visible to all tenants); tenant-owned rows are scoped.
        modelBuilder.Entity<Customer>().HasQueryFilter(e => CurrentTenantId == null || e.Buid == null || e.Buid == CurrentTenantId);
        modelBuilder.Entity<Supplier>().HasQueryFilter(e => CurrentTenantId == null || e.Buid == null || e.Buid == CurrentTenantId);
        modelBuilder.Entity<Product>().HasQueryFilter(e => CurrentTenantId == null || e.Buid == null || e.Buid == CurrentTenantId);
    }
}
