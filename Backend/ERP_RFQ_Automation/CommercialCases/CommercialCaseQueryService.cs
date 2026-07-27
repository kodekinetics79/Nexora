using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Agent.Models;
using ERP_RFQ_Automation.OrderToCash;
using ERP_RFQ_Automation.Procurement;
using ERP_RFQ_Automation.SupplierQuotes;
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
    private readonly ITenantContext _tenantContext;

    public CommercialCaseQueryService(ErpRfqAutomationContext db, ITenantContext tenantContext)
    {
        _db = db;
        _tenantContext = tenantContext;
    }

    public async Task<IReadOnlyList<CommercialCaseSearchResult>> SearchAsync(
        long businessUnitId, string query, int limit, CancellationToken cancellationToken)
    {
        businessUnitId = RequireAuthenticatedTenant(businessUnitId);
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
                (c.Lead.EmailIngests.EmailSubject != null && c.Lead.EmailIngests.EmailSubject.ToLower().Contains(term)) ||
                _db.Customers.Any(customer => customer.Buid == businessUnitId && customer.Id == c.Lead.CustomerId &&
                    customer.Name.ToLower().Contains(term)) ||
                _db.Contacts.Any(contact => contact.BusinessUnitId == businessUnitId && contact.CustomerId == c.Lead.CustomerId &&
                    (contact.FirstName.ToLower().Contains(term) || contact.LastName.ToLower().Contains(term) ||
                     (contact.Email != null && contact.Email.ToLower().Contains(term)))) ||
                c.Lead.Rfqs.Any(r => r.Rfqno.ToLower().Contains(term)) ||
                c.Lead.Rfqs.Any(r => r.Rfqitems.Any(item =>
                    (item.ItemMaterialCode != null && item.ItemMaterialCode.ToLower().Contains(term)) ||
                    (item.ManufacturerPartNumber != null && item.ManufacturerPartNumber.ToLower().Contains(term)) ||
                    (item.ManufacturerName != null && item.ManufacturerName.ToLower().Contains(term)) ||
                    (item.Product != null && ((item.Product.ProductName != null && item.Product.ProductName.ToLower().Contains(term)) ||
                        item.Product.PartNo.ToLower().Contains(term))) ||
                    (item.Supplier != null && item.Supplier.Name.ToLower().Contains(term)))) ||
                c.Lead.Rfqs.Any(r => r.Quotes.Any(q => q.QuoteNo.ToLower().Contains(term))) ||
                _db.Set<SupplierSolicitation>().Any(s => s.BusinessUnitId == businessUnitId &&
                    s.NexoraSerial == c.MasterReference && s.SupplierRfqNumber != null && s.SupplierRfqNumber.ToLower().Contains(term)) ||
                _db.SupplierQuotes.Any(sq => sq.BusinessUnitId == businessUnitId && sq.NexoraSerial == c.MasterReference &&
                    sq.SupplierQuoteReference.ToLower().Contains(term)) ||
                _db.CustomerPurchaseOrders.Any(po => po.BusinessUnitId == businessUnitId && po.CommercialCaseId == c.Id &&
                    (po.InternalNumber.ToLower().Contains(term) || po.ExternalPoNumber.ToLower().Contains(term))) ||
                _db.Orders.Any(o => o.BusinessUnitId == businessUnitId &&
                    (o.LeadId == c.Lead.Id ||
                     (o.Rfqid.HasValue && c.Lead.Rfqs.Any(r => r.Id == o.Rfqid.Value)) ||
                     (o.QuoteId.HasValue && c.Lead.Rfqs.Any(r => r.Quotes.Any(q => q.Id == o.QuoteId.Value)))) &&
                    o.OrderNo.ToLower().Contains(term)) ||
                _db.SupplierPurchaseOrders.Any(po => po.BusinessUnitId == businessUnitId &&
                    c.Lead.Rfqs.Any(r => r.Id == po.RfqId) && po.PurchaseOrderNumber.ToLower().Contains(term)) ||
                _db.ProcurementHandoffs.Any(h => h.BusinessUnitId == businessUnitId && h.NexoraSerial == c.MasterReference &&
                    h.ExternalSupplierPoNumber != null && h.ExternalSupplierPoNumber.ToLower().Contains(term)) ||
                _db.Shipments.Any(s => s.BusinessUnitId == businessUnitId &&
                    _db.Orders.Any(o => o.Id == s.OrderId && o.BusinessUnitId == businessUnitId &&
                        (o.LeadId == c.Lead.Id ||
                         (o.Rfqid.HasValue && c.Lead.Rfqs.Any(r => r.Id == o.Rfqid.Value)) ||
                         (o.QuoteId.HasValue && c.Lead.Rfqs.Any(r => r.Quotes.Any(q => q.Id == o.QuoteId.Value))))) &&
                    (s.ShipmentNo.ToLower().Contains(term) ||
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
                _db.Orders.Count(o => o.BusinessUnitId == businessUnitId &&
                    (o.LeadId == c.Lead.Id ||
                     (o.Rfqid.HasValue && c.Lead.Rfqs.Any(r => r.Id == o.Rfqid.Value)) ||
                     (o.QuoteId.HasValue && c.Lead.Rfqs.Any(r => r.Quotes.Any(q => q.Id == o.QuoteId.Value))))),
                _db.Shipments.Count(s => s.BusinessUnitId == businessUnitId &&
                    _db.Orders.Any(o => o.Id == s.OrderId && o.BusinessUnitId == businessUnitId &&
                        (o.LeadId == c.Lead.Id ||
                         (o.Rfqid.HasValue && c.Lead.Rfqs.Any(r => r.Id == o.Rfqid.Value)) ||
                         (o.QuoteId.HasValue && c.Lead.Rfqs.Any(r => r.Quotes.Any(q => q.Id == o.QuoteId.Value)))))),
                c.MasterReference.ToLower().Contains(term) ? "Nexora Serial" :
                ((c.Lead.Rfqno != null && c.Lead.Rfqno.ToLower().Contains(term)) ||
                 c.Lead.Rfqs.Any(r => r.Rfqno.ToLower().Contains(term))) ? "Customer RFQ" :
                _db.Customers.Any(customer => customer.Buid == businessUnitId && customer.Id == c.Lead.CustomerId &&
                    customer.Name.ToLower().Contains(term)) ? "Customer" :
                ((c.Lead.BuyersName != null && c.Lead.BuyersName.ToLower().Contains(term)) ||
                 (c.Lead.Clientemail != null && c.Lead.Clientemail.ToLower().Contains(term)) ||
                 _db.Contacts.Any(contact => contact.BusinessUnitId == businessUnitId && contact.CustomerId == c.Lead.CustomerId &&
                    (contact.FirstName.ToLower().Contains(term) || contact.LastName.ToLower().Contains(term) ||
                     (contact.Email != null && contact.Email.ToLower().Contains(term))))) ? "Contact" :
                c.Lead.Rfqs.Any(r => r.Rfqitems.Any(item =>
                    (item.ItemMaterialCode != null && item.ItemMaterialCode.ToLower().Contains(term)) ||
                    (item.ManufacturerPartNumber != null && item.ManufacturerPartNumber.ToLower().Contains(term)) ||
                    (item.ManufacturerName != null && item.ManufacturerName.ToLower().Contains(term)) ||
                    (item.Product != null && ((item.Product.ProductName != null && item.Product.ProductName.ToLower().Contains(term)) ||
                        item.Product.PartNo.ToLower().Contains(term))))) ? "Part or manufacturer" :
                c.Lead.Rfqs.Any(r => r.Rfqitems.Any(item => item.Supplier != null && item.Supplier.Name.ToLower().Contains(term))) ? "Supplier" :
                c.Lead.Rfqs.Any(r => r.Quotes.Any(q => q.QuoteNo.ToLower().Contains(term))) ? "Customer Quote" :
                _db.CustomerPurchaseOrders.Any(po => po.BusinessUnitId == businessUnitId && po.CommercialCaseId == c.Id &&
                    (po.InternalNumber.ToLower().Contains(term) || po.ExternalPoNumber.ToLower().Contains(term))) ? "Client PO" :
                _db.Orders.Any(o => o.BusinessUnitId == businessUnitId &&
                    (o.LeadId == c.Lead.Id || (o.Rfqid.HasValue && c.Lead.Rfqs.Any(r => r.Id == o.Rfqid.Value)) ||
                     (o.QuoteId.HasValue && c.Lead.Rfqs.Any(r => r.Quotes.Any(q => q.Id == o.QuoteId.Value)))) &&
                    o.OrderNo.ToLower().Contains(term)) ? "Customer Order" :
                (_db.SupplierPurchaseOrders.Any(po => po.BusinessUnitId == businessUnitId && c.Lead.Rfqs.Any(r => r.Id == po.RfqId) &&
                    po.PurchaseOrderNumber.ToLower().Contains(term)) ||
                 _db.ProcurementHandoffs.Any(h => h.BusinessUnitId == businessUnitId && h.NexoraSerial == c.MasterReference &&
                    h.ExternalSupplierPoNumber != null && h.ExternalSupplierPoNumber.ToLower().Contains(term))) ? "Supplier PO" :
                (c.Lead.EmailIngests.EmailSubject != null && c.Lead.EmailIngests.EmailSubject.ToLower().Contains(term)) ? "Email subject" :
                (c.Lead.OpportunityNo != null && c.Lead.OpportunityNo.ToLower().Contains(term)) ? "Opportunity" :
                "Related commercial record"))
            .ToListAsync(cancellationToken);
    }

    public async Task<CommercialCaseDetail?> GetAsync(
        long businessUnitId, long commercialCaseId, CancellationToken cancellationToken)
    {
        businessUnitId = RequireAuthenticatedTenant(businessUnitId);
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
                r.CreatedDate, null))
            .ToListAsync(cancellationToken);
        var rfqIds = rfqs.Select(r => r.DocumentId).ToArray();

        var quotes = await _db.Quotes.AsNoTracking()
            .Where(q => q.BusinessUnitId == businessUnitId && q.Rfqid != null && rfqIds.Contains(q.Rfqid.Value))
            .Select(q => new CommercialCaseDocument(
                "Quote", q.Id, q.QuoteNo,
                q.Status == null ? null : q.Status.SetupValue,
                q.QuoteDate ?? q.CreatedDate, null))
            .ToListAsync(cancellationToken);
        var quoteIds = quotes.Select(q => q.DocumentId).ToArray();

        var orders = await _db.Orders.AsNoTracking()
            .Where(o => o.BusinessUnitId == businessUnitId &&
                (o.LeadId == header.LeadId ||
                 (o.Rfqid != null && rfqIds.Contains(o.Rfqid.Value)) ||
                 (o.QuoteId != null && quoteIds.Contains(o.QuoteId.Value))))
            .Select(o => new CommercialCaseDocument(
                "Order", o.Id, o.OrderNo, o.Status.SetupValue, o.OrderDate, null))
            .ToListAsync(cancellationToken);
        var orderIds = orders.Select(o => o.DocumentId).ToArray();

        var shipments = await _db.Shipments.AsNoTracking()
            .Where(s => s.BusinessUnitId == businessUnitId && orderIds.Contains(s.OrderId))
            .Select(s => new CommercialCaseDocument(
                "Shipment", s.Id, s.ShipmentNo, s.Status.SetupValue, s.ShipmentDate, null))
            .ToListAsync(cancellationToken);

        var sourcingCases = await _db.SourcingCases.AsNoTracking()
            .Where(s => s.BusinessUnitId == businessUnitId &&
                (s.LeadId == header.LeadId || rfqIds.Contains(s.RfqId)))
            .Select(s => new CommercialCaseDocument(
                "SourcingCase", s.Id, $"Sourcing Case {s.Id}", s.Status, s.UpdatedOn, null))
            .ToListAsync(cancellationToken);

        var supplierRfqs = await _db.Set<SupplierSolicitation>().AsNoTracking()
            .Where(s => s.BusinessUnitId == businessUnitId &&
                (s.NexoraSerial == header.MasterReference || rfqIds.Contains(s.RfqId)))
            .Select(s => new CommercialCaseDocument(
                "SupplierRFQ", s.Id, s.SupplierRfqNumber ?? $"Supplier RFQ {s.Id}",
                s.Status.ToString(), s.UpdatedOn, s.RfqId))
            .ToListAsync(cancellationToken);

        var supplierQuotes = await _db.SupplierQuotes.AsNoTracking()
            .Where(s => s.BusinessUnitId == businessUnitId &&
                (s.NexoraSerial == header.MasterReference || rfqIds.Contains(s.RfqId)))
            .Select(s => new CommercialCaseDocument(
                "SupplierQuote", s.Id, s.SupplierQuoteReference, s.InboxStatus, s.UpdatedOn, null))
            .ToListAsync(cancellationToken);

        var clientPurchaseOrders = await _db.CustomerPurchaseOrders.AsNoTracking()
            .Where(po => po.BusinessUnitId == businessUnitId && po.CommercialCaseId == header.Id)
            .Select(po => new CommercialCaseDocument(
                "ClientPO", po.Id, po.ExternalPoNumber, po.Status, po.ReceivedOn, null))
            .ToListAsync(cancellationToken);

        var supplierPurchaseOrders = await _db.SupplierPurchaseOrders.AsNoTracking()
            .Where(po => po.BusinessUnitId == businessUnitId && rfqIds.Contains(po.RfqId))
            .Select(po => new CommercialCaseDocument(
                "SupplierPO", po.Id, po.PurchaseOrderNumber, po.Status, po.CreatedOn, null))
            .ToListAsync(cancellationToken);

        var procurementHandoffs = await _db.ProcurementHandoffs.AsNoTracking()
            .Where(h => h.BusinessUnitId == businessUnitId && h.NexoraSerial == header.MasterReference)
            .Select(h => new CommercialCaseDocument(
                "ProcurementHandoff", h.Id,
                h.ExternalSupplierPoNumber ?? $"Handoff {h.Id}", h.Status, h.ModifiedOn ?? h.CreatedOn, null))
            .ToListAsync(cancellationToken);

        var lifecycleHistory = await _db.CommercialLifecycleEvents.AsNoTracking()
            .Where(h => h.BusinessUnitId == businessUnitId && h.CommercialCaseId == header.Id)
            .Select(h => new CommercialCaseStatusEvent(
                h.Id,
                h.EventType,
                h.PreviousStatusCode,
                h.NewStatusCode,
                h.ActorId,
                h.ActorSource,
                h.OccurredOn,
                h.ReasonNotes,
                h.AggregateType,
                h.CorrelationId,
                h.RequestReference,
                h.PolicyVersion,
                h.ReasonCode))
            .ToListAsync(cancellationToken);

        var legacyHistory = await _db.LeadStatusHistories.AsNoTracking()
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
                h.Reason,
                null,
                null,
                null,
                null,
                null))
            .ToListAsync(cancellationToken);
        var duplicateWindow = TimeSpan.FromSeconds(2);
        var nonDuplicatedLegacy = legacyHistory.Where(legacy =>
            legacy.EventType != "StatusChanged" || !lifecycleHistory.Any(governed =>
                governed.AggregateType == "Lead" &&
                (governed.ChangedOn - legacy.ChangedOn).Duration() <= duplicateWindow));
        var history = nonDuplicatedLegacy.Concat(lifecycleHistory)
            .OrderBy(h => h.ChangedOn)
            .ThenBy(h => h.Id)
            .ToArray();

        var documents = new List<CommercialCaseDocument>
        {
            new("Lead", header.LeadId, header.Rfqno ?? header.MasterReference,
                header.Status, header.LeadCreatedOn)
        };
        documents.AddRange(rfqs);
        documents.AddRange(quotes);
        documents.AddRange(sourcingCases);
        documents.AddRange(supplierRfqs);
        documents.AddRange(supplierQuotes);
        documents.AddRange(clientPurchaseOrders);
        documents.AddRange(orders);
        documents.AddRange(supplierPurchaseOrders);
        documents.AddRange(procurementHandoffs);
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

    private long RequireAuthenticatedTenant(long requestedBusinessUnitId)
    {
        var authenticated = _tenantContext.BusinessUnitId;
        if (!authenticated.HasValue || authenticated.Value <= 0 || authenticated.Value != requestedBusinessUnitId)
            throw new UnauthorizedAccessException("A matching authenticated tenant context is required.");
        return authenticated.Value;
    }

}
