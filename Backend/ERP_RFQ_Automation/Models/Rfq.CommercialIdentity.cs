namespace ERP_RFQ_Automation.Models;

public partial class Rfq
{
    public long? CommercialCaseId { get; private set; }
    public string? NexoraSerial { get; private set; }
    public long? ContactId { get; private set; }

    public void InheritCommercialIdentity(Lead lead)
    {
        ArgumentNullException.ThrowIfNull(lead);
        if (lead.BusinessUnitId != BusinessUnitId)
            throw new InvalidOperationException("Lead and RFQ must belong to the same tenant.");
        if (lead.CommercialCaseId <= 0 || string.IsNullOrWhiteSpace(lead.CommercialCaseReference))
            throw new InvalidOperationException("The lead does not have a valid Nexora Serial.");
        if (CommercialCaseId.HasValue && CommercialCaseId != lead.CommercialCaseId)
            throw new InvalidOperationException("An RFQ commercial identity cannot be replaced.");
        if (!string.IsNullOrWhiteSpace(NexoraSerial) && NexoraSerial != lead.CommercialCaseReference)
            throw new InvalidOperationException("An RFQ Nexora Serial cannot be replaced.");
        if (CustomerId.HasValue && CustomerId != lead.CustomerId)
            throw new InvalidOperationException("The RFQ customer must match its lead.");
        if (ContactId.HasValue && ContactId != lead.ContactId)
            throw new InvalidOperationException("The RFQ contact must match its lead.");

        CommercialCaseId = lead.CommercialCaseId;
        NexoraSerial = lead.CommercialCaseReference;
        CustomerId = lead.CustomerId;
        ContactId = lead.ContactId;
    }
}
