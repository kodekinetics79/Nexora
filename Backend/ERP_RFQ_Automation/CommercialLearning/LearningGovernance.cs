using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.SupplierQuotes;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.CommercialLearning;

public static class LearningGovernanceActions
{
    public const string Approved = "APPROVED";
    public const string Disabled = "DISABLED";
    public const string RolledBack = "ROLLED_BACK";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [Approved, Disabled, RolledBack], StringComparer.Ordinal);
}

[Table("learning_governance_events")]
[Index(nameof(BusinessUnitId), nameof(SignalId), nameof(Version), IsUnique = true,
    Name = "UX_learning_governance_events_BU_Signal_Version")]
[Index(nameof(BusinessUnitId), nameof(IdempotencyKey), IsUnique = true,
    Name = "UX_learning_governance_events_BU_Idempotency")]
public sealed class LearningGovernanceEvent
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    [MaxLength(64)] public string SignalId { get; set; } = null!;
    public long Version { get; set; }
    [MaxLength(24)] public string Action { get; set; } = null!;
    [MaxLength(1000)] public string Reason { get; set; } = null!;
    public long ActorUserId { get; set; }
    [MaxLength(160)] public string IdempotencyKey { get; set; } = null!;
    [MaxLength(500)] public string EvidenceReference { get; set; } = null!;
    public string SnapshotJson { get; set; } = null!;
    public DateTime OccurredOn { get; set; }
    public long? RevertsVersion { get; set; }
}

public sealed class LearningGovernanceConflictException(string message) : InvalidOperationException(message);
public sealed class LearningGovernanceValidationException(string message) : ArgumentException(message);

