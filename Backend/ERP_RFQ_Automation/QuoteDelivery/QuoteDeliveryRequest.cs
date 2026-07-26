namespace ERP_RFQ_Automation.QuoteDelivery;

/// <summary>Durable, tenant-owned request to deliver an immutable quote-send intent.</summary>
public sealed class QuoteDeliveryRequest
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long QuoteId { get; set; }
    public string IdempotencyKey { get; set; } = null!;
    public string RecipientEmail { get; set; } = null!;
    public string Subject { get; set; } = null!;
    public string Body { get; set; } = null!;
    public string? FromEmail { get; set; }
    public string AttachmentFileName { get; set; } = null!;
    public DateTime RequestedOn { get; set; }
    public DateTime AvailableOn { get; set; }
    public int AttemptCount { get; set; }
    public DateTime? LastAttemptOn { get; set; }
    public string? LeaseOwner { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTime? LeaseUntil { get; set; }
    public DateTime? CompletedOn { get; set; }
    public DateTime? DeadLetteredOn { get; set; }
    public string? LastErrorCode { get; set; }
    public long Version { get; set; }
}
