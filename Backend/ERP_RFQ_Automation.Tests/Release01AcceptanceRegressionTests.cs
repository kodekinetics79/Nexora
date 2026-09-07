using System.Reflection;
using System.Text.Json;
using ERP_RFQ_Automation.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Tests;

public sealed class Release01AcceptanceRegressionTests
{
    [Theory]
    // Lead adds the authenticated commercial row-scope dependency.
    [InlineData(typeof(LeadController), 3)]
    // Quote adds the row-scope dependency after IPriceAttestationService and the logger.
    [InlineData(typeof(QuoteController), 8)]
    public void UnexpectedControllerErrors_DoNotExposeInternalExceptionText(Type controllerType, int dependencyCount)
    {
        var arguments = Enumerable.Repeat<object?>(null, dependencyCount).ToArray();
        var controller = Assert.IsAssignableFrom<ControllerBase>(Activator.CreateInstance(controllerType, arguments));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { TraceIdentifier = "release-01-correlation" }
        };
        var method = controllerType.GetMethod("Unexpected", BindingFlags.Instance | BindingFlags.NonPublic);

        var result = Assert.IsType<ObjectResult>(method!.Invoke(controller,
            [new InvalidOperationException("database-password=must-not-leak"), "acceptance-test"]));
        var body = JsonSerializer.Serialize(result.Value);

        Assert.Equal(StatusCodes.Status500InternalServerError, result.StatusCode);
        Assert.DoesNotContain("database-password", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("release-01-correlation", body);
    }

    [Fact]
    public void FrontendAcceptanceContracts_UseLifecycleVersionAndGuardInvalidDashboardWindows()
    {
        var root = FindRepositoryRoot();
        var quoteService = File.ReadAllText(Path.Combine(root, "Frontend/src/api/services/quoteService.ts"));
        var quotePage = File.ReadAllText(Path.Combine(root, "Frontend/src/pages/Sales/Quotes/QuoteViewPage.tsx"));
        var dashboard = File.ReadAllText(Path.Combine(root, "Frontend/src/pages/Dashboard/DashboardPage.tsx"));

        // The optimistic-concurrency contract lives on the service, and still does.
        Assert.Contains("lifecycleVersion: number", quoteService);
        Assert.Contains("expectedVersion", quoteService);
        Assert.DoesNotContain("quote?.version ?? 1", quotePage);

        // QuoteViewPage previously asserted `quote?.lifecycleVersion`, because it carried a
        // "Ready to Send" button calling transitionStatus(id, 'Sent', quote?.lifecycleVersion).
        // That button was a pure lifecycle write: it raised "Status updated successfully" and
        // turned the chip green while emailing nobody, and left SentOn null, so the status was a
        // claim the delivery record did not support.
        //
        // The server owns this transition — FinalizeQuoteDeliveryAsync stamps SentOn, moves the
        // lifecycle to SENT and creates the follow-up task when the mail is actually delivered —
        // so the page now performs no client-side transition at all. That is a STRONGER guarantee
        // than passing the right version to a write that should not happen from here, and this
        // assertion pins it.
        Assert.DoesNotContain("transitionStatus", quotePage);

        // This used to assert `enabled: !invalidWindow` — the dashboard held whatever dates were
        // typed and switched its queries off while they were unusable. The glance screen cannot
        // hold an unusable window at all: `applied` is the only state the bands read, and the one
        // place that writes it refuses anything `isValidWindow` rejects, so a half-typed or
        // inverted range never becomes a window and there is nothing left to disable. That is the
        // same shape of stronger guarantee as the transitionStatus assertion above — the guard
        // moved from the query to the state that feeds it — so the assertion moves with it rather
        // than being dropped. The behaviour itself is pinned in DashboardPage.test.tsx, "keeps the
        // last usable window when a custom range is inverted, and says so".
        Assert.Contains("if (isValidWindow(next.from, next.to)) setApplied(next);", dashboard);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "Frontend")))
            directory = directory.Parent;
        return Assert.IsType<DirectoryInfo>(directory).FullName;
    }
}
