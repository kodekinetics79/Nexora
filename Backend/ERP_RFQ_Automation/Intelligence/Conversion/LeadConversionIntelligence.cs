using System.Text.RegularExpressions;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using ERP_RFQ_Automation.ProductIntelligence;
using ERP_RFQ_Automation.Services.Uom;
using ERP_RFQ_Automation.LeadIdentity;

namespace ERP_RFQ_Automation.Intelligence.Conversion;

/// <summary>
/// Deterministic (no-LLM) conversion intelligence.
///
/// Product resolution per lead line, best rule wins. Each code rung compares the line's
/// identifier against BOTH catalogue number columns (PartNo, ModelNo), and loses 0.02 when the
/// two sides agree on the characters but not the punctuation:
///   1.00  ItemMaterialCode        == a catalogue number
///   0.95  ManufacturerPartNumber  == a catalogue number
///   0.93  AlternatePartNumber     == a catalogue number
///   0.92  a code-SHAPED ProductShortName / ProductShortDescription == a catalogue number
///   0.90  normalized-name equality vs Product.ProductName
///   0.40–0.85  contains / token-overlap vs Product name + description,
///              scaled by overlap ratio
///
/// Note the arithmetic: the similarity band tops out at 0.85 (0.40 + 0.45 * ratio, ratio &lt;= 1)
/// and the floor is 0.90, so NOTHING below the name-equality rung can ever auto-assign. That is
/// intentional — a line silently bound to the wrong product prices the wrong thing while looking
/// right — and it is why a catalogue number the resolver fails to recognise as one produces
/// zero auto-assignments rather than a few.
///
/// Candidates are fetched with cheap set-based queries (IN on part/model numbers,
/// ILIKE on the most significant name tokens) and scored in memory; catalogs are
/// never scanned wholesale. Tenant visibility comes from the global Product query
/// filter (Buid == tenant OR shared Buid == null).
/// </summary>
public sealed class LeadConversionIntelligence : ILeadConversionIntelligence
{
    // Below this top score a line needs human attention; also the threshold for
    // auto-assigning a ProductId when the convert request leaves it unspecified.
    private const decimal ConfidenceFloor = 0.90m;
    private const int MaxMatchesPerLine = 3;
    private const int MaxNameCandidatesPerLine = 40;

    private readonly ErpRfqAutomationContext _db;
    private readonly IProductItemResolver? _productResolver;

    public LeadConversionIntelligence(ErpRfqAutomationContext db,
        IProductItemResolver? productResolver = null)
    {
        _db = db;
        _productResolver = productResolver;
    }

    // ================================================================ Preview

    public async Task<ConversionPreview> PreviewAsync(long leadId, long businessUnitId, CancellationToken ct)
    {
        var lead = await _db.Leads
            .AsNoTracking()
            .Include(l => l.LeadItems)
            .FirstOrDefaultAsync(l => l.Id == leadId && l.BusinessUnitId == businessUnitId, ct);
        if (lead == null)
            throw new KeyNotFoundException($"Lead with ID {leadId} not found in Business Unit {businessUnitId}.");

        var resolved = await ResolveLinesAsync(lead.LeadItems, businessUnitId, ct);

        var items = lead.LeadItems
            .OrderBy(li => li.Id)
            .Select(li =>
            {
                var r = resolved[li.Id];
                return new ConversionPreviewItem
                {
                    LeadItemId = li.Id,
                    SourceText = BuildSourceText(li),
                    Quantity = li.Quantity,
                    UnitOfMeasure = li.UnitOfMeasure,
                    NormalizedQuantity = r.NormalizedQuantity,
                    NormalizedUom = r.NormalizedUom,
                    Matches = r.Matches,
                    BestMatchProductId = r.Matches.Count > 0 ? r.Matches[0].ProductId : null,
                    Confidence = r.Confidence,
                    NeedsAttention = r.NeedsAttention,
                    AttentionReason = r.AttentionReason
                };
            })
            .ToList();

        return new ConversionPreview
        {
            LeadId = lead.Id,
            Header = new ConversionPreviewHeader
            {
                // Same derivation the convert uses (legacy semantics): reuse the
                // lead's RFQ number when present, otherwise derive one.
                Rfqno = !string.IsNullOrWhiteSpace(lead.Rfqno)
                    ? lead.Rfqno
                    : $"RFQ-{lead.Id}-{DateTime.UtcNow:yyyyMMddHHmmss}",
                BuyersName = lead.BuyersName,
                RecDate = lead.RecDate,
                BidClosingDate = lead.BidClosingDate
            },
            Items = items,
            OverallConfidence = items.Count == 0 ? 0m : Math.Round(items.Average(i => i.Confidence), 2)
        };
    }

