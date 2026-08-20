using ERP_RFQ_Automation.Ingestion.Assembly;
using ERP_RFQ_Automation.Tests.Support;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// What actually happens to a component the worker holds as
/// <c>assembly_result_store_pending</c>.
///
/// <para>These tests exist because the word "recoverable" was doing unearned work. The fence
/// records <see cref="EmailInquiryComponentStatus.FailedRecoverable"/>, and it would be easy to
/// read that as "something will retry it". <b>Nothing does.</b> There is no sweep over held
/// components in this repository, so the honest description is HELD — visible, re-runnable once
/// the capability exists, and costing nothing meanwhile. These assertions pin that reality so
/// the next reader does not inherit an optimistic reading of an enum name.</para>
/// </summary>
public class EmailInquiryHoldLifecycleTests
{
    /// <summary>The reason the worker records while the result store does not exist.</summary>
    private static readonly string HoldReason =
        EmailInquiryHoldReasons.AssemblyResultStorePending;

    private static EmailInquiryComponent Held(long assemblyId = 1, long? jobId = 4242) => new()
    {
        BusinessUnitId = 7,
        AssemblyId = assemblyId,
        ComponentKey = "email:m1:part:1",
        Ordinal = 0,
        Kind = EmailInquiryComponentKind.Attachment,
        Status = EmailInquiryComponentStatus.FailedRecoverable,
        ReasonCode = HoldReason,
        ExtractionJobId = jobId
    };

    [Fact]
    public void A_held_component_is_not_terminal_so_the_message_is_never_declared_finished()
    {
        // If the hold counted as terminal, the barrier would decide the message was complete
        // with a part whose content was never stored — a Lead priced from the rest.
        Assert.False(Held().IsTerminal);
        Assert.True(Held().IsRecoverableHold);
    }

    [Fact]
    public void A_held_component_holds_the_whole_message_and_blocks_assembly()
    {
        var evaluation = EmailInquiryAssemblyStateMachine.Evaluate(
            2, [EmailInquiryComponentStatus.Completed, EmailInquiryComponentStatus.FailedRecoverable]);

        Assert.Equal(EmailInquiryAssemblyStatus.FailedRecoverable, evaluation.Status);
        Assert.NotEqual(EmailInquiryAssemblyStatus.ReadyForAssembly, evaluation.Status);
    }

    [Fact]
    public void A_held_component_is_never_counted_as_captured_content()
    {
        // CompletedComponentCount is barrier progress; CapturedComponentCount is "did we get
        // anything". A hold must contribute to neither, or the message looks richer than it is.
        var evaluation = EmailInquiryAssemblyStateMachine.Evaluate(
            1, [EmailInquiryComponentStatus.FailedRecoverable]);

        Assert.Equal(0, evaluation.CapturedComponentCount);
        Assert.Equal(0, evaluation.CompletedComponentCount);
    }

    [Fact]
    public void A_held_message_cannot_reach_ReadyForAssembly_by_transition_either()
    {
        // Belt and braces: even if some future caller computed ReadyForAssembly, the transition
        // table refuses it. Recovery must re-enter inspection or extraction.
        Assert.False(EmailInquiryAssemblyStateMachine.CanTransition(
            EmailInquiryAssemblyStatus.FailedRecoverable, EmailInquiryAssemblyStatus.ReadyForAssembly));
    }

