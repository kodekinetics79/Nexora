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

    [Fact]
    public void A_message_with_nothing_enqueuable_is_ready_immediately_not_held_forever()
    {
        // No fresh body, no supported attachment. There is nothing to wait for and nothing to
        // recover; a permanent hold here would be a queue entry no operator can ever clear.
        var result = Evaluate(0);

        Assert.Equal(EmailInquiryAssemblyStatus.ReadyForAssembly, result.Status);
        Assert.Equal(0, result.CompletedComponentCount);
    }

    [Fact]
    public void A_message_whose_every_part_was_skipped_is_ready_with_its_reasons_not_held()
    {
        var result = Evaluate(2,
            EmailInquiryComponentStatus.Skipped,
            EmailInquiryComponentStatus.Skipped);

        Assert.Equal(EmailInquiryAssemblyStatus.ReadyForAssembly, result.Status);
        Assert.Equal(2, result.CompletedComponentCount);
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
    [InlineData(EmailInquiryAssemblyStatus.Captured, EmailInquiryAssemblyStatus.ReadyForAssembly)]
    [InlineData(EmailInquiryAssemblyStatus.Inspecting, EmailInquiryAssemblyStatus.Extracting)]
    [InlineData(EmailInquiryAssemblyStatus.Extracting, EmailInquiryAssemblyStatus.ReadyForAssembly)]
    [InlineData(EmailInquiryAssemblyStatus.ReadyForAssembly, EmailInquiryAssemblyStatus.Assembled)]
    [InlineData(EmailInquiryAssemblyStatus.ReadyForAssembly, EmailInquiryAssemblyStatus.NeedsReview)]
    public void Legal_transitions_are_permitted(
        EmailInquiryAssemblyStatus from, EmailInquiryAssemblyStatus to)
        => Assert.True(EmailInquiryAssemblyStateMachine.CanTransition(from, to));

    [Theory]
    [InlineData(EmailInquiryAssemblyStatus.Captured, EmailInquiryAssemblyStatus.Assembled)]
    [InlineData(EmailInquiryAssemblyStatus.Inspecting, EmailInquiryAssemblyStatus.Assembled)]
    [InlineData(EmailInquiryAssemblyStatus.Extracting, EmailInquiryAssemblyStatus.Assembled)]
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
    [InlineData(EmailInquiryAssemblyStatus.ReadyForAssembly)]
    public void Recovery_re_enters_the_same_assembly(EmailInquiryAssemblyStatus to)
        => Assert.True(EmailInquiryAssemblyStateMachine.CanTransition(
            EmailInquiryAssemblyStatus.FailedRecoverable, to));

    [Fact]
    public void An_amendment_re_opens_an_assembled_message_rather_than_forking_one()
        => Assert.True(EmailInquiryAssemblyStateMachine.CanTransition(
            EmailInquiryAssemblyStatus.Assembled, EmailInquiryAssemblyStatus.Extracting));

    [Fact]
    public void Re_declaring_the_current_state_is_idempotent()
    {
        // Replays re-assert what they observe. Treating that as an illegal transition would
        // make every recovery pass throw.
        foreach (EmailInquiryAssemblyStatus status in Enum.GetValues<EmailInquiryAssemblyStatus>())
            Assert.True(EmailInquiryAssemblyStateMachine.CanTransition(status, status));
    }
}
