using System;
using System.Collections.Generic;

namespace ERP_RFQ_Automation.Boq;

// ─── Service RFQ → BOQ (Bill of Quantities) domain (WP-BOQ) ──────────────────
//
// All entities are tenant-scoped: they carry `long BusinessUnitId` and get the
// SAME fail-closed global query filter as every commercial document (ADR-0005).
// Entity configuration lives in Models/ErpRfqAutomationContext.Boq.cs
// (ConfigureBoqModel partial, spliced from the Tenancy partial).
//
// Honest-by-design: quantities the source document does not state are NEVER
// invented — such items carry IsTbd = true, are excluded from totals, and are
// surfaced to humans ("Needs a quantity") in the editor UI.

/// <summary>Allowed <see cref="BoqDocument.Status"/> values (string column).</summary>
public static class BoqStatus
{
    public const string Draft = "Draft";
    public const string InReview = "InReview";
    public const string Approved = "Approved";

    public static readonly string[] All = { Draft, InReview, Approved };
}

/// <summary>Allowed <see cref="BoqItem.ItemType"/> values (string column).</summary>
public static class BoqItemType
{
    public const string Material = "Material";
    public const string Labor = "Labor";
    public const string Equipment = "Equipment";
    public const string Subcontract = "Subcontract";

    public static readonly string[] All = { Material, Labor, Equipment, Subcontract };

    /// <summary>Normalizes free-form model/user input to a canonical type; defaults to Material.</summary>
    public static string Normalize(string? raw)
    {
        var t = (raw ?? "").Trim();
        foreach (var known in All)
            if (string.Equals(known, t, StringComparison.OrdinalIgnoreCase))
                return known;
        // Common synonyms the LLM may emit.
        return t.ToLowerInvariant() switch
        {
            "labour" or "manpower" or "workmanship" => Labor,
            "plant" or "machinery" or "tools" => Equipment,
            "subcontractor" or "sub-contract" or "specialist" => Subcontract,
            _ => Material
        };
    }
}

/// <summary>Allowed <see cref="BoqItem.Source"/> values (string column).</summary>
public static class BoqItemSource
{
    public const string Extracted = "extracted";
    public const string Assembly = "assembly";
    public const string Manual = "manual";
}

/// <summary>Service categories a BOQ can belong to (string column, lowercase).</summary>
public static class BoqServiceCategory
{
    public static readonly string[] All =
        { "electrical", "mechanical", "civil", "maintenance", "manpower", "mixed", "other" };

    public static string Normalize(string? raw)
    {
        var t = (raw ?? "").Trim().ToLowerInvariant();
        foreach (var known in All)
            if (known == t) return known;
        return "other";
    }
}

/// <summary>
/// A priced (or partially priced) bill of quantities drafted from a service
/// request — maintenance scope, install/commissioning, supply-and-install,
/// manpower hire, or (vision-pending) an SLD/drawing takeoff.
/// </summary>
public class BoqDocument
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }

    /// <summary>Source lead when drafted from a lead; null for ad-hoc text drafts.</summary>
    public long? LeadId { get; set; }

    public string Title { get; set; } = null!;

    /// <summary>electrical | mechanical | civil | maintenance | manpower | mixed | other.</summary>
    public string ServiceCategory { get; set; } = "other";

    /// <summary>Draft | InReview | Approved.</summary>
    public string Status { get; set; } = BoqStatus.Draft;

    /// <summary>0..1 model confidence for the draft as a whole; null when not AI-drafted.</summary>
    public decimal? OverallConfidence { get; set; }

    public string? Notes { get; set; }

    /// <summary>JSON array of assumption strings the drafting model declared (jsonb).</summary>
    public string? AssumptionsJson { get; set; }

    /// <summary>Sum of priced line totals. TBD items are excluded (tracked via TbdCount).</summary>
    public decimal TotalAmount { get; set; }

    /// <summary>Count of items still needing human input (IsTbd). Recomputed on every recalc.</summary>
    public int TbdCount { get; set; }

    public string? CreatedBy { get; set; }
    public DateTime CreatedOn { get; set; }
    public DateTime UpdatedOn { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedOn { get; set; }

    public virtual ICollection<BoqSection> Sections { get; set; } = new List<BoqSection>();
}

