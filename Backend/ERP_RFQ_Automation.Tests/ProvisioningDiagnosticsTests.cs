using System.Security.Claims;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Activation;
using ERP_RFQ_Automation.Platform.Entitlements;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Provisioning;
using ERP_RFQ_Automation.Platform.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The provisioning failure projection: the read model that replaced "Provisioning failed."
///
/// <para><b>The state this pins.</b> Every fact these tests assert on was already persisted — the
/// failed step, its code, the sentence the runner wrote, the correlation id — and none of it
/// reached a human. The console rendered one sentence for four unrelated causes, so an operator
/// could not tell a mistyped address from a missing database GRANT from a worker that was never
/// switched on, and every one of them became a ticket routed by guesswork.</para>
/// </summary>
public sealed class ProvisioningDiagnosticsTests
{
    [Theory]
    [InlineData(ProvisioningExecutionState.Pending, "NO_FAILURE")]
    [InlineData(ProvisioningExecutionState.Running, "NO_FAILURE")]
    [InlineData(ProvisioningExecutionState.Cancelled, "CANCELLED")]
    [InlineData(ProvisioningExecutionState.Failed, "RETRYABLE_SYSTEM_FAILURE")]
    public async Task Execution_state_without_a_failed_step_is_classified_truthfully(
        ProvisioningExecutionState state, string expected)
    {
        using var harness = new ProvisioningHarness();
        var planId = await harness.PlanAsync();
        var submitted = await harness.SubmitAsync(ProvisioningHarness.Request(
            "northwind-state", "state@northwind.test", planId,
            activation: AdminActivationMethods.Password, password: "Correct-Horse-9!"));
        await using (var db = harness.Context())
        {
            var row = await db.Set<ProvisioningExecution>().SingleAsync(x => x.Id == submitted.Execution!.Id);
            row.State = state;
            if (state == ProvisioningExecutionState.Running)
            {
                row.StartedOn = DateTime.UtcNow;
                row.LeaseUntil = DateTime.UtcNow.AddMinutes(5);
                row.LeaseOwner = "test-runner";
            }
            await db.SaveChangesAsync();
        }

        var result = await DiagnoseExecutionAsync(harness, submitted.Execution!.Id);
        Assert.Equal(expected, result.Classification);
        Assert.Null(result.FailedStep);
        if (state is ProvisioningExecutionState.Running or ProvisioningExecutionState.Cancelled)
            Assert.All(result.RecoveryActions, action => Assert.False(action.Available));
    }

    [Fact]
    public async Task Terminal_execution_failure_without_a_failed_step_is_not_reported_as_healthy()
    {
        using var harness = new ProvisioningHarness();
        var planId = await harness.PlanAsync();
        var submitted = await harness.SubmitAsync(ProvisioningHarness.Request(
            "northwind-terminal", "terminal@northwind.test", planId,
            activation: AdminActivationMethods.Password, password: "Correct-Horse-9!"));
        await using (var db = harness.Context())
        {
            var row = await db.Set<ProvisioningExecution>().SingleAsync(x => x.Id == submitted.Execution!.Id);
            row.State = ProvisioningExecutionState.Failed;
            row.FailureIsTerminal = true;
            row.FailureReason = "A required platform configuration is missing.";
            await db.SaveChangesAsync();
        }

        var result = await DiagnoseExecutionAsync(harness, submitted.Execution!.Id);
        Assert.Equal(ProvisioningIssueClassifications.PlatformConfiguration, result.Classification);
        Assert.False(result.RecoveryActions.Single(x => x.Action == "resume").Safe);
    }

