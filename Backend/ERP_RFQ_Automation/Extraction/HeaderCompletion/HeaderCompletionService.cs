using System.Text;
using System.Text.RegularExpressions;
using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.Services.DocumentIntelligence;
using ERP_RFQ_Automation.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace ERP_RFQ_Automation.Extraction.HeaderCompletion;

public interface IHeaderCompletionService
{
    /// <summary>
    /// True when the structured document states none of the inquiry-level facts the completion
    /// can supply and there is header text to read — the cheap check a caller makes before
    /// taking a model slot.
    /// </summary>
    bool HasGap(DocumentExtractionInput input);

    /// <summary>
    /// Fills the inquiry-level fields the vocabulary could not find, on every structured row,
    /// from the document's own header text — only with values the model quoted verbatim from
    /// that text. Never throws for a refused, unavailable or unhelpful model; the rows are then
    /// left exactly as the deterministic read left them.
    /// </summary>
    Task<HeaderCompletionOutcome> CompleteAsync(DocumentExtractionInput input, CancellationToken ct = default);
}

/// <param name="CompletedFields">The <see cref="RfqSpreadsheetFields"/> names filled in.</param>
/// <param name="RejectedFields">Fields the model offered but whose span the text did not contain.</param>
/// <param name="Skipped">Why nothing was attempted, when nothing was.</param>
public sealed record HeaderCompletionOutcome(
    IReadOnlyList<string> CompletedFields,
    IReadOnlyList<string> RejectedFields,
    string? Skipped)
{
    public static HeaderCompletionOutcome None(string reason) => new([], [], reason);
}

/// <summary>
/// Reads a structured document's header text with the model when the vocabulary could not.
///
/// <para><b>Why this exists.</b> A Word table or a workbook is read deterministically, line by
/// line, without a model — that is right and it stays. But the closing date sits in a label the
/// vocabulary may not know ("BCD: 10/8/2026"), or in a paragraph of prose, and a spelling list
/// can only ever hold what it has seen. Before this, such a document arrived with no closing date
/// and the rep typed it in from the document, every time, for every file from that customer.
/// The header text is a few hundred characters; reading it costs almost nothing and never
/// touches a line item.</para>
///
/// <para><b>Why the model is never trusted on its own.</b> Every value the model returns must
/// come with a verbatim span, the span must occur in the text that was sent, the span must
/// contain the value (a date must parse from the span to the same day), and the value goes onto
/// the row with its confidence capped below a deterministic read and a note saying where it
/// came from. A model that invents a date produces a span the text does not contain, and the
/// date is dropped. The buyer's NAME is deliberately not completed: on this path the customer is
/// resolved from the document's identity evidence, and an invented client is worse than an
/// unresolved one.</para>
///
/// <para><b>Why a refusal is not a failure.</b> A tenant not authorised for an external provider,
/// a provider that is down, a policy that denies the call: each leaves the document exactly as
/// it was, on the deterministic path, with the closing date flagged for the reviewer as before.
/// Completion is an improvement, never a dependency.</para>
/// </summary>
public sealed class HeaderCompletionService : IHeaderCompletionService
{
    /// <summary>A model reading of the document's own text, anchored and parsed, but not a labelled cell.</summary>
    public const decimal CompletionConfidence = 0.85m;
    public const string ProvenancePrefix = "ai_header_completion";
    private const int MaxNarrativeChars = 4_000;

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private readonly ILLMService _llm;
    private readonly IAiExternalProviderTrust? _externalProviderTrust;
    private readonly ILogger<HeaderCompletionService> _log;

    public HeaderCompletionService(
        ILLMService llm,
        ILogger<HeaderCompletionService> log,
        IAiExternalProviderTrust? externalProviderTrust = null)
    {
        _llm = llm;
        _log = log;
        _externalProviderTrust = externalProviderTrust;
    }

    /// <summary>The fields completion may fill. The buyer's name is deliberately absent (see class remarks).</summary>
    public static readonly IReadOnlyList<string> CompletableFields =
    [
        RfqSpreadsheetFields.RfqNo,
        RfqSpreadsheetFields.BidClosingDate,
        RfqSpreadsheetFields.RequiredDeliveryDate,
        RfqSpreadsheetFields.DeliveryLocation,
        RfqSpreadsheetFields.AgreementReference,
    ];

