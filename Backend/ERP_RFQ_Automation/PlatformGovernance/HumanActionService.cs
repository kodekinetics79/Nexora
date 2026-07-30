using System.Data;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.PlatformGovernance;

public sealed class HumanActionService(ErpRfqAutomationContext db)
{
    public async Task<IReadOnlyList<HumanActionItemDto>> ListAsync(long tenantId,
        HumanActionStatus? status, CancellationToken ct)
    {
        PlatformGovernanceService.EnsureTenant(tenantId);
        var query = db.HumanActionItems.AsNoTracking().Where(x => x.BusinessUnitId == tenantId);
        if (status.HasValue) query = query.Where(x => x.Status == status);
        return await query.OrderByDescending(x => x.Priority).ThenBy(x => x.DueOn)
            .Select(x => Map(x)).Take(250).ToListAsync(ct);
    }

    public async Task<HumanActionTransitionResult> CreateAsync(long tenantId, long actorUserId,
        string idempotencyKey, CreateHumanActionCommand command, CancellationToken ct)
    {
        PlatformGovernanceService.EnsureActor(tenantId, actorUserId);
        idempotencyKey = PlatformGovernanceService.Required(idempotencyKey, 160,
            "Idempotency-Key is required.");
        if (command.Confidence is < 0 or > 1)
            throw new PlatformGovernanceValidationException("Confidence must be between zero and one.");
        if (command.DueOn <= DateTime.UtcNow.AddMinutes(-1))
            throw new PlatformGovernanceValidationException("Due date must be in the future.");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var replay = await db.HumanActionEvents.AsNoTracking().SingleOrDefaultAsync(
            x => x.BusinessUnitId == tenantId && x.IdempotencyKey == idempotencyKey, ct);
        if (replay is not null)
            return new(Map(await ItemAsync(tenantId, replay.HumanActionItemId, ct)), true);
        var now = DateTime.UtcNow;
        var item = new HumanActionItem
        {
            BusinessUnitId = tenantId,
            ActionType = PlatformGovernanceService.Required(command.ActionType, 64, "Action type is required."),
            SourceType = PlatformGovernanceService.Required(command.SourceType, 64, "Source type is required."),
            SourceReference = PlatformGovernanceService.Required(command.SourceReference, 200, "Source reference is required."),
            Title = PlatformGovernanceService.Required(command.Title, 240, "Title is required."),
            Summary = PlatformGovernanceService.Required(command.Summary, 2000, "Summary is required."),
            Recommendation = PlatformGovernanceService.Required(command.Recommendation, 2000, "Recommendation is required."),
            EvidenceJson = ValidateJson(command.EvidenceJson),
            Confidence = command.Confidence,
            CommercialImpact = PlatformGovernanceService.Required(command.CommercialImpact, 1000, "Commercial impact is required."),
            ResumeActionCode = PlatformGovernanceService.Required(command.ResumeActionCode, 80, "Resume action code is required."),
            Priority = command.Priority,
            AssignedToUserId = command.AssignedToUserId,
            DueOn = command.DueOn.ToUniversalTime(),
            CreatedOn = now,
            CreatedByUserId = actorUserId,
            UpdatedOn = now
        };
        item.Events.Add(new HumanActionEvent
        {
            BusinessUnitId = tenantId,
            ToStatus = HumanActionStatus.Open,
            Action = "CREATED",
            Comment = "Human review requested",
            IdempotencyKey = idempotencyKey,
            ActorUserId = actorUserId,
            OccurredOn = now
        });
        db.HumanActionItems.Add(item);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(Map(item), false);
    }

    public async Task<HumanActionTransitionResult> TransitionAsync(long tenantId, long itemId,
        long actorUserId, string idempotencyKey, TransitionHumanActionCommand command,
        CancellationToken ct)
    {
        PlatformGovernanceService.EnsureActor(tenantId, actorUserId);
        idempotencyKey = PlatformGovernanceService.Required(idempotencyKey, 160,
            "Idempotency-Key is required.");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var replay = await db.HumanActionEvents.AsNoTracking().SingleOrDefaultAsync(
            x => x.BusinessUnitId == tenantId && x.IdempotencyKey == idempotencyKey, ct);
        if (replay is not null)
            return new(Map(await ItemAsync(tenantId, replay.HumanActionItemId, ct)), true);
        var item = await ItemAsync(tenantId, itemId, ct);
        if (item.Version != command.ExpectedVersion)
            throw new PlatformGovernanceConflictException($"Action version is {item.Version}; refresh and retry.");
        if (item.Status is HumanActionStatus.Completed or HumanActionStatus.Rejected)
            throw new PlatformGovernanceConflictException("A completed decision is immutable.");
        if (command.TargetStatus == HumanActionStatus.Open)
            throw new PlatformGovernanceValidationException("A transitioned action cannot return to Open.");
        var prior = item.Status;
        item.Status = command.TargetStatus;
        item.AssignedToUserId = command.AssignedToUserId ?? item.AssignedToUserId;
        item.Version++;
        item.UpdatedOn = DateTime.UtcNow;
        db.HumanActionEvents.Add(new HumanActionEvent
        {
            BusinessUnitId = tenantId,
            HumanActionItemId = itemId,
            FromStatus = prior,
            ToStatus = command.TargetStatus,
            Action = PlatformGovernanceService.Required(command.Action, 32, "Action is required.").ToUpperInvariant(),
            Comment = PlatformGovernanceService.Required(command.Comment, 2000, "A decision comment is required."),
            IdempotencyKey = idempotencyKey,
            ActorUserId = actorUserId,
            OccurredOn = item.UpdatedOn
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(Map(item), false);
    }

    private async Task<HumanActionItem> ItemAsync(long tenantId, long id, CancellationToken ct) =>
        await db.HumanActionItems.SingleOrDefaultAsync(x => x.BusinessUnitId == tenantId && x.Id == id, ct)
        ?? throw new PlatformGovernanceNotFoundException("The human action was not found.");

    private static HumanActionItemDto Map(HumanActionItem x) => new(x.Id, x.ActionType,
        x.SourceType, x.SourceReference, x.Title, x.Summary, x.Recommendation, x.EvidenceJson,
        x.Confidence, x.CommercialImpact, x.ResumeActionCode, x.Priority, x.Status,
        x.AssignedToUserId, x.DueOn, x.DueOn < DateTime.UtcNow
            && x.Status is not (HumanActionStatus.Completed or HumanActionStatus.Rejected),
        x.Version, x.UpdatedOn);

    private static string ValidateJson(string value)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(value);
            return document.RootElement.GetRawText();
        }
        catch (System.Text.Json.JsonException)
        {
            throw new PlatformGovernanceValidationException("Evidence must be valid JSON.");
        }
    }
}