    [Fact]
    public async Task A_taken_administrator_address_is_named_as_customer_input_and_is_not_retry_safe()
    {
        using var harness = new ProvisioningHarness();
        var planId = await harness.PlanAsync();

        // Accepted first — the submit-time validator rejects an address that is ALREADY taken, so
        // the only way to reach this failure is the way it happens in production: the address is
        // claimed by somebody else in the window between accepting the request and running it.
        var submitted = await harness.SubmitAsync(ProvisioningHarness.Request(
            "northwind-clash", "clash@northwind.test", planId,
            activation: AdminActivationMethods.Password, password: "Correct-Horse-9!"));
        Assert.Equal(ProvisioningSubmitOutcome.Created, submitted.Outcome);

        await using (var db = harness.Context())
        {
            Seed.EnsureBusinessUnit(db, 4242);
            db.Users.Add(new User
            {
                FirstName = "Prior", LastName = "Holder", Email = "clash@northwind.test",
                PasswordHash = "x", ImageUrl = string.Empty, Buid = 4242, IsActive = true,
                CreatedBy = "seed", CreatedOn = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var outcome = await harness.Runner().RunAsync(submitted.Execution!.Id);
        var execution = outcome!.Execution;
        Assert.Equal(ProvisioningExecutionState.Failed, execution.State);

        var diagnostics = await DiagnoseExecutionAsync(harness, execution.Id);

        Assert.Equal(nameof(ProvisioningExecutionState.Failed), diagnostics.Status);
        Assert.Equal(ProvisioningStepCodes.FoundingAdmin, diagnostics.FailedStep!.Step);
        Assert.Equal("email-taken", diagnostics.FailureCode);
        Assert.Equal(ProvisioningIssueClassifications.CustomerInput, diagnostics.Classification);
        Assert.Contains("already", diagnostics.FailureReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("email address", diagnostics.MissingPrerequisite!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(execution.CorrelationId, diagnostics.CorrelationId);

        // The steps BEFORE the failure are reported as done, not lost — that is the whole point of
        // a journal, and it is what tells the operator a tenant row already exists.
        Assert.Contains(ProvisioningStepCodes.Tenant, diagnostics.CompletedSteps);
        Assert.Contains(ProvisioningStepCodes.BusinessUnit, diagnostics.CompletedSteps);
        Assert.DoesNotContain(ProvisioningStepCodes.FoundingAdmin, diagnostics.CompletedSteps);

        // Offered, because the server would accept it — and marked UNSAFE with the reason, because
        // it re-runs the same step against the same taken address and fails identically.
        var resume = diagnostics.RecoveryActions.Single(x => x.Action == "resume");
        Assert.True(resume.Available);
        Assert.False(resume.Safe);
        Assert.Contains("terminal", resume.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_accepted_request_that_no_runner_ever_claimed_is_named_as_platform_configuration()
    {
        using var harness = new ProvisioningHarness();
        var planId = await harness.PlanAsync();

        var submitted = await harness.SubmitAsync(ProvisioningHarness.Request(
            "northwind-queued", "queued@northwind.test", planId,
            activation: AdminActivationMethods.Password, password: "Correct-Horse-9!"));
        Assert.Equal(ProvisioningSubmitOutcome.Created, submitted.Outcome);

        // Age it past the stall threshold WITHOUT running it: the deployment where the worker is
        // switched off, which on every other surface is indistinguishable from "slow".
        await using (var db = harness.Context())
        {
            var row = await db.Set<ProvisioningExecution>().SingleAsync(x => x.Id == submitted.Execution!.Id);
            row.CreatedOn = DateTime.UtcNow.AddHours(-1);
            await db.SaveChangesAsync();
        }

        var diagnostics = await DiagnoseExecutionAsync(harness, submitted.Execution!.Id);

        Assert.Equal(nameof(ProvisioningExecutionState.Pending), diagnostics.Status);
        Assert.Null(diagnostics.FailedStep);
        Assert.Equal(ProvisioningIssueClassifications.PlatformConfiguration, diagnostics.Classification);
        Assert.Contains("ProvisioningRunWorker", diagnostics.MissingPrerequisite!);
        // Never a blank screen: nothing failed, and the projection still explains why nothing moved.
        Assert.False(string.IsNullOrWhiteSpace(diagnostics.FailureReason));
    }

    [Fact]
    public async Task Production_blockers_are_listed_separately_from_local_test_blockers()
    {
        using var harness = new ProvisioningHarness();
        var planId = await harness.PlanAsync();

        var execution = await harness.ProvisionAsync(ProvisioningHarness.Request(
            "northwind-blockers", "blockers@northwind.test", planId,
            activation: AdminActivationMethods.Password, password: "Correct-Horse-9!"));
        Assert.Equal(ProvisioningExecutionState.Succeeded, execution.State);
        var tenantId = execution.TenantId!.Value;

        var diagnostics = await DiagnoseTenantAsync(harness, tenantId);

        Assert.Equal(nameof(ProvisioningExecutionState.Succeeded), diagnostics.Status);
        Assert.Equal("NO_FAILURE", diagnostics.Classification);
        Assert.Null(diagnostics.FailureReason);
        Assert.Null(diagnostics.FailedStep);
        Assert.Null(diagnostics.MissingPrerequisite);
        Assert.Equal(diagnostics.TotalStepCount, diagnostics.CompletedStepCount);
        Assert.False(diagnostics.RecoveryActions.Single(x => x.Action == "resume").Available);
        Assert.Equal(TenantDeploymentProfiles.Production, diagnostics.DeploymentProfile);
        Assert.Null(diagnostics.BlockersUnavailableReason);

        // This tenant has BOTH kinds outstanding: real defects in its own record, and the four
        // third-party dependencies. Production owes every one of them.
        Assert.Contains(diagnostics.ProductionBlockers, x => x.Code == "billing.currency-tax");
        Assert.Contains(diagnostics.ProductionBlockers, x => x.Code == "data.residency-isolation");
        Assert.Contains(diagnostics.ProductionBlockers, x => x.Code == "integrations.mandatory");
        Assert.Contains(diagnostics.ProductionBlockers, x => x.Code == "security.privileged-mfa-policy");
        Assert.Contains(diagnostics.ProductionBlockers, x => x.Code == "identity.legal-customer");

        // The local-test list is the strict subset that no profile may defer — the defects. The
        // four third-party dependencies are deliberately absent from it: an engineer reproducing a
        // bug on a laptop cannot connect the customer's ERP and must not be told to.
        Assert.Contains(diagnostics.LocalTestBlockers, x => x.Code == "identity.legal-customer");
        foreach (var deferrable in new[]
                 {
                     "billing.currency-tax", "data.residency-isolation",
                     "integrations.mandatory", "security.privileged-mfa-policy"
                 })
        {
            Assert.DoesNotContain(diagnostics.LocalTestBlockers, x => x.Code == deferrable);
            Assert.Contains(diagnostics.ProductionBlockers, x => x.Code == deferrable);
        }

        Assert.All(diagnostics.ProductionBlockers,
            blocker => Assert.Equal(TenantDeploymentProfiles.Production, blocker.Scope));
        Assert.All(diagnostics.LocalTestBlockers,
            blocker => Assert.Equal(TenantDeploymentProfiles.LocalTest, blocker.Scope));
        Assert.True(diagnostics.LocalTestBlockers.Count < diagnostics.ProductionBlockers.Count);

        // The prerequisite that governs no activation control still has to be answered for
        // somewhere, and production is the only place it ever is.
        Assert.Contains(diagnostics.ProductionBlockers,
            x => x.Code == DeploymentPrerequisiteCatalog.PrivateClamAv);
    }

    // ---- fixture ---------------------------------------------------------------------------

    private static async Task<TenantProvisioningDiagnostics> DiagnoseExecutionAsync(
        ProvisioningHarness harness, long executionId)
    {
        await using var db = harness.Context();
        var diagnostics = await ServiceFor(db).ForExecutionAsync(executionId);
        Assert.NotNull(diagnostics);
        return diagnostics!;
    }

    private static async Task<TenantProvisioningDiagnostics> DiagnoseTenantAsync(
        ProvisioningHarness harness, long tenantId)
    {
        await using var db = harness.Context();
        var diagnostics = await ServiceFor(db).ForTenantAsync(tenantId);
        Assert.NotNull(diagnostics);
        return diagnostics!;
    }

    private static ProvisioningDiagnosticsService ServiceFor(ErpRfqAutomationContext db)
        => new(db,
            new TenantActivationPolicyService(db, new NoopAudit(),
                new TenantAccessService(db, new MemoryCache(new MemoryCacheOptions()),
                    NullLogger<TenantAccessService>.Instance)),
            new StaticOptionsMonitor<ProvisioningOptions>(new ProvisioningOptions()));

    private sealed class NoopAudit : IPlatformAuditService
    {
        public Task WriteAsync(ClaimsPrincipal actor, string action, string? targetType = null,
            string? targetId = null, object? metadata = null, long? actAsTenantId = null,
            HttpContext? httpContext = null, CancellationToken ct = default) => Task.CompletedTask;
    }
}
