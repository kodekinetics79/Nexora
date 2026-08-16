using ERP_RFQ_Automation.Ingestion.Assembly;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// A genuine RFQ does not stop being an inquiry because one attachment could not be read.
///
/// <para><b>The defect these tests close.</b> <c>Evaluate</c> used to send the whole message to
/// <see cref="EmailInquiryAssemblyStatus.NeedsReview"/> when any component was
/// <see cref="EmailInquiryComponentStatus.Skipped"/>, "even when the body extracted perfectly".
/// <c>Skipped</c> is reached by any attachment outside <c>DocumentIntakeAllowList</c> — which
/// excludes <c>.p7s</c> (an S/MIME signature, attached by the SENDER'S gateway to every outgoing
/// message), <c>.dwg</c>/<c>.step</c> (drawings, routine on an industrial RFQ) and <c>.zip</c>.</para>
///
/// <para>So a buyer whose mail is signed produced NO Lead, ever. And because
/// <c>NeedsReview</c> cannot reach <c>ReadyForAssembly</c>, the recovery sweep never revisited
/// the message — the only escape was a human noticing. Proven in isolation first; see
/// <c>rfq-email-lead-bridge-proof</c>.</para>
/// </summary>
public sealed class EmailInquiryUnreadAttachmentTests
{
    private static EmailInquiryAssemblyEvaluation Evaluate(params EmailInquiryComponentStatus[] statuses)
        => EmailInquiryAssemblyStateMachine.Evaluate(statuses.Length, statuses);

    [Fact]
    public void A_readable_body_plus_an_unreadable_attachment_becomes_an_inquiry()
    {
        // The exact shape that produced zero Leads: the RFQ is in the body and reads
        // perfectly, and the sender's gateway attached a signature this system cannot open.
        var verdict = Evaluate(
            EmailInquiryComponentStatus.Completed,
            EmailInquiryComponentStatus.Skipped);

        Assert.Equal(EmailInquiryAssemblyStatus.ReadyForAssembly, verdict.Status);
        Assert.Equal(1, verdict.CapturedComponentCount);

        // The loss is recorded rather than hidden — that is what makes dropping the blockade
        // honest rather than merely permissive.
        Assert.Contains("could not be read", verdict.Reason);
    }

    [Fact]
    public void A_read_attachment_alongside_an_unread_one_still_becomes_an_inquiry()
    {
        // Body + priced BoQ + a drawing we cannot open. The BoQ's lines are real money and
        // used to be discarded along with everything else.
        var verdict = Evaluate(
            EmailInquiryComponentStatus.Completed,
            EmailInquiryComponentStatus.Completed,
            EmailInquiryComponentStatus.Skipped);

        Assert.Equal(EmailInquiryAssemblyStatus.ReadyForAssembly, verdict.Status);
        Assert.Equal(2, verdict.CapturedComponentCount);
    }

    [Fact]
    public void A_message_where_NOTHING_could_be_read_still_goes_to_a_human()
    {
        // The boundary. No captured content at all means there is no inquiry to build — only
        // a message whose entire commercial content is in files we cannot open.
        var verdict = Evaluate(
            EmailInquiryComponentStatus.Skipped,
            EmailInquiryComponentStatus.Skipped);

        Assert.Equal(EmailInquiryAssemblyStatus.NeedsReview, verdict.Status);
        Assert.Equal(0, verdict.CapturedComponentCount);
        Assert.Contains("No part of this message could be read", verdict.Reason);
    }

    [Fact]
    public void A_clean_message_is_unaffected_and_carries_no_review_reason()
    {
        var verdict = Evaluate(
            EmailInquiryComponentStatus.Completed,
            EmailInquiryComponentStatus.Completed);

        Assert.Equal(EmailInquiryAssemblyStatus.ReadyForAssembly, verdict.Status);
        Assert.Null(verdict.Reason);
    }

    [Fact]
    public void A_security_refusal_still_outranks_everything()
    {
        // Unchanged, and it must stay that way: malware is not "price it with a caveat".
        var verdict = Evaluate(
            EmailInquiryComponentStatus.Completed,
            EmailInquiryComponentStatus.RefusedSecurity);

        Assert.Equal(EmailInquiryAssemblyStatus.RejectedSecurity, verdict.Status);
    }

    [Fact]
    public void An_infrastructure_hold_still_holds_the_whole_message()
    {
        // Also unchanged. A part we could not STORE is not a part we decided not to read —
        // it is a part that is still coming, and finalizing now would under-quote the buyer.
        var verdict = Evaluate(
            EmailInquiryComponentStatus.Completed,
            EmailInquiryComponentStatus.FailedRecoverable);

        Assert.Equal(EmailInquiryAssemblyStatus.FailedRecoverable, verdict.Status);
    }

    [Fact]
    public void A_message_that_is_still_in_flight_is_never_finalized_early()
    {
        Assert.Equal(EmailInquiryAssemblyStatus.Extracting,
            Evaluate(EmailInquiryComponentStatus.Completed,
                     EmailInquiryComponentStatus.Skipped,
                     EmailInquiryComponentStatus.Extracting).Status);
    }

    [Fact]
    public void A_missing_component_row_still_holds_the_message_for_replay()
    {
        // The barrier must not be satisfiable by failing to record something.
        var verdict = EmailInquiryAssemblyStateMachine.Evaluate(
            expectedComponentCount: 3,
            componentStatuses: new[]
            {
                EmailInquiryComponentStatus.Completed,
                EmailInquiryComponentStatus.Skipped
            });

        Assert.Equal(EmailInquiryAssemblyStatus.FailedRecoverable, verdict.Status);
    }

    [Fact]
    public void An_inquiry_carrying_unread_parts_can_reach_a_Lead_without_a_human()
    {
        // The half of the defect that made it permanent: NeedsReview cannot reach
        // ReadyForAssembly, so the recovery sweep never looked at a stranded message again.
        // Now that such a message IS ReadyForAssembly, the sweep's normal path applies.
        Assert.True(EmailInquiryAssemblyStateMachine.CanTransition(
            EmailInquiryAssemblyStatus.ReadyForAssembly,
            EmailInquiryAssemblyStatus.Assembled));
    }
}
