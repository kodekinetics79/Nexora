namespace ERP_RFQ_Automation.Ingestion.Assembly;

/// <summary>
/// Operator-facing sentences for the states a message can be held in, as named values rather
/// than string literals buried at their call sites.
///
/// <para>They live here so they can be asserted directly. The alternative — reading the worker's
/// source and matching substrings — is not a behavioural test: it passes when the text is right
/// and the behaviour is wrong, and fails when someone renames a variable. Naming the value makes
/// the promise itself testable.</para>
/// </summary>
public static class EmailInquiryHoldReasons
{
    /// <summary>Extraction succeeded but there is nowhere durable to put the result yet.</summary>
    public const string AssemblyResultStorePending = "assembly_result_store_pending";

    /// <summary>
    /// What an operator is told while a component is held for the missing assembly step.
    ///
    /// <para>Deliberately does NOT promise an automatic retry. Nothing sweeps held components —
    /// there is no recovery service in this build — and telling someone their message will be
    /// reprocessed on its own would be the same class of untruth as advising a retry that cannot
    /// succeed. It says what is true: held, nothing lost.</para>
    /// </summary>
    public const string AssemblyResultStorePendingDetail =
        "This part of the message was read successfully, but the step that combines it with the "
        + "rest of the email is not available yet, so the inquiry is not complete. It is being "
        + "held; the original captured email evidence is preserved.";

    /// <summary>A stored result carries a payload contract version this build cannot read.</summary>
    public const string ResultContractUnsupported = "result_contract_unsupported";

    /// <summary>A stored result is present but could not be deserialized.</summary>
    public const string ResultUnreadable = "result_unreadable";

    /// <summary>A component reported Completed but has no durable result row.</summary>
    public const string ResultMissing = "result_missing";

    /// <summary>An Email job the worker cannot map to exactly one owning component.</summary>
    public const string OwnershipUnresolved = "assembly_ownership_unresolved";

    /// <summary>
    /// The whole message was read successfully and asked for nothing that can be quoted.
    ///
    /// <para>This is the disposition for marketing mail, cold outreach and newsletters that
    /// carry no bulk headers — a human wrote them to a human, so no header rule can see them,
    /// and structurally they are identical to a first-time buyer's opening email. The
    /// difference only becomes visible AFTER the message has been read: a buyer asks for
    /// something, an agency does not.</para>
    ///
    /// <para><b>Why the judgement is not made at triage.</b>
    /// <c>DeterministicEmailTriage</c> stops mail only on positive, machine-verifiable evidence
    /// that it is not business mail, and absence of RFQ vocabulary is deliberately not such
    /// evidence: "Do you carry Schneider NSX250N MCCBs?" contains no quantity, no request verb
    /// and no RFQ reference, yet it is a real deal. Gating on that absence before extraction
    /// would suppress exactly that buyer. Gating on it after extraction cannot, because a
    /// message with one extracted line never reaches this reason.</para>
    ///
    /// <para><b>Why held rather than closed.</b> Zero requestable lines is not proof that
    /// nothing was requested. A public-procurement notification — BidNet Direct, DemandStar,
    /// SAM.gov — states the solicitation in a portal behind a link, so it extracts to zero
    /// lines and is still a real bid opportunity. <c>NoInquiry</c> is terminal and absorbing;
    /// this state is neither, and the message stays on the Email Intake screen with its reason
    /// showing.</para>
    /// </summary>
    public const string NoRequestableContent = "assembly_no_requestable_content";

    /// <summary>
    /// What an operator is told when the message was read and asked for nothing.
    ///
    /// <para>Says what is true and stops: read in full, nothing requestable found, no inquiry
    /// created, nothing lost. It does not call the sender a spammer — the same sentence has to
    /// be right for a marketing blast and for a solicitation whose detail is behind a login.
    /// </para>
    ///
    /// <para><b>Length is load bearing.</b> The coordinator stores these as
    /// <c>"{code}: {detail}"</c>, and the Inbound Mail screen puts that string through
    /// <c>presentableServerText</c>, which REJECTS — does not truncate — anything over
    /// <c>MAX_MESSAGE_LENGTH</c> (300 characters, <c>Frontend/src/utils/apiErrors.ts</c>). The
    /// first draft of this sentence was 359 characters, which would have rendered the held
    /// message with a blank reason: the message visible and the explanation gone, which is the
    /// half of "captured and held" that is worth nothing. Pinned by
    /// <c>EmailInquiryHoldLifecycleTests</c>.</para>
    /// </summary>
    public const string NoRequestableContentDetail =
        "This message was read in full and names no product, quantity or specification anywhere, "
        + "so no inquiry was created. Nothing is lost — the original email is retained. If it "
        + "is a real request, its detail is somewhere Nexora could not read, such as a portal "
        + "link.";

