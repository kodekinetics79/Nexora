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
    /// <summary>
    /// The first instant from which <see cref="AiRequest.ExternalAuthorizationId"/> is written
    /// on EVERY authorized external reservation (555c5b8, 2026-08-10). The column itself
    /// arrived on 2026-08-04 and no migration backfills it, so an external row older than this
    /// is unclassifiable: it may have been fully authorized and simply not recorded.
    ///
    /// <para>Reporting callers floor their sample here rather than counting those rows as
    /// unauthorized. Counting them is not conservative, it is wrong in the direction that
    /// costs most — a standing Critical on a tenant whose allow-list has never refused
    /// anything, which is precisely the alarm-nobody-can-act-on this control was fixed to
    /// stop raising. Enforcement never reaches for this: it decides on the live grant at
    /// reservation time, not on what an old row remembers.</para>
    /// </summary>
    public static readonly DateTime ReceiptRecordingBegan =
        new(2026, 8, 11, 0, 0, 0, DateTimeKind.Utc);

    public sealed record GovernedCall(
        AiProviderClass ProviderClass, long? ExternalAuthorizationId, string Status);

    /// <param name="notBefore">
    /// Optional floor on the sample. Enforcement passes none: it asks "what has this tenant
    /// been doing lately", and a row's age does not make it less true.
    ///
    /// <para>A REPORTING caller must pass one, and the reason is the authorization receipt.
    /// <see cref="AiRequest.ExternalAuthorizationId"/> has only been written on every
    /// authorized external reservation since 555c5b8 (2026-08-10); before that it was written
    /// only when the ceiling was also waived, and no migration backfills it. So an authorized
    /// call reserved by an older build is indistinguishable from an unauthorized one, and an
    /// unbounded window lets those rows vote forever — twelve of them with nothing else in the
    /// ledger reported 100% dependency beside a card reading "Monthly requests 0". Bounding
    /// the sample to the period the screen is already reporting on excludes them by
    /// construction, and makes the percentage reconcilable with the counters beside it.</para>
    /// </param>
    public static async Task<AiExternalDependencySnapshot> EvaluateAsync(
        IQueryable<AiRequest> requests,
        long businessUnitId,
        decimal ceilingPercent,
        CancellationToken ct,
        DateTime? notBefore = null)
    {
        var recent = await requests.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.Status != AiCallStatuses.Denied
                && (notBefore == null || x.CreatedOn >= notBefore))
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
        var exact = recent.Count == 0 ? 0m : 100m * dependentExternal / recent.Count;
        var share = Math.Round(exact, 1);

        return new(
            recent.Count,
            recent.Count(x => x.ProviderClass == AiProviderClass.Local),
            external,
            authorized,
            recent.Count(x => x.Status is AiCallStatuses.Reserved or AiCallStatuses.Running),
            share,
            ceilingPercent,
            AiPolicyDenials.DependencyWindow,
            // Judged on the EXACT share, reported on the rounded one. Comparing the rounded
            // value let a tenant sit genuinely over a fractional ceiling with no warning:
            // 2 unauthorized in 79 is 2.53%, rounds to 2.5, and 2.5 > 2.5 is false. The
            // ceiling is validated to 0..10 with two decimals, so fractional ceilings are
            // a shape an operator can actually set.
            exact > ceilingPercent);
    }
}
