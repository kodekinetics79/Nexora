using System.Reflection;
using System.Security.Claims;
using System.Net;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.DTOs;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Hardening;
using ERP_RFQ_Automation.Security;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;

namespace ERP_RFQ_Automation.Tests;

public sealed class AdministrativeSecurityTests
{
    [Theory]
    [InlineData(typeof(UserController), nameof(UserController.Create), "Users", PermissionAction.Create)]
    [InlineData(typeof(UserController), nameof(UserController.Update), "Users", PermissionAction.Edit)]
    [InlineData(typeof(UserController), nameof(UserController.Delete), "Users", PermissionAction.Delete)]
    [InlineData(typeof(BusinessUnitController), nameof(BusinessUnitController.Create), "Business Units", PermissionAction.Create)]
    [InlineData(typeof(BusinessUnitController), nameof(BusinessUnitController.Update), "Business Units", PermissionAction.Edit)]
    [InlineData(typeof(BusinessUnitController), nameof(BusinessUnitController.Delete), "Business Units", PermissionAction.Delete)]
    [InlineData(typeof(SmtpController), nameof(SmtpController.SendEmail), "Email & SMTP", PermissionAction.Create)]
    public void AdministrativeMutations_RequireDedicatedPermission(
        Type controller, string methodName, string module, PermissionAction action)
    {
        var method = controller.GetMethods().Single(x => x.Name == methodName);
        var permission = method.GetCustomAttributes<RequireModulePermissionAttribute>()
            .Single(x => x.ModuleName == module && x.Action == action);

        Assert.Equal(module, permission.ModuleName);
        Assert.Equal(action, permission.Action);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task ReadOnlyImpersonation_DeniesEveryUnsafeMethod(string method)
    {
        var nextCalled = false;
        var middleware = new ReadOnlyImpersonationMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, new MemoryCache(new MemoryCacheOptions()));
        var context = Context(method, readOnly: true);

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task ReadOnlyImpersonation_AllowsSafeRead()
    {
        var nextCalled = false;
        var middleware = new ReadOnlyImpersonationMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, new MemoryCache(new MemoryCacheOptions()));
        var context = Context("GET", readOnly: true);

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task ImpersonationWithoutReadOnlyClaim_StillDeniesMutation()
    {
        var nextCalled = false;
        var middleware = new ReadOnlyImpersonationMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, new MemoryCache(new MemoryCacheOptions()));
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Response.Body = new MemoryStream();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(PlatformAuthConstants.ImpersonatedClaim, "true"),
            new Claim("businessUnitId", "42")
        }, "test"));

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    /// <summary>
    /// The permission each user-mutation endpoint declares: Create requires "Users":Create,
    /// Update requires "Users":Edit. Plus the counterpart on the RBAC administration surface, so
    /// the two halves of the separation cannot drift apart unnoticed.
    ///
    /// This test used to also assert <c>[RequireModulePermission("Roles &amp; Permissions", Edit)]</c>
    /// on Create/Update. That assertion is gone on purpose, and its absence is NOT an oversight —
    /// but note that this test also does NOT assert the attribute is absent. Whether that second
    /// attribute belongs there is an open POLICY question, and this test deliberately does not
    /// take a side, because it is not where the security answer lives:
    ///
    ///  - The argument for removing it (B7): it makes every user administrator an RBAC
    ///    administrator by construction, so the "can create users, cannot grant permissions" role
    ///    a customer actually wants becomes unrepresentable. See
    ///    <c>UserAdministrationEscalationTests.PilotUserAdministratorRole_*</c>, which shows such a
    ///    role passing the Users gates and failing the "Roles &amp; Permissions" gate — i.e. with
    ///    the attribute present it cannot call these endpoints at all.
    ///
    ///  - The argument for keeping it: least privilege / defence in depth on a privilege-granting
    ///    action.
    ///
    ///  - What is NOT in dispute: the attribute is not the escalation control, in either
    ///    direction. Assigning — or changing to — a role the caller does not outrank is refused
    ///    imperatively by <c>CanManageRoleAsync</c> (Controllers/UserController.cs:246 for Create,
    ///    :326 and :328 for Update, :504 for Delete) via <see cref="IRoleGate"/>, and self-role
    ///    mutation is refused by the <c>callerId == id</c> check at :328. Those refusals are proven
    ///    behaviourally — real controller, real gate, real database, with a permissive-gate
    ///    mutation control confirming the refusals actually come from the gate — in
    ///    <c>UserAdministrationEscalationTests</c>. THAT is the escalation coverage. A reflection
    ///    test can only ever prove which attribute was typed, which is why the old assertion was
    ///    misleading: it read like escalation coverage and was not.
    ///
    /// So: if you are here because you added or removed that attribute and expected this test to
    /// have an opinion — it does not. Change the policy on its merits; the escalation invariant is
    /// enforced and tested elsewhere and does not move when you do.
    /// </summary>
    [Fact]
    public void UserMutations_DeclareUserAdministrationPermission()
    {
        var userAdministration = new[]
        {
            (Method: nameof(UserController.Create), Action: PermissionAction.Create),
            (Method: nameof(UserController.Update), Action: PermissionAction.Edit)
        };

        foreach (var (methodName, action) in userAdministration)
        {
            var permissions = typeof(UserController).GetMethods().Single(x => x.Name == methodName)
                .GetCustomAttributes<RequireModulePermissionAttribute>().ToArray();

            Assert.Contains(permissions, x => x.ModuleName == "Users" && x.Action == action);
        }

        foreach (var methodName in new[]
                 {
                     nameof(RolePermissionController.Create),
                     nameof(RolePermissionController.Update),
                     nameof(RolePermissionController.Delete),
                     nameof(RolePermissionController.BulkApply)
                 })
        {
            var permissions = typeof(RolePermissionController).GetMethods().Single(x => x.Name == methodName)
                .GetCustomAttributes<RequireModulePermissionAttribute>().ToArray();

            Assert.Contains(permissions, x => x.ModuleName == "Roles & Permissions");
        }
    }

    [Fact]
    public void SmtpRequest_DoesNotAcceptConnectionSettingsOrCredentials()
    {
        var properties = typeof(SendSupplierEmailRequestDTO).GetProperties()
            .Select(x => x.Name).ToArray();

        Assert.DoesNotContain(properties, x => x.Contains("Smtp", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(properties, x => x.Contains("Password", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(properties, x => x.Contains("Host", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("localhost", 587, false)]
    [InlineData("127.0.0.1", 587, false)]
    [InlineData("10.0.0.5", 587, false)]
    [InlineData("169.254.169.254", 587, false)]
    [InlineData("192.168.1.10", 587, false)]
    [InlineData("smtp.example.com", 587, true)]
    [InlineData("smtp.example.com", 0, false)]
    public void SmtpEndpointValidation_FailsClosed(string host, int port, bool expected)
    {
        var method = typeof(SmtpController).GetMethod(
            "IsValidConfiguredEndpoint", BindingFlags.Static | BindingFlags.NonPublic)!;
        var actual = (bool)method.Invoke(null, new object?[] { host, port })!;

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("192.0.2.1", false)]
    [InlineData("198.18.0.1", false)]
    [InlineData("198.51.100.1", false)]
    [InlineData("203.0.113.1", false)]
    [InlineData("192.88.99.1", false)]
    [InlineData("100::1", false)]
    [InlineData("64:ff9b::7f00:1", false)]
    [InlineData("64:ff9b:1::1", false)]
    [InlineData("100:0:0:1::1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("2002::1", false)]
    [InlineData("3fff::1", false)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("8.8.8.8", true)]
    public void SmtpAddressValidation_RejectsNonPublicNetworks(string address, bool expected)
    {
        var method = typeof(SmtpController).GetMethod(
            "IsPublicAddress", BindingFlags.Static | BindingFlags.NonPublic)!;
        var actual = (bool)method.Invoke(null, new object[] { IPAddress.Parse(address) })!;

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void SmtpDnsValidation_RejectsMixedPublicAndPrivateAnswers()
    {
        var method = typeof(MailKitOutboundSmtpTransport).GetMethod(
            "ValidateResolvedAddresses", BindingFlags.Static | BindingFlags.NonPublic)!;

        var error = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, new object[]
        {
            new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Parse("127.0.0.1") }
        }));

        Assert.IsType<InvalidOperationException>(error.InnerException);
    }

    [Fact]
    public void SmtpSend_UsesDedicatedTenantRateLimit()
    {
        var attribute = typeof(SmtpController).GetMethod(nameof(SmtpController.SendEmail))!
            .GetCustomAttribute<EnableRateLimitingAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal(RateLimitingExtensions.SmtpPolicy, attribute.PolicyName);
    }

    [Fact]
    public void SmtpSend_EnforcesRequestAndMultipartLimits()
    {
        var method = typeof(SmtpController).GetMethod(nameof(SmtpController.SendEmail))!;
        var requestLimit = method.CustomAttributes.Single(x => x.AttributeType == typeof(RequestSizeLimitAttribute));
        var formLimit = method.GetCustomAttribute<RequestFormLimitsAttribute>();

        Assert.NotNull(formLimit);
        var requestBytes = (long)requestLimit.ConstructorArguments.Single().Value!;
        Assert.True(requestBytes > 0);
        Assert.Equal(requestBytes, formLimit.MultipartBodyLengthLimit);
    }

    [Fact]
    public async Task SmtpConcurrencyGate_FailsClosedAtTenantLimitAndReleasesLease()
    {
        var gate = new TenantSmtpConcurrencyGate();
        await using var first = await gate.TryAcquireAsync(42, CancellationToken.None);
        await using var second = await gate.TryAcquireAsync(42, CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Null(await gate.TryAcquireAsync(42, CancellationToken.None));

        await first!.DisposeAsync();
        await using var replacement = await gate.TryAcquireAsync(42, CancellationToken.None);
        Assert.NotNull(replacement);
    }

    [Fact]
    public async Task RoleHierarchy_RejectsPrivilegeEscalationAndProtectedRoleManagement()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(42);
        context.BusinessUnits.Add(new BusinessUnit
        {
            Id = 42, BusinessUnitCode = "SEC", BusinessUnitName = "Security", IsActive = true,
            CreatedBy = "test", CreatedOn = DateTime.UtcNow
        });
        context.Modules.Add(new ERP_RFQ_Automation.Models.Module
        {
            Id = 101, ModuleName = "Users", IsActive = true, CreatedBy = "test", CreatedOn = DateTime.UtcNow
        });
        context.SetupMasters.AddRange(
            Role(1, "limited"), Role(2, "elevated"), Role(3, "super-admin"), Role(4, "manager"));
        context.RolePermissions.AddRange(
            Permission(1, canEdit: false), Permission(2, canEdit: true), Permission(3, canEdit: true),
            Permission(4, canEdit: false));
        await context.SaveChangesAsync();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var gate = new RoleGate(context, cache);

        Assert.False(await gate.CanManageRoleAsync(1, 2, 42));
        Assert.False(await gate.CanManageRoleAsync(1, 3, 42));
        Assert.False(await gate.CanManageRoleAsync(1, 4, 42));
        Assert.True(await gate.CanManageRoleAsync(2, 1, 42));
        Assert.True(await gate.CanManageRoleAsync(3, 2, 42));
    }

    [Fact]
    public async Task SmtpController_EnforcesTenantRecipientHeaderAndAttachmentBoundaries()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(42);
        context.BusinessUnits.AddRange(
            BusinessUnit(42, "ONE"), BusinessUnit(84, "TWO"));
        context.Suppliers.AddRange(
            Supplier(501, 42, "allowed@example.com"),
            Supplier(502, 84, "other@example.com"));
        context.EmailConfigurations.Add(new EmailConfiguration
        {
            Id = 601, BusinessUnitId = 42, ConfigurationName = "Primary",
            EmailAddress = "sender@example.com", Protocol = "SMTP", Host = "smtp.example.com",
            Port = 587, Username = "sender", Password = "secret", UseSsl = false,
            PollingInterval = 60, IsActive = true, CreatedOn = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
        var transport = new RecordingSmtpTransport();
        var controller = new SmtpController(
            NullLogger<SmtpController>.Instance, context, new TenantSmtpConcurrencyGate(), transport)
        {
            ControllerContext = new ControllerContext { HttpContext = SmtpContext(42) }
        };

        var valid = await controller.SendEmail(SmtpRequest(501, "allowed@example.com"));
        Assert.IsType<OkObjectResult>(valid);
        Assert.Equal(1, transport.SendCount);
        Assert.Equal(42, transport.Configuration!.BusinessUnitId);
        Assert.True(transport.CancellationCanBeCanceled);

        Assert.IsType<BadRequestObjectResult>(
            await controller.SendEmail(SmtpRequest(501, "unrelated@example.com")));
        Assert.IsType<BadRequestObjectResult>(
            await controller.SendEmail(SmtpRequest(502, "other@example.com")));
        Assert.IsType<BadRequestObjectResult>(
            await controller.SendEmail(SmtpRequest(501, "allowed@example.com\r\nBcc: attacker@example.com")));

        var withAttachment = SmtpRequest(501, "allowed@example.com");
        withAttachment.Attachments = new List<IFormFile>
        {
            new FormFile(new MemoryStream("payload"u8.ToArray()), 0, 7, "attachments", "offer.pdf")
        };
        var attachmentResult = Assert.IsType<ObjectResult>(await controller.SendEmail(withAttachment));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, attachmentResult.StatusCode);
        Assert.Equal(1, transport.SendCount);
    }

    private static DefaultHttpContext Context(string method, bool readOnly)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Response.Body = new MemoryStream();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(PlatformAuthConstants.ReadOnlyClaim, readOnly ? "true" : "false"),
            new Claim("businessUnitId", "42")
        }, "test"));
        return context;
    }

    private static SetupMaster Role(long id, string code) => new()
    {
        SetupId = id, SetupType = "role", SetupCode = code, SetupValue = code,
        BusinessUnitId = 42, IsActive = true, CreatedBy = "test", CreatedOn = DateTime.UtcNow
    };

    private static RolePermission Permission(long roleId, bool canEdit) => new()
    {
        RoleId = roleId, ModuleId = 101, BusinessUnitId = 42,
        CanCreate = false, CanEdit = canEdit, CanDelete = false,
        CreatedBy = "test", CreatedOn = DateTime.UtcNow
    };

    private static BusinessUnit BusinessUnit(long id, string code) => new()
    {
        Id = id, BusinessUnitCode = code, BusinessUnitName = code,
        IsActive = true, CreatedBy = "test", CreatedOn = DateTime.UtcNow
    };

    private static Supplier Supplier(long id, long businessUnitId, string email) => new()
    {
        Id = id, Buid = businessUnitId, Name = "Supplier " + id, ContactEmail = email,
        ImageUrl = string.Empty, IsActive = true, CreatedBy = "test", CreatedOn = DateTime.UtcNow
    };

    private static SendSupplierEmailRequestDTO SmtpRequest(long supplierId, string recipient) => new()
    {
        SupplierId = supplierId, ToEmail = recipient, Subject = "RFQ", Body = "<p>Request</p>"
    };

    private static DefaultHttpContext SmtpContext(long businessUnitId)
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("businessUnitId", businessUnitId.ToString())
        }, "test"));
        return context;
    }

    private sealed class RecordingSmtpTransport : IOutboundSmtpTransport
    {
        public int SendCount { get; private set; }
        public EmailConfiguration? Configuration { get; private set; }
        public bool CancellationCanBeCanceled { get; private set; }

        public Task SendAsync(
            EmailConfiguration configuration, MimeMessage message, CancellationToken cancellationToken)
        {
            SendCount++;
            Configuration = configuration;
            CancellationCanBeCanceled = cancellationToken.CanBeCanceled;
            return Task.CompletedTask;
        }
    }

}
