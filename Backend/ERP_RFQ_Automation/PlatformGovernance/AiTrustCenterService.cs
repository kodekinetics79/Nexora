using System.Data;
using System.Text.Json;
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
    IReadOnlyList<AiTrustRequestItem> Requests, IReadOnlyList<AiTrustAuditItem> Audit);

public sealed record UpdateAiTrustPolicyCommand(
    long ExpectedVersion, bool IsEnabled, bool ExternalProcessingAllowed,
    string AllowedPurposes, string? AllowedProvider, string? AllowedModel,
    long? MonthlySoftTokenLimit, long? MonthlyHardTokenLimit, long? MaxTokensPerDocument,
    decimal? ExternalInputCostPerMillionTokens, decimal? ExternalOutputCostPerMillionTokens,
    string? ExternalCostCurrency, string? ExternalPricingVersion,
    decimal ExternalDependencyCeilingPercent, bool RedactionRequired,
    string AllowedDataClassifications, string EgressPolicy, string DataResidency,
    int RetentionDays, bool InputOutputAuditAllowed, bool PrivacyReviewRequired,
    decimal? LocalComputeCostPerHour, decimal? OcrCostPerPage, string? LocalCostCurrency,
    string Reason);

public sealed record RollbackAiTrustPolicyCommand(long ExpectedVersion, long AuditEventId, string Reason);
public sealed record AiTrustPolicyMutationResult(AiTrustPolicyState Policy, bool IdempotentReplay);

