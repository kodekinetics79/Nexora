using System;

namespace ERP_RFQ_Automation.Sla;

/// <summary>
/// Shared pure helpers for SLA-derived display values, so the repository, the
/// service DTO mapping and the sweep worker all agree on one definition of
/// "stale".
/// </summary>
public static class SlaComputed
{
    /// <summary>SENT, never responded, and older than the BU's stale threshold.</summary>
    public static bool IsStale(string? statusCode, DateTime? sentOn, DateTime? respondedOn, int staleQuoteDays, DateTime? nowUtc = null)
    {
        if (!string.Equals(statusCode, "SENT", StringComparison.OrdinalIgnoreCase)) return false;
        if (respondedOn.HasValue || !sentOn.HasValue) return false;
        return sentOn.Value.AddDays(staleQuoteDays) < (nowUtc ?? DateTime.UtcNow);
    }

    /// <summary>Whole days since the quote was sent; null when never sent.</summary>
    public static int? DaysSinceSent(DateTime? sentOn, DateTime? nowUtc = null)
        => sentOn.HasValue ? Math.Max(0, (int)((nowUtc ?? DateTime.UtcNow) - sentOn.Value).TotalDays) : null;
}
