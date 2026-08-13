using System;
using System.Collections.Generic;
using ERP_RFQ_Automation.Models;

namespace ERP_RFQ_Automation.Ingestion.Assembly;

/// <summary>
/// The lifecycle of ONE received email message, as a whole.
///
/// <para>It exists because the pipeline had no such concept. A message's body and each of its
/// attachments became independent extraction jobs sharing nothing but a batch id and a
/// <c>LogicalGroupKey</c> that reconciliation reads as a duplicate-detection SIGNAL — so
/// <see cref="ERP_RFQ_Automation.Extraction.ExtractionWorker"/> reconciled a Lead per JOB, and
/// one email carrying a covering note and three priced attachments produced up to four
/// unrelated Leads. Nothing waited for siblings and nothing counted how many were expected,
/// so "the BoQ attachment failed" and "the customer sent no BoQ" were indistinguishable
/// downstream.</para>
///
/// <para>The states below are deliberately NOT a copy of <c>EmailIngest.ParseStatus</c>, which
/// is a 50-character free-text lifecycle string. This is the commercial gate: no Lead may be
/// finalized for a message until every expected component reaches a terminal state.</para>
/// </summary>
public enum EmailInquiryAssemblyStatus
{
    /// <summary>The message and its raw evidence are durably recorded. Nothing has been
    /// inspected yet. Reached before any classification, so a message can never be lost
    /// between arrival and triage.</summary>
    Captured = 0,

    /// <summary>Components are with the malware scanner and the structural inspector.</summary>
    Inspecting = 1,

    /// <summary>Components passed inspection and are with extraction.</summary>
    Extracting = 2,

    /// <summary>
    /// Every expected component reached a terminal state AND at least one of them durably
    /// captured commercial content.
    ///
    /// <para>The second half of that sentence is load-bearing and was missing. An earlier
    /// draft reached this state whenever the expected count was zero, which made "we captured
    /// nothing" indistinguishable from "we captured everything" — the empty message would have
    /// gone on to produce an empty Lead. A message becomes commercially assemblable because
    /// content was captured, never because nothing was outstanding.</para>
    /// </summary>
    ReadyForAssembly = 3,

    /// <summary>Merged. One canonical Lead exists per genuine commercial inquiry in this
    /// message — not one per file.</summary>
    Assembled = 4,

    /// <summary>Assembled, but a human must look: ambiguous grouping, low extraction
    /// confidence, or a partial loss the reviewer has to weigh. Terminal for the pipeline,
    /// open for the operator.</summary>
    NeedsReview = 5,

    /// <summary>An infrastructure dependency was unavailable — evidence storage, the malware
    /// scanner, the extraction queue. NOT the sender's fault and NOT a verdict about the
    /// message. The raw bytes are retained and the assembly is replayable; recovery resumes
    /// THIS assembly rather than creating a second one.</summary>
    FailedRecoverable = 6,

    /// <summary>A component was refused on security grounds and the message cannot proceed.
    /// Terminal. The raw evidence is retained for the security record.</summary>
    RejectedSecurity = 7,

    /// <summary>
    /// The message carried no commercial content to capture at all: no fresh body text and no
    /// parts. Terminal, and deliberately NOT the same state as a message whose parts were all
    /// unsupported — that one is <see cref="NeedsReview"/>, because a human may recognise an
    /// inquiry the pipeline could not read.
    ///
    /// <para>This is the triage outcome expressed on the aggregate. It exists so that
    /// <see cref="ReadyForAssembly"/> can mean what it says; without it, "nothing to do" and
    /// "everything is done" were the same state and an empty Lead was one merge away.</para>
    /// </summary>
    NoInquiry = 8
}

/// <summary>What a component of the message physically is.</summary>
public enum EmailInquiryComponentKind
{
    /// <summary>The sender's own fresh body text, after quote stripping.</summary>
    Body = 0,

    /// <summary>A MIME attachment.</summary>
    Attachment = 1,

