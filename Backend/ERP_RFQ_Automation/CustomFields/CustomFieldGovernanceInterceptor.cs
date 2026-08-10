using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ERP_RFQ_Automation.CustomFields;

/// <summary>
/// Enforces append-only schema versions and history plus retirement-instead-of-deletion.
/// Register this interceptor on the DbContext when the custom-field model is integrated.
/// </summary>
public sealed class CustomFieldGovernanceInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        Validate(eventData.Context?.ChangeTracker);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Validate(eventData.Context?.ChangeTracker);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public static void Validate(ChangeTracker? changeTracker)
    {
        if (changeTracker is null) return;

        foreach (var entry in changeTracker.Entries())
        {
            if (entry.State == EntityState.Deleted && IsGovernedEntity(entry.Entity))
                throw new CustomFieldDomainException(
                    "Custom-field records are governed records and cannot be deleted; retire definitions instead.");

            if (entry.State == EntityState.Modified && IsImmutableEntity(entry.Entity))
                throw new CustomFieldDomainException(
                    $"{entry.Metadata.ClrType.Name} is immutable; create a new version or history record.");

            // AA-01 · the stable key and the entity it attaches to are the identity of a
            // custom field. Values live in a jsonb bag keyed BY THAT STRING, so renaming the
            // key does not migrate anything — it orphans every value already written. There
            // is no service method that changes either field; this is the backstop that makes
            // that a guarantee rather than an accident of the current call graph.
            if (entry.State == EntityState.Modified && entry.Entity is CustomFieldDefinition)
            {
                foreach (var property in ImmutableDefinitionProperties)
                {
                    var member = entry.Property(property);
                    if (member.IsModified && !Equals(member.OriginalValue, member.CurrentValue))
                        throw new CustomFieldDomainException(
                            $"A custom field's {property} cannot be changed once created: stored values are keyed " +
                            "by it, so renaming would orphan every value already captured. Retire this field and " +
                            "create a new one instead.");
                }
            }
        }
    }

    private static readonly string[] ImmutableDefinitionProperties =
    [
        nameof(CustomFieldDefinition.StableKey),
        nameof(CustomFieldDefinition.EntityType),
        nameof(CustomFieldDefinition.BusinessUnitId)
    ];

    private static bool IsGovernedEntity(object entity) => entity is
        CustomFieldDefinition or CustomFieldVersion or CustomFieldOption or CustomFieldRule or
        CustomFieldDependency or CustomFieldRecord or CustomFieldValue;

    private static bool IsImmutableEntity(object entity) => entity is
        CustomFieldVersion or CustomFieldOption or CustomFieldRule or CustomFieldDependency;
}
