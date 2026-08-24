namespace ERP_RFQ_Automation.Retention;

/// <summary>
/// The selection axis a tenant's system admin can actually use: OUTCOME, not date.
///
/// <para><b>Why not a date range, and why not per-record ticks.</b> A date window does not
/// separate this deployment's real data from its junk — business unit 7's four "Nexora outbound
/// email test" messages arrived on the same day as forty genuine ones, so any cutoff that catches
/// the tests catches the work. And per-record selection across 103 message ingests and 108
/// documents is a screen nobody will ever finish; asking a business owner to tick 211 boxes is
/// asking him not to bother.</para>
///
/// <para><b>Why outcome is also the SAFE axis.</b> A record with no downstream artefact has no
/// RESTRICT foreign key pointing at it and nothing derived from it to orphan. "Produced nothing"
/// is therefore both the only bucket a human can judge at a glance and the only bucket whose
/// deletion cannot break a link — the same property, seen from two sides.</para>
/// </summary>
public static class TenantDataBuckets
{
    /// <summary>Ingested mail that never became an inquiry and never became a lead.</summary>
    public const string MailThatProducedNothing = "MAIL_PRODUCED_NOTHING";

    /// <summary>The subset of the above that intake positively identified as noise —
    /// autoreplies, bulk mail, no-reply senders, replies that added no words.</summary>
    public const string MailTriagedAsNoise = "MAIL_TRIAGED_AS_NOISE";

    /// <summary>Stored files under this tenant's own prefix that no live database row points at.
    /// No row-driven purge can ever reach these, however long it runs.</summary>
    public const string OrphanedStoredFiles = "ORPHANED_STORED_FILES";

    public static readonly IReadOnlyList<string> All =
        [MailThatProducedNothing, MailTriagedAsNoise, OrphanedStoredFiles];

    public static bool IsKnown(string? code) =>
        code is not null && All.Contains(code.Trim().ToUpperInvariant());
}

/// <summary>
/// Message states that are still IN FLIGHT, and whose stored copy is therefore still needed.
///
/// <para>This is the "open intake keeps the bytes" rule the byte purge already applies to
/// documents, applied to mail — and it is not optional. A message left at <c>Pending</c> is one
/// the poller's stranded-ingest sweeper (ING-09) will come back for, and it recovers by re-reading
/// the retained raw <c>.eml</c>. Clear one and the sweeper finds nothing, marks the row
/// <i>"Failed - raw message lost"</i>, and tells the tenant we lost his mail when in fact he asked
/// us to delete it. A loud FALSE answer is no better than a silent one.</para>
///
/// <para><c>Reprocessing</c> is the same hazard through the other door: a governed human override
/// that has been committed but not yet scheduled, which the manual reprocess path replays from
/// those same bytes.</para>
/// </summary>
public static class TenantDataInFlightMail
{
    public const string Pending = "Pending";
    public const string Reprocessing = "Reprocessing";

    public static readonly IReadOnlyList<string> ParseStatuses = [Pending, Reprocessing];
}

/// <summary>
/// One row of the selection UI. Three of these is the entire selection surface.
///
/// <para><see cref="Title"/> and <see cref="Detail"/> are finished product copy written on the
/// server, not codes for a client to decorate. The tenant's admin is a business owner, not an
/// operator: he must never have to know what an "assembly" is to understand his own mail.
/// <see cref="Code"/> exists only so the follow-up request can name the bucket, and is never
/// rendered.</para>
/// </summary>
public sealed record TenantDataBucketView(
    string Code,
    string Title,
    string Detail,
    int Count,
    long Bytes,
    bool CanClear,
    string? BlockedReason);

/// <summary>
/// One line of the standing "what we will never touch" panel.
///
/// <para>The skip reasons were always computed — they just only ever appeared as a footnote
/// AFTER a purge, which answers the question too late to reassure anybody. Stated up front and
/// in plain words, this is the panel that makes the delete button safe to press.</para>
/// </summary>
public sealed record TenantDataKeptView(string Title, string Detail, int Count);

/// <summary>Everything the Storage &amp; Retention screen needs to draw itself.</summary>
public sealed record TenantDataControlView(
    IReadOnlyList<TenantDataBucketView> Buckets,
    IReadOnlyList<TenantDataKeptView> Kept,
    string KeptSummary);

