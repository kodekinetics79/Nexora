using System.Globalization;
using System.Text;
using System.Text.Json;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using ERP_RFQ_Automation.AI;

namespace ERP_RFQ_Automation.Boq;

/// <summary>
/// Service RFQ → BOQ engine (see BOQ-WIRING.md for the full write-up).
///
/// HONESTY RULES the whole engine is built around:
///   * Quantities are never invented. A line whose quantity the source does not
///     state is persisted with IsTbd = true, Quantity = 0, an EvidenceNote saying
///     what is missing, and is EXCLUDED from every total (tracked via TbdCount).
///   * Prices are never invented either — extracted lines carry no rate until a
///     human (or an assembly explosion using the tenant's own rate library) sets one.
///   * Drawings (SLD/layout files) try the vision seam first; without a configured
///     vision model the draft degrades to a clearly-labeled skeleton for manual
///     takeoff instead of pretending to read the diagram.
///
/// TENANCY — every query carries an explicit BusinessUnitId predicate on top of
/// the EF global filters (same defense-in-depth as PricingEngine), so the engine
/// stays correct on tenant-less context paths (copilot/background execution).
/// </summary>
public sealed class BoqBuilderService : IBoqBuilderService
{
    private const int MaxPageSize = 100;

    // Extensions/mime prefixes treated as engineering drawings / images for the
    // vision seam. PDFs are NOT listed: text-bearing PDFs already flow through the
    // normal extraction path owned by another work package.
    private static readonly string[] DrawingExtensions =
        { ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".bmp", ".webp", ".dwg", ".dxf", ".svg", ".vsd", ".vsdx" };

    private readonly ErpRfqAutomationContext _db;
    private readonly ILLMService _llm;
    private readonly IVisionDocumentReader _vision;
    private readonly ILogger<BoqBuilderService> _logger;

    public BoqBuilderService(
        ErpRfqAutomationContext db,
        ILLMService llm,
        IVisionDocumentReader vision,
        ILogger<BoqBuilderService> logger)
    {
        _db = db;
        _llm = llm;
        _vision = vision;
        _logger = logger;
    }

    // ------------------------------------------------------------------ draft

    public async Task<BoqDocumentDto> DraftFromTextAsync(BoqDraftRequest request, long businessUnitId, CancellationToken ct)
    {
        if (request is null)
            throw new ArgumentException("A draft request is required.");

        string? sourceText = request.Text;
        string title = (request.Title ?? "").Trim();
        string? drawingFileName = request.FileName;
        string? drawingMime = request.MimeType;

        if (request.LeadId is long leadId)
        {
            var lead = await _db.Leads.AsNoTracking()
                .Include(l => l.LeadItems)
                .FirstOrDefaultAsync(l => l.Id == leadId && l.BusinessUnitId == businessUnitId, ct);
            if (lead is null)
                throw new KeyNotFoundException($"Lead {leadId} was not found in this business unit.");

            sourceText = string.IsNullOrWhiteSpace(sourceText) ? BuildLeadScopeText(lead) : sourceText;
            if (title.Length == 0)
                title = $"Service BOQ — {lead.Rfqno ?? lead.OpportunityNo ?? ("Lead " + lead.Id)}";

            // Drawing attachments on the lead (loose ParentType/ParentId linkage) feed
            // the vision seam. The lead itself was tenant-checked above.
            if (drawingFileName is null)
            {
                var drawing = await _db.Attachments.AsNoTracking()
                    .Where(a => a.ParentType == "Lead" && a.ParentId == leadId)
                    .OrderBy(a => a.Id)
                    .Select(a => new { a.FileName, a.MimeType, a.FilePath })
                    .ToListAsync(ct);
                var firstDrawing = drawing.FirstOrDefault(a => IsDrawingFile(a.FileName, a.MimeType));
                if (firstDrawing != null)
                {
                    drawingFileName = firstDrawing.FileName;
                    drawingMime = firstDrawing.MimeType;
                }
            }
        }

        if (title.Length == 0)
            title = "Service BOQ draft";

        var isDrawing = IsDrawingFile(drawingFileName, drawingMime);
        string? drawingNote = null;

        if (isDrawing)
        {
            // Vision-first: a configured reader turns the drawing into scope text that
            // rides the normal LLM path; the placeholder answers honestly and we fall
            // back to the manual-takeoff skeleton.
            var visionResult = await _vision.ReadAsync(
                new VisionReadRequest(drawingFileName!, drawingMime, null), ct);
            if (visionResult.Success && !string.IsNullOrWhiteSpace(visionResult.ExtractedText))
            {
                sourceText = string.IsNullOrWhiteSpace(sourceText)
                    ? visionResult.ExtractedText
                    : sourceText + "\n\n[From drawing]\n" + visionResult.ExtractedText;
            }
            else
            {
                drawingNote = visionResult.FailureReason ?? NotConfiguredVisionReader.Reason;
            }
        }

        BoqDocument doc;
        if (drawingNote != null && string.IsNullOrWhiteSpace(sourceText))
        {
            // Drawing only + no vision model: honest skeleton, nothing invented.
            doc = BuildSkeletonDocument(businessUnitId, request, title, drawingNote);
        }
        else if (string.IsNullOrWhiteSpace(sourceText))
        {
            throw new ArgumentException("Provide either a leadId or scope text to draft from.");
        }
        else
        {
            var draft = await _llm.DraftServiceBoqAsync(sourceText,
                new AiCallContext(businessUnitId, AiPurposes.BoqDraft,
                    $"boq:{request.LeadId?.ToString() ?? "adhoc"}:{Guid.NewGuid():N}", "boq-draft-v1"), ct);
            if (draft is null)
            {
                // The model failed or its output was untrustworthy — degrade honestly
                // instead of fabricating a confident-looking BOQ.
                _logger.LogWarning("BOQ LLM draft unavailable/rejected; falling back to skeleton (BU {Bu})", businessUnitId);
                doc = BuildSkeletonDocument(businessUnitId, request, title,
                    "AI drafting was unavailable or its output failed validation — sections prepared for manual entry.");
            }
            else
            {
                doc = MapDraftToDocument(draft, businessUnitId, request, title);
            }
            if (drawingNote != null)
                doc.Notes = string.IsNullOrWhiteSpace(doc.Notes) ? drawingNote : doc.Notes + " " + drawingNote;
        }

        RecalcInMemory(doc);
        _db.Add(doc);
        await _db.SaveChangesAsync(ct);

        var assemblyCodes = await LoadAssemblyCodesAsync(businessUnitId, ct);
        return ToDto(doc, assemblyCodes);
    }

