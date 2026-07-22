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
        }
    }

    private static bool IsGovernedEntity(object entity) => entity is
        CustomFieldDefinition or CustomFieldVersion or CustomFieldOption or CustomFieldRule or
        CustomFieldDependency or CustomFieldRecord or CustomFieldValue or CustomFieldValueHistory;

    private static bool IsImmutableEntity(object entity) => entity is
        CustomFieldVersion or CustomFieldOption or CustomFieldRule or
        CustomFieldDependency or CustomFieldValueHistory;
}
