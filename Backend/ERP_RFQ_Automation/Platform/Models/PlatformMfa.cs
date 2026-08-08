namespace ERP_RFQ_Automation.Platform.Models;

public sealed class PlatformMfaCredential
{
    public long PlatformUserId { get; set; }
    public string TotpSecret { get; set; } = null!;
    public DateTime? EnabledAtUtc { get; set; }
    public long? LastAcceptedTotpStep { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public PlatformUser PlatformUser { get; set; } = null!;
}

public sealed class PlatformMfaRecoveryCode
{
    public long Id { get; set; }
    public long PlatformUserId { get; set; }
    public string CodeHash { get; set; } = null!;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UsedAtUtc { get; set; }
    public PlatformUser PlatformUser { get; set; } = null!;
}

public sealed class PlatformMfaChallenge
{
    public Guid Id { get; set; }
    public long PlatformUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public int FailedAttempts { get; set; }
    public DateTime? ConsumedAtUtc { get; set; }
    public PlatformUser PlatformUser { get; set; } = null!;
}
