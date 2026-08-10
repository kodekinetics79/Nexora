using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.MasterData;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Tax;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// FR-CST-01 — the customer master fields the BRD names: CR number, VAT registration number,
/// sector, Saudi region and assigned account team.
///
/// <para><b>Every registration number in this file is synthetic.</b> No real Saudi company's CR
/// number, VAT registration number or address appears anywhere in these tests. The values are
/// chosen to exercise the FORMAT rule — right length, wrong length, right prefix, wrong prefix —
/// and nothing else; "3000000000003" and "1010101010" identify no company.</para>
///
/// <para>Each test asserts a DEPENDENCE. Delete the field or the validator and the test fails; none
/// of them merely asserts that a value round-trips.</para>
/// </summary>
public sealed class Gate8CustomerMasterTests
{
    private const long Bu = 8_200;
    private const long OtherBu = 8_290;
    private static readonly AccountTeamScope Everything = AccountTeamScope.TenantWide(1);

    // ── CR number ────────────────────────────────────────────────────────────

    /// <summary>
    /// A 10-digit synthetic value is a well-formed KSA CR. Nine or eleven digits is not, and the
    /// message says which rule was broken — a validator that only says "invalid" makes the operator
    /// guess whether they mistyped or whether the field wants something else entirely.
    /// </summary>
    [Theory]
    [InlineData("1010101010", true)]
    [InlineData("101010101", false)]      // synthetic, nine digits
    [InlineData("10101010101", false)]    // synthetic, eleven digits
    public void A_claimed_saudi_cr_number_must_be_ten_digits(string value, bool accepted)
    {
        var ok = CommercialRegistrationNumbers.TryCanonicalize(
            value, "Commercial registration number", out var canonical, out var error);

        Assert.Equal(accepted, ok);
        if (accepted)
        {
            Assert.Equal(value, canonical);
            Assert.Null(error);
        }
        else
        {
            Assert.Contains("exactly 10 digits", error);
        }
    }

    /// <summary>
    /// A foreign customer has no CR and never will. Refusing every unfamiliar value would make the
    /// field unusable on exactly the accounts where it matters most, so a value carrying a country
    /// prefix is accepted — the same convention the tax-registration validator already states.
    /// </summary>
    [Fact]
    public void A_foreign_registration_carrying_its_country_prefix_is_accepted()
    {
        Assert.True(CommercialRegistrationNumbers.TryCanonicalize(
            "GB01234567", "Commercial registration number", out var canonical, out _));
        Assert.Equal("GB01234567", canonical);
    }

    /// <summary>
    /// Separators a person types are removed, so the same registration entered two ways is one
    /// value and compares equal. Blank canonicalises to NULL — "not captured" — never to "".
    /// </summary>
    [Fact]
    public void A_cr_number_is_canonicalised_and_blank_becomes_null_not_empty()
    {
        Assert.True(CommercialRegistrationNumbers.TryCanonicalize(
            " 1010-101-010 ", "CR", out var spaced, out _));
        Assert.Equal("1010101010", spaced);

        Assert.True(CommercialRegistrationNumbers.TryCanonicalize("   ", "CR", out var blank, out _));
        Assert.Null(blank);
    }

    // ── VAT registration ─────────────────────────────────────────────────────

    /// <summary>
    /// THE reuse requirement, pinned. The customer's VAT number is validated by the SAME type that
    /// already governs the supplier and business-unit columns. If somebody writes a second KSA rule
    /// for customers, this test is what notices: it drives the customer write path and asserts the
    /// message the shared validator produces.
    /// </summary>
    [Fact]
    public async Task The_customer_vat_number_is_validated_by_the_same_definition_as_the_supplier_one()
    {
        // Synthetic: 15 digits, starts with 3, ends with 3 — the KSA shape, identifying nobody.
        const string wellFormed = "300000000000003";
        // Synthetic: same length and prefix, wrong trailing marker.
        const string malformed = "300000000000001";

        Assert.True(TaxRegistrationNumbers.TryCanonicalize(wellFormed, "VAT registration number", out _, out _));
        Assert.False(TaxRegistrationNumbers.TryCanonicalize(malformed, "VAT registration number", out _, out var sharedError));

        using var db = new TestDb();
        await using var context = db.ContextFor(Bu);
        Seed.EnsureBusinessUnit(context, Bu);
        await context.SaveChangesAsync();

        var repository = new CustomerRepository(context);
        var failure = await Assert.ThrowsAsync<ArgumentException>(() => repository.AddAsync(
            new Customer { Name = "Synthetic Buyer", ImageUrl = "n/a", TaxRegistrationNumber = malformed },
            Bu, "tester"));

        Assert.Equal(sharedError, failure.Message);
    }

    // ── sector ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The sector list is closed, and "not stated" is NULL rather than a default. Defaulting an
    /// unclassified customer to PRIVATE would be an invented fact that decides whether government
    /// procurement rules apply.
    /// </summary>
    [Fact]
    public void The_sector_list_is_closed_and_an_unstated_sector_is_null_not_private()
    {
        Assert.True(CustomerSectors.TryCanonicalize("Semi Government", out var canonical, out _));
        Assert.Equal(CustomerSectors.SemiGovernment, canonical);

        Assert.True(CustomerSectors.TryCanonicalize(null, out var unstated, out _));
        Assert.Null(unstated);

        Assert.False(CustomerSectors.TryCanonicalize("Parastatal", out _, out var error));
        Assert.Contains("Government, Semi-Government or Private", error);
    }

