using ERP_RFQ_Automation.AI;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Models;

/// <summary>
/// Per-tenant allow-list of external AI inference endpoints
/// (<see cref="AiExternalProviderAuthorization"/>). Kept in its own partial — the same
/// idiom as .Fx.cs / .Sla.cs / .LoginThrottling.cs — so the scaffolded context and the
/// tenancy partial stay untouched.
///
/// <para>
/// Tenant isolation is enforced twice, as the codebase requires for every tenant-scoped
/// table: the EF global query filter declared here (application half) and the Postgres
/// <c>nexora_tenant_isolation</c> RLS policy created by the
/// <c>GovernExternalAiProviderAllowList</c> migration (database half). One tenant
/// therefore cannot read — let alone rely on — another tenant's authorizations.
/// </para>
/// </summary>
public partial class ErpRfqAutomationContext
{
    public virtual DbSet<AiExternalProviderAuthorization> AiExternalProviderAuthorizations { get; set; } = null!;

    private void ConfigureAiProviderTrustModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AiExternalProviderAuthorization>(entity =>
        {
            entity.ToTable("AiProviderAuthorizations");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Provider)
                .HasMaxLength(AiProviderEndpoint.MaxProviderLength).IsRequired();
            entity.Property(e => e.Endpoint)
                .HasMaxLength(AiProviderEndpoint.MaxEndpointLength).IsRequired();
            entity.Property(e => e.Model)
                .HasMaxLength(AiProviderEndpoint.MaxModelLength).IsRequired();
            entity.Property(e => e.AllowedPurposes).HasMaxLength(500).IsRequired();
            entity.Property(e => e.Justification).HasMaxLength(2000).IsRequired();
            entity.Property(e => e.AuthorizedBy).HasMaxLength(255).IsRequired();
            entity.Property(e => e.RevokedBy).HasMaxLength(255);
            entity.Property(e => e.RevocationReason).HasMaxLength(2000);
            entity.Property(e => e.Version).HasDefaultValue(1L).IsConcurrencyToken();

            // One row per (tenant, provider, endpoint, model). Model is never null — the
            // "*" sentinel is stored explicitly — because PostgreSQL treats NULLs as
            // distinct in a unique index, which would silently permit duplicate
            // "any model" grants for the same destination.
            entity.HasIndex(e => new { e.BusinessUnitId, e.Provider, e.Endpoint, e.Model })
                .IsUnique()
                .HasDatabaseName("UX_AiProviderAuthorizations_BU_Provider_Endpoint_Model");
            entity.HasIndex(e => new { e.BusinessUnitId, e.Endpoint })
                .HasDatabaseName("IX_AiProviderAuthorizations_BU_Endpoint");

            entity.HasOne<BusinessUnit>().WithMany()
                .HasForeignKey(e => e.BusinessUnitId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // Application half of tenant isolation (ADR-0005). A null tenant (background
        // worker sweep / design-time model) applies no filter, exactly like every other
        // tenant-scoped entity; the allow-list evaluator additionally predicates every
        // read on the explicit business unit id, so a null ambient tenant cannot widen it.
        modelBuilder.Entity<AiExternalProviderAuthorization>().HasQueryFilter(
            e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
    }
}
