using ERP_RFQ_Automation.Ingestion.Assembly;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// THE GOVERNING INVARIANT, asserted directly rather than inferred from a happy path:
/// <b>no inbound message may come to rest in a non-terminal state with nothing that will move
/// it.</b>
///
/// <para>Every stranding defect in this pipeline has been an instance of that one rule being
/// broken somewhere different — a verdict the transition table refused, a component state no
/// sweep queried, a ledger row nothing rewrote. Testing the instances one at a time has caught
/// each of them exactly once, after it reached production. These tests are about the RULE, and
/// they are written so that a new state added to either enum fails them until somebody says what
/// moves a message out of it.</para>
///
/// <para>Pure: no database, no queue, no mailbox. Everything here is a property of the state
/// machine and of the sweep's own declared predicates.</para>
/// </summary>
public class EmailInquiryStrandingInvariantTests
{
    /// <summary>
    /// Component states in which a message can be left by CAPTURE and SCHEDULING alone — before
    /// any extraction job exists, and therefore before any worker can report anything.
    ///
    /// <para><see cref="EmailInquiryComponentStatus.Completed"/> is deliberately absent: reaching
    /// it requires an extraction result, which requires a job, which moves the assembly out of
    /// Captured before the result can arrive.</para>
    /// </summary>
    private static readonly EmailInquiryComponentStatus[] ReachableAtCapture =
    [
        EmailInquiryComponentStatus.Pending,
        EmailInquiryComponentStatus.Skipped,
        EmailInquiryComponentStatus.Ignored,
        EmailInquiryComponentStatus.StructuralOnly,
        EmailInquiryComponentStatus.RefusedSecurity,
        EmailInquiryComponentStatus.FailedRecoverable
    ];

    private static readonly EmailInquiryComponentStatus[] AllComponentStatuses =
        Enum.GetValues<EmailInquiryComponentStatus>();

    /// <summary>
    /// The component states something in production actually WRITES.
    ///
    /// <para><see cref="EmailInquiryComponentStatus.Inspecting"/> is the one omission and it is
    /// evidence-based rather than convenient: no production code path assigns it. Inspection runs
    /// synchronously inside <c>DocumentIngestionService.IngestAsync</c> while a component is being
    /// scheduled, so a part goes Pending to Extracting with no window in between, and the state
    /// survives only as a defensive read in the state machine and the closure rule. It is exactly
    /// the position <see cref="EmailInquiryComponentStatus.RefusedSecurity"/> was in until the
    /// malware path started writing it.</para>
    /// </summary>
    private static readonly EmailInquiryComponentStatus[] WrittenByProduction =
        AllComponentStatuses.Where(s => s != EmailInquiryComponentStatus.Inspecting).ToArray();

    // =====================================================================================
    // 1. NO VERDICT MAY BE DISCARDED
    // =====================================================================================

    /// <summary>
    /// Every verdict the barrier can reach while a message is still <c>Captured</c> must be a
    /// legal transition FROM Captured.
    ///
    /// <para>This is the exact shape of the defect that swallowed a whole class of message. A
    /// reply with a quoted-only body and one unsupported attachment has every component terminal
    /// the moment it is captured: nothing is scheduled, nothing fails, and the barrier's honest
    /// answer is NeedsReview. NeedsReview was missing from the Captured row, so the coordinator
    /// logged "not a legal transition", left the status alone, and the message sat at Captured
    /// with no component in flight — acknowledged to the mailbox, suppressed by the ledger on
    /// every later poll, and invisible to a sweep that asks for ReadyForAssembly assemblies or
    /// non-terminal components.</para>
    ///
    /// <para>Exhaustive over every combination of up to three at-capture component states, so it
    /// cannot be satisfied by the one arrangement someone happened to think of.</para>
    /// </summary>
    [Fact]
    public void No_verdict_reachable_at_capture_is_refused_by_the_transition_table()
    {
        var refused = new List<string>();

        foreach (var combination in CombinationsUpTo(ReachableAtCapture, maxSize: 3))
        {
            var evaluation = EmailInquiryAssemblyStateMachine.Evaluate(
                combination.Count, combination);

            if (!EmailInquiryAssemblyStateMachine.CanTransition(
                    EmailInquiryAssemblyStatus.Captured, evaluation.Status))
            {
                refused.Add($"[{string.Join(", ", combination)}] evaluates to {evaluation.Status}");
            }
        }

        Assert.True(refused.Count == 0,
            "A message at Captured evaluated to a status the transition table refuses, so the "
            + "verdict is discarded and the message stops moving:\n  " + string.Join("\n  ", refused));
    }

