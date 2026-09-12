namespace ERP_RFQ_Automation.Retention;

/// <summary>
/// Per-tenant control over how long uploaded document BYTES are kept after extraction has
/// finished with them.
///
/// <para>
/// The legal basis is symmetrical, not one-sided. UAE PDPL Art. 6/16 and KSA PDPL Art. 18
/// both require personal data to be destroyed once the purpose of processing is met, and
/// extraction IS the purpose — satisfied at
/// <see cref="DocumentIntelligence.Persistence.DocumentProcessingStatus.Completed"/>. Pulling
/// the other way, UAE Commercial Transactions Law and KSA/ZATCA e-invoicing require multi-year
/// retention of commercial books and invoices. That is why the invoice/PO/contract exclusion in
/// <see cref="EvidenceRetentionEligibility"/> is hard-coded rather than a tenant toggle: a
/// tenant may choose how long to keep intake artefacts, and may not choose to delete
/// statutory records.
/// </para>
///
/// <para>
/// The default is 90 days (a dispute and re-extraction buffer). The floor is ONE day, and it
/// is a settle guard rather than a retention opinion: extraction and assembly are asynchronous,
/// and a document ingested minutes ago may still be mid-flight. How long to keep original files
/// beyond that is the tenant's decision, not ours — the owner's rule is that Nexora does not
/// impose a data policy on a customer. What Nexora does still refuse, however the policy is
/// set, is the statutory set (invoices, purchase orders, contracts), legal holds, and anything
/// still in use; those exclusions live in <see cref="EvidenceRetentionEligibility"/> and are
/// enforced in SQL, not by the number here.
/// </para>
/// <para>
/// <see cref="IsEnabled"/> is a recorded consent to a STANDING rule. No scheduler reads it in
/// this build, and a manually confirmed purge does not require it: that path proves intent by a
/// signed preview, a written reason, the typed count and an Idempotency-Key.
/// </para>
/// </summary>
public sealed class EvidenceRetentionPolicy
{
    public const int DefaultRetentionDays = 90;
    public const int MinimumRetentionDays = 1;
    public const int MaximumRetentionDays = 3650;

    public long Id { get; set; }
    public long BusinessUnitId { get; set; }

    /// <summary>Days after ingestion before an eligible document's bytes may be purged.</summary>
    public int RetentionDays { get; set; } = DefaultRetentionDays;

    /// <summary>False until a named user opts in to a standing rule. No scheduler exists in this
    /// build, so the flag currently records consent and nothing else; an explicitly confirmed
    /// manual run never needs it, so a tenant can reclaim space once without committing to a
    /// standing policy.</summary>
    public bool IsEnabled { get; set; }

    /// <summary>Monotonic. Stamped into every tombstone so an auditor can reconstruct which
    /// rule was in force at the moment a specific document's bytes were destroyed — the
    /// policy row itself is mutable and cannot answer that question retrospectively.</summary>
    public int Version { get; set; } = 1;

    public long UpdatedByUserId { get; set; }
    public DateTime UpdatedOn { get; set; }
    public DateTime CreatedOn { get; set; }

    /// <summary>The immutable identifier written into <c>source_documents.purge_policy_code</c>
    /// and into the tombstone. Not a foreign key: the policy row can change afterwards, and the
    /// audit answer must not.</summary>
    public string PolicyCode => $"retention/v{Version}/{RetentionDays}d";

    public static EvidenceRetentionPolicy Default(long businessUnitId, DateTime now) => new()
    {
        BusinessUnitId = businessUnitId,
        RetentionDays = DefaultRetentionDays,
        IsEnabled = false,
        Version = 1,
        UpdatedOn = now,
        CreatedOn = now
    };

    public static int Clamp(int retentionDays) =>
        Math.Clamp(retentionDays, MinimumRetentionDays, MaximumRetentionDays);
}
