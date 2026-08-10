using System;

namespace ERP_RFQ_Automation.Models;

/// <summary>
/// The tombstone: an append-only record that a quotation was removed, who removed it, why, and what
/// it was at the moment it went.
///
/// <para><b>Why it carries no foreign key to Quotes.</b> Same reason
/// <see cref="IamAuditEvent.TargetId"/> carries none — the record of "this quote was discarded"
/// must outlive the quote it names, and a discarded DRAFT leaves no row to point at. The quote
/// identity is preserved as data (<see cref="QuoteId"/> plus <see cref="QuoteNo"/>), not as a
/// reference. <see cref="BusinessUnitId"/> does carry one, because a tenant-less audit row is
/// unattributable and would sit outside the RLS policy's reach.</para>
///
/// <para><b>Why it snapshots the quote.</b> Number, customer, status, currency and total are copied
/// in at write time. An auditor asking "what was removed" gets an answer from this row alone,
/// without having to reconstruct it from a record that may no longer exist.</para>
/// </summary>
public class QuoteRemovalRecord
{
    public long Id { get; set; }

    /// <summary>Tenant that owns the event. Stamped from the caller's <c>businessUnitId</c> claim.</summary>
    public long BusinessUnitId { get; set; }

    /// <summary>The removed quote's id. Deliberately a loose reference — see the type remarks.</summary>
    public long QuoteId { get; set; }

    /// <summary>The quote number as issued, captured so the tombstone reads without a join.</summary>
    public string QuoteNo { get; set; } = null!;

    /// <summary>
    /// <see cref="QuoteRemovalModes.DraftDiscarded"/> when the row was actually deleted (a draft
    /// that had never been attested, extended, or turned into an order), or
    /// <see cref="QuoteRemovalModes.Withdrawn"/> when the quote was marked removed and kept.
    /// </summary>
    public string Mode { get; set; } = null!;

    /// <summary>The stated reason. Never null, never blank — enforced by CHECK constraint.</summary>
    public string Reason { get; set; } = null!;

    /// <summary>The authenticated actor who removed it.</summary>
    public string RemovedBy { get; set; } = null!;

    public DateTime RemovedOn { get; set; }

    public long? CustomerId { get; set; }

    /// <summary>Status id at removal, and its SetupMaster code where one was resolvable.</summary>
    public long? StatusId { get; set; }

    public string? StatusCode { get; set; }

    public long? CurrencyId { get; set; }

    public decimal? TotalAmount { get; set; }

    /// <summary>
    /// How many R5 price attestations and R7 validity extensions were attached at the time. These
    /// rows survive the removal (their FKs are RESTRICT, not CASCADE); the counts are recorded so a
    /// later reconciliation can tell "no evidence existed" apart from "evidence went missing".
    /// </summary>
    public int PriceAttestationCount { get; set; }

    public int ValidityExtensionCount { get; set; }

    public virtual BusinessUnit BusinessUnit { get; set; } = null!;
}

public static class QuoteRemovalModes
{
    /// <summary>A never-issued draft with no attached evidence and no order: row deleted.</summary>
    public const string DraftDiscarded = "DRAFT_DISCARDED";

    /// <summary>Anything past DRAFT, or a draft carrying evidence: row kept and marked removed.</summary>
    public const string Withdrawn = "WITHDRAWN";
}

/// <summary>
/// Raised when a quote removal is refused: no reason given, the quote is already withdrawn, or the
/// caller tried to destroy a record the platform is required to keep. Derives from
/// <see cref="InvalidOperationException"/> so it maps to a refused command, not a server fault.
/// </summary>
public sealed class QuoteRemovalRefusedException(string message) : InvalidOperationException(message);

/// <summary>What <c>QuoteRepository.RemoveAsync</c> actually did, so the caller can say so.</summary>
public sealed record QuoteRemovalOutcome(string QuoteNo, string Mode, DateTime RemovedOn)
{
    public bool WasDeleted => Mode == QuoteRemovalModes.DraftDiscarded;
}
