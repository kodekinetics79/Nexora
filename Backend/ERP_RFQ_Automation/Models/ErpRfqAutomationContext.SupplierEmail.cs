using ERP_RFQ_Automation.Procurement.SupplierEmail;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Models;

/// <summary>
/// The company's and each sales person's wording for supplier request emails
/// (<see cref="SupplierEmailSettings"/>). Tenant-isolated twice, like every tenant table: the query
/// filter below and the <c>nexora_tenant_isolation</c> policy written by the SupplierEmailSettings migration.
/// </summary>
public partial class ErpRfqAutomationContext
{
    public DbSet<SupplierEmailSettings> SupplierEmailSettings => Set<SupplierEmailSettings>();

    private void ConfigureSupplierEmailModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SupplierEmailSettings>(entity =>
        {
            entity.ToTable("supplier_email_settings");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Subject).HasMaxLength(SupplierEmailDefaults.SubjectMax + 20);
            entity.Property(x => x.Greeting).HasMaxLength(SupplierEmailDefaults.GreetingMax);
            entity.Property(x => x.Opening).HasMaxLength(SupplierEmailDefaults.OpeningMax);
            entity.Property(x => x.DefaultMessage).HasMaxLength(SupplierEmailDefaults.MessageMax);
            entity.Property(x => x.SignOff).HasMaxLength(SupplierEmailDefaults.SignOffMax);
            entity.Property(x => x.UpdatedBy).HasMaxLength(255).IsRequired();
            // One company standard per tenant, and one row per sales person.
            entity.HasIndex(x => x.BusinessUnitId).IsUnique().HasFilter("\"UserId\" IS NULL")
                .HasDatabaseName("UX_supplier_email_settings_BU_Company");
            entity.HasIndex(x => new { x.BusinessUnitId, x.UserId }).IsUnique().HasFilter("\"UserId\" IS NOT NULL")
                .HasDatabaseName("UX_supplier_email_settings_BU_User");
            entity.HasOne<BusinessUnit>().WithMany()
                .HasForeignKey(x => x.BusinessUnitId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(x => CurrentTenantId == null || x.BusinessUnitId == CurrentTenantId);
        });
    }
}
