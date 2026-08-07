using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Provisioning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The duplicate-tenant guard, and the reserved-address rules.
///
/// <para><b>The state this pins.</b> A double-click, a client retry or a proxy replay attempted a
/// second, complete provision, and the only thing that stopped it was the slug and email unique
/// indexes firing mid-transaction — by accident of schema rather than by design, and reported to
/// the operator as a generic failure rather than as "you already did this". Nothing refused
/// <c>admin</c>, <c>api</c> or <c>nexora</c> as a workspace address at all.</para>
/// </summary>
public sealed class ProvisioningIdempotencyTests
{
    [Fact]
    public async Task The_same_key_and_the_same_payload_returns_the_original_and_provisions_once()
    {
        using var harness = new ProvisioningHarness();
        var planId = await harness.PlanAsync();
        var request = ProvisioningHarness.Request("idempotent-co", "ada@idempotent.test", planId);

        var first = await harness.SubmitAsync(request, "operator-key-0001");
        Assert.Equal(ProvisioningSubmitOutcome.Created, first.Outcome);

        var second = await harness.SubmitAsync(request, "operator-key-0001");
        Assert.Equal(ProvisioningSubmitOutcome.Replayed, second.Outcome);
        Assert.Equal(first.Execution!.Id, second.Execution!.Id);

        // A replay must NOT re-reveal a one-time secret: only a BCrypt hash is stored, and if the
        // key could hand the credential back it would have become a retrieval endpoint.
        Assert.Null(second.GeneratedPassword);

        await harness.Runner().RunAvailableAsync(10);

        // A third submit after completion still replays rather than provisioning again.
        var third = await harness.SubmitAsync(request, "operator-key-0001");
        Assert.Equal(ProvisioningSubmitOutcome.Replayed, third.Outcome);
        Assert.Equal(ProvisioningExecutionState.Succeeded, third.Execution!.State);

        await using var db = harness.Context();
        Assert.Equal(1, await db.Set<ProvisioningExecution>().CountAsync());
        Assert.Equal(1, await db.Set<Tenant>().IgnoreQueryFilters().CountAsync(t => t.Slug == "idempotent-co"));
        Assert.Equal(1, await db.Set<BusinessUnit>().CountAsync(b => b.BusinessUnitCode == "IDEMPOTENT-CO"));
        Assert.Equal(1, await db.Users.IgnoreQueryFilters().CountAsync(u => u.Email == "ada@idempotent.test"));
    }

    [Fact]
    public async Task The_same_key_with_a_changed_payload_is_refused_rather_than_silently_accepted()
    {
        using var harness = new ProvisioningHarness();
        var planId = await harness.PlanAsync();

        var first = await harness.SubmitAsync(
            ProvisioningHarness.Request("conflicted-co", "ada@conflicted.test", planId), "reused-key-01");
        Assert.Equal(ProvisioningSubmitOutcome.Created, first.Outcome);

        // Same key, different administrator. Returning the first result would silently discard
        // the change; running it would silently provision a tenant nobody asked for.
        var changed = await harness.SubmitAsync(
            ProvisioningHarness.Request("conflicted-co", "someone-else@conflicted.test", planId),
            "reused-key-01");

        Assert.Equal(ProvisioningSubmitOutcome.Conflict, changed.Outcome);
        Assert.Contains("already used for a DIFFERENT request", changed.Error);
        Assert.Equal(first.Execution!.Id, changed.Execution!.Id);

        await using var db = harness.Context();
        Assert.Equal(1, await db.Set<ProvisioningExecution>().CountAsync());
    }

    [Fact]
    public async Task Insignificant_differences_still_count_as_the_same_request()
    {
        using var harness = new ProvisioningHarness();
        var planId = await harness.PlanAsync();

        var first = await harness.SubmitAsync(
            ProvisioningHarness.Request("whitespace-co", "ada@whitespace.test", planId), "ws-key-01");
        Assert.Equal(ProvisioningSubmitOutcome.Created, first.Outcome);

        // A client that re-serialises its form object has no obligation to trim identically, and
        // telling it "different payload" would break the very retry it was attempting.
        var padded = ProvisioningHarness.Request("  whitespace-co  ", "ada@whitespace.test", planId);
        padded.Name = "  Tenant whitespace-co  ";
        padded.CountryCode = "sa";

        var replay = await harness.SubmitAsync(padded, "ws-key-01");
        Assert.Equal(ProvisioningSubmitOutcome.Replayed, replay.Outcome);
        Assert.Equal(first.Execution!.Id, replay.Execution!.Id);
    }

