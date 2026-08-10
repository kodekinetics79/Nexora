using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ERP_RFQ_Automation.Interfaces;

namespace ERP_RFQ_Automation.Reporting;

public interface IReportBuilder
{
    Task<ReportDocument> BuildAsync(long businessUnitId, string reportKey, string tenantLabel,
        DateTime from, DateTime to, DateTime generatedAt, CancellationToken ct = default);
}

/// <summary>
/// Turns a governed report key into a <see cref="ReportDocument"/> by reading the same repository
/// and service the dashboard reads. Deliberately no second query path: a scheduled report that
/// computes its numbers differently from the screen is a second source of truth, and the first
/// person to notice will be the one who trusted the wrong one.
/// </summary>
public sealed class ReportBuilder : IReportBuilder
{
    private readonly IDashboardRepository _dashboard;
    private readonly IGrossMarginService _grossMargin;

    public ReportBuilder(IDashboardRepository dashboard, IGrossMarginService grossMargin)
    {
        _dashboard = dashboard;
        _grossMargin = grossMargin;
    }

    public async Task<ReportDocument> BuildAsync(long businessUnitId, string reportKey, string tenantLabel,
        DateTime from, DateTime to, DateTime generatedAt, CancellationToken ct = default)
    {
        if (!ReportKeys.IsKnown(reportKey))
            throw new ArgumentException($"'{reportKey}' is not a report this system produces.", nameof(reportKey));

        var document = new ReportDocument
        {
            Title = ReportKeys.Title(reportKey),
            TenantLabel = tenantLabel,
            PeriodFrom = from,
            PeriodTo = to,
            GeneratedAt = generatedAt
        };

        switch (reportKey)
        {
            case ReportKeys.DeadlineBoard: await BuildDeadlineBoardAsync(businessUnitId, document, ct); break;
            case ReportKeys.Pipeline: await BuildPipelineAsync(businessUnitId, document); break;
            case ReportKeys.GrossMargin: await BuildGrossMarginAsync(businessUnitId, document, from, to, generatedAt, ct); break;
        }

        return document;
    }

    private async Task BuildDeadlineBoardAsync(long businessUnitId, ReportDocument document, CancellationToken ct)
    {
        var board = await _dashboard.GetDeadlineBoardAsync(businessUnitId, 200, ct);

        // "Six buckets all reading zero" is not the same as content. The bucket table is emitted
        // whatever happens, so emptiness is decided from the subject — open enquiries — not from the
        // row count.
        document.IsEmpty = board.OpenLeads == 0 && board.LeadsWithoutClosingDate == 0;

        var summary = document.AddSection("Where the work sits")
            .WithColumns("Bucket", "Enquiries", "Line items");
        foreach (var bucket in board.Buckets)
            summary.AddRow(bucket.Label, ReportCell.Number(bucket.Leads), ReportCell.Number(bucket.LineItems));
        summary.AddRow("TOTAL", ReportCell.Number(board.OpenLeads), ReportCell.Number(board.OpenLineItems));

        // Both disclosures travel with the table. They are the two facts most likely to make the
        // bucket counts read better than the position actually is.
        if (board.LeadsWithoutClosingDate > 0)
            summary.Note($"{board.LeadsWithoutClosingDate} open enquiry(s) carry no usable closing date and are "
                         + "in no bucket. A missing deadline is a gap to close with the buyer, not a comfortable one.");
        if (board.LateIngestedExcludedLeads > 0)
            summary.Note($"{board.LateIngestedExcludedLeads} open enquiry(s) reached Nexora after their own closing "
                         + "date. They appear as overdue but are excluded from handling-performance judgements.");

        var detail = document.AddSection("Enquiries by urgency")
            .WithColumns("RFQ", "Buyer", "Closes", "Days left", "Lines", "Awaiting review");
        detail.EmptyMessage = "No open enquiry carries a deadline in this business unit.";
        foreach (var lead in board.Leads.OrderBy(l => l.DaysLeft ?? int.MaxValue).Take(100))
        {
            detail.AddRow(
                ReportCell.Text(lead.Rfqno ?? $"Lead #{lead.LeadId}"),
                ReportCell.Text(lead.BuyersName),
                ReportCell.Date(lead.BidClosingDate),
                lead.DaysLeft is null ? ReportCell.NotAvailable : ReportCell.Number(lead.DaysLeft.Value),
                ReportCell.Number(lead.LineItems),
                lead.AwaitingReview ? "Yes" : "No");
        }
        if (board.Leads.Count > 100)
            detail.Note($"Showing the 100 most urgent of {board.Leads.Count} enquiries.");
    }

