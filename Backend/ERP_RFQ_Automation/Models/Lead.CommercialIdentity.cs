namespace ERP_RFQ_Automation.Models;

public partial class Lead
{
    public long? CustomerId { get; private set; }
    public long? ContactId { get; private set; }
    public string CustomerMatchStatus { get; private set; } = "UNRESOLVED";

    public void ResolveCommercialIdentity(long customerId, long? contactId, string matchStatus)
    {
        if (customerId <= 0) throw new ArgumentOutOfRangeException(nameof(customerId));
        if (string.IsNullOrWhiteSpace(matchStatus)) throw new ArgumentException("Match status is required.", nameof(matchStatus));

        CustomerId = customerId;
        ContactId = contactId;
        CustomerMatchStatus = matchStatus.Trim().ToUpperInvariant();
    }
}
