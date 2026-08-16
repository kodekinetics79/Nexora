using ERP_RFQ_Automation.Ingestion.Assembly;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The commercial gate: given what every part of a message currently is, what is the message?
///
/// <para>These are the cases the pipeline used to get wrong by not asking the question at all.
/// A test that merely proves several jobs share a batch id proves nothing here — what has to
/// hold is that a message with an outstanding or held part CANNOT be turned into a Lead, and
/// that the reasons a message is held are distinguishable from the reasons it is refused.</para>
/// </summary>
public class EmailInquiryAssemblyStateMachineTests
{
    private static EmailInquiryAssemblyEvaluation Evaluate(
        int expected, params EmailInquiryComponentStatus[] statuses)
        => EmailInquiryAssemblyStateMachine.Evaluate(expected, statuses);

    // ---- the barrier -------------------------------------------------------------------

    [Fact]
    public void Body_only_inquiry_is_ready_when_its_single_component_completes()
    {
        var result = Evaluate(1, EmailInquiryComponentStatus.Completed);

        Assert.Equal(EmailInquiryAssemblyStatus.ReadyForAssembly, result.Status);
        Assert.Equal(1, result.CompletedComponentCount);
    }

    [Fact]
    public void Attachment_only_inquiry_is_ready_when_its_single_component_completes()
    {
        var result = Evaluate(1, EmailInquiryComponentStatus.Completed);

        Assert.Equal(EmailInquiryAssemblyStatus.ReadyForAssembly, result.Status);
    }

    [Fact]
    public void Body_plus_one_attachment_is_NOT_ready_while_the_attachment_is_still_extracting()
    {
        // THE defect, stated as a test. The body finishing first must not be able to finalize
        // a Lead: the priced lines are in the attachment that has not landed yet.
        var result = Evaluate(2,
            EmailInquiryComponentStatus.Completed,
            EmailInquiryComponentStatus.Extracting);

        Assert.Equal(EmailInquiryAssemblyStatus.Extracting, result.Status);
        Assert.Equal(1, result.CompletedComponentCount);
    }

    [Fact]
    public void Body_plus_multiple_attachments_is_ready_only_when_every_one_is_terminal()
    {
        var partial = Evaluate(4,
            EmailInquiryComponentStatus.Completed,
            EmailInquiryComponentStatus.Completed,
            EmailInquiryComponentStatus.Completed,
            EmailInquiryComponentStatus.Pending);
        Assert.Equal(EmailInquiryAssemblyStatus.Extracting, partial.Status);

        var complete = Evaluate(4,
            EmailInquiryComponentStatus.Completed,
            EmailInquiryComponentStatus.Completed,
            EmailInquiryComponentStatus.Completed,
            EmailInquiryComponentStatus.Completed);
        Assert.Equal(EmailInquiryAssemblyStatus.ReadyForAssembly, complete.Status);
        Assert.Equal(4, complete.CompletedComponentCount);
    }

    [Fact]
    public void A_component_row_that_was_never_written_holds_the_message_rather_than_shrinking_it()
    {
        // The barrier must not be satisfiable by failing to record something. Two parts were
        // expected; only one has a row, so the message is held for replay rather than read as
        // a completed single-part message.
        var result = Evaluate(2, EmailInquiryComponentStatus.Completed);

        Assert.Equal(EmailInquiryAssemblyStatus.FailedRecoverable, result.Status);
        Assert.Contains("no record yet", result.Reason);
    }

    // ---- "nothing to do" must never read as "everything is done" --------------------------

    [Fact]
    public void A_message_that_carried_nothing_is_NoInquiry_not_ready()
    {
        // No fresh body, no attachments. This returned ReadyForAssembly in the first draft,
        // which made "we captured nothing" and "we captured everything" the same state — one
        // merge away from a Lead with nothing in it.
        var result = Evaluate(0);

        Assert.Equal(EmailInquiryAssemblyStatus.NoInquiry, result.Status);
        Assert.Equal(0, result.CapturedComponentCount);
    }

