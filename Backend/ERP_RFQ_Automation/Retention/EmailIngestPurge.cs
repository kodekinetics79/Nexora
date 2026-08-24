namespace ERP_RFQ_Automation.Models;

/// <summary>
/// The tombstone shape, extended from <c>source_documents</c> to <c>EmailIngests</c>.
///
/// <para><b>Why not delete the row.</b> <c>source_documents</c> already answers "we destroyed
/// this file" in a way an auditor can read: the row survives with its hash, filename, size and
/// ingestion date frozen by trigger, DELETE is refused outright, and only the bytes go.
/// <c>EmailIngests</c> had no equivalent, and a delete-the-row design cannot work on real data
/// anyway — <c>EmailInquiryAssemblies.EmailIngestId</c> is a RESTRICT foreign key, so the row is
/// undeletable the moment a message produced an assembly, and a "cleanup" that only succeeds on
/// the messages nobody cares about is not a cleanup.</para>
///
/// <para>So the same shape is applied rather than a second one invented: null the pointer to the
/// stored message, delete the stored <c>.eml</c>, stamp who did it, when, and under what reason,
/// and keep the row. The message-id, sender, subject, arrival time and triage verdict stay
/// readable forever, so "why is there no record of the mail you deleted?" has an answer.</para>
///
/// <para>Column names deliberately mirror the source-document ones — <c>bytes_purged_on</c>,
/// <c>purged_by_user_id</c>, <c>purge_reason</c> — so one vocabulary covers both tables and an
/// auditor reading either does not have to learn a second dialect.</para>
/// </summary>
public partial class EmailIngest
{
    /// <summary>When the stored message bytes were destroyed. Null while the message is intact.
    /// This — not the absence of <see cref="RawEmailPath"/> — is what distinguishes "deleted on
    /// purpose" from "we never stored it", which are different answers and must not collapse.</summary>
    public DateTime? BytesPurgedOn { get; set; }

    /// <summary>The named user who asked for it. Irreversible deletion always has an author.</summary>
    public long? PurgedByUserId { get; set; }

    /// <summary>The tenant's written reason, stored on the row as well as in the audit event so
    /// the row alone can answer for itself.</summary>
    public string? PurgeReason { get; set; }

    /// <summary>True once the stored message has been destroyed. Readers gate on this and answer
    /// with the tombstone instead of a bare "not found".</summary>
    public bool RawMessageAvailable => BytesPurgedOn is null;
}
