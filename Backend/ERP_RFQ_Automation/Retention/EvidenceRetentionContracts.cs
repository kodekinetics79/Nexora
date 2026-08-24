namespace ERP_RFQ_Automation.Retention;

/// <summary>Tenant-visible policy shape.</summary>
public sealed record EvidenceRetentionPolicyView(
    int RetentionDays,
    bool IsEnabled,
    int MinimumRetentionDays,
    int MaximumRetentionDays,
    int Version,
    DateTime? UpdatedOn);

/// <summary>
/// Storage figures derived entirely from tenant-owned rows. Drive free space is deliberately
/// absent: on a shared volume it reports other tenants' consumption, which is a cross-tenant
/// leak. Capacity belongs to the platform-admin surface and to
/// <c>HealthChecks/StorageCapacityHealthCheck</c>.
/// </summary>
public sealed record EvidenceRetentionStorageView(
    long UsedBytes,
    long ReclaimableBytes,
    int DocumentCount,
    int PurgedCount,
    int ReclaimableDocumentCount);

public sealed record EvidenceRetentionView(
    EvidenceRetentionPolicyView Policy,
    EvidenceRetentionStorageView Storage);

public sealed record UpdateEvidenceRetentionPolicyCommand(int RetentionDays, bool IsEnabled, string Reason);

/// <summary>
/// A purge run.
///
/// <para><see cref="DryRun"/> is nullable on purpose. A plain <c>bool</c> deserializes an
/// absent field to <c>false</c>, which would make a truncated or malformed body mean
/// "delete everything eligible". Here only an explicit <c>false</c> deletes: absent, null
/// and true all simulate. The destructive reading must be the one that was asked for, never
/// the one that fell out of a default.</para>
/// </summary>
public sealed record EvidenceRetentionPurgeCommand(bool? DryRun, string Reason)
{
    public bool IsDryRun => DryRun is not false;
}

/// <summary>One document the run refused to touch, and the specific rule that refused it.
/// Surfaced rather than silently dropped: "12 documents were excluded" is only a trustworthy
/// statement if the tenant can see which twelve and why.</summary>
public sealed record EvidenceRetentionSkip(long DocumentId, string FileName, string Reason);

public sealed record EvidenceRetentionPurgeResult(
    bool DryRun,
    int Scanned,
    int Eligible,
    int Purged,
    long BytesReclaimed,
    int LegacyCopiesDeleted,
    int LegacyCopiesUnresolved,
    IReadOnlyList<EvidenceRetentionSkip> Skipped,
    string Disclosure,
    bool IdempotentReplay);

public static class EvidenceRetentionDisclosure
{
    /// <summary>
    /// The sentence the product is NOT allowed to omit. Purging bytes is not erasure:
    /// buyer names and email addresses were copied out of the file into
    /// <c>field_evidence</c>, <c>document_regions</c> and the lead itself during
    /// extraction, and they survive this operation. Telling a tenant their personal data
    /// is gone because the PDF is gone would be a false compliance claim, so the claim is
    /// made here, once, next to the thing that would otherwise imply it.
    ///
    /// <para>It used to end "raise a Data Subject Request". There is no such process anywhere in
    /// this product — no screen, no endpoint, no queue and no approver — so the one sentence
    /// written specifically to stop a false compliance claim was closing with one: it sent a
    /// tenant to a door that does not exist, and a tenant who tried would conclude the erasure had
    /// been handled. It now names only what he can actually do.</para>
    /// </summary>
    public const string NotErasure = TenantDataControlCopy.NotErasure;

    public const string Irreversible =
        "This cannot be undone. Nexora keeps no backup of these files: once purged you cannot open, "
        + "download, re-read or re-extract them, and you cannot produce the original to a customer, "
        + "auditor or court.";

    public const string Retained =
        "What is kept permanently: the document record — filename, size, SHA-256 fingerprint and "
        + "ingestion date — and everything extracted from it, including every field, its confidence, "
        + "its page and position, and the linked lead, RFQ, quote and order. Your audit trail stays "
        + "complete, and a document re-sent later can still be proven identical to the one extracted.";

    public const string Excluded =
        "Never purged: documents under legal hold, still processing or under review, with unresolved "
        + "intake, on open commercial cases, or classified as invoices, purchase orders or contracts, "
        + "which carry statutory retention.";

    public static string For(bool dryRun, int documents, long bytes) => string.Join(" ",
        dryRun
            ? $"Dry run: {documents} document(s) would be purged, freeing {bytes:N0} bytes. Nothing was deleted."
            : $"{documents} document(s) purged, freeing {bytes:N0} bytes.",
        Irreversible, Retained, Excluded, NotErasure);
}
