using System.Text.RegularExpressions;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.CommercialCases.Lifecycle;
using Microsoft.EntityFrameworkCore;
using ERP_RFQ_Automation.Inventory.Commercial;

namespace ERP_RFQ_Automation.Intelligence.Conversion;

/// <summary>
/// Deterministic (no-LLM) conversion intelligence.
///
/// Product resolution per lead line, best rule wins:
///   1.00  exact ItemMaterialCode == Product.PartNo
///   0.95  exact ManufacturerPartNumber == Product.ModelNo (or PartNo)
///   0.90  normalized-name equality vs Product.ProductName
///   0.40–0.85  contains / token-overlap vs Product name + description,
///              scaled by overlap ratio
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
    private readonly ICommercialLineResolutionApplicationService? _lineResolution;

    public LeadConversionIntelligence(ErpRfqAutomationContext db,
        ICommercialLineResolutionApplicationService? lineResolution = null)
    {
        _db = db;
        _lineResolution = lineResolution;
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

    public async Task<long> ConvertAsync(long leadId, long businessUnitId, ConvertRequest request, CancellationToken ct)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(() => ConvertCoreAsync(leadId, businessUnitId, request, ct));
    }

    private async Task<long> ConvertCoreAsync(long leadId, long businessUnitId, ConvertRequest request, CancellationToken ct)
    {
        _db.ChangeTracker.Clear();
        await using var tx = await _db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        var lead = await _db.Leads
            .Include(l => l.LeadItems)
            .Include(l => l.LeadStatus)
            .FirstOrDefaultAsync(l => l.Id == leadId && l.BusinessUnitId == businessUnitId, ct);
        if (lead == null)
            throw new KeyNotFoundException($"Lead with ID {leadId} not found in Business Unit {businessUnitId}.");

        var already = await _db.Rfqs
            .FirstOrDefaultAsync(r => r.LeadId == leadId && r.BusinessUnitId == businessUnitId, ct);
        if (already != null)
        {
            already.InheritCommercialIdentity(lead);
            await _db.SaveChangesAsync(ct);
            if (_lineResolution is not null)
            {
                await _lineResolution.ResolveLeadAsync(businessUnitId, lead.Id, 10, ct);
                await _lineResolution.LinkRfqAsync(businessUnitId, lead.Id, already.Id, ct);
            }
            await CompleteConversionLifecycleInCurrentTransactionAsync(lead, already.Id, request.ActingUser, ct);
            await tx.CommitAsync(ct);
            return already.Id;
        }

        // Legacy gates, replicated verbatim: only accepted leads convert, and a
        // lead is only ever converted once.
        if (LifecyclePolicy.Canonicalize("Lead", lead.LeadStatus?.SetupCode, lead.LeadStatus?.SetupValue) != "QUALIFIED")
            throw new InvalidOperationException("Only a qualified lead can be converted to an RFQ.");

        // WP-A3 hard block: an unresolved duplicate flag stops conversion here too.
        if (lead.DuplicateStatus is "suspected" or "confirmed")
            throw new InvalidOperationException(
                $"This lead is flagged as a possible duplicate of lead #{lead.DuplicateOfLeadId} — resolve the duplicate flag first.");

        if (lead.RequiresCommercialReview && !lead.CommercialFactsVerified)
            throw new InvalidOperationException(
                "AI-extracted commercial facts must be approved in extraction review before RFQ conversion.");

        var blockers = FindConversionBlockers(lead);
        if (blockers.Count > 0)
            throw new InvalidOperationException($"Review these required inquiry fields before creating the RFQ: {string.Join("; ", blockers)}.");

        // Per-line choices. Reject ids that don't belong to this lead (never trust
        // caller-supplied identifiers across the tenant boundary).
        var choices = (request.Items ?? new List<ConvertRequestItem>())
            .GroupBy(i => i.LeadItemId)
            .ToDictionary(g => g.Key, g => g.Last());
        var lineIds = lead.LeadItems.Select(li => li.Id).ToHashSet();
        var foreign = choices.Keys.Where(id => !lineIds.Contains(id)).ToList();
        if (foreign.Count > 0)
            throw new ArgumentException($"Request references line item(s) [{string.Join(", ", foreign)}] that do not belong to lead {leadId}.");

        // Explicitly chosen products must exist in the tenant-visible catalog
        // (global query filter scopes this to the tenant + shared rows).
        var explicitProductIds = choices.Values
            .Where(c => c.Include && c.ProductId.HasValue)
            .Select(c => c.ProductId!.Value)
            .Distinct()
            .ToList();
        if (explicitProductIds.Count > 0)
        {
            var visible = await _db.Products.AsNoTracking()
                .Where(p => explicitProductIds.Contains(p.Id))
                .Select(p => p.Id)
                .ToListAsync(ct);
            var missing = explicitProductIds.Except(visible).ToList();
            if (missing.Count > 0)
                throw new ArgumentException($"Product(s) [{string.Join(", ", missing)}] not found in this business unit's catalog.");
        }

        var included = lead.LeadItems
            .OrderBy(li => li.Id)
            .Where(li => !choices.TryGetValue(li.Id, out var c) || c.Include)
            .ToList();
        if (included.Count == 0)
            throw new ArgumentException("At least one line item must be included to convert a lead.");

        // Resolve once: supplies best-match products, match scores and UoM ids.
        var resolved = await ResolveLinesAsync(included, businessUnitId, ct);

        var createdBy = string.IsNullOrWhiteSpace(request.ActingUser) ? "System" : request.ActingUser!;
        var now = DateTime.UtcNow;

        // RFQ number derivation — identical to the legacy path.
        var rfqno = !string.IsNullOrWhiteSpace(lead.Rfqno)
            ? lead.Rfqno!
            : $"RFQ-{leadId}-{now:yyyyMMddHHmmss}";
        if (await _db.Rfqs.AnyAsync(r => r.Rfqno == rfqno && r.BusinessUnitId == businessUnitId, ct))
            rfqno = $"{rfqno}-{now:yyyyMMddHHmmss}";

        var headerRemarks = string.IsNullOrWhiteSpace(request.Notes)
            ? lead.HeaderRemarks
            : string.IsNullOrWhiteSpace(lead.HeaderRemarks)
                ? $"[Conversion] {request.Notes!.Trim()}"
                : $"{lead.HeaderRemarks}\n[Conversion] {request.Notes!.Trim()}";

        var rfq = new Rfq
        {
            Rfqno = rfqno,
            BuyersName = lead.BuyersName,
            RecDate = lead.RecDate,
            BidClosingDate = lead.BidClosingDate,
            AcknowledgmentDate = lead.AcknowledgmentDate,
            SubDate = lead.SubDate,
            HeaderRemarks = headerRemarks,
            OpportunityNo = lead.OpportunityNo,
            // Legacy uses lead.NoOfLineItems ?? item count; with include/exclude in
            // play the actual included count is the only correct value.
            NoOfLineItems = included.Count,
            Rfqtype = lead.Rfqtype,
            DurationAgreement = lead.DurationAgreement,
            LeadId = lead.Id,
            BusinessUnitId = businessUnitId,
            RfqstatusId = await LifecycleStatusCatalog.ResolveIdAsync(_db, businessUnitId, "Rfq", "DRAFT", ct),
            CreatedBy = createdBy,
            CreatedDate = now,
            Rfqitems = included.Select(li =>
            {
                choices.TryGetValue(li.Id, out var choice);
                var r = resolved[li.Id];

                // Product: explicit choice wins; otherwise auto-assign the best
                // match only when it clears the confidence floor.
                var productId = choice?.ProductId
                    ?? (r.Confidence >= ConfidenceFloor && r.Matches.Count > 0
                        ? r.Matches[0].ProductId
                        : (long?)null);

                // Quantity: corrected value wins; else raw lead quantity; else the
                // normalized (parsed-from-text) quantity when the raw one is 0.
                int quantity;
                if (choice?.Quantity is int cq)
                {
                    if (cq <= 0) throw new ArgumentException($"Quantity for line {li.Id} must be greater than zero.");
                    quantity = cq;
                }
                else
                {
                    quantity = li.Quantity > 0
                        ? li.Quantity
                        : (int)Math.Ceiling(r.NormalizedQuantity ?? 0m);
                }

                // UoM: corrected value wins; both paths standardize against SetUoms.
                string? uomText;
                int? uomId;
                if (!string.IsNullOrWhiteSpace(choice?.UnitOfMeasure))
                    (uomText, uomId) = NormalizeUom(choice!.UnitOfMeasure, r.UomLookup);
                else
                    (uomText, uomId) = (r.NormalizedUom ?? li.UnitOfMeasure, r.UomId);

                // Confidence stamp: the match score of whichever product landed on
                // the line; unresolved lines keep the extraction confidence (legacy).
                var confidence = productId.HasValue
                    ? (r.Matches.FirstOrDefault(m => m.ProductId == productId.Value)?.Score ?? li.Aiconfidence)
                    : li.Aiconfidence;

                return new Rfqitem
                {
                    CompanyRef = li.CompanyRef,
                    CustomerAccountPortalId = li.CustomerAccountPortalId,
                    CustomerRfqno = li.CustomerRfqno,
                    ItemMaterialCode = li.ItemMaterialCode,
                    LineItemNo = li.LineItemNo,
                    ProductId = productId,
                    CommodityProduct = li.CommodityProduct,
                    ProductShortName = li.ProductShortName,
                    ProductShortDescription = li.ProductShortDescription,
                    Alternative = li.Alternative,
                    BuyerName = li.BuyerName,
                    Currency = li.Currency,
                    UnitOfMeasure = uomText,
                    UomId = uomId,
                    UnitPrice = li.UnitPrice,
                    Quantity = quantity,
                    StorageLocation = li.StorageLocation,
                    ManufacturerName = li.ManufacturerName,
                    ManufacturerPartNumber = li.ManufacturerPartNumber,
                    AlternateProductName = li.AlternateProductName,
                    AlternatePartNumber = li.AlternatePartNumber,
                    ItemText = li.ItemText,
                    MaterialPotext = li.MaterialPotext,
                    LeadTime = li.LeadTime,
                    ReceivedDate = li.ReceivedDate,
                    BidClosingDateLine = li.BidClosingDateLine,
                    Aiconfidence = confidence,
                    CreatedBy = createdBy,
                    CreatedDate = now
                };
            }).ToList()
        };
        rfq.InheritCommercialIdentity(lead);

        _db.Rfqs.Add(rfq);
        await _db.SaveChangesAsync(ct);
        if (_lineResolution is not null)
        {
            await _lineResolution.ResolveLeadAsync(businessUnitId, lead.Id, 10, ct);
            await _lineResolution.LinkRfqAsync(businessUnitId, lead.Id, rfq.Id, ct);
        }
        await CompleteConversionLifecycleInCurrentTransactionAsync(lead, rfq.Id, createdBy, ct);
        await tx.CommitAsync(ct);
        return rfq.Id;
    }

    private async Task CompleteConversionLifecycleInCurrentTransactionAsync(Lead lead, long rfqId, string? createdBy, CancellationToken ct)
    {
        if (LifecyclePolicy.Canonicalize("Lead", lead.LeadStatus?.SetupCode, lead.LeadStatus?.SetupValue) != "QUALIFIED") return;
        var actor = string.IsNullOrWhiteSpace(createdBy) ? "System" : createdBy.Trim();
        await new LifecycleApplicationService(_db).TransitionLeadInCurrentTransactionAsync(
            lead.BusinessUnitId, lead.Id, new LifecycleActor(actor, "LeadConversion"),
            new LifecycleTransitionCommand("CONVERTED_TO_RFQ", lead.LifecycleVersion, null, null,
                "Api", $"conversion-{lead.Id}", $"rfq-{rfqId}", $"lead-conversion:{lead.BusinessUnitId}:{lead.Id}"),
            false, ct);
    }

    private static List<string> FindConversionBlockers(Lead lead)
    {
        var blockers = new List<string>();
        if (!lead.CustomerId.HasValue) blockers.Add("customer");
        if (!lead.BidClosingDate.HasValue) blockers.Add("customer deadline");
        if (lead.LeadItems.Count == 0) blockers.Add("at least one requested item");

        foreach (var line in lead.LeadItems.OrderBy(item => item.Id))
        {
            var label = string.IsNullOrWhiteSpace(line.LineItemNo) ? $"line {line.Id}" : $"line {line.LineItemNo}";
            if (string.IsNullOrWhiteSpace(line.ItemMaterialCode)
                && string.IsNullOrWhiteSpace(line.ManufacturerPartNumber)
                && string.IsNullOrWhiteSpace(line.ProductShortDescription))
                blockers.Add($"{label} part number or description");
            if (line.Quantity <= 0) blockers.Add($"{label} quantity");
            if (string.IsNullOrWhiteSpace(line.UnitOfMeasure)) blockers.Add($"{label} unit");
            if (string.IsNullOrWhiteSpace(line.Currency)) blockers.Add($"{label} currency");
        }

        return blockers;
    }

    // ====================================================== Resolution engine

    /// <summary>Per-line resolution outcome (internal; the wire shape is ConversionPreviewItem).</summary>
    internal sealed class ResolvedLine
    {
        public IReadOnlyList<ProductMatch> Matches { get; init; } = Array.Empty<ProductMatch>();
        public decimal Confidence { get; init; }
        public decimal? NormalizedQuantity { get; init; }
        public string? NormalizedUom { get; init; }
        public int? UomId { get; init; }
        public bool NeedsAttention { get; init; }
        public string? AttentionReason { get; init; }
        /// <summary>Tenant UoM lookup, exposed so ConvertAsync can normalize corrected UoM strings.</summary>
        public IReadOnlyDictionary<string, SetUom> UomLookup { get; init; } = new Dictionary<string, SetUom>();
    }

    private sealed record Candidate(long Id, string? ProductName, string PartNo, string? ModelNo, string? Description);

    private async Task<Dictionary<long, ResolvedLine>> ResolveLinesAsync(
        IEnumerable<LeadItem> leadItems, long businessUnitId, CancellationToken ct)
    {
        var items = leadItems.ToList();
        var result = new Dictionary<long, ResolvedLine>();
        if (items.Count == 0) return result;

        // ---- Tenant UoM table (small, per-BU; SetUom has no global tenant filter,
        // so the BU predicate here is mandatory). Keyed by lower code AND name.
        var uoms = await _db.SetUoms.AsNoTracking()
            .Where(u => u.BusinessUnitId == businessUnitId && u.IsActive)
            .ToListAsync(ct);
        var uomLookup = new Dictionary<string, SetUom>(StringComparer.OrdinalIgnoreCase);
        foreach (var u in uoms)
        {
            uomLookup.TryAdd(u.UomCode.Trim(), u);
            uomLookup.TryAdd(u.UomName.Trim(), u);
        }

        // ---- Candidate fetch 1: exact part/model numbers in one IN query.
        var codes = items.Select(i => i.ItemMaterialCode?.Trim().ToLowerInvariant())
                         .Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).Distinct().ToList();
        var mpns = items.Select(i => i.ManufacturerPartNumber?.Trim().ToLowerInvariant())
                        .Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).Distinct().ToList();

        var candidates = new Dictionary<long, Candidate>();
        if (codes.Count > 0 || mpns.Count > 0)
        {
            var exactRows = await ActiveProducts()
                .Where(p => codes.Contains(p.PartNo.ToLower())
                            || mpns.Contains(p.PartNo.ToLower())
                            || (p.ModelNo != null && mpns.Contains(p.ModelNo.ToLower())))
                .Select(p => new Candidate(p.Id, p.ProductName, p.PartNo, p.ModelNo, p.Description))
                .ToListAsync(ct);
            foreach (var c in exactRows) candidates[c.Id] = c;
        }

        // ---- Candidate fetch 2: one bounded ILIKE query per distinct name, using
        // the two most significant tokens. Leads carry few lines, so this stays cheap.
        var nameQueriesDone = new HashSet<string>();
        foreach (var item in items)
        {
            var normName = NormalizeName(item.ProductShortName ?? item.ProductShortDescription);
            if (normName is null || !nameQueriesDone.Add(normName)) continue;

            var tokens = Tokenize(normName).OrderByDescending(t => t.Length).Take(2).ToList();
            if (tokens.Count == 0) continue;

            var pat1 = $"%{EscapeLike(tokens[0])}%";
            var pat2 = tokens.Count > 1 ? $"%{EscapeLike(tokens[1])}%" : pat1;

            var nameRows = await ActiveProducts()
                .Where(p => (p.ProductName != null && (EF.Functions.ILike(p.ProductName, pat1, "\\")
                                                       || EF.Functions.ILike(p.ProductName, pat2, "\\")))
                            || (p.Description != null && (EF.Functions.ILike(p.Description, pat1, "\\")
                                                          || EF.Functions.ILike(p.Description, pat2, "\\"))))
                .OrderBy(p => p.Id)
                .Take(MaxNameCandidatesPerLine)
                .Select(p => new Candidate(p.Id, p.ProductName, p.PartNo, p.ModelNo, p.Description))
                .ToListAsync(ct);
            foreach (var c in nameRows) candidates.TryAdd(c.Id, c);
        }

        // ---- Score every line against the pooled candidate set, in memory.
        foreach (var item in items)
        {
            var matches = ScoreItem(item, candidates.Values)
                .OrderByDescending(m => m.Score)
                .ThenBy(m => m.ProductId)
                .Take(MaxMatchesPerLine)
                .ToList();

            var confidence = matches.Count > 0 ? matches[0].Score : 0m;
            var normalizedQty = NormalizeQuantity(item);
            var (normalizedUom, uomId) = NormalizeUom(item.UnitOfMeasure, uomLookup);

            var reasons = new List<string>();
            if (matches.Count == 0) reasons.Add("No catalog match found");
            else if (confidence < ConfidenceFloor) reasons.Add($"Low-confidence match ({Math.Round(confidence * 100)}%)");
            if (normalizedQty is null or <= 0) reasons.Add("Quantity missing");
            if (string.IsNullOrWhiteSpace(normalizedUom)) reasons.Add("Unit of measure missing");

            result[item.Id] = new ResolvedLine
            {
                Matches = matches,
                Confidence = confidence,
                NormalizedQuantity = normalizedQty,
                NormalizedUom = normalizedUom,
                UomId = uomId,
                NeedsAttention = reasons.Count > 0,
                AttentionReason = reasons.Count > 0 ? string.Join("; ", reasons) : null,
                UomLookup = uomLookup
            };
        }

        return result;
    }

    private IQueryable<Product> ActiveProducts() =>
        _db.Products.AsNoTracking().Where(p => p.IsActive == null || p.IsActive == true);

    private static IEnumerable<ProductMatch> ScoreItem(LeadItem item, IEnumerable<Candidate> candidates)
    {
        var code = item.ItemMaterialCode?.Trim();
        var mpn = item.ManufacturerPartNumber?.Trim();
        var normName = NormalizeName(item.ProductShortName ?? item.ProductShortDescription);
        var nameTokens = normName is null ? new HashSet<string>() : Tokenize(normName);

        foreach (var p in candidates)
        {
            decimal score;
            string reason;

            if (!string.IsNullOrEmpty(code) && Eq(p.PartNo, code))
            {
                (score, reason) = (1.00m, "Matched by material code");
            }
            else if (!string.IsNullOrEmpty(mpn) && (Eq(p.ModelNo, mpn) || Eq(p.PartNo, mpn)))
            {
                (score, reason) = (0.95m, "Matched by manufacturer part number");
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

    private static string EscapeLike(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>
    /// Quantity is typed int on LeadItem; when it is 0/dirty, try to recover a
    /// numeric value from the raw UoM ("10 EA") or the item text fields.
    /// </summary>
    private static decimal? NormalizeQuantity(LeadItem item)
    {
        if (item.Quantity > 0) return item.Quantity;
        foreach (var source in new[] { item.UnitOfMeasure, item.ItemText, item.MaterialPotext })
        {
            if (string.IsNullOrWhiteSpace(source)) continue;
            var m = FirstNumber.Match(source);
            if (m.Success && decimal.TryParse(m.Value.Replace(',', '.'),
                    System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out var q) && q > 0)
                return q;
        }
        return null;
    }

    /// <summary>
    /// Trim/standardize a raw UoM string against the tenant's SetUom table: a code
    /// or name hit returns the canonical UomCode + UomId; otherwise the cleaned
    /// raw text is kept (no id). Leading digits ("10 EA") are stripped first.
    /// </summary>
    private static (string? uom, int? uomId) NormalizeUom(string? raw, IReadOnlyDictionary<string, SetUom> lookup)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (null, null);
        var cleaned = Regex.Replace(raw, @"^[\d\s.,]+", "").Trim().TrimEnd('.', ',', ';');
        if (cleaned.Length == 0) return (null, null);
        if (lookup.TryGetValue(cleaned, out var uom))
            return (uom.UomCode, uom.UomId);
        return (cleaned.ToUpperInvariant(), null);
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
