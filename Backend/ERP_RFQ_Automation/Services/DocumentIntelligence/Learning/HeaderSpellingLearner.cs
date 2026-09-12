using System.Text.Json;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ERP_RFQ_Automation.Services.DocumentIntelligence.Learning;

public interface IHeaderSpellingLearner
{
    /// <summary>
    /// Turns an approved review into header knowledge: when the approved header value is one an
    /// unrecognised label on the source document carried, the label's spelling is learned for
    /// the tenant. Never flushes — the caller
    /// commits it with the review that taught it, or not at all.
    /// </summary>
    Task<HeaderSpellingLearningResult> LearnFromReviewAsync(
        long businessUnitId, Lead lead, long? reviewAuditId, CancellationToken ct = default);
}

public sealed record HeaderSpellingLearningResult(int Learned, int Reinforced, IReadOnlyList<string> SkipReasons)
{
    public static readonly HeaderSpellingLearningResult None = new(0, 0, []);
}

/// <summary>
/// <para><b>What counts as evidence.</b> A value the reviewer APPROVED — typed in, or confirmed
/// after the anchored header completion read it from the document text — when exactly one
/// unrecognised label on the document (a column heading kept in the line's ExtraFields, or a
/// "Label: value" pair kept with the canonical inquiry) carried the same value. A date matches
/// when it parses to the same day; a reference matches on the normalised text. A reviewer who
/// typed a date from the email rather than the document teaches nothing, because no label
/// carried it; two labels carrying the same value teach nothing either, because which of them
/// meant the field cannot be told.</para>
///
/// <para><b>Why a confirmed completion teaches too.</b> The model read "Cut-off: 12/12/2026"
/// once and the reviewer approved it. Learning the label now means the next file from that
/// customer is read deterministically, with no model call at all — which is the point of the
/// whole loop: the model is the bridge to a spelling, not the way that spelling is read forever.</para>
///
/// <para><b>What can never be learned.</b> A spelling the built-in vocabulary already knows
/// (it was not matched for a reason, such as the "Date:" tail guard), a label that matched two
/// fields in one review, and a spelling another review already taught for a different field —
/// the first confirmation wins, as in <see cref="RfqHeaderVocabulary.WithLearned"/>.</para>
/// </summary>
public sealed class HeaderSpellingLearner : IHeaderSpellingLearner
{
    public const string SkipNoCandidates = "noUnrecognisedLabels";
    public const string SkipConflict = "spellingTaughtForAnotherField";
    public const string SkipBuiltin = "builtinSpelling";

    private readonly ErpRfqAutomationContext _db;
    private readonly ILogger<HeaderSpellingLearner>? _log;

    public HeaderSpellingLearner(ErpRfqAutomationContext db, ILogger<HeaderSpellingLearner>? log = null)
    {
        _db = db;
        _log = log;
    }

    public async Task<HeaderSpellingLearningResult> LearnFromReviewAsync(
        long businessUnitId, Lead lead, long? reviewAuditId, CancellationToken ct = default)
    {
        var candidates = await CandidateLabelsAsync(businessUnitId, lead, ct);
        if (candidates.Count == 0)
            return new HeaderSpellingLearningResult(0, 0, [SkipNoCandidates]);
        var readFromALabel = await FieldsReadFromARecognisedLabelAsync(businessUnitId, lead, ct);

        var matches = new Dictionary<string, (string Field, string Label)>(StringComparer.Ordinal);
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);

        void Consider(string field, Func<string, bool> valueMatches)
        {
            var carrying = candidates
                .Where(pair => valueMatches(pair.Value))
                .Select(pair => (Label: pair.Key, Spelling: RfqHeaderVocabulary.Normalize(pair.Key)))
                .Where(pair => pair.Spelling.Length > 0)
                .ToList();
            // Two different labels carrying the same value: which one meant the field cannot be told.
            if (carrying.Select(pair => pair.Spelling).Distinct(StringComparer.Ordinal).Count() != 1)
                return;
            var (label, spelling) = carrying[0];
            if (matches.TryGetValue(spelling, out var existing) && existing.Field != field)
                ambiguous.Add(spelling);
            else
                matches[spelling] = (field, label);
        }

        // A field the parser read from a label it already knew teaches nothing: the approved
        // value merely coincides with some other label's value ("Clarification Deadline" on the
        // one tender where it equals the closing date), and learning it would re-route the
        // field on every later document from that customer.
        if (!string.IsNullOrWhiteSpace(lead.Rfqno) && !readFromALabel.Contains(RfqSpreadsheetFields.RfqNo))
        {
            var wanted = RfqHeaderVocabulary.Normalize(lead.Rfqno);
            Consider(RfqSpreadsheetFields.RfqNo, value => RfqHeaderVocabulary.Normalize(value) == wanted);
        }