    /// <summary>An embedded <c>message/rfc822</c> part, serialized to <c>.eml</c> and treated
    /// as a first-class component within the declared nesting limits.</summary>
    EmbeddedMessage = 2
}

/// <summary>
/// Per-component state. Every value except <see cref="Pending"/>, <see cref="Inspecting"/>
/// and <see cref="Extracting"/> is TERMINAL — the assembly advances only when every expected
/// component is terminal, so this enum is the thing the barrier counts.
/// </summary>
public enum EmailInquiryComponentStatus
{
    Pending = 0,
    Inspecting = 1,
    Extracting = 2,

    /// <summary>Terminal. Extraction produced a result that is now part of the message.</summary>
    Completed = 3,

    /// <summary>
    /// Terminal, and COMMERCIALLY SIGNIFICANT. A part the sender attached that this system
    /// could not process — unsupported type, empty, oversized, unnamed, unreadable, beyond a
    /// declared limit.
    ///
    /// <para>One of these anywhere sends the message to <see cref="EmailInquiryAssemblyStatus.NeedsReview"/>,
    /// even when the body extracted perfectly. "Please see the attached quotation" plus an
    /// attachment we could not read is not a body-only inquiry — it is an inquiry whose
    /// commercial content we do not have, and a clean Lead built from the covering note alone
    /// is a quotation priced against a document nobody read.</para>
    /// </summary>
    Skipped = 4,

    /// <summary>Terminal. The scanner refused it. The whole assembly goes to
    /// <see cref="EmailInquiryAssemblyStatus.RejectedSecurity"/>.</summary>
    RefusedSecurity = 5,

    /// <summary>NOT terminal for the message, and that is the point. An infrastructure fault —
    /// storage down, scanner unreachable — leaves the component here, which HOLDS the assembly
    /// in <see cref="EmailInquiryAssemblyStatus.FailedRecoverable"/> instead of letting the
    /// remaining components finalize a misleading body-only Lead.</summary>
    FailedRecoverable = 6,

    /// <summary>
    /// Terminal and commercially INSIGNIFICANT: a deterministically classified non-commercial
    /// inline asset — a signature logo, a tracking pixel, a social icon.
    ///
    /// <para>The ONLY status that lets a message stay clean despite a part not being read, and
    /// it is deliberately hard to reach — see <c>EmailInquiryManifestPlanner.IsIgnorableInlineAsset</c>.
    /// Every judgement call in that classifier errs toward <see cref="Skipped"/>, because
    /// wrongly ignoring a part produces a Lead priced against content nobody saw, while wrongly
    /// reviewing one costs a few seconds of a human's attention.</para>
    /// </summary>
    Ignored = 7,

    /// <summary>
    /// Terminal. A <c>message/rfc822</c> container whose CHILDREN carry the commercial content.
    ///
    /// <para>It is represented so the forward is visible, its raw identity is recorded, and the
    /// barrier counts it — but it produces no extraction job of its own, because its children are
    /// planned separately and extracting both would duplicate every line the forward contains.
    /// Distinct from <see cref="Ignored"/>: nothing here was judged unimportant, the content is
    /// simply accounted for one level down.</para>
    /// </summary>
    StructuralOnly = 8
}

/// <summary>
/// The durable message-level aggregate: exactly one row per <see cref="EmailIngest"/>.
///
/// <para>Tenancy is carried explicitly rather than reached through
/// <c>EmailIngest.EmailConfiguration.BusinessUnitId</c>. The join would be a correctness
/// dependency on a navigation the RLS policy cannot see, and the policy is what actually
/// isolates tenants here — see the migration.</para>
/// </summary>
public class EmailInquiryAssembly
{
    public long Id { get; set; }

    /// <summary>Tenant/business unit. The RLS predicate matches on this column.</summary>
    public long BusinessUnitId { get; set; }

    /// <summary>One-to-one with the durable ingest record. Unique.</summary>
    public long EmailIngestId { get; set; }

    /// <summary>The mailbox configuration this message arrived through.</summary>
    public long EmailConfigurationId { get; set; }

