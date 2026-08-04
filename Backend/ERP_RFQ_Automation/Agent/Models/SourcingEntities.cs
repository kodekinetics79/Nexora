using System;

namespace ERP_RFQ_Automation.Agent.Models;

/// <summary>Delivery/response state of an RFQ solicitation sent to one supplier.</summary>
public enum SolicitationStatus
{
    PendingDispatch,
    Dispatching,
    Sent,
    DeliveryFailed,
    Responded,
    Declined,
    Expired
}

/// <summary>
/// Tracks that an RFQ was actually dispatched to a specific supplier and the
/// lifecycle of that solicitation (sent → responded/declined/expired). This is the
/// record that turns the previously-facade dispatch into a real, auditable step.
/// Tenant-scoped via the global query filter configured in
/// <c>ErpRfqAutomationContext.Agent.cs</c>.
/// </summary>
public sealed class SupplierSolicitation
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }

    public long RfqId { get; set; }
    public long SupplierId { get; set; }
    public long? SourcingCaseId { get; set; }
    public long? CommercialDemandLineId { get; set; }
    public string? NexoraSerial { get; set; }
    public string? SupplierRfqNumber { get; set; }

    public string IdempotencyKey { get; set; } = null!;
    public string RequestHash { get; set; } = null!;
    public string RequestedRfqItemIdsJson { get; set; } = "[]";
    public long Version { get; set; } = 1;

    public SolicitationStatus Status { get; set; } = SolicitationStatus.PendingDispatch;

    public DateTime SentOn { get; set; }
    public DateTime? RespondedOn { get; set; }

    /// <summary>
    /// The response deadline the buyer committed to when preparing this Supplier RFQ.
    /// Persisted as a first-class UTC column so it survives as queryable evidence: it is
    /// echoed on the workbench, carried in the dispatch payload, and is the only durable
    /// record of the commitment. Null when the buyer set no deadline.
    /// </summary>
    public DateTime? DueOn { get; set; }

    /// <summary>Dispatch channel, e.g. "Email".</summary>
    public string Channel { get; set; } = "Email";

    public string? Notes { get; set; }

    public DateTime CreatedOn { get; set; }
    public DateTime UpdatedOn { get; set; }
}

/// <summary>
/// The sourcing DECISION: a recorded award of an RFQ (optionally a specific line
/// item) to a supplier at an agreed unit price. This closes the loop that was
/// entirely missing — evaluation → award. Tenant-scoped.
/// </summary>
public sealed class SourcingAward
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }

    public long RfqId { get; set; }

    /// <summary>Null when the award covers the whole RFQ rather than one line.</summary>
    public long? RfqItemId { get; set; }

    public long SupplierId { get; set; }

    public long? SupplierQuotedItemId { get; set; }
    public long? CurrencyId { get; set; }
    public string Status { get; set; } = "APPROVED";
    public string? IdempotencyKey { get; set; }
    public string? RequestHash { get; set; }
    public long Version { get; set; } = 1;

    public decimal UnitPrice { get; set; }
    public decimal? Quantity { get; set; }

    /// <summary>UnitPrice × Quantity (Quantity defaults to 1 when absent).</summary>
    public decimal TotalValue { get; set; }

    public decimal? LandedUnitCost { get; set; }

    public string? Rationale { get; set; }

    public long? AwardedByUserId { get; set; }

    /// <summary>True when recorded through the copilot rather than a manual entry.</summary>
    public bool AwardedByAgent { get; set; }

    public DateTime CreatedOn { get; set; }
}
