using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.OrderToCash;
using ERP_RFQ_Automation.Traceability;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Delivery;

public interface IDeliveryNoteReadService
{
    Task<DeliveryNoteView?> GetAsync(
        long businessUnitId, long shipmentId, CancellationToken cancellationToken = default);
}

/// <summary>
/// FR-DLM-01. The delivery note, assembled server-side from the record.
///
/// <para><b>Every number on a signed document comes from here.</b> The printed note previously
/// joined an order response and a shipment response in the browser and subtracted one from the
/// other to show what was left — so a load-order race, a stale cache or a filtered list produced a
/// remaining quantity nobody could reproduce, on a piece of paper somebody had signed. The
/// remaining column is now computed against the same
/// <see cref="IDeliveredQuantityLedger"/> the invoice ceiling uses, which is the only way the note
/// and the invoice can agree.</para>
///
/// <para><b>Arabic is a named gap, not a missing feature.</b> The BRD asks for an Arabic/English
/// note. Decision R6 defers Arabic, RTL and Hijri to a future version and records the deferral as
/// an approved deviation from FR-DLM-01 among others. The document is therefore English-only, and
/// this service names the gap in <see cref="DeliveryNoteView.Gaps"/> so the omission is visible on
/// the artefact rather than only in a register nobody prints. Half-rendering Arabic — a mirrored
/// heading over untranslated line descriptions — would be worse than not doing it.</para>
///
/// <para><b>A document that cannot identify its sender says so on its face.</b>
/// <see cref="IssuerIdentity"/> is read from the tenant's own business-unit record and every field
/// is nullable. Nothing here invents a commercial registration, a VAT number or an address.</para>
/// </summary>
public sealed class DeliveryNoteReadService(
    ErpRfqAutomationContext db, IDeliveredQuantityLedger ledger, IDeliveryConfirmationService proofs)
    : IDeliveryNoteReadService
{
    public const string ArabicGap =
        "Arabic rendering is deferred by decision R6. This note is English-only.";

    private readonly ErpRfqAutomationContext _db = db;
    private readonly IDeliveredQuantityLedger _ledger = ledger;
    private readonly IDeliveryConfirmationService _proofs = proofs;

    public async Task<DeliveryNoteView?> GetAsync(
        long businessUnitId, long shipmentId, CancellationToken cancellationToken = default)
    {
        if (businessUnitId <= 0)
            throw new UnauthorizedAccessException("A valid authenticated tenant is required.");

        var header = await _db.Shipments.AsNoTracking()
            .Where(s => s.BusinessUnitId == businessUnitId && s.Id == shipmentId)
            .Select(s => new
            {
                s.Id,
                s.ShipmentNo,
                s.ShipmentDate,
                s.DeliveryStatus,
                s.OrderId,
                s.Order.OrderNo,
                s.CommercialCaseId,
                s.NexoraSerial,
                CustomerName = s.Order.Customer.Name,
                s.ShippingAddress,
                s.DeliveryCityId,
                s.Carrier,
                s.TrackingNumber
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (header is null) return null;

        var gaps = new List<string> { ArabicGap };

        // FR-DLM-01: the address, mapped to the governed region master rather than parsed out of a
        // free-text blob. The city is stored; the region is its parent state, read from the same
        // SetState/SetCity master CommercialRouting derives a sales territory from.
        string? cityName = null, regionName = null, countryName = null;
        if (header.DeliveryCityId.HasValue)
        {
            var region = await _db.SetCities.AsNoTracking()
                .Where(c => c.Buid == businessUnitId && c.CityId == header.DeliveryCityId.Value)
                .Select(c => new { c.CityName, RegionName = c.State.StateName, CountryName = c.Country.CountryName })
                .SingleOrDefaultAsync(cancellationToken);
            cityName = region?.CityName;
            regionName = region?.RegionName;
            countryName = region?.CountryName;
        }
        if (string.IsNullOrWhiteSpace(regionName))
            gaps.Add("The delivery address is not mapped to a region in the tenant's city master.");
        if (string.IsNullOrWhiteSpace(header.ShippingAddress))
            gaps.Add("No delivery address is recorded on this shipment.");

        // FR-DLM-01: the customer's own purchase-order number. Reached through the award, which is
        // the only link the order actually holds; a commercial case can carry several purchase
        // orders, so deriving it from the case would name the wrong one.
        var customerPo = await (
            from order in _db.Orders.AsNoTracking()
            join award in _db.Set<CustomerAward>().AsNoTracking()
                on new { order.BusinessUnitId, Id = order.CustomerAwardId ?? 0 }
                equals new { award.BusinessUnitId, award.Id }
            join po in _db.Set<CustomerPurchaseOrder>().AsNoTracking()
                on new { award.BusinessUnitId, Id = award.CustomerPurchaseOrderId }
                equals new { po.BusinessUnitId, po.Id }
            where order.BusinessUnitId == businessUnitId && order.Id == header.OrderId
            select new { po.ExternalPoNumber, po.PoDate })
            .FirstOrDefaultAsync(cancellationToken);
        if (customerPo is null)
            gaps.Add("No customer purchase order is linked to this order.");
        if (header.CommercialCaseId is null)
            gaps.Add("This shipment carries no commercial case reference.");

        // The issuer, read from the tenant's own records and nowhere else. Every field may be null,
        // and each null is reported as a gap rather than filled with something plausible: a
        // delivery note carrying an invented Saudi address or VAT number is worse than one that
        // admits it has neither, because the invented one gets used.
        var issuer = await _db.BusinessUnits.AsNoTracking()
            .Where(b => b.Id == businessUnitId)
            .Select(b => new IssuerIdentity(
                b.BusinessUnitName,
                b.TaxRegistrationNumber,
                b.QuoteConfiguration!.CompanyAddress,
                b.QuoteConfiguration.CompanyPhone,
                b.QuoteConfiguration.CompanyEmail))
            .SingleAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(issuer.TaxRegistrationNumber))
            gaps.Add("The despatching business unit has no VAT registration number on file.");
        if (string.IsNullOrWhiteSpace(issuer.AddressLine)
            && string.IsNullOrWhiteSpace(issuer.Phone)
            && string.IsNullOrWhiteSpace(issuer.Email))
            gaps.Add("This delivery note does not identify its sender: no issuing address, "
                     + "telephone or email is configured for this business unit.");

        var manifest = await _db.ShipmentItems.AsNoTracking()
            .Where(si => si.ShipmentId == shipmentId && si.IsActive)
            .OrderBy(si => si.Id)
            .Select(si => new
            {
                si.Id,
                si.OrderItemId,
                si.Quantity,
                si.OrderItem.Description,
                ProductName = si.OrderItem.Product.ProductName,
                Uom = si.OrderItem.Uom!.UomName
            })
            .ToListAsync(cancellationToken);

        var ledgerLines = (await _ledger.ForOrderAsync(businessUnitId, header.OrderId, cancellationToken))
            .ToDictionary(x => x.OrderItemId);

        // FR-DLM-01: the material lots that left on this note. Read from the goods issue the
        // shipment itself drove, so the note names the lots that physically went, not the lots the
        // order happens to be associated with.
        var lots = await (
            from consumption in _db.Set<MaterialLotConsumption>().AsNoTracking()
            join lot in _db.MaterialLots.AsNoTracking()
                on new { consumption.BusinessUnitId, Id = consumption.MaterialLotId }
                equals new { lot.BusinessUnitId, lot.Id }
            where consumption.BusinessUnitId == businessUnitId && consumption.ShipmentId == shipmentId
            select new
            {
                consumption.OrderItemId,
                lot.LotNumber,
                consumption.Quantity,
                lot.CountryOfOrigin
            })
            .ToListAsync(cancellationToken);
        var lotsByLine = lots
            .GroupBy(x => x.OrderItemId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<DeliveryNoteLot>)g
                    .OrderBy(x => x.LotNumber, StringComparer.Ordinal)
                    .Select(x => new DeliveryNoteLot(x.LotNumber, x.Quantity, x.CountryOfOrigin))
                    .ToList());
        if (lotsByLine.Count == 0)
            gaps.Add("No material lots were declared against this despatch.");

        var lines = manifest.Select(m =>
        {
            ledgerLines.TryGetValue(m.OrderItemId, out var ledgerLine);
            var awarded = ledgerLine?.AwardedQuantity ?? 0m;
            var acceptedElsewhere = ledgerLine?.AcceptedQuantity ?? 0m;
            return new DeliveryNoteLine(
                m.Id, m.OrderItemId, m.ProductName, m.Description, m.Uom,
                awarded,
                m.Quantity,
                acceptedElsewhere,
                // Server-computed and stated as such: awarded, less what the customer already has,
                // less what is on THIS note. The browser is handed the answer, never the operands.
                Math.Max(0m, awarded - acceptedElsewhere - m.Quantity),
                lotsByLine.GetValueOrDefault(m.OrderItemId, []));
        }).ToList();

        var proof = await _proofs.GetAsync(businessUnitId, shipmentId, cancellationToken);

        return new DeliveryNoteView(
            header.Id, header.ShipmentNo, header.ShipmentDate, header.DeliveryStatus,
            header.OrderId, header.OrderNo, header.CommercialCaseId, header.NexoraSerial,
            header.CustomerName, customerPo?.ExternalPoNumber, customerPo?.PoDate,
            header.ShippingAddress, header.DeliveryCityId, cityName, regionName, countryName,
            header.Carrier, header.TrackingNumber, issuer, lines, proof, gaps);
    }
}
