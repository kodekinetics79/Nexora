namespace ERP_RFQ_Automation.Models;

public partial class Quote
{
    public long? CommercialCaseId { get; private set; }
    public string? NexoraSerial { get; private set; }
    public long? ContactId { get; private set; }
    public int LifecycleVersion { get; set; } = 1;

    public void InheritCommercialIdentity(Rfq rfq)
    {
        ArgumentNullException.ThrowIfNull(rfq);
        if (rfq.BusinessUnitId != BusinessUnitId)
            throw new InvalidOperationException("RFQ and Quote must belong to the same tenant.");
        if (!rfq.CommercialCaseId.HasValue || string.IsNullOrWhiteSpace(rfq.NexoraSerial))
            throw new InvalidOperationException("The RFQ does not have a valid Nexora Serial.");
        if (CommercialCaseId.HasValue && CommercialCaseId != rfq.CommercialCaseId)
            throw new InvalidOperationException("A Quote commercial identity cannot be replaced.");
        if (!string.IsNullOrWhiteSpace(NexoraSerial) && NexoraSerial != rfq.NexoraSerial)
            throw new InvalidOperationException("A Quote Nexora Serial cannot be replaced.");
        if (CustomerId.HasValue && CustomerId != rfq.CustomerId)
            throw new InvalidOperationException("The Quote customer must match its RFQ.");
        if (ContactId.HasValue && ContactId != rfq.ContactId)
            throw new InvalidOperationException("The Quote contact must match its RFQ.");

        CommercialCaseId = rfq.CommercialCaseId;
        NexoraSerial = rfq.NexoraSerial;
        CustomerId = rfq.CustomerId;
        ContactId = rfq.ContactId;
    }

    public void InheritCommercialIdentity(Quote predecessor)
    {
        ArgumentNullException.ThrowIfNull(predecessor);
        if (predecessor.BusinessUnitId != BusinessUnitId || predecessor.Rfqid != Rfqid)
            throw new InvalidOperationException("Quote revisions must retain their tenant and RFQ.");
        if (!predecessor.CommercialCaseId.HasValue || string.IsNullOrWhiteSpace(predecessor.NexoraSerial))
            throw new InvalidOperationException("The predecessor quote has no canonical commercial identity.");
        CommercialCaseId = predecessor.CommercialCaseId;
        NexoraSerial = predecessor.NexoraSerial;
        CustomerId = predecessor.CustomerId;
        ContactId = predecessor.ContactId;
    }
}
