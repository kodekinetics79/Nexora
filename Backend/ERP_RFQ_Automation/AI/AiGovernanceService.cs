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
    private readonly ITenantContext _tenantContext;
    private readonly IAiExternalProviderTrust _externalProviderTrust;

    /// <param name="externalProviderTrust">
    /// The per-tenant external-provider allow-list. REQUIRED, deliberately: here the gate
    /// can only ever GRANT an exemption from the external-dependency ceiling, so an
    /// optional-and-null dependency would be indistinguishable from "never exempt" — but a
    /// required dependency keeps the invariant explicit and un-forgettable: no gate, no
    /// exemption, and a misregistration fails at composition time instead of silently
    /// changing ceiling semantics. Absence of a matching live authorization always reads
    /// as "not authorized", never as "exempt".
    /// </param>
    public AiGovernanceService(
        IServiceScopeFactory scopeFactory,
        ITenantScopeAccessor tenantScope,
        ITenantContext tenantContext,
        IAiExternalProviderTrust externalProviderTrust)
    {
        _scopeFactory = scopeFactory;
        _tenantScope = tenantScope;
        _tenantContext = tenantContext;
        _externalProviderTrust = externalProviderTrust
            ?? throw new ArgumentNullException(nameof(externalProviderTrust));
    }

    public async Task<AiReservation> ReserveAsync(
        AiCallContext context, string provider, string model, string input,
        int maximumInputBytes, int maximumOutputTokens, int maximumAttempts, CancellationToken ct)
    {
        if (context.BusinessUnitId <= 0 || string.IsNullOrWhiteSpace(context.IdempotencyKey))
            throw new AiPolicyDeniedException("invalid_context");
        EnsureTenant(context.BusinessUnitId);

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

        // Consult the allow-list gate ONCE, before the serializable reservation
        // transaction: the gate reads through its own scoped context, and its verdict is
        // point-in-time either way (the extraction gate re-evaluates it independently).
        // Null — no live authorization for this exact resolved destination, or a gate
        // refusal of any kind — always reads as "not authorized", never as "exempt".
        var liveCeilingAuthorizationId = context.ProviderClass == AiProviderClass.External
            ? await CeilingExemptionAsync(context, provider, model, ct)
            : null;

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
            long? ceilingExemptAuthorizationId = null;
            if (denial is null && context.ProviderClass == AiProviderClass.External)
            {
                var recentProviderClasses = await db.AiRequests.AsNoTracking()
                    .Where(x => x.BusinessUnitId == context.BusinessUnitId && x.Status != AiCallStatuses.Denied)
                    .OrderByDescending(x => x.CreatedOn).Take(100).Select(x => x.ProviderClass).ToListAsync(ct);
                // Honour the tenant's configured ceiling instead of a hardcoded 10%.
                // AiProcessingPolicy.ExternalDependencyCeilingPercent has always been
                // persisted, editable through the AI Trust Center and validated to 0..10,
                // but this comparison used a literal .10m — so a tenant who tightened the
                // ceiling to, say, 2% silently got 10%. A knob that does nothing is worse
                // than no knob. `policy` is non-null here: PolicyDenial returns
                // "policy_missing" for a null policy, which short-circuits this branch.
                var externalCeiling = policy!.ExternalDependencyCeilingPercent / 100m;
                if ((recentProviderClasses.Count(x => x == AiProviderClass.External) + 1m) / (recentProviderClasses.Count + 1m) > externalCeiling)
                {
                    // The ceiling governs UNAUTHORIZED external usage only. An endpoint the
                    // tenant explicitly authorized through the allow-list is exempt from
                    // this ratio — the allow-list is the precise, attributed control for
                    // authorized egress, and on a deployment with no local model the ratio
                    // is always 100%, which would otherwise deny work the tenant has
                    // deliberately approved. The exemption is CEILING-ONLY: every other
                    // control (monthly + per-document budgets, reserve/attempt/settle
                    // ledger, redaction, injection nonce, count conservation) still runs
                    // below, unchanged. Any non-allowed outcome — no matching live
                    // authorization, endpoint/provider/model mismatch, gate refusal of any
                    // kind — keeps the existing denial: not authorized never means exempt.
                    ceilingExemptAuthorizationId = liveCeilingAuthorizationId;
                    if (ceilingExemptAuthorizationId is null)
                        denial = "external_dependency_cap";
                }
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

            if (policy.MaxTokensPerDocument is { } documentLimit
                && (context.SourceDocumentOccurrenceId.HasValue || context.ExtractionJobId.HasValue))
            {
                var documentUsage = await db.AiRequests.AsNoTracking()
                    .Where(x => x.BusinessUnitId == context.BusinessUnitId
                        && x.Status != AiCallStatuses.Denied
                        && (context.SourceDocumentOccurrenceId.HasValue
                            ? x.SourceDocumentOccurrenceId == context.SourceDocumentOccurrenceId
                            : x.ExtractionJobId == context.ExtractionJobId))
                    .SumAsync(x => x.CompletedOn == null
                        ? x.ReservedTokens
                        : x.InputTokens + x.OutputTokens, ct);
                if (checked(documentUsage + reserve) > documentLimit)
                {
                    db.AiRequests.Add(NewRequest(context, provider, model, input, inputHash, estimatedInput, 0, now,
                        AiCallStatuses.Denied, "document_budget_exceeded"));
                    await db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);
                    throw new AiPolicyDeniedException("document_budget_exceeded");
                }
            }

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
            if (ceilingExemptAuthorizationId is not null)
            {
                // Audit linkage: the ledger row records WHICH authorization exempted this
                // reservation from the ceiling, and the deployment posture at that moment,
                // so "which calls went external under whose authorization" is answerable
                // from the ledger alone.
                request.ExternalAuthorizationId = ceilingExemptAuthorizationId;
                request.InferencePosture = InferencePostures
                    .For(_externalProviderTrust.ResolvedProvider.ProviderClass).ToString();
            }
            request.BudgetWarning = budget.SoftTokenLimit is { } soft
                && budget.ReservedTokens + budget.SettledTokens > soft;
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

            if (request.ProviderClass == AiProviderClass.External)
            {
                var policy = await db.AiProcessingPolicies.SingleAsync(
                    x => x.BusinessUnitId == reservation.BusinessUnitId, ct);
                if (policy.ExternalInputCostPerMillionTokens.HasValue
                    && policy.ExternalOutputCostPerMillionTokens.HasValue
                    && !string.IsNullOrWhiteSpace(policy.ExternalCostCurrency)
                    && !string.IsNullOrWhiteSpace(policy.ExternalPricingVersion))
                {
                    request.EstimatedCost = decimal.Round(
                        (Math.Max(0, inputTokens) * policy.ExternalInputCostPerMillionTokens.Value
                         + Math.Max(0, outputTokens) * policy.ExternalOutputCostPerMillionTokens.Value) / 1_000_000m,
                        6, MidpointRounding.AwayFromZero);
                    request.CostCurrency = policy.ExternalCostCurrency.Trim().ToUpperInvariant();
                    request.CostStatus = AiCostStatuses.EstimatedConfiguredRate;
                    request.CostPricingVersion = policy.ExternalPricingVersion;
                }
                else
                {
                    request.EstimatedCost = null;
                    request.CostCurrency = null;
                    request.CostStatus = AiCostStatuses.RateUnavailable;
                    request.CostPricingVersion = null;
                }
            }

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
            CostStatus = context.ProviderClass == AiProviderClass.Local
                ? AiCostStatuses.LocalUnpriced
                : AiCostStatuses.RateUnavailable,
            ErrorCode = errorCode,
            CreatedOn = now,
            CompletedOn = status == AiCallStatuses.Denied ? now : null
        };

    private void EnsureTenant(long businessUnitId)
    {
        if (_tenantContext.BusinessUnitId != businessUnitId)
            throw new AiPolicyDeniedException("tenant_context_mismatch");
    }

    /// <summary>
    /// Returns the id of the live allow-list authorization covering this reservation's
    /// destination, or null when the reservation must remain subject to the ceiling.
    ///
    /// <para>
    /// The only endpoint identity this service can trust is the one resolved at startup
    /// (<see cref="IAiExternalProviderTrust.ResolvedProvider"/>), so the exemption is
    /// consulted ONLY when the reservation's provider and model are exactly that resolved
    /// destination. Any other external reservation (for example the Anthropic agent
    /// client, whose endpoint is not the resolved one) cannot be matched to an
    /// authorization here and therefore stays under the ceiling — mismatch fails closed.
    /// </para>
    ///
    /// <para>
    /// <c>unstructuredPayload</c> is false because the ledger cannot see payload shape;
    /// the unstructured-egress switch is enforced where the payload is known, at the
    /// extraction gate (ChunkedExtractionService), BEFORE any reservation is attempted.
    /// Purpose, endpoint, model, expiry, revocation and tenant scope are all still
    /// enforced by <see cref="IAiExternalProviderTrust.EvaluateAsync"/> here.
    /// </para>
    /// </summary>
    private async Task<long?> CeilingExemptionAsync(
        AiCallContext context, string provider, string model, CancellationToken ct)
    {
        var resolved = _externalProviderTrust.ResolvedProvider;
        if (!resolved.IsResolved
            || !AiProviderEndpoint.ProviderMatches(resolved.Provider, provider)
            || !string.Equals(resolved.Model, model, StringComparison.Ordinal))
            return null;

        var decision = await _externalProviderTrust.EvaluateAsync(
            context.BusinessUnitId, resolved, context.Purpose, unstructuredPayload: false, ct);
        return decision.Allowed ? decision.AuthorizationId : null;
    }

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