    private static string BuildLeadScopeText(Lead lead)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(lead.Rfqno)) sb.AppendLine($"RFQ No: {lead.Rfqno}");
        if (!string.IsNullOrWhiteSpace(lead.BuyersName)) sb.AppendLine($"Customer / buyer: {lead.BuyersName}");
        if (!string.IsNullOrWhiteSpace(lead.Rfqtype)) sb.AppendLine($"RFQ type: {lead.Rfqtype}");
        if (!string.IsNullOrWhiteSpace(lead.HeaderRemarks)) sb.AppendLine($"Remarks: {lead.HeaderRemarks}");
        sb.AppendLine();
        sb.AppendLine("Requested items / scope lines:");
        foreach (var item in lead.LeadItems.OrderBy(i => i.Id))
        {
            var desc = item.ProductShortDescription ?? item.ProductShortName ?? item.ItemText ?? item.CommodityProduct;
            if (string.IsNullOrWhiteSpace(desc)) continue;
            var qty = item.Quantity > 0 ? $" | qty {item.Quantity} {item.UnitOfMeasure ?? ""}".TrimEnd() : "";
            var extra = string.IsNullOrWhiteSpace(item.MaterialPotext) ? "" : $" | {item.MaterialPotext}";
            sb.AppendLine($"- {desc}{qty}{extra}");
        }
        return sb.ToString();
    }

    /// <summary>Manual-takeoff skeleton: standard service sections, one clearly-TBD placeholder line each.</summary>
    private static BoqDocument BuildSkeletonDocument(long businessUnitId, BoqDraftRequest request, string title, string note)
    {
        var now = DateTime.UtcNow;
        var doc = new BoqDocument
        {
            BusinessUnitId = businessUnitId,
            LeadId = request.LeadId,
            Title = title,
            ServiceCategory = BoqServiceCategory.Normalize(request.ServiceCategory),
            Status = BoqStatus.Draft,
            OverallConfidence = 0m,
            Notes = note,
            CreatedBy = request.CreatedBy,
            CreatedOn = now,
            UpdatedOn = now
        };

        var sections = new[] { "Supply", "Installation", "Testing & Commissioning" };
        for (int s = 0; s < sections.Length; s++)
        {
            var section = new BoqSection
            {
                BusinessUnitId = businessUnitId,
                Seq = s + 1,
                Title = sections[s]
            };
            section.Items.Add(new BoqItem
            {
                BusinessUnitId = businessUnitId,
                Seq = 1,
                Description = $"{sections[s]} scope — take off items manually",
                Unit = "lot",
                Quantity = 0m,
                ItemType = s == 0 ? BoqItemType.Material : BoqItemType.Labor,
                Source = BoqItemSource.Extracted,
                IsTbd = true,
                EvidenceNote = "Source could not be auto-read — add real lines and quantities."
            });
            doc.Sections.Add(section);
        }
        return doc;
    }

    private static BoqDocument MapDraftToDocument(BoqDraftResult draft, long businessUnitId, BoqDraftRequest request, string title)
    {
        var now = DateTime.UtcNow;
        var doc = new BoqDocument
        {
            BusinessUnitId = businessUnitId,
            LeadId = request.LeadId,
            Title = title,
            // An explicit category from the caller wins; otherwise the model's.
            ServiceCategory = BoqServiceCategory.Normalize(
                string.IsNullOrWhiteSpace(request.ServiceCategory) ? draft.ServiceCategory : request.ServiceCategory),
            Status = BoqStatus.Draft,
            OverallConfidence = ClampConfidence(draft.OverallConfidence),
            AssumptionsJson = draft.Assumptions is { Count: > 0 }
                ? JsonSerializer.Serialize(draft.Assumptions)
                : null,
            CreatedBy = request.CreatedBy,
            CreatedOn = now,
            UpdatedOn = now
        };

        int sectionSeq = 0;
        foreach (var s in draft.Sections ?? new List<BoqDraftSection>())
        {
            var items = s.Items ?? new List<BoqDraftItem>();
            if (items.Count == 0) continue;

            var section = new BoqSection
            {
                BusinessUnitId = businessUnitId,
                Seq = ++sectionSeq,
                Title = Truncate(string.IsNullOrWhiteSpace(s.Title) ? $"Section {sectionSeq}" : s.Title!.Trim(), 200)
            };

            int itemSeq = 0;
            foreach (var i in items)
            {
                if (string.IsNullOrWhiteSpace(i.Description)) continue;

                // The honesty invariant: unstated quantity -> TBD, quantity zeroed.
                var quantity = i.Quantity;
                var tbd = i.Tbd == true || quantity is null or <= 0;

                section.Items.Add(new BoqItem
                {
                    BusinessUnitId = businessUnitId,
                    Seq = ++itemSeq,
                    ItemCode = Truncate(i.ItemCode, 64),
                    Description = Truncate(i.Description!.Trim(), 2000)!,
                    Unit = Truncate(string.IsNullOrWhiteSpace(i.Unit) ? "lot" : i.Unit!.Trim(), 20)!,
                    Quantity = tbd ? 0m : Math.Round(quantity!.Value, 3, MidpointRounding.AwayFromZero),
                    ItemType = BoqItemType.Normalize(i.ItemType),
                    Source = BoqItemSource.Extracted,
                    Confidence = ClampConfidence(i.Confidence),
                    IsTbd = tbd,
                    EvidenceNote = Truncate(
                        !string.IsNullOrWhiteSpace(i.TbdReason) ? i.TbdReason
                        : tbd ? "Quantity not stated in the source — needs a human number."
                        : null, 1000)
                });
            }

            if (section.Items.Count > 0)
                doc.Sections.Add(section);
        }

        // A draft with no usable lines still gets an honest skeleton rather than an empty shell.
        if (doc.Sections.Count == 0)
        {
            var skeleton = BuildSkeletonDocument(businessUnitId, request, title,
                "The AI draft produced no usable lines — sections prepared for manual entry.");
            skeleton.OverallConfidence = 0m;
            return skeleton;
        }

        return doc;
    }

    // ------------------------------------------------------------------ read

    public async Task<BoqListResultDto> ListAsync(long businessUnitId, int page, int pageSize, string? status, string? search, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var query = _db.Set<BoqDocument>().AsNoTracking()
            .Where(d => d.BusinessUnitId == businessUnitId);

        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(d => d.Status == status);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(d => EF.Functions.Like(d.Title, $"%{term}%"));
        }

        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderByDescending(d => d.UpdatedOn)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(d => new BoqListItemDto
            {
                Id = d.Id,
                LeadId = d.LeadId,
                Title = d.Title,
                ServiceCategory = d.ServiceCategory,
                Status = d.Status,
                OverallConfidence = d.OverallConfidence,
                TotalAmount = d.TotalAmount,
                TbdCount = d.TbdCount,
                ItemCount = d.Sections.SelectMany(s => s.Items).Count(),
                CreatedOn = d.CreatedOn,
                UpdatedOn = d.UpdatedOn
            })
            .ToListAsync(ct);

        return new BoqListResultDto { Items = rows, TotalCount = total, Page = page, PageSize = pageSize };
    }

    public async Task<BoqDocumentDto?> GetAsync(long boqDocumentId, long businessUnitId, CancellationToken ct)
    {
        var doc = await LoadTreeAsync(boqDocumentId, businessUnitId, track: false, ct);
        if (doc is null) return null;
        var codes = await LoadAssemblyCodesAsync(businessUnitId, ct);
        return ToDto(doc, codes);
    }

    // ------------------------------------------------------------------ update / approve

    public async Task<BoqDocumentDto?> UpdateAsync(long boqDocumentId, long businessUnitId, BoqUpdateRequest request, CancellationToken ct)
    {
        if (request is null)
            throw new ArgumentException("An update payload is required.");

        var doc = await LoadTreeAsync(boqDocumentId, businessUnitId, track: true, ct);
        if (doc is null) return null;
        if (doc.Status == BoqStatus.Approved)
            throw new InvalidOperationException("This BOQ is approved and locked. Reopen it before editing.");

        // ---- header (only provided fields are applied — review-workbench convention) ----
        var header = request.Header;
        if (header != null)
        {
            if (!string.IsNullOrWhiteSpace(header.Title)) doc.Title = Truncate(header.Title.Trim(), 300)!;
            if (header.ServiceCategory != null) doc.ServiceCategory = BoqServiceCategory.Normalize(header.ServiceCategory);
            if (header.Notes != null) doc.Notes = Truncate(header.Notes, 4000);
            if (header.Assumptions != null)
                doc.AssumptionsJson = header.Assumptions.Count > 0 ? JsonSerializer.Serialize(header.Assumptions) : null;
            if (!string.IsNullOrWhiteSpace(header.Status))
            {
                if (header.Status != BoqStatus.Draft && header.Status != BoqStatus.InReview)
                    throw new ArgumentException("Status can only be set to Draft or InReview here; use the approve endpoint.");
                doc.Status = header.Status;
            }
        }

        // ---- sections/items upsert: match by Id, insert new (Id null/0), delete the rest ----
        if (request.Sections != null)
        {
            var keptSectionIds = request.Sections
                .Where(s => s.Id is > 0).Select(s => s.Id!.Value).ToHashSet();
            var sectionsToRemove = doc.Sections.Where(s => !keptSectionIds.Contains(s.Id)).ToList();
            foreach (var gone in sectionsToRemove)
            {
                _db.RemoveRange(gone.Items);
                _db.Remove(gone);
                doc.Sections.Remove(gone);
            }

            foreach (var sDto in request.Sections.OrderBy(s => s.Seq))
            {
                BoqSection section;
                if (sDto.Id is > 0)
                {
                    var existing = doc.Sections.FirstOrDefault(s => s.Id == sDto.Id.Value);
                    if (existing == null) continue; // stale/foreign id; ignore rather than trust it
                    section = existing;
                }
                else
                {
                    section = new BoqSection { BusinessUnitId = businessUnitId };
                    doc.Sections.Add(section);
                }

                section.Seq = sDto.Seq;
                section.Title = Truncate(string.IsNullOrWhiteSpace(sDto.Title) ? "Section" : sDto.Title.Trim(), 200)!;

                var keptItemIds = sDto.Items.Where(i => i.Id is > 0).Select(i => i.Id!.Value).ToHashSet();
                var itemsToRemove = section.Items.Where(i => !keptItemIds.Contains(i.Id)).ToList();
                foreach (var gone in itemsToRemove)
                {
                    _db.Remove(gone);
                    section.Items.Remove(gone);
                }

                foreach (var iDto in sDto.Items.OrderBy(i => i.Seq))
                {
                    BoqItem item;
                    if (iDto.Id is > 0)
                    {
                        var existing = section.Items.FirstOrDefault(i => i.Id == iDto.Id.Value);
                        if (existing == null) continue;
                        item = existing;
                        // A human editing an extracted line takes ownership of it.
                        if (item.Source == BoqItemSource.Extracted && HasItemChanged(item, iDto))
                            item.Source = BoqItemSource.Manual;
                    }
                    else
                    {
                        item = new BoqItem { BusinessUnitId = businessUnitId, Source = BoqItemSource.Manual };
                        section.Items.Add(item);
                    }

                    item.Seq = iDto.Seq;
                    item.ItemCode = Truncate(iDto.ItemCode, 64);
                    item.Description = Truncate(string.IsNullOrWhiteSpace(iDto.Description) ? "Item" : iDto.Description.Trim(), 2000)!;
                    item.Unit = Truncate(string.IsNullOrWhiteSpace(iDto.Unit) ? "lot" : iDto.Unit.Trim(), 20)!;
                    item.Quantity = Math.Round(Math.Max(0m, iDto.Quantity), 3, MidpointRounding.AwayFromZero);
                    item.ItemType = BoqItemType.Normalize(iDto.ItemType);
                    item.UnitRate = iDto.UnitRate is >= 0 ? iDto.UnitRate : null;
                    // Supplying a real quantity clears TBD unless the client insists.
                    item.IsTbd = iDto.IsTbd || item.Quantity <= 0m;
                    item.AssemblyCode = Truncate(iDto.AssemblyCode, 64);
                    item.EvidenceNote = Truncate(iDto.EvidenceNote, 1000);
                }
            }
        }

        RecalcInMemory(doc);
        doc.UpdatedOn = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        var codes = await LoadAssemblyCodesAsync(businessUnitId, ct);
        return ToDto(doc, codes);
    }

    private static bool HasItemChanged(BoqItem item, BoqItemUpdate dto) =>
        item.Description != dto.Description
        || item.Unit != dto.Unit
        || item.Quantity != dto.Quantity
        || item.ItemType != BoqItemType.Normalize(dto.ItemType)
        || item.UnitRate != dto.UnitRate
        || item.IsTbd != dto.IsTbd;

    public async Task<BoqDocumentDto?> ApproveAsync(long boqDocumentId, long businessUnitId, string? approvedBy, CancellationToken ct)
    {
        var doc = await LoadTreeAsync(boqDocumentId, businessUnitId, track: true, ct);
        if (doc is null) return null;

        RecalcInMemory(doc);
        if (doc.TbdCount > 0)
            throw new InvalidOperationException(
                $"{doc.TbdCount} item(s) still need details (quantities or units). Resolve them before approving.");

        doc.Status = BoqStatus.Approved;
        doc.ApprovedBy = approvedBy;
        doc.ApprovedOn = DateTime.UtcNow;
        doc.UpdatedOn = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        var codes = await LoadAssemblyCodesAsync(businessUnitId, ct);
        return ToDto(doc, codes);
    }

    // ------------------------------------------------------------------ assemblies

    public async Task<IReadOnlyList<BoqAssemblyDto>> GetAssembliesAsync(long businessUnitId, CancellationToken ct)
    {
        await SeedStarterAssembliesAsync(businessUnitId, ct);

        return await _db.Set<BoqAssembly>().AsNoTracking()
            .Where(a => a.BusinessUnitId == businessUnitId)
            .OrderBy(a => a.Code)
            .Select(a => new BoqAssemblyDto
            {
                Id = a.Id,
                Code = a.Code,
                Name = a.Name,
                Description = a.Description,
                ServiceCategory = a.ServiceCategory,
                Unit = a.Unit,
                IsStarter = a.IsStarter,
                Components = a.Components.OrderBy(c => c.Seq).Select(c => new BoqAssemblyComponentDto
                {
                    Id = c.Id,
                    Seq = c.Seq,
                    Description = c.Description,
                    Unit = c.Unit,
                    QtyPer = c.QtyPer,
                    ItemType = c.ItemType,
                    DefaultRate = c.DefaultRate
                }).ToList()
            })
            .ToListAsync(ct);
    }

    /// <summary>
    /// Idempotent lazy seed: only fires when the tenant has NO assemblies at all, so
    /// tenant edits (including deleting most starters) are never overwritten. The
    /// unique (BusinessUnitId, Code) index backstops races.
    /// </summary>
    public async Task SeedStarterAssembliesAsync(long businessUnitId, CancellationToken ct)
    {
        var any = await _db.Set<BoqAssembly>()
            .AnyAsync(a => a.BusinessUnitId == businessUnitId, ct);
        if (any) return;

        var now = DateTime.UtcNow;
        foreach (var starter in BoqStarterAssemblies.All)
        {
            var assembly = new BoqAssembly
            {
                BusinessUnitId = businessUnitId,
                Code = starter.Code,
                Name = starter.Name,
                Description = starter.Description,
                ServiceCategory = starter.ServiceCategory,
                Unit = starter.Unit,
                IsStarter = true,
                CreatedOn = now,
                UpdatedOn = now
            };
            int seq = 0;
            foreach (var c in starter.Components)
            {
                assembly.Components.Add(new BoqAssemblyComponent
                {
                    BusinessUnitId = businessUnitId,
                    Seq = ++seq,
                    Description = c.Description,
                    Unit = c.Unit,
                    QtyPer = c.QtyPer,
                    ItemType = c.ItemType,
                    DefaultRate = c.DefaultRate
                });
            }
            _db.Add(assembly);
        }

        try
        {
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("Seeded {Count} starter BOQ assemblies for BU {Bu}",
                BoqStarterAssemblies.All.Count, businessUnitId);
        }
        catch (DbUpdateException ex)
        {
            // A concurrent request seeded first — the unique index did its job.
            _logger.LogDebug(ex, "Starter assembly seed race for BU {Bu}; another writer won.", businessUnitId);
            _db.ChangeTracker.Clear();
        }
    }

    public async Task<BoqDocumentDto> ExplodeAssemblyAsync(long boqItemId, long businessUnitId, string? assemblyCode, CancellationToken ct)
    {
        await SeedStarterAssembliesAsync(businessUnitId, ct);

        var item = await _db.Set<BoqItem>()
            .FirstOrDefaultAsync(i => i.Id == boqItemId && i.BusinessUnitId == businessUnitId, ct);
        if (item is null)
            throw new KeyNotFoundException($"BOQ item {boqItemId} was not found in this business unit.");

        var section = await _db.Set<BoqSection>()
            .Include(s => s.Items)
            .Include(s => s.Document)
            .FirstOrDefaultAsync(s => s.Id == item.BoqSectionId && s.BusinessUnitId == businessUnitId, ct);
        if (section is null)
            throw new KeyNotFoundException($"BOQ item {boqItemId} has no section in this business unit.");
        if (section.Document.Status == BoqStatus.Approved)
            throw new InvalidOperationException("This BOQ is approved and locked. Reopen it before exploding assemblies.");

        var code = (assemblyCode ?? item.AssemblyCode ?? "").Trim();
        if (code.Length == 0)
            throw new ArgumentException("This item has no assembly code. Pick an assembly from the library first.");

        if (item.IsTbd || item.Quantity <= 0m)
            throw new InvalidOperationException(
                "This item needs a quantity before it can be exploded — the assembly multiplies its components by the item quantity.");

        var assembly = await _db.Set<BoqAssembly>().AsNoTracking()
            .Include(a => a.Components)
            .FirstOrDefaultAsync(a => a.BusinessUnitId == businessUnitId && a.Code == code, ct);
        if (assembly is null)
            throw new KeyNotFoundException($"Assembly '{code}' was not found in this business unit's library.");
        if (assembly.Components.Count == 0)
            throw new InvalidOperationException($"Assembly '{code}' has no components to explode.");

        // Replace the item with its component lines at the same position, quantities
        // multiplied by the parent quantity, rates from the tenant library.
        var parentQty = item.Quantity;
        var parentDescription = item.Description;
        var insertAtSeq = item.Seq;

        section.Items.Remove(item);
        _db.Remove(item);

        var newItems = assembly.Components.OrderBy(c => c.Seq).Select((c, idx) => new BoqItem
        {
            BusinessUnitId = businessUnitId,
            BoqSectionId = section.Id,
            Seq = insertAtSeq + idx,
            ItemCode = assembly.Code,
            Description = c.Description,
            Unit = c.Unit,
            Quantity = Math.Round(parentQty * c.QtyPer, 3, MidpointRounding.AwayFromZero),
            ItemType = c.ItemType,
            UnitRate = c.DefaultRate,
            Source = BoqItemSource.Assembly,
            Confidence = null,
            IsTbd = false,
            AssemblyCode = assembly.Code,
            EvidenceNote = Truncate(
                $"From assembly {assembly.Code} ({assembly.Name}) × {FmtQty(parentQty)} — was: {parentDescription}", 1000)
        }).ToList();

        // Shift the trailing items to keep Seq contiguous and stable. The exploded
        // item was removed, so the net insertion is (components − 1) positions.
        var shift = newItems.Count - 1;
        if (shift > 0)
            foreach (var trailing in section.Items.Where(i => i.Seq > insertAtSeq).OrderBy(i => i.Seq))
                trailing.Seq += shift;
        foreach (var ni in newItems)
            section.Items.Add(ni);

        RecalcSection(section);
        await RecalcDocumentFromDbAsync(section.Document, ct);
        section.Document.UpdatedOn = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        var full = await LoadTreeAsync(section.BoqDocumentId, businessUnitId, track: false, ct)
                   ?? throw new KeyNotFoundException("BOQ document disappeared during explode.");
        var codes = await LoadAssemblyCodesAsync(businessUnitId, ct);
        return ToDto(full, codes);
    }

    // ------------------------------------------------------------------ totals

    public async Task RecalcTotalsAsync(long boqDocumentId, long businessUnitId, CancellationToken ct)
    {
        var doc = await LoadTreeAsync(boqDocumentId, businessUnitId, track: true, ct);
        if (doc is null)
            throw new KeyNotFoundException($"BOQ {boqDocumentId} was not found in this business unit.");

        RecalcInMemory(doc);
        doc.UpdatedOn = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Pure recomputation over a loaded tree:
    ///   line total = qty × rate when a rate exists and the line is not TBD;
    ///   TBD lines get a null total and are counted in TbdCount instead;
    ///   section total = sum of line totals; document total = sum of section totals.
    /// </summary>
    internal static void RecalcInMemory(BoqDocument doc)
    {
        foreach (var section in doc.Sections)
            RecalcSection(section);
        doc.TotalAmount = Round2(doc.Sections.Sum(s => s.TotalAmount));
        doc.TbdCount = doc.Sections.SelectMany(s => s.Items).Count(i => i.IsTbd);
    }

    private static void RecalcSection(BoqSection section)
    {
        foreach (var item in section.Items)
        {
            item.TotalAmount = !item.IsTbd && item.UnitRate.HasValue
                ? Round2(item.Quantity * item.UnitRate.Value)
                : null;
        }
        section.TotalAmount = Round2(section.Items.Sum(i => i.TotalAmount ?? 0m));
    }

    /// <summary>Document-level rollup when only ONE section is loaded in memory (explode path).</summary>
    private async Task RecalcDocumentFromDbAsync(BoqDocument doc, CancellationToken ct)
    {
        // Other sections' stored totals are still valid — only the edited one changed.
        var otherSections = await _db.Set<BoqSection>().AsNoTracking()
            .Where(s => s.BoqDocumentId == doc.Id)
            .Select(s => new { s.Id, s.TotalAmount })
            .ToListAsync(ct);
        var loadedIds = doc.Sections.Select(s => s.Id).ToHashSet();

        var total = doc.Sections.Sum(s => s.TotalAmount)
                    + otherSections.Where(s => !loadedIds.Contains(s.Id)).Sum(s => s.TotalAmount);
        doc.TotalAmount = Round2(total);

        var loadedTbd = doc.Sections.SelectMany(s => s.Items).Count(i => i.IsTbd);
        var otherTbd = await _db.Set<BoqItem>().AsNoTracking()
            .Where(i => i.Section.BoqDocumentId == doc.Id && !loadedIds.Contains(i.BoqSectionId) && i.IsTbd)
            .CountAsync(ct);
        doc.TbdCount = loadedTbd + otherTbd;
    }

    // ------------------------------------------------------------------ export

    public async Task<string?> ExportCsvAsync(long boqDocumentId, long businessUnitId, CancellationToken ct)
    {
        var doc = await LoadTreeAsync(boqDocumentId, businessUnitId, track: false, ct);
        if (doc is null) return null;

        var sb = new StringBuilder();
        sb.AppendLine($"# {Csv(doc.Title)} — {doc.ServiceCategory} — {doc.Status}");
        sb.AppendLine("Section,Line,Item Code,Description,Unit,Quantity,Type,Unit Rate,Line Total,Needs Details,Source,Note");

        foreach (var section in doc.Sections.OrderBy(s => s.Seq))
        {
            foreach (var item in section.Items.OrderBy(i => i.Seq))
            {
                sb.AppendLine(string.Join(",",
                    Csv(section.Title),
                    item.Seq.ToString(CultureInfo.InvariantCulture),
                    Csv(item.ItemCode),
                    Csv(item.Description),
                    Csv(item.Unit),
                    item.IsTbd ? "TBD" : FmtQty(item.Quantity),
                    Csv(item.ItemType),
                    item.UnitRate.HasValue ? FmtMoney(item.UnitRate.Value) : "",
                    item.TotalAmount.HasValue ? FmtMoney(item.TotalAmount.Value) : "",
                    item.IsTbd ? "YES" : "",
                    Csv(item.Source),
                    Csv(item.EvidenceNote)));
            }
            sb.AppendLine(string.Join(",",
                Csv($"{section.Title} — subtotal"), "", "", "", "", "", "", "",
                FmtMoney(section.TotalAmount), "", "", ""));
        }

        sb.AppendLine(string.Join(",",
            Csv("GRAND TOTAL (priced lines only)"), "", "", "", "", "", "", "",
            FmtMoney(doc.TotalAmount), "", "", ""));
        if (doc.TbdCount > 0)
            sb.AppendLine(string.Join(",",
                Csv($"NOTE: {doc.TbdCount} line(s) still need details and are excluded from the total"),
                "", "", "", "", "", "", "", "", "", "", ""));

        var assumptions = DeserializeAssumptions(doc.AssumptionsJson);
        foreach (var a in assumptions)
            sb.AppendLine(string.Join(",", Csv($"ASSUMPTION: {a}"), "", "", "", "", "", "", "", "", "", "", ""));

        return sb.ToString();
    }

    // ------------------------------------------------------------------ helpers

    private async Task<BoqDocument?> LoadTreeAsync(long id, long businessUnitId, bool track, CancellationToken ct)
    {
        var query = _db.Set<BoqDocument>()
            .Include(d => d.Sections.OrderBy(s => s.Seq))
            .ThenInclude(s => s.Items.OrderBy(i => i.Seq))
            .Where(d => d.Id == id && d.BusinessUnitId == businessUnitId);
        if (!track) query = query.AsNoTracking();
        return await query.FirstOrDefaultAsync(ct);
    }

    private async Task<HashSet<string>> LoadAssemblyCodesAsync(long businessUnitId, CancellationToken ct)
    {
        var codes = await _db.Set<BoqAssembly>().AsNoTracking()
            .Where(a => a.BusinessUnitId == businessUnitId)
            .Select(a => a.Code)
            .ToListAsync(ct);
        return codes.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    internal static bool IsDrawingFile(string? fileName, string? mimeType)
    {
        if (!string.IsNullOrWhiteSpace(mimeType))
        {
            var m = mimeType.Trim().ToLowerInvariant();
            if (m.StartsWith("image/")) return true;
            if (m.Contains("dwg") || m.Contains("dxf") || m.Contains("vnd.visio")) return true;
        }
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            var ext = Path.GetExtension(fileName.Trim()).ToLowerInvariant();
            if (DrawingExtensions.Contains(ext)) return true;
        }
        return false;
    }

    private static BoqDocumentDto ToDto(BoqDocument doc, HashSet<string> assemblyCodes) => new()
    {
        Id = doc.Id,
        LeadId = doc.LeadId,
        Title = doc.Title,
        ServiceCategory = doc.ServiceCategory,
        Status = doc.Status,
        OverallConfidence = doc.OverallConfidence,
        Notes = doc.Notes,
        Assumptions = DeserializeAssumptions(doc.AssumptionsJson),
        TotalAmount = doc.TotalAmount,
        TbdCount = doc.TbdCount,
        ItemCount = doc.Sections.Sum(s => s.Items.Count),
        CreatedBy = doc.CreatedBy,
        CreatedOn = doc.CreatedOn,
        UpdatedOn = doc.UpdatedOn,
        ApprovedBy = doc.ApprovedBy,
        ApprovedOn = doc.ApprovedOn,
        Sections = doc.Sections.OrderBy(s => s.Seq).Select(s => new BoqSectionDto
        {
            Id = s.Id,
            Seq = s.Seq,
            Title = s.Title,
            TotalAmount = s.TotalAmount,
            Items = s.Items.OrderBy(i => i.Seq).Select(i => new BoqItemDto
            {
                Id = i.Id,
                Seq = i.Seq,
                ItemCode = i.ItemCode,
                Description = i.Description,
                Unit = i.Unit,
                Quantity = i.Quantity,
                ItemType = i.ItemType,
                UnitRate = i.UnitRate,
                TotalAmount = i.TotalAmount,
                Source = i.Source,
                Confidence = i.Confidence,
                IsTbd = i.IsTbd,
                AssemblyCode = i.AssemblyCode,
                CanExplode = !string.IsNullOrWhiteSpace(i.AssemblyCode)
                             && i.Source != BoqItemSource.Assembly
                             && assemblyCodes.Contains(i.AssemblyCode!),
                EvidenceNote = i.EvidenceNote
            }).ToList()
        }).ToList()
    };

    private static List<string> DeserializeAssumptions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<string>();
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch (JsonException)
        {
            return new List<string>();
        }
    }

    private static decimal? ClampConfidence(double? raw) =>
        raw is null ? null : Math.Round((decimal)Math.Clamp(raw.Value, 0.0, 1.0), 2, MidpointRounding.AwayFromZero);

    private static decimal Round2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    private static string FmtQty(decimal v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    private static string FmtMoney(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var needsQuoting = value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r');
        return needsQuoting ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }

    private static string? Truncate(string? s, int max) =>
        s is null ? null : (s.Length <= max ? s : s[..max]);
}
