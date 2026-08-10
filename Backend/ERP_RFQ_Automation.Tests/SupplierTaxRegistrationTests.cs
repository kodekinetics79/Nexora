using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.OrderToCash;
using ERP_RFQ_Automation.Tax;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The input-VAT evidence finding.
///
/// <para>Excluding recoverable supplier input VAT from landed cost (R15/R18) implicitly books a
/// reclaim, and a reclaim requires a valid supplier tax invoice carrying the supplier's VAT
/// registration number. Neither <c>Supplier</c> nor <c>BusinessUnit</c> held a tax registration
/// number at all, so the position rested on nothing an auditor could look at. These tests pin the
/// format rule and the fail-closed guard.</para>
///
/// <para>EVERY registration number below is SYNTHETIC. The Saudi-shaped values are constructed to
/// satisfy the published format (15 digits, first and last digit 3) and are deliberately made of
/// repeated 9s so they cannot be mistaken for, or reused as, a real registration.</para>
/// </summary>
public sealed class SupplierTaxRegistrationTests
{
    private const long Tenant = 98_001;
    private const long SupplierId = 98_101;

    /// <summary>SYNTHETIC. Correct KSA shape (15 digits, 3…3); not a real registration.</summary>
    private const string SyntheticSaudiVatNumber = "399999999999993";

    // ============================================================================
    // Format
    // ============================================================================

    /// <summary>Separators humans type are removed, so one registration has one stored form.</summary>
    [Theory]
    [InlineData("399999999999993", "399999999999993")]
    [InlineData("399 999 999 999 993", "399999999999993")]
    [InlineData("399-999-999-999-993", "399999999999993")]
    [InlineData("  gb999999973  ", "GB999999973")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void A_registration_number_is_canonicalised_before_it_is_stored(string? input, string? expected)
        => Assert.Equal(expected, TaxRegistrationNumbers.Normalize(input));

    /// <summary>
    /// A value that CLAIMS to be Saudi — all digits, leading 3 — must be a well-formed KSA VAT
    /// number. A malformed one is worse than an absent one: it looks like evidence and is not.
    /// </summary>
    [Theory]
    [InlineData("399999999999993")]  // SYNTHETIC, correct shape
    [InlineData("3999999999999 93")] // SYNTHETIC, same number typed with a space
    public void A_well_formed_saudi_claim_is_accepted(string input)
    {
        Assert.True(TaxRegistrationNumbers.TryCanonicalize(input, "TRN", out var canonical, out var error));
        Assert.Null(error);
        Assert.Equal(SyntheticSaudiVatNumber, canonical);
    }

    [Theory]
    [InlineData("39999999999999")]   // 14 digits
    [InlineData("3999999999999999")] // 16 digits
    [InlineData("399999999999999")]  // right length, does not end in 3
    public void A_malformed_saudi_claim_is_refused_with_the_rule_stated(string input)
    {
        Assert.False(TaxRegistrationNumbers.TryCanonicalize(input, "TRN", out _, out var error));
        Assert.Contains("15 digits", error);
        Assert.Contains("beginning with 3 and ending with 3", error);
    }

    /// <summary>
    /// A foreign supplier will never have a Saudi TRN, and refusing every non-Saudi string would
    /// make the field unusable on exactly the import lines where the tax treatment matters most.
    /// The claim test is narrow on purpose: a UAE TRN is also 15 digits but starts with 1, and EU
    /// numbers carry a country prefix, so neither is swept into the Saudi rule.
    /// </summary>
    [Theory]
    [InlineData("DE999999999")]      // SYNTHETIC German-shaped
    [InlineData("GB999999973")]      // SYNTHETIC UK-shaped
    [InlineData("100999999999999")]  // SYNTHETIC UAE-shaped: 15 digits, leading 1
    public void A_foreign_registration_is_accepted_rather_than_forced_into_the_ksa_format(string input)
    {
        Assert.True(TaxRegistrationNumbers.TryCanonicalize(input, "TRN", out var canonical, out var error));
        Assert.Null(error);
        Assert.Equal(input, canonical);
    }

    [Theory]
    [InlineData("AB1")]                   // too short to be a registration
    [InlineData("DE 999*999#999")]        // characters no registration format uses
    public void An_implausible_value_is_refused(string input)
        => Assert.False(TaxRegistrationNumbers.TryCanonicalize(input, "TRN", out _, out _));

    // ============================================================================
    // The guard
    // ============================================================================

    /// <summary>
    /// THE TEST THAT MATTERS. The tenant recovers input tax, the supplier charged some, and we
    /// cannot name that supplier to the authority — so the treatment is refused, in words that say
    /// what is missing and how to fix it.
    /// </summary>
    [Fact]
    public async Task Recoverable_treatment_is_refused_when_the_supplier_has_no_registration_number()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        SeedSupplier(context, taxRegistrationNumber: null);

        var refusal = await Assert.ThrowsAsync<SupplierInputTaxEvidenceMissingException>(() =>
            context.EnsureSupplierInputTaxIsClaimableAsync(Tenant, SupplierId, supplierTaxAmount: 1_500m));

        Assert.Contains("has no tax registration number", refusal.Message);
        Assert.Contains("Add the supplier's tax registration number", refusal.Message);
        Assert.Contains("set its commercial policy's supplier input tax recoverable percentage to 0",
            refusal.Message);
    }

