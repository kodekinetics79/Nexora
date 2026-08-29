using System.Security.Claims;
using ERP_RFQ_Automation.Authorization;

namespace ERP_RFQ_Automation.CommercialCases.Participation;

/// <summary>
/// Maker-checker boundary for the commercial commitment. Sales representatives may prepare fit
/// assessments and participation drafts, but only a stored manager-or-higher role may commit that
/// decision or promote it into a formal RFQ.
/// </summary>
internal static class ParticipationDecisionAuthority
{
    internal static async Task<bool> CanCommitOrPromoteAsync(
        ClaimsPrincipal actor, long businessUnitId, IRoleGate roleGate)
    {
        if (businessUnitId <= 0
            || !long.TryParse(actor.FindFirst("roleId")?.Value, out var roleId)
            || roleId <= 0)
            return false;

        return await roleGate.IsManagerOrAdminAsync(roleId, businessUnitId);
    }
}