    /// <summary>
    /// The same rule for the state a message spends most of its life in. A verdict that cannot be
    /// applied is a message that stops, wherever it happens.
    ///
    /// <para>The alphabet is the component states production actually WRITES.
    /// <see cref="EmailInquiryComponentStatus.Inspecting"/> is excluded because nothing writes it:
    /// inspection runs synchronously inside scheduling, so a component is Pending and then
    /// Extracting, and the state survives only as a defensive read. Including it would fail this
    /// test on a configuration that cannot occur — and if it ever gains a writer,
    /// <see cref="Every_component_state_is_terminal_or_has_a_mover"/> still holds the line, because
    /// the sweep claims it either way.</para>
    /// </summary>
    [Theory]
    [InlineData(EmailInquiryAssemblyStatus.Extracting)]
    public void No_verdict_reachable_mid_pipeline_is_refused_by_the_transition_table(
        EmailInquiryAssemblyStatus from)
    {
        var refused = new List<string>();

        foreach (var combination in CombinationsUpTo(WrittenByProduction, maxSize: 2))
        {
            var evaluation = EmailInquiryAssemblyStateMachine.Evaluate(
                combination.Count, combination);

            if (!EmailInquiryAssemblyStateMachine.CanTransition(from, evaluation.Status))
                refused.Add($"[{string.Join(", ", combination)}] evaluates to {evaluation.Status}");
        }

        Assert.True(refused.Count == 0,
            $"A message at {from} evaluated to a status the transition table refuses:\n  "
            + string.Join("\n  ", refused));
    }

    /// <summary>
    /// A HELD message is the one place the table deliberately refuses a verdict —
    /// <c>FailedRecoverable -> ReadyForAssembly</c> — and that refusal is safe only because a held
    /// message cannot reach an all-captured configuration WITHOUT first leaving FailedRecoverable.
    ///
    /// <para>Both ways out of a hold set the assembly to Extracting in the same transaction that
    /// reopens the component: <c>EmailInquiryAssemblyCoordinator.RecordComponentQueuedAsync</c> on
    /// the automatic scheduling-recovery path, and
    /// <c>ExtractionDeadLetterService.ReopenEmailComponentAsync</c> on the audited one. So this
    /// test asserts what is left: every OTHER verdict a held message can reach is applicable.</para>
    /// </summary>
    [Fact]
    public void Every_verdict_a_held_message_can_reach_is_applicable_except_the_one_it_cannot_reach()
    {
        var refused = new List<string>();

        foreach (var combination in CombinationsUpTo(WrittenByProduction, maxSize: 2))
        {
            var evaluation = EmailInquiryAssemblyStateMachine.Evaluate(
                combination.Count, combination);

            // Reaching this verdict requires every component terminal, at least one Completed and
            // none held — which cannot happen while the assembly is still FailedRecoverable,
            // because both reopen paths write Extracting before any result can arrive.
            if (evaluation.Status == EmailInquiryAssemblyStatus.ReadyForAssembly) continue;

            if (!EmailInquiryAssemblyStateMachine.CanTransition(
                    EmailInquiryAssemblyStatus.FailedRecoverable, evaluation.Status))
                refused.Add($"[{string.Join(", ", combination)}] evaluates to {evaluation.Status}");
        }

        Assert.True(refused.Count == 0,
            "A held message evaluated to a status the transition table refuses:\n  "
            + string.Join("\n  ", refused));

        // The precondition the exclusion above rests on, stated so it cannot quietly stop being
        // true: recovery from a hold is authorized, and it re-enters the pipeline.
        Assert.True(EmailInquiryAssemblyStateMachine.CanAutomaticSchedulingRecoveryTransition(
            EmailInquiryAssemblyStatus.FailedRecoverable));
        Assert.True(EmailInquiryAssemblyStateMachine.CanTransition(
            EmailInquiryAssemblyStatus.FailedRecoverable, EmailInquiryAssemblyStatus.Extracting));
    }

