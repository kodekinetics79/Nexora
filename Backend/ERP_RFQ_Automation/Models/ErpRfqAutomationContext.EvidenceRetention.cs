using ERP_RFQ_Automation.Retention;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Models;

/// <summary>
/// Per-tenant evidence retention policy (<see cref="EvidenceRetentionPolicy"/>). Its own
/// partial, following the .Fx.cs / .Sla.cs / .AiProviderTrust.cs idiom, so the scaffolded
/// context and the large tenancy partial stay untouched.
///
/// <para>
/// Tenant isolation is enforced twice, as every tenant-scoped table in this codebase must:
/// the EF global query filter declared here, and the <c>nexora_tenant_isolation</c> RLS
/// policy created by the <c>TenantEvidenceRetentionAndBytePurge</c> migration. A policy that
/// decides which documents get destroyed is the last table that should be readable across
/// tenants.
/// </para>
/// </summary>
public partial class ErpRfqAutomationContext
{
    public virtual DbSet<EvidenceRetentionPolicy> EvidenceRetentionPolicies { get; set; } = null!;

    private void ConfigureEvidenceRetentionModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EvidenceRetentionPolicy>(entity =>
        {
            entity.ToTable("evidence_retention_policies");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.RetentionDays)
                .HasDefaultValue(EvidenceRetentionPolicy.DefaultRetentionDays);
            entity.Property(e => e.IsEnabled).HasDefaultValue(false);
            entity.Property(e => e.Version).HasDefaultValue(1);
            entity.Ignore(e => e.PolicyCode);

            // One policy per tenant. Two rows would mean two answers to "when do this
            // tenant's documents get destroyed", and the destructive path must not have to
            // choose between them.
            entity.HasIndex(e => e.BusinessUnitId).IsUnique()
                .HasDatabaseName("UX_evidence_retention_policies_BusinessUnit");

            // The floor is a database constraint, not just a validation message: it must
            // hold against any writer, including a future worker or a manual fix-up.
            entity.HasCheckConstraint("CK_evidence_retention_policies_retention_days",
                $"\"RetentionDays\" >= {EvidenceRetentionPolicy.MinimumRetentionDays} "
                + $"AND \"RetentionDays\" <= {EvidenceRetentionPolicy.MaximumRetentionDays}");

            entity.HasOne<BusinessUnit>().WithMany()
                .HasForeignKey(e => e.BusinessUnitId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<EvidenceRetentionPolicy>().HasQueryFilter(
            e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
    }
}