public sealed class AiTrustCenterService(ErpRfqAutomationContext db)
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
                x.CreatedOn, x.CompletedOn)).ToList(), audit);
    }

    public async Task<AiTrustPolicyMutationResult> UpdateAsync(long tenantId, long actorUserId,
        string idempotencyKey, UpdateAiTrustPolicyCommand command, CancellationToken ct)
    {
        PlatformGovernanceService.EnsureActor(tenantId, actorUserId);
        idempotencyKey = PlatformGovernanceService.Required(idempotencyKey, 160, "Idempotency-Key is required.");
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var replay = await ReplayAsync(tenantId, idempotencyKey, ct);
            if (replay is not null) return new(Map(await PolicyAsync(tenantId, ct)), true);
            var policy = await PolicyAsync(tenantId, ct);
            if (policy.Version != command.ExpectedVersion)
                throw new PlatformGovernanceConflictException($"AI policy version is {policy.Version}; refresh and retry.");
            var before = Map(policy);
            Apply(policy, command, actorUserId);
            AddAudit(tenantId, actorUserId, idempotencyKey, "POLICY_UPDATED", command.Reason,
                before, Map(policy));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new AiTrustPolicyMutationResult(Map(policy), false);
        });
    }

    public async Task<AiTrustPolicyMutationResult> RollbackAsync(long tenantId, long actorUserId,
        string idempotencyKey, RollbackAiTrustPolicyCommand command, CancellationToken ct)
    {
        PlatformGovernanceService.EnsureActor(tenantId, actorUserId);
        idempotencyKey = PlatformGovernanceService.Required(idempotencyKey, 160, "Idempotency-Key is required.");
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            if (await ReplayAsync(tenantId, idempotencyKey, ct) is not null)
                return new(Map(await PolicyAsync(tenantId, ct)), true);
            var policy = await PolicyAsync(tenantId, ct);
            if (policy.Version != command.ExpectedVersion)
                throw new PlatformGovernanceConflictException($"AI policy version is {policy.Version}; refresh and retry.");
            var target = await db.TenantGovernanceAuditEvents.AsNoTracking().SingleOrDefaultAsync(x =>
                x.BusinessUnitId == tenantId && x.Area == "AITrust" && x.Id == command.AuditEventId, ct)
                ?? throw new PlatformGovernanceNotFoundException("The AI policy audit version was not found.");
            var envelope = JsonSerializer.Deserialize<PolicyAuditEnvelope>(target.EvidenceJson)
                ?? throw new PlatformGovernanceValidationException("The selected audit version cannot be restored.");
            var before = Map(policy);
            Apply(policy, envelope.Before with { ExpectedVersion = policy.Version, Reason = command.Reason }, actorUserId);
            AddAudit(tenantId, actorUserId, idempotencyKey, "POLICY_ROLLED_BACK", command.Reason,
                before, Map(policy));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new AiTrustPolicyMutationResult(Map(policy), false);
        });
    }

    private async Task<AiProcessingPolicy> PolicyAsync(long tenantId, CancellationToken ct) =>
        await db.AiProcessingPolicies.SingleOrDefaultAsync(x => x.BusinessUnitId == tenantId, ct)
        ?? throw new PlatformGovernanceNotFoundException("The tenant AI policy has not been provisioned.");

    private Task<TenantGovernanceAuditEvent?> ReplayAsync(long tenantId, string key, CancellationToken ct) =>
        db.TenantGovernanceAuditEvents.AsNoTracking().SingleOrDefaultAsync(x =>
            x.BusinessUnitId == tenantId && x.IdempotencyKey == key, ct);

    private void AddAudit(long tenantId, long actorUserId, string key, string action, string reason,
        AiTrustPolicyState before, AiTrustPolicyState after) => db.TenantGovernanceAuditEvents.Add(new()
        {
            BusinessUnitId = tenantId,
            Area = "AITrust",
            AggregateType = "AiProcessingPolicy",
            AggregateReference = tenantId.ToString(),
            Action = action,
            Reason = PlatformGovernanceService.Required(reason, 2000, "A policy reason is required."),
            EvidenceJson = JsonSerializer.Serialize(new PolicyAuditEnvelope(ToCommand(before), ToCommand(after))),
            IdempotencyKey = key,
            ActorUserId = actorUserId,
            OccurredOn = DateTime.UtcNow
        });

    private static void Apply(AiProcessingPolicy policy, UpdateAiTrustPolicyCommand command, long actorUserId)
    {
        Validate(command);
        policy.IsEnabled = command.IsEnabled;
        policy.ExternalProcessingAllowed = command.ExternalProcessingAllowed;
        policy.AllowedPurposes = command.AllowedPurposes.Trim();
        policy.AllowedProvider = Trim(command.AllowedProvider);
        policy.AllowedModel = Trim(command.AllowedModel);
        policy.MonthlySoftTokenLimit = command.MonthlySoftTokenLimit;
        policy.MonthlyHardTokenLimit = command.MonthlyHardTokenLimit;
        policy.MaxTokensPerDocument = command.MaxTokensPerDocument;
        policy.ExternalInputCostPerMillionTokens = command.ExternalInputCostPerMillionTokens;
        policy.ExternalOutputCostPerMillionTokens = command.ExternalOutputCostPerMillionTokens;
        policy.ExternalCostCurrency = Trim(command.ExternalCostCurrency)?.ToUpperInvariant();
        policy.ExternalPricingVersion = Trim(command.ExternalPricingVersion);
        policy.ExternalDependencyCeilingPercent = command.ExternalDependencyCeilingPercent;
        policy.RedactionRequired = command.RedactionRequired;
        policy.AllowedDataClassifications = command.AllowedDataClassifications.Trim();
        policy.EgressPolicy = command.EgressPolicy.Trim();
        policy.DataResidency = command.DataResidency.Trim();
        policy.RetentionDays = command.RetentionDays;
        policy.InputOutputAuditAllowed = command.InputOutputAuditAllowed;
        policy.PrivacyReviewRequired = command.PrivacyReviewRequired;
        policy.LocalComputeCostPerHour = command.LocalComputeCostPerHour;
        policy.OcrCostPerPage = command.OcrCostPerPage;
        policy.LocalCostCurrency = Trim(command.LocalCostCurrency)?.ToUpperInvariant();
        policy.Version++;
        policy.UpdatedOn = DateTime.UtcNow;
        policy.UpdatedBy = $"user:{actorUserId}";
    }

    private static void Validate(UpdateAiTrustPolicyCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.AllowedPurposes) || string.IsNullOrWhiteSpace(command.AllowedDataClassifications)
            || string.IsNullOrWhiteSpace(command.EgressPolicy) || string.IsNullOrWhiteSpace(command.DataResidency))
            throw new PlatformGovernanceValidationException("Purposes, data classifications, egress policy and residency are required.");
        if (command.ExternalDependencyCeilingPercent is < 0 or > 10)
            throw new PlatformGovernanceValidationException("External dependency ceiling must be between zero and 10 percent.");
        if (command.RetentionDays is < 1 or > 3650)
            throw new PlatformGovernanceValidationException("Retention must be between one and 3650 days.");
        if (command.MonthlySoftTokenLimit < 0 || command.MonthlyHardTokenLimit < 0
            || command.MaxTokensPerDocument is <= 0
            || command.MonthlySoftTokenLimit > command.MonthlyHardTokenLimit)
            throw new PlatformGovernanceValidationException("Token limits are invalid.");
        if (command.ExternalProcessingAllowed && (!command.RedactionRequired || !command.PrivacyReviewRequired
            || string.IsNullOrWhiteSpace(command.AllowedProvider) || string.IsNullOrWhiteSpace(command.AllowedModel)))
            throw new PlatformGovernanceValidationException("External processing requires redaction, privacy review, an allowed provider and an allowed model.");
        if ((command.ExternalInputCostPerMillionTokens.HasValue || command.ExternalOutputCostPerMillionTokens.HasValue)
            && (command.ExternalInputCostPerMillionTokens is null or < 0 || command.ExternalOutputCostPerMillionTokens is null or < 0
                || string.IsNullOrWhiteSpace(command.ExternalCostCurrency) || string.IsNullOrWhiteSpace(command.ExternalPricingVersion)))
            throw new PlatformGovernanceValidationException("External rates require non-negative input/output rates, currency and pricing version.");
        if ((command.LocalComputeCostPerHour.HasValue || command.OcrCostPerPage.HasValue)
            && (command.LocalComputeCostPerHour is null or < 0 || command.OcrCostPerPage is null or < 0
                || string.IsNullOrWhiteSpace(command.LocalCostCurrency)))
            throw new PlatformGovernanceValidationException("Local rates require non-negative compute/OCR rates and currency.");
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static AiTrustPolicyState Map(AiProcessingPolicy x) => new(x.IsEnabled,
        x.ExternalProcessingAllowed, x.AllowedPurposes, x.AllowedProvider, x.AllowedModel,
        x.MonthlySoftTokenLimit, x.MonthlyHardTokenLimit, x.MaxTokensPerDocument,
        x.ExternalInputCostPerMillionTokens, x.ExternalOutputCostPerMillionTokens,
        x.ExternalCostCurrency, x.ExternalPricingVersion, x.ExternalDependencyCeilingPercent,
        x.RedactionRequired, x.AllowedDataClassifications, x.EgressPolicy, x.DataResidency,
        x.RetentionDays, x.InputOutputAuditAllowed, x.PrivacyReviewRequired,
        x.LocalComputeCostPerHour, x.OcrCostPerPage, x.LocalCostCurrency, x.Version,
        x.UpdatedOn, x.UpdatedBy);
    private static UpdateAiTrustPolicyCommand ToCommand(AiTrustPolicyState x) => new(x.Version,
        x.IsEnabled, x.ExternalProcessingAllowed, x.AllowedPurposes, x.AllowedProvider,
        x.AllowedModel, x.MonthlySoftTokenLimit, x.MonthlyHardTokenLimit, x.MaxTokensPerDocument,
        x.ExternalInputCostPerMillionTokens, x.ExternalOutputCostPerMillionTokens,
        x.ExternalCostCurrency, x.ExternalPricingVersion, x.ExternalDependencyCeilingPercent,
        x.RedactionRequired, x.AllowedDataClassifications, x.EgressPolicy, x.DataResidency,
        x.RetentionDays, x.InputOutputAuditAllowed, x.PrivacyReviewRequired,
        x.LocalComputeCostPerHour, x.OcrCostPerPage, x.LocalCostCurrency, "Audit snapshot");
    private sealed record PolicyAuditEnvelope(UpdateAiTrustPolicyCommand Before, UpdateAiTrustPolicyCommand After);
}
