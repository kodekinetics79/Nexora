using System.Security.Claims;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.Inventory.Commercial;
using ERP_RFQ_Automation.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The lead inventory check must tell the operator WHY it failed.
///
/// <para><b>The defect this pins.</b> <c>ResolveLead</c> was a bare expression body, so every
/// failure left the pipeline as an unhandled 500 and the screen printed one fixed sentence —
/// "Inventory Check Unavailable. No product, stock, or supplier commitment was selected
/// automatically." That reads like an empty result rather than a fault, and named no cause. A
/// legacy lead with no immutable current revision, a lead belonging to another tenant, and a
/// bad request all looked identical, so diagnosing a single lead meant reading server logs.</para>
///
/// <para>These are the real gate messages, written for operators, so they are safe to render.</para>
/// </summary>
public sealed class InventoryIntelligenceErrorSurfacingTests
{
    private sealed class ThrowingLineResolution(Exception failure) : ICommercialLineResolutionApplicationService
    {
        public Task<IReadOnlyList<LeadLineCommercialResolution>> ResolveLeadAsync(
            long businessUnitId, long leadId, int resourceLimit, CancellationToken ct = default,
            bool forceRefresh = true) => throw failure;

        public Task LinkRfqAsync(long businessUnitId, long leadId, long rfqId, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private static InventoryIntelligenceController ControllerThatThrows(Exception failure)
    {
        var controller = new InventoryIntelligenceController(
            db: null!, lineResolution: new ThrowingLineResolution(failure),
            inventoryAvailability: null!, orderStock: null!);
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("businessUnitId", "1")], "test")),
            TraceIdentifier = "trace-under-test"
        };
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private static ProblemDetails ProblemFrom(ActionResult result) =>
        Assert.IsType<ProblemDetails>(Assert.IsAssignableFrom<ObjectResult>(result).Value);

    [Fact]
    public async Task A_lead_with_no_current_revision_returns_409_naming_the_reason()
    {
        // The exact failure behind "Inventory Check Unavailable" on a legacy lead.
        var controller = ControllerThatThrows(
            new InvalidOperationException("The lead has no immutable current revision."));

        var result = await controller.ResolveLead(446);

        var problem = ProblemFrom(result);
        Assert.Equal(StatusCodes.Status409Conflict, problem.Status);
        Assert.Equal("Inventory check not run", problem.Title);
        Assert.Contains("immutable current revision", problem.Detail);
    }

    [Fact]
    public async Task A_lead_outside_the_tenant_returns_404_not_a_500()
    {
        var controller = ControllerThatThrows(new KeyNotFoundException("Lead was not found in this tenant."));

        var result = await controller.ResolveLead(446);

        var problem = ProblemFrom(result);
        Assert.Equal(StatusCodes.Status404NotFound, problem.Status);
        Assert.Contains("not found", problem.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_bad_request_returns_400()
    {
        var controller = ControllerThatThrows(new ArgumentException("Resource limit must be 10, 20 or 50."));

        var result = await controller.ResolveLead(446);

        var problem = ProblemFrom(result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.Status);
        Assert.Contains("10, 20 or 50", problem.Detail);
    }

    [Fact]
    public async Task Every_mapped_failure_carries_a_traceId_for_support()
    {
        // Without it, an operator reporting "inventory check failed" gives support nothing to
        // correlate against the server logs.
        foreach (var failure in new Exception[]
                 {
                     new InvalidOperationException("The lead has no immutable current revision."),
                     new KeyNotFoundException("Lead was not found in this tenant."),
                     new ArgumentException("bad input")
                 })
        {
            var problem = ProblemFrom(await ControllerThatThrows(failure).ResolveLead(446));
            Assert.Equal("trace-under-test", Assert.Contains("traceId", problem.Extensions));
        }
    }

    [Fact]
    public async Task An_unexpected_failure_is_NOT_swallowed_into_a_success()
    {
        // Anything not explicitly mapped must keep propagating to the pipeline's 500 handler.
        // Catching Exception here would turn a real fault into a silent empty result — the very
        // shape of the bug this file exists to prevent.
        var controller = ControllerThatThrows(new TimeoutException("provider timed out"));

        await Assert.ThrowsAsync<TimeoutException>(() => controller.ResolveLead(446));
    }
}