    // ================================================================ Convert

    public Task<long> ConvertAsync(long leadId, long businessUnitId, ConvertRequest request, CancellationToken ct)
    {
        return Task.FromException<long>(new InvalidOperationException(
            "Direct intelligence conversion is retired. Commit the current Lead Revision participation decision and invoke RFQ Promotion."));
    }

    // ====================================================== Resolution engine

    /// <summary>Per-line resolution outcome (internal; the wire shape is ConversionPreviewItem).</summary>
    internal sealed class ResolvedLine
    {
        public IReadOnlyList<ProductMatch> Matches { get; init; } = Array.Empty<ProductMatch>();
        public long? AutoLinkedProductId { get; init; }
        public decimal Confidence { get; init; }
        public decimal? NormalizedQuantity { get; init; }
        public string? NormalizedUom { get; init; }
        public int? UomId { get; init; }
        public bool NeedsAttention { get; init; }
        public string? AttentionReason { get; init; }

    }

    private sealed record Candidate(long Id, string? ProductName, string PartNo, string? ModelNo, string? Description);

    private async Task<Dictionary<long, ResolvedLine>> ResolveLinesAsync(
        IEnumerable<LeadItem> leadItems, long businessUnitId, CancellationToken ct)
    {
        // Navigation collections have no database ordering guarantee. Keep fallback revision-line
        // alignment deterministic for historical lines that do not carry a parseable line number.
        var items = leadItems.OrderBy(item => item.Id).ToList();
        var result = new Dictionary<long, ResolvedLine>();
        if (items.Count == 0) return result;

        // ---- Tenant UoM table (small, per-BU; SetUom has no global tenant filter,
        // so the BU predicate here is mandatory). Folded on both code and name by
        // SetUomVocabulary, which also indexes each row under the canonical code it maps
        // to — so a tenant who spells the unit "Nos" is still found when we ask for "EA".
        var uoms = await _db.SetUoms.AsNoTracking()
            .Where(u => u.BusinessUnitId == businessUnitId && u.IsActive)
            .ToListAsync(ct);
        var uomVocabulary = SetUomVocabulary.From(uoms);
        var authoritative = await AuthoritativeMatchesAsync(items, businessUnitId, ct);
        // Legacy leads without immutable revision lines retain the bounded fallback matcher.
        // Current leads never run two matchers: every mapped canonical line is resolved only by
        // IProductItemResolver, which is also the commercial-intelligence authority.
        var fallbackItems = items.Where(item => !authoritative.ContainsKey(item.Id)).ToList();

        // ---- Candidate fetch 1: every catalogue number any line offers, in one IN query.
        // Each identifier contributes BOTH its literal spelling and its punctuation-free fold, so
        // a catalogue keyed on "A2A50006470" is still reached by a document that wrote
        // "A2A-50006470". Folding the LINE side is free — a handful of extra strings in an IN
        // list. Folding the CATALOGUE side would mean replace() over every product row, which is
        // exactly the wholesale scan this resolver exists to avoid, so it is not done here; see
        // BestCodeHit, which still recognises a catalogue-side spelling difference on any product
        // the name query happens to have pulled in.
        var spellings = fallbackItems
            .SelectMany(CodeIdentifiers)
            .SelectMany(id => new[] { id.Value, ProductIdentityNormalizer.FoldIdentifier(id.Value) })
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => s!.ToLowerInvariant())
            .Distinct()
            .ToList();

