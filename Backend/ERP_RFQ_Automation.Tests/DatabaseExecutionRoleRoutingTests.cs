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
    public void TenantLoginUsesReadOnlyIdentityRole(string path)
    {
        Assert.Equal(TenantRlsCommandInterceptor.IdentityRole,
            TenantRlsCommandInterceptor.ResolveDatabaseRole(null, Accessor(path)));
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
    public void OtherTenantOrAnonymousRoutesReceiveNoPrivilegedRole(string path)
    {
        Assert.Null(TenantRlsCommandInterceptor.ResolveDatabaseRole(null, Accessor(path)));
    }

    [Fact]
    public void MinimalConstructorDoesNotEnablePrivilegedRoleRouting()
    {
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