public static class LearningGovernanceRules
{
    public static string SignalId(string signalType, string subject)
    {
        var canonical = $"{Normalize(signalType)}\n{Normalize(subject)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    public static string SupplierQuoteCorrectionSignalId(string fieldName, string? originalValue)
    {
        var field = Normalize(fieldName);
        var original = Normalize(originalValue ?? string.Empty);
        return SignalId("SUPPLIER_QUOTE_CORRECTION",
            $"{field.Length}:{field}|{original.Length}:{original}");
    }

    public static string EffectiveStatus(IReadOnlyList<LearningGovernanceEvent> history,
        string derivedStatus)
    {
        if (history.Count == 0) return derivedStatus;
        return Resolve(history, history[^1], derivedStatus, new HashSet<long>());
    }

    private static string Resolve(IReadOnlyList<LearningGovernanceEvent> history,
        LearningGovernanceEvent current, string derivedStatus, HashSet<long> visited)
    {
        if (!visited.Add(current.Version)) return derivedStatus;
        if (current.Action == LearningGovernanceActions.Approved) return "APPROVED";
        if (current.Action == LearningGovernanceActions.Disabled) return "DISABLED";
        if (current.Action != LearningGovernanceActions.RolledBack || !current.RevertsVersion.HasValue)
            return derivedStatus;

        var prior = history.LastOrDefault(x => x.Version < current.RevertsVersion.Value);
        return prior is null ? derivedStatus : Resolve(history, prior, derivedStatus, visited);
    }

    internal static string Normalize(string value) => string.Join(' ', value.Trim().ToUpperInvariant()
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

public sealed class LearningGovernanceService(ErpRfqAutomationContext context)
{
    public async Task<LearningGovernanceResult> GovernAsync(long businessUnitId, string signalId,
        string action, LearningGovernanceCommand command, long actorUserId, string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        EnsureTenant(businessUnitId);
        signalId = ValidateSignalId(signalId);
        action = action.Trim().ToUpperInvariant();
        if (!LearningGovernanceActions.All.Contains(action))
            throw new LearningGovernanceValidationException("The governance action is invalid.");
        var reason = Required(command.Reason, 1000, "A governance reason is required.");
        idempotencyKey = Required(idempotencyKey, 160, "Idempotency-Key is required.");
        if (actorUserId <= 0)
            throw new UnauthorizedAccessException("A valid authenticated actor identifier is required.");

        var replay = await context.LearningGovernanceEvents.AsNoTracking()
            .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId &&
                x.IdempotencyKey == idempotencyKey, cancellationToken);
        if (replay is not null)
            return await ReplayAsync(businessUnitId, replay, signalId, action, command, reason,
                cancellationToken);

        var signal = (await BuildDerivedSignalsAsync(businessUnitId, cancellationToken))
            .SingleOrDefault(x => x.SignalId == signalId)
            ?? throw new KeyNotFoundException("The learning signal was not found in this tenant.");
        var history = await context.LearningGovernanceEvents.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.SignalId == signalId)
            .OrderBy(x => x.Version).ToListAsync(cancellationToken);
        var currentVersion = history.LastOrDefault()?.Version ?? 0;
        if (command.ExpectedVersion != currentVersion)
            throw new LearningGovernanceConflictException(
                $"Learning signal version is {currentVersion}; refresh and retry.");

        long? revertsVersion = null;
        if (action == LearningGovernanceActions.RolledBack)
        {
            revertsVersion = command.RevertsVersion
                ?? throw new LearningGovernanceValidationException("RevertsVersion is required for rollback.");
            if (revertsVersion != currentVersion || currentVersion == 0)
                throw new LearningGovernanceConflictException(
                    "Rollback must compensate the current governed version.");
        }
        else if (command.RevertsVersion.HasValue)
        {
            throw new LearningGovernanceValidationException(
                "RevertsVersion is accepted only for rollback.");
        }

        var occurredOn = DateTime.UtcNow;
        var appended = new LearningGovernanceEvent
        {
            BusinessUnitId = businessUnitId,
            SignalId = signalId,
            Version = checked(currentVersion + 1),
            Action = action,
            Reason = reason,
            ActorUserId = actorUserId,
            IdempotencyKey = idempotencyKey,
            EvidenceReference = signal.EvidenceReference,
            SnapshotJson = JsonSerializer.Serialize(new LearningSignalSnapshot(signal.SignalType,
                signal.Subject, signal.Value, signal.SampleSize, signal.LastObservedOn,
                signal.EvidenceReference, signal.Status)),
            OccurredOn = occurredOn,
            RevertsVersion = revertsVersion
        };
        context.LearningGovernanceEvents.Add(appended);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            context.Entry(appended).State = EntityState.Detached;
            var concurrentReplay = await context.LearningGovernanceEvents.AsNoTracking()
                .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId &&
                    x.IdempotencyKey == idempotencyKey, cancellationToken);
            if (concurrentReplay is not null)
                return await ReplayAsync(businessUnitId, concurrentReplay, signalId, action, command,
                    reason, cancellationToken);
            throw new LearningGovernanceConflictException(
                "The learning signal changed during this request. Refresh and retry.");
        }