        var candidates = new Dictionary<long, Candidate>();
        if (spellings.Count > 0)
        {
            var exactRows = await ActiveProducts()
                .Where(p => spellings.Contains(p.PartNo.ToLower())
                            || (p.ModelNo != null && spellings.Contains(p.ModelNo.ToLower())))
                .Select(p => new Candidate(p.Id, p.ProductName, p.PartNo, p.ModelNo, p.Description))
                .ToListAsync(ct);
            foreach (var c in exactRows) candidates[c.Id] = c;
        }

        // ---- Candidate fetch 2: one bounded ILIKE query per distinct name, using
        // the two most significant tokens. Leads carry few lines, so this stays cheap.
        var nameQueriesDone = new HashSet<string>();
        foreach (var item in fallbackItems)
        {
            var normName = NormalizeName(item.ProductShortName ?? item.ProductShortDescription);
            if (normName is null || !nameQueriesDone.Add(normName)) continue;

            var tokens = Tokenize(normName).OrderByDescending(t => t.Length).Take(2).ToList();
            if (tokens.Count == 0) continue;

            var token1 = tokens[0];
            var token2 = tokens.Count > 1 ? tokens[1] : token1;

            var nameRows = await ActiveProducts()
                .Where(p => (p.ProductName != null && (p.ProductName.ToLower().Contains(token1)
                                                       || p.ProductName.ToLower().Contains(token2)))
                            || (p.Description != null && (p.Description.ToLower().Contains(token1)
                                                          || p.Description.ToLower().Contains(token2))))
                .OrderBy(p => p.Id)
                .Take(MaxNameCandidatesPerLine)
                .Select(p => new Candidate(p.Id, p.ProductName, p.PartNo, p.ModelNo, p.Description))
                .ToListAsync(ct);
            foreach (var c in nameRows) candidates.TryAdd(c.Id, c);
        }

        // ---- Score every line against the pooled candidate set, in memory.
        foreach (var item in items)
        {
            authoritative.TryGetValue(item.Id, out var authoritativeMatch);
            var matches = authoritativeMatch?.Matches ?? ScoreItem(item, candidates.Values)
                .OrderByDescending(m => m.Score)
                .ThenBy(m => m.ProductId)
                .Take(MaxMatchesPerLine)
                .ToList();

            var confidence = matches.Count > 0 ? matches[0].Score : 0m;
            var normalizedQty = NormalizeQuantity(item);
            var uom = UomCanonicalizer.Canonicalize(item.UnitOfMeasure, uomVocabulary);

            var reasons = new List<string>();
            // Soft: a human can look at these and reasonably say "convert anyway".
            var soft = new List<string>();
            // Hard: no acknowledgement can make these safe to quote.
            var hard = new List<string>();

            if (matches.Count == 0) soft.Add("No catalog match found");
            else if (confidence < ConfidenceFloor) soft.Add($"Low-confidence match ({Math.Round(confidence * 100)}%)");
            if (normalizedQty is null or <= 0) hard.Add("Quantity missing");
            if (uom.Resolution == UomResolution.Absent) hard.Add("Unit of measure missing");
            // A unit we refuse to map is NOT a missing unit and must not be quoted as if it
            // were a piece count: "25 Pack" needs a human to say how many are in a pack.
            // Soft because the human CAN say — by correcting the line, or by acknowledging.
            else if (uom.NeedsReview)
                soft.Add($"Unit of measure \"{uom.SourceText}\" needs review — {UomCanonicalizer.Explain(uom.ReviewReason)}");

            reasons.AddRange(hard);
            reasons.AddRange(soft);

            result[item.Id] = new ResolvedLine
            {
                Matches = matches,
                AutoLinkedProductId = authoritativeMatch?.AutoLinkedProductId
                    ?? (_productResolver is null && confidence >= ConfidenceFloor && matches.Count > 0
                        ? matches[0].ProductId : null),
                Confidence = confidence,
                NormalizedQuantity = normalizedQty,
                NormalizedUom = uom.Value,
                UomId = uom.TenantUomId,
                NeedsAttention = reasons.Count > 0,
                AttentionReason = reasons.Count > 0 ? string.Join("; ", reasons) : null,
            };
        }