    [Fact]
    public void A_message_whose_every_part_was_refused_or_unsupported_goes_to_a_human()
    {
        // Distinct from the empty message: a person may well recognise an inquiry in a file the
        // pipeline cannot read. It must never become an empty Lead and never be discarded.
        var result = Evaluate(2,
            EmailInquiryComponentStatus.Skipped,
            EmailInquiryComponentStatus.Skipped);

        Assert.Equal(EmailInquiryAssemblyStatus.NeedsReview, result.Status);
        Assert.Equal(2, result.CompletedComponentCount);
        Assert.Equal(0, result.CapturedComponentCount);
        Assert.Contains("retained for review", result.Reason);
    }

    [Fact]
    public void An_empty_message_and_an_all_refused_message_are_not_the_same_state()
    {
        // The two cases have different operator consequences: one is noise, the other is a
        // capability gap worth knowing about. Collapsing them hides the second forever.
        Assert.NotEqual(
            Evaluate(0).Status,
            Evaluate(1, EmailInquiryComponentStatus.Skipped).Status);
    }

    [Fact]
    public void ReadyForAssembly_requires_captured_content_not_merely_an_empty_expectation()
    {
        // The invariant, stated directly: every route to ReadyForAssembly has at least one
        // component that durably captured something.
        foreach (var statuses in new[]
                 {
                     Array.Empty<EmailInquiryComponentStatus>(),
                     new[] { EmailInquiryComponentStatus.Skipped },
                     new[] { EmailInquiryComponentStatus.Skipped, EmailInquiryComponentStatus.Skipped }
                 })
        {
            var result = EmailInquiryAssemblyStateMachine.Evaluate(statuses.Length, statuses);
            Assert.NotEqual(EmailInquiryAssemblyStatus.ReadyForAssembly, result.Status);
        }

        // Captured content is necessary but no longer sufficient: nothing the sender attached
        // may have gone unread either.
        var withContent = Evaluate(2,
            EmailInquiryComponentStatus.Completed,
            EmailInquiryComponentStatus.Ignored);
        Assert.Equal(EmailInquiryAssemblyStatus.ReadyForAssembly, withContent.Status);
        Assert.Equal(1, withContent.CapturedComponentCount);
    }

    // ---- an unread attachment outranks a cleanly extracted body ----------------------------

    [Fact]
    public void See_attached_quotation_plus_an_unreadable_attachment_becomes_a_flagged_inquiry()
    {
        // The body extracts perfectly and says "please see the attached quotation"; the
        // attachment is a format we cannot read.
        //
        // This used to return NeedsReview, on the reasoning that a clean body-only Lead prices
        // a deal against a document nobody opened. The concern is real, but withholding the
        // Lead was the wrong lever for it: `Skipped` is reached by any attachment outside
        // DocumentIntakeAllowList — including .p7s, which the SENDER'S gateway attaches to
        // every outgoing message — so a whole customer could never produce a Lead at all, and
        // NeedsReview cannot reach ReadyForAssembly, so the sweep never looked again.
        //
        // The inquiry is now built from what WAS read and the gap is named on it, which the
        // salesperson can price around. Strictly more information than never seeing it.
        var result = Evaluate(2,
            EmailInquiryComponentStatus.Completed,
            EmailInquiryComponentStatus.Skipped);

        Assert.Equal(EmailInquiryAssemblyStatus.ReadyForAssembly, result.Status);
        Assert.Equal(1, result.CapturedComponentCount);
        Assert.Contains("could not be read", result.Reason);
    }

    [Fact]
    public void One_unread_attachment_among_several_good_ones_does_not_discard_the_good_ones()
    {
        // Three attachments' worth of priced lines is real money. Discarding all of it because
        // a fourth part was unreadable is the expensive half of the old rule.
        var result = Evaluate(4,
            EmailInquiryComponentStatus.Completed,
            EmailInquiryComponentStatus.Completed,
            EmailInquiryComponentStatus.Completed,
            EmailInquiryComponentStatus.Skipped);

        Assert.Equal(EmailInquiryAssemblyStatus.ReadyForAssembly, result.Status);
        Assert.Equal(3, result.CapturedComponentCount);
        Assert.Contains("could not be read", result.Reason);
    }