/// <summary>
/// A request to clear one or more buckets.
///
/// <para><see cref="DryRun"/> is nullable for the same reason it is on the byte purge: a plain
/// <c>bool</c> deserializes an absent field to <c>false</c>, which would make a truncated body
/// mean "delete everything". Only an explicit <c>false</c> deletes.</para>
///
/// <para><see cref="Confirmation"/> is verified on the SERVER. A confirmation phrase checked only
/// in the browser is not a gate — it is a decoration on a request anyone can send directly.</para>
/// </summary>
public sealed record TenantDataCleanupCommand(
    IReadOnlyList<string>? Buckets,
    bool? DryRun,
    string Reason,
    string? Confirmation)
{
    public bool IsDryRun => DryRun is not false;
}

/// <summary>
/// Something the sweep found and deliberately did NOT delete, with the reason.
///
/// <para>Reported rather than dropped. A sweep that quietly skips what it cannot prove safe is
/// indistinguishable from a sweep that had nothing to do, and the tenant is owed the difference:
/// those bytes are still on disk and still being paid for.</para>
/// </summary>
public sealed record TenantDataRefusal(string What, string Why, long Bytes);

/// <summary>The receipt. Numbers first — what went, what stays, how much space came back.</summary>
public sealed record TenantDataCleanupResult(
    bool DryRun,
    int MessagesCleared,
    int FilesDeleted,
    long BytesReclaimed,
    IReadOnlyList<TenantDataRefusal> Refused,
    string Summary,
    string Disclosure,
    bool IdempotentReplay);

public static class TenantDataControlCopy
{
    /// <summary>
    /// Typed by the admin, verified by the server. Same word the existing confirmation dialog
    /// already asks for, so there is one phrase to remember rather than two.
    /// </summary>
    public const string ConfirmationPhrase = "DELETE";

    public const string MailProducedNothingTitle = "Mail that never became an inquiry";

    public const string MailProducedNothingDetail =
        "Messages we received, read and filed — and nothing came of them. No inquiry was raised "
        + "and no lead was created. Clearing these deletes the stored copy of the message. The "
        + "record that it arrived — who sent it, when, what the subject was — is kept forever.";

    public const string MailNoiseTitle = "Mail we identified as not being business";

    public const string MailNoiseDetail =
        "Out-of-office replies, mailing lists, no-reply senders, and replies that added no new "
        + "words. These are part of the group above, not extra to it.";

    public const string OrphanedFilesTitle = "Stored files nothing points to any more";

    public const string OrphanedFilesDetail =
        "Leftover copies in your storage that no document, message or record refers to. Nothing "
        + "in Nexora can open them and nothing links to them — they only take up space. Anything "
        + "we cannot positively prove is unused is left alone and listed for you.";

    public const string NothingToClear = "There is nothing here to clear.";

    public const string StorageCannotList =
        "Your storage provider cannot list what it holds, so we cannot prove which files are "
        + "unused. Nothing will be deleted from storage.";

    /// <summary>
    /// Replaces the old disclosure's pointer at a "Data Subject Request" process. That process
    /// does not exist anywhere in this product — no screen, no endpoint, no queue, no approver —
    /// so telling a tenant to raise one was directing him at a door that is not there. This says
    /// only what is true, and what he can actually do about it.
    /// </summary>
    public const string NotErasure =
        "This does not erase personal data. Buyer names and email addresses read out of these "
        + "documents were copied into your leads and extraction records while they were processed, "
        + "and they stay there. To remove a specific person's details, edit or delete the lead "
        + "that holds them.";

    public static string Summarise(bool dryRun, int messages, int files, long bytes)
    {
        var what = (messages, files) switch
        {
            (0, 0) => "Nothing was selected that we could clear",
            (> 0, 0) => $"{messages:N0} stored message(s)",
            (0, > 0) => $"{files:N0} leftover file(s)",
            _ => $"{messages:N0} stored message(s) and {files:N0} leftover file(s)"
        };
        return dryRun
            ? $"Preview only — nothing has been deleted. {what} would be removed, freeing {bytes:N0} bytes."
            : $"{what} removed, freeing {bytes:N0} bytes.";
    }
}