        return result;
    }

    private async Task<Dictionary<long, AuthoritativeMatch>> AuthoritativeMatchesAsync(
        IReadOnlyList<LeadItem> items, long businessUnitId, CancellationToken ct)
    {
        var result = new Dictionary<long, AuthoritativeMatch>();
        if (_productResolver is null || items.Count == 0) return result;
        var leadId = items[0].LeadId;
        var revisionId = await _db.Leads.AsNoTracking()
            .Where(lead => lead.BusinessUnitId == businessUnitId && lead.Id == leadId)
            .Select(lead => lead.CurrentRevisionId)
            .SingleOrDefaultAsync(ct);
        if (!revisionId.HasValue) return result;
        var revisionLines = await _db.Set<LeadItemRevision>().AsNoTracking()
            .Where(line => line.BusinessUnitId == businessUnitId && line.LeadRevisionId == revisionId.Value)
            .OrderBy(line => line.LineNumber)
            .ToListAsync(ct);

        foreach (var item in items)
        {
            // Catalog evidence belongs to the immutable revision line that names this exact
            // canonical projection. Line numbers and collection positions are presentation
            // details and must never be used as identity fallbacks.
            var revisionLine = revisionLines.SingleOrDefault(line => line.LeadItemId == item.Id);
            if (revisionLine is null) continue;
            var part = FirstValue(item.ManufacturerPartNumber, item.ItemMaterialCode);
            var description = FirstValue(item.ProductShortDescription, item.ProductShortName, item.ItemText);
            var resolution = await _productResolver.ResolveAsync(new ProductResolutionRequest(
                businessUnitId, revisionId.Value, revisionLine.Id, part, item.ManufacturerName, description,
                [new ProductResolutionEvidence("canonical-lead-line", $"lead:{leadId}:item:{item.Id}", part)]), ct);
            var matches = resolution.RankedCandidates.Take(MaxMatchesPerLine).Select(candidate => new ProductMatch
            {
                ProductId = candidate.ProductId,
                ProductName = candidate.ProductName,
                MaterialCode = candidate.PartNumber,
                ManufacturerPartNumber = candidate.InternalCode,
                Score = candidate.Confidence,
                Reason = candidate.Reason
            }).ToList();
            result[item.Id] = new AuthoritativeMatch(matches,
                resolution.DecisionState == ProductResolutionDecisionState.AutoLinked
                    ? resolution.ResolvedProductId : null);
        }
        return result;
    }

    private sealed record AuthoritativeMatch(IReadOnlyList<ProductMatch> Matches, long? AutoLinkedProductId);

    private static string? FirstValue(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private IQueryable<Product> ActiveProducts() =>
        _db.Products.AsNoTracking().Where(p => p.IsActive == null || p.IsActive == true);

    private static IEnumerable<ProductMatch> ScoreItem(LeadItem item, IEnumerable<Candidate> candidates)
    {
        var identifiers = CodeIdentifiers(item).ToList();
        var normName = NormalizeName(item.ProductShortName ?? item.ProductShortDescription);
        var nameTokens = normName is null ? new HashSet<string>() : Tokenize(normName);

        foreach (var p in candidates)
        {
            decimal score;
            string reason;

            if (BestCodeHit(identifiers, p) is { } hit)
            {
                (score, reason) = hit;
            }
            else
            {
                if (normName is null) continue;
                var candName = NormalizeName(p.ProductName);
                if (candName is not null && candName == normName)
                {
                    (score, reason) = (0.90m, "Exact product name match");
                }
                else
                {
                    // Token overlap across candidate name + description, with a
                    // containment boost; scaled into the 0.40–0.85 band.
                    var candTokens = candName is null ? new HashSet<string>() : Tokenize(candName);
                    var candDesc = NormalizeName(p.Description);
                    if (candDesc is not null) candTokens.UnionWith(Tokenize(candDesc));

                    decimal ratio = 0m;
                    if (nameTokens.Count > 0 && candTokens.Count > 0)
                        ratio = (decimal)nameTokens.Count(candTokens.Contains) / nameTokens.Count;

                    var contains = candName is not null && normName.Length >= 4 && candName.Length >= 4
                                   && (candName.Contains(normName) || normName.Contains(candName));
                    if (contains) ratio = Math.Max(ratio, 0.8m);

                    if (ratio <= 0m) continue;
                    score = 0.40m + 0.45m * ratio;
                    reason = $"Name similarity {Math.Round(ratio * 100)}%";
                }
            }

            yield return new ProductMatch
            {
                ProductId = p.Id,
                ProductName = p.ProductName,
                MaterialCode = p.PartNo,
                ManufacturerPartNumber = p.ModelNo,
                Score = Math.Round(score, 2),
                Reason = reason
            };
        }
    }

    // ================================================== Normalization helpers

    /// <summary>A catalogue number a lead line offers, and what an exact hit on it is worth.</summary>
    private readonly record struct CodeIdentifier(string Value, decimal ExactScore, string Reason);

    /// <summary>
    /// Every field on a lead line that can carry a catalogue number, best evidence first.
    ///
    /// <para>Only <c>ItemMaterialCode</c> and <c>ManufacturerPartNumber</c> used to be read, and
    /// that omission is the defect. A buyer's material number arrives in whichever FIELD the door
    /// that read the document happens to populate: <c>NativeSpreadsheetParser</c> routes every
    /// material-code heading ("materialcode", "stockcode", "sapmaterial", "buyerpartno", …) into
    /// <c>ManufacturerPartNumber</c>, and a table whose code column has an unrecognised heading
    /// leaves the number sitting in the description cell. Measured against the live catalogue
    /// shape: a code in <c>AlternatePartNumber</c> scored 0.85 and a code in
    /// <c>ProductShortDescription</c> scored 0.00 — not a near miss, no candidate at all, because
    /// the ILIKE candidate query searches ProductName and Description and never the catalogue's
    /// own number columns.</para>
    ///
    /// <para>CORRECTION (2026-08, after an adversarial re-read): an earlier revision of this
    /// comment claimed the <c>ItemMaterialCode</c> rung was dead because
    /// <c>ChunkedExtractionService.MapCanonicalItem</c> hardcodes that field null. That is FALSE
    /// and it cost an investigation. <c>CanonicalRfqLineItem</c> genuinely has no member for a
    /// material code, so the structured chunked path leaving it null is correct, not a bug — but
    /// it is not the only door. <c>AramcoBidListExtraction</c> writes the Aramco material number
    /// straight into <c>ItemMaterialCode</c> at <c>Certain</c> confidence, and the model door
    /// populates it too. The 1.00 rung below is LIVE. Do not "fix" the hardcoded null, do not
    /// touch <c>CanonicalRfqLineItem</c>, <c>MapCanonicalItem</c>, the extraction prompt, or
    /// <c>NativeSpreadsheetParser.FieldAliases</c> (that routing is deliberate and
    /// test-pinned by <c>ProductionDocumentReaderSpreadsheetFallbackTests</c>). Any consumer that
    /// reads a lead line's catalogue number must read BOTH fields, exactly as the ladder below
    /// and <c>LeadDecisionService</c> now do on both its brief and its grid paths.</para>
    ///
    /// <para>The confidence floor is NOT relaxed anywhere below. Every rung here is exact equality
    /// against a catalogue number; what changed is which of the line's fields are allowed to
    /// supply that number.</para>
    /// </summary>
    private static IEnumerable<CodeIdentifier> CodeIdentifiers(LeadItem item)
    {
        if (Clean(item.ItemMaterialCode) is { } code)
            yield return new CodeIdentifier(code, 1.00m, "Matched by material code");
        if (Clean(item.ManufacturerPartNumber) is { } mpn)
            yield return new CodeIdentifier(mpn, 0.95m, "Matched by manufacturer part number");
        if (Clean(item.AlternatePartNumber) is { } alternate)
            yield return new CodeIdentifier(alternate, 0.93m, "Matched by alternate part number");

        // A code typed into a description cell, which is where it lands whenever the document had
        // no column heading the parser recognised. Offered as an identifier only when it is SHAPED
        // like one, and it still has to equal a catalogue number outright to score: prose never
        // qualifies, because "GASKET:SPIRAL WOUND,2 IN,CL300" contains spaces and "VALVE" contains
        // no digit.
        foreach (var text in new[] { item.ProductShortName, item.ProductShortDescription })
            if (Clean(text) is { } quoted && LooksLikeBareCode(quoted))
                yield return new CodeIdentifier(quoted, 0.92m, "Matched by catalog number in the line text");
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>One unspaced token, at least five characters, carrying at least one digit.</summary>
    private static bool LooksLikeBareCode(string value) =>
        value.Length is >= 5 and <= 60
        && !value.Any(char.IsWhiteSpace)
        && value.Any(char.IsDigit);

    /// <summary>
    /// How much an identifier loses when the two sides agree on the characters but not the
    /// punctuation. Deliberately small: "A2A-50006470" and "A2A50006470" are one number written
    /// two ways, so a folded hit must still clear <see cref="ConfidenceFloor"/> — while ranking
    /// below an outright hit whenever both are available on the same line.
    /// </summary>
    private const decimal FoldedHitPenalty = 0.02m;

    /// <summary>
    /// The best catalogue-number hit this candidate takes from the line, or null when it takes
    /// none and must fall through to name similarity. Every identifier is compared against BOTH
    /// catalogue number columns: <c>PartNo</c> and <c>ModelNo</c> are both codes, and which one a
    /// tenant keyed its catalogue on is its own choice, not something a lead line can know.
    /// </summary>
    private static (decimal Score, string Reason)? BestCodeHit(
        IReadOnlyList<CodeIdentifier> identifiers, Candidate p)
    {
        (decimal Score, string Reason)? best = null;
        foreach (var identifier in identifiers)
        {
            decimal? score = null;
            if (Eq(p.PartNo, identifier.Value) || Eq(p.ModelNo, identifier.Value))
                score = identifier.ExactScore;
            else if (FoldedEq(p.PartNo, identifier.Value) || FoldedEq(p.ModelNo, identifier.Value))
                score = identifier.ExactScore - FoldedHitPenalty;

            if (score is { } value && (best is null || value > best.Value.Score))
                best = (value, identifier.Reason);
        }
        return best;
    }

    /// <summary>
    /// Equality once punctuation is folded away, using the SAME normaliser
    /// (<c>ProductIntelligence/ProductIdentityNormalizer</c>) the deterministic resolver already
    /// trusts for this, rather than a second opinion about what a part number is.
    /// </summary>
    private static bool FoldedEq(string? a, string? b) =>
        ProductIdentityNormalizer.FoldIdentifier(a) is { } x
        && ProductIdentityNormalizer.FoldIdentifier(b) is { } y
        && x == y;

    private static bool Eq(string? a, string? b) =>
        a is not null && b is not null &&
        string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static readonly Regex CurrencyNoise = new(
        @"\b(usd|eur|gbp|aed|sar|inr|pkr|cad|aud|jpy|cny|chf)\b|[$€£¥₹]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NonAlphaNum = new(@"[^a-z0-9]+", RegexOptions.Compiled);

    private static readonly Regex FirstNumber = new(@"\d+(?:[.,]\d+)?", RegexOptions.Compiled);

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
        { "the", "and", "for", "with", "per", "pcs", "each" };

    /// <summary>Lowercase, strip currency-ish noise, collapse to space-separated alphanumerics.</summary>
    private static string? NormalizeName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = CurrencyNoise.Replace(raw.ToLowerInvariant(), " ");
        s = NonAlphaNum.Replace(s, " ").Trim();
        while (s.Contains("  ")) s = s.Replace("  ", " ");
        return s.Length == 0 ? null : s;
    }

    private static HashSet<string> Tokenize(string normalized) =>
        normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 3 && !StopWords.Contains(t))
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The line's canonical quantity. Raw description/UoM text is not a quantity field and is
    /// never parsed into one; an absent value remains absent until a human corrects it.
    /// </summary>
    private static decimal? NormalizeQuantity(LeadItem item)
    {
        return item.Quantity is > 0 ? item.Quantity.Value : null;
    }

    private static string? BuildSourceText(LeadItem li)
    {
        var parts = new[] { li.ProductShortName, li.ProductShortDescription }
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (parts.Count > 0) return string.Join(" — ", parts);
        return li.ItemText ?? li.CommodityProduct;
    }
}