    /// <summary>
    /// The stable RFC 5322 Message-Id, or the deterministic content digest when the header is
    /// absent or unusable. Same value as <see cref="EmailIngest.MessageId"/> and produced by
    /// the same resolver, so a replay lands on the same assembly instead of forking one.
    /// </summary>
    public string MessageKey { get; set; } = null!;

    /// <summary>
    /// Storage URI of the raw <c>.eml</c> in <c>IEvidenceObjectStorage</c>. THE authoritative
    /// copy. A local path is never authoritative in production — the previous code wrote the
    /// raw bytes only to a container-local path, so the evidence for every ingested message
    /// lived exactly one disk failure away from gone.
    /// </summary>
    public string? RawEvidenceUri { get; set; }

    /// <summary>SHA-256 of the raw <c>.eml</c>, for read-back verification.</summary>
    public string? RawEvidenceSha256 { get; set; }

    /// <summary>Object version of the raw evidence, where the provider supplies one.</summary>
    public string? RawEvidenceVersionId { get; set; }

    public string? SenderAddress { get; set; }

    /// <summary>JSON array of recipient addresses (To + Cc), as received.</summary>
    public string? RecipientsJson { get; set; }

    public string? Subject { get; set; }

    /// <summary>When the message was received, from the message itself — not when we polled.</summary>
    public DateTimeOffset? ReceivedAtUtc { get; set; }

    /// <summary>
    /// How many components this message is expected to produce, decided ONCE at capture from
    /// the parsed MIME tree. The barrier compares against it, so a component that never
    /// reports is a visible hole rather than an early finalize.
    /// </summary>

    /// <summary>
    /// <see cref="EmailInquiryManifestPlanner.ContractVersion"/> in force when this message was
    /// planned.
    ///
    /// <para>Persisted, not inferred. Recovery re-plans the stored bytes and compares against
    /// THIS value, so a planner whose rules changed while a message was mid-flight is detected
    /// instead of silently reinterpreting evidence. Passing the planner's current constant here
    /// would make the comparison tautological, and passing
    /// <see cref="ConcurrencyVersion"/> — an easy mistake when both were called "Version" —
    /// would report a mismatch on essentially every recovery while swallowing the real
    /// component differences behind the version short-circuit.</para>
    /// </summary>
    public int ManifestContractVersion { get; set; }

    public int ExpectedComponentCount { get; set; }

    /// <summary>How many expected components have reached a terminal state.</summary>
    public int CompletedComponentCount { get; set; }

    public EmailInquiryAssemblyStatus Status { get; set; } = EmailInquiryAssemblyStatus.Captured;

    /// <summary>Operator-readable reason for the current status. Must never carry a bucket
    /// name, endpoint, credential or provider exception type — those belong in the log.</summary>
    public string? StatusReason { get; set; }

    /// <summary>JSON array of parts that were skipped or refused, with reasons. The durable,
    /// user-visible record of partial loss.</summary>
    public string? SkippedPartsJson { get; set; }

    /// <summary>
    /// Optimistic concurrency token. Named <c>ConcurrencyVersion</c> rather than <c>Version</c>
    /// because a plain "Version" next to <see cref="ManifestContractVersion"/> invites passing
    /// the wrong one into manifest verification, which would fail every recovery for the wrong
    /// reason and hide the real differences.
    ///
    /// <para>It is incremented explicitly in <c>ErpRfqAutomationContext.SaveChanges</c>: Npgsql
    /// does NOT auto-generate <c>int</c> tokens, so a token that is only declared and never
    /// incremented stays 0 forever and every concurrent update matches — the protection reads
    /// as present and does nothing.</para>
    /// </summary>
    public int ConcurrencyVersion { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public virtual EmailIngest EmailIngest { get; set; } = null!;

    public virtual ICollection<EmailInquiryComponent> Components { get; set; }
        = new List<EmailInquiryComponent>();
}

/// <summary>
/// One body, attachment or embedded message within an <see cref="EmailInquiryAssembly"/>.
/// Field-level provenance lives here: which physical part of the message a downstream fact
/// came from survives the merge, so a reviewer can always ask "where did this line come from?"
/// and get the covering note or the BoQ by name.
/// </summary>
public class EmailInquiryComponent
{
    public long Id { get; set; }

