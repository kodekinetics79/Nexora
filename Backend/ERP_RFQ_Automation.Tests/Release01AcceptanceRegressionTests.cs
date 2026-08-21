using System.Reflection;
using System.Text.Json;
using ERP_RFQ_Automation.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Tests;

public sealed class Release01AcceptanceRegressionTests
{
    [Theory]
    [InlineData(typeof(LeadController), 2)]
    // 7 = + IPriceAttestationService (R5 price-provenance attestation).
    [InlineData(typeof(QuoteController), 7)]
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
        Assert.Contains("enabled: !invalidWindow", dashboard);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "Frontend")))
            directory = directory.Parent;
        return Assert.IsType<DirectoryInfo>(directory).FullName;
    }
}
