using ERP_RFQ_Automation.SupplierEvaluation;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Models;

/// <summary>
/// Gate 2 — supplier tier classification (FR-MDM-03, first clause) and the per-tenant weight set the
/// supplier comparison score is computed from (FR-QTM-03).
///
/// <para>Two columns on the existing <see cref="Supplier"/> table, which already carries its tenant
/// query filter and composite key, plus ONE new tenant-owned table. The new table carries its EF
/// global query filter here — that single line is what enrols it in the tenant-isolation guard — and
/// requires a matching PostgreSQL row-level-security policy and GRANT in the migration. The
/// migration is the Integration Owner's to write.</para>
/// </summary>
public partial class ErpRfqAutomationContext
{
    /// <summary>
    /// Named <c>…WeightSets</c> rather than <c>…Weights</c> so the DbSet does not shadow the entity
    /// type inside this class, where the table configuration reads the entity's own defaults.
    /// </summary>
    public virtual DbSet<SupplierComparisonWeights> SupplierComparisonWeightSets { get; set; } = null!;

    /// <summary>
    /// EF omits a property from an INSERT when its value equals the property's SENTINEL, letting the
    /// database default supply it — and the sentinel defaults to the CLR default, which for
    /// <c>int</c> is 0. Zero is a REAL weight here: warranty ships at 0 and the "Cheapest wins"
    /// preset is 100/0/0/0. Without an impossible sentinel a tenant saving "lead time is worth
    /// nothing to us" would write a row that came back reading 20. Same defect, same fix, as
    /// <c>OrderToCashModelBuilderExtensions.NotAPercentage</c>.
    /// </summary>
    private const int NotAWeight = -1;

    partial void ConfigureSupplierEvaluationModel(ModelBuilder modelBuilder);

    partial void ConfigureSupplierEvaluationModel(ModelBuilder modelBuilder)
    {
        var tiers = string.Join(",", SupplierTiers.All.Select(x => $"'{x}'"));

        modelBuilder.Entity<Supplier>(entity =>
        {
            entity.Property(x => x.Tier).HasMaxLength(32);
            // A report, a dispatch filter and a pre-selection can only group by a controlled value.
            // NULL is the "not yet classified" state every existing supplier is in and must stay
            // reachable — the constraint permits it and forbids everything else.
            entity.ToTable(table => table.HasCheckConstraint("CK_Suppliers_Tier",
                $"\"Tier\" IS NULL OR \"Tier\" IN ({tiers})"));
        });

        modelBuilder.Entity<SupplierComparisonWeights>(entity =>
        {
            entity.ToTable("SupplierComparisonWeights");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            // One weight set per business unit (R-H: no per-customer, per-category, per-supplier or
            // per-RFQ override, no inheritance chain). The unique index is what makes "create on
            // demand" safe under concurrency: two requests racing to create the first row leaves one
            // winner.
            entity.HasIndex(x => x.BusinessUnitId).IsUnique();
            entity.Property(x => x.PriceWeight)
                .HasDefaultValue(SupplierComparisonWeights.DefaultPriceWeight).HasSentinel(NotAWeight);
            entity.Property(x => x.LeadTimeWeight)
                .HasDefaultValue(SupplierComparisonWeights.DefaultLeadTimeWeight).HasSentinel(NotAWeight);
            entity.Property(x => x.WarrantyWeight)
                .HasDefaultValue(SupplierComparisonWeights.DefaultWarrantyWeight).HasSentinel(NotAWeight);
            entity.Property(x => x.PaymentTermsWeight)
                .HasDefaultValue(SupplierComparisonWeights.DefaultPaymentTermsWeight).HasSentinel(NotAWeight);
            entity.Property(x => x.Version).HasDefaultValue(1).IsConcurrencyToken();
            entity.Property(x => x.CreatedOn).HasDefaultValueSql("now()");
            entity.Property(x => x.ModifiedBy).HasMaxLength(255);
            // The invariant the whole score depends on, in the database and not only in the service.
            // A score presented as "82 out of 100" is a lie the moment the weights sum to anything
            // else, and a second write path would otherwise be free to create one.
            entity.ToTable(table => table.HasCheckConstraint("CK_SupplierComparisonWeights_Total",
                "\"PriceWeight\" >= 0 AND \"LeadTimeWeight\" >= 0 AND \"WarrantyWeight\" >= 0 "
                + "AND \"PaymentTermsWeight\" >= 0 AND (\"PriceWeight\" + \"LeadTimeWeight\" "
                + $"+ \"WarrantyWeight\" + \"PaymentTermsWeight\") = {SupplierComparisonWeights.RequiredTotal}"));
            entity.HasOne<BusinessUnit>().WithMany().HasForeignKey(x => x.BusinessUnitId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasQueryFilter(x => CurrentTenantId == null || x.BusinessUnitId == CurrentTenantId);
        });
    }
}