    /// <summary>
    /// The message carried content that could have held a request, and none of it survived
    /// into an inquiry.
    ///
    /// <para>The same zero-line merge as <see cref="NoRequestableContent"/>, reached for the
    /// opposite reason. Here the extractors DID see candidate lines — parsed text regions on a
    /// document, model-named items before anchor verification on a body — and none of them
    /// became a requestable line. A scanned RFQ whose OCR came back partial is the case that
    /// matters: it is a real customer request, and telling the operator it asked for nothing
    /// sends them to chase a sender who did their part.</para>
    ///
    /// <para>Held, not closed, exactly as the other case is: the content is still there to be
    /// read, and reprocessing the message can still produce the inquiry.</para>
    /// </summary>
    public const string ContentNotRecovered = "assembly_content_not_recovered";

    /// <summary>
    /// What an operator is told when the message was not read well enough to quote from.
    ///
    /// <para>It says the one thing that changes what they do next: the failure is on Nexora's
    /// side of the exchange, so the next step is to read or reprocess the message, not to reply
    /// to the sender. Length obeys the same 300-character screen gate as every sentence here,
    /// which is why the extractor's own review reasons are LOGGED rather than appended — they
    /// are unbounded, and a reason one character too long renders as no reason at all.</para>
    /// </summary>
    public const string ContentNotRecoveredDetail =
        "Nexora found content in this message but could not recover any product, quantity or "
        + "specification from it, so no inquiry was created. Nothing is lost — the original "
        + "email is retained. Read it before replying: this is not a message that asked for "
        + "nothing.";

    /// <summary>
    /// The message merged cleanly, but the persist path produced no Lead.
    ///
    /// <para>The persister returns a lead id, and a NON-POSITIVE one is not an id — it is
    /// "nothing was created". It happens for a real, ordinary reason: identity reconciliation
    /// classified the merged inquiry as a possible match against an existing Lead and raised it
    /// for a human decision instead of writing a second commercial record. That is the system
    /// working. Recording it as <c>Assembled</c> with <c>AssembledLeadId = 0</c> is not: the
    /// message reads as finished, the UI offers "open lead" for a lead that does not exist, and
    /// the match waiting for a human is invisible because nothing says the message is unresolved.
    /// </para>
    ///
    /// <para>A genuine duplicate is NOT this case and never reaches it — reconciliation returns
    /// the existing Lead's real id, and that id is what the message records.</para>
    /// </summary>
    public const string LeadNotProduced = "assembly_lead_not_produced";

    /// <summary>
    /// What an operator is told when the merge succeeded but no Lead was created.
    ///
    /// <para>Says the two things that are true: nothing was lost, and someone has to decide.
    /// It does not promise a retry — nothing sweeps this, because there is nothing to retry;
    /// the decision is the next step and a person owns it.</para>
    /// </summary>
    public const string LeadNotProducedDetail =
        "This message was read in full, but it looks like an inquiry that already exists, so no "
        + "new inquiry was created from it. Nothing has been lost — confirm whether it is the "
        + "same request or a new one.";

    /// <summary>
    /// What an operator is told when an email job reaches persistence owning no component.
    ///
    /// <para>Says the two things that are true and nothing else: no lead was invented from the
    /// fragment, and the message is intact. It deliberately does not name the job, the queue or
    /// the storage layout — this string is rendered on a screen a salesperson reads.</para>
    /// </summary>
    /// <para><b>The last sentence used to be a lie.</b> It said "reprocess the message to rebuild
    /// it as one inquiry", and <c>EmailTriageService.ReprocessAsync</c> throws "The message could
    /// not be captured durably" on exactly this shape — so the one instruction the product gave
    /// an operator was guaranteed to fail, every time, on the messages that needed it most. It
    /// now says what the system actually does: the part is sent to processing again on its own,
    /// under the governed scheduling reopen, and a person is told plainly if that stops helping.
    /// </para>
    public const string OwnershipUnresolvedDetail =
        "A part of this message was processed without being linked back to the message, so no "
        + "inquiry was created from it on its own. Nothing is lost — the email and every part of "
        + "it are retained, and that part is being sent to processing again automatically.";

