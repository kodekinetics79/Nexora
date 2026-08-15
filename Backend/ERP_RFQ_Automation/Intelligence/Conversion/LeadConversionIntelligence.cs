using System.Text.RegularExpressions;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.CommercialCases.Lifecycle;
using Microsoft.EntityFrameworkCore;
using ERP_RFQ_Automation.Inventory.Commercial;
using ERP_RFQ_Automation.Services.Uom;

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
        try
        {
            return await strategy.ExecuteAsync(() => ConvertCoreAsync(leadId, businessUnitId, request, ct));
        }
        catch (DbUpdateException ex) when (LeadConversionGate.IsDuplicateKey(ex))
        {
            // Lost the race against the RFQ."LeadID" partial unique index: another caller
            // converted this lead between our existence check and our insert. The lead HAS
            // its RFQ — resolve to it exactly as the read-then-return idempotent path would.
            _db.ChangeTracker.Clear();
            var winner = await _db.Rfqs.AsNoTracking()
                .FirstOrDefaultAsync(r => r.LeadId == leadId && r.BusinessUnitId == businessUnitId, ct);
            if (winner == null) throw; // Not our index after all — surface the truth.
            return winner.Id;
        }
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
            await CompleteConversionLifecycleInCurrentTransactionAsync(lead, already.Id, request.ActingUser, null, ct);
            await tx.CommitAsync(ct);
            return already.Id;
        }

        // The shared gate every RFQ-creating door runs (LeadRepository.ConvertLeadToRfqAsync,
        // this path, and POST /api/Rfq with a LeadId). Before extraction these checks were a
        // second, drifting copy of the legacy door's; a lead is only ever converted once and
        // only through the same rules whichever door it enters by.
        LeadConversionGate.EnsureEligible(lead);

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

        // Header + per-line requirements, evaluated over the lines actually being converted with
        // the caller's corrections applied. Runs AFTER `included`/`choices` exist, because
        // checking the lead's raw stored lines ignored both exclusions and corrections (A-9).
        var blockers = FindConversionBlockers(lead, included, choices);
        if (blockers.Count > 0)
            throw new InvalidOperationException($"Review these required inquiry fields before creating the RFQ: {string.Join("; ", blockers)}.");

        // Resolve once: supplies best-match products, match scores and UoM ids.
        var resolved = await ResolveLinesAsync(included, businessUnitId, ct);

        // WP-B1 governance gate. The resolver has always computed NeedsAttention/AttentionReason
        // and the screen has always coloured those lines red — but nothing read the flag, so
        // "Create RFQ" stayed enabled and an operator could convert an inquiry the system knew
        // it had failed to read. Everything below runs BEFORE the first write, so a refusal
        // leaves no partial RFQ.
        var acknowledgedLines = EnforceLineWarningGovernance(included, resolved, choices, request);

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

                // Quantity: corrected value wins; else the raw lead quantity WHEN THE DOCUMENT
                // STATED ONE; else the normalized (parsed-from-text) quantity. A lead line whose
                // quantity is null states none, so it falls through to recovery exactly as a
                // dirty 0 did — and if recovery finds nothing the conversion is refused rather
                // than writing a demand for zero units into an RFQ.
                int quantity;
                if (choice?.Quantity is int cq)
                {
                    if (cq <= 0) throw new ArgumentException($"Quantity for line {li.Id} must be greater than zero.");
                    quantity = cq;
                }
                else
                {
                    quantity = li.Quantity is > 0
                        ? li.Quantity.Value
                        : (int)Math.Ceiling(r.NormalizedQuantity ?? 0m);
                    if (quantity <= 0)
                        throw new ArgumentException(
                            $"Quantity for line {li.Id} is not stated in the source document. Enter it before converting.");
                }

                // UoM: corrected value wins; both paths standardize against SetUoms.
                string? uomText;
                int? uomId;
                if (!string.IsNullOrWhiteSpace(choice?.UnitOfMeasure))
                    (uomText, uomId) = NormalizeUom(choice!.UnitOfMeasure, r.UomVocabulary);
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
        await CompleteConversionLifecycleInCurrentTransactionAsync(lead, rfq.Id, createdBy, acknowledgedLines, ct);
        await tx.CommitAsync(ct);
        return rfq.Id;
    }

    /// <param name="acknowledgedWarnings">
    /// Null when every converted line was clean. Otherwise a summary of which lines were
    /// converted over a warning and why, recorded on the existing lifecycle event rather than in
    /// a new audit table — the event already carries ReasonCode/ReasonNotes and both were being
    /// passed as null.
    /// </param>
    private async Task CompleteConversionLifecycleInCurrentTransactionAsync(
        Lead lead, long rfqId, string? createdBy, string? acknowledgedWarnings, CancellationToken ct)
    {
        if (LifecyclePolicy.Canonicalize("Lead", lead.LeadStatus?.SetupCode, lead.LeadStatus?.SetupValue) != "QUALIFIED") return;
        var actor = string.IsNullOrWhiteSpace(createdBy) ? "System" : createdBy.Trim();
        var reasonCode = acknowledgedWarnings is null ? null : "CONVERTED_WITH_ACKNOWLEDGED_WARNINGS";
        var lifecycle = new LifecycleApplicationService(_db);
        var lifecycleActor = new LifecycleActor(actor, "LeadConversion");
        await lifecycle.TransitionLeadInCurrentTransactionAsync(
            lead.BusinessUnitId, lead.Id, lifecycleActor,
            new LifecycleTransitionCommand("CONVERTED_TO_RFQ", lead.LifecycleVersion, reasonCode, acknowledgedWarnings,
                "Api", $"conversion-{lead.Id}", $"rfq-{rfqId}", $"lead-conversion:{lead.BusinessUnitId}:{lead.Id}"),
            false, ct);
        // Dedicated promotion event ALONGSIDE the generic transition, same transaction:
        // consumers get the lead/RFQ/revision facts by name instead of parsing a
        // status-transition payload.
        await lifecycle.RecordLeadPromotedToRfqInCurrentTransactionAsync(
            lead.BusinessUnitId, lead.Id, rfqId, lifecycleActor, $"conversion-{lead.Id}", ct);
    }

    /// <summary>
    /// Header requirements plus per-line requirements evaluated over the lines ACTUALLY being
    /// converted, with the caller's corrections applied.
    ///
    /// <para><b>The defect this closes (A-9).</b> This used to read <c>lead.LeadItems</c>
    /// wholesale: every line on the lead, at its persisted values. Two consequences, both wrong.
    /// A line the operator deliberately EXCLUDED still blocked the conversion — so one unreadable
    /// line on an 84-line bid list made the whole RFQ unconvertible. And a quantity CORRECTED on
    /// the Review &amp; Create RFQ screen was ignored, because the check read the stored 0 rather
    /// than the corrected value in the request: the quantity box the UI offers could never
    /// satisfy the gate it was there to satisfy.</para>
    ///
    /// <para>Nothing is relaxed. An included line must still carry a description, a positive
    /// quantity, a unit and a currency — the values are simply the ones being converted.</para>
    /// </summary>
    private static List<string> FindConversionBlockers(
        Lead lead,
        IReadOnlyList<LeadItem> includedLines,
        IReadOnlyDictionary<long, ConvertRequestItem> choices)
    {
        var blockers = new List<string>();
        if (!lead.CustomerId.HasValue) blockers.Add("customer");
        if (!lead.BidClosingDate.HasValue) blockers.Add("customer deadline");
        if (includedLines.Count == 0) blockers.Add("at least one requested item");

        foreach (var line in includedLines.OrderBy(item => item.Id))
        {
            choices.TryGetValue(line.Id, out var choice);
            // Effective values: what this conversion will actually write.
            var quantity = choice?.Quantity ?? line.Quantity;
            var unitOfMeasure = string.IsNullOrWhiteSpace(choice?.UnitOfMeasure)
                ? line.UnitOfMeasure
                : choice!.UnitOfMeasure;
            var label = string.IsNullOrWhiteSpace(line.LineItemNo) ? $"line {line.Id}" : $"line {line.LineItemNo}";
            if (string.IsNullOrWhiteSpace(line.ItemMaterialCode)
                && string.IsNullOrWhiteSpace(line.ManufacturerPartNumber)
                && string.IsNullOrWhiteSpace(line.ProductShortDescription))
                blockers.Add($"{label} part number or description");
            // `is null or <= 0`, not `<= 0`. With a nullable quantity the bare comparison is
            // FALSE for null, so an unstated quantity would have stopped being a blocker and the
            // line would have converted with nothing in it.
            if (quantity is null or <= 0) blockers.Add($"{label} quantity");
            if (string.IsNullOrWhiteSpace(unitOfMeasure)) blockers.Add($"{label} unit");
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

        /// <summary>
        /// The subset of <see cref="AttentionReason"/> an operator may never wave through:
        /// a missing quantity or a missing unit. Acknowledging "I don't know how many" produces
        /// an RFQ that cannot be quoted, so these are refused whatever the caller sends.
        ///
        /// <para>Typed rather than re-derived by matching the reason strings, so that changing
        /// the wording of a message can never silently make a hard failure waivable.</para>
        /// </summary>
        public string? BlockingReason { get; init; }

        public bool IsBlocked => BlockingReason is not null;

        /// <summary>Flagged, but every reason is soft — an explicit acknowledgement may proceed.</summary>
        public bool NeedsAcknowledgement => NeedsAttention && BlockingReason is null;
        /// <summary>Tenant UoM master data, exposed so ConvertAsync can canonicalize corrected UoM strings.</summary>
        public IUomVocabulary? UomVocabulary { get; init; }
    }

    /// <summary>Minimum characters for an acknowledgement reason. Matches the threshold the
    /// routing-override and No-Quote paths already enforce, so operators meet one rule.</summary>
    private const int MinimumAcknowledgementReasonLength = 5;

    /// <summary>
    /// Refuses conversion when a line the resolver flagged has not been dealt with, and returns
    /// a one-line summary of what WAS acknowledged so the lifecycle audit can record it.
    ///
    /// <para>Three outcomes per flagged line: a hard integrity failure is refused outright; a
    /// soft warning that was acknowledged with a reason proceeds and is audited; a soft warning
    /// that was neither corrected nor acknowledged is refused with the reason quoted back, so
    /// the operator is told what to fix rather than being handed a generic validation error.</para>
    /// </summary>
    private static string? EnforceLineWarningGovernance(
        IReadOnlyList<LeadItem> included,
        IReadOnlyDictionary<long, ResolvedLine> resolved,
        IReadOnlyDictionary<long, ConvertRequestItem> choices,
        ConvertRequest request)
    {
        static string Label(LeadItem li) =>
            string.IsNullOrWhiteSpace(li.LineItemNo) ? $"line {li.Id}" : $"line {li.LineItemNo}";

        var blocked = new List<string>();
        var unacknowledged = new List<string>();
        var acknowledged = new List<string>();
        var batchReason = request.WarningAcknowledgementReason?.Trim();

        foreach (var li in included)
        {
            if (!resolved.TryGetValue(li.Id, out var r) || !r.NeedsAttention) continue;
            choices.TryGetValue(li.Id, out var choice);

            // A missing quantity or unit is never waivable by ACKNOWLEDGEMENT — but it is
            // resolvable by CORRECTION, which is the whole point of the quantity and unit boxes
            // on the Review & Create RFQ screen. The resolver classifies from the persisted line,
            // so a line corrected in this request would otherwise stay blocked forever and the
            // operator would have no way to proceed: the gate would demand a fix it then ignored.
            // Re-evaluate the hard reasons against the values actually being converted.
            var effectiveQuantity = choice?.Quantity ?? li.Quantity;
            var effectiveUom = string.IsNullOrWhiteSpace(choice?.UnitOfMeasure)
                ? li.UnitOfMeasure
                : choice!.UnitOfMeasure;
            // Same reason as above: null is "not stated", which is exactly the condition this
            // gate exists to hold, so it must be spelled out rather than left to a comparison
            // that silently answers false.
            var stillBlocked = r.IsBlocked
                && (effectiveQuantity is null or <= 0 || string.IsNullOrWhiteSpace(effectiveUom));
            if (stillBlocked)
            {
                blocked.Add($"{Label(li)}: {r.BlockingReason}");
                continue;
            }

            // A line whose ONLY problem was the hard value, now corrected, needs no
            // acknowledgement — there is no longer anything to acknowledge.
            if (r.IsBlocked) continue;

            if (choice?.AcknowledgeWarning != true && !request.AcknowledgeAllWarnings)
            {
                unacknowledged.Add($"{Label(li)}: {r.AttentionReason}");
                continue;
            }

            var reason = string.IsNullOrWhiteSpace(choice?.AcknowledgementReason)
                ? batchReason
                : choice!.AcknowledgementReason!.Trim();
            if (string.IsNullOrWhiteSpace(reason) || reason.Length < MinimumAcknowledgementReasonLength)
                throw new InvalidOperationException(
                    $"Acknowledging the warning on {Label(li)} requires a reason of at least " +
                    $"{MinimumAcknowledgementReasonLength} characters. An acknowledgement without an explanation is not an audit trail.");

            acknowledged.Add($"{Label(li)} ({r.AttentionReason}) — {reason}");
        }

        if (blocked.Count > 0)
            throw new InvalidOperationException(
                "These lines cannot be converted because the request could not be read reliably, and no acknowledgement can " +
                $"substitute for the missing values — correct them on the lead first: {string.Join("; ", blocked)}.");

        if (unacknowledged.Count > 0)
            throw new InvalidOperationException(
                "These lines carry extraction warnings that must be corrected or explicitly acknowledged with a reason before " +
                $"the RFQ is created: {string.Join("; ", unacknowledged)}.");

        return acknowledged.Count > 0 ? string.Join(" | ", acknowledged) : null;
    }

    private sealed record Candidate(long Id, string? ProductName, string PartNo, string? ModelNo, string? Description);

    private async Task<Dictionary<long, ResolvedLine>> ResolveLinesAsync(
        IEnumerable<LeadItem> leadItems, long businessUnitId, CancellationToken ct)
    {
        var items = leadItems.ToList();
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
                Confidence = confidence,
                NormalizedQuantity = normalizedQty,
                NormalizedUom = uom.Value,
                UomId = uom.TenantUomId,
                NeedsAttention = reasons.Count > 0,
                AttentionReason = reasons.Count > 0 ? string.Join("; ", reasons) : null,
                BlockingReason = hard.Count > 0 ? string.Join("; ", hard) : null,
                UomVocabulary = uomVocabulary
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
    /// The line's usable quantity. NULL on LeadItem means the document stated none we could
    /// read; 0 or a dirty value means the same thing in practice. In either case try to recover
    /// a number from the raw UoM ("10 EA") or the item text fields, and return null — never a
    /// substituted zero — when there is nothing to recover.
    /// </summary>
    private static decimal? NormalizeQuantity(LeadItem item)
    {
        if (item.Quantity is > 0) return item.Quantity.Value;
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
    /// Standardize a raw UoM string. Delegates wholesale to the ONE canonicaliser
    /// (<c>Services/Uom/UomCanonicalizer.cs</c>), so the RFQ carries the same unit string the
    /// lead does.
    ///
    /// The previous miss-fallback here — <c>cleaned.ToUpperInvariant()</c> — is DELETED. It
    /// was the source of a whole spelling: 2,868 lead items say "each", and every one of them
    /// missed the tenant lookup and landed on the RFQ as "EACH", a sixth spelling that exists
    /// nowhere in the customer's document and matches nothing in the tenant's UoM table.
    /// Upper-casing a string is not canonicalisation.
    /// </summary>
    private static (string? uom, int? uomId) NormalizeUom(string? raw, IUomVocabulary? vocabulary)
    {
        var result = UomCanonicalizer.Canonicalize(raw, vocabulary);
        return (result.Value, result.TenantUomId);
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
