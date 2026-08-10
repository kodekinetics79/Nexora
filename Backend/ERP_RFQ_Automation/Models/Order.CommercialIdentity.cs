namespace ERP_RFQ_Automation.Models;

public partial class Order
{
    /// <summary>
    /// The commercial case this sales order belongs to.
    ///
    /// <para>Private, exactly as on <see cref="Rfq"/> and <see cref="Quote"/>. These three
    /// properties used to have public setters and no guard, so a priced customer document could be
    /// minted outside the spine entirely — <c>POST /api/Order</c> set none of them. FR-COM-07 makes
    /// the Sales Order the single source of truth linking RFQ, quotation, customer PO, supplier PO
    /// and delivery; a Sales Order that cannot name its own case links nothing.</para>
    /// </summary>
    public long? CommercialCaseId { get; private set; }

    public string? NexoraSerial { get; private set; }

    public long? ContactId { get; private set; }

    /// <summary>
    /// Takes the case from the lead this order was raised against.
    ///
    /// <para>Used when an order is raised straight from an inquiry with no intervening RFQ — the
    /// lead is the document that owns the case, so it is the only honest source.</para>
    /// </summary>
    public void InheritCommercialIdentity(Lead lead)
    {
        ArgumentNullException.ThrowIfNull(lead);
        if (lead.BusinessUnitId != BusinessUnitId)
            throw new InvalidOperationException("Lead and Order must belong to the same tenant.");
        if (lead.CommercialCaseId <= 0 || string.IsNullOrWhiteSpace(lead.CommercialCaseReference))
            throw new InvalidOperationException("The lead does not have a valid Nexora Serial.");

        Apply(lead.CommercialCaseId, lead.CommercialCaseReference, lead.CustomerId, lead.ContactId, "lead");
    }

    /// <summary>Takes the case from the RFQ this order fulfils.</summary>
    public void InheritCommercialIdentity(Rfq rfq)
    {
        ArgumentNullException.ThrowIfNull(rfq);
        if (rfq.BusinessUnitId != BusinessUnitId)
            throw new InvalidOperationException("RFQ and Order must belong to the same tenant.");
        if (!rfq.CommercialCaseId.HasValue || string.IsNullOrWhiteSpace(rfq.NexoraSerial))
            throw new InvalidOperationException("The RFQ does not have a valid Nexora Serial.");

        Apply(rfq.CommercialCaseId.Value, rfq.NexoraSerial, rfq.CustomerId, rfq.ContactId, "RFQ");
    }

    /// <summary>Takes the case from the quotation this order was awarded against.</summary>
    public void InheritCommercialIdentity(Quote quote)
    {
        ArgumentNullException.ThrowIfNull(quote);
        if (quote.BusinessUnitId != BusinessUnitId)
            throw new InvalidOperationException("Quote and Order must belong to the same tenant.");
        if (!quote.CommercialCaseId.HasValue || string.IsNullOrWhiteSpace(quote.NexoraSerial))
            throw new InvalidOperationException("The quote does not have a valid Nexora Serial.");

        Apply(quote.CommercialCaseId.Value, quote.NexoraSerial, quote.CustomerId, quote.ContactId, "quote");
    }

    /// <summary>
    /// True once this order states which case it belongs to. An order that answers false is
    /// outside the spine and must not be persisted by any creation path.
    /// </summary>
    public bool HasCommercialIdentity =>
        CommercialCaseId is > 0 && !string.IsNullOrWhiteSpace(NexoraSerial);

    private void Apply(long commercialCaseId, string nexoraSerial, long? customerId, long? contactId, string source)
    {
        if (CommercialCaseId.HasValue && CommercialCaseId != commercialCaseId)
            throw new InvalidOperationException("An Order commercial identity cannot be replaced.");
        if (!string.IsNullOrWhiteSpace(NexoraSerial) && NexoraSerial != nexoraSerial)
            throw new InvalidOperationException("An Order Nexora Serial cannot be replaced.");
        // CustomerId is non-nullable on Order, so "unset" is 0 rather than null.
        if (CustomerId != 0 && customerId.HasValue && CustomerId != customerId.Value)
            throw new InvalidOperationException($"The Order customer must match its {source}.");
        if (ContactId.HasValue && contactId.HasValue && ContactId != contactId.Value)
            throw new InvalidOperationException($"The Order contact must match its {source}.");

        CommercialCaseId = commercialCaseId;
        NexoraSerial = nexoraSerial;
        if (CustomerId == 0 && customerId is > 0)
            CustomerId = customerId.Value;
        ContactId ??= contactId;
    }
}
