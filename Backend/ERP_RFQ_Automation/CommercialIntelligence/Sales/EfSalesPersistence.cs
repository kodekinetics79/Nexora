using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.CommercialIntelligence.Sales;

public sealed class EfSalesPersistence(ErpRfqAutomationContext db) : ISalesPersistence
{
    public Task<bool> UserExistsAsync(long tenant, long userId, CancellationToken ct) =>
        db.Users.AnyAsync(x => x.Buid == tenant && x.Id == userId && x.IsActive != false, ct);
    public Task<bool> CustomerExistsAsync(long tenant, long customerId, CancellationToken ct) =>
        db.Customers.AnyAsync(x => x.Buid == tenant && x.Id == customerId, ct);
    public Task<bool> LeadAssignmentExistsAsync(long tenant, long assignmentId, CancellationToken ct) =>
        db.Set<CommercialRouting.LeadAssignment>().AnyAsync(
            x => x.BusinessUnitId == tenant && x.Id == assignmentId, ct);
    public Task<bool> AggregateExistsAsync(long tenant, string aggregateType, long aggregateId, CancellationToken ct) =>
        aggregateType.Trim().ToUpperInvariant() switch
        {
            "LEAD" => db.Leads.AnyAsync(x => x.BusinessUnitId == tenant && x.Id == aggregateId, ct),
            "RFQ" => db.Rfqs.AnyAsync(x => x.BusinessUnitId == tenant && x.Id == aggregateId, ct),
            "QUOTE" => db.Quotes.AnyAsync(x => x.BusinessUnitId == tenant && x.Id == aggregateId, ct),
            "ORDER" => db.Orders.AnyAsync(x => x.BusinessUnitId == tenant && x.Id == aggregateId, ct),
            "CUSTOMER" => db.Customers.AnyAsync(x => x.Buid == tenant && x.Id == aggregateId, ct),
            _ => Task.FromResult(false)
        };
    public Task<SalesRepProfile?> GetProfileAsync(long tenant, long userId, CancellationToken ct) =>
        db.SalesRepProfiles.SingleOrDefaultAsync(x => x.BusinessUnitId == tenant && x.UserId == userId, ct);
    public Task<SalesRepProfile?> FindProfileMutationAsync(long tenant, string key, CancellationToken ct) =>
        db.SalesRepProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.BusinessUnitId == tenant && x.LastMutationIdempotencyKey == key, ct);

    public async Task<SalesRepProfile> SaveProfileAsync(SalesRepProfile profile, long expectedVersion, string key, CancellationToken ct)
    {
        profile.LastMutationIdempotencyKey = key;
        if (profile.Id == 0) db.SalesRepProfiles.Add(profile);
        await SaveAsync(ct);
        return profile;
    }

    public Task<CommercialActivity?> FindActivityAsync(long tenant, string key, CancellationToken ct) =>
        db.CommercialActivities.AsNoTracking().SingleOrDefaultAsync(x => x.BusinessUnitId == tenant && x.IdempotencyKey == key, ct);
    public async Task<CommercialActivity> AppendActivityAsync(CommercialActivity value, CancellationToken ct)
    { db.CommercialActivities.Add(value); await SaveAsync(ct); return value; }
    public Task<FollowUpTask?> FindFollowUpByCreationKeyAsync(long tenant, string key, CancellationToken ct) =>
        db.FollowUpTasks.AsNoTracking().SingleOrDefaultAsync(x => x.BusinessUnitId == tenant && x.CreationIdempotencyKey == key, ct);
    public async Task<FollowUpTask> CreateFollowUpAsync(FollowUpTask value, CancellationToken ct)
    { db.FollowUpTasks.Add(value); await SaveAsync(ct); return value; }

    public async Task<(FollowUpTask Task, FollowUpTransitionEvent? Replay)> GetFollowUpForTransitionAsync(long tenant, long taskId, string key, CancellationToken ct)
    {
        var replay = await db.FollowUpTransitionEvents.AsNoTracking()
            .SingleOrDefaultAsync(x => x.BusinessUnitId == tenant && x.IdempotencyKey == key, ct);
        var task = await db.FollowUpTasks.SingleOrDefaultAsync(x => x.BusinessUnitId == tenant && x.Id == taskId, ct)
            ?? throw new SalesNotFoundException("Follow-up was not found.");
        return (task, replay);
    }

    public async Task<FollowUpTransitionEvent> TransitionFollowUpAsync(FollowUpTask task, FollowUpTransitionEvent transition, long expectedVersion, CancellationToken ct)
    {
        db.Entry(task).Property(x => x.Version).OriginalValue = expectedVersion;
        db.FollowUpTransitionEvents.Add(transition);
        await SaveAsync(ct);
        return transition;
    }

    public Task<SalesContribution?> FindContributionAsync(long tenant, string key, CancellationToken ct) =>
        db.SalesContributions.AsNoTracking().SingleOrDefaultAsync(x => x.BusinessUnitId == tenant && x.IdempotencyKey == key, ct);
    public async Task<SalesContribution> AppendContributionAsync(SalesContribution value, CancellationToken ct)
    { db.SalesContributions.Add(value); await SaveAsync(ct); return value; }

    public async Task<IReadOnlyList<CommercialActivity>> QueryActivitiesAsync(long tenant, DateTime from, DateTime to, long? userId, CancellationToken ct) =>
        await db.CommercialActivities.AsNoTracking().Where(x => x.BusinessUnitId == tenant && x.OccurredAtUtc >= from && x.OccurredAtUtc < to && (!userId.HasValue || x.SalesRepUserId == userId)).ToListAsync(ct);
    public async Task<IReadOnlyList<FollowUpTask>> QueryFollowUpsAsync(long tenant, DateTime from, DateTime to, long? userId, CancellationToken ct) =>
        await db.FollowUpTasks.AsNoTracking().Where(x => x.BusinessUnitId == tenant
            && (!userId.HasValue || x.AssignedToUserId == userId)
            && (x.CreatedAtUtc >= from && x.CreatedAtUtc < to
                || db.FollowUpTransitionEvents.Any(t => t.BusinessUnitId == tenant
                    && t.FollowUpTaskId == x.Id && t.OccurredAtUtc >= from && t.OccurredAtUtc < to)))
            .ToListAsync(ct);
    public async Task<IReadOnlyList<FollowUpTransitionEvent>> QueryFollowUpTransitionsAsync(long tenant, DateTime from, DateTime to, long? userId, CancellationToken ct)
    {
        var taskIds = db.FollowUpTasks.Where(x => x.BusinessUnitId == tenant && (!userId.HasValue || x.AssignedToUserId == userId)).Select(x => x.Id);
        return await db.FollowUpTransitionEvents.AsNoTracking().Where(x => x.BusinessUnitId == tenant && taskIds.Contains(x.FollowUpTaskId) && x.OccurredAtUtc >= from && x.OccurredAtUtc < to).ToListAsync(ct);
    }
    public async Task<IReadOnlyList<SalesContribution>> QueryContributionsAsync(long tenant, DateTime from, DateTime to, long? userId, CancellationToken ct) =>
        await db.SalesContributions.AsNoTracking().Where(x => x.BusinessUnitId == tenant && x.RecognizedAtUtc >= from && x.RecognizedAtUtc < to && (!userId.HasValue || x.SalesRepUserId == userId)).ToListAsync(ct);

    private async Task SaveAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new SalesConflictException("The sales record changed. Refresh and retry."); }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true)
        { throw new SalesConflictException("The request was already recorded."); }
    }
}