    /// <summary>
    /// A message ALREADY IN A PERSON'S TRAY can still be holding a part that is not terminal, and
    /// the transition table deliberately refuses to move it back — NeedsReview leads only to
    /// Assembled or NoInquiry, because an automatic recompute must not pull a message out of a
    /// human's queue.
    ///
    /// <para>That refusal is only safe if the PART has a mover of its own. It did not: assembly
    /// NeedsReview + component FailedRecoverable + no job was the shape every recovery door was
    /// shut on at once, and the sweep that did claim the component threw when it tried to
    /// reschedule, forever. The governed scheduling reopen is that mover, and this test pins the
    /// authority rather than the incident.</para>
    /// </summary>
    [Fact]
    public void A_message_in_review_can_still_have_its_unscheduled_part_reopened()
    {
        // The automatic authority must NOT reach into a review tray — that is the property the
        // governed one exists to provide safely.
        Assert.False(EmailInquiryAssemblyStateMachine.CanAutomaticSchedulingRecoveryTransition(
            EmailInquiryAssemblyStatus.NeedsReview));

        // The governed one must, or the shape has no mover at all.
        Assert.True(EmailInquiryAssemblyStateMachine.CanGovernedSchedulingRecoveryTransition(
            EmailInquiryAssemblyStatus.NeedsReview));

        // And it must reach every other state a part can be stranded in without a job.
        foreach (var from in new[]
                 {
                     EmailInquiryAssemblyStatus.Captured,
                     EmailInquiryAssemblyStatus.Inspecting,
                     EmailInquiryAssemblyStatus.Extracting,
                     EmailInquiryAssemblyStatus.FailedRecoverable,
                     EmailInquiryAssemblyStatus.NeedsReview
                 })
        {
            Assert.True(EmailInquiryAssemblyStateMachine.CanGovernedSchedulingRecoveryTransition(from),
                $"A part of a message at {from} could hold no job and would have no way back.");
        }
    }

    /// <summary>
    /// The reopen is wide, so its limits matter as much as its reach. A decision that was made
    /// ON PURPOSE is not reopened by a scheduling retry.
    /// </summary>
    [Fact]
    public void The_governed_reopen_does_not_reach_a_decision_that_was_made_deliberately()
    {
        // Malware is not "retry later". RejectedSecurity is absorbing, and a reopen that could
        // reach it would walk a refused message back into processing.
        Assert.False(EmailInquiryAssemblyStateMachine.CanGovernedSchedulingRecoveryTransition(
            EmailInquiryAssemblyStatus.RejectedSecurity));

        // "This was never an inquiry" belongs to the audited triage reopen, which carries a
        // human's judgement. A scheduling retry has none to offer.
        Assert.False(EmailInquiryAssemblyStateMachine.CanGovernedSchedulingRecoveryTransition(
            EmailInquiryAssemblyStatus.NoInquiry));

        // And a message that already produced its Lead is finished.
        Assert.False(EmailInquiryAssemblyStateMachine.CanGovernedSchedulingRecoveryTransition(
            EmailInquiryAssemblyStatus.Assembled));
    }

    /// <summary>
    /// The grant is the whole governance: without an actor and a reason there is no wider
    /// authority to acquire. A future call site cannot get it by forgetting to pass something.
    /// </summary>
    [Fact]
    public void A_reopen_cannot_be_performed_by_nobody_for_no_reason()
    {
        Assert.Throws<ArgumentException>(() => new EmailInquirySchedulingGrant("  ", "because"));
        Assert.Throws<ArgumentException>(() => new EmailInquirySchedulingGrant("someone", "   "));

        var sweep = EmailInquirySchedulingGrant.RecoverySweep;
        Assert.Equal(EmailInquirySchedulingGrant.RecoverySweepActor, sweep.ActorId);
        Assert.Contains(sweep.ActorId, sweep.Describe(), StringComparison.Ordinal);

        // It is rendered on the Inbound Mail screen, which REJECTS rather than truncates
        // anything over 300 characters — a reopen nobody can read is not a record.
        Assert.True(sweep.Describe().Length <= 300, $"{sweep.Describe().Length} characters");
    }

    // =====================================================================================
    // 2. EVERY NON-TERMINAL COMPONENT STATE HAS SOMETHING THAT MOVES IT
    // =====================================================================================

