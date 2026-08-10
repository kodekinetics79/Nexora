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

/// <summary>
/// Reads the commercial case — the identity allocated at lead ingestion — and everything that
/// declares it.
///
/// <para><b>The case id is the read key.</b> The timeline used to be reconstructed by walking
/// foreign keys (lead → RFQ → quote → order → shipment), which made every
/// <c>CommercialCaseId</c> column decorative: a record carrying the WRONG case still appeared,
/// a record carrying the RIGHT one behind a broken join was invisible, and every unpopulated
/// column was a silent defect. Membership is now decided by what each document declares.</para>
///
/// <para>The foreign-key walk survives only as <em>reconciliation evidence</em>. It never adds a
/// document to the timeline and never substitutes for a missing key. Where the two views disagree
/// the disagreement is reported in <see cref="CommercialCaseDetail.TraceabilityGaps"/> — a
/// document reachable through the chain that declares no case, or a different one, is named there
/// rather than quietly folded in or quietly dropped.</para>
/// </summary>
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

        // Every clause that reaches a downstream document does so through that document's declared
        // case (its CommercialCaseId, or its Nexora Serial where the surrogate key does not exist
        // yet), never through a join. Typing a document number that carries the wrong case must not
        // surface this case, and the counts on the card must be the same population the timeline
        // shows.
        return await _db.CommercialCases
            .AsNoTracking()
            .Where(c => c.BusinessUnitId == businessUnitId)
            .Where(c =>
                c.MasterReference.ToLower().Contains(term) ||
                (c.Lead.Rfqno != null && c.Lead.Rfqno.ToLower().Contains(term)) ||
                (c.Lead.BuyersName != null && c.Lead.BuyersName.ToLower().Contains(term)) ||
                (c.Lead.Clientemail != null && c.Lead.Clientemail.ToLower().Contains(term)) ||
                (c.Lead.OpportunityNo != null && c.Lead.OpportunityNo.ToLower().Contains(term)) ||
                (c.Lead.EmailIngests != null && c.Lead.EmailIngests.EmailSubject != null && c.Lead.EmailIngests.EmailSubject.ToLower().Contains(term)) ||
                _db.Customers.Any(customer => customer.Buid == businessUnitId && customer.Id == c.Lead.CustomerId &&
                    customer.Name.ToLower().Contains(term)) ||
                _db.Contacts.Any(contact => contact.BusinessUnitId == businessUnitId && contact.CustomerId == c.Lead.CustomerId &&
                    (contact.FirstName.ToLower().Contains(term) || contact.LastName.ToLower().Contains(term) ||
                     (contact.Email != null && contact.Email.ToLower().Contains(term)))) ||
                _db.Rfqs.Any(r => r.BusinessUnitId == businessUnitId && r.CommercialCaseId == c.Id &&
                    r.Rfqno.ToLower().Contains(term)) ||
                _db.Rfqs.Any(r => r.BusinessUnitId == businessUnitId && r.CommercialCaseId == c.Id &&
                    r.Rfqitems.Any(item =>
                        (item.ItemMaterialCode != null && item.ItemMaterialCode.ToLower().Contains(term)) ||
                        (item.ManufacturerPartNumber != null && item.ManufacturerPartNumber.ToLower().Contains(term)) ||
                        (item.ManufacturerName != null && item.ManufacturerName.ToLower().Contains(term)) ||
                        (item.Product != null && ((item.Product.ProductName != null && item.Product.ProductName.ToLower().Contains(term)) ||
                            item.Product.PartNo.ToLower().Contains(term))) ||
                        (item.Supplier != null && item.Supplier.Name.ToLower().Contains(term)))) ||
                _db.Quotes.Any(q => q.BusinessUnitId == businessUnitId && q.CommercialCaseId == c.Id &&
                    q.QuoteNo.ToLower().Contains(term)) ||
                _db.Set<SupplierSolicitation>().Any(s => s.BusinessUnitId == businessUnitId &&
                    s.NexoraSerial == c.MasterReference && s.SupplierRfqNumber != null && s.SupplierRfqNumber.ToLower().Contains(term)) ||
                _db.SupplierQuotes.Any(sq => sq.BusinessUnitId == businessUnitId && sq.NexoraSerial == c.MasterReference &&
                    sq.SupplierQuoteReference.ToLower().Contains(term)) ||
                _db.CustomerPurchaseOrders.Any(po => po.BusinessUnitId == businessUnitId && po.CommercialCaseId == c.Id &&
                    (po.InternalNumber.ToLower().Contains(term) || po.ExternalPoNumber.ToLower().Contains(term))) ||
                _db.Orders.Any(o => o.BusinessUnitId == businessUnitId && o.CommercialCaseId == c.Id &&
                    o.OrderNo.ToLower().Contains(term)) ||
                _db.SupplierPurchaseOrders.Any(po => po.BusinessUnitId == businessUnitId &&
                    po.CommercialCaseId == c.Id && po.PurchaseOrderNumber.ToLower().Contains(term)) ||
                _db.ProcurementHandoffs.Any(h => h.BusinessUnitId == businessUnitId && h.NexoraSerial == c.MasterReference &&
                    h.ExternalSupplierPoNumber != null && h.ExternalSupplierPoNumber.ToLower().Contains(term)) ||
                _db.Shipments.Any(s => s.BusinessUnitId == businessUnitId && s.CommercialCaseId == c.Id &&
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
                _db.Rfqs.Count(r => r.BusinessUnitId == businessUnitId && r.CommercialCaseId == c.Id),
                _db.Quotes.Count(q => q.BusinessUnitId == businessUnitId && q.CommercialCaseId == c.Id),
                _db.Orders.Count(o => o.BusinessUnitId == businessUnitId && o.CommercialCaseId == c.Id),
                _db.Shipments.Count(s => s.BusinessUnitId == businessUnitId && s.CommercialCaseId == c.Id),
                c.MasterReference.ToLower().Contains(term) ? "Nexora Serial" :
                ((c.Lead.Rfqno != null && c.Lead.Rfqno.ToLower().Contains(term)) ||
                 _db.Rfqs.Any(r => r.BusinessUnitId == businessUnitId && r.CommercialCaseId == c.Id &&
                    r.Rfqno.ToLower().Contains(term))) ? "Customer RFQ" :
                _db.Customers.Any(customer => customer.Buid == businessUnitId && customer.Id == c.Lead.CustomerId &&
                    customer.Name.ToLower().Contains(term)) ? "Customer" :
                ((c.Lead.BuyersName != null && c.Lead.BuyersName.ToLower().Contains(term)) ||
                 (c.Lead.Clientemail != null && c.Lead.Clientemail.ToLower().Contains(term)) ||
                 _db.Contacts.Any(contact => contact.BusinessUnitId == businessUnitId && contact.CustomerId == c.Lead.CustomerId &&
                    (contact.FirstName.ToLower().Contains(term) || contact.LastName.ToLower().Contains(term) ||
                     (contact.Email != null && contact.Email.ToLower().Contains(term))))) ? "Contact" :
                _db.Rfqs.Any(r => r.BusinessUnitId == businessUnitId && r.CommercialCaseId == c.Id &&
                    r.Rfqitems.Any(item =>
                        (item.ItemMaterialCode != null && item.ItemMaterialCode.ToLower().Contains(term)) ||
                        (item.ManufacturerPartNumber != null && item.ManufacturerPartNumber.ToLower().Contains(term)) ||
                        (item.ManufacturerName != null && item.ManufacturerName.ToLower().Contains(term)) ||
                        (item.Product != null && ((item.Product.ProductName != null && item.Product.ProductName.ToLower().Contains(term)) ||
                            item.Product.PartNo.ToLower().Contains(term))))) ? "Part or manufacturer" :
                _db.Rfqs.Any(r => r.BusinessUnitId == businessUnitId && r.CommercialCaseId == c.Id &&
                    r.Rfqitems.Any(item => item.Supplier != null && item.Supplier.Name.ToLower().Contains(term))) ? "Supplier" :
                _db.Quotes.Any(q => q.BusinessUnitId == businessUnitId && q.CommercialCaseId == c.Id &&
                    q.QuoteNo.ToLower().Contains(term)) ? "Customer Quote" :
                _db.CustomerPurchaseOrders.Any(po => po.BusinessUnitId == businessUnitId && po.CommercialCaseId == c.Id &&
                    (po.InternalNumber.ToLower().Contains(term) || po.ExternalPoNumber.ToLower().Contains(term))) ? "Client PO" :
                _db.Orders.Any(o => o.BusinessUnitId == businessUnitId && o.CommercialCaseId == c.Id &&
                    o.OrderNo.ToLower().Contains(term)) ? "Customer Order" :
                (_db.SupplierPurchaseOrders.Any(po => po.BusinessUnitId == businessUnitId && po.CommercialCaseId == c.Id &&
                    po.PurchaseOrderNumber.ToLower().Contains(term)) ||
                 _db.ProcurementHandoffs.Any(h => h.BusinessUnitId == businessUnitId && h.NexoraSerial == c.MasterReference &&
                    h.ExternalSupplierPoNumber != null && h.ExternalSupplierPoNumber.ToLower().Contains(term))) ? "Supplier PO" :
                (c.Lead.EmailIngests != null && c.Lead.EmailIngests.EmailSubject != null && c.Lead.EmailIngests.EmailSubject.ToLower().Contains(term)) ? "Email subject" :
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

        var caseId = header.Id;
        var serial = header.MasterReference;

        // ---- 1. Membership: every document that DECLARES this case -------------------------

        var rfqs = await _db.Rfqs.AsNoTracking()
            .Where(r => r.BusinessUnitId == businessUnitId && r.CommercialCaseId == caseId)
            .Select(r => new CommercialCaseDocument(
                DocumentTypes.Rfq, r.Id, r.Rfqno,
                r.Rfqstatus == null ? null : r.Rfqstatus.SetupValue,
                r.CreatedDate, null, CommercialCaseLinkStates.DeclaredOnly))
            .ToListAsync(cancellationToken);

        var quotes = await _db.Quotes.AsNoTracking()
            .Where(q => q.BusinessUnitId == businessUnitId && q.CommercialCaseId == caseId)
            .Select(q => new CommercialCaseDocument(
                DocumentTypes.Quote, q.Id, q.QuoteNo,
                q.Status == null ? null : q.Status.SetupValue,
                q.QuoteDate ?? q.CreatedDate, q.Rfqid, CommercialCaseLinkStates.DeclaredOnly))
            .ToListAsync(cancellationToken);

        var orders = await _db.Orders.AsNoTracking()
            .Where(o => o.BusinessUnitId == businessUnitId && o.CommercialCaseId == caseId)
            .Select(o => new CommercialCaseDocument(
                DocumentTypes.Order, o.Id, o.OrderNo, o.Status.SetupValue, o.OrderDate, o.QuoteId ?? o.Rfqid,
                CommercialCaseLinkStates.DeclaredOnly))
            .ToListAsync(cancellationToken);

        var shipments = await _db.Shipments.AsNoTracking()
            .Where(s => s.BusinessUnitId == businessUnitId && s.CommercialCaseId == caseId)
            .Select(s => new CommercialCaseDocument(
                DocumentTypes.Shipment, s.Id, s.ShipmentNo, s.Status.SetupValue, s.ShipmentDate, s.OrderId,
                CommercialCaseLinkStates.DeclaredOnly))
            .ToListAsync(cancellationToken);

        var sourcingCases = await _db.SourcingCases.AsNoTracking()
            .Where(s => s.BusinessUnitId == businessUnitId && s.NexoraSerial == serial)
            .Select(s => new CommercialCaseDocument(
                DocumentTypes.SourcingCase, s.Id, $"Sourcing Case {s.Id}", s.Status, s.UpdatedOn, s.RfqId,
                CommercialCaseLinkStates.DeclaredOnly))
            .ToListAsync(cancellationToken);

        var supplierRfqs = await _db.Set<SupplierSolicitation>().AsNoTracking()
            .Where(s => s.BusinessUnitId == businessUnitId && s.NexoraSerial == serial)
            .Select(s => new CommercialCaseDocument(
                DocumentTypes.SupplierRfq, s.Id, s.SupplierRfqNumber ?? $"Supplier RFQ {s.Id}",
                s.Status.ToString(), s.UpdatedOn, s.RfqId, CommercialCaseLinkStates.DeclaredOnly))
            .ToListAsync(cancellationToken);

        var supplierQuotes = await _db.SupplierQuotes.AsNoTracking()
            .Where(s => s.BusinessUnitId == businessUnitId && s.NexoraSerial == serial)
            .Select(s => new CommercialCaseDocument(
                DocumentTypes.SupplierQuote, s.Id, s.SupplierQuoteReference, s.InboxStatus, s.UpdatedOn, s.RfqId,
                CommercialCaseLinkStates.DeclaredOnly))
            .ToListAsync(cancellationToken);

        var clientPurchaseOrders = await _db.CustomerPurchaseOrders.AsNoTracking()
            .Where(po => po.BusinessUnitId == businessUnitId && po.CommercialCaseId == caseId)
            .Select(po => new CommercialCaseDocument(
                DocumentTypes.ClientPo, po.Id, po.ExternalPoNumber, po.Status, po.ReceivedOn, po.QuoteId ?? po.RfqId,
                CommercialCaseLinkStates.DeclaredOnly))
            .ToListAsync(cancellationToken);

        var supplierPurchaseOrders = await _db.SupplierPurchaseOrders.AsNoTracking()
            .Where(po => po.BusinessUnitId == businessUnitId && po.CommercialCaseId == caseId)
            .Select(po => new CommercialCaseDocument(
                DocumentTypes.SupplierPo, po.Id, po.PurchaseOrderNumber, po.Status, po.CreatedOn, po.RfqId,
                CommercialCaseLinkStates.DeclaredOnly))
            .ToListAsync(cancellationToken);

        // FR-COM-07. The customer-origin keys a supplier purchase order should carry, read as a
        // question rather than as decoration: on a CUSTOMER_DEMAND order, all three absent means the
        // order cannot say which customer it was bought for without re-joining through the RFQ —
        // which is the de-facto spine those columns exist to replace. STOCK orders are excluded
        // because absent keys on them are correct, and a report that flags correct rows is a report
        // reviewers learn to ignore.
        var supplierPurchaseOrdersWithoutCustomerOrigin = await _db.SupplierPurchaseOrders.AsNoTracking()
            .Where(po => po.BusinessUnitId == businessUnitId && po.CommercialCaseId == caseId
                && po.DemandSource != SupplierPurchaseOrderDemandSources.Stock
                && po.CustomerPurchaseOrderId == null && po.CustomerOrderId == null && po.QuoteId == null)
            .Select(po => new { po.Id, po.PurchaseOrderNumber })
            .ToListAsync(cancellationToken);

        var procurementHandoffs = await _db.ProcurementHandoffs.AsNoTracking()
            .Where(h => h.BusinessUnitId == businessUnitId && h.NexoraSerial == serial)
            .Select(h => new CommercialCaseDocument(
                DocumentTypes.ProcurementHandoff, h.Id,
                h.ExternalSupplierPoNumber ?? $"Handoff {h.Id}", h.Status, h.ModifiedOn ?? h.CreatedOn, h.RfqId,
                CommercialCaseLinkStates.DeclaredOnly))
            .ToListAsync(cancellationToken);

        // ---- 2. Reconciliation: what the legacy foreign-key chain reaches -------------------
        // Evidence only. Nothing below ever adds a document to the timeline; it exists so the
        // difference between "declares the case" and "used to be joinable to it" is reportable.

        var chainRfqs = await _db.Rfqs.AsNoTracking()
            .Where(r => r.BusinessUnitId == businessUnitId && r.LeadId == header.LeadId)
            .Select(r => new ChainRow(r.Id, r.Rfqno, r.CommercialCaseId, null, false))
            .ToListAsync(cancellationToken);
        var chainRfqIds = chainRfqs.Select(r => r.Id).ToArray();

        var chainQuotes = await _db.Quotes.AsNoTracking()
            .Where(q => q.BusinessUnitId == businessUnitId && q.Rfqid != null && chainRfqIds.Contains(q.Rfqid.Value))
            .Select(q => new ChainRow(q.Id, q.QuoteNo, q.CommercialCaseId, null, false))
            .ToListAsync(cancellationToken);
        var chainQuoteIds = chainQuotes.Select(q => q.Id).ToArray();

        var chainOrders = await _db.Orders.AsNoTracking()
            .Where(o => o.BusinessUnitId == businessUnitId &&
                (o.LeadId == header.LeadId ||
                 (o.Rfqid != null && chainRfqIds.Contains(o.Rfqid.Value)) ||
                 (o.QuoteId != null && chainQuoteIds.Contains(o.QuoteId.Value))))
            .Select(o => new ChainRow(o.Id, o.OrderNo, o.CommercialCaseId, null, false))
            .ToListAsync(cancellationToken);
        var chainOrderIds = chainOrders.Select(o => o.Id).ToArray();

        var chainShipments = await _db.Shipments.AsNoTracking()
            .Where(s => s.BusinessUnitId == businessUnitId && chainOrderIds.Contains(s.OrderId))
            .Select(s => new ChainRow(s.Id, s.ShipmentNo, s.CommercialCaseId, null, false))
            .ToListAsync(cancellationToken);

        var chainClientPurchaseOrders = await _db.CustomerPurchaseOrders.AsNoTracking()
            .Where(po => po.BusinessUnitId == businessUnitId &&
                ((po.RfqId != null && chainRfqIds.Contains(po.RfqId.Value)) ||
                 (po.QuoteId != null && chainQuoteIds.Contains(po.QuoteId.Value))))
            .Select(po => new ChainRow(po.Id, po.ExternalPoNumber, po.CommercialCaseId, null, false))
            .ToListAsync(cancellationToken);

        // A STOCK replenishment order still carries an RfqId, but it has no customer behind it and
        // therefore correctly has no case. Excluding it here keeps a correct row out of the gap
        // report; including it would train reviewers to ignore the report.
        var chainSupplierPurchaseOrders = await _db.SupplierPurchaseOrders.AsNoTracking()
            .Where(po => po.BusinessUnitId == businessUnitId && chainRfqIds.Contains(po.RfqId) &&
                po.DemandSource != SupplierPurchaseOrderDemandSources.Stock)
            .Select(po => new ChainRow(po.Id, po.PurchaseOrderNumber, po.CommercialCaseId, null, false))
            .ToListAsync(cancellationToken);

        // The serial-keyed documents have no CommercialCaseId column yet, so their declared key is
        // the master reference. A chain row whose serial is null or different is the same defect a
        // null/wrong id would be, and is reported the same way.
        var chainSourcingCases = await _db.SourcingCases.AsNoTracking()
            .Where(s => s.BusinessUnitId == businessUnitId &&
                (s.LeadId == header.LeadId || chainRfqIds.Contains(s.RfqId)))
            .Select(s => new ChainRow(s.Id, $"Sourcing Case {s.Id}", null, s.NexoraSerial, true))
            .ToListAsync(cancellationToken);

        var chainSupplierRfqs = await _db.Set<SupplierSolicitation>().AsNoTracking()
            .Where(s => s.BusinessUnitId == businessUnitId && chainRfqIds.Contains(s.RfqId))
            .Select(s => new ChainRow(s.Id, s.SupplierRfqNumber ?? $"Supplier RFQ {s.Id}", null, s.NexoraSerial, true))
            .ToListAsync(cancellationToken);

        var chainSupplierQuotes = await _db.SupplierQuotes.AsNoTracking()
            .Where(s => s.BusinessUnitId == businessUnitId && chainRfqIds.Contains(s.RfqId))
            .Select(s => new ChainRow(s.Id, s.SupplierQuoteReference, null, s.NexoraSerial, true))
            .ToListAsync(cancellationToken);

        var chainProcurementHandoffs = await _db.ProcurementHandoffs.AsNoTracking()
            .Where(h => h.BusinessUnitId == businessUnitId && chainRfqIds.Contains(h.RfqId))
            .Select(h => new ChainRow(h.Id, h.ExternalSupplierPoNumber ?? $"Handoff {h.Id}", null, h.NexoraSerial, true))
            .ToListAsync(cancellationToken);

        // ---- 3. Assemble -------------------------------------------------------------------

        var gaps = new List<CommercialCaseTraceabilityGap>();
        var documents = new List<CommercialCaseDocument>
        {
            // The case owns exactly one lead (UX_Leads_CommercialCaseID is a unique one-to-one),
            // so the header lead IS the declaration; there is no second view to reconcile it with.
            new(DocumentTypes.Lead, header.LeadId, header.Rfqno ?? serial,
                header.Status, header.LeadCreatedOn, null, CommercialCaseLinkStates.DeclaredOnly)
        };

        documents.AddRange(Reconcile(DocumentTypes.Rfq, rfqs, chainRfqs, caseId, serial, gaps));
        documents.AddRange(Reconcile(DocumentTypes.Quote, quotes, chainQuotes, caseId, serial, gaps));
        documents.AddRange(Reconcile(DocumentTypes.SourcingCase, sourcingCases, chainSourcingCases, caseId, serial, gaps));
        documents.AddRange(Reconcile(DocumentTypes.SupplierRfq, supplierRfqs, chainSupplierRfqs, caseId, serial, gaps));
        documents.AddRange(Reconcile(DocumentTypes.SupplierQuote, supplierQuotes, chainSupplierQuotes, caseId, serial, gaps));
        documents.AddRange(Reconcile(DocumentTypes.ClientPo, clientPurchaseOrders, chainClientPurchaseOrders, caseId, serial, gaps));
        documents.AddRange(Reconcile(DocumentTypes.Order, orders, chainOrders, caseId, serial, gaps));
        documents.AddRange(Reconcile(DocumentTypes.SupplierPo, supplierPurchaseOrders, chainSupplierPurchaseOrders, caseId, serial, gaps));
        documents.AddRange(Reconcile(DocumentTypes.ProcurementHandoff, procurementHandoffs, chainProcurementHandoffs, caseId, serial, gaps));
        documents.AddRange(Reconcile(DocumentTypes.Shipment, shipments, chainShipments, caseId, serial, gaps));

        foreach (var order in supplierPurchaseOrdersWithoutCustomerOrigin)
            gaps.Add(new CommercialCaseTraceabilityGap(
                DocumentTypes.SupplierPo, order.Id, order.PurchaseOrderNumber,
                CommercialCaseGapKinds.CustomerOriginMissing, caseId,
                "Raised against customer demand but names no client purchase order, sales order or "
                + "quotation, so the customer it was bought for can only be inferred by re-joining "
                + "through the RFQ. The sourcing awards on it are not linked to a customer quote "
                + "line, or the customer award had not been raised when the order was placed."));

        var history = await ReadStatusHistoryAsync(businessUnitId, caseId, cancellationToken);

        return new CommercialCaseDetail(
            header.Id,
            serial,
            header.AllocationNumber,
            header.BusinessUnitId,
            header.CreatedOn,
            header.LeadId,
            header.Rfqno,
            header.BuyersName,
            header.Clientemail,
            header.OpportunityNo,
            header.Status,
            documents.OrderBy(d => d.OccurredOn).ThenBy(d => d.DocumentType).ThenBy(d => d.DocumentId).ToArray(),
            history,
            gaps.OrderBy(g => g.DocumentType).ThenBy(g => g.DocumentId).ToArray());
    }

    private async Task<IReadOnlyList<CommercialCaseStatusEvent>> ReadStatusHistoryAsync(
        long businessUnitId, long caseId, CancellationToken cancellationToken)
    {
        var lifecycleHistory = await _db.CommercialLifecycleEvents.AsNoTracking()
            .Where(h => h.BusinessUnitId == businessUnitId && h.CommercialCaseId == caseId)
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
            .Where(h => h.BusinessUnitId == businessUnitId && h.CommercialCaseId == caseId)
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

        return nonDuplicatedLegacy.Concat(lifecycleHistory)
            .OrderBy(h => h.ChangedOn)
            .ThenBy(h => h.Id)
            .ToArray();
    }

    /// <summary>
    /// Stamps each declared document with whether the foreign-key chain agrees, and records every
    /// disagreement in <paramref name="gaps"/>.
    ///
    /// <para>Membership is never changed here. A chain row that does not declare this case is not
    /// promoted into the timeline no matter how reachable it is, and a declared document is never
    /// dropped for being unreachable.</para>
    /// </summary>
    private static IEnumerable<CommercialCaseDocument> Reconcile(
        string documentType,
        IReadOnlyList<CommercialCaseDocument> declared,
        IReadOnlyList<ChainRow> chain,
        long caseId,
        string masterReference,
        List<CommercialCaseTraceabilityGap> gaps)
    {
        var chainIds = chain.Select(row => row.Id).ToHashSet();
        var declaredIds = declared.Select(document => document.DocumentId).ToHashSet();

        foreach (var row in chain)
        {
            if (declaredIds.Contains(row.Id))
                continue;

            // Serial-keyed types have no surrogate case column yet; their declaration is the
            // master reference, so an absent serial is the "no case stated" case.
            var declaresNothing = row.SerialIsTheKey
                ? string.IsNullOrWhiteSpace(row.DeclaredSerial)
                : !row.DeclaredCaseId.HasValue;

            gaps.Add(declaresNothing
                ? new CommercialCaseTraceabilityGap(
                    documentType, row.Id, row.Reference, CommercialCaseGapKinds.Unlinked, null,
                    $"Reachable from this case through the document chain but states no commercial case, " +
                    $"so it is not part of the {masterReference} timeline. It was created outside the spine or predates it.")
                : new CommercialCaseTraceabilityGap(
                    documentType, row.Id, row.Reference, CommercialCaseGapKinds.ConflictingCase,
                    row.DeclaredCaseId,
                    $"Reachable from this case through the document chain but states " +
                    $"{(row.SerialIsTheKey ? row.DeclaredSerial : row.DeclaredCaseId?.ToString())} instead of {masterReference}."));
        }

        foreach (var document in declared)
        {
            if (chainIds.Contains(document.DocumentId))
            {
                yield return document with { LinkState = CommercialCaseLinkStates.Reconciled };
                continue;
            }

            gaps.Add(new CommercialCaseTraceabilityGap(
                documentType, document.DocumentId, document.Reference, CommercialCaseGapKinds.ChainBroken, caseId,
                $"States {masterReference} and is shown in the timeline, but the document chain no longer reaches it. " +
                "The declared case is authoritative; the broken link is the defect."));
            yield return document with { LinkState = CommercialCaseLinkStates.ChainBroken };
        }
    }

    /// <summary>
    /// One row as seen by the legacy foreign-key walk, with whatever case it declares.
    ///
    /// <para><paramref name="SerialIsTheKey"/> is set by the projection, not inferred from the
    /// values: a serial-keyed document type with a NULL serial declares nothing, and inferring the
    /// key from the data would read that as "surrogate-keyed with a null id" and mis-report it.</para>
    /// </summary>
    private sealed record ChainRow(
        long Id, string Reference, long? DeclaredCaseId, string? DeclaredSerial = null, bool SerialIsTheKey = false);

    private static class DocumentTypes
    {
        public const string Lead = "Lead";
        public const string Rfq = "RFQ";
        public const string Quote = "Quote";
        public const string Order = "Order";
        public const string Shipment = "Shipment";
        public const string SourcingCase = "SourcingCase";
        public const string SupplierRfq = "SupplierRFQ";
        public const string SupplierQuote = "SupplierQuote";
        public const string ClientPo = "ClientPO";
        public const string SupplierPo = "SupplierPO";
        public const string ProcurementHandoff = "ProcurementHandoff";
    }

    private long RequireAuthenticatedTenant(long requestedBusinessUnitId)
    {
        var authenticated = _tenantContext.BusinessUnitId;
        if (!authenticated.HasValue || authenticated.Value <= 0 || authenticated.Value != requestedBusinessUnitId)
            throw new UnauthorizedAccessException("A matching authenticated tenant context is required.");
        return authenticated.Value;
    }
}
