using System.Text.Json.Serialization;

namespace ERP_RFQ_Automation.Boq;

// ─── Wire contracts for the BOQ engine (WP-BOQ) ──────────────────────────────
// Mirrors the Intelligence/Pricing DTO conventions: sealed POCOs, camelCased by
// the default JSON options, model scores kept 0..1 (the frontend maps them to
// High/Medium/Low — never shown raw).

// ---- requests ----

/// <summary>POST /api/boq/draft body. Either LeadId or Text must be provided.</summary>
public sealed class BoqDraftRequest
{
    /// <summary>Draft from an existing lead's extracted content.</summary>
    public long? LeadId { get; set; }

    public string? Title { get; set; }

    /// <summary>Raw scope-of-work text to draft from (ad-hoc path).</summary>
    public string? Text { get; set; }

    /// <summary>Optional category hint; the model's own classification wins when absent.</summary>
    public string? ServiceCategory { get; set; }

    /// <summary>Original file name when the request stems from an uploaded document — used for drawing detection.</summary>
    public string? FileName { get; set; }

    public string? MimeType { get; set; }

    /// <summary>Set server-side from the JWT — never trusted from the body.</summary>
    [JsonIgnore]
    public string? CreatedBy { get; set; }
}

/// <summary>PUT /api/boq/{id} body — full-tree upsert, review-workbench style
/// (sections/items matched by Id; Id null/0 inserts; rows missing from the payload are deleted).</summary>
public sealed class BoqUpdateRequest
{
    public BoqHeaderUpdate? Header { get; set; }
    public List<BoqSectionUpdate>? Sections { get; set; }
}

public sealed class BoqHeaderUpdate
{
    public string? Title { get; set; }
    public string? ServiceCategory { get; set; }
    public string? Notes { get; set; }
    /// <summary>Draft | InReview. (Approval goes through POST /approve only.)</summary>
    public string? Status { get; set; }
    public List<string>? Assumptions { get; set; }
}

public sealed class BoqSectionUpdate
{
    public long? Id { get; set; }
    public int Seq { get; set; }
    public string Title { get; set; } = string.Empty;
    public List<BoqItemUpdate> Items { get; set; } = new();
}

public sealed class BoqItemUpdate
{
    public long? Id { get; set; }
    public int Seq { get; set; }
    public string? ItemCode { get; set; }
    public string Description { get; set; } = string.Empty;
    public string Unit { get; set; } = "lot";
    public decimal Quantity { get; set; }
    public string ItemType { get; set; } = BoqItemType.Material;
    public decimal? UnitRate { get; set; }
    public bool IsTbd { get; set; }
    public string? AssemblyCode { get; set; }
    public string? EvidenceNote { get; set; }
}

// ---- responses ----

public sealed class BoqDocumentDto
{
    public long Id { get; set; }
    public long? LeadId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string ServiceCategory { get; set; } = "other";
    public string Status { get; set; } = BoqStatus.Draft;

    /// <summary>0..1 — never shown raw in the UI.</summary>
    public decimal? OverallConfidence { get; set; }

    public string? Notes { get; set; }
    public List<string> Assumptions { get; set; } = new();
    public decimal TotalAmount { get; set; }
    public int TbdCount { get; set; }
    public int ItemCount { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime CreatedOn { get; set; }
    public DateTime UpdatedOn { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedOn { get; set; }
    public List<BoqSectionDto> Sections { get; set; } = new();
}

public sealed class BoqSectionDto
{
    public long Id { get; set; }
    public int Seq { get; set; }
    public string Title { get; set; } = string.Empty;
    public decimal TotalAmount { get; set; }
    public List<BoqItemDto> Items { get; set; } = new();
}

public sealed class BoqItemDto
{
    public long Id { get; set; }
    public int Seq { get; set; }
    public string? ItemCode { get; set; }
    public string Description { get; set; } = string.Empty;
    public string Unit { get; set; } = "lot";
    public decimal Quantity { get; set; }
    public string ItemType { get; set; } = BoqItemType.Material;
    public decimal? UnitRate { get; set; }
    public decimal? TotalAmount { get; set; }
    public string Source { get; set; } = BoqItemSource.Manual;

    /// <summary>0..1 — never shown raw in the UI.</summary>
    public decimal? Confidence { get; set; }

    public bool IsTbd { get; set; }
    public string? AssemblyCode { get; set; }

    /// <summary>True when this tenant's assembly library has a matching AssemblyCode (explode is possible).</summary>
    public bool CanExplode { get; set; }

    public string? EvidenceNote { get; set; }
}

/// <summary>GET /api/boq list row.</summary>
public sealed class BoqListItemDto
{
    public long Id { get; set; }
    public long? LeadId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string ServiceCategory { get; set; } = "other";
    public string Status { get; set; } = BoqStatus.Draft;
    public decimal? OverallConfidence { get; set; }
    public decimal TotalAmount { get; set; }
    public int TbdCount { get; set; }
    public int ItemCount { get; set; }
    public DateTime CreatedOn { get; set; }
    public DateTime UpdatedOn { get; set; }
}

public sealed class BoqListResultDto
{
    public List<BoqListItemDto> Items { get; set; } = new();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}

public sealed class BoqAssemblyDto
{
    public long Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string ServiceCategory { get; set; } = "other";
    public string Unit { get; set; } = "EA";
    public bool IsStarter { get; set; }
    public List<BoqAssemblyComponentDto> Components { get; set; } = new();
}

public sealed class BoqAssemblyComponentDto
{
    public long Id { get; set; }
    public int Seq { get; set; }
    public string Description { get; set; } = string.Empty;
    public string Unit { get; set; } = "EA";
    public decimal QtyPer { get; set; }
    public string ItemType { get; set; } = BoqItemType.Material;
    public decimal? DefaultRate { get; set; }
}
