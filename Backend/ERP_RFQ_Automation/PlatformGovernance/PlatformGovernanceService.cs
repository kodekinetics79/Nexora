using System.Data;
using System.Text.Json;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.PlatformGovernance;

public sealed class PlatformGovernanceService(ErpRfqAutomationContext db)
{
    private const int MaximumDefinitionBytes = 65_536;

    public async Task<IReadOnlyList<GovernedArtifactSummary>> ListAsync(long tenantId,
        GovernedArtifactType? type, string? search, CancellationToken ct)
    {
        EnsureTenant(tenantId);
        var query = db.GovernedArtifacts.AsNoTracking().Where(x => x.BusinessUnitId == tenantId);
        if (type.HasValue) query = query.Where(x => x.ArtifactType == type);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(x => x.Name.ToLower().Contains(term)
                || x.ArtifactKey.ToLower().Contains(term));
        }
        return await query.OrderBy(x => x.ArtifactType).ThenBy(x => x.Name)
            .Select(x => Summary(x)).Take(250).ToListAsync(ct);
    }

    public async Task<GovernedArtifactDetail> GetAsync(long tenantId, long artifactId,
        CancellationToken ct)
    {
        EnsureTenant(tenantId);
        var artifact = await db.GovernedArtifacts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.BusinessUnitId == tenantId && x.Id == artifactId, ct)
            ?? throw new PlatformGovernanceNotFoundException("The governed artifact was not found.");
        var versions = await db.GovernedArtifactVersions.AsNoTracking()
            .Where(x => x.BusinessUnitId == tenantId && x.GovernedArtifactId == artifactId)
            .OrderByDescending(x => x.VersionNumber)
            .Select(x => new GovernedArtifactVersionItem(x.Id, x.VersionNumber, x.Status,
                x.DefinitionJson, x.ChangeSummary, x.CreatedOn, x.CreatedByUserId,
                x.TestedOn, x.PublishedOn)).ToListAsync(ct);
        var events = await db.GovernedArtifactEvents.AsNoTracking()
            .Where(x => x.BusinessUnitId == tenantId && x.GovernedArtifactId == artifactId)
            .OrderByDescending(x => x.OccurredOn)
            .Select(x => new GovernedArtifactEventItem(x.Id, x.ArtifactVersionNumber,
                x.Action, x.Reason, x.OccurredOn, x.ActorUserId)).Take(100).ToListAsync(ct);
        return new(Summary(artifact), versions, events);
    }

    public async Task<ArtifactTransitionResult> CreateAsync(long tenantId, long actorUserId,
        string idempotencyKey, CreateGovernedArtifactCommand command, CancellationToken ct)
    {
        EnsureActor(tenantId, actorUserId);
        idempotencyKey = Required(idempotencyKey, 160, "Idempotency-Key is required.");
        var definition = ValidateDefinition(command.ArtifactType, command.DefinitionJson);
        var artifactKey = NormalizeKey(command.ArtifactKey);
        var now = DateTime.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var replay = await EventReplayAsync(tenantId, idempotencyKey, ct);
        if (replay is not null)
            return new(Summary(await ArtifactAsync(tenantId, replay.GovernedArtifactId, ct)), true);

        if (await db.GovernedArtifacts.AnyAsync(x => x.BusinessUnitId == tenantId
                && x.ArtifactType == command.ArtifactType && x.ArtifactKey == artifactKey, ct))
            throw new PlatformGovernanceConflictException("An artifact with this type and key already exists.");

        var artifact = new GovernedArtifact
        {
            BusinessUnitId = tenantId,
            ArtifactType = command.ArtifactType,
            ArtifactKey = artifactKey,
            Name = Required(command.Name, 200, "Name is required."),
            Description = Optional(command.Description, 1000),
            CreatedOn = now,
            CreatedByUserId = actorUserId,
            UpdatedOn = now,
            UpdatedByUserId = actorUserId
        };
        artifact.Versions.Add(new GovernedArtifactVersion
        {
            BusinessUnitId = tenantId,
            VersionNumber = 1,
            DefinitionJson = definition,
            ChangeSummary = Required(command.ChangeSummary, 1000, "A change summary is required."),
            CreatedOn = now,
            CreatedByUserId = actorUserId
        });
        artifact.Events.Add(NewEvent(tenantId, 1, "CREATED", "Initial governed version",
            definition, idempotencyKey, actorUserId, now));
        db.GovernedArtifacts.Add(artifact);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(Summary(artifact), false);
    }

    public async Task<ArtifactTransitionResult> CreateVersionAsync(long tenantId, long artifactId,
        long actorUserId, string idempotencyKey, CreateGovernedArtifactVersionCommand command,
        CancellationToken ct)
    {
        EnsureActor(tenantId, actorUserId);
        idempotencyKey = Required(idempotencyKey, 160, "Idempotency-Key is required.");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var replay = await EventReplayAsync(tenantId, idempotencyKey, ct);
        if (replay is not null)
            return new(Summary(await ArtifactAsync(tenantId, replay.GovernedArtifactId, ct)), true);
        var artifact = await ArtifactAsync(tenantId, artifactId, ct);
        EnsureVersion(artifact, command.ExpectedVersion);
        if (artifact.Status == GovernedLifecycleStatus.Archived)
            throw new PlatformGovernanceConflictException("Restore the artifact before creating a version.");
        var definition = ValidateDefinition(artifact.ArtifactType, command.DefinitionJson);
        var number = checked(artifact.CurrentVersionNumber + 1);
        var now = DateTime.UtcNow;
        db.GovernedArtifactVersions.Add(new GovernedArtifactVersion
        {
            BusinessUnitId = tenantId,
            GovernedArtifactId = artifactId,
            VersionNumber = number,
            DefinitionJson = definition,
            ChangeSummary = Required(command.ChangeSummary, 1000, "A change summary is required."),
            CreatedOn = now,
            CreatedByUserId = actorUserId
        });
        artifact.CurrentVersionNumber = number;
        artifact.Status = GovernedLifecycleStatus.Draft;
        Touch(artifact, actorUserId, now);
        db.GovernedArtifactEvents.Add(NewEvent(tenantId, number, "VERSION_CREATED",
            command.ChangeSummary, definition, idempotencyKey, actorUserId, now, artifactId));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(Summary(artifact), false);
    }

    public async Task<ArtifactTransitionResult> TransitionAsync(long tenantId, long artifactId,
        long actorUserId, string idempotencyKey, TransitionGovernedArtifactCommand command,
        CancellationToken ct)
    {
        EnsureActor(tenantId, actorUserId);
        idempotencyKey = Required(idempotencyKey, 160, "Idempotency-Key is required.");
        var action = Required(command.Action, 32, "Action is required.").ToUpperInvariant();
        var reason = Required(command.Reason, 1000, "A governance reason is required.");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var replay = await EventReplayAsync(tenantId, idempotencyKey, ct);
        if (replay is not null)
            return new(Summary(await ArtifactAsync(tenantId, replay.GovernedArtifactId, ct)), true);
        var artifact = await ArtifactAsync(tenantId, artifactId, ct);
        EnsureVersion(artifact, command.ExpectedVersion);
        var now = DateTime.UtcNow;
        var current = await db.GovernedArtifactVersions.SingleAsync(x => x.BusinessUnitId == tenantId
            && x.GovernedArtifactId == artifactId
            && x.VersionNumber == artifact.CurrentVersionNumber, ct);

        switch (action)
        {
            case "TEST":
                if (artifact.Status != GovernedLifecycleStatus.Draft)
                    throw new PlatformGovernanceConflictException("Only a draft version can enter test.");
                ValidateDefinition(artifact.ArtifactType, current.DefinitionJson);
                artifact.Status = current.Status = GovernedLifecycleStatus.Test;
                current.TestedOn = now;
                break;
            case "PUBLISH":
                if (artifact.Status != GovernedLifecycleStatus.Test)
                    throw new PlatformGovernanceConflictException("Only a tested version can be published.");
                var prior = await db.GovernedArtifactVersions.Where(x => x.BusinessUnitId == tenantId
                    && x.GovernedArtifactId == artifactId && x.Status == GovernedLifecycleStatus.Production)
                    .ToListAsync(ct);
                foreach (var version in prior) version.Status = GovernedLifecycleStatus.Archived;
                artifact.Status = current.Status = GovernedLifecycleStatus.Production;
                artifact.ProductionVersionNumber = current.VersionNumber;
                current.PublishedOn = now;
                break;
            case "ROLLBACK":
                if (!command.TargetVersionNumber.HasValue)
                    throw new PlatformGovernanceValidationException("A rollback target version is required.");
                var target = await db.GovernedArtifactVersions.SingleOrDefaultAsync(x =>
                    x.BusinessUnitId == tenantId && x.GovernedArtifactId == artifactId
                    && x.VersionNumber == command.TargetVersionNumber.Value, ct)
                    ?? throw new PlatformGovernanceNotFoundException("The rollback version was not found.");
                if (!target.PublishedOn.HasValue)
                    throw new PlatformGovernanceConflictException("Only a previously published version can be restored.");
                var production = await db.GovernedArtifactVersions.Where(x => x.BusinessUnitId == tenantId
                    && x.GovernedArtifactId == artifactId && x.Status == GovernedLifecycleStatus.Production)
                    .ToListAsync(ct);
                foreach (var version in production) version.Status = GovernedLifecycleStatus.Archived;
                target.Status = GovernedLifecycleStatus.Production;
                artifact.CurrentVersionNumber = target.VersionNumber;
                artifact.ProductionVersionNumber = target.VersionNumber;
                artifact.Status = GovernedLifecycleStatus.Production;
                break;
            case "ARCHIVE":
                if (artifact.Status == GovernedLifecycleStatus.Archived)
                    throw new PlatformGovernanceConflictException("The artifact is already archived.");
                artifact.Status = GovernedLifecycleStatus.Archived;
                break;
            case "RESTORE":
                if (artifact.Status != GovernedLifecycleStatus.Archived)
                    throw new PlatformGovernanceConflictException("Only an archived artifact can be restored.");
                artifact.Status = GovernedLifecycleStatus.Draft;
                current.Status = GovernedLifecycleStatus.Draft;
                break;
            default:
                throw new PlatformGovernanceValidationException("Supported actions are TEST, PUBLISH, ROLLBACK, ARCHIVE and RESTORE.");
        }

        Touch(artifact, actorUserId, now);
        db.GovernedArtifactEvents.Add(NewEvent(tenantId, artifact.CurrentVersionNumber, action,
            reason, current.DefinitionJson, idempotencyKey, actorUserId, now, artifactId));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(Summary(artifact), false);
    }

    private async Task<GovernedArtifact> ArtifactAsync(long tenantId, long artifactId, CancellationToken ct) =>
        await db.GovernedArtifacts.SingleOrDefaultAsync(x => x.BusinessUnitId == tenantId && x.Id == artifactId, ct)
        ?? throw new PlatformGovernanceNotFoundException("The governed artifact was not found.");

    private Task<GovernedArtifactEvent?> EventReplayAsync(long tenantId, string key, CancellationToken ct) =>
        db.GovernedArtifactEvents.AsNoTracking().SingleOrDefaultAsync(
            x => x.BusinessUnitId == tenantId && x.IdempotencyKey == key, ct);

    private static GovernedArtifactEvent NewEvent(long tenantId, int version, string action,
        string reason, string snapshot, string key, long actor, DateTime now, long artifactId = 0) => new()
    {
        BusinessUnitId = tenantId,
        GovernedArtifactId = artifactId,
        ArtifactVersionNumber = version,
        Action = action,
        Reason = reason,
        SnapshotJson = snapshot,
        IdempotencyKey = key,
        ActorUserId = actor,
        OccurredOn = now
    };

    private static GovernedArtifactSummary Summary(GovernedArtifact x) => new(x.Id, x.ArtifactType,
        x.ArtifactKey, x.Name, x.Description, x.Status, x.CurrentVersionNumber,
        x.ProductionVersionNumber, x.Version, x.UpdatedOn, x.UpdatedByUserId);

    private static void Touch(GovernedArtifact artifact, long actor, DateTime now)
    {
        artifact.Version = checked(artifact.Version + 1);
        artifact.UpdatedByUserId = actor;
        artifact.UpdatedOn = now;
    }

    private static void EnsureVersion(GovernedArtifact artifact, long expected)
    {
        if (artifact.Version != expected)
            throw new PlatformGovernanceConflictException(
                $"Artifact version is {artifact.Version}; refresh and retry.");
    }

    internal static string ValidateDefinition(GovernedArtifactType type, string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || System.Text.Encoding.UTF8.GetByteCount(value) > MaximumDefinitionBytes)
            throw new PlatformGovernanceValidationException("Definition JSON is required and must not exceed 64 KiB.");
        JsonDocument document;
        try { document = JsonDocument.Parse(value); }
        catch (JsonException) { throw new PlatformGovernanceValidationException("Definition must be valid JSON."); }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new PlatformGovernanceValidationException("Definition must be a JSON object.");
            var required = type switch
            {
                GovernedArtifactType.CommercialTaxonomy => "documentType",
                GovernedArtifactType.DocumentSkill => "taxonomyKey",
                GovernedArtifactType.Model => "modelKind",
                GovernedArtifactType.Rule => "condition",
                GovernedArtifactType.Dataset => "purpose",
                GovernedArtifactType.Connector => "connectorType",
                GovernedArtifactType.TestSuite => "requirements",
                GovernedArtifactType.ReleaseCandidate => "releaseVersion",
                _ => throw new PlatformGovernanceValidationException("Unsupported artifact type.")
            };
            if (!document.RootElement.TryGetProperty(required, out _))
                throw new PlatformGovernanceValidationException($"Definition requires '{required}'.");
            var additional = type switch
            {
                GovernedArtifactType.Model => new[] { "purpose", "evaluationDatasetKey", "external" },
                GovernedArtifactType.Rule => new[] { "outcome", "evidenceRequired" },
                GovernedArtifactType.Dataset => new[] { "scope", "retentionDays", "sourceReferences" },
                GovernedArtifactType.Connector => new[] { "contractVersion", "authMode",
                    "credentialReference", "actions", "eventTriggers", "webhooks", "polling",
                    "fieldMappings", "idempotency", "retryPolicy", "deadLetterPolicy",
                    "rateLimit", "health", "sandbox" },
                _ => Array.Empty<string>()
            };
            foreach (var property in additional)
                if (!document.RootElement.TryGetProperty(property, out _))
                    throw new PlatformGovernanceValidationException($"Definition requires '{property}'.");
            if (type == GovernedArtifactType.Model
                && document.RootElement.TryGetProperty("external", out var external)
                && external.ValueKind == JsonValueKind.True
                && (!document.RootElement.TryGetProperty("endpointReference", out var endpoint)
                    || endpoint.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(endpoint.GetString())))
                throw new PlatformGovernanceValidationException(
                    "External models require a governed endpoint reference.");
            RejectEmbeddedSecrets(document.RootElement);
            return document.RootElement.GetRawText();
        }
    }

    private static void RejectEmbeddedSecrets(JsonElement element)
    {
        foreach (var property in element.EnumerateObject())
        {
            var key = property.Name.ToLowerInvariant();
            if (key is not ("secretreference" or "credentialreference")
                && (key.Contains("password") || key.Contains("secret") || key.Contains("apikey")
                    || key.Contains("accesskey") || key.Contains("token"))
                && property.Value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(property.Value.GetString()))
                throw new PlatformGovernanceValidationException(
                    "Governed definitions may contain secret references, never embedded credentials.");
            if (property.Value.ValueKind == JsonValueKind.Object) RejectEmbeddedSecrets(property.Value);
            if (property.Value.ValueKind == JsonValueKind.Array)
                foreach (var child in property.Value.EnumerateArray())
                    if (child.ValueKind == JsonValueKind.Object) RejectEmbeddedSecrets(child);
        }
    }

    private static string NormalizeKey(string value)
    {
        var normalized = Required(value, 120, "Artifact key is required.").Trim().ToLowerInvariant();
        if (normalized.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_' or '.')))
            throw new PlatformGovernanceValidationException(
                "Artifact key may contain letters, numbers, hyphens, underscores and periods.");
        return normalized;
    }

    internal static string Required(string? value, int max, string message)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized) || normalized.Length > max)
            throw new PlatformGovernanceValidationException(message);
        return normalized;
    }

    internal static string Optional(string? value, int max)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length > max) throw new PlatformGovernanceValidationException($"Value must not exceed {max} characters.");
        return normalized;
    }

    internal static void EnsureActor(long tenantId, long actor)
    {
        EnsureTenant(tenantId);
        if (actor <= 0) throw new UnauthorizedAccessException("A valid authenticated actor is required.");
    }

    internal static void EnsureTenant(long tenantId)
    {
        if (tenantId <= 0) throw new UnauthorizedAccessException("A valid authenticated tenant is required.");
    }
}
