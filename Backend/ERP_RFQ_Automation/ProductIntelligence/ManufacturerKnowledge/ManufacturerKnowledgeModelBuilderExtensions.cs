using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.ProductIntelligence.ManufacturerKnowledge;

/// <summary>
/// Mapping for the tenant's manufacturer knowledge. Lives beside the entity so the module owns
/// its own schema, the way <c>CommercialRoutingModelBuilderExtensions</c> does; the DbContext
/// calls it once from <c>ErpRfqAutomationContext.Tenancy.cs</c> and adds the tenant query filter
/// there, next to every other tenant-scoped entity.
///
/// <para>Portable constructs only (no jsonb, no PostgreSQL expressions) so the SQLite unit lane
/// builds the table with <c>EnsureCreated()</c> and the learner can be tested without a
/// container. Row-level security and the execution-role grants are PostgreSQL-only and live in
/// the migration, where every other tenant table gets them.</para>
/// </summary>
public static class ManufacturerKnowledgeModelBuilderExtensions
{
    public const string TableName = "manufacturer_part_patterns";
    public const string UniqueIndexName = "UX_manufacturer_part_patterns_tenant_pattern_maker";

    public static ModelBuilder ApplyManufacturerKnowledgeModel(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<ManufacturerPartPattern>(entity =>
        {
            entity.ToTable(TableName);
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Pattern).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Manufacturer).HasMaxLength(200).IsRequired();
            entity.Property(x => x.NormalizedManufacturer).HasMaxLength(200).IsRequired();
            entity.Property(x => x.ObservationCount).HasDefaultValue(1);
            // One row per (tenant, pattern, maker). The index leads with the two columns every
            // lookup filters on, so it also serves the read path; a pattern that two makers
            // share is two rows, which is exactly what the inference needs to see to refuse.
            entity.HasIndex(x => new { x.BusinessUnitId, x.Pattern, x.NormalizedManufacturer })
                .IsUnique()
                .HasDatabaseName(UniqueIndexName);
        });

        return modelBuilder;
    }
}