    private async Task BuildPipelineAsync(long businessUnitId, ReportDocument document)
    {
        var pipeline = await _dashboard.GetPipelineAnalyticsAsync(businessUnitId);
        document.IsEmpty = pipeline.Funnel.All(stage => stage.Count == 0);

        var funnel = document.AddSection("Stage funnel")
            .WithColumns("Stage", "Count", "Value");
        foreach (var stage in pipeline.Funnel)
        {
            funnel.AddRow(stage.Label, ReportCell.Number(stage.Count),
                stage.Value is null
                    ? ReportCell.NotAvailable
                    : ReportCell.Money(stage.Value, stage.ValueCurrency));
        }
        funnel.Note("This funnel counts every record in the business unit with no date filter, "
                    + "unlike the period stated at the head of this report.");
        foreach (var reason in pipeline.Funnel.Where(s => s.ValueUnavailableReason is not null))
            funnel.Note($"{reason.Label}: {reason.ValueUnavailableReason}");

        var losses = document.AddSection("Why work was lost")
            .WithColumns("Reason", "Count", "Value");
        losses.EmptyMessage = "No quote in this business unit has been recorded as lost.";
        foreach (var reason in pipeline.LossReasons)
            losses.AddRow(reason.Reason, ReportCell.Number(reason.Count),
                ReportCell.Money(reason.Value, reason.ValueCurrency));

        var forecast = document.AddSection("Open pipeline")
            .WithColumns("Measure", "Value");
        forecast.AddRow("Quotes awaiting any response", ReportCell.Number(pipeline.AwaitingResponseQuotes));
        forecast.AddRow("Value awaiting response",
            ReportCell.Money(pipeline.AwaitingResponseValue, pipeline.ForecastCurrency));
        forecast.AddRow("Quotes responded to, outcome not recorded", ReportCell.Number(pipeline.RespondedQuotes));
        forecast.AddRow("Value responded, undecided",
            ReportCell.Money(pipeline.RespondedValue, pipeline.ForecastCurrency));
        forecast.AddRow("Weighted forecast",
            ReportCell.Money(pipeline.WeightedForecast, pipeline.ForecastCurrency));
        forecast.Note("The weighted forecast applies 30% to quotes still waiting and 50% to quotes the "
                      + "customer has answered without a recorded outcome. It is a weighting, not a commitment.");
        forecast.Note(pipeline.ForecastUnavailableReason);
    }

    private async Task BuildGrossMarginAsync(long businessUnitId, ReportDocument document,
        DateTime from, DateTime to, DateTime generatedAt, CancellationToken ct)
    {
        var margin = await _grossMargin.GetAsync(businessUnitId, from, to, generatedAt, ct);

        // An UNAVAILABLE margin is emphatically not "nothing to report" — it is the finding. Only a
        // window with no accepted quote lines at all is empty.
        document.IsEmpty = margin.AcceptedQuoteLines == 0
                           && margin.QuotesExcludedForMissingAcceptanceDate == 0;

        var headline = document.AddSection("Gross margin on accepted quotes")
            .WithColumns("Measure", "Value");

        if (margin.Status == GrossMarginStatuses.Available)
        {
            headline.AddRow("Value-weighted gross margin", ReportCell.Percent(margin.MarginPercent));
            headline.AddRow("Accepted value", ReportCell.Money(margin.RevenueTotal, margin.CurrencyCode));
            headline.AddRow("Landed cost of that value", ReportCell.Money(margin.CostTotal, margin.CurrencyCode));
        }
        else
        {
            // The whole point of the rewrite: no number where there is no evidence.
            headline.AddRow("Value-weighted gross margin", "Unavailable");
            headline.Note(margin.Reason);
        }

        headline.Note($"Accepted is defined as: {margin.AcceptedDefinition}");
        headline.Note("Cost is the landed unit cost recorded on the sourcing decision the customer price was "
                      + "built from, multiplied by the quantity on that decision. It is not the product card's "
                      + "cost, which is a purchase price and not a landed cost.");
        headline.Note(margin.CostBasisNote);
        if (margin.MarginPercentCurrentBasisOnly is not null)
            headline.Note("Margin on the current cost basis alone: "
                          + ReportCell.Percent(margin.MarginPercentCurrentBasisOnly));

        var evidence = document.AddSection("What the figure rests on")
            .WithColumns("Measure", "Count");
        evidence.AddRow("Sourcing decisions sampled", ReportCell.Number(margin.SampleLines));
        evidence.AddRow("Accepted quotes represented", ReportCell.Number(margin.SampleQuotes));
        evidence.AddRow("Accepted quote lines in the period", ReportCell.Number(margin.AcceptedQuoteLines));
        evidence.AddRow("Lines with no sourcing decision (excluded)",
            ReportCell.Number(margin.LinesWithoutSourcingEvidence));
        evidence.AddRow("Accepted quotes with no acceptance date (in no period)",
            ReportCell.Number(margin.QuotesExcludedForMissingAcceptanceDate));
        if (margin.CostBasisChangedOn is { } changed)
        {
            evidence.AddRow($"Sampled lines priced before {changed:dd MMM yyyy}",
                ReportCell.Number(margin.LinesOnPriorCostBasis));
            evidence.AddRow($"Sampled lines priced on or after {changed:dd MMM yyyy}",
                ReportCell.Number(margin.LinesOnCurrentCostBasis));
        }

        if (margin.LinesWithoutSourcingEvidence > 0 && margin.AcceptedQuoteLines > 0)
        {
            var coverage = Math.Round(100m * margin.SampleLines / margin.AcceptedQuoteLines, 1,
                MidpointRounding.AwayFromZero);
            evidence.Note($"This figure covers {coverage}% of the accepted quote lines in the period. "
                          + "The remainder priced without a recorded sourcing decision, so their cost cannot "
                          + "be evidenced and they are excluded rather than estimated.");
        }
    }
}
