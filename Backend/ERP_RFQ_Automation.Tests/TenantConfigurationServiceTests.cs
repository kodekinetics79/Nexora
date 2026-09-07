using System.Security.Claims;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Configuration;
using ERP_RFQ_Automation.Platform.Lifecycle;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Tests.Support;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The aggregate read behind the redesigned customer screen, tested for what it must NOT say.
///
/// <para>This endpoint replaces eleven per-tab reads with one, and the hazard in that shape is
/// disclosure: a single response assembled from several subsystems inherits the WIDEST audience of
/// any field in it unless somebody checks each one. An adversarial review caught exactly that —
/// the first version returned legal-hold state, which is Owner-only everywhere else in this
/// control plane, from an endpoint gated on PlatformScope. These tests exist so the next field
/// added here has to answer the same question.</para>
/// </summary>
public sealed class TenantConfigurationServiceTests
{
    private const long TenantId = 77_001;
    private const long BusinessUnitId = 77_100;

    /// <summary>
    /// Whether a named customer is under legal hold is a litigation signal. TenantLegalHoldsController
    /// is class-level Owner and TenantOffboardingStatusDto carries no hold field at all, so an Owner
    /// is the only role that learns this anywhere else.
    /// </summary>
    [Theory]
    [InlineData(PlatformRole.SupportAdmin)]
    [InlineData(PlatformRole.BillingAdmin)]
    [InlineData(PlatformRole.ReadOnlyOps)]
    public async Task A_legal_hold_is_not_disclosed_to_anyone_but_an_owner(PlatformRole role)
    {
        using var db = new TestDb();
        await SeedAsync(db, withActiveLegalHold: true);

        await using var context = db.ContextFor(null);
        var view = await new TenantConfigurationService(context).ReadAsync(TenantId, Operator(role));

        Assert.NotNull(view);
        Assert.False(view!.State.LegalHoldActive);

        // The prose leaks the same fact, so it has to be suppressed with the flag rather than
        // alongside it. "Under legal hold — deletion and erasure are refused" is the sentence.
        Assert.DoesNotContain("legal hold", view.NextAction?.Label ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("legal hold", view.NextAction?.Detail ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_owner_sees_the_legal_hold_and_is_told_what_it_blocks()
    {
        using var db = new TestDb();
        await SeedAsync(db, withActiveLegalHold: true);

        await using var context = db.ContextFor(null);
        var view = await new TenantConfigurationService(context).ReadAsync(TenantId, Operator(PlatformRole.Owner));

        Assert.NotNull(view);
        Assert.True(view!.State.LegalHoldActive);
        Assert.Contains("legal hold", view.NextAction?.Label ?? "", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A RELEASED hold is not an active one. Guards against a predicate that finds any hold row.
    /// </summary>
    [Fact]
    public async Task A_released_legal_hold_is_not_reported_as_active()
    {
        using var db = new TestDb();
        await SeedAsync(db, withActiveLegalHold: true, releaseTheHold: true);

        await using var context = db.ContextFor(null);
        var view = await new TenantConfigurationService(context).ReadAsync(TenantId, Operator(PlatformRole.Owner));

        Assert.False(view!.State.LegalHoldActive);
    }

    /// <summary>
    /// The console stops guessing which slices it may write from a role name. The server answers,
    /// so an operator is never offered a control that is certain to 403 — and never denied one
    /// they could have used.
    /// </summary>
    [Theory]
    [InlineData(PlatformRole.Owner, "identity", true)]
    [InlineData(PlatformRole.Owner, "commercial", true)]
    [InlineData(PlatformRole.Owner, "deployment", true)]
    [InlineData(PlatformRole.SupportAdmin, "identity", true)]
    [InlineData(PlatformRole.SupportAdmin, "commercial", false)]
    [InlineData(PlatformRole.SupportAdmin, "deployment", false)]
    [InlineData(PlatformRole.BillingAdmin, "identity", false)]
    [InlineData(PlatformRole.BillingAdmin, "commercial", true)]
    [InlineData(PlatformRole.ReadOnlyOps, "identity", false)]
    [InlineData(PlatformRole.ReadOnlyOps, "commercial", false)]
    public async Task Each_slice_reports_whether_this_caller_may_write_it(
        PlatformRole role, string sliceKey, bool expectedEditable)
    {
        using var db = new TestDb();
        await SeedAsync(db);

        await using var context = db.ContextFor(null);
        var view = await new TenantConfigurationService(context).ReadAsync(TenantId, Operator(role));

        var slice = Assert.Single(view!.Slices, x => x.Key == sliceKey);
        Assert.Equal(expectedEditable, slice.Editable);
    }

    /// <summary>
    /// Operating defaults are derived, never keyboard input — that is the whole reason the region
    /// and currency typos stopped being possible. If one ever becomes editable, this fails.
    /// </summary>
    [Fact]
    public async Task Operating_defaults_are_derived_and_carry_the_reason_they_are_not_typed()
    {
        using var db = new TestDb();
        await SeedAsync(db);

        await using var context = db.ContextFor(null);
        var view = await new TenantConfigurationService(context).ReadAsync(TenantId, Operator(PlatformRole.Owner));

        var operating = Assert.Single(view!.Slices, x => x.Key == "operating");
        Assert.False(operating.Editable);
        Assert.All(operating.Fields, f =>
        {
            Assert.True(f.Derived, $"{f.Key} must be derived, not typed.");
            Assert.False(string.IsNullOrWhiteSpace(f.Source), $"{f.Key} must say where it comes from.");
        });
    }

    /// <summary>
    /// A null activation evaluator must read as "readiness unknown", never as "nothing blocking".
    /// An unwired probe must not be able to report a customer as clear to go live.
    /// </summary>
    [Fact]
    public async Task An_unavailable_activation_policy_never_reports_a_tenant_as_ready()
    {
        using var db = new TestDb();
        await SeedAsync(db, status: TenantStatus.Provisioning);

        await using var context = db.ContextFor(null);
        var view = await new TenantConfigurationService(context, activation: null)
            .ReadAsync(TenantId, Operator(PlatformRole.Owner));

        Assert.Empty(view!.Blockers);
        Assert.Equal("Readiness unknown", view.NextAction?.Label);
    }

    // ------------------------------------------------------------------ helpers

    private static ClaimsPrincipal Operator(PlatformRole role) => new(new ClaimsIdentity(
    [
        new Claim(PlatformAuthConstants.PlatformRoleClaim, role.ToString()),
        new Claim("email", $"{role}@nexora.test".ToLowerInvariant())
    ], "test"));

    private static async Task SeedAsync(
        TestDb db, bool withActiveLegalHold = false, bool releaseTheHold = false,
        TenantStatus status = TenantStatus.Active)
    {
        await using var seed = db.ContextFor(null);
        seed.Set<BusinessUnit>().Add(new BusinessUnit
        {
            Id = BusinessUnitId, BusinessUnitCode = "CFGTEST", BusinessUnitName = "Configuration test",
            IsActive = true, CreatedBy = "tests", CreatedOn = DateTime.UtcNow
        });
        seed.Set<Tenant>().Add(new Tenant
        {
            Id = TenantId, Name = "Configuration Co", Slug = "configuration-co", Status = status,
            PrimaryBusinessUnitId = BusinessUnitId,
            BillingContactEmail = "ap@configuration-co.test",
            BaseCurrencyCode = "SAR", TimeZoneId = "Asia/Riyadh", Locale = "en-SA", DataRegion = "me-central-1",
            CreatedBy = "tests", CreatedOn = DateTime.UtcNow
        });
        if (withActiveLegalHold)
            seed.Set<TenantLegalHold>().Add(new TenantLegalHold
            {
                TenantId = TenantId, Scope = "AllData", Authority = "Litigation",
                Reason = "Preserve all customer records for active litigation.",
                EvidenceReference = "case:configuration-co", PlacedBy = "owner@nexora.test",
                PlacedByPlatformUserId = 7, PlacedOn = DateTime.UtcNow.AddDays(-1),
                ReleasedOn = releaseTheHold ? DateTime.UtcNow : null
            });
        await seed.SaveChangesAsync();
    }
}