    // ── region: the governed master, not a typed string ──────────────────────

    /// <summary>
    /// The region is a key into the tenant's OWN region master — the same <c>SetState</c> list
    /// <c>RoutingScopeKeys.Territory</c> resolves sales territory against. A region id belonging to
    /// another tenant is refused, which is the boundary no single-column foreign key can express.
    /// </summary>
    [Fact]
    public async Task A_region_from_another_tenants_master_is_refused()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        Seed.EnsureBusinessUnit(context, Bu);
        Seed.EnsureBusinessUnit(context, OtherBu);
        var country = new SetCountry
        {
            CountryId = 1, CountryCode = "SA", CountryName = "Saudi Arabia",
            Buid = OtherBu, IsActive = true, CreatedBy = "seed", CreatedDate = DateTime.UtcNow
        };
        context.SetCountries.Add(country);
        context.SetStates.Add(new SetState
        {
            StateId = 4_100, StateCode = "EP", StateName = "Eastern Province",
            CountryId = 1, Buid = OtherBu, IsActive = true,
            CreatedBy = "seed", CreatedDate = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var repository = new CustomerRepository(context);
        var failure = await Assert.ThrowsAsync<ArgumentException>(() => repository.AddAsync(
            new Customer { Name = "Synthetic Buyer", ImageUrl = "n/a", RegionStateId = 4_100 },
            Bu, "tester"));

        Assert.Contains("region master", failure.Message);
    }

    /// <summary>
    /// An account team from another tenant is refused for the same reason. The foreign key on
    /// <c>AccountTeamId</c> is single-column and cannot see the team's business unit, so this
    /// predicate is the only thing standing between a customer and another tenant's team.
    /// </summary>
    [Fact]
    public async Task An_account_team_from_another_tenant_is_refused()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        Seed.EnsureBusinessUnit(context, Bu);
        Seed.EnsureBusinessUnit(context, OtherBu);
        context.Teams.Add(new Team
        {
            Id = 5_100, TeamName = "Strategic Accounts", BusinessUnitId = OtherBu,
            CreatedBy = "seed", CreatedOn = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var repository = new CustomerRepository(context);
        var failure = await Assert.ThrowsAsync<ArgumentException>(() => repository.AddAsync(
            new Customer { Name = "Synthetic Buyer", ImageUrl = "n/a", AccountTeamId = 5_100 },
            Bu, "tester"));

        Assert.Contains("account team does not exist", failure.Message);
    }

    /// <summary>
    /// Zero is a value, not an absence. It would satisfy "not null", match no team for the rest of
    /// the record's life, and never be looked at again — wiring-contract failure #8.
    /// </summary>
    [Fact]
    public async Task A_zero_account_team_is_refused_rather_than_stored()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Bu);
        Seed.EnsureBusinessUnit(context, Bu);
        await context.SaveChangesAsync();

        var failure = await Assert.ThrowsAsync<ArgumentException>(() =>
            new CustomerRepository(context).AddAsync(
                new Customer { Name = "Synthetic Buyer", ImageUrl = "n/a", AccountTeamId = 0 },
                Bu, "tester"));

        Assert.Contains("not a valid team", failure.Message);
    }

    // ── the fields reach the database, and the audit trail ───────────────────

    /// <summary>
    /// The master fields survive a round trip through the real write path AND appear in the
    /// master-data change trail. The trail is derived from EF metadata rather than a hand-written
    /// include-list, which is exactly why a new column is audited the day it is added — this test
    /// is what would catch a regression to a hand-maintained list.
    /// </summary>
    [Fact]
    public async Task The_new_master_fields_are_persisted_and_audited()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Bu);
        Seed.EnsureBusinessUnit(context, Bu);
        context.Teams.Add(new Team
        {
            Id = 5_200, TeamName = "Strategic Accounts", BusinessUnitId = Bu,
            CreatedBy = "seed", CreatedOn = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var repository = new CustomerRepository(context);
        var customer = new Customer
        {
            Name = "Synthetic Buyer",
            ImageUrl = "n/a",
            CommercialRegistrationNumber = "1010101010",
            TaxRegistrationNumber = "300000000000003",
            Sector = "Government",
            AccountTeamId = 5_200
        };
        await repository.AddAsync(customer, Bu, "tester");

        var stored = await repository.GetByIdAsync(customer.Id, Bu, Everything);
        Assert.Equal("1010101010", stored.CommercialRegistrationNumber);
        Assert.Equal("300000000000003", stored.TaxRegistrationNumber);
        // Stored as the CODE, not the label the caller typed.
        Assert.Equal(CustomerSectors.Government, stored.Sector);
        Assert.Equal(5_200, stored.AccountTeamId);
        Assert.Equal("Strategic Accounts", stored.AccountTeam?.TeamName);

        // Change one of the new fields and confirm the audit interceptor recorded it by name.
        stored.Sector = CustomerSectors.Private;
        await repository.UpdateAsync(stored, Bu, "tester", stored.ConcurrencyToken);

        var audited = await context.Set<MasterDataFieldChange>().AsNoTracking()
            .Select(change => change.FieldName)
            .ToListAsync();
        Assert.Contains("Sector", audited);
    }
}
