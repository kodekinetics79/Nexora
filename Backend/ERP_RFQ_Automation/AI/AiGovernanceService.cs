using System.Data;
using System.Security.Cryptography;
using System.Text;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.AI;

public static class AiPurposes
{
    public const string RfqExtraction = "RfqExtraction";
    public const string BoqDraft = "BoqDraft";
    public const string Agent = "Agent";
}

public sealed record AiCallContext(
    long BusinessUnitId,
    string Purpose,
    string IdempotencyKey,
    string PromptVersion,
    bool InjectionDetected = false,
    AiProviderClass ProviderClass = AiProviderClass.External,
    long? ExtractionJobId = null,
    long? SourceDocumentOccurrenceId = null);

public sealed record AiReservation(
    Guid RequestId,
    long BusinessUnitId,
    long ReservedTokens,
    long EstimatedInputTokens,
    int NextAttemptNumber);

public sealed record AiAttemptCompletion(
    int AttemptNumber,
    string Status,
    int? HttpStatus,
    string? ProviderRequestId,
    long InputTokens,
    long OutputTokens,
    string TokenSource,
    long LatencyMilliseconds,
    long? ProviderDurationNanoseconds,
    string? ResponseHash,
    string? ErrorCode,
    DateTime StartedOn,
    DateTime CompletedOn);

public interface IAiGovernanceService
{
    Task<AiReservation> ReserveAsync(
        AiCallContext context, string provider, string model, string input,
        int maximumInputBytes, int maximumOutputTokens, int maximumAttempts, CancellationToken ct);
    Task RecordAttemptAsync(AiReservation reservation, AiAttemptCompletion attempt, CancellationToken ct);
    Task CompleteAsync(
        AiReservation reservation, string status, long inputTokens, long outputTokens,
        string tokenSource, string? output, string? errorCode, CancellationToken ct);
}

public sealed class AiPolicyDeniedException : InvalidOperationException
{
    public string Code { get; }

    public AiPolicyDeniedException(string code) : base("AI processing is not permitted for this request.")
        => Code = code;
}

