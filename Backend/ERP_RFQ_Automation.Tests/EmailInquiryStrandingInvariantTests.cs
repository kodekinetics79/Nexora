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