    public long BusinessUnitId { get; set; }

    public long AssemblyId { get; set; }

    /// <summary>
    /// THE stable idempotency identity of this component, deterministic from the message and
    /// its position in the MIME tree: <c>email:{messageKey}:body</c>,
    /// <c>email:{messageKey}:attachment:{n}</c>, <c>email:{messageKey}:embedded:{n}:{m}</c>.
    ///
    /// <para>It is byte-identical to the <c>SourceOccurrenceId</c> handed to the extraction
    /// queue, which is what lets a returning job find its component without a second lookup
    /// table, and what makes a replay after a crash land on the same row instead of appending
    /// a parallel one. A restart re-walks the same MIME tree and computes the same keys, so
    /// identity survives process death — an auto-increment id would not have.</para>
    /// </summary>
    public string ComponentKey { get; set; } = null!;

    public EmailInquiryComponentKind Kind { get; set; }

    /// <summary>
    /// Position within the message, 0 for the body. Together with
    /// <see cref="AssemblyId"/> this is unique, which is what makes a replay idempotent: the
    /// second pass updates the same rows rather than appending a parallel set.
    /// </summary>
    public int Ordinal { get; set; }

    public string? FileName { get; set; }

    public string? MimeType { get; set; }

    public long? ByteSize { get; set; }

    /// <summary>SHA-256 of the component bytes, content-addressing it to the evidence store.</summary>
    public string? ContentHash { get; set; }

    /// <summary>Storage URI of this component's evidence object, where one was written.</summary>
    public string? EvidenceUri { get; set; }

    /// <summary>The extraction job this component became, once queued.</summary>
    public long? ExtractionJobId { get; set; }

    /// <summary>The occurrence row extraction created, once processed.</summary>
    public long? SourceDocumentOccurrenceId { get; set; }

    public EmailInquiryComponentStatus Status { get; set; } = EmailInquiryComponentStatus.Pending;

    /// <summary>Stable snake_case code for why this component is skipped, refused or held.
    /// Machine-readable so a hold can be classified and retried without parsing prose.</summary>
    public string? ReasonCode { get; set; }

    /// <summary>Operator-safe detail. Same rule as
    /// <see cref="EmailInquiryAssembly.StatusReason"/> — no infrastructure identifiers.</summary>
    public string? ReasonDetail { get; set; }

    /// <summary>
    /// Nesting depth for embedded messages. 0 is a part of the received message itself.
    /// Compared against the configured limit so a mail bomb of nested <c>message/rfc822</c>
    /// parts terminates at a declared boundary instead of by running out of stack.
    /// </summary>
    public int NestingDepth { get; set; }

    /// <inheritdoc cref="EmailInquiryAssembly.ConcurrencyVersion"/>
    public int ConcurrencyVersion { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public virtual EmailInquiryAssembly Assembly { get; set; } = null!;

    /// <summary>True when this component can no longer change on its own.</summary>
    public bool IsTerminal => Status is EmailInquiryComponentStatus.Completed
        or EmailInquiryComponentStatus.Skipped
        or EmailInquiryComponentStatus.RefusedSecurity
        // Ignored and StructuralOnly are terminal at capture and were previously omitted here
        // while CompletedCount already counted them. The disagreement let a replayed report
        // overwrite an Ignored logo with Skipped and drag a clean message into review.
        or EmailInquiryComponentStatus.Ignored
        or EmailInquiryComponentStatus.StructuralOnly;

    /// <summary>
    /// True when the component is holding the message open for an infrastructure reason.
    /// Deliberately separate from <see cref="IsTerminal"/>: a recoverable hold is NOT terminal
    /// (the message must be replayed) but it also is NOT progress, so the barrier must treat
    /// it as neither.
    /// </summary>
    public bool IsRecoverableHold => Status == EmailInquiryComponentStatus.FailedRecoverable;
}
