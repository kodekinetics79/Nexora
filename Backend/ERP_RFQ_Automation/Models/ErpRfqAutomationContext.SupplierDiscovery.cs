using ERP_RFQ_Automation.Procurement.Discovery;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Models;

/// <summary>
/// Internet supplier discovery: the 30-day search cache (<see cref="SupplierDiscoverySearch"/>) and
/// the two supplier columns it writes (<see cref="Supplier.Website"/>, <see cref="Supplier.Role"/>).
/// Own partial, same idiom as .AiProviderTrust.cs, so the scaffolded context stays untouched.
///
/// <para>Tenant isolation on the cache table is enforced twice, as every tenant-scoped table here
/// is: the EF global query filter below, and the Postgres <c>nexora_tenant_isolation</c> policy plus
/// the <c>nexora_tenant_app</c> GRANT written by the <c>SupplierDiscovery</c> migration.</para>
/// </summary>
public partial class ErpRfqAutomationContext
{
    public DbSet<SupplierDiscoverySearch> SupplierDiscoverySearches => Set<SupplierDiscoverySearch>();

    private void ConfigureSupplierDiscoveryModel(ModelBuilder modelBuilder)
    {
        var roles = string.Join(",", SupplierRoles.All.Select(x => $"'{x}'"));
        modelBuilder.Entity<Supplier>(entity =>
        {
            entity.Property(x => x.Website).HasMaxLength(500);
            entity.Property(x => x.Role).HasMaxLength(32);
            // A rank and a filter can only group by a controlled value. NULL is "nobody has said",
            // the state every existing supplier is in; the constraint permits it and nothing else.
            entity.ToTable(table => table.HasCheckConstraint("CK_Suppliers_Role",
                $"\"Role\" IS NULL OR \"Role\" IN ({roles})"));
        });

        modelBuilder.Entity<SupplierDiscoverySearch>(entity =>
        {
            entity.ToTable("supplier_discovery_searches");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.IdentityKey).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Subject).HasMaxLength(255).IsRequired();
            entity.Property(x => x.Provider).HasMaxLength(100).IsRequired();
            entity.Property(x => x.QueriesJson).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.HitsJson).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.SearchedBy).HasMaxLength(255).IsRequired();
            // One cached search per (tenant, identity): a repeat within 30 days reads this row, a
            // repeat after it overwrites this row.
            entity.HasIndex(x => new { x.BusinessUnitId, x.IdentityKey }).IsUnique()
                .HasDatabaseName("UX_supplier_discovery_searches_BU_IdentityKey");
            entity.HasOne<BusinessUnit>().WithMany()
                .HasForeignKey(x => x.BusinessUnitId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(x => CurrentTenantId == null || x.BusinessUnitId == CurrentTenantId);
        });
    }
}