    // =====================================================================================
    // The stranded-component sweep. Five codes rather than one, because "why did this part
    // never finish?" has five genuinely different answers and an operator deciding whether to
    // reprocess a message, fix a bucket, or simply read it needs to know which.
    // =====================================================================================

    /// <summary>The component was never handed to the queue: no job id was ever recorded.</summary>
    public const string StrandedWithoutJob = "stranded_no_extraction_job";

    /// <summary>The component names a job whose row no longer exists.</summary>
    public const string StrandedJobMissing = "stranded_extraction_job_missing";

    /// <summary>The job succeeded but recorded no result the assembler could read back.</summary>
    public const string StrandedResultMissing = "stranded_extraction_result_missing";

    /// <summary>The job stopped trying for a reason retrying cannot change — a content fault.
    /// Terminal and commercially significant: the message finalizes into review.</summary>
    public const string StrandedJobStopped = "stranded_extraction_job_stopped";

    /// <summary>The job stopped trying because a dependency was unavailable. The message is HELD
    /// rather than finalized, because the content is still readable once the fault is fixed.</summary>
    public const string StrandedInfrastructureFault = "stranded_infrastructure_fault";

    // =====================================================================================
    // Parts held WITHOUT a processing job. There is no queue row to exhaust its attempts and
    // no dead letter to recover, so the sweep re-drives scheduling from the stored original
    // and, when that cannot work, DECIDES the message rather than holding it again.
    //
    // The comment on AssemblyResultStorePendingDetail above used to be literally true of this
    // build — "nothing sweeps held components — there is no recovery service". It no longer is
    // for a hold with no job, which is the population that had no path out at all.
    // =====================================================================================

    /// <summary>
    /// The recorded parts of the message and the stored original no longer agree, so scheduling
    /// refuses. Terminal by construction: the disagreement is a fact about two durable records,
    /// and no number of retries changes it.
    ///
    /// <para>This is the shape a manifest CONTRACT bump leaves behind. A message captured under
    /// planner contract v2 or v3 is re-planned by a v4 build, the versions differ, every
    /// non-terminal component is held, and — because the verdict is not Compatible — the message
    /// is judged safe to acknowledge and released by the mailbox. Held with no job, no dead
    /// letter, and no copy left to re-fetch.</para>
    /// </summary>
    public const string SchedulingRefusedByManifest = "scheduling_manifest_refused";

    /// <summary>A part held with no job that could not be scheduled inside its recovery window.</summary>
    public const string SchedulingNotRecovered = "scheduling_not_recovered";

    /// <summary>A part held with no job whose stored original no longer survives.</summary>
    public const string SchedulingEvidenceLost = "scheduling_evidence_lost";

    /// <summary>A part of a message the intake gate classified as a supplier document.</summary>
    public const string SchedulingNotAnInquiry = "commercial_non_inquiry";

    /// <summary>
    /// What a person is told when a part could not be handed to processing and the message is
    /// being given to them instead.
    ///
    /// <para>Under the 300-character ceiling <c>presentableServerText</c> imposes, like every
    /// sentence in this file — the coordinator stores these as <c>"{code}: {detail}"</c> and the
    /// Inbound Mail screen REJECTS rather than truncates anything longer, which would render the
    /// message with no reason at all.</para>
    /// </summary>
    public const string SchedulingNotRecoveredDetail =
        "This part of the message could not be handed to processing, and retrying has not helped, "
        + "so the message is being kept for review rather than left waiting. Nothing is lost — "
        + "the original email and every part of it are retained.";

    /// <summary>What a person is told when the stored copy of the message is gone.</summary>
    public const string SchedulingEvidenceLostDetail =
        "This part of the message could not be processed and no stored copy of the original "
        + "remains, so it cannot be retried. The message is kept for review with everything that "
        + "was read.";

    /// <summary>What a person is told about a part of a supplier document that needed no inquiry.</summary>
    public const string SchedulingNotAnInquiryDetail =
        "This message was identified as a supplier document rather than a request to quote, so "
        + "this part was not processed into an inquiry. Nothing has been lost.";
}
