using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace ERP_RFQ_Automation.CommercialCases.Lifecycle;

public static class LifecycleGovernanceInterceptor
{
    public static void Validate(ChangeTracker tracker)
    {
        if (tracker.Entries<CommercialLifecycleEvent>()
            .Any(entry => entry.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Commercial lifecycle events are append-only.");

        foreach (var entry in tracker.Entries<Lead>().Where(entry => entry.State == EntityState.Modified))
        {
            if (!entry.Property(nameof(Lead.LeadStatusId)).IsModified) continue;
            RequireMatchingEvent(tracker, "Lead", entry.Entity.Id, entry.Entity.BusinessUnitId,
                entry.Property(nameof(Lead.LifecycleVersion)).OriginalValue as int? ?? 0,
                entry.Entity.LifecycleVersion, entry.Entity.LeadStatusId);
        }

        foreach (var entry in tracker.Entries<Rfq>().Where(entry => entry.State == EntityState.Modified))
        {
            if (!entry.Property(nameof(Rfq.RfqstatusId)).IsModified) continue;
            RequireMatchingEvent(tracker, "Rfq", entry.Entity.Id, entry.Entity.BusinessUnitId,
                entry.Property(nameof(Rfq.LifecycleVersion)).OriginalValue as int? ?? 0,
                entry.Entity.LifecycleVersion, entry.Entity.RfqstatusId);
        }
    }

    private static void RequireMatchingEvent(
        ChangeTracker tracker, string aggregateType, long aggregateId, long businessUnitId,
        int originalVersion, int newVersion, long? newStatusId)
    {
        var hasEvent = tracker.Entries<CommercialLifecycleEvent>().Any(entry =>
            entry.State == EntityState.Added &&
            entry.Entity.AggregateType == aggregateType &&
            entry.Entity.AggregateId == aggregateId &&
            entry.Entity.BusinessUnitId == businessUnitId &&
            entry.Entity.NewStatusId == newStatusId &&
            entry.Entity.AggregateVersion == newVersion);

        if (newVersion != originalVersion + 1 || !hasEvent)
            throw new InvalidOperationException(
                $"{aggregateType} status must be changed through the governed lifecycle command.");
    }
}
