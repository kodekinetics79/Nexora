using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.AI;

public sealed record AiExternalDependencySnapshot(
    int Total,
    int Local,
    int External,
    int AuthorizedExternal,
    int Unresolved,
    decimal ExternalSharePercent,
    decimal CeilingPercent,
    int WindowSize,
    bool CeilingBreached);

/// <summary>
/// One reporting projection for the external-dependency control enforced by
/// <see cref="AiGovernanceService"/>. Denied reservations are not governed calls, the sample
/// is the latest bounded window, and external calls carrying their authorization receipt are
/// reported but do not consume the unauthorized-dependency ceiling.
/// </summary>
public static class AiExternalDependencyEvaluator
{
    public sealed record GovernedCall(
        AiProviderClass ProviderClass, long? ExternalAuthorizationId, string Status);

    public static async Task<AiExternalDependencySnapshot> EvaluateAsync(
        IQueryable<AiRequest> requests,
        long businessUnitId,
        decimal ceilingPercent,
        CancellationToken ct)
    {
        var recent = await requests.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.Status != AiCallStatuses.Denied)
            .OrderByDescending(x => x.CreatedOn)
            .Take(AiPolicyDenials.DependencyWindow)
            .Select(x => new GovernedCall(x.ProviderClass, x.ExternalAuthorizationId, x.Status))
            .ToListAsync(ct);

        return Evaluate(recent, ceilingPercent);
    }

    public static AiExternalDependencySnapshot Evaluate(
        IReadOnlyList<GovernedCall> recent, decimal ceilingPercent)
    {

        var external = recent.Count(x => x.ProviderClass == AiProviderClass.External);
        var authorized = recent.Count(x => x.ProviderClass == AiProviderClass.External
            && x.ExternalAuthorizationId != null);
        var dependentExternal = external - authorized;
        var share = recent.Count == 0
            ? 0m
            : Math.Round(100m * dependentExternal / recent.Count, 1);

        return new(
            recent.Count,
            recent.Count(x => x.ProviderClass == AiProviderClass.Local),
            external,
            authorized,
            recent.Count(x => x.Status is AiCallStatuses.Reserved or AiCallStatuses.Running),
            share,
            ceilingPercent,
            AiPolicyDenials.DependencyWindow,
            share > ceilingPercent);
    }
}
