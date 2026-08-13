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

/// <summary>
/// Governed prompt version labels. The value written to the ledger IS the switch the LLM
/// client uses to select an instruction set, so the prompt that is recorded is provably
/// the prompt that was sent — the same idiom as
/// <c>Extraction.Conversational.ConversationalPrompt.PromptVersion</c>, which owns the
/// conversational label next to the instructions it names.
/// Bump the version here whenever the shape of the request changes, so ledger rows can be
/// attributed to the prompt that produced them.
/// </summary>
public static class AiPromptVersions
{
    /// <summary>
    /// The structured document prompt (<c>OllamaLlmService.BuildExtractionInstructions</c>).
    ///
    /// v1 -> v2 (2026-08-05): the per-item schema no longer asks for a
    /// "&lt;Field&gt;Confidence" number beside each of the 24 line-item value fields. Those
    /// numbers were parsed and then discarded — a LeadItem row carries ONE confidence
    /// column (<c>Aiconfidence</c>), fed from <c>ItemConfidence</c> — while costing about
    /// half of every item's output budget and forcing chunk sizes down into the
    /// truncation-and-dead-letter regime that <c>Extraction.ExtractionOutputBudget</c>
    /// exists to prevent. Header-level confidences and OverallConfidence are unchanged;
    /// several ingestion gates read OverallConfidence.
    ///
    /// v2 -> v3 (2026-08-05): added the unit-of-measure transcription rule. The prompt had
    /// no unit vocabulary at all, and production shows the cost — one concept spelled five
    /// ways across 2,966 stored line items (each / EA / pcs / Pcs / piece / NOS). The rule
    /// tells the model to TRANSCRIBE the document's own wording and forbids it from
    /// standardising, defaulting a missing unit, or turning a package ("Pallet") into a
    /// piece count; canonicalisation is deterministic and happens in
    /// <c>Services/Uom/UomCanonicalizer.cs</c>, where it can be corrected and replayed.
    ///
    /// v3 -> v4 (2026-08-05): quantity format rules. The item schema typed Quantity as
    /// "number", which licensed <c>"Quantity": 2.0</c> — and because
    /// <c>LeadItemData.Quantity</c> is an <c>int?</c>, one decimal point on one line
    /// failed the WHOLE document's deserialization, a candidate cause of the
    /// "unparseable output" dead-letters. The schema now reads <c>integer | null</c> and
    /// rule 5 demands bare digits — no decimal point, no thousands separators — with null
    /// when a line states no quantity. The parse path was hardened in the same change
    /// (<c>OllamaLlmService.LenientQuantityConverter</c> plus per-line quarantine of
    /// non-positive quantities), so v4 is belt on top of braces, not the only defence.
    /// </summary>
    public const string StructuredRfqExtraction = "rfq-extraction-v4";
}

public sealed record AiCallContext(
    long BusinessUnitId,
    string Purpose,
    string IdempotencyKey,
    string PromptVersion,
    bool InjectionDetected = false,
    AiProviderClass ProviderClass = AiProviderClass.External,
    long? ExtractionJobId = null,
    long? SourceDocumentOccurrenceId = null,
    // How many line items the caller packed into this payload. Diagnostic only — it is
    // never sent to the provider. It exists so that when the provider reports output
    // truncation the log says WHICH chunk size overflowed, which is the single fact an
    // operator needs to tell "the model is broken" apart from "we asked for too much".
    int? ItemsInPayload = null);

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

    // The code is part of the message on purpose: catch-all surfaces persist ex.Message
    // (e.g. ExtractionWorker -> ExtractionJobs.LastError), and a refusal whose stored
    // trace does not say WHY it was refused is indistinguishable from a model failure.
    public AiPolicyDeniedException(string code)
        : base($"AI processing is not permitted for this request ({code}).")
        => Code = code;
}

