using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.PlatformGovernance;

public sealed record AiTrustPolicyState(
    bool IsEnabled, bool ExternalProcessingAllowed, string AllowedPurposes,
    string? AllowedProvider, string? AllowedModel, long? MonthlySoftTokenLimit,
    long? MonthlyHardTokenLimit, long? MaxTokensPerDocument,
    decimal? ExternalInputCostPerMillionTokens, decimal? ExternalOutputCostPerMillionTokens,
    string? ExternalCostCurrency, string? ExternalPricingVersion,
    decimal ExternalDependencyCeilingPercent, bool RedactionRequired,
    string AllowedDataClassifications, string EgressPolicy, string DataResidency,
    int RetentionDays, bool InputOutputAuditAllowed, bool PrivacyReviewRequired,
    decimal? LocalComputeCostPerHour, decimal? OcrCostPerPage, string? LocalCostCurrency,
    long Version, DateTime UpdatedOn, string UpdatedBy);

public sealed record AiTrustUsageSummary(
    int Requests, int LocalRequests, int ExternalRequests, int AuthorizedExternalRequests,
    decimal ExternalDependencyPercent,
    bool DependencyCeilingBreached, int DeniedRequests, int FailedRequests,
    int InjectionDetections, long InputTokens, long OutputTokens, long ReservedTokens,
    long SettledTokens, long? SoftTokenLimit, long? HardTokenLimit,
    IReadOnlyDictionary<string, decimal> EstimatedExternalCost);

public sealed record AiTrustRequestItem(
    Guid Id, string Operation, string Provider, AiProviderClass ProviderClass, string Model,
    string Status, string PromptVersion, long InputTokens, long OutputTokens,
    decimal? EstimatedCost, string? CostCurrency, string CostStatus, bool InjectionDetected,
    string? ErrorCode, DateTime CreatedOn, DateTime? CompletedOn);

public sealed record AiTrustAuditItem(
    long Id, string Action, string Reason, long ActorUserId, DateTime OccurredOn);

public sealed record AiTrustCenterView(
    AiTrustPolicyState Policy, AiTrustUsageSummary Usage,
    IReadOnlyList<AiTrustRequestItem> Requests, IReadOnlyList<AiTrustAuditItem> Audit,
    string InferencePosture, AiExternalDependencySnapshot Dependency);

