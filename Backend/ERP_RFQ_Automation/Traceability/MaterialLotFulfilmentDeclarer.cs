using ERP_RFQ_Automation.Inventory;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Traceability;

/// <summary>
/// Gate 6 / FR-INV-03. Turns "these lots physically left on this despatch" into the declared
/// consumption rows FR-MTR-03's where-used trace reads.
///
/// <para>This is the adapter that closes Gate 5's last named gap. It adds no rule of its own: the
/// quarantine block, the certificate-expiry policy, the override attribution, the remaining-quantity
/// check and the append-only audit write all stay in <see cref="IMaterialTraceabilityService"/>,
/// which is the only writer of a consumption row. All this class does is decide, per lot, whether
/// the despatch's single override reason is the one that lot needs.</para>
///
/// <para><b>Why the override is decided here rather than passed through.</b>
/// <c>DeclareConsumptionAsync</c> refuses an override on a lot whose certificates are in date — a
/// deliberate guard, so that supplying one cannot become a reflex. A despatch, though, ships several
/// lots at once and the supervisor signs once for the despatch. Passing the same reason to every lot
/// would trip that guard on the compliant ones and fail a legitimate shipment; dropping it entirely
/// would fail the lapsed ones. So the reason is routed only to the lots that are actually lapsed,
/// and if none of them are, the despatch is refused outright rather than accepting a signature for
/// a risk nobody took.</para>
/// </summary>
public sealed class MaterialLotFulfilmentDeclarer(
    ErpRfqAutomationContext db,
    IMaterialTraceabilityService traceability) : ILotFulfilmentDeclarer
{
    private readonly ErpRfqAutomationContext _db = db;
    private readonly IMaterialTraceabilityService _traceability = traceability;

    public async Task DeclareIssueAsync(
        long businessUnitId, IReadOnlyList<LotIssueLine> lines, string actor, string correlationId,
        string? complianceOverrideReason = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var declared = lines.Where(x => x.Quantity > 0m).ToArray();
        var reason = (complianceOverrideReason ?? string.Empty).Trim();

        if (declared.Length == 0)
        {
            if (reason.Length > 0)
                throw new MaterialTraceabilityValidationException(
                    "A compliance override was supplied for a despatch that issues no lot-controlled "
                    + "material. There is nothing for it to authorise.");
            return;
        }

        var lotIds = declared.Select(x => x.MaterialLotId).Distinct().ToArray();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var certificates = (await _db.Set<MaterialLotCertificate>().AsNoTracking()
                .Where(c => c.BusinessUnitId == businessUnitId && lotIds.Contains(c.MaterialLotId))
                .Select(c => new { c.MaterialLotId, c.CertificateType, c.ExpiresOn })
                .ToListAsync(ct))
            .GroupBy(c => c.MaterialLotId)
            .ToDictionary(g => g.Key, g => g.Select(c => (c.CertificateType, c.ExpiresOn)).ToArray());

        var lapsed = lotIds.Where(id => MaterialTraceabilityService.ComplianceState(
                certificates.GetValueOrDefault(id, []), today)
            == MaterialLotComplianceStates.CertificateExpired).ToHashSet();

        if (reason.Length > 0 && lapsed.Count == 0)
            throw new MaterialTraceabilityValidationException(
                "A compliance override was supplied but every lot on this despatch holds certificates "
                + "that are in date. An override that is always present stops meaning anything.");

        // Deterministic per (issue, lot, line): the same despatch replayed declares the same row
        // rather than a second one, and two lines of the same order shipping the same lot stay
        // distinguishable.
        foreach (var line in declared.OrderBy(x => x.OrderItemId).ThenBy(x => x.MaterialLotId))
            await _traceability.DeclareConsumptionAsync(new DeclareLotConsumptionCommand(
                businessUnitId,
                line.MaterialLotId,
                line.OrderId,
                line.OrderItemId,
                line.ShipmentId,
                line.Quantity,
                lapsed.Contains(line.MaterialLotId) && reason.Length > 0 ? reason : null,
                actor,
                correlationId,
                $"{correlationId}:lot:{line.MaterialLotId}:line:{line.OrderItemId}"), ct);
    }
}