public sealed class AiGovernanceService : IAiGovernanceService
{
    /// <summary>
    /// Retry headroom on the per-document token ceiling: the tenant-configured
    /// <see cref="AiProcessingPolicy.MaxTokensPerDocument"/> is the budget for ONE full
    /// extraction pass, and the ceiling enforced in <see cref="ReserveAsync"/> is that
    /// per-pass budget × this factor.
    ///
    /// SIZING. The per-document usage query keys on the document/job — NOT the lease
    /// attempt — so settled tokens from every earlier pipeline attempt keep counting for
    /// the document's whole life. Extraction idempotency keys are attempt-scoped
    /// (a retried job makes genuinely new governed calls), and the queue retries a
    /// document up to ExtractionJob.MaxAttempts times — default 5, the value this factor
    /// mirrors — on lease reclaim, worker restart, or manual re-drive. A ceiling sized
    /// for a single pass would therefore refuse exactly the FINAL attempts of the
    /// documents that most need retries, re-creating the dead-letter class the
    /// attempt-scoped keys just eliminated. Five full honest passes is the worst
    /// legitimate lifetime spend; the ceiling stays finite and per-document, so runaway
    /// callers and pathological re-splitting loops are still stopped. Do NOT remove the
    /// ceiling — it is the runaway-cost guard.
    /// </summary>
    internal const int DocumentBudgetRetryCycles = 5;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ITenantScopeAccessor _tenantScope;
    private readonly ITenantContext _tenantContext;
    private readonly IAiExternalProviderTrust _externalProviderTrust;
    private readonly ERP_RFQ_Automation.Platform.Hardening.NexoraMetrics? _metrics;