    [Fact]
    public void A_provable_inline_asset_is_the_only_thing_that_goes_unread_SILENTLY()
    {
        // Both shapes now become an inquiry; what separates them is whether anyone is TOLD.
        //
        // A signature logo is provably non-commercial, so it costs the message nothing: no
        // review reason, no noise on the Lead. An unreadable part is a real gap in the
        // evidence, so it becomes an inquiry too — but a flagged one. The distinction moved
        // from "does a Lead exist" to "what does the Lead say", which is where it belongs.
        var withLogo = Evaluate(2,
            EmailInquiryComponentStatus.Completed,
            EmailInquiryComponentStatus.Ignored);
        Assert.Equal(EmailInquiryAssemblyStatus.ReadyForAssembly, withLogo.Status);
        Assert.Null(withLogo.Reason);

        var withUnknown = Evaluate(2,
            EmailInquiryComponentStatus.Completed,
            EmailInquiryComponentStatus.Skipped);
        Assert.Equal(EmailInquiryAssemblyStatus.ReadyForAssembly, withUnknown.Status);
        Assert.Contains("could not be read", withUnknown.Reason);
    }

    [Fact]
    public void A_message_of_nothing_but_inline_assets_is_NoInquiry_not_ready()
    {
        var result = Evaluate(2,
            EmailInquiryComponentStatus.Ignored,
            EmailInquiryComponentStatus.Ignored);

        Assert.Equal(EmailInquiryAssemblyStatus.NoInquiry, result.Status);
        Assert.Equal(0, result.CapturedComponentCount);
    }

    // ---- infrastructure faults hold; they do not decide -----------------------------------

    [Fact]
    public void One_attachment_held_on_infrastructure_holds_the_whole_message()
    {
        // Storage or the scanner was unavailable for ONE attachment. The body completed. The
        // old pipeline produced a body-only Lead here and told the operator to retry the file.
        var result = Evaluate(2,
            EmailInquiryComponentStatus.Completed,
            EmailInquiryComponentStatus.FailedRecoverable);

        Assert.Equal(EmailInquiryAssemblyStatus.FailedRecoverable, result.Status);
    }