    [Fact]
    public async Task A_double_submit_without_a_key_is_still_refused_by_the_live_slug_guard()
    {
        using var harness = new ProvisioningHarness();
        var planId = await harness.PlanAsync();
        var request = ProvisioningHarness.Request("doubleclick-co", "ada@doubleclick.test", planId);

        var first = await harness.SubmitAsync(request);
        Assert.Equal(ProvisioningSubmitOutcome.Created, first.Outcome);

        // A caller that sends no key gets no replay, but must still not start a rival attempt on
        // the same tenant — the guard that the old design only had by accident.
        var second = await harness.SubmitAsync(request);
        Assert.Equal(ProvisioningSubmitOutcome.Conflict, second.Outcome);
        Assert.Contains("already in progress", second.Error);
        Assert.Contains(first.Execution!.Id.ToString(), second.Error);

        await using var db = harness.Context();
        Assert.Equal(1, await db.Set<ProvisioningExecution>().CountAsync());
    }

    [Fact]
    public async Task A_failed_execution_still_owns_its_address_until_it_is_retried_or_cancelled()
    {
        using var harness = new ProvisioningHarness();
        var planId = await harness.PlanAsync();

        var submitted = await harness.SubmitAsync(
            ProvisioningHarness.Request("stalled-co", "ada@stalled.test", planId));

        // Fail it, so the execution is at rest holding a tenant row and a business unit.
        await SeedRivalUserAsync(harness, "ada@stalled.test");
        await harness.Runner().RunAsync(submitted.Execution!.Id);
        Assert.Equal(ProvisioningExecutionState.Failed, (await harness.ReloadAsync(submitted.Execution.Id)).State);

        // A fresh submit for the same address must be refused: letting it through would strand
        // the rows the failed attempt already committed with nothing pointing at them.
        var rival = await harness.SubmitAsync(
            ProvisioningHarness.Request("stalled-co", "different@stalled.test", planId));
        Assert.Equal(ProvisioningSubmitOutcome.Conflict, rival.Outcome);

        // Cancelling releases the address for a fresh attempt, which is the deliberate way out.
        using (var scope = harness.Scope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITenantProvisioningService>();
            await service.CancelAsync(submitted.Execution.Id, "Abandoned", "owner@nexora.app");
        }

        var after = await harness.SubmitAsync(
            ProvisioningHarness.Request("stalled-co-2", "different@stalled.test", planId));
        Assert.Equal(ProvisioningSubmitOutcome.Created, after.Outcome);
    }

    // ---- reserved addresses ------------------------------------------------------------------

    [Theory]
    // The names the brief calls out by name.
    [InlineData("admin")]
    [InlineData("api")]
    [InlineData("platform")]
    [InlineData("www")]
    [InlineData("support")]
    [InlineData("billing")]
    [InlineData("nexora")]
    [InlineData("static")]
    [InlineData("assets")]
    // Backend top-level endpoints and console routes: a tenant holding one of these would shadow,
    // or be shadowed by, an existing URL the moment anything resolves /{slug}.
    [InlineData("health")]
    [InlineData("metrics")]
    [InlineData("swagger")]
    [InlineData("login")]
    [InlineData("activate")]
    [InlineData("dashboard")]
    [InlineData("suppliers")]
    // Privilege and vendor identity.
    [InlineData("root")]
    [InlineData("system")]
    [InlineData("superadmin")]
    [InlineData("postmaster")]
    [InlineData("noreply")]
    // Environments: a tenant called "staging" makes every operational sentence ambiguous.
    [InlineData("staging")]
    [InlineData("production")]
    [InlineData("localhost")]
    public void Reserved_addresses_are_refused_with_a_reason_a_person_can_act_on(string slug)
    {
        var verdict = ReservedTenantSlugs.Judge(slug);

        Assert.False(verdict.IsAccepted);
        Assert.Contains(verdict.Reason,
            new[] { SlugRefusalReason.RouteCollision, SlugRefusalReason.Impersonation });
        // The message has to name the value and say what to do, or the operator retypes it.
        Assert.Contains($"'{slug}'", verdict.Message);
        Assert.Contains("Choose a different one", verdict.Message);
    }

    [Theory]
    [InlineData("nexora-support")]
    [InlineData("nexorabilling")]
    [InlineData("nexora-security-team")]
    [InlineData("kodekinetics-billing")]
    public void Anything_that_could_be_read_as_the_vendor_is_refused_by_prefix_not_by_exact_match(string slug)
    {
        // A blocklist of exact strings cannot cover these, and they are precisely the addresses a
        // phishing setup would ask for.
        var verdict = ReservedTenantSlugs.Judge(slug);
        Assert.Equal(SlugRefusalReason.Impersonation, verdict.Reason);
        Assert.Contains("vendor", verdict.Message);
    }

    [Theory]
    [InlineData("404", SlugRefusalReason.Confusable)]
    [InlineData("12345", SlugRefusalReason.Confusable)]
    [InlineData("xn--80ak6aa92e", SlugRefusalReason.Confusable)]
    [InlineData("ab", SlugRefusalReason.Malformed)]
    [InlineData("-leading", SlugRefusalReason.Malformed)]
    [InlineData("trailing-", SlugRefusalReason.Malformed)]
    [InlineData("Upper", SlugRefusalReason.Malformed)]
    public void Shapes_that_are_ambiguous_or_malformed_are_refused_server_side(
        string slug, SlugRefusalReason expected)
    {
        // The console enforces a shape rule; this is the copy that binds. The existing endpoint
        // simply re-slugifies whatever arrives, so a direct API call can create a tenant
        // addressed "404" today.
        var verdict = ReservedTenantSlugs.Judge(slug);
        Assert.Equal(expected, verdict.Reason);
    }

