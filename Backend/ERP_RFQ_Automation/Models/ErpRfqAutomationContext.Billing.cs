using ERP_RFQ_Automation.Billing;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Models;

// SaaS billing model (WS-C): rate cards + billing statements in the "platform"
// schema. These tables are platform-plane control data, NOT tenant-scoped — no
// global query filters, exactly like Tenants/Plans/PlatformAuditLogs (the tenant
// invariant tests exempt schema == "platform", and the columns are TenantId, not
// BusinessUnitId). Integration Owner wires the single call
// `ConfigureBillingModel(modelBuilder);` into OnModelCreatingPartial
// (ErpRfqAutomationContext.Tenancy.cs) and generates the one merged migration.
public partial class ErpRfqAutomationContext
{
    // Deliberately NO DbSet properties: access is via Set<T>() (the existing
    // platform-plane pattern). DbSet properties would pull the entities into the
    // model by convention BEFORE the Integration Owner wires the configuration
    // call, producing unconfigured tables.
    private void ConfigureBillingModel(ModelBuilder modelBuilder)
        => BillingModelConfiguration.Apply(modelBuilder);
}

/// <summary>
/// The actual billing entity configuration, exposed as a static helper so the
/// test suite can build a billing-enabled model before the Integration Owner
/// wires <c>ConfigureBillingModel</c> into the production context. Applying it
/// twice is harmless (same fluent calls, idempotent).
/// </summary>
public static class BillingModelConfiguration
{
    public static void Apply(ModelBuilder modelBuilder)
    {
        // ==== platform.RateCards ====
        modelBuilder.Entity<RateCard>(e =>
        {
            e.ToTable("RateCards", "platform");
            e.HasKey(x => x.Id);
            e.Property(x => x.Code).IsRequired().HasMaxLength(64);
            e.Property(x => x.Currency).IsRequired().HasMaxLength(3);
            e.Property(x => x.CreatedBy).HasMaxLength(256);
            e.Property(x => x.Version).HasDefaultValue(1L).IsConcurrencyToken();
            e.HasIndex(x => x.Code).IsUnique().HasDatabaseName("UX_RateCards_Code");
            e.HasIndex(x => new { x.IsActive, x.EffectiveFromUtc })
                .HasDatabaseName("IX_RateCards_Active_EffectiveFrom");
        });

        // ==== platform.RateCardLines ====
        modelBuilder.Entity<RateCardLine>(e =>
        {
            e.ToTable("RateCardLines", "platform");
            e.HasKey(x => x.Id);
            e.Property(x => x.MeterKey).IsRequired().HasMaxLength(64);
            e.Property(x => x.Unit).IsRequired().HasMaxLength(32);
            e.Property(x => x.TierNote).HasMaxLength(400);
            e.Property(x => x.IncludedQuantity).HasPrecision(18, 3);
            e.Property(x => x.UnitPrice).HasPrecision(12, 6);
            e.HasIndex(x => new { x.RateCardId, x.MeterKey }).IsUnique()
                .HasDatabaseName("UX_RateCardLines_RateCard_MeterKey");
            e.HasOne(x => x.RateCard).WithMany(x => x.Lines)
                .HasForeignKey(x => x.RateCardId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ==== platform.BillingStatements ====
        modelBuilder.Entity<BillingStatement>(e =>
        {
            e.ToTable("BillingStatements", "platform");
            e.HasKey(x => x.Id);
            e.Property(x => x.Currency).IsRequired().HasMaxLength(3);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
            e.Property(x => x.TotalAmount).HasPrecision(14, 2);
            e.Property(x => x.FinalizedBy).HasMaxLength(256);
            e.Property(x => x.Version).HasDefaultValue(1L).IsConcurrencyToken();
            // THE duplicate-charge guard: one statement per tenant per period.
            e.HasIndex(x => new { x.TenantId, x.PeriodStartUtc }).IsUnique()
                .HasDatabaseName("UX_BillingStatements_Tenant_PeriodStart");
            e.HasIndex(x => new { x.TenantId, x.Status })
                .HasDatabaseName("IX_BillingStatements_Tenant_Status");
            e.HasIndex(x => x.RateCardId)
                .HasDatabaseName("IX_BillingStatements_RateCard");
            e.HasOne<ERP_RFQ_Automation.Platform.Models.Tenant>().WithMany()
                .HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne<RateCard>().WithMany()
                .HasForeignKey(x => x.RateCardId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ==== platform.BillingStatementLines ====
        modelBuilder.Entity<BillingStatementLine>(e =>
        {
            e.ToTable("BillingStatementLines", "platform");
            e.HasKey(x => x.Id);
            e.Property(x => x.MeterKey).IsRequired().HasMaxLength(64);
            e.Property(x => x.Description).IsRequired().HasMaxLength(256);
            e.Property(x => x.SourceNote).HasMaxLength(400);
            e.Property(x => x.MeteredQuantity).HasPrecision(18, 3);
            e.Property(x => x.IncludedQuantity).HasPrecision(18, 3);
            e.Property(x => x.BillableQuantity).HasPrecision(18, 3);
            e.Property(x => x.UnitPrice).HasPrecision(12, 6);
            e.Property(x => x.Amount).HasPrecision(14, 2);
            e.HasIndex(x => x.BillingStatementId)
                .HasDatabaseName("IX_BillingStatementLines_Statement");
            e.HasOne(x => x.Statement).WithMany(x => x.Lines)
                .HasForeignKey(x => x.BillingStatementId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
