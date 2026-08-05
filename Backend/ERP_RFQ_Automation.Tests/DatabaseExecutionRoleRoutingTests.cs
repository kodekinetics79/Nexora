using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace ERP_RFQ_Automation.Tests;

public sealed class DatabaseExecutionRoleRoutingTests
{
    [Fact]
    public void TenantScopeAlwaysUsesTenantRole()
    {
        var accessor = Accessor("/api/platform/tenants");

        Assert.Equal(TenantRlsCommandInterceptor.TenantRole,
            TenantRlsCommandInterceptor.ResolveDatabaseRole(42, accessor));
    }

    [Theory]
    [InlineData("/api/platform/auth/login")]
    [InlineData("/api/platform/overview")]
    [InlineData("/api/platform/tenants/7")]
    public void PlatformRoutesUsePipelineRole(string path)
    {
        Assert.Equal(TenantRlsCommandInterceptor.PipelineRole,
            TenantRlsCommandInterceptor.ResolveDatabaseRole(null, Accessor(path)));
    }

    [Theory]
    [InlineData("/api/Auth/Login")]
    [InlineData("/API/AUTH/LOGIN")]
    [InlineData("/api/Auth/Login/")]
    public void TenantLoginUsesReadOnlyIdentityRole(string path)
    {
        Assert.Equal(TenantRlsCommandInterceptor.IdentityRole,
            TenantRlsCommandInterceptor.ResolveDatabaseRole(null, Accessor(path)));
    }

    [Theory]
    [InlineData(42L, "/api/Auth/Login")]
    [InlineData(42L, "/api/Auth/Login/")]
    public void LoginKeepsTheIdentityRoleEvenWhenTheRequestCarriesATenantToken(long businessUnitId, string path)
    {
        // THE LOGIN THROTTLE DEPENDS ON THIS. axiosInstance attaches the bearer token to every
        // request, so re-posting the login form while a live tenant JWT is still in local storage
        // is the DEFAULT path, not an edge case — and it arrives here with businessUnitId set.
        //
        // When the tenant check came first, that request executed as nexora_tenant_app, from
        // which 20260804181701_LoginAttemptThrottle REVOKEs ALL privileges on "LoginAttempts".
        // LoginAttemptThrottle fails open on infrastructure faults, so the resulting 42501
        // insufficient_privilege was logged and swallowed: no counter, no lockout, no forensic
        // row. A user holding any tenant token could guess their own tenant's Super Admin
        // password at the full request rate, indefinitely.
        //
        // Not an escalation: the same endpoint already resolves to nexora_identity_app when no
        // token is sent, so this grants nothing a caller could not get by dropping the token.
        Assert.Equal(TenantRlsCommandInterceptor.IdentityRole,
            TenantRlsCommandInterceptor.ResolveDatabaseRole(businessUnitId, Accessor(path)));
    }

    [Theory]
    [InlineData("/api/platform/auth/login")]
    [InlineData("/api/platform/auth/login/")]
    public void PlatformLoginKeepsThePipelineRoleEvenWhenTheRequestCarriesATenantToken(string path)
    {
        // Same composition defect on the platform plane: "LoginAttempts" is granted to
        // nexora_pipeline_app and nexora_identity_app only.
        Assert.Equal(TenantRlsCommandInterceptor.PipelineRole,
            TenantRlsCommandInterceptor.ResolveDatabaseRole(42, Accessor(path)));
    }

    [Fact]
    public void ATenantTokenOnANonLoginPlatformRouteStaysOnTheFailClosedTenantRole()
    {
        // Deliberately NOT hoisted with the login paths. nexora_pipeline_app is BYPASSRLS, and
        // an impersonation token (PlatformAuthService) is a TENANT token — it carries
        // businessUnitId and no platform scope. Routing it to the bypass role would remove RLS
        // as the backstop the instant a platform authorization check regressed.
        Assert.Equal(TenantRlsCommandInterceptor.TenantRole,
            TenantRlsCommandInterceptor.ResolveDatabaseRole(42, Accessor("/api/platform/overview")));
    }

    [Fact]
    public void LoginPrefixMatchingIsSegmentAwareAndNotMerelyStringPrefixed()
    {
        // StartsWithSegments closes the trailing-slash variant WITHOUT widening the match to a
        // sibling route that merely shares the first characters.
        Assert.Equal(TenantRlsCommandInterceptor.TenantRole,
            TenantRlsCommandInterceptor.ResolveDatabaseRole(null, Accessor("/api/Auth/LoginHistory")));
        Assert.Equal(TenantRlsCommandInterceptor.TenantRole,
            TenantRlsCommandInterceptor.ResolveDatabaseRole(42, Accessor("/api/Auth/LoginHistory")));
    }

    [Fact]
    public void BackgroundProcessingUsesPipelineRole()
    {
        Assert.Equal(TenantRlsCommandInterceptor.PipelineRole,
            TenantRlsCommandInterceptor.ResolveDatabaseRole(null, new HttpContextAccessor()));
    }

    [Theory]
    [InlineData("/api/BusinessUnit/Dropdown")]
    [InlineData("/api/Leads")]
    [InlineData("/health")]
    public void OtherTenantOrAnonymousRoutesFallBackToTheUnprivilegedTenantRole(string path)
    {
        // These routes previously resolved to null, which issued no SET LOCAL ROLE and left the
        // command on the connection's login role. That role owns the tables (migrations run under
        // the same username), and a table owner is exempt from its own RLS policies unless the
        // table is FORCEd — so an anonymous request read every tenant's rows while the EF query
        // filter was simultaneously a no-op for the null tenant.
        //
        // nexora_tenant_app is NOBYPASSRLS and no nexora.business_unit_id GUC is set for a null
        // tenant, so every policy compares against NULL and matches nothing: zero rows on read,
        // rejected on write. Not PipelineRole — that is BYPASSRLS and would still see everything.
        Assert.Equal(TenantRlsCommandInterceptor.TenantRole,
            TenantRlsCommandInterceptor.ResolveDatabaseRole(null, Accessor(path)));
        Assert.NotEqual(TenantRlsCommandInterceptor.PipelineRole,
            TenantRlsCommandInterceptor.ResolveDatabaseRole(null, Accessor(path)));
    }

    [Fact]
    public void MinimalConstructorDoesNotEnablePrivilegedRoleRouting()
    {
        // The parameterless-accessor overload is not reachable from Program.cs (DI resolves the
        // three-argument constructor); it backs background-worker test harnesses that sweep across
        // tenants before pushing a scope, so it keeps the no-op behaviour.
        Assert.Null(TenantRlsCommandInterceptor.ResolveDatabaseRole(null, null));
    }

    [Fact]
    public void BusinessUnitDropdownIsNotAnAnonymousTenantDirectory()
    {
        var action = typeof(BusinessUnitController).GetMethod(nameof(BusinessUnitController.GetDropdown));

        Assert.NotNull(action);
        Assert.Empty(action.GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true));
        Assert.NotEmpty(typeof(BusinessUnitController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true));
    }

    private static HttpContextAccessor Accessor(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        return new HttpContextAccessor { HttpContext = context };
    }
}
