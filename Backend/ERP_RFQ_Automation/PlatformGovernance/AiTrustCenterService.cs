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
    int Requests, int LocalRequests, int ExternalRequests, decimal ExternalDependencyPercent,
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
    string InferencePosture);

public sealed class AiTrustCenterService(
    ErpRfqAutomationContext db, IAiProviderEndpointResolver endpointResolver)
{
    public async Task<AiTrustCenterView> GetAsync(long tenantId, CancellationToken ct)
    {
        PlatformGovernanceService.EnsureTenant(tenantId);
        var policy = await PolicyAsync(tenantId, ct);
        var period = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var requests = await db.AiRequests.AsNoTracking()
            .Where(x => x.BusinessUnitId == tenantId && x.CreatedOn >= period)
            .OrderByDescending(x => x.CreatedOn).Take(100).ToListAsync(ct);
        var budget = await db.AiBudgetPeriods.AsNoTracking().SingleOrDefaultAsync(
            x => x.BusinessUnitId == tenantId && x.PeriodStartUtc == period, ct);
        var external = requests.Count(x => x.ProviderClass == AiProviderClass.External);
        var dependency = requests.Count == 0 ? 0m : decimal.Round(external * 100m / requests.Count, 2);
        var costs = requests.Where(x => x.ProviderClass == AiProviderClass.External
                && x.EstimatedCost.HasValue && !string.IsNullOrWhiteSpace(x.CostCurrency))
            .GroupBy(x => x.CostCurrency!.ToUpperInvariant())
            .ToDictionary(x => x.Key, x => x.Sum(y => y.EstimatedCost!.Value));
        var audit = await db.TenantGovernanceAuditEvents.AsNoTracking()
            .Where(x => x.BusinessUnitId == tenantId && x.Area == "AITrust")
            .OrderByDescending(x => x.OccurredOn).Take(50)
            .Select(x => new AiTrustAuditItem(x.Id, x.Action, x.Reason, x.ActorUserId, x.OccurredOn))
            .ToListAsync(ct);
        return new(Map(policy), new(requests.Count,
                requests.Count(x => x.ProviderClass == AiProviderClass.Local), external, dependency,
                dependency > policy.ExternalDependencyCeilingPercent,
                requests.Count(x => x.Status == AiCallStatuses.Denied),
                requests.Count(x => x.Status == AiCallStatuses.Failed),
                requests.Count(x => x.InjectionDetected), requests.Sum(x => x.InputTokens),
                requests.Sum(x => x.OutputTokens), budget?.ReservedTokens ?? 0,
                budget?.SettledTokens ?? 0, budget?.SoftTokenLimit ?? policy.MonthlySoftTokenLimit,
                budget?.HardTokenLimit ?? policy.MonthlyHardTokenLimit, costs),
            requests.Select(x => new AiTrustRequestItem(x.Id, x.Operation, x.Provider,
                x.ProviderClass, x.Model, x.Status, x.PromptVersion, x.InputTokens, x.OutputTokens,
                x.EstimatedCost, x.CostCurrency, x.CostStatus, x.InjectionDetected, x.ErrorCode,
                x.CreatedOn, x.CompletedOn)).ToList(), audit,
            // Read-only, resolved once at startup: the deployment's declared inference
            // stance (LocalFirst / ExternalAuthorized). Informational — enforcement lives
            // in the allow-list gate and the ceiling logic, never here.
            endpointResolver.Posture.ToString());
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