    /// <param name="externalProviderTrust">
    /// The per-tenant external-provider allow-list. REQUIRED, deliberately: here the gate
    /// can only ever GRANT an exemption from the external-dependency ceiling, so an
    /// optional-and-null dependency would be indistinguishable from "never exempt" — but a
    /// required dependency keeps the invariant explicit and un-forgettable: no gate, no
    /// exemption, and a misregistration fails at composition time instead of silently
    /// changing ceiling semantics. Absence of a matching live authorization always reads
    /// as "not authorized", never as "exempt".
    /// </param>
    /// <param name="metrics">
    /// Optional, unlike the trust gate: metrics are an observation of the ledger, never a
    /// control over it. A missing registration must not be able to change what is allowed.
    /// </param>
    public AiGovernanceService(
        IServiceScopeFactory scopeFactory,
        ITenantScopeAccessor tenantScope,
        ITenantContext tenantContext,
        IAiExternalProviderTrust externalProviderTrust,
        ERP_RFQ_Automation.Platform.Hardening.NexoraMetrics? metrics = null)
    {
        _scopeFactory = scopeFactory;
        _tenantScope = tenantScope;
        _tenantContext = tenantContext;
        _externalProviderTrust = externalProviderTrust
            ?? throw new ArgumentNullException(nameof(externalProviderTrust));
        _metrics = metrics;
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
        var period = AiPolicyDenials.BudgetPeriodStart(now);

        using var tenant = _tenantScope.Push(context.BusinessUnitId);

        // Consult the allow-list gate ONCE, before the serializable reservation
        // transaction: the gate reads through its own scoped context, and its verdict is
        // point-in-time either way (the extraction gate re-evaluates it independently).
        // Any non-allowed outcome — no matching live authorization, endpoint/provider/model
        // mismatch, expiry, revocation, gate refusal of any kind — is a REFUSAL of the
        // reservation, never merely the loss of an exemption.
        AiProviderDescriptor? externalDestination = null;
        AiExternalProviderDecision? externalAuthorization = null;
        if (context.ProviderClass == AiProviderClass.External)
            (externalDestination, externalAuthorization) =
                await AuthorizeExternalAsync(context, provider, model, ct);
        // The authorization that PERMITTED this call, whatever the ratio then says about it. It is
        // the ledger's answer to "which calls went external, under whose authorization" and it is
        // recorded on every authorized external reservation — see the linkage below.
        var liveAuthorizationId = externalAuthorization is { Allowed: true } allowed
            ? allowed.AuthorizationId
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
            // AiPolicyDenials is the ONE implementation of these tests. The read-only
            // extraction-readiness pre-flight reports them from the same code, so an
            // operator can never again be told a document is authorized by one layer and
            // refused by this one under a reason nothing surfaces.
            var denial = AiPolicyDenials.Evaluate(policy, context.Purpose, provider, model, context.ProviderClass);

            // ---- the allow-list is a GATE, not an exemption --------------------
            // This check used to live only inside the ceiling branch below, where the
            // authorization could do one thing and one thing only: waive a ratio. Absence of
            // an authorization was therefore not a refusal — it merely left the call subject
            // to a ratio test. The consequence was that a tenant with
            // ExternalProcessingAllowed = TRUE egressed structured line-item data (part
            // numbers, quantities, unit prices, customer names) to a third party with no
            // attributed authorization, no justification, no expiry and no revocation path,
            // for the nine calls in ten the ratio happened to permit — and the ratio is
            // drainable besides, because local calls buy external headroom.
            //
            // A ratio is a cost control. It was never an authorization, and it is not one
            // here any more. EVERY external reservation, from every path — structured-row
            // extraction, BOQ drafting, the agent — now requires a live allow-list
            // authorization naming this exact destination, or it is denied outright with the
            // gate's own reason code so the ledger records WHY.
            if (denial is null && context.ProviderClass == AiProviderClass.External
                && externalAuthorization is not { Allowed: true })
                denial = externalAuthorization?.Reason ?? AiExternalProviderTrustReasons.GateUnavailable;

            if (denial is null && context.ProviderClass == AiProviderClass.External)
            {
                var recentProviderClasses = await AiPolicyDenials.RecentProviderClassesAsync(
                    db.AiRequests, context.BusinessUnitId, ct);
                // Honour the tenant's configured ceiling instead of a hardcoded 10%.
                // AiProcessingPolicy.ExternalDependencyCeilingPercent has always been
                // persisted, editable through the AI Trust Center and validated to 0..10,
                // but this comparison used a literal .10m — so a tenant who tightened the
                // ceiling to, say, 2% silently got 10%. A knob that does nothing is worse
                // than no knob. `policy` is non-null here: AiPolicyDenials.Evaluate returns
                // "policy_missing" for a null policy, which short-circuits this branch.
                if (AiPolicyDenials.ExceedsExternalDependencyCeiling(
                        recentProviderClasses, policy!.ExternalDependencyCeilingPercent))
                {
                    // The ceiling is a COST control and nothing more. It governs unauthorized
                    // external usage only, and an endpoint the tenant explicitly authorized
                    // through the allow-list is exempt from the ratio — on a deployment with
                    // no local model the ratio is always 100%, which would otherwise deny
                    // work the tenant has deliberately approved. The exemption is
                    // CEILING-ONLY: every other control (monthly + per-document budgets,
                    // reserve/attempt/settle ledger, injection nonce, count conservation)
                    // still runs below, unchanged.
                    //
                    // Since the gate above now refuses every unauthorized external
                    // reservation outright, the denial on this line is defence in depth
                    // rather than a live path: it fires only if the gate is ever regressed or
                    // bypassed by a future call site. That is deliberate — it is the same
                    // reasoning that keeps an RLS policy under an app-layer check — but it
                    // does mean the ratio is no longer the thing standing between a tenant's
                    // line-item data and a third party. The allow-list is.
                    if (liveAuthorizationId is null)
                        denial = AiPolicyDenials.ExternalDependencyCap;
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
                if (checked(documentUsage + reserve) > checked(documentLimit * DocumentBudgetRetryCycles))
                {
                    db.AiRequests.Add(NewRequest(context, provider, model, input, inputHash, estimatedInput, 0, now,
                        AiCallStatuses.Denied, "document_budget_exceeded"));
                    await db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);
                    throw new AiPolicyDeniedException("document_budget_exceeded");
                }
            }

            // The last refusal in the chain, and the only one no allow-list grant exempts —
            // so it kills documents that every lock above has already cleared, which is the
            // same shape of failure the readiness pre-flight was built to end. It therefore
            // runs through AiPolicyDenials, the one implementation both surfaces ask.
            // budget.HardTokenLimit is the policy's value: it was copied on the line above.
            if (AiPolicyDenials.ExceedsHardTokenBudget(
                    budget.HardTokenLimit, budget.ReservedTokens, budget.SettledTokens, reserve))
            {
                db.AiRequests.Add(NewRequest(context, provider, model, input, inputHash, estimatedInput, 0, now,
                    AiCallStatuses.Denied, AiPolicyDenials.HardBudgetExceeded));
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                throw new AiPolicyDeniedException(AiPolicyDenials.HardBudgetExceeded);
            }

            budget.ReservedTokens = checked(budget.ReservedTokens + reserve);
            budget.Version++;
            budget.UpdatedOn = now;
            var request = NewRequest(context, provider, model, input, inputHash, estimatedInput, reserve, now,
                AiCallStatuses.Reserved, null);
            if (liveAuthorizationId is not null)
            {
                // Audit linkage: the ledger row records WHICH authorization permitted this
                // reservation, and the deployment posture at that moment, so "which calls
                // went external under whose authorization" is answerable from the ledger
                // alone.
                //
                // This used to be written only inside the ceiling-breach branch above, i.e.
                // only when the authorization ALSO waived the ratio. Under the default 10%
                // ceiling an external call that happened to land at or under the ratio was
                // recorded with a null ExternalAuthorizationId — an authorized egress with
                // no attributable authorization on the row, which is the one question the
                // column exists to answer. The ceiling is a cost control and its outcome is
                // not what makes a call attributable; the authorization is. The waiver
                // itself is still tracked separately by ceilingExemptAuthorizationId.
                request.ExternalAuthorizationId = liveAuthorizationId;
                // The posture of the destination THIS call went to, not of whichever endpoint
                // happens to be the extraction one — the agent's origin is a separate
                // descriptor and would otherwise be logged under the wrong stance.
                request.InferencePosture = InferencePostures
                    .For((externalDestination ?? _externalProviderTrust.ResolvedProvider).ProviderClass)
                    .ToString();
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
        // Captured, then emitted AFTER the strategy returns. Emitting inside the lambda
        // would double-count every transient retry, and would count a settlement whose
        // transaction later rolled back — the ledger and the meter must agree exactly.
        SettledCall? settled = null;
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

            var period = AiPolicyDenials.BudgetPeriodStart(request.CreatedOn);
            var budget = await db.AiBudgetPeriods.SingleAsync(
                x => x.BusinessUnitId == reservation.BusinessUnitId && x.PeriodStartUtc == period, ct);
            budget.ReservedTokens = Math.Max(0, budget.ReservedTokens - reservation.ReservedTokens);
            budget.SettledTokens = checked(budget.SettledTokens + Math.Max(0, inputTokens) + Math.Max(0, outputTokens));
            budget.Version++;
            budget.UpdatedOn = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            settled = new SettledCall(
                request.InputTokens, request.OutputTokens, request.Provider, request.Model,
                request.ProviderClass.ToString(), request.EstimatedCost, request.CostCurrency);
        });

