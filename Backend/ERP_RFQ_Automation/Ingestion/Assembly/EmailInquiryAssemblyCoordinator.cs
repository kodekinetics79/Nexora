using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ERP_RFQ_Automation.Ingestion.Assembly;

public interface IEmailInquiryAssemblyCoordinator
{
    Task<EmailInquiryComponent?> FindComponentAsync(
        long businessUnitId, string componentKey, CancellationToken ct = default);

    Task RecordComponentQueuedAsync(
        long businessUnitId, string componentKey, long extractionJobId, CancellationToken ct = default);

    Task RecordComponentOutcomeAsync(
        long businessUnitId, string componentKey, EmailInquiryComponentStatus status,
        string? reasonCode, string? reasonDetail, long? sourceDocumentOccurrenceId,
        CancellationToken ct = default);

    Task<EmailInquiryAssemblyEvaluation> ReevaluateAsync(
        long assemblyId, long businessUnitId, CancellationToken ct = default);

    Task MarkNoInquiryAsync(EmailInquiryAssembly assembly, string reason, CancellationToken ct = default);
}

/// <summary>
/// The one writer of assembly and component state after capture.
///
/// <para>It exists so that "what is this message now?" is answered by
/// <see cref="EmailInquiryAssemblyStateMachine"/> from the durable component rows, in one
/// place, every time — rather than inferred by whichever worker happened to finish last. The
/// previous pipeline had no such place, which is why a body finishing before its attachment
/// silently became a complete commercial fact.</para>
/// </summary>
public sealed class EmailInquiryAssemblyCoordinator : IEmailInquiryAssemblyCoordinator
{
    private readonly ErpRfqAutomationContext _context;
    private readonly ILogger<EmailInquiryAssemblyCoordinator> _logger;

    public EmailInquiryAssemblyCoordinator(
        ErpRfqAutomationContext context, ILogger<EmailInquiryAssemblyCoordinator> logger)
    {
        _context = context;
        _logger = logger;
    }

    public Task<EmailInquiryComponent?> FindComponentAsync(
        long businessUnitId, string componentKey, CancellationToken ct = default)
        => _context.EmailInquiryComponents
            .FirstOrDefaultAsync(x => x.BusinessUnitId == businessUnitId
                                      && x.ComponentKey == componentKey, ct)!;

    public async Task RecordComponentQueuedAsync(
        long businessUnitId, string componentKey, long extractionJobId, CancellationToken ct = default)
    {
        var component = await FindComponentAsync(businessUnitId, componentKey, ct);
        if (component is null) return;

        component.ExtractionJobId = extractionJobId;
        // Pending -> Extracting only. A component that already reached a terminal state is left
        // alone: a replayed enqueue must not walk a finished part backwards and reopen a
        // barrier that has already been satisfied.
        if (component.Status == EmailInquiryComponentStatus.Pending)
            component.Status = EmailInquiryComponentStatus.Extracting;
        component.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await _context.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Records what became of one component and re-evaluates the message.
    ///
    /// <para>Idempotent by construction: re-reporting the same outcome is a no-op, and a
    /// terminal component is never moved by a later report. That is what makes a replay after a
    /// crash safe — the worker cannot know whether its previous attempt committed.</para>
    /// </summary>
    public async Task RecordComponentOutcomeAsync(
        long businessUnitId, string componentKey, EmailInquiryComponentStatus status,
        string? reasonCode, string? reasonDetail, long? sourceDocumentOccurrenceId,
        CancellationToken ct = default)
    {
        var component = await FindComponentAsync(businessUnitId, componentKey, ct);
        if (component is null)
        {
            _logger.LogWarning(
                "No email inquiry component {ComponentKey} for business unit {BusinessUnitId}; "
                + "the outcome could not be recorded against a message.",
                componentKey, businessUnitId);
            return;
        }

        // A refusal on security grounds is the one outcome allowed to overwrite a terminal
        // component: the scanner's verdict outranks a result produced before it was known.
        if (component.IsTerminal && status != EmailInquiryComponentStatus.RefusedSecurity)
            return;

        component.Status = status;
        component.ReasonCode = reasonCode;
        component.ReasonDetail = Truncate(reasonDetail, 1000);
        if (sourceDocumentOccurrenceId.HasValue)
            component.SourceDocumentOccurrenceId = sourceDocumentOccurrenceId;
        component.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await _context.SaveChangesAsync(ct);

        await ReevaluateAsync(component.AssemblyId, businessUnitId, ct);
    }

    /// <summary>
    /// Re-reads every component of the message and applies the state machine's verdict.
    ///
    /// <para>Deliberately recomputed from the rows rather than accumulated in a counter: a
    /// counter can only be right if every increment happened exactly once, which is precisely
    /// what a crash, a retry or two concurrent workers cannot promise.</para>
    /// </summary>
    public async Task<EmailInquiryAssemblyEvaluation> ReevaluateAsync(
        long assemblyId, long businessUnitId, CancellationToken ct = default)
    {
        var assembly = await _context.EmailInquiryAssemblies
            .Include(x => x.Components)
            .FirstOrDefaultAsync(x => x.Id == assemblyId && x.BusinessUnitId == businessUnitId, ct)
            ?? throw new InvalidOperationException(
                $"Email inquiry assembly {assemblyId} was not found for this tenant.");

        var statuses = assembly.Components.Select(c => c.Status).ToList();
        var evaluation = EmailInquiryAssemblyStateMachine.Evaluate(
            assembly.ExpectedComponentCount, statuses);

        assembly.CompletedComponentCount = evaluation.CompletedComponentCount;

        // An illegal transition is a defect in the caller, but throwing here would strand the
        // message mid-pipeline with no record of why. It is logged loudly and the status is
        // left where it was, so the assembly stays visible and recoverable rather than lost.
        if (EmailInquiryAssemblyStateMachine.CanTransition(assembly.Status, evaluation.Status))
        {
            assembly.Status = evaluation.Status;
            assembly.StatusReason = Truncate(evaluation.Reason, 1000);
        }
        else
        {
            _logger.LogError(
                "Email inquiry assembly {AssemblyId} evaluated to {Evaluated} but is {Current}, "
                + "which is not a legal transition. The status is unchanged and the message is held.",
                assembly.Id, evaluation.Status, assembly.Status);
        }

        assembly.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await _context.SaveChangesAsync(ct);
        return evaluation;
    }

    public async Task MarkNoInquiryAsync(
        EmailInquiryAssembly assembly, string reason, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        if (!EmailInquiryAssemblyStateMachine.CanTransition(
                assembly.Status, EmailInquiryAssemblyStatus.NoInquiry))
            return;

        assembly.Status = EmailInquiryAssemblyStatus.NoInquiry;
        assembly.StatusReason = Truncate(reason, 1000);
        assembly.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await _context.SaveChangesAsync(ct);
    }

    private static string? Truncate(string? value, int max)
        => value is null || value.Length <= max ? value : value[..max];
}