        history.Add(appended);
        return Result(appended, LearningGovernanceRules.EffectiveStatus(history, signal.Status), false);
    }

    public async Task<IReadOnlyCollection<LearningSignal>> BuildSignalsAsync(long businessUnitId,
        CancellationToken cancellationToken = default)
    {
        EnsureTenant(businessUnitId);
        var derived = await BuildDerivedSignalsAsync(businessUnitId, cancellationToken);
        var events = await context.LearningGovernanceEvents.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId)
            .OrderBy(x => x.SignalId).ThenBy(x => x.Version).ToListAsync(cancellationToken);
        var bySignal = events.GroupBy(x => x.SignalId).ToDictionary(x => x.Key, x => x.ToList());
        var signals = derived.Select(signal =>
        {
            if (!bySignal.TryGetValue(signal.SignalId, out var history) || history.Count == 0) return signal;
            var latest = history[^1];
            return signal with
            {
                Status = LearningGovernanceRules.EffectiveStatus(history, signal.Status),
                GovernanceVersion = latest.Version,
                GovernanceAction = latest.Action,
                GovernedOn = latest.OccurredOn,
                GovernedByUserId = latest.ActorUserId
            };
        }).ToList();

        foreach (var (signalId, history) in bySignal)
        {
            if (signals.Any(x => x.SignalId == signalId))
                continue;
            var latest = history[^1];
            var snapshot = JsonSerializer.Deserialize<LearningSignalSnapshot>(latest.SnapshotJson);
            if (snapshot is null)
                continue;
            signals.Add(new LearningSignal(signalId, snapshot.SignalType, snapshot.Subject,
                snapshot.Value, snapshot.SampleSize, snapshot.LastObservedOn,
                LearningGovernanceRules.EffectiveStatus(history, snapshot.DerivedStatus),
                snapshot.EvidenceReference, latest.Version, latest.Action,
                latest.OccurredOn, latest.ActorUserId));
        }
        return signals;
    }

    private async Task<IReadOnlyCollection<LearningSignal>> BuildDerivedSignalsAsync(long businessUnitId,
        CancellationToken cancellationToken)
    {
        var decisions = await context.SupplierQuoteReviewDecisions.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.Status == SupplierQuoteReviewStatuses.Corrected)
            .Join(context.SupplierQuoteFieldEvidence.AsNoTracking(), d => d.SupplierQuoteFieldEvidenceId,
                e => e.Id, (d, e) => new { d, e })
            .OrderByDescending(x => x.d.ReviewedOn).Take(200).ToListAsync(cancellationToken);
        return decisions.GroupBy(x => new
        {
            FieldName = LearningGovernanceRules.Normalize(x.e.FieldName),
            OriginalValue = LearningGovernanceRules.Normalize(x.e.OriginalValue ?? string.Empty)
        }).Take(50).Select(group =>
        {
            var latest = group.OrderByDescending(x => x.d.ReviewedOn).First();
            var conflict = group.Select(x => x.d.CorrectedValue).Distinct().Count() > 1;
            var status = conflict ? "CONFLICT_REVIEW" : "OBSERVED";
            return new LearningSignal(LearningGovernanceRules.SupplierQuoteCorrectionSignalId(
                    group.Key.FieldName, group.Key.OriginalValue),
                "SUPPLIER_QUOTE_CORRECTION", latest.e.FieldName, latest.d.CorrectedValue ?? "", group.Count(),
                latest.d.ReviewedOn, status, $"SupplierQuoteEvidence:{latest.e.Id}", 0, null, null, null);
        }).ToArray();
    }

    private async Task<LearningGovernanceResult> ReplayAsync(long businessUnitId,
        LearningGovernanceEvent replay, string signalId, string action, LearningGovernanceCommand command,
        string reason, CancellationToken cancellationToken)
    {
        if (replay.SignalId != signalId || replay.Action != action || replay.Reason != reason ||
            replay.Version != command.ExpectedVersion + 1 || replay.RevertsVersion != command.RevertsVersion)
            throw new LearningGovernanceConflictException(
                "Idempotency-Key was already used for a different governance request.");
        var history = await context.LearningGovernanceEvents.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.SignalId == signalId &&
                x.Version <= replay.Version).OrderBy(x => x.Version).ToListAsync(cancellationToken);
        var snapshot = JsonSerializer.Deserialize<LearningSignalSnapshot>(replay.SnapshotJson);
        return Result(replay, LearningGovernanceRules.EffectiveStatus(history,
            snapshot?.DerivedStatus ?? "OBSERVED"), true);
    }

    private static LearningGovernanceResult Result(LearningGovernanceEvent value,
        string effectiveStatus, bool replay) => new(value.SignalId, value.Version, value.Action,
        effectiveStatus, value.RevertsVersion, value.OccurredOn, replay);

    private void EnsureTenant(long businessUnitId)
    {
        if (businessUnitId <= 0 || context.ScopedTenantId != businessUnitId)
            throw new UnauthorizedAccessException("The authenticated tenant context is required.");
    }

    private static string ValidateSignalId(string value)
    {
        value = value.Trim().ToLowerInvariant();
        if (value.Length != 64 || value.Any(x => !char.IsAsciiHexDigit(x)))
            throw new LearningGovernanceValidationException("SignalId must be a 64-character hexadecimal identifier.");
        return value;
    }

    private static string Required(string? value, int maximumLength, string message)
    {
        value = value?.Trim();
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value.Any(char.IsControl))
            throw new LearningGovernanceValidationException(message);
        return value;
    }

    private sealed record LearningSignalSnapshot(string SignalType, string Subject, string Value,
        int SampleSize, DateTime LastObservedOn, string EvidenceReference, string DerivedStatus);
}
