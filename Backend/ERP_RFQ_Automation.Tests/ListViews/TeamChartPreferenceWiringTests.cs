using ERP_RFQ_Automation.ListViews;

namespace ERP_RFQ_Automation.Tests.ListViews;

/// <summary>
/// The team chart on Sales performance keeps each reader's two chosen measures in the list-view
/// store: first visible key = across axis, second = up axis. The client
/// (Frontend/src/pages/SalesManagement/TeamChart.tsx, MEASURES) reads these keys by name, so a
/// rename here silently resets every reader's chart.
/// </summary>
public sealed class TeamChartPreferenceWiringTests
{
    private static readonly string[] ShippedKeys =
    [
        "quoteSent", "wonQuotes", "lostQuotes", "conversionRate", "averageResponseHours",
        "opportunities", "activityCount", "overdueFollowUps", "openRfqs", "activeLeads"
    ];

    [Fact]
    public void Chart_view_is_registered_with_every_shipped_measure()
    {
        var view = ListViewCatalog.Find("sales.performance.chart");
        Assert.NotNull(view);
        Assert.Equal(ShippedKeys, view!.Columns.Select(c => c.Key));
        Assert.Null(view.CustomFieldEntityType);
        Assert.DoesNotContain(view.Columns, c => c.Locked);
    }

    [Fact]
    public void A_reader_with_no_saved_choice_gets_quotes_sent_across_and_won_up()
    {
        var visible = ListViewCatalog.Find("sales.performance.chart")!.Columns
            .Where(c => c.DefaultVisible).Select(c => c.Key);
        Assert.Equal(["quoteSent", "wonQuotes"], visible);
    }
}
