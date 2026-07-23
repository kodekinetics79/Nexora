using System.Security.Claims;
using ERP_RFQ_Automation.MultiTenancy;
using Microsoft.AspNetCore.Http;

namespace ERP_RFQ_Automation.Tests;

public sealed class TenantClaimGuardMiddlewareTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("not-a-number")]
    public async Task AuthenticatedTenantRequestWithoutValidClaimIsRejected(string? claimValue)
    {
        var nextCalled = false;
        var middleware = new TenantClaimGuardMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = AuthenticatedContext("/api/Order", claimValue);

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task AuthenticatedTenantRequestWithPositiveClaimContinues()
    {
        var nextCalled = false;
        var middleware = new TenantClaimGuardMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = AuthenticatedContext("/api/Shipment", "42");

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task PlatformControlPlaneUsesItsIndependentAuthorizationBoundary()
    {
        var nextCalled = false;
        var middleware = new TenantClaimGuardMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = AuthenticatedContext("/api/platform/tenants", null);

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task AnonymousLoginRequestContinues()
    {
        var nextCalled = false;
        var middleware = new TenantClaimGuardMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/Auth/login";

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    private static DefaultHttpContext AuthenticatedContext(string path, string? businessUnitId)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "test-user") };
        if (businessUnitId is not null)
            claims.Add(new Claim("businessUnitId", businessUnitId));

        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        return context;
    }
}
