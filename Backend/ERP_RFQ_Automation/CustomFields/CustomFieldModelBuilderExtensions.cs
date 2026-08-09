using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.CustomFields;

public static class CustomFieldModelBuilderExtensions
{
    public static ModelBuilder ConfigureGovernedCustomFields(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CustomFieldDefinition>(entity =>
        {
            entity.ToTable("custom_field_definitions");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.Property(x => x.EntityType).HasMaxLength(80).IsRequired();
            entity.Property(x => x.StableKey).HasMaxLength(63).IsRequired();
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            // AA-01: position in the tenant's field list and in the column picker.
            entity.Property(x => x.DisplayOrder).HasDefaultValue(0);
            entity.Property(x => x.CreatedBy).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.Property(x => x.RetiredBy).HasMaxLength(200);
            entity.Property(x => x.RetirementReason).HasMaxLength(1000);
            entity.HasIndex(x => new { x.BusinessUnitId, x.EntityType, x.StableKey }).IsUnique();
            entity.HasMany(x => x.Versions).WithOne(x => x.Definition).HasForeignKey(x => x.DefinitionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.Navigation(x => x.Versions).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<CustomFieldVersion>(entity =>
        {
            entity.ToTable("custom_field_versions");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.DefinitionId, x.VersionNumber });
            entity.Property(x => x.Label).HasMaxLength(200).IsRequired();
            entity.Property(x => x.HelpText).HasMaxLength(2000);
            entity.Property(x => x.DataType).HasConversion<string>().HasMaxLength(30);
            entity.Property(x => x.DefaultValueJson).HasMaxLength(8000);
            entity.Property(x => x.MinimumValue).HasPrecision(28, 8);
            entity.Property(x => x.MaximumValue).HasPrecision(28, 8);
            entity.Property(x => x.CreatedBy).HasMaxLength(200).IsRequired();
            entity.Property(x => x.ViewAccess).HasConversion<string>().HasMaxLength(24);
            entity.Property(x => x.EditAccess).HasConversion<string>().HasMaxLength(24);
            entity.HasIndex(x => new { x.DefinitionId, x.VersionNumber }).IsUnique();
            entity.HasMany(x => x.Options).WithOne(x => x.Version).HasForeignKey(x => x.VersionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasMany(x => x.Rules).WithOne(x => x.Version).HasForeignKey(x => x.VersionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasMany(x => x.Dependencies).WithOne(x => x.Version).HasForeignKey(x => x.VersionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.Navigation(x => x.Options).UsePropertyAccessMode(PropertyAccessMode.Field);
            entity.Navigation(x => x.Rules).UsePropertyAccessMode(PropertyAccessMode.Field);
            entity.Navigation(x => x.Dependencies).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<CustomFieldOption>(entity =>
        {
            entity.ToTable("custom_field_options");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.StableKey).HasMaxLength(63).IsRequired();
            entity.Property(x => x.Label).HasMaxLength(200).IsRequired();
            entity.HasIndex(x => new { x.VersionId, x.StableKey }).IsUnique();
        });

        modelBuilder.Entity<CustomFieldRule>(entity =>
        {
            entity.ToTable("custom_field_rules");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Effect).HasConversion<string>().HasMaxLength(20);
            entity.Property(x => x.ConditionJson).IsRequired();
            entity.Ignore(x => x.Condition);
        });

        modelBuilder.Entity<CustomFieldDependency>(entity =>
        {
            entity.ToTable("custom_field_dependencies");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.VersionId, x.DependsOnDefinitionId }).IsUnique();
            entity.HasOne<CustomFieldDefinition>().WithMany().HasForeignKey(x => x.DependsOnDefinitionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CustomFieldRecord>(entity =>
        {
            entity.ToTable("custom_field_records");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.Property(x => x.EntityType).HasMaxLength(80).IsRequired();
            entity.HasIndex(x => new { x.BusinessUnitId, x.EntityType, x.EntityId }).IsUnique();
            entity.HasMany(x => x.Values).WithOne(x => x.Record)
                .HasForeignKey(x => new { x.BusinessUnitId, x.RecordId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.Navigation(x => x.Values).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<CustomFieldValue>(entity =>
        {
            entity.ToTable("custom_field_values", table =>
            {
                table.HasCheckConstraint("CK_custom_field_values_typed_value", """
                    (CASE WHEN "TextValue" IS NULL THEN 0 ELSE 1 END +
                     CASE WHEN "IntegerValue" IS NULL THEN 0 ELSE 1 END +
                     CASE WHEN "DecimalValue" IS NULL THEN 0 ELSE 1 END +
                     CASE WHEN "BooleanValue" IS NULL THEN 0 ELSE 1 END +
                     CASE WHEN "DateValue" IS NULL THEN 0 ELSE 1 END +
                     CASE WHEN "TimestampValue" IS NULL THEN 0 ELSE 1 END +
                     CASE WHEN "JsonValue" IS NULL THEN 0 ELSE 1 END +
                     CASE WHEN "ReferenceId" IS NULL THEN 0 ELSE 1 END) <= 1
                    """);
                table.HasCheckConstraint("CK_custom_field_values_reference_pair", """
                    ("ReferenceType" IS NULL AND "ReferenceId" IS NULL) OR
                    ("ReferenceType" IS NOT NULL AND "ReferenceId" IS NOT NULL)
                    """);
            });
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.Property(x => x.DecimalValue).HasPrecision(28, 8);
            entity.Property(x => x.ReferenceType).HasMaxLength(80);
            entity.Property(x => x.UpdatedBy).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.RecordId, x.DefinitionId }).IsUnique();
            entity.HasIndex(x => new { x.BusinessUnitId, x.DefinitionId, x.TextValue });
            entity.HasIndex(x => new { x.BusinessUnitId, x.DefinitionId, x.IntegerValue });
            entity.HasIndex(x => new { x.BusinessUnitId, x.DefinitionId, x.DecimalValue });
            entity.HasIndex(x => new { x.BusinessUnitId, x.DefinitionId, x.DateValue });
            entity.HasOne<CustomFieldDefinition>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.DefinitionId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<CustomFieldVersion>().WithMany()
                .HasForeignKey(x => new { x.DefinitionId, x.DefinitionVersion })
                .HasPrincipalKey(x => new { x.DefinitionId, x.VersionNumber })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CustomFieldValueHistory>(entity =>
        {
            entity.ToTable("custom_field_value_history");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ChangeType).HasMaxLength(30).IsRequired();
            entity.Property(x => x.ChangedBy).HasMaxLength(200).IsRequired();
            entity.Property(x => x.CorrelationId).HasMaxLength(100).IsRequired();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(160).IsRequired();
            entity.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Reason).HasMaxLength(1000);
            entity.HasIndex(x => new { x.BusinessUnitId, x.CustomFieldValueId, x.ChangedOn });
            entity.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey }).IsUnique();
            entity.HasOne<CustomFieldValue>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.CustomFieldValueId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        return modelBuilder;
    }
}