        if (lead.BidClosingDate is { } closing && !readFromALabel.Contains(RfqSpreadsheetFields.BidClosingDate))
            Consider(RfqSpreadsheetFields.BidClosingDate, value => RfqDateParser.Read(value).Value?.Date == closing.Date);

        var skips = new List<string>();
        var learned = 0;
        var reinforced = 0;
        var now = DateTime.UtcNow;

        foreach (var (spelling, (field, label)) in matches)
        {
            if (ambiguous.Contains(spelling)) continue;
            if (RfqHeaderVocabulary.IsBuiltin(spelling))
            {
                skips.Add(SkipBuiltin);
                continue;
            }

            var existing = await _db.Set<HeaderSpelling>()
                .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Spelling == spelling, ct);
            if (existing is null)
            {
                _db.Set<HeaderSpelling>().Add(new HeaderSpelling
                {
                    BusinessUnitId = businessUnitId,
                    Spelling = spelling,
                    OriginalLabel = label.Length <= 200 ? label : label[..200],
                    Field = field,
                    LearnedFromLeadId = lead.Id,
                    LearnedFromReviewAuditId = reviewAuditId,
                    ObservationCount = 1,
                    CreatedOn = now,
                    LastObservedOn = now,
                });
                learned++;
            }
            else if (existing.Field == field)
            {
                existing.ObservationCount++;
                existing.LastObservedOn = now;
                reinforced++;
            }
            else
            {
                skips.Add(SkipConflict);
            }
        }

        if (learned + reinforced > 0)
            _log?.LogInformation(
                "Header spellings for tenant {Tenant} from lead {LeadId}: {Learned} learned, {Reinforced} reinforced.",
                businessUnitId, lead.Id, learned, reinforced);

        return new HeaderSpellingLearningResult(learned, reinforced, skips);
    }

    /// <summary>
    /// Every unrecognised label the source document carried, with its value: document-level
    /// pairs from the canonical inquiry, then column headings from the first current line.
    /// </summary>
    private async Task<Dictionary<string, string>> CandidateLabelsAsync(long businessUnitId, Lead lead, CancellationToken ct)
    {
        var candidates = new Dictionary<string, string>(StringComparer.Ordinal);

        // The evidence ledger is PostgreSQL-only; a provider without it simply has no
        // document-level labels to offer.
        if (_db.Model.FindEntityType(typeof(CanonicalInquiry)) is not null)
        {
            var inquiries = await _db.Set<CanonicalInquiry>()
                .AsNoTracking()
                .Where(x => x.BusinessUnitId == businessUnitId && x.LeadId == lead.Id && x.UnmappedHeadersJson != null)
                .OrderBy(x => x.Id)
                .Select(x => x.UnmappedHeadersJson)
                .ToListAsync(ct);
            foreach (var json in inquiries)
                foreach (var (label, value) in ParseDictionary(json))
                    candidates.TryAdd(label, value);
        }

        var firstLine = lead.LeadItems
            .Where(item => item.IsCurrentRevisionProjection && !string.IsNullOrWhiteSpace(item.ExtraFields))
            .OrderBy(item => item.Id)
            .FirstOrDefault();
        if (firstLine is not null)
            foreach (var (label, value) in ParseDictionary(firstLine.ExtraFields))
                candidates.TryAdd(label, value);

        return candidates;
    }

    /// <summary>
    /// Header fields the extraction read deterministically, from a label the vocabulary already
    /// knew. Evidence written by the anchored model completion carries its own marker and does
    /// not count: that reading is exactly what a reviewer's confirmation should turn into a
    /// learned label.
    /// </summary>
    private async Task<HashSet<string>> FieldsReadFromARecognisedLabelAsync(long businessUnitId, Lead lead, CancellationToken ct)
    {
        var read = new HashSet<string>(StringComparer.Ordinal);
        if (_db.Model.FindEntityType(typeof(FieldEvidence)) is null || _db.Model.FindEntityType(typeof(CanonicalInquiry)) is null)
            return read;

        var rows = await (from field in _db.Set<FieldEvidence>().AsNoTracking()
                          join inquiry in _db.Set<CanonicalInquiry>().AsNoTracking() on field.InquiryId equals inquiry.Id
                          where inquiry.BusinessUnitId == businessUnitId && inquiry.LeadId == lead.Id
                                && field.LineItemId == null
                                && (field.FieldName == "RfqNo" || field.FieldName == "BidClosingDate")
                                && field.NormalizedValue != null
                          select new { field.FieldName, field.TransformationsJson })
            .ToListAsync(ct);
        foreach (var row in rows)
        {
            if (row.TransformationsJson is not null && row.TransformationsJson.Contains("ai_header_completion", StringComparison.Ordinal))
                continue;
            read.Add(row.FieldName == "RfqNo" ? RfqSpreadsheetFields.RfqNo : RfqSpreadsheetFields.BidClosingDate);
        }
        return read;
    }

    private static Dictionary<string, string> ParseDictionary(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, string>();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

}
