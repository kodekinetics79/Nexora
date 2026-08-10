using System;
using System.Collections.Generic;
using System.Globalization;

namespace ERP_RFQ_Automation.Reporting;

/// <summary>
/// The rendering-neutral shape of a report: headings, notes and tables of already-formatted cells.
///
/// <para><b>Why cells are strings.</b> Formatting happens where the record is, because the record
/// is the only thing that knows its own currency. A renderer handed a bare decimal would have to
/// pick a symbol, and picking a symbol is the defect <c>Frontend/src/utils/currency.ts</c> was
/// written to end. Where a currency is unknown the builder emits a bare number, exactly as the
/// shared formatter's fallback does.</para>
///
/// <para><b>Notes are part of the report, not decoration.</b> A cost-basis disclosure or a
/// "coverage is 40% of lines" caveat travels in <see cref="ReportSection.Notes"/> and is rendered
/// beside its table, so it cannot be separated from the number it qualifies.</para>
/// </summary>
public sealed class ReportDocument
{
    public string Title { get; init; } = string.Empty;
    public string TenantLabel { get; init; } = string.Empty;
    public DateTime PeriodFrom { get; init; }
    public DateTime PeriodTo { get; init; }
    public DateTime GeneratedAt { get; init; }
    public List<ReportSection> Sections { get; } = new();

    /// <summary>
    /// True when the report's subject has no records at all in the window — not merely when a
    /// table is empty. The builder sets it, because only the builder knows the difference between
    /// "six buckets all reading zero" and "nothing happened". The scheduled worker uses it to record
    /// the run and stay quiet: an empty report arriving every morning is how a reporting channel
    /// gets filtered, and the real one is lost with it.
    /// </summary>
    public bool IsEmpty { get; set; }

    public string PeriodLabel =>
        $"{PeriodFrom:dd MMM yyyy} to {PeriodTo:dd MMM yyyy} (inclusive of the start, exclusive of the end)";

    public ReportSection AddSection(string heading)
    {
        var section = new ReportSection { Heading = heading };
        Sections.Add(section);
        return section;
    }
}

public sealed class ReportSection
{
    public string Heading { get; init; } = string.Empty;

    /// <summary>Plain-language qualifications that must travel with the table.</summary>
    public List<string> Notes { get; } = new();

    public List<string> Columns { get; } = new();
    public List<string[]> Rows { get; } = new();

    /// <summary>Rendered in place of the table when there is nothing to show.</summary>
    public string? EmptyMessage { get; set; }

    public ReportSection WithColumns(params string[] columns)
    {
        Columns.AddRange(columns);
        return this;
    }

    public ReportSection AddRow(params string[] cells)
    {
        Rows.Add(cells);
        return this;
    }

    public ReportSection Note(string? note)
    {
        if (!string.IsNullOrWhiteSpace(note)) Notes.Add(note);
        return this;
    }
}

/// <summary>Cell formatting. The currency rules here mirror the frontend's shared formatter.</summary>
public static class ReportCell
{
    /// <summary>An explicit gap. Never an empty cell, which reads as "loading" or "zero".</summary>
    public const string NotAvailable = "—";

    public static string Number(decimal? value, int decimals = 0) => value is null
        ? NotAvailable
        : value.Value.ToString("N" + decimals, CultureInfo.InvariantCulture);

    public static string Number(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>
    /// Money in the currency the record carries. A null or blank currency code yields a bare
    /// grouped number — the honest rendering, and the same fallback the UI formatter preserves.
    /// </summary>
    public static string Money(decimal? value, string? currencyCode)
    {
        if (value is null) return NotAvailable;
        var amount = value.Value.ToString("N2", CultureInfo.InvariantCulture);
        var code = currencyCode?.Trim();
        return string.IsNullOrEmpty(code) ? amount : $"{code} {amount}";
    }

    public static string Percent(decimal? value) =>
        value is null ? NotAvailable : value.Value.ToString("0.#", CultureInfo.InvariantCulture) + "%";

    public static string Date(DateTime? value) =>
        value is null ? NotAvailable : value.Value.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);

    public static string Text(string? value) =>
        string.IsNullOrWhiteSpace(value) ? NotAvailable : value.Trim();
}
