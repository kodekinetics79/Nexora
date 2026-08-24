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

        // The source-document tombstone shape, extended to EmailIngests. Configured here with
        // the rest of retention rather than in the scaffolded EmailIngest block, so the columns
        // sit next to the only code that ever writes them. See EmailIngest (Retention partial).
        modelBuilder.Entity<EmailIngest>(entity =>
        {
            entity.Property(e => e.BytesPurgedOn).HasColumnName("bytes_purged_on");
            entity.Property(e => e.PurgedByUserId).HasColumnName("purged_by_user_id");
            entity.Property(e => e.PurgeReason).HasColumnName("purge_reason").HasMaxLength(1000);
            entity.Ignore(e => e.RawMessageAvailable);

            // A tombstone is only a tombstone if it is complete. Half of one — a timestamp with
            // no author, or an author with no reason — is worse than none, because it reads as a
            // record while answering neither "who" nor "why". The database refuses the partial
            // shape rather than trusting every future writer to remember all three.
            // trim(), not btrim(). btrim is PostgreSQL-only, and this model also builds the
            // schema for the SQLite integration suites through EnsureCreated — where the
            // constraint would be emitted verbatim and fail every database creation with
            // "no such function: btrim". trim() means the same thing on both.
            entity.HasCheckConstraint("CK_EmailIngests_purge_tombstone_complete",
                "(\"bytes_purged_on\" IS NULL AND \"purged_by_user_id\" IS NULL AND \"purge_reason\" IS NULL) "
                + "OR (\"bytes_purged_on\" IS NOT NULL AND \"purged_by_user_id\" IS NOT NULL "
                + "AND length(trim(\"purge_reason\")) > 0)");
        });
    }
}