    public bool HasGap(DocumentExtractionInput input)
        => input.IsStructured
           && input.StructuredRows is { Count: > 0 } rows
           && MissingFields(rows).Count > 0
           && ComposeHeaderText(rows[0], input.DocumentNarrative).Length > 0;

    public async Task<HeaderCompletionOutcome> CompleteAsync(DocumentExtractionInput input, CancellationToken ct = default)
    {
        if (!input.IsStructured || input.StructuredRows is not { Count: > 0 } rows)
            return HeaderCompletionOutcome.None("notStructured");

        var missing = MissingFields(rows);
        if (missing.Count == 0)
            return HeaderCompletionOutcome.None("nothingMissing");

        var text = ComposeHeaderText(rows[0], input.DocumentNarrative);
        if (text.Length == 0)
            return HeaderCompletionOutcome.None("noHeaderText");

        if (_llm.ProviderClass == AiProviderClass.External)
        {
            var descriptor = _llm.ProviderDescriptor;
            var decision = _externalProviderTrust is null
                ? AiExternalProviderDecision.Deny(AiExternalProviderTrustReasons.GateUnavailable, descriptor)
                : await _externalProviderTrust.EvaluateAsync(
                    input.BusinessUnitId, descriptor, AiPurposes.RfqExtraction, unstructuredPayload: true, ct);
            if (!decision.Allowed)
            {
                _log.LogInformation(
                    "Header completion skipped for tenant {Tenant}: external provider not authorised ({Reason}). The document keeps its deterministic read.",
                    input.BusinessUnitId, decision.Reason);
                return HeaderCompletionOutcome.None($"providerNotAuthorised:{decision.Reason}");
            }
        }

        HeaderCompletionResult? result;
        try
        {
            result = await _llm.CompleteHeaderAsync(text, new AiCallContext(
                input.BusinessUnitId, AiPurposes.RfqExtraction,
                $"header:{input.SourceId}:a{input.AttemptNumber}", AiPromptVersions.HeaderCompletion,
                ExtractionJobId: input.ExtractionJobId,
                SourceDocumentOccurrenceId: input.SourceDocumentOccurrenceId), ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (AiPolicyDeniedException ex)
        {
            _log.LogInformation("Header completion denied by AI policy for tenant {Tenant}: {Code}.", input.BusinessUnitId, ex.Code);
            return HeaderCompletionOutcome.None($"policyDenied:{ex.Code}");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Header completion failed for {Document}; the document keeps its deterministic read.", input.SourceDocumentName);
            return HeaderCompletionOutcome.None("providerFailed");
        }

        if (result is null)
            return HeaderCompletionOutcome.None("noResult");

        var completed = new List<string>();
        var rejected = new List<string>();
        foreach (var field in missing)
        {
            var (value, span) = Offered(result, field);
            if (string.IsNullOrWhiteSpace(value))
                continue;

            var anchored = Anchor(field, value, span, text);
            if (anchored is null)
            {
                rejected.Add(field);
                _log.LogInformation(
                    "Header completion offered {Field}=\"{Value}\" for {Document} but its span was not found verbatim in the text; dropped.",
                    field, value, input.SourceDocumentName);
                continue;
            }

            var note = $"{ProvenancePrefix}: read from the document text, anchored on \"{span!.Trim()}\"";
            foreach (var row in rows)
            {
                Set(row, field, anchored);
                row.FieldProvenance[field] = new RowFieldProvenance(CompletionConfidence, note);
                // The evidence ledger addresses every structured value by a cell. A value read
                // from the header text has no cell, so it is anchored on the document's header
                // row; the span in the provenance note says exactly which words it came from.
                row.FieldSourceAddresses[field] = $"row {Math.Max(1, row.HeaderRowNumber)}";
            }
            completed.Add(field);
        }

        if (completed.Count > 0)
            _log.LogInformation(
                "Header completion filled {Fields} on {Document} from its own header text.",
                string.Join(", ", completed), input.SourceDocumentName);

        return new HeaderCompletionOutcome(completed, rejected, null);
    }

    /// <summary>Fields no row states. A field any row states is the document's own business.</summary>
    internal static List<string> MissingFields(IReadOnlyList<RfqSpreadsheetRow> rows)
        => CompletableFields.Where(field => rows.All(row => string.IsNullOrWhiteSpace(Get(row, field)))).ToList();

    /// <summary>
    /// The text the model reads: the unrecognised document-level labels, then the unrecognised
    /// column headings of the first line with their values, then the prose outside the table.
    /// Never a line item.
    /// </summary>
    internal static string ComposeHeaderText(RfqSpreadsheetRow first, string? narrative)
    {
        var sb = new StringBuilder();
        foreach (var (label, value) in first.UnmappedHeaderLabels)
            sb.Append(label).Append(": ").AppendLine(value);
        foreach (var (label, value) in first.UnmappedColumns)
            sb.Append(label).Append(": ").AppendLine(value);
        if (!string.IsNullOrWhiteSpace(narrative))
        {
            var prose = narrative.Trim();
            sb.AppendLine(prose.Length <= MaxNarrativeChars ? prose : prose[..MaxNarrativeChars]);
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// The raw text to put on the row, or null when the offer cannot be trusted: the span must
    /// occur verbatim in the text (whitespace and case folded), and the value must come from the
    /// span — a date must parse from the span to the same day the model reported.
    /// </summary>
    internal static string? Anchor(string field, string value, string? span, string text)
    {
        if (string.IsNullOrWhiteSpace(span) || span.Length > 240)
            return null;
        if (!Fold(text).Contains(Fold(span), StringComparison.Ordinal))
            return null;

        if (field is RfqSpreadsheetFields.BidClosingDate or RfqSpreadsheetFields.RequiredDeliveryDate)
        {
            var fromSpan = RfqDateParser.Read(span);
            var fromModel = RfqDateParser.Read(value);
            if (fromSpan.Value is not { } inSpan || fromModel.Value is not { } reported || inSpan.Date != reported.Date)
                return null;
            // The span's own token, so the normaliser reads the buyer's wording (time of day,
            // day/month ambiguity) exactly as it would from a labelled cell.
            return span.Trim();
        }

        return Fold(span).Contains(Fold(value), StringComparison.Ordinal) ? value.Trim() : null;
    }

    private static string Fold(string s) => Whitespace.Replace(s, " ").Trim().ToLowerInvariant();

    private static (string? Value, string? Span) Offered(HeaderCompletionResult result, string field) => field switch
    {
        RfqSpreadsheetFields.RfqNo => (result.Rfqno, result.RfqnoSpan),
        RfqSpreadsheetFields.BidClosingDate => (result.BidClosingDate, result.BidClosingDateSpan),
        RfqSpreadsheetFields.RequiredDeliveryDate => (result.RequiredDeliveryDate, result.RequiredDeliveryDateSpan),
        RfqSpreadsheetFields.DeliveryLocation => (result.DeliveryLocation, result.DeliveryLocationSpan),
        RfqSpreadsheetFields.AgreementReference => (result.AgreementReference, result.AgreementReferenceSpan),
        _ => (null, null),
    };

    private static string? Get(RfqSpreadsheetRow row, string field) => field switch
    {
        RfqSpreadsheetFields.RfqNo => row.RfqNo,
        RfqSpreadsheetFields.BidClosingDate => row.BidClosingDate,
        RfqSpreadsheetFields.RequiredDeliveryDate => row.RequiredDeliveryDate,
        RfqSpreadsheetFields.DeliveryLocation => row.DeliveryLocation,
        RfqSpreadsheetFields.AgreementReference => row.AgreementReference,
        _ => null,
    };

    private static void Set(RfqSpreadsheetRow row, string field, string value)
    {
        switch (field)
        {
            case RfqSpreadsheetFields.RfqNo: row.RfqNo = value; break;
            case RfqSpreadsheetFields.BidClosingDate: row.BidClosingDate = value; break;
            case RfqSpreadsheetFields.RequiredDeliveryDate: row.RequiredDeliveryDate = value; break;
            case RfqSpreadsheetFields.DeliveryLocation: row.DeliveryLocation = value; break;
            case RfqSpreadsheetFields.AgreementReference: row.AgreementReference = value; break;
        }
    }
}