/// <summary>An ordered grouping of BOQ items, e.g. "Supply", "Installation", "Testing &amp; Commissioning".</summary>
public class BoqSection
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long BoqDocumentId { get; set; }
    public int Seq { get; set; }
    public string Title { get; set; } = null!;

    /// <summary>Sum of this section's priced line totals (TBD lines excluded).</summary>
    public decimal TotalAmount { get; set; }

    public virtual BoqDocument Document { get; set; } = null!;
    public virtual ICollection<BoqItem> Items { get; set; } = new List<BoqItem>();
}

/// <summary>A single BOQ line.</summary>
public class BoqItem
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long BoqSectionId { get; set; }
    public int Seq { get; set; }

    public string? ItemCode { get; set; }
    public string Description { get; set; } = null!;

    /// <summary>EA / m / m² / m³ / lot / hr / day / set / kg …</summary>
    public string Unit { get; set; } = "lot";

    public decimal Quantity { get; set; }

    /// <summary>Material | Labor | Equipment | Subcontract.</summary>
    public string ItemType { get; set; } = BoqItemType.Material;

    public decimal? UnitRate { get; set; }

    /// <summary>Quantity × UnitRate when both are known and the line is not TBD; else null.</summary>
    public decimal? TotalAmount { get; set; }

    /// <summary>extracted | assembly | manual.</summary>
    public string Source { get; set; } = BoqItemSource.Manual;

    /// <summary>0..1 model confidence for this line; null for manual/assembly lines.</summary>
    public decimal? Confidence { get; set; }

    /// <summary>True when the source scope under-specifies this line (e.g. quantity not stated).</summary>
    public bool IsTbd { get; set; }

    /// <summary>Optional link to a rate-library assembly this line can explode into.</summary>
    public string? AssemblyCode { get; set; }

    /// <summary>Where/why this line came from ("Cable sizes not stated — quantity TBD", …).</summary>
    public string? EvidenceNote { get; set; }

    public virtual BoqSection Section { get; set; } = null!;
}

/// <summary>
/// A tenant rate-library assembly: a named, coded bundle (e.g. "DB-PANEL-250A")
/// that explodes into components with per-unit quantities and default rates.
/// Starter assemblies are seeded lazily per business unit (IsStarter = true) and
/// are meant to be edited by the tenant.
/// </summary>
public class BoqAssembly
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }

    /// <summary>Stable code referenced by BoqItem.AssemblyCode, e.g. "CABLE-RUN-M".</summary>
    public string Code { get; set; } = null!;

    public string Name { get; set; } = null!;
    public string? Description { get; set; }

    /// <summary>Category hint (electrical | mechanical | …) for filtering in the UI.</summary>
    public string ServiceCategory { get; set; } = "other";

    /// <summary>The unit ONE of this assembly represents (EA, m, m³ …); component QtyPer is per this unit.</summary>
    public string Unit { get; set; } = "EA";

    /// <summary>True for the seeded starter library — a signal to tenants to review the rates.</summary>
    public bool IsStarter { get; set; }

    public DateTime CreatedOn { get; set; }
    public DateTime UpdatedOn { get; set; }

    public virtual ICollection<BoqAssemblyComponent> Components { get; set; } = new List<BoqAssemblyComponent>();
}

/// <summary>One component line of an assembly.</summary>
public class BoqAssemblyComponent
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long BoqAssemblyId { get; set; }
    public int Seq { get; set; }

    public string Description { get; set; } = null!;
    public string Unit { get; set; } = "EA";

    /// <summary>Quantity of this component per ONE unit of the parent assembly.</summary>
    public decimal QtyPer { get; set; }

    /// <summary>Material | Labor | Equipment | Subcontract.</summary>
    public string ItemType { get; set; } = BoqItemType.Material;

    /// <summary>Starter/default rate — tenants are expected to maintain these.</summary>
    public decimal? DefaultRate { get; set; }

    public virtual BoqAssembly Assembly { get; set; } = null!;
}