    [Fact]
    public void An_address_longer_than_the_business_unit_code_column_is_refused_before_it_can_fail()
    {
        // The defect this closes: the existing Slugify truncates at 60 characters, but
        // BusinessUnits.BusinessUnitCode is varchar(50). A 51-60 character address passes the
        // tenant insert and then fails the business-unit insert inside the same transaction,
        // surfacing as the generic "Provisioning failed." with no field named.
        var fiftyOne = new string('a', 51);
        var verdict = ReservedTenantSlugs.Judge(fiftyOne);

        Assert.Equal(SlugRefusalReason.Malformed, verdict.Reason);
        Assert.Contains("50-character column", verdict.Message);

        // The derivation path caps rather than refuses, so a long COMPANY NAME still works.
        var derived = ReservedTenantSlugs.Evaluate(null, new string('b', 90));
        Assert.True(derived.IsAccepted);
        Assert.Equal(ReservedTenantSlugs.MaximumLength, derived.Slug!.Length);
    }

    [Fact]
    public void A_company_literally_named_Admin_is_caught_after_derivation_not_before()
    {
        // Checking the typed name rather than the derived address is how "Admin" gets checked as
        // "Admin" (not in the list, wrong case) and stored as "admin" (very much in the list).
        var verdict = ReservedTenantSlugs.Evaluate(requestedSlug: null, tenantName: "Admin");
        Assert.False(verdict.IsAccepted);
        // "admin" is both a console route and a privilege word; either classification is a
        // refusal and the operator acts on the message, not the enum.
        Assert.Contains(verdict.Reason,
            new[] { SlugRefusalReason.RouteCollision, SlugRefusalReason.Impersonation });

        // A real company whose name merely CONTAINS a reserved word is fine — the rule is about
        // the whole address, not about substrings.
        Assert.True(ReservedTenantSlugs.Evaluate(null, "Admin Logistics Ltd").IsAccepted);
        Assert.True(ReservedTenantSlugs.Evaluate(null, "Support Services Group").IsAccepted);
    }

    [Fact]
    public async Task Submitting_a_reserved_address_is_refused_before_anything_is_written()
    {
        using var harness = new ProvisioningHarness();
        var planId = await harness.PlanAsync();

        var result = await harness.SubmitAsync(
            ProvisioningHarness.Request("platform", "ada@platform.test", planId));

        Assert.Equal(ProvisioningSubmitOutcome.SlugRefused, result.Outcome);
        Assert.Equal(SlugRefusalReason.RouteCollision, result.SlugReason);

        await using var db = harness.Context();
        Assert.Equal(0, await db.Set<ProvisioningExecution>().CountAsync());
        Assert.Equal(0, await db.Set<Tenant>().IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task The_wizards_address_check_answers_reserved_taken_and_in_flight_separately()
    {
        using var harness = new ProvisioningHarness();
        var planId = await harness.PlanAsync();

        using var scope = harness.Scope();
        var service = scope.ServiceProvider.GetRequiredService<ITenantProvisioningService>();

        var free = await service.CheckSlugAsync(null, "Northwind Trading");
        Assert.True(free.IsAvailable);
        Assert.Equal("northwind-trading", free.Slug);

        var reserved = await service.CheckSlugAsync("api", null);
        Assert.False(reserved.IsAvailable);
        Assert.Equal(nameof(SlugRefusalReason.RouteCollision), reserved.Reason);

        await harness.SubmitAsync(
            ProvisioningHarness.Request("inflight-co", "ada@inflight.test", planId));

        // "Claimed by an unfinished attempt" and "taken by a live tenant" are different problems
        // with different fixes, so the wizard is told which one it is.
        var inFlight = await service.CheckSlugAsync("inflight-co", null);
        Assert.False(inFlight.IsAvailable);
        Assert.Contains("has not finished", inFlight.Message);
    }

    private static async Task SeedRivalUserAsync(ProvisioningHarness harness, string email)
    {
        await using var db = harness.Context();
        var businessUnit = new BusinessUnit
        {
            BusinessUnitCode = $"RIVAL-{Guid.NewGuid():N}"[..20],
            BusinessUnitName = "Rival",
            IsActive = true,
            CreatedBy = "tests",
            CreatedOn = DateTime.UtcNow
        };
        db.Set<BusinessUnit>().Add(businessUnit);
        await db.SaveChangesAsync();

        db.Users.Add(new User
        {
            FirstName = "Rival", LastName = "Holder", Email = email,
            PasswordHash = "x", ImageUrl = string.Empty, Buid = businessUnit.Id,
            IsActive = true, CreatedBy = "tests", CreatedOn = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }
}