    /// <summary>
    /// Partitions the WHOLE component enum: every value is either terminal, or claimed by the
    /// recovery sweep, or explicitly owned by the audited dead-letter recovery command. Nothing
    /// may fall between.
    ///
    /// <para>The lists it checks are the ones the sweep's own queries use — EF translates
    /// <c>Contains</c> to <c>IN</c> — so this is not a copy of the predicate agreeing with
    /// itself. <see cref="EmailInquiryComponentStatus.FailedRecoverable"/> is the value that used
    /// to fall through: not terminal, not queried by any sweep, and reachable by four different
    /// scheduling failures that leave no extraction job for the dead-letter queue to recover.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_component_state_is_terminal_or_has_a_mover()
    {
        var orphaned = new List<EmailInquiryComponentStatus>();

        foreach (var status in AllComponentStatuses)
        {
            var terminal = new EmailInquiryComponent { Status = status }.IsTerminal;
            var sweptAlways = EmailInquiryAssemblyRecoveryService.SweptRegardlessOfJob.Contains(status);
            var sweptWhenUnbound = status == EmailInquiryAssemblyRecoveryService.SweptOnlyWithoutJob;

            if (!terminal && !sweptAlways && !sweptWhenUnbound) orphaned.Add(status);
        }

        Assert.True(orphaned.Count == 0,
            "These component states are neither terminal nor reachable by any sweep, so a "
            + "message resting in one of them is lost: " + string.Join(", ", orphaned));
    }

    /// <summary>
    /// The mirror assertion, and the one that stops the partition above being satisfied by
    /// declaring everything sweepable: a TERMINAL state must not be swept, or every finished
    /// message would be re-examined forever and a decided part could be walked backwards.
    /// </summary>
    [Fact]
    public void No_terminal_component_state_is_swept()
    {
        foreach (var status in AllComponentStatuses)
        {
            if (!new EmailInquiryComponent { Status = status }.IsTerminal) continue;

            Assert.DoesNotContain(status, EmailInquiryAssemblyRecoveryService.SweptRegardlessOfJob);
            Assert.NotEqual(EmailInquiryAssemblyRecoveryService.SweptOnlyWithoutJob, status);
        }
    }

    // =====================================================================================
    // 3. THE LEDGER MUST NOT CLAIM PROGRESS OVER WORK THAT STOPPED
    // =====================================================================================

    [Fact]
    public void A_decided_assembly_never_leaves_its_ledger_row_claiming_progress()
    {
        var decided = new[]
        {
            EmailInquiryAssemblyStatus.Assembled,
            EmailInquiryAssemblyStatus.NeedsReview,
            EmailInquiryAssemblyStatus.NoInquiry,
            EmailInquiryAssemblyStatus.RejectedSecurity
        };

        foreach (var status in decided)
        {
            var corrected = EmailInquiryLedgerReconciliation.StatusFor(
                "Queued", status, hasRunnableJob: false, hasStoppedJob: true);

            Assert.NotNull(corrected);
            Assert.False(EmailInquiryLedgerReconciliation.ClaimsInFlight(corrected),
                $"An assembly at {status} left its ledger row claiming '{corrected}'.");
        }
    }

    [Fact]
    public void A_message_with_no_assembly_and_no_live_job_is_not_left_reading_Queued()
    {
        var corrected = EmailInquiryLedgerReconciliation.StatusFor(
            "Queued", assemblyStatus: null, hasRunnableJob: false, hasStoppedJob: true);

        // This is the population the queue's own claim statement creates: the exhausted-lease and
        // lineage-quarantine CTEs dead-letter a row with no worker in the loop, so neither the
        // persist path nor the worker's dead-letter annotation ever runs and "Queued" is never
        // cleared. A terminal state that presents itself as in-flight is why nobody looked.
        Assert.Equal(ERP_RFQ_Automation.Extraction.ExtractionWorker.DeadLetterParseStatus, corrected);
        Assert.False(EmailInquiryLedgerReconciliation.ClaimsInFlight(corrected));
    }

    [Fact]
    public void A_live_job_outranks_everything_and_the_ledger_is_left_alone()
    {
        // The correction must never fire over work that is genuinely running: reporting a healthy
        // message as failed is the same lie pointing the other way.
        Assert.Null(EmailInquiryLedgerReconciliation.StatusFor(
            "Queued", EmailInquiryAssemblyStatus.NeedsReview,
            hasRunnableJob: true, hasStoppedJob: false));

        Assert.Null(EmailInquiryLedgerReconciliation.StatusFor(
            "Queued", assemblyStatus: null, hasRunnableJob: true, hasStoppedJob: false));
    }

