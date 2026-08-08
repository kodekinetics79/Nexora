using ERP_RFQ_Automation.Billing;
using ERP_RFQ_Automation.Billing.Accounting;
using ERP_RFQ_Automation.Billing.Metering;
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
        UsageMeteringModelConfiguration.Apply(modelBuilder);
        AccountingOutboxModelConfiguration.Apply(modelBuilder);
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
            e.Property(x => x.ComputedBy).IsRequired().HasMaxLength(256);
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
            // Provenance and the coverage caveat are UNBOUNDED text, not
            // varchar(400). A page/storage provenance note names every
            // contributing ledger and its caveat names every instrumentation gap;
            // both blow past 400 characters routinely. On PostgreSQL a bounded
            // column turns that into a 22001 (string data right truncation) that
            // no billing catch handles, so statement compute 500s in production
            // while SQLite — which ignores varchar lengths entirely — stays green.
            e.Property(x => x.SourceNote).HasColumnType("text");
            e.Property(x => x.CoverageNote).HasColumnType("text");
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

        modelBuilder.Entity<SubscriptionInvoice>(e =>
        {
            e.ToTable("SubscriptionInvoices", "platform");
            e.HasKey(x => x.Id);
            e.Property(x => x.InvoiceNumber).HasMaxLength(64).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(24).IsRequired();
            e.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            e.Property(x => x.Subtotal).HasPrecision(14, 2);
            e.Property(x => x.TaxRatePercent).HasPrecision(7, 4);
            e.Property(x => x.TaxAmount).HasPrecision(14, 2);
            e.Property(x => x.TotalAmount).HasPrecision(14, 2);
            e.Property(x => x.CreditedAmount).HasPrecision(14, 2);
            e.Property(x => x.PaidAmount).HasPrecision(14, 2);
            e.Property(x => x.SellerSnapshotJson).HasColumnType("jsonb").IsRequired();
            e.Property(x => x.BuyerSnapshotJson).HasColumnType("jsonb").IsRequired();
            e.Property(x => x.TaxTreatment).HasMaxLength(128).IsRequired();
            e.Property(x => x.SourceEvidenceJson).HasColumnType("jsonb").IsRequired();
            e.Property(x => x.SourceEvidenceSha256).HasMaxLength(64).IsFixedLength().IsRequired();
            e.Property(x => x.CreatedBy).HasMaxLength(256).IsRequired();
            e.Property(x => x.FinalizedBy).HasMaxLength(256);
            e.Property(x => x.Version).HasDefaultValue(1L).IsConcurrencyToken();
            e.HasIndex(x => x.InvoiceNumber).IsUnique();
            e.HasIndex(x => x.BillingStatementId).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.Status, x.DueAtUtc });
            e.HasOne<BillingStatement>().WithMany().HasForeignKey(x => x.BillingStatementId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ERP_RFQ_Automation.Platform.Models.Tenant>().WithMany().HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SubscriptionCreditNote>(e =>
        {
            e.ToTable("SubscriptionCreditNotes", "platform");
            e.HasKey(x => x.Id);
            e.Property(x => x.CreditNumber).HasMaxLength(64).IsRequired();
            e.Property(x => x.IdempotencyKey).HasMaxLength(128).IsRequired();
            e.Property(x => x.Amount).HasPrecision(14, 2);
            e.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
            e.Property(x => x.CreatedBy).HasMaxLength(256).IsRequired();
            e.HasIndex(x => x.CreditNumber).IsUnique();
            e.HasIndex(x => x.IdempotencyKey).IsUnique();
            e.HasOne(x => x.Invoice).WithMany(x => x.Credits)
                .HasForeignKey(x => x.SubscriptionInvoiceId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SubscriptionPayment>(e =>
        {
            e.ToTable("SubscriptionPayments", "platform");
            e.HasKey(x => x.Id);
            e.Property(x => x.ExternalReference).HasMaxLength(128).IsRequired();
            e.Property(x => x.Amount).HasPrecision(14, 2);
            e.Property(x => x.RecordedBy).HasMaxLength(256).IsRequired();
            e.HasIndex(x => x.ExternalReference).IsUnique();
            e.HasOne(x => x.Invoice).WithMany(x => x.Payments)
                .HasForeignKey(x => x.SubscriptionInvoiceId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