    /// <summary>With the registration on file the reclaim is at least attributable, and it passes.</summary>
    [Fact]
    public async Task Recoverable_treatment_is_allowed_once_the_registration_number_is_captured()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        SeedSupplier(context, SyntheticSaudiVatNumber);

        await context.EnsureSupplierInputTaxIsClaimableAsync(Tenant, SupplierId, supplierTaxAmount: 1_500m);
    }

    /// <summary>
    /// A tenant that cannot reclaim input tax is booking no reclaim, so there is nothing to
    /// substantiate and nothing to refuse. It is not made to chase registration numbers it has no
    /// use for.
    /// </summary>
    [Fact]
    public async Task A_tenant_that_recovers_nothing_is_not_asked_for_evidence_it_does_not_need()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        SeedSupplier(context, taxRegistrationNumber: null);

        await context.EnsureSupplierInputTaxIsClaimableAsync(Tenant, SupplierId,
            supplierTaxAmount: 1_500m, supplierInputTaxRecoverablePercent: 0m);
    }

    /// <summary>No tax charged, no reclaim, no evidence needed.</summary>
    [Fact]
    public async Task A_line_carrying_no_supplier_tax_needs_no_registration_number()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        SeedSupplier(context, taxRegistrationNumber: null);

        await context.EnsureSupplierInputTaxIsClaimableAsync(Tenant, SupplierId, supplierTaxAmount: 0m);
    }

    /// <summary>
    /// A partly exempt tenant (R18) recovers pro-rata — still a reclaim, still needs the supplier
    /// named. Anything above zero fails closed.
    /// </summary>
    [Fact]
    public async Task Partial_recovery_still_requires_the_supplier_to_be_identifiable()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        SeedSupplier(context, taxRegistrationNumber: null);

        await Assert.ThrowsAsync<SupplierInputTaxEvidenceMissingException>(() =>
            context.EnsureSupplierInputTaxIsClaimableAsync(Tenant, SupplierId,
                supplierTaxAmount: 1_500m, supplierInputTaxRecoverablePercent: 70m));
    }

    /// <summary>A supplier belonging to another tenant is not a supplier we can claim against.</summary>
    [Fact]
    public async Task A_supplier_outside_the_tenant_is_never_claimable()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        SeedSupplier(context, SyntheticSaudiVatNumber);

        await Assert.ThrowsAsync<SupplierInputTaxEvidenceMissingException>(() =>
            context.EnsureSupplierInputTaxIsClaimableAsync(Tenant + 1, SupplierId, supplierTaxAmount: 1_500m));
    }

    // ============================================================================
    // The business unit's own registration
    // ============================================================================

    /// <summary>
    /// The claimant can now state its own VAT number, which it could not before: the only
    /// <c>TaxNumber</c> in the system belonged to the SaaS control-plane tenant record — who pays
    /// for Nexora, not which registered entity is claiming this input tax.
    /// </summary>
    [Fact]
    public async Task A_business_unit_can_hold_its_own_registration_number()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        var businessUnit = Support.Seed.EnsureBusinessUnit(context, Tenant);
        businessUnit.TaxRegistrationNumber = SyntheticSaudiVatNumber; // SYNTHETIC
        context.SaveChanges();
        context.ChangeTracker.Clear();

        var stored = await context.BusinessUnits.AsNoTracking().SingleAsync(x => x.Id == Tenant);
        Assert.Equal(SyntheticSaudiVatNumber, stored.TaxRegistrationNumber);
    }

    private static void SeedSupplier(ErpRfqAutomationContext context, string? taxRegistrationNumber)
    {
        Support.Seed.EnsureBusinessUnit(context, Tenant);
        context.SaveChanges();
        context.Suppliers.Add(new Supplier
        {
            Id = SupplierId,
            Buid = Tenant,
            Name = "Test Supplier",
            ImageUrl = string.Empty,
            TaxRegistrationNumber = taxRegistrationNumber,
            CreatedBy = "tests",
            CreatedOn = DateTime.UtcNow
        });
        context.SaveChanges();
        context.ChangeTracker.Clear();
    }
}
