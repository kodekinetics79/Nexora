namespace ERP_RFQ_Automation.Inventory;

/// <summary>
/// One lot's share of one goods issue, as the issue itself observed it.
/// </summary>
/// <param name="MaterialLotId">The lot the consumed hold named.</param>
/// <param name="OrderId">The sales order the stock left against.</param>
/// <param name="OrderItemId">The order line.</param>
/// <param name="ShipmentId">
/// The delivery note, when the issue was driven by one. Null for the order-scoped consume
/// endpoint, which is a real state — the material is committed and gone but no despatch note
/// names it — and the trace reports it as such rather than inventing one.
/// </param>
/// <param name="Quantity">Units physically issued from this lot by THIS issue.</param>
public sealed record LotIssueLine(
    long MaterialLotId, long OrderId, long OrderItemId, long? ShipmentId, decimal Quantity);

/// <summary>
/// The seam between the goods issue and material traceability.
///
/// <para><b>Why a port and not a direct call.</b> Traceability already depends on inventory — it
/// reclassifies stock through <c>IStockLedgerService</c> and reads availability — so calling
/// <c>IMaterialTraceabilityService</c> from the reservation engine would close a dependency loop
/// between two modules that are meant to be layered. More practically: the declaration carries
/// certificate-compliance rules, an override policy and an append-only audit write that the
/// reservation engine has no business knowing about, and duplicating any of it here would be a
/// second implementation of a control to keep in step with the first.</para>
///
/// <para><b>What it exists to fix.</b> Gate 5 shipped where-used trace driven by declared
/// consumption, and nothing declared. Every despatch therefore issued stock without naming a lot
/// and produced an <c>UndeclaredFulfilment</c> gap until somebody remembered to declare it by hand
/// — honest, because the gap was visible, but it made trace completeness a matter of operator
/// discipline. Now that a hold names its lot, the issue knows exactly what left the building and
/// says so.</para>
/// </summary>
public interface ILotFulfilmentDeclarer
{
    /// <summary>
    /// Declares each line's lot against its order and delivery note, inside the caller's
    /// transaction. Idempotent per (issue correlation, lot, order line).
    ///
    /// <para>Throws rather than swallowing: a lot whose certificates have lapsed refuses the
    /// declaration unless <paramref name="complianceOverrideReason"/> carries an accepted,
    /// attributable reason, and a declaration that cannot be made must fail the goods issue rather
    /// than let stock leave with a gap nobody was told about.</para>
    /// </summary>
    /// <param name="complianceOverrideReason">
    /// The despatch supervisor's written acceptance of a documented lapse, applied only to the
    /// lots that actually need it. Supplying one when every lot is in date is refused — an
    /// override that is always present stops meaning anything.
    /// </param>
    Task DeclareIssueAsync(
        long businessUnitId,
        IReadOnlyList<LotIssueLine> lines,
        string actor,
        string correlationId,
        string? complianceOverrideReason = null,
        CancellationToken ct = default);
}

/// <summary>
/// The no-op declarer. Used only where material traceability is not composed into the host — it
/// exists so a caller cannot be written against a null reference, NOT as a production fallback.
/// If this is ever reached in a running system, lot consumption silently stops being declared,
/// which is why <see cref="Program"/> registers the real adapter unconditionally.
/// </summary>
public sealed class NullLotFulfilmentDeclarer : ILotFulfilmentDeclarer
{
    public Task DeclareIssueAsync(long businessUnitId, IReadOnlyList<LotIssueLine> lines, string actor,
        string correlationId, string? complianceOverrideReason = null, CancellationToken ct = default)
        => Task.CompletedTask;
}
