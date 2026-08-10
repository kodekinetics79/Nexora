using System;

namespace ERP_RFQ_Automation.Sla;

/// <summary>
/// Working-time arithmetic for the SLA sweep, with a SAUDI weekend (Friday–Saturday).
///
/// <para>WHY THIS EXISTS. Every other clock in this worker is calendar-based:
/// <c>CreatedOn.AddHours(ApprovalEscalationHours)</c>, <c>SentOn.AddDays(StaleQuoteDays)</c>.
/// A repo-wide search for <c>DayOfWeek</c>, weekend or holiday handling returned nothing at
/// all before this file. In Saudi Arabia the office is closed Friday and Saturday, so a
/// calendar clock does two harmful things: it fires escalations into an empty office (nobody
/// reads them, and the "someone was told" audit trail is a fiction), and it MIS-COUNTS the
/// allowance — a 48-hour acknowledgement window that starts on Wednesday evening expires on
/// Friday evening, giving the supplier one working day instead of two, while the same window
/// started on Sunday gives them the full two. Two suppliers, same SLA, different deal.</para>
///
/// <para>SCOPE. Weekends only. A public-holiday calendar (Eid, National Day) is a per-tenant
/// data table with its own admin surface and is deliberately NOT in scope here — Eid moves
/// with the lunar calendar and cannot be hard-coded honestly. The residual defect is
/// therefore "escalations can still land on a public holiday", which is a handful of days a
/// year rather than 104.</para>
///
/// <para>The weekend is a CONSTANT, not a per-tenant setting. That is a deliberate limit of
/// this change: a multi-country tenant (a Sunday–Thursday Gulf office and a Monday–Friday
/// European one) needs a per-business-unit working-week column, which is schema work. Today
/// the platform is single-region, and Friday–Saturday is strictly better than the calendar
/// maths it replaces.</para>
///
/// <para>All inputs are UTC, matching the rest of the sweep. Saudi local time is UTC+3 with
/// no daylight saving, so a UTC day boundary sits at 03:00 local — close enough for a
/// weekend-granularity rule, and a full time-zone treatment would need the same per-tenant
/// column the working week does.</para>
/// </summary>
internal static class BusinessCalendar
{
    /// <summary>The Saudi weekend. Named so the reason is legible at every call site.</summary>
    public static bool IsWeekend(DayOfWeek day) => day is DayOfWeek.Friday or DayOfWeek.Saturday;

    public static bool IsWeekend(DateOnly date) => IsWeekend(date.DayOfWeek);

    public static bool IsWeekend(DateTime instant) => IsWeekend(instant.DayOfWeek);

    /// <summary>
    /// The date <paramref name="businessDays"/> WORKING days after <paramref name="start"/>.
    /// Used to turn a "remind me N working days before the ship date" policy into a single
    /// calendar horizon the database can filter on, so the reminder window is business-day
    /// wide without any per-row client-side evaluation.
    /// </summary>
    public static DateOnly AddBusinessDays(DateOnly start, int businessDays)
    {
        if (businessDays <= 0) return start;

        var cursor = start;
        for (var i = 0; i < businessDays; i++)
        {
            do { cursor = cursor.AddDays(1); } while (IsWeekend(cursor));
        }
        return cursor;
    }

    /// <summary>
    /// Working days remaining from <paramref name="from"/> (exclusive) to <paramref name="to"/>
    /// (inclusive) — what the reminder email tells the buyer they actually have. Zero when the
    /// target is today or already past.
    /// </summary>
    public static int BusinessDaysUntil(DateOnly from, DateOnly to)
    {
        if (to <= from) return 0;

        var count = 0;
        for (var day = from.AddDays(1); day <= to; day = day.AddDays(1))
        {
            if (!IsWeekend(day)) count++;
        }
        return count;
    }

    /// <summary>
    /// <paramref name="start"/> advanced by <paramref name="duration"/> of WORKING time —
    /// weekend days contribute nothing. A clock that starts inside the weekend starts
    /// accruing at midnight on the next working day, which is the honest reading of "the
    /// supplier has 48 hours to respond" for an order that reached them on a Friday.
    ///
    /// <para>This is a 24-hour working day, not a 9-to-5 one: office-hours arithmetic needs a
    /// per-tenant opening-hours setting, and modelling the weekend alone already removes the
    /// large, systematic error.</para>
    /// </summary>
    public static DateTime AddBusinessTime(DateTime start, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return start;

        // A clock started on the weekend does not tick until the office reopens.
        var cursor = start;
        while (IsWeekend(cursor)) cursor = cursor.Date.AddDays(1);

        var remaining = duration;
        while (true)
        {
            var nextMidnight = cursor.Date.AddDays(1);
            var availableToday = nextMidnight - cursor;   // always > 0
            if (remaining < availableToday) return cursor + remaining;

            remaining -= availableToday;
            cursor = nextMidnight;
            while (IsWeekend(cursor)) cursor = cursor.AddDays(1);
            if (remaining <= TimeSpan.Zero) return cursor;
        }
    }
}