        if (settled is { } call)
        {
            // Provider/model/class are configuration-scoped values with a handful of
            // possible values, so they are safe dimensions. Prompt hashes, idempotency
            // keys and request ids are NOT tagged — each is unique per call.
            _metrics?.LlmSettled(
                call.InputTokens, call.OutputTokens, reservation.BusinessUnitId,
                call.Provider, call.Model, call.ProviderClass, call.Cost, call.Currency);
        }
    }

    /// <summary>What a committed settlement reported, carried out of the retryable
    /// execution strategy so it can be metered exactly once.</summary>
    private readonly record struct SettledCall(
        long InputTokens, long OutputTokens, string? Provider, string? Model,
        string ProviderClass, decimal? Cost, string? Currency);

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
    /// The allow-list verdict for this reservation's destination. Never null-as-maybe: a
    /// decision is always returned, and anything other than <c>Allowed</c> denies the
    /// reservation in <see cref="ReserveAsync"/>.
    ///
    /// <para>
    /// A reservation can only be authorized against a destination this process actually
    /// has a descriptor for (<see cref="IAiExternalProviderTrust.KnownProviders"/>) —
    /// extraction and agent alike, since the agent's origin is now registered too. A
    /// provider/model pair that matches no descriptor names an endpoint the governance model
    /// cannot identify, so it cannot be authorized and is refused: mismatch fails closed.
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
    private async Task<(AiProviderDescriptor? Destination, AiExternalProviderDecision Decision)>
        AuthorizeExternalAsync(AiCallContext context, string provider, string model, CancellationToken ct)
    {
        var destination = _externalProviderTrust.KnownProviders.FirstOrDefault(
            x => x.IsResolved
                 && AiProviderEndpoint.ProviderMatches(x.Provider, provider)
                 && string.Equals(x.Model, model, StringComparison.Ordinal));
        if (destination is null)
            return (null, AiExternalProviderDecision.Deny(AiExternalProviderTrustReasons.EndpointUnresolved));

        return (destination, await _externalProviderTrust.EvaluateAsync(
            context.BusinessUnitId, destination, context.Purpose, unstructuredPayload: false, ct));
    }

    public static long EstimateTokens(int characters) => Math.Max(1, (characters + 3L) / 4L);
    public static long ConservativeTokenUpperBound(int utf8Bytes) => Math.Max(1, utf8Bytes);
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool FixedEquals(string left, string right)
        => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));
}
