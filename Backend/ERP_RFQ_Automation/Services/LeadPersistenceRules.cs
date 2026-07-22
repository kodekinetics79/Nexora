using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace ERP_RFQ_Automation.Services;

internal static class LeadPersistenceRules
{
    public static void Prepare(ErpRfqAutomationContext context)
    {
        EnforceReferenceImmutability(context.ChangeTracker);

        // Production allocation and history are database-triggered. This equivalent
        // fallback keeps relational SQLite tests and offline tooling faithful to the
        // invariant without pretending SQLite offers PostgreSQL's concurrency model.
        if (context.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) != true)
            PrepareNonPostgresChanges(context);
    }

    private static void EnforceReferenceImmutability(ChangeTracker changeTracker)
    {
        foreach (var entry in changeTracker.Entries<Lead>().Where(e => e.State == EntityState.Modified))
        {
            var reference = entry.Property(e => e.CommercialCaseReference);
            if (reference.IsModified && !string.Equals(reference.OriginalValue, reference.CurrentValue, StringComparison.Ordinal))
                throw new InvalidOperationException("The permanent commercial-case reference cannot be changed.");

            var commercialCaseId = entry.Property(e => e.CommercialCaseId);
            if (commercialCaseId.IsModified && commercialCaseId.OriginalValue != commercialCaseId.CurrentValue)
                throw new InvalidOperationException("A lead cannot be moved to another commercial case.");

            var businessUnitId = entry.Property(e => e.BusinessUnitId);
            if (businessUnitId.IsModified && businessUnitId.OriginalValue != businessUnitId.CurrentValue)
                throw new InvalidOperationException("A lead cannot be moved to another tenant.");
        }

        foreach (var entry in changeTracker.Entries<CommercialCase>().Where(e => e.State == EntityState.Modified))
        {
            var reference = entry.Property(e => e.MasterReference);
            var allocation = entry.Property(e => e.AllocationNumber);
            if ((reference.IsModified && !string.Equals(reference.OriginalValue, reference.CurrentValue, StringComparison.Ordinal))
                || (allocation.IsModified && allocation.OriginalValue != allocation.CurrentValue))
                throw new InvalidOperationException("The permanent commercial-case identity cannot be changed.");

            var businessUnitId = entry.Property(e => e.BusinessUnitId);
            if (businessUnitId.IsModified && businessUnitId.OriginalValue != businessUnitId.CurrentValue)
                throw new InvalidOperationException("A commercial case cannot be moved to another tenant.");
        }
    }

    private static void PrepareNonPostgresChanges(ErpRfqAutomationContext context)
    {
        var addedLeads = context.ChangeTracker.Entries<Lead>()
            .Where(e => e.State == EntityState.Added && string.IsNullOrWhiteSpace(e.Entity.CommercialCaseReference))
            .ToArray();
        var nextAllocation = addedLeads.Length == 0
            ? 0
            : context.CommercialCases.IgnoreQueryFilters().Select(c => (long?)c.AllocationNumber).Max() ?? 0;

        foreach (var entry in addedLeads)
        {
            var lead = entry.Entity;
            var configuration = context.LeadReferenceConfigurations.Local
                .FirstOrDefault(c => c.BusinessUnitId == lead.BusinessUnitId)
                ?? context.LeadReferenceConfigurations.Find(lead.BusinessUnitId);
            if (configuration == null)
            {
                configuration = new LeadReferenceConfiguration
                {
                    BusinessUnitId = lead.BusinessUnitId,
                    CreatedOn = DateTime.UtcNow
                };
                context.LeadReferenceConfigurations.Add(configuration);
            }

            var createdOn = lead.CreatedDate == default ? DateTime.UtcNow : lead.CreatedDate;
            nextAllocation++;
            var businessUnitCode = context.BusinessUnits.Local
                .FirstOrDefault(b => b.Id == lead.BusinessUnitId)?.BusinessUnitCode
                ?? context.BusinessUnits.Where(b => b.Id == lead.BusinessUnitId).Select(b => b.BusinessUnitCode).Single();
            var masterReference = LeadReferenceFormatter.Format(
                configuration, businessUnitCode, lead.LeadSource, createdOn, nextAllocation);
            var commercialCase = new CommercialCase
            {
                BusinessUnitId = lead.BusinessUnitId,
                CreatedOn = createdOn,
                CreatedBy = string.IsNullOrWhiteSpace(lead.CreatedBy) ? "System" : lead.CreatedBy
            };
            commercialCase.AssignIdentity(nextAllocation, masterReference);
            lead.AssignCommercialCase(commercialCase);
            // Some high-volume persistence paths disable automatic change detection.
            // Track the newly attached principal explicitly so EF orders the case
            // insert before the Lead foreign key in those paths as well.
            context.CommercialCases.Add(commercialCase);

            context.LeadStatusHistories.Add(CreateHistory(entry, "Created", null, lead.LeadStatusId));
        }

        foreach (var entry in context.ChangeTracker.Entries<Lead>().Where(e => e.State == EntityState.Modified))
        {
            var status = entry.Property(e => e.LeadStatusId);
            if (status.IsModified && status.OriginalValue != status.CurrentValue)
                context.LeadStatusHistories.Add(CreateHistory(entry, "StatusChanged", status.OriginalValue, status.CurrentValue));
        }

        // The extraction persister deliberately disables automatic change detection
        // for large item graphs. The fallback adds principals and relationships during
        // SaveChanges, so run one explicit pass to propagate temporary case keys and
        // establish insert ordering. PostgreSQL uses triggers and never enters here.
        context.ChangeTracker.DetectChanges();
    }

    private static LeadStatusHistory CreateHistory(
        EntityEntry<Lead> entry,
        string eventType,
        long? previousStatusId,
        long? newStatusId)
    {
        var lead = entry.Entity;
        var history = new LeadStatusHistory
        {
            Lead = lead,
            BusinessUnitId = lead.BusinessUnitId,
            PreviousStatusId = previousStatusId,
            NewStatusId = newStatusId,
            EventType = eventType,
            ChangedBy = eventType == "Created" && !string.IsNullOrWhiteSpace(lead.CreatedBy) ? lead.CreatedBy : null,
            ActorSource = eventType == "Created" ? "LeadCreation" : "PersistenceFallback",
            ChangedOn = DateTime.UtcNow,
            Reason = lead.AssignComment,
            CommercialCaseReference = lead.CommercialCaseReference
        };

        if (lead.CommercialCase != null)
            history.CommercialCase = lead.CommercialCase;
        else
            history.CommercialCaseId = lead.CommercialCaseId;

        return history;
    }
}