    [Fact]
    public void An_assembly_still_in_the_pipeline_is_left_to_the_sweeps_that_own_it()
    {
        foreach (var status in new[]
                 {
                     EmailInquiryAssemblyStatus.Captured,
                     EmailInquiryAssemblyStatus.Inspecting,
                     EmailInquiryAssemblyStatus.Extracting,
                     EmailInquiryAssemblyStatus.ReadyForAssembly,
                     EmailInquiryAssemblyStatus.FailedRecoverable
                 })
        {
            Assert.Null(EmailInquiryLedgerReconciliation.StatusFor(
                "Queued", status, hasRunnableJob: false, hasStoppedJob: true));
        }
    }

    [Fact]
    public void A_status_that_is_already_a_decision_is_never_rewritten()
    {
        foreach (var settled in new[] { "Success", "NeedsReview", "Rejected", "Manual" })
        {
            Assert.Null(EmailInquiryLedgerReconciliation.StatusFor(
                settled, EmailInquiryAssemblyStatus.NeedsReview,
                hasRunnableJob: false, hasStoppedJob: true));
        }
    }

    // =====================================================================================
    // 4. NOTHING A PERSON READS MAY CARRY THIS SYSTEM'S PRIVATE VOCABULARY
    // =====================================================================================

    /// <summary>
    /// Every operator-facing sentence this module can write, checked against the member names of
    /// both state enums. A reason that says "FailedRecoverable" or "RejectedSecurity" at a
    /// salesperson is not an explanation.
    /// </summary>
    [Fact]
    public void No_reason_a_person_reads_contains_an_internal_state_name()
    {
        var enumNames = Enum.GetNames<EmailInquiryAssemblyStatus>()
            .Concat(Enum.GetNames<EmailInquiryComponentStatus>())
            .Distinct()
            .ToArray();

        var sentences = new List<string>();

        // Every evaluation reason the state machine can produce.
        foreach (var combination in CombinationsUpTo(AllComponentStatuses, maxSize: 2))
        {
            var reason = EmailInquiryAssemblyStateMachine.Evaluate(combination.Count, combination).Reason;
            if (reason is not null) sentences.Add(reason);
        }

        // Plus every hold sentence declared as a constant.
        sentences.AddRange(typeof(EmailInquiryHoldReasons)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string) && f.Name.EndsWith("Detail"))
            .Select(f => (string)f.GetRawConstantValue()!));

        sentences.Add(ERP_RFQ_Automation.Ingestion.Triage.EmailIngestEnqueuer.MalwareRefusedDetail);
        sentences.Add(ERP_RFQ_Automation.Ingestion.Triage.EmailIngestEnqueuer.InspectionUnavailableDetail);
        sentences.Add(ERP_RFQ_Automation.Ingestion.Triage.EmailIngestEnqueuer.InspectionRefusedDetail);
        sentences.Add(ERP_RFQ_Automation.Extraction.ExtractionWorker.CommercialNonInquiryDetail);
        sentences.Add(ERP_RFQ_Automation.Extraction.ExtractionWorker.DeadLetterParseStatus);

        var leaked = new List<string>();
        foreach (var sentence in sentences.Distinct())
        foreach (var name in enumNames)
        {
            if (sentence.Contains(name, StringComparison.Ordinal))
                leaked.Add($"'{name}' in \"{sentence}\"");
        }

        Assert.True(leaked.Count == 0,
            "Operator-facing text carries an internal state name:\n  " + string.Join("\n  ", leaked));
    }

    /// <summary>
    /// The Inbound Mail screen's <c>presentableServerText</c> REJECTS rather than truncates
    /// anything over 300 characters, and the coordinator stores these as "{code}: {detail}" — so
    /// a sentence one character too long renders the held message with no reason at all.
    /// </summary>
    [Fact]
    public void Every_operator_sentence_fits_the_screen_that_shows_it()
    {
        const int MaxRendered = 300;

        var sentences = typeof(EmailInquiryHoldReasons)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string) && f.Name.EndsWith("Detail"))
            .Select(f => (Name: f.Name, Text: (string)f.GetRawConstantValue()!))
            .ToList();

        sentences.Add(("MalwareRefusedDetail",
            ERP_RFQ_Automation.Ingestion.Triage.EmailIngestEnqueuer.MalwareRefusedDetail));
        sentences.Add(("InspectionUnavailableDetail",
            ERP_RFQ_Automation.Ingestion.Triage.EmailIngestEnqueuer.InspectionUnavailableDetail));
        sentences.Add(("InspectionRefusedDetail",
            ERP_RFQ_Automation.Ingestion.Triage.EmailIngestEnqueuer.InspectionRefusedDetail));
        sentences.Add(("CommercialNonInquiryDetail",
            ERP_RFQ_Automation.Extraction.ExtractionWorker.CommercialNonInquiryDetail));

        var oversize = sentences
            .Where(s => s.Text.Length > MaxRendered)
            .Select(s => $"{s.Name} is {s.Text.Length} characters")
            .ToList();

        Assert.True(oversize.Count == 0,
            "These reasons would render as a blank explanation:\n  " + string.Join("\n  ", oversize));
    }

    /// <summary>
    /// The capture grace is the one threshold here that may NOT be switched off. Zero would mean
    /// "sweep a message the scheduler committed a millisecond ago", which is not an operator
    /// intent — it is a race with the live scheduling pass, and every other knob in this options
    /// class can legitimately be zeroed, so the floor has to live in the type rather than in a
    /// convention nobody reads.
    /// </summary>
    [Fact]
    public void The_capture_grace_cannot_be_switched_off()
    {
        Assert.True(new EmailInquiryAssemblyRecoveryOptions { CaptureGraceSeconds = 0 }
            .ValidatedCaptureGrace >= TimeSpan.FromSeconds(30));
        Assert.True(new EmailInquiryAssemblyRecoveryOptions { CaptureGraceSeconds = -5000 }
            .ValidatedCaptureGrace >= TimeSpan.FromSeconds(30));

        // And it is still a knob: a deployment that wants to wait longer can.
        Assert.Equal(TimeSpan.FromSeconds(600),
            new EmailInquiryAssemblyRecoveryOptions { CaptureGraceSeconds = 600 }.ValidatedCaptureGrace);

        // The thresholds that MAY be zero still may be — the floor is deliberately narrow.
        Assert.Equal(TimeSpan.Zero,
            new EmailInquiryAssemblyRecoveryOptions { StrandedComponentSweepMinutes = 0 }
                .ValidatedStrandedComponentAge);
    }

    /// <summary>The ledger vocabulary is a varchar(50); an overflow would throw on write.</summary>
    [Fact]
    public void Every_ledger_status_fits_its_column()
    {
        foreach (var value in new[]
                 {
                     EmailInquiryLedgerReconciliation.NeedsReview,
                     EmailInquiryLedgerReconciliation.Rejected,
                     EmailInquiryLedgerReconciliation.NothingToExtract,
                     ERP_RFQ_Automation.Extraction.ExtractionWorker.DeadLetterParseStatus
                 })
        {
            Assert.True(value.Length <= 50, $"'{value}' is {value.Length} characters.");
        }
    }

    // =====================================================================================
    // Harness
    // =====================================================================================

    /// <summary>
    /// The one instruction the product used to give an operator about this shape — "reprocess the
    /// message to rebuild it as one inquiry" — was guaranteed to fail: the reprocess command
    /// throws on a message whose part holds no job. Telling somebody to take an action that
    /// cannot work is worse than telling them nothing, because they conclude the data is at
    /// fault. Pinned so the sentence cannot drift back.
    /// </summary>
    [Fact]
    public void The_ownership_hold_does_not_tell_a_person_to_do_something_that_will_fail()
    {
        Assert.DoesNotContain("reprocess the message",
            EmailInquiryHoldReasons.OwnershipUnresolvedDetail, StringComparison.OrdinalIgnoreCase);
        Assert.True(EmailInquiryHoldReasons.OwnershipUnresolvedDetail.Length <= 300);
    }

    /// <summary>Every non-empty combination WITH repetition, up to <paramref name="maxSize"/>.</summary>
    private static IEnumerable<IReadOnlyCollection<EmailInquiryComponentStatus>> CombinationsUpTo(
        IReadOnlyList<EmailInquiryComponentStatus> alphabet, int maxSize)
    {
        var current = new List<IReadOnlyCollection<EmailInquiryComponentStatus>>();
        foreach (var value in alphabet) current.Add(new[] { value });

        foreach (var combination in current) yield return combination;

        for (var size = 2; size <= maxSize; size++)
        {
            var next = new List<IReadOnlyCollection<EmailInquiryComponentStatus>>();
            foreach (var shorter in current)
            foreach (var value in alphabet)
            {
                var extended = shorter.Append(value).ToArray();
                next.Add(extended);
                yield return extended;
            }
            current = next;
        }
    }
}
