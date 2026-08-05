namespace ERP_RFQ_Automation.CustomerResolution;

/// <summary>
/// What the machine PROPOSED for a lead's client organisation, ranked.
///
/// Persisted rather than recomputed so (a) the leads list and the resolve dialog do not
/// each re-run resolution, and (b) "what did Nexora suggest, and why" stays auditable after
/// a human overrides it. Rows are replaced wholesale on every resolution pass — they are a
/// snapshot of the latest decision, never an event log.
///
/// Tenant-scoped three ways: BusinessUnitId column, an EF global query filter, and RLS with
/// a USAGE-only sequence grant for nexora_tenant_app.
/// </summary>
public sealed class LeadCustomerMatchCandidate
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long LeadId { get; set; }
    public long CustomerId { get; set; }

    /// <summary>1 = strongest. Unique per (tenant, lead).</summary>
    public int Rank { get; set; }

    /// <summary>0..1 strength of the signal behind this candidate.</summary>
    public decimal Confidence { get; set; }

    /// <summary>One of <see cref="CustomerMatchReasonCodes"/>.</summary>
    public string ReasonCode { get; set; } = string.Empty;

    /// <summary>Sentence a sales rep can act on ("shares sender domain se.com.sa").</summary>
    public string Explanation { get; set; } = string.Empty;

    public DateTime CreatedOn { get; set; }
}
