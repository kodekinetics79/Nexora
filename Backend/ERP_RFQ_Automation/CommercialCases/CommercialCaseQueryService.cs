using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.CommercialCases;

public interface ICommercialCaseQueryService
{
    Task<IReadOnlyList<CommercialCaseSearchResult>> SearchAsync(
        long businessUnitId, string query, int limit, CancellationToken cancellationToken);

    Task<CommercialCaseDetail?> GetAsync(
        long businessUnitId, long commercialCaseId, CancellationToken cancellationToken);
}

public sealed class CommercialCaseQueryService : ICommercialCaseQueryService
{
    private readonly ErpRfqAutomationContext _db;

    public CommercialCaseQueryService(ErpRfqAutomationContext db) => _db = db;

    public async Task<IReadOnlyList<CommercialCaseSearchResult>> SearchAsync(
        long businessUnitId, string query, int limit, CancellationToken cancellationToken)
    {
        var term = query.Trim().ToLower();
        limit = Math.Clamp(limit, 1, 50);

        return await _db.CommercialCases
            .AsNoTracking()
            .Where(c => c.BusinessUnitId == businessUnitId)
            .Where(c =>
                c.MasterReference.ToLower().Contains(term) ||
                (c.Lead.Rfqno != null && c.Lead.Rfqno.ToLower().Contains(term)) ||
                (c.Lead.BuyersName != null && c.Lead.BuyersName.ToLower().Contains(term)) ||
                (c.Lead.Clientemail != null && c.Lead.Clientemail.ToLower().Contains(term)) ||
                (c.Lead.OpportunityNo != null && c.Lead.OpportunityNo.ToLower().Contains(term)) ||
                c.Lead.Rfqs.Any(r => r.Rfqno.ToLower().Contains(term)) ||
                c.Lead.Rfqs.Any(r => r.Quotes.Any(q => q.QuoteNo.ToLower().Contains(term))) ||
                c.Lead.Orders.Any(o => o.OrderNo.ToLower().Contains(term)) ||
                c.Lead.Orders.Any(o => o.Shipments.Any(s => s.ShipmentNo.ToLower().Contains(term) ||
                    (s.TrackingNumber != null && s.TrackingNumber.ToLower().Contains(term)))))
            .OrderByDescending(c => c.CreatedOn)
            .ThenByDescending(c => c.Id)
            .Take(limit)
            .Select(c => new CommercialCaseSearchResult(
                c.Id,
                c.MasterReference,
                c.Lead.Id,
                c.Lead.Rfqno,
                c.Lead.BuyersName,
                c.Lead.Clientemail,
                c.Lead.LeadStatus == null ? null : c.Lead.LeadStatus.SetupValue,
                c.CreatedOn,
                c.Lead.Rfqs.Count,
                c.Lead.Rfqs.SelectMany(r => r.Quotes).Count(),
                c.Lead.Orders.Count,
                c.Lead.Orders.SelectMany(o => o.Shipments).Count()))
            .ToListAsync(cancellationToken);
    }

    public async Task<CommercialCaseDetail?> GetAsync(
        long businessUnitId, long commercialCaseId, CancellationToken cancellationToken)
    {
        var header = await _db.CommercialCases
            .AsNoTracking()
            .Where(c => c.BusinessUnitId == businessUnitId && c.Id == commercialCaseId)
            .Select(c => new
            {
                c.Id,
                c.MasterReference,
                c.AllocationNumber,
                c.BusinessUnitId,
                c.CreatedOn,
                LeadId = c.Lead.Id,
                c.Lead.Rfqno,
                c.Lead.BuyersName,
                c.Lead.Clientemail,
                c.Lead.OpportunityNo,
                Status = c.Lead.LeadStatus == null ? null : c.Lead.LeadStatus.SetupValue,
                LeadCreatedOn = c.Lead.CreatedDate
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (header == null)
            return null;

        var rfqs = await _db.Rfqs.AsNoTracking()
            .Where(r => r.BusinessUnitId == businessUnitId && r.LeadId == header.LeadId)
            .Select(r => new CommercialCaseDocument(
                "RFQ", r.Id, r.Rfqno,
                r.Rfqstatus == null ? null : r.Rfqstatus.SetupValue,
                r.CreatedDate))
            .ToListAsync(cancellationToken);
        var rfqIds = rfqs.Select(r => r.DocumentId).ToArray();

        var quotes = await _db.Quotes.AsNoTracking()
            .Where(q => q.BusinessUnitId == businessUnitId && q.Rfqid != null && rfqIds.Contains(q.Rfqid.Value))
            .Select(q => new CommercialCaseDocument(
                "Quote", q.Id, q.QuoteNo,
                q.Status == null ? null : q.Status.SetupValue,
                q.QuoteDate ?? q.CreatedDate))
            .ToListAsync(cancellationToken);
        var quoteIds = quotes.Select(q => q.DocumentId).ToArray();

        var orders = await _db.Orders.AsNoTracking()
            .Where(o => o.BusinessUnitId == businessUnitId &&
                (o.LeadId == header.LeadId ||
                 (o.Rfqid != null && rfqIds.Contains(o.Rfqid.Value)) ||
                 (o.QuoteId != null && quoteIds.Contains(o.QuoteId.Value))))
            .Select(o => new CommercialCaseDocument(
                "Order", o.Id, o.OrderNo, o.Status.SetupValue, o.OrderDate))
            .ToListAsync(cancellationToken);
        var orderIds = orders.Select(o => o.DocumentId).ToArray();

        var shipments = await _db.Shipments.AsNoTracking()
            .Where(s => s.BusinessUnitId == businessUnitId && orderIds.Contains(s.OrderId))
            .Select(s => new CommercialCaseDocument(
                "Shipment", s.Id, s.ShipmentNo, s.Status.SetupValue, s.ShipmentDate))
            .ToListAsync(cancellationToken);

        var history = await _db.LeadStatusHistories.AsNoTracking()
            .Where(h => h.BusinessUnitId == businessUnitId && h.CommercialCaseId == header.Id)
            .OrderBy(h => h.ChangedOn)
            .Select(h => new CommercialCaseStatusEvent(
                h.Id,
                h.EventType,
                h.PreviousStatusId == null ? null : _db.SetupMasters
                    .Where(s => s.SetupId == h.PreviousStatusId).Select(s => s.SetupValue).FirstOrDefault(),
                h.NewStatusId == null ? null : _db.SetupMasters
                    .Where(s => s.SetupId == h.NewStatusId).Select(s => s.SetupValue).FirstOrDefault(),
                h.ChangedBy,
                h.ActorSource,
                h.ChangedOn,
                h.Reason))
            .ToListAsync(cancellationToken);

        var documents = new List<CommercialCaseDocument>
        {
            new("Lead", header.LeadId, header.Rfqno ?? header.MasterReference,
                header.Status, header.LeadCreatedOn)
        };
        documents.AddRange(rfqs);
        documents.AddRange(quotes);
        documents.AddRange(orders);
        documents.AddRange(shipments);

        return new CommercialCaseDetail(
            header.Id,
            header.MasterReference,
            header.AllocationNumber,
            header.BusinessUnitId,
            header.CreatedOn,
            header.LeadId,
            header.Rfqno,
            header.BuyersName,
            header.Clientemail,
            header.OpportunityNo,
            header.Status,
            documents.OrderBy(d => d.OccurredOn).ThenBy(d => d.DocumentType).ToArray(),
            history);
    }
}
