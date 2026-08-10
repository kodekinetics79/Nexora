using System;

namespace ERP_RFQ_Automation.Reporting;

/// <summary>Governed report keys. A subscription may name nothing else.</summary>
public static class ReportKeys
{
    /// <summary>FR-DSH-01 — open enquiries bucketed by time left to answer.</summary>
    public const string DeadlineBoard = "deadline-board";

    /// <summary>FR-DSH-02 — stage funnel, loss reasons and weighted forecast.</summary>
    public const string Pipeline = "pipeline";

    /// <summary>FR-DSH-02 — value-weighted gross margin on accepted quotes.</summary>
    public const string GrossMargin = "gross-margin";

    public static readonly string[] All = { DeadlineBoard, Pipeline, GrossMargin };

    public static bool IsKnown(string? key) => key is not null && Array.IndexOf(All, key) >= 0;

    public static string Title(string key) => key switch
    {
        DeadlineBoard => "Open enquiries by deadline",
        Pipeline => "Pipeline and loss reasons",
        GrossMargin => "Gross margin on accepted quotes",
        _ => key
    };
}

/// <summary>FR-DSH-06 — daily, weekly or monthly.</summary>
public static class ReportCadences
{
    public const string Daily = "DAILY";
    public const string Weekly = "WEEKLY";
    public const string Monthly = "MONTHLY";

    public static readonly string[] All = { Daily, Weekly, Monthly };

    public static bool IsKnown(string? cadence) => cadence is not null && Array.IndexOf(All, cadence) >= 0;
}

/// <summary>FR-DSH-06 — PDF or Excel.</summary>
public static class ReportFormats
{
    public const string Pdf = "PDF";
    public const string Xlsx = "XLSX";

    public static readonly string[] All = { Pdf, Xlsx };

    public static bool IsKnown(string? format) => format is not null && Array.IndexOf(All, format) >= 0;
}

public static class ReportRunOutcomes
{
    public const string Delivered = "DELIVERED";
    public const string Failed = "FAILED";
    /// <summary>Ran, produced no rows, and said so rather than mailing an empty page.</summary>
    public const string NothingToReport = "NOTHING_TO_REPORT";
}

/// <summary>
/// One tenant's standing instruction to email a report on a schedule.
///
/// <para><b>NextRunOn is NULLABLE and null means "not scheduled".</b> That is deliberate and is the
/// Gate 4 lesson (register R12) applied to a date: a NOT NULL column backfilled with
/// <c>default(DateTime)</c> would read as "due since year 1" and the first sweep after deployment
/// would mail every subscription in the estate at once. The safe direction for an unknown value is
/// "never", not "now", so the column carries no server default and the service computes the first
/// occurrence when the row is written.</para>
///
/// <para><b>Send-once is the database's job.</b> The worker claims an <c>SlaEvent</c> row keyed
/// <c>scheduled-report:{Id}:sent:{yyyy-MM-dd}</c> before any mail leaves, against the existing
/// unique <c>(BusinessUnitId, DedupKey)</c> index — the same mechanism, and the same guarantee, as
/// every other alert in the product. Two application instances cannot both send.</para>
/// </summary>
public sealed class ReportSubscription
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }

    /// <summary>One of <see cref="ReportKeys"/>.</summary>
    public string ReportKey { get; set; } = string.Empty;

    /// <summary>One of <see cref="ReportCadences"/>.</summary>
    public string Cadence { get; set; } = ReportCadences.Weekly;

    /// <summary>One of <see cref="ReportFormats"/>.</summary>
    public string Format { get; set; } = ReportFormats.Pdf;

    /// <summary>UTC hour of day, 0–23, at which the occurrence is due.</summary>
    public int HourUtc { get; set; }

    /// <summary>
    /// Weekly cadence only: 0 = Sunday … 6 = Saturday. Sunday is the first working day in KSA, and
    /// the default of 0 therefore lands a weekly report at the start of the working week rather than
    /// inside the Friday–Saturday weekend.
    /// </summary>
    public int DayOfWeek { get; set; }

    /// <summary>
    /// Monthly cadence only: 1–28. Capped at 28 on purpose — 29/30/31 do not exist in every month,
    /// and a schedule that silently skips February is a report nobody notices missing.
    /// </summary>
    public int DayOfMonth { get; set; } = 1;

    /// <summary>
    /// How many days of history the report covers. Independent of cadence, because "email me the
    /// last 30 days every week" is a real request and coupling the two would forbid it.
    /// </summary>
    public int WindowDays { get; set; } = 7;

    /// <summary>Semicolon-separated recipient addresses. A subscription with none cannot be saved.</summary>
    public string Recipients { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    /// <summary>Next due occurrence in UTC. NULL means not scheduled — see the class remarks.</summary>
    public DateTime? NextRunOn { get; set; }

    public DateTime? LastRunOn { get; set; }

    /// <summary>One of <see cref="ReportRunOutcomes"/>. NULL until it has run once.</summary>
    public string? LastRunOutcome { get; set; }

    /// <summary>What happened, in words, so a failure is visible on the screen and not only in a log.</summary>
    public string? LastRunDetail { get; set; }

    public DateTime CreatedOn { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime? ModifiedOn { get; set; }
    public string? ModifiedBy { get; set; }

    /// <summary>Recipient list as addresses, empty entries removed.</summary>
    public string[] RecipientAddresses() =>
        Recipients.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