public sealed class AiTrustCenterService(
    ErpRfqAutomationContext db, IAiProviderEndpointResolver endpointResolver)
{
    /// <summary>How many recent calls the ledger tab renders. A display cap, never a divisor.</summary>
    private const int LedgerPageSize = 100;

    /// <summary>The month's calls, projected to what the summary actually counts.</summary>
    private sealed record MonthlyCall(AiProviderClass ProviderClass, long? ExternalAuthorizationId,
        string Status, bool InjectionDetected, long InputTokens, long OutputTokens,
        decimal? EstimatedCost, string? CostCurrency);

    public async Task<AiTrustCenterView> GetAsync(long tenantId, CancellationToken ct)
    {
        PlatformGovernanceService.EnsureTenant(tenantId);
        var policy = await PolicyAsync(tenantId, ct);
        var period = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        // Two different questions, so two different reads. The ledger tab shows the most recent
        // calls and is paged; the counters above it are supposed to describe the whole month.
        // One capped query answered both, so a tenant past 100 calls in a month saw "Monthly
        // requests 100" forever and every total under it was wrong by the overflow.
        var ledger = await db.AiRequests.AsNoTracking()
            .Where(x => x.BusinessUnitId == tenantId && x.CreatedOn >= period)
            .OrderByDescending(x => x.CreatedOn).Take(LedgerPageSize).ToListAsync(ct);
        var requests = await db.AiRequests.AsNoTracking()
            .Where(x => x.BusinessUnitId == tenantId && x.CreatedOn >= period)
            .Select(x => new MonthlyCall(x.ProviderClass, x.ExternalAuthorizationId, x.Status,
                x.InjectionDetected, x.InputTokens, x.OutputTokens, x.EstimatedCost, x.CostCurrency))
            .ToListAsync(ct);
        var budget = await db.AiBudgetPeriods.AsNoTracking().SingleOrDefaultAsync(
            x => x.BusinessUnitId == tenantId && x.PeriodStartUtc == period, ct);
        // A denied reservation never reached a provider, so it is not egress and does not
        // belong in a "local / external" reading of what this tenant sent out. Counted, the
        // tile said "0 / 20 (0 authorized)" for twenty REFUSALS — the control working, rendered
        // as twenty unapproved calls. Denials keep their own counter below.
        var governed = requests.Where(x => x.Status != AiCallStatuses.Denied).ToList();
        var external = governed.Count(x => x.ProviderClass == AiProviderClass.External);
        var authorizedExternal = governed.Count(x => x.ProviderClass == AiProviderClass.External
            && x.ExternalAuthorizationId != null);
        // The ceiling governs UNAUTHORIZED external usage, and nothing else. Enforcement has
        // always known that — AiGovernanceService denies on the ratio only when
        // `liveAuthorizationId is null` — and AiExternalDependencyEvaluator is the one
        // projection of that rule. This screen used to compute its own raw external/total
        // share instead, so any deployment whose inference endpoint is not loopback reported
        // a permanent ceiling breach: every call external, every call authorized, not one of
        // them denied. The banner even said authorized calls were exempt while the number
        // beside it applied no such exemption. One definition, shared with the enforcer.
        //
        // Bounded to the SAME period the counters report, which is what makes the two
        // reconcilable — and, because the authorization receipt has been written on every
        // authorized external reservation since 2026-08-10, also excludes the legacy rows
        // that would otherwise be counted as unauthorized forever.
        var dependencySnapshot = await AiExternalDependencyEvaluator.EvaluateAsync(
            db.AiRequests, tenantId, policy.ExternalDependencyCeilingPercent, ct, notBefore: period);
        var costs = requests.Where(x => x.ProviderClass == AiProviderClass.External
                && x.EstimatedCost.HasValue && !string.IsNullOrWhiteSpace(x.CostCurrency))
            .GroupBy(x => x.CostCurrency!.ToUpperInvariant())
            .ToDictionary(x => x.Key, x => x.Sum(y => y.EstimatedCost!.Value));
        var audit = await db.TenantGovernanceAuditEvents.AsNoTracking()
            .Where(x => x.BusinessUnitId == tenantId && x.Area == "AITrust")
            .OrderByDescending(x => x.OccurredOn).Take(50)
            .Select(x => new AiTrustAuditItem(x.Id, x.Action, x.Reason, x.ActorUserId, x.OccurredOn))
            .ToListAsync(ct);
        return new(Map(policy), new(governed.Count,
                governed.Count(x => x.ProviderClass == AiProviderClass.Local), external,
                authorizedExternal, dependencySnapshot.ExternalSharePercent,
                dependencySnapshot.CeilingBreached,
                requests.Count(x => x.Status == AiCallStatuses.Denied),
                requests.Count(x => x.Status == AiCallStatuses.Failed),
                requests.Count(x => x.InjectionDetected), requests.Sum(x => x.InputTokens),
                requests.Sum(x => x.OutputTokens), budget?.ReservedTokens ?? 0,
                budget?.SettledTokens ?? 0, budget?.SoftTokenLimit ?? policy.MonthlySoftTokenLimit,
                budget?.HardTokenLimit ?? policy.MonthlyHardTokenLimit, costs),
            ledger.Select(x => new AiTrustRequestItem(x.Id, x.Operation, x.Provider,
                x.ProviderClass, x.Model, x.Status, x.PromptVersion, x.InputTokens, x.OutputTokens,
                x.EstimatedCost, x.CostCurrency, x.CostStatus, x.InjectionDetected, x.ErrorCode,
                x.CreatedOn, x.CompletedOn)).ToList(), audit,
            // Read-only, resolved once at startup: the deployment's declared inference
            // stance (LocalFirst / ExternalAuthorized). Informational — enforcement lives
            // in the allow-list gate and the ceiling logic, never here.
            endpointResolver.Posture.ToString(),
            // The control's own sample, published whole: the window it was measured over, how
            // many of its external calls carried an authorization, and the share that remained
            // unauthorized. Without it the screen shows a percentage nobody can reconcile
            // against the month-to-date counters beside it, which are a different sample.
            dependencySnapshot);
    }

    private async Task<AiProcessingPolicy> PolicyAsync(long tenantId, CancellationToken ct) =>
        await db.AiProcessingPolicies.SingleOrDefaultAsync(x => x.BusinessUnitId == tenantId, ct)
        ?? throw new PlatformGovernanceNotFoundException("The tenant AI policy has not been provisioned.");

    private static AiTrustPolicyState Map(AiProcessingPolicy x) => new(x.IsEnabled,
        x.ExternalProcessingAllowed, x.AllowedPurposes, x.AllowedProvider, x.AllowedModel,
        x.MonthlySoftTokenLimit, x.MonthlyHardTokenLimit, x.MaxTokensPerDocument,
        x.ExternalInputCostPerMillionTokens, x.ExternalOutputCostPerMillionTokens,
        x.ExternalCostCurrency, x.ExternalPricingVersion, x.ExternalDependencyCeilingPercent,
        x.RedactionRequired, x.AllowedDataClassifications, x.EgressPolicy, x.DataResidency,
        x.RetentionDays, x.InputOutputAuditAllowed, x.PrivacyReviewRequired,
        x.LocalComputeCostPerHour, x.OcrCostPerPage, x.LocalCostCurrency, x.Version,
        x.UpdatedOn, x.UpdatedBy);
}