    [Fact]
    public void A_recoverable_hold_reason_names_no_infrastructure_detail()
    {
        var result = Evaluate(2,
            EmailInquiryComponentStatus.Completed,
            EmailInquiryComponentStatus.FailedRecoverable);

        // The reason is durable and rendered to an operator. A bucket, endpoint, credential or
        // provider exception type reaching this string is a disclosure, not a diagnostic.
        Assert.DoesNotContain("Amazon", result.Reason!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bucket", result.Reason!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Exception", result.Reason!, StringComparison.OrdinalIgnoreCase);
        // And it must not advise a retry that the operator cannot perform.
        Assert.DoesNotContain("try again", result.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_hold_is_not_counted_as_completed_progress()
    {
        var result = Evaluate(3,
            EmailInquiryComponentStatus.Completed,
            EmailInquiryComponentStatus.Skipped,
            EmailInquiryComponentStatus.FailedRecoverable);

        Assert.Equal(EmailInquiryAssemblyStatus.FailedRecoverable, result.Status);
        Assert.Equal(2, result.CompletedComponentCount);
    }

    // ---- security outranks everything -----------------------------------------------------

    [Fact]
    public void A_refused_component_rejects_the_message_even_while_siblings_are_in_flight()
    {
        var result = Evaluate(3,
            EmailInquiryComponentStatus.Completed,
            EmailInquiryComponentStatus.Extracting,
            EmailInquiryComponentStatus.RefusedSecurity);

        Assert.Equal(EmailInquiryAssemblyStatus.RejectedSecurity, result.Status);
    }

    [Fact]
    public void A_security_refusal_outranks_a_concurrent_infrastructure_hold()
    {
        // A message carrying malware is not "retry when storage returns". If these were ordered
        // the other way the message would sit in the recovery sweep being rescanned forever.
        var result = Evaluate(2,
            EmailInquiryComponentStatus.RefusedSecurity,
            EmailInquiryComponentStatus.FailedRecoverable);

        Assert.Equal(EmailInquiryAssemblyStatus.RejectedSecurity, result.Status);
    }

    // ---- transitions ----------------------------------------------------------------------

    [Theory]
    [InlineData(EmailInquiryAssemblyStatus.Captured, EmailInquiryAssemblyStatus.Inspecting)]
    [InlineData(EmailInquiryAssemblyStatus.Captured, EmailInquiryAssemblyStatus.NoInquiry)]
    [InlineData(EmailInquiryAssemblyStatus.Inspecting, EmailInquiryAssemblyStatus.NeedsReview)]
    [InlineData(EmailInquiryAssemblyStatus.Extracting, EmailInquiryAssemblyStatus.NeedsReview)]
    [InlineData(EmailInquiryAssemblyStatus.Inspecting, EmailInquiryAssemblyStatus.Extracting)]
    [InlineData(EmailInquiryAssemblyStatus.Extracting, EmailInquiryAssemblyStatus.ReadyForAssembly)]
    [InlineData(EmailInquiryAssemblyStatus.ReadyForAssembly, EmailInquiryAssemblyStatus.Assembled)]
    [InlineData(EmailInquiryAssemblyStatus.ReadyForAssembly, EmailInquiryAssemblyStatus.NeedsReview)]
    public void Legal_transitions_are_permitted(
        EmailInquiryAssemblyStatus from, EmailInquiryAssemblyStatus to)
        => Assert.True(EmailInquiryAssemblyStateMachine.CanTransition(from, to));

    [Fact]
    public void Captured_cannot_jump_straight_to_ReadyForAssembly()
        // Nothing has completed yet, so there is nothing captured; the only way out of Captured
        // for a message with no parts is NoInquiry.
        => Assert.False(EmailInquiryAssemblyStateMachine.CanTransition(
            EmailInquiryAssemblyStatus.Captured, EmailInquiryAssemblyStatus.ReadyForAssembly));

    [Theory]
    [InlineData(EmailInquiryAssemblyStatus.Captured, EmailInquiryAssemblyStatus.Assembled)]
    [InlineData(EmailInquiryAssemblyStatus.Inspecting, EmailInquiryAssemblyStatus.Assembled)]
    [InlineData(EmailInquiryAssemblyStatus.Extracting, EmailInquiryAssemblyStatus.Assembled)]
    [InlineData(EmailInquiryAssemblyStatus.NoInquiry, EmailInquiryAssemblyStatus.Assembled)]
    public void A_message_cannot_reach_Assembled_without_passing_the_barrier(
        EmailInquiryAssemblyStatus from, EmailInquiryAssemblyStatus to)
    {
        // ReadyForAssembly is the ONLY door into Assembled. If any other state could reach it,
        // the barrier would be advisory.
        Assert.False(EmailInquiryAssemblyStateMachine.CanTransition(from, to));
        Assert.Throws<InvalidOperationException>(
            () => EmailInquiryAssemblyStateMachine.EnsureTransition(from, to));
    }

    [Theory]
    [InlineData(EmailInquiryAssemblyStatus.Captured)]
    [InlineData(EmailInquiryAssemblyStatus.Inspecting)]
    [InlineData(EmailInquiryAssemblyStatus.Extracting)]
    [InlineData(EmailInquiryAssemblyStatus.ReadyForAssembly)]
    [InlineData(EmailInquiryAssemblyStatus.Assembled)]
    [InlineData(EmailInquiryAssemblyStatus.NeedsReview)]
    [InlineData(EmailInquiryAssemblyStatus.FailedRecoverable)]
    [InlineData(EmailInquiryAssemblyStatus.NoInquiry)]
    public void RejectedSecurity_is_absorbing(EmailInquiryAssemblyStatus to)
    {
        // A refusal must not be walked back by a retry or a replay. Reversing it is a human act
        // that creates its own record.
        Assert.False(EmailInquiryAssemblyStateMachine.CanTransition(
            EmailInquiryAssemblyStatus.RejectedSecurity, to));
    }

    [Theory]
    [InlineData(EmailInquiryAssemblyStatus.Inspecting)]
    [InlineData(EmailInquiryAssemblyStatus.Extracting)]
    public void Recovery_re_enters_the_pipeline(EmailInquiryAssemblyStatus to)
        => Assert.True(EmailInquiryAssemblyStateMachine.CanTransition(
            EmailInquiryAssemblyStatus.FailedRecoverable, to));

    [Fact]
    public void Recovery_may_NOT_declare_a_held_message_ready()
    {
        // A held message has components that never reached a terminal state. Letting recovery
        // jump to ReadyForAssembly would walk straight past the barrier this aggregate exists
        // to impose — recovery re-inspects and re-extracts, and the barrier decides again.
        Assert.False(EmailInquiryAssemblyStateMachine.CanTransition(
            EmailInquiryAssemblyStatus.FailedRecoverable, EmailInquiryAssemblyStatus.ReadyForAssembly));
        Assert.Throws<InvalidOperationException>(() => EmailInquiryAssemblyStateMachine.EnsureTransition(
            EmailInquiryAssemblyStatus.FailedRecoverable, EmailInquiryAssemblyStatus.ReadyForAssembly));
    }

    [Fact]
    public void An_amendment_may_NOT_re_open_an_assembled_message()
    {
        // An amendment is a different email: its own Message-Id, its own EmailIngest, its own
        // assembly, reconciled onto the same commercial Lead by ILeadIdentityApplicationService.
        // Re-opening this assembly would mutate the durable record of what the FIRST message
        // contained, and the revision history would then describe a message that never arrived
        // in that form.
        Assert.False(EmailInquiryAssemblyStateMachine.CanTransition(
            EmailInquiryAssemblyStatus.Assembled, EmailInquiryAssemblyStatus.Extracting));
        Assert.False(EmailInquiryAssemblyStateMachine.CanTransition(
            EmailInquiryAssemblyStatus.NeedsReview, EmailInquiryAssemblyStatus.Extracting));
    }

    [Theory]
    [InlineData(EmailInquiryAssemblyStatus.NeedsReview, true)]
    [InlineData(EmailInquiryAssemblyStatus.FailedRecoverable, true)]
    [InlineData(EmailInquiryAssemblyStatus.Extracting, true)]
    [InlineData(EmailInquiryAssemblyStatus.Assembled, false)]
    [InlineData(EmailInquiryAssemblyStatus.RejectedSecurity, false)]
    public void Only_governed_recovery_can_reopen_a_held_message(
        EmailInquiryAssemblyStatus status, bool expected)
    {
        Assert.Equal(expected,
            EmailInquiryAssemblyStateMachine.CanGovernedExtractionRecoveryTransition(status));
    }

    [Theory]
    [InlineData(EmailInquiryAssemblyStatus.Inspecting)]
    [InlineData(EmailInquiryAssemblyStatus.Extracting)]
    [InlineData(EmailInquiryAssemblyStatus.ReadyForAssembly)]
    [InlineData(EmailInquiryAssemblyStatus.Assembled)]
    public void NoInquiry_is_absorbing_until_audited_reprocessing_exists(EmailInquiryAssemblyStatus to)
        // Re-opening it needs a recorded actor, a reason and an idempotency key. None of that
        // exists yet, so the honest behaviour is to refuse rather than allow a silent,
        // unattributed reopen.
        => Assert.False(EmailInquiryAssemblyStateMachine.CanTransition(
            EmailInquiryAssemblyStatus.NoInquiry, to));

    [Fact]
    public void Re_declaring_the_current_state_is_idempotent()
    {
        // Replays re-assert what they observe. Treating that as an illegal transition would
        // make every recovery pass throw.
        foreach (EmailInquiryAssemblyStatus status in Enum.GetValues<EmailInquiryAssemblyStatus>())
            Assert.True(EmailInquiryAssemblyStateMachine.CanTransition(status, status));
    }
}
