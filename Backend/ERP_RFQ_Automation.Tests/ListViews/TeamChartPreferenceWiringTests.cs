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

/// <summary>
/// The reader's own dashboard (Frontend/src/pages/Dashboard/glance/chartPrefs.ts): band order and
/// visibility in `dashboard.layout`, chart options and band widths in `dashboard.charts`. The client
/// reads these keys by name.
/// </summary>
public sealed class DashboardPreferenceWiringTests
{
    private static readonly string[] Bands = ["verdict", "outstanding", "losses", "closing", "today", "sixmonths", "brands", "customers"];

    [Fact]
    public void Layout_lists_every_band_visible_in_reading_order()
    {
        var view = ListViewCatalog.Find("dashboard.layout");
        Assert.NotNull(view);
        Assert.Equal(Bands, view!.Columns.Select(c => c.Key));
        Assert.All(view.Columns, c => Assert.True(c.DefaultVisible));
    }

    [Fact]
    public void Each_chart_group_has_exactly_one_default_choice()
    {
        var view = ListViewCatalog.Find("dashboard.charts");
        Assert.NotNull(view);
        var groups = view!.Columns.GroupBy(c => c.Key[..c.Key.LastIndexOf('.')]).ToList();
        foreach (var band in Bands) Assert.Contains(groups, g => g.Key == $"size.{band}");
        Assert.Contains(groups, g => g.Key == "outstanding");
        Assert.Contains(groups, g => g.Key == "losses");
        Assert.Contains(groups, g => g.Key == "closing");
        Assert.Contains(groups, g => g.Key == "customers");
        Assert.All(groups, g => Assert.Single(g, c => c.DefaultVisible));
    }
}