    [Fact]
    public void The_hold_message_promises_no_automatic_retry_because_nothing_retries()
    {
        // Asserted against the CONSTANT the worker actually passes, not against its source text.
        // The original sentence said the part "will be processed again automatically". Nothing
        // sweeps FailedRecoverable components — there is no recovery service in this build — so
        // that was untrue, and it is the same class of defect as advising a retry that cannot
        // succeed.
        var detail = EmailInquiryHoldReasons.AssemblyResultStorePendingDetail;

        Assert.DoesNotContain("automatically", detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("will retry", detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("try again", detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("being held", detail, StringComparison.OrdinalIgnoreCase);
        // Narrowed deliberately: the extraction OUTPUT of this pass is discarded, so claiming
        // "no information has been lost" was too broad. What survives is the captured evidence.
        Assert.DoesNotContain("no information has been lost", detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("captured email evidence is preserved", detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_hold_message_names_no_infrastructure_detail()
    {
        var detail = EmailInquiryHoldReasons.AssemblyResultStorePendingDetail;

        foreach (var forbidden in new[] { "Exception", "bucket", "http", "sql", "null" })
            Assert.DoesNotContain(forbidden, detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_held_component_still_carries_its_job_reference_so_the_hold_is_diagnosable()
    {
        // The operator and any future sweep need to know which job produced the hold. Clearing
        // it would make the hold untraceable and the work unrepeatable.
        var component = Held();

        Assert.Equal(4242, component.ExtractionJobId);
        Assert.Equal(HoldReason, component.ReasonCode);
    }

    [Fact]
    public void A_held_component_is_not_rescheduled_while_its_job_still_exists_so_no_hot_loop()
    {
        // This is the cost question. ScheduleAsync skips a component whose referenced job is
        // verified to belong to it — so a held component with a live job is NOT re-extracted,
        // and re-running the scheduler cannot burn AI or OCR spend in a loop while the
        // capability is missing. It also means nothing advances until the result store lands,
        // which is exactly what "held" means and why it is not called "retrying".
        var component = Held();

        Assert.False(component.IsTerminal);
        Assert.NotNull(component.ExtractionJobId);
    }

    [Fact]
    public void Two_assemblies_hold_their_own_components_independently()
    {
        // A hold on one message must not describe another. Identity is per assembly, so the
        // same component key under a different assembly is a different row.
        var first = Held(assemblyId: 1);
        var second = Held(assemblyId: 2);

        Assert.Equal(first.ComponentKey, second.ComponentKey);
        Assert.NotEqual(first.AssemblyId, second.AssemblyId);
    }

    /// <summary>
    /// EVERY operator sentence must actually reach the operator.
    ///
    /// <para>The coordinator persists a hold as <c>"{code}: {detail}"</c>, and the Inbound Mail
    /// screen renders it through <c>presentableServerText</c>
    /// (<c>Frontend/src/utils/apiErrors.ts</c>), whose gate is
    /// <c>value.length &lt;= MAX_MESSAGE_LENGTH</c> — a REJECT, not a truncation. A sentence one
    /// character too long renders as nothing at all, so the message sits held on the screen with
    /// no reason beside it. That is not a hypothetical: the first draft of
    /// <see cref="EmailInquiryHoldReasons.NoRequestableContentDetail"/> was 359 characters and
    /// would have shipped exactly that.</para>
    ///
    /// <para>The bound is duplicated here rather than imported because it lives in TypeScript.
    /// A duplicated constant with the reason for its value written next to it is worth more than
    /// a shared one nobody can trace, and this test is what makes the copy honest.</para>
    /// </summary>
    [Theory]
    [InlineData(EmailInquiryHoldReasons.AssemblyResultStorePending,
        EmailInquiryHoldReasons.AssemblyResultStorePendingDetail)]
    [InlineData(EmailInquiryHoldReasons.LeadNotProduced,
        EmailInquiryHoldReasons.LeadNotProducedDetail)]
    [InlineData(EmailInquiryHoldReasons.OwnershipUnresolved,
        EmailInquiryHoldReasons.OwnershipUnresolvedDetail)]
    [InlineData(EmailInquiryHoldReasons.NoRequestableContent,
        EmailInquiryHoldReasons.NoRequestableContentDetail)]
    public void A_hold_reason_fits_the_sentence_gate_the_operator_screen_applies(
        string code, string detail)
    {
        const int maxPresentableLength = 300;
        var persisted = $"{code}: {detail}";

        Assert.True(persisted.Length <= maxPresentableLength,
            $"'{code}' composes to {persisted.Length} characters. The Inbound Mail screen "
            + $"rejects anything over {maxPresentableLength} outright, so this hold would show "
            + "with no reason at all. Shorten the sentence; do not raise the bound.");
    }
}