public sealed class AiGovernanceService : IAiGovernanceService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ITenantScopeAccessor _tenantScope;

    public AiGovernanceService(IServiceScopeFactory scopeFactory, ITenantScopeAccessor tenantScope)
    {
        _scopeFactory = scopeFactory;
        _tenantScope = tenantScope;
    }

    public async Task<AiReservation> ReserveAsync(
        AiCallContext context, string provider, string model, string input,
        int maximumInputBytes, int maximumOutputTokens, int maximumAttempts, CancellationToken ct)
    {
        if (context.BusinessUnitId <= 0 || string.IsNullOrWhiteSpace(context.IdempotencyKey))
            throw new AiPolicyDeniedException("invalid_context");

        var now = DateTime.UtcNow;
        var inputHash = Hash(input);
        var estimatedInput = EstimateTokens(input.Length);
        var inputBytes = Encoding.UTF8.GetByteCount(input);
        if (maximumInputBytes <= 0 || inputBytes > maximumInputBytes)
            throw new AiPolicyDeniedException("input_too_large");
        var perAttempt = checked((long)maximumInputBytes + Math.Max(1, maximumOutputTokens));
        var reserve = checked(perAttempt * Math.Max(1, maximumAttempts));
        var period = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        using var tenant = _tenantScope.Push(context.BusinessUnitId);
        using var strategyScope = _scopeFactory.CreateScope();
        var strategyDb = strategyScope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
        var strategy = strategyDb.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            using var operationScope = _scopeFactory.CreateScope();
            var db = operationScope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);

            var existing = await db.AiRequests
                .FirstOrDefaultAsync(x => x.BusinessUnitId == context.BusinessUnitId
                                       && x.IdempotencyKey == context.IdempotencyKey, ct);
            if (existing is not null)
            {
                if (!FixedEquals(existing.PromptHash, inputHash)
                    || !string.Equals(existing.PromptVersion, context.PromptVersion, StringComparison.Ordinal))
                    throw new AiPolicyDeniedException("idempotency_collision");
                throw new AiPolicyDeniedException("duplicate_request");
            }

            var policy = await db.AiProcessingPolicies
                .SingleOrDefaultAsync(x => x.BusinessUnitId == context.BusinessUnitId, ct);
            var denial = PolicyDenial(policy, context.Purpose, provider, model, context.ProviderClass);
            if (denial is null && context.ProviderClass == AiProviderClass.External)
            {
                var recentProviderClasses = await db.AiRequests.AsNoTracking()
                    .Where(x => x.BusinessUnitId == context.BusinessUnitId && x.Status == AiCallStatuses.Succeeded)
                    .OrderByDescending(x => x.CreatedOn).Take(100).Select(x => x.ProviderClass).ToListAsync(ct);
                if ((recentProviderClasses.Count(x => x == AiProviderClass.External) + 1m) / (recentProviderClasses.Count + 1m) > .10m)
                    denial = "external_dependency_cap";
            }
            if (denial is not null)
            {
                db.AiRequests.Add(NewRequest(context, provider, model, input, inputHash, estimatedInput, 0, now,
                    AiCallStatuses.Denied, denial));
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                throw new AiPolicyDeniedException(denial);
            }

            var budget = await db.AiBudgetPeriods
                .SingleOrDefaultAsync(x => x.BusinessUnitId == context.BusinessUnitId && x.PeriodStartUtc == period, ct);
            if (budget is null && db.Database.IsNpgsql())
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO public."AiBudgetPeriods"
                        ("BusinessUnitId", "PeriodStartUtc", "SoftTokenLimit", "HardTokenLimit",
                         "ReservedTokens", "SettledTokens", "Version", "UpdatedOn")
                    VALUES ({context.BusinessUnitId}, {period}, {policy!.MonthlySoftTokenLimit},
                            {policy.MonthlyHardTokenLimit}, 0, 0, 1, {now})
                    ON CONFLICT ("BusinessUnitId", "PeriodStartUtc") DO NOTHING
                    """, ct);
                budget = await db.AiBudgetPeriods.SingleAsync(
                    x => x.BusinessUnitId == context.BusinessUnitId && x.PeriodStartUtc == period, ct);
            }
            else if (budget is null)
            {
                budget = new AiBudgetPeriod
                {
                    BusinessUnitId = context.BusinessUnitId,
                    PeriodStartUtc = period,
                    SoftTokenLimit = policy!.MonthlySoftTokenLimit,
                    HardTokenLimit = policy.MonthlyHardTokenLimit,
                    UpdatedOn = now
                };
                db.AiBudgetPeriods.Add(budget);
                await db.SaveChangesAsync(ct);
            }

            budget.SoftTokenLimit = policy!.MonthlySoftTokenLimit;
            budget.HardTokenLimit = policy.MonthlyHardTokenLimit;

            if (budget.HardTokenLimit is { } hard
                && checked(budget.ReservedTokens + budget.SettledTokens + reserve) > hard)
            {
                db.AiRequests.Add(NewRequest(context, provider, model, input, inputHash, estimatedInput, 0, now,
                    AiCallStatuses.Denied, "hard_budget_exceeded"));
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                throw new AiPolicyDeniedException("hard_budget_exceeded");
            }

            budget.ReservedTokens = checked(budget.ReservedTokens + reserve);
            budget.Version++;
            budget.UpdatedOn = now;
            var request = NewRequest(context, provider, model, input, inputHash, estimatedInput, reserve, now,
                AiCallStatuses.Reserved, null);
            db.AiRequests.Add(request);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new AiReservation(request.Id, context.BusinessUnitId, reserve, estimatedInput, 1);
        });
    }

    public async Task RecordAttemptAsync(AiReservation reservation, AiAttemptCompletion attempt, CancellationToken ct)
    {
        using var tenant = _tenantScope.Push(reservation.BusinessUnitId);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
        var request = await db.AiRequests.SingleAsync(x => x.Id == reservation.RequestId, ct);
        if (request.CompletedOn is not null)
            throw new InvalidOperationException("Cannot append an attempt to a completed AI request.");
        request.Status = AiCallStatuses.Running;
        request.StartedOn ??= attempt.StartedOn;
        db.AiCallAttempts.Add(new AiCallAttempt
        {
            RequestId = reservation.RequestId,
            BusinessUnitId = reservation.BusinessUnitId,
            AttemptNumber = attempt.AttemptNumber,
            Provider = request.Provider,
            Model = request.Model,
            Status = attempt.Status,
            HttpStatus = attempt.HttpStatus,
            ProviderRequestId = attempt.ProviderRequestId,
            InputTokens = attempt.InputTokens,
            OutputTokens = attempt.OutputTokens,
            TokenSource = attempt.TokenSource,
            LatencyMilliseconds = attempt.LatencyMilliseconds,
            ProviderDurationNanoseconds = attempt.ProviderDurationNanoseconds,
            ResponseHash = attempt.ResponseHash,
            ErrorCode = attempt.ErrorCode,
            StartedOn = attempt.StartedOn,
            CompletedOn = attempt.CompletedOn
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task CompleteAsync(
        AiReservation reservation, string status, long inputTokens, long outputTokens,
        string tokenSource, string? output, string? errorCode, CancellationToken ct)
    {
        using var tenant = _tenantScope.Push(reservation.BusinessUnitId);
        using var strategyScope = _scopeFactory.CreateScope();
        var strategyDb = strategyScope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
        var strategy = strategyDb.Database.CreateExecutionStrategy();
        var expectedOutputHash = string.IsNullOrEmpty(output) ? null : Hash(output);
        await strategy.ExecuteAsync(async () =>
        {
            using var operationScope = _scopeFactory.CreateScope();
            var db = operationScope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var request = await db.AiRequests.SingleAsync(x => x.Id == reservation.RequestId, ct);
            if (request.CompletedOn is not null)
            {
                if (request.Status == status && request.InputTokens == Math.Max(0, inputTokens)
                    && request.OutputTokens == Math.Max(0, outputTokens)
                    && request.TokenSource == tokenSource && request.OutputHash == expectedOutputHash
                    && request.ErrorCode == errorCode)
                    return;
                throw new InvalidOperationException("The AI request has already been settled with a different result.");
            }
            request.Status = status;
            request.InputTokens = Math.Max(0, inputTokens);
            request.OutputTokens = Math.Max(0, outputTokens);
            request.TokenSource = tokenSource;
            request.OutputCharacters = output?.Length ?? 0;
            request.OutputHash = expectedOutputHash;
            request.ErrorCode = errorCode;
            request.CompletedOn = DateTime.UtcNow;

            var period = new DateTime(request.CreatedOn.Year, request.CreatedOn.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var budget = await db.AiBudgetPeriods.SingleAsync(
                x => x.BusinessUnitId == reservation.BusinessUnitId && x.PeriodStartUtc == period, ct);
            budget.ReservedTokens = Math.Max(0, budget.ReservedTokens - reservation.ReservedTokens);
            budget.SettledTokens = checked(budget.SettledTokens + Math.Max(0, inputTokens) + Math.Max(0, outputTokens));
            budget.Version++;
            budget.UpdatedOn = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });
    }

    private static AiRequest NewRequest(
        AiCallContext context, string provider, string model, string input, string inputHash,
        long estimatedInput, long reserved, DateTime now, string status, string? errorCode) => new()
        {
            Id = Guid.NewGuid(),
            BusinessUnitId = context.BusinessUnitId,
            ExtractionJobId = context.ExtractionJobId,
            SourceDocumentOccurrenceId = context.SourceDocumentOccurrenceId,
            Operation = context.Purpose,
            IdempotencyKey = context.IdempotencyKey,
            PromptHash = inputHash,
            PromptVersion = context.PromptVersion,
            Provider = provider,
            ProviderClass = context.ProviderClass,
            Model = model,
            Status = status,
            InputCharacters = input.Length,
            InputHash = inputHash,
            InjectionDetected = context.InjectionDetected,
            EstimatedInputTokens = estimatedInput,
            ReservedTokens = reserved,
            CostStatus = context.ProviderClass == AiProviderClass.Local ? "LocalUnpriced" : "RateUnavailable",
            ErrorCode = errorCode,
            CreatedOn = now,
            CompletedOn = status == AiCallStatuses.Denied ? now : null
        };

    private static string? PolicyDenial(
        AiProcessingPolicy? policy,
        string purpose,
        string provider,
        string model,
        AiProviderClass providerClass)
    {
        if (policy is null) return "policy_missing";
        if (!policy.IsEnabled) return "policy_disabled";
        if (providerClass == AiProviderClass.External && !policy.ExternalProcessingAllowed)
            return "external_processing_denied";
        var purposes = policy.AllowedPurposes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!purposes.Contains(purpose, StringComparer.OrdinalIgnoreCase)) return "purpose_denied";
        if (!string.IsNullOrWhiteSpace(policy.AllowedProvider)
            && !string.Equals(policy.AllowedProvider, provider, StringComparison.OrdinalIgnoreCase)) return "provider_denied";
        if (!string.IsNullOrWhiteSpace(policy.AllowedModel)
            && !string.Equals(policy.AllowedModel, model, StringComparison.Ordinal)) return "model_denied";
        return null;
    }

    public static long EstimateTokens(int characters) => Math.Max(1, (characters + 3L) / 4L);
    public static long ConservativeTokenUpperBound(int utf8Bytes) => Math.Max(1, utf8Bytes);
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool FixedEquals(string left, string right)
        => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));
}
