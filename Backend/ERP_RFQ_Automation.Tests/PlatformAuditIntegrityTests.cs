using System.Security.Claims;
using System.Text.Json;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Controllers;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Services;
using ERP_RFQ_Automation.Security;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Audit integrity for Platform Admin 360: the persisted Result column and its
/// round-trip through /api/platform/audit, the real failure filter, search applied
/// BEFORE the 500-row cap, platform login auditing (success + failure), and the
/// explicit system-actor relaxation that failure records — and only failure
/// records — may use.
/// </summary>
public sealed class PlatformAuditIntegrityTests
{
    // ---- Service invariants -------------------------------------------------

    [Fact]
    public async Task Write_defaults_to_a_success_result()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        var audit = new PlatformAuditService(context, NullLogger<PlatformAuditService>.Instance);

        await audit.WriteAsync(PlatformActor(), "tenant.suspend", nameof(Tenant), "42");

        var row = await context.Set<PlatformAuditLog>().SingleAsync();
        Assert.Equal(PlatformAuditResults.Success, row.Result);
        Assert.Equal(7, row.ActorPlatformUserId);
    }

    [Fact]
    public async Task A_failure_record_without_an_actor_is_pinned_to_the_system_actor()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        var audit = new PlatformAuditService(context, NullLogger<PlatformAuditService>.Instance);

        await audit.WriteAsync(new ClaimsPrincipal(new ClaimsIdentity()), "platform.login.failed",
            nameof(PlatformUser), null, new { email = "nobody@example.test" }, null, null,
            PlatformAuditResults.Failure);

        var row = await context.Set<PlatformAuditLog>().SingleAsync();
        Assert.Equal(PlatformAuditService.SystemActorId, row.ActorPlatformUserId);
        Assert.Equal(PlatformAuditResults.Failure, row.Result);
    }

    [Fact]
    public async Task A_success_record_still_requires_a_positive_actor()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        var audit = new PlatformAuditService(context, NullLogger<PlatformAuditService>.Instance);

        // The relaxation is scoped to failure records only — an anonymous SUCCESS
        // write keeps throwing, exactly as before this increment.
        await Assert.ThrowsAsync<InvalidOperationException>(() => audit.WriteAsync(
            new ClaimsPrincipal(new ClaimsIdentity()), "tenant.suspend", nameof(Tenant), "42"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => audit.WriteAsync(
            new ClaimsPrincipal(new ClaimsIdentity()), "tenant.suspend", nameof(Tenant), "42",
            null, null, null, PlatformAuditResults.Success));

        Assert.Empty(await context.Set<PlatformAuditLog>().ToListAsync());
    }

    [Fact]
    public async Task An_unknown_result_value_is_rejected()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        var audit = new PlatformAuditService(context, NullLogger<PlatformAuditService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => audit.WriteAsync(
            PlatformActor(), "tenant.suspend", nameof(Tenant), "42", null, null, null, "partial"));
    }

    // ---- /api/platform/audit ------------------------------------------------

    [Fact]
    public async Task Audit_endpoint_returns_the_persisted_result_and_filters_failures_for_real()
    {
        using var db = new TestDb();
        await using (var seed = db.ContextFor(null))
        {
            seed.Set<PlatformAuditLog>().AddRange(
                Row("platform.login", PlatformAuditResults.Success, 7),
                Row("platform.login.failed", PlatformAuditResults.Failure, 0),
                Row("platform.login.failed", PlatformAuditResults.Failure, 0));
            await seed.SaveChangesAsync();
        }

        await using var context = db.ContextFor(null);
        var controller = OperationsController(context);

        var failures = await Rows(controller.Audit(null, null, "failure", null, CancellationToken.None));
        Assert.Equal(2, failures.Count);
        Assert.All(failures, row => Assert.Equal("failure", row.GetProperty("result").GetString()));

        var everything = await Rows(controller.Audit(null, null, null, null, CancellationToken.None));
        Assert.Equal(3, everything.Count);
        Assert.Contains(everything, row => row.GetProperty("result").GetString() == "success");
    }

    [Fact]
    public async Task Search_is_applied_before_the_500_row_cap()
    {
        using var db = new TestDb();
        var anchor = DateTime.UtcNow;
        await using (var seed = db.ContextFor(null))
        {
            // One matching row, older than 500 newer non-matching rows: the old
            // post-Take(500) filtering could never see it.
            var needle = Row("tenant.archive", PlatformAuditResults.Success, 7);
            needle.TargetId = "needle-tenant";
            needle.CreatedOn = anchor.AddHours(-10);
            seed.Set<PlatformAuditLog>().Add(needle);
            for (var i = 0; i < 500; i++)
            {
                var noise = Row("tenant.noise", PlatformAuditResults.Success, 7);
                noise.CreatedOn = anchor.AddSeconds(-i);
                seed.Set<PlatformAuditLog>().Add(noise);
            }
            await seed.SaveChangesAsync();
        }

        await using var context = db.ContextFor(null);
        var rows = await Rows(OperationsController(context).Audit(
            null, null, null, "needle", CancellationToken.None));

        var match = Assert.Single(rows);
        Assert.Equal("needle-tenant", match.GetProperty("targetId").GetString());
    }

    [Fact]
    public async Task Legacy_audit_route_withholds_billing_payload_from_read_only_ops()
    {
        using var db = new TestDb();
        await using (var seed = db.ContextFor(null))
        {
            var row = Row("billing.tenant.commercial-terms", PlatformAuditResults.Success, 7);
            row.Metadata = "{\"billingModeReason\":\"confidential concession\"}";
            seed.Set<PlatformAuditLog>().Add(row);
            await seed.SaveChangesAsync();
        }

        await using var context = db.ContextFor(null);
        var controller = OperationsController(context);
        controller.HttpContext.User = PlatformSupportFixture.Actor(
            role: PlatformRole.ReadOnlyOps, email: "readonly@example.test");

        var rows = await Rows(controller.Audit(null, null, null, null, CancellationToken.None));
        var result = Assert.Single(rows);
        Assert.Equal(JsonValueKind.Null, result.GetProperty("detail").ValueKind);
        Assert.False(result.GetProperty("metadataDisclosed").GetBoolean());
        Assert.Equal(PlatformPolicies.Billing, result.GetProperty("requiredPolicy").GetString());
    }

    // ---- Platform login auditing -------------------------------------------

    [Fact]
    public async Task Successful_platform_login_is_audited_as_the_user_who_logged_in()
    {
        using var db = new TestDb();
        long userId;
        await using (var seed = db.ContextFor(null))
        {
            var user = new PlatformUser
            {
                Email = "owner@example.test",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("owner-password-123"),
                PlatformRole = PlatformRole.Owner,
                IsActive = true,
                CreatedOn = DateTime.UtcNow
            };
            seed.Set<PlatformUser>().Add(user);
            await seed.SaveChangesAsync();
            userId = user.Id;
        }

        await using var context = db.ContextFor(null);
        var controller = AuthController(context);

        var result = await controller.Login(new PlatformLoginRequest
        {
            Email = "owner@example.test",
            Password = "owner-password-123"
        });

        Assert.IsType<OkObjectResult>(result.Result);
        await using var verification = db.ContextFor(null);
        var audit = await verification.Set<PlatformAuditLog>().SingleAsync(a => a.Action == "platform.login");
        Assert.Equal(userId, audit.ActorPlatformUserId);
        Assert.Equal(PlatformAuditResults.Success, audit.Result);
        Assert.DoesNotContain("owner-password-123", audit.Metadata ?? string.Empty);
    }

    [Fact]
    public async Task Failed_platform_login_is_audited_with_result_failure_under_the_system_actor()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        var controller = AuthController(context);

        var result = await controller.Login(new PlatformLoginRequest
        {
            Email = "ghost@example.test",
            Password = "wrong-password-123"
        });

        Assert.IsType<UnauthorizedObjectResult>(result.Result);
        await using var verification = db.ContextFor(null);
        var audit = await verification.Set<PlatformAuditLog>().SingleAsync();
        Assert.Equal("platform.login.failed", audit.Action);
        Assert.Equal(PlatformAuditResults.Failure, audit.Result);
        Assert.Equal(PlatformAuditService.SystemActorId, audit.ActorPlatformUserId);
        Assert.Contains("ghost@example.test", audit.Metadata);
        Assert.DoesNotContain("wrong-password-123", audit.Metadata);
    }

    // ---- Helpers ------------------------------------------------------------

    private static ClaimsPrincipal PlatformActor() => new(new ClaimsIdentity(
    [
        new Claim("sub", "7"),
        new Claim("email", "operator@example.test")
    ], "Platform"));

    private static PlatformAuditLog Row(string action, string result, long actorId) => new()
    {
        ActorPlatformUserId = actorId,
        Action = action,
        Result = result,
        CreatedOn = DateTime.UtcNow
    };

    private static PlatformOperationsController OperationsController(ErpRfqAutomationContext context) => new(
        context, new PlatformAuditService(context, NullLogger<PlatformAuditService>.Instance),
        PlatformSupportFixture.Authorization())
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = PlatformActor() }
        }
    };

    private static PlatformAuthController AuthController(ErpRfqAutomationContext context)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "tenant-signing-key-that-is-at-least-32-bytes",
            ["Jwt:Issuer"] = "nexora-tests",
            ["Jwt:Audience"] = "RFQ",
            ["Jwt:PlatformIssuer"] = "nexora-tests"
        }).Build();
        return new PlatformAuthController(
            new PlatformAuthService(context, configuration, NullLogger<PlatformAuthService>.Instance),
            new OpenThrottle(),
            new PlatformAuditService(context, NullLogger<PlatformAuditService>.Instance))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private static async Task<List<JsonElement>> Rows(Task<IActionResult> pending)
    {
        var ok = Assert.IsType<OkObjectResult>(await pending);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        return document.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    private sealed class OpenThrottle : ILoginAttemptThrottle
    {
        public Task<LoginLockoutState> CheckAsync(string plane, string? identifier, CancellationToken cancellationToken = default)
            => Task.FromResult(LoginLockoutState.Allowed);

        public Task RegisterFailureAsync(string plane, string? identifier, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RegisterSuccessAsync(string plane, string? identifier, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
