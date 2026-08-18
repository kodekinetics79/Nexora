using ERP_RFQ_Automation.CommercialCases.Lifecycle;
using ERP_RFQ_Automation.Intelligence.Conversion;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// WP-B1: extraction warnings stop being decorative.
///
/// <para>The defect being closed: <c>ResolveLinesAsync</c> has always computed
/// <c>NeedsAttention</c>/<c>AttentionReason</c> — "Quantity missing", "No catalog match found",
/// a UoM like "25 Pack" that needs a human — and the conversion path never read them. The screen
/// coloured those lines red and left <b>Create RFQ enabled</b>. An operator could convert an
/// inquiry the system knew it had failed to read, and the resulting RFQ looked exactly like a
/// clean one.</para>
///
/// <para>The rule now: a hard integrity failure (missing quantity, missing unit) is refused
/// outright and cannot be acknowledged; a soft warning requires an explicit acknowledgement
/// carrying a reason, recorded on the existing lifecycle event.</para>
///
/// <para><b>Why these are PostgreSQL tests.</b> <c>ResolveLinesAsync</c> issues a product-match
/// query the SQLite provider cannot translate, so the resolver — and therefore this gate — is
/// only ever exercised against a real database. Asserting it on the SQLite lane would be
/// asserting against a path production never takes.</para>
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class ConversionWarningGovernancePostgreSqlTests
{
    private const long Tenant = 947_101;
    private const long CustomerId = 947_111;

    private readonly PostgreSqlTestDatabase _database;

    public ConversionWarningGovernancePostgreSqlTests(PostgreSqlTestDatabase database) => _database = database;

    private async Task SeedTenantAsync()
    {
        await using var owner = _database.ContextFor(null);
        if (await owner.BusinessUnits.AnyAsync(b => b.Id == Tenant)) return;
        var businessUnit = Seed.BusinessUnit(owner, Tenant);
        owner.SetupMasters.AddRange(LifecycleStatusCatalog.CreateFor(businessUnit, "tests"));
        Seed.Customer(owner, CustomerId, Tenant, "Saudi Electricity Company");
        await owner.SaveChangesAsync();
    }

    /// <summary>A lead that clears every PRE-EXISTING gate, so the only thing under test is the
    /// warning governance.</summary>
    private async Task<long> QualifiedLeadAsync(params LeadItem[] items)
    {
        await SeedTenantAsync();
        await using var owner = _database.ContextFor(null);
        var qualifiedId = await LifecycleStatusCatalog.ResolveIdAsync(owner, Tenant, "Lead", "QUALIFIED");
        var lead = new Lead
        {
            BuyersName = "SEC Bid Desk",
            RecDate = DateTime.UtcNow,
            BidClosingDate = DateTime.UtcNow.AddDays(14),
            LeadSource = "IntegrationTest",
            CreatedBy = "tests",
            CreatedDate = DateTime.UtcNow,
            BusinessUnitId = Tenant,
            LeadStatusId = qualifiedId,
            NoOfLineItems = items.Length
        };
        foreach (var item in items) lead.LeadItems.Add(item);
        owner.Leads.Add(lead);
        await owner.SaveChangesAsync();
        lead.ResolveCommercialIdentity(CustomerId, null, "CUSTOMER_CONFIRMED");
        await owner.SaveChangesAsync();
        return lead.Id;
    }

    private static LeadItem Line(string lineNo, int qty, string? uom, string partNo = "SEC-889120") => new()
    {
        LineItemNo = lineNo,
        ItemMaterialCode = partNo,
        ProductShortDescription = "Ball valve 2IN class 300",
        Quantity = qty,
        UnitOfMeasure = uom,
        Currency = "SAR"
    };

    /// <summary>A line as an Aramco materials list leaves it: coded, counted, measured, and
    /// silent about money. Conversion demands a currency the document never stated.</summary>
    private static LeadItem LineWithoutCurrency(string lineNo, int qty, string partNo) => new()
    {
        LineItemNo = lineNo,
        ItemMaterialCode = partNo,
        ProductShortDescription = "KEY:SHAFT,SQUARE,10 MM X 10 MM LG",
        Quantity = qty,
        UnitOfMeasure = "EA",
        Currency = null,
    };

    private async Task<long> ConvertAsync(long leadId, ConvertRequest request)
    {
        await using var tenantContext = _database.TenantContextWithRls(Tenant);
        return await new LeadConversionIntelligence(tenantContext).ConvertAsync(leadId, Tenant, request, default);
    }

    private async Task<int> RfqCountForAsync(long leadId)
    {
        await using var owner = _database.ContextFor(null);
        return await owner.Rfqs.AsNoTracking().CountAsync(r => r.LeadId == leadId);
    }

    // ------------------------------------------------------------------ hard integrity

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_missing_quantity_cannot_be_acknowledged_away()
    {
        // The whole point of the hard/soft split. Acknowledging "I don't know how many"
        // produces an RFQ that cannot be quoted, so ticking the box must not help.
        var leadId = await QualifiedLeadAsync(Line("00010", 0, "EA"));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => ConvertAsync(leadId, new ConvertRequest
        {
            ActingUser = "sara@nexora.sa",
            AcknowledgeAllWarnings = true,
            WarningAcknowledgementReason = "Buyer confirmed quantities verbally, proceeding"
        }));

        Assert.Equal(0, await RfqCountForAsync(leadId));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_missing_unit_of_measure_cannot_be_acknowledged_away()
    {
        var leadId = await QualifiedLeadAsync(Line("00010", 5, null));

        await Assert.ThrowsAnyAsync<Exception>(() => ConvertAsync(leadId, new ConvertRequest
        {
            ActingUser = "sara@nexora.sa",
            AcknowledgeAllWarnings = true,
            WarningAcknowledgementReason = "Assume each, standard for this buyer"
        }));

        Assert.Equal(0, await RfqCountForAsync(leadId));
    }

    // ------------------------------------------------------------------ soft warnings

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task An_unacknowledged_soft_warning_refuses_conversion_and_quotes_the_reason_back()
    {
        // No catalog row matches, so the line raises "No catalog match found" — soft.
        // Before this gate, this converted silently.
        var leadId = await QualifiedLeadAsync(Line("00010", 5, "EA", "UNMATCHED-A"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConvertAsync(leadId, new ConvertRequest { ActingUser = "sara@nexora.sa" }));

        Assert.Contains("00010", ex.Message);
        Assert.Contains("acknowledged", ex.Message, StringComparison.OrdinalIgnoreCase);
        // the operator is told WHAT to fix, not handed a generic validation error
        Assert.Contains("No catalog match", ex.Message);
        Assert.Equal(0, await RfqCountForAsync(leadId));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task An_acknowledgement_without_a_reason_is_refused()
    {
        var leadId = await QualifiedLeadAsync(Line("00010", 5, "EA", "UNMATCHED-B"));
        var lineId = await FirstLineIdAsync(leadId);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ConvertAsync(leadId, new ConvertRequest
        {
            ActingUser = "sara@nexora.sa",
            Items = { new ConvertRequestItem { LeadItemId = lineId, AcknowledgeWarning = true } }
        }));

        Assert.Contains("reason", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await RfqCountForAsync(leadId));
    }

    [Theory]
    [InlineData("ok")]
    [InlineData("n/a")]
    [InlineData("   ")]
    [Trait("Category", "PostgreSQL")]
    public async Task A_token_acknowledgement_reason_is_refused(string reason)
    {
        var leadId = await QualifiedLeadAsync(Line("00010", 5, "EA", "UNMATCHED-C"));
        var lineId = await FirstLineIdAsync(leadId);

        await Assert.ThrowsAsync<InvalidOperationException>(() => ConvertAsync(leadId, new ConvertRequest
        {
            ActingUser = "sara@nexora.sa",
            Items = { new ConvertRequestItem { LeadItemId = lineId, AcknowledgeWarning = true, AcknowledgementReason = reason } }
        }));

        Assert.Equal(0, await RfqCountForAsync(leadId));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task An_acknowledged_soft_warning_converts_and_is_recorded_on_the_lifecycle_event()
    {
        var leadId = await QualifiedLeadAsync(Line("00010", 5, "EA", "UNMATCHED-D"));
        var lineId = await FirstLineIdAsync(leadId);

        var rfqId = await ConvertAsync(leadId, new ConvertRequest
        {
            ActingUser = "sara@nexora.sa",
            Items =
            {
                new ConvertRequestItem
                {
                    LeadItemId = lineId,
                    AcknowledgeWarning = true,
                    AcknowledgementReason = "Part confirmed against the buyer's drawing pack by phone"
                }
            }
        });

        Assert.True(rfqId > 0);

        // The acknowledgement is durable, and it lives on the EXISTING lifecycle event rather
        // than in a new audit table — ReasonCode/ReasonNotes were previously always null.
        await using var owner = _database.ContextFor(null);
        var converted = await owner.CommercialLifecycleEvents.AsNoTracking()
            .SingleAsync(e => e.AggregateType == "Lead" && e.AggregateId == leadId
                              && e.NewStatusCode == "CONVERTED_TO_RFQ");
        Assert.Equal("CONVERTED_WITH_ACKNOWLEDGED_WARNINGS", converted.ReasonCode);
        Assert.Contains("00010", converted.ReasonNotes!);
        Assert.Contains("drawing pack", converted.ReasonNotes!);
        // WHAT was waived, not merely that something was
        Assert.Contains("No catalog match", converted.ReasonNotes!);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_batch_reason_covers_every_acknowledged_line()
    {
        // How operators actually work: tick the flagged lines, type one explanation.
        var leadId = await QualifiedLeadAsync(
            Line("00010", 5, "EA", "UNMATCHED-E"), Line("00020", 8, "EA", "UNMATCHED-F"));
        var lineIds = await LineIdsAsync(leadId);

        var rfqId = await ConvertAsync(leadId, new ConvertRequest
        {
            ActingUser = "sara@nexora.sa",
            AcknowledgeAllWarnings = true,
            WarningAcknowledgementReason = "Catalog not yet loaded for this brand; parts verified manually",
            Items =
            {
                new ConvertRequestItem { LeadItemId = lineIds[0], AcknowledgeWarning = true },
                new ConvertRequestItem { LeadItemId = lineIds[1], AcknowledgeWarning = true }
            }
        });

        await using var owner = _database.ContextFor(null);
        Assert.Equal(2, await owner.Rfqitems.AsNoTracking().CountAsync(i => i.Rfqid == rfqId));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Excluding_an_unreadable_line_lets_the_valid_lines_convert()
    {
        // A-9, now FIXED. FindConversionBlockers used to evaluate every line on the lead at its
        // persisted values, so one unreadable line made the whole RFQ unconvertible even when the
        // operator had deliberately left it out — a cliff on an 84-line bid list. It now evaluates
        // the lines actually being converted, with the caller's corrections applied.
        //
        // This test previously pinned the DEFECT ("does NOT bypass"). It pins the repair instead:
        // excluding is a legitimate resolution, and nothing about the remaining lines is relaxed.
        var leadId = await QualifiedLeadAsync(
            Line("00010", 5, "EA", "UNMATCHED-G"), Line("00020", 0, "EA", "UNMATCHED-H"));
        var lineIds = await LineIdsAsync(leadId);

        var rfqId = await ConvertAsync(leadId, new ConvertRequest
        {
            ActingUser = "sara@nexora.sa",
            AcknowledgeAllWarnings = true,
            WarningAcknowledgementReason = "Catalog not yet loaded for this brand; verified manually",
            Items =
            {
                new ConvertRequestItem { LeadItemId = lineIds[0], AcknowledgeWarning = true },
                new ConvertRequestItem { LeadItemId = lineIds[1], Include = false } // the zero-qty line
            }
        });

        await using var owner = _database.ContextFor(null);
        // Only the included line travelled; the excluded one is still on the lead, untouched.
        Assert.Equal(1, await owner.Rfqitems.AsNoTracking().CountAsync(i => i.Rfqid == rfqId));
        Assert.Equal(2, await owner.LeadItems.AsNoTracking().CountAsync(i => i.LeadId == leadId));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Correcting_a_zero_quantity_in_the_request_satisfies_the_hard_gate()
    {
        // The other half of A-9. The Review & Create RFQ screen offers a quantity box; the gate
        // used to read the PERSISTED value, so the correction it demanded was the one thing it
        // ignored and the operator could never proceed. A supplied correction now satisfies it.
        var leadId = await QualifiedLeadAsync(Line("00010", 0, "EA", "UNMATCHED-Q"));
        var lineId = await FirstLineIdAsync(leadId);

        var rfqId = await ConvertAsync(leadId, new ConvertRequest
        {
            ActingUser = "sara@nexora.sa",
            Items = { new ConvertRequestItem { LeadItemId = lineId, Include = true, Quantity = 25 } }
        });

        await using var owner = _database.ContextFor(null);
        var line = await owner.Rfqitems.AsNoTracking().SingleAsync(i => i.Rfqid == rfqId);
        Assert.Equal(25, line.Quantity);   // the corrected value is what was written
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Excluding_a_softly_flagged_line_needs_no_acknowledgement_for_that_line()
    {
        // The exclusion case that DOES work: a line carrying only a soft warning is dropped,
        // and the gate must not demand an acknowledgement for a line nobody is converting.
        var leadId = await QualifiedLeadAsync(
            Line("00010", 5, "EA", "UNMATCHED-K"), Line("00020", 7, "EA", "UNMATCHED-L"));
        var lineIds = await LineIdsAsync(leadId);

        var rfqId = await ConvertAsync(leadId, new ConvertRequest
        {
            ActingUser = "sara@nexora.sa",
            Items =
            {
                new ConvertRequestItem
                {
                    LeadItemId = lineIds[0],
                    AcknowledgeWarning = true,
                    AcknowledgementReason = "Verified against the buyer's drawing pack"
                },
                new ConvertRequestItem { LeadItemId = lineIds[1], Include = false }
            }
        });

        await using var owner = _database.ContextFor(null);
        Assert.Equal(1, await owner.Rfqitems.AsNoTracking().CountAsync(i => i.Rfqid == rfqId));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Refusal_happens_before_any_write_so_no_partial_rfq_survives()
    {
        var leadId = await QualifiedLeadAsync(
            Line("00010", 5, "EA", "UNMATCHED-I"), Line("00020", 0, "EA", "UNMATCHED-J"));

        await Assert.ThrowsAnyAsync<Exception>(
            () => ConvertAsync(leadId, new ConvertRequest { ActingUser = "sara@nexora.sa" }));

        await using var owner = _database.ContextFor(null);
        Assert.Equal(0, await RfqCountForAsync(leadId));
        Assert.Empty(await owner.CommercialLifecycleEvents.AsNoTracking()
            .Where(e => e.AggregateType == "Lead" && e.AggregateId == leadId
                        && e.NewStatusCode == "CONVERTED_TO_RFQ").ToListAsync());
    }

    // ------------------------------------------------------------------ helpers

    private async Task<long> FirstLineIdAsync(long leadId) => (await LineIdsAsync(leadId))[0];

    private async Task<IReadOnlyList<long>> LineIdsAsync(long leadId)
    {
        await using var owner = _database.ContextFor(null);
        return await owner.LeadItems.AsNoTracking()
            .Where(i => i.LeadId == leadId).OrderBy(i => i.Id).Select(i => i.Id).ToListAsync();
    }

    // ------------------------------------------------------------------ currency

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_bid_that_states_no_currency_can_be_converted_by_stating_one()
    {
        // THE SECOND DEAD END, found on lead 466 in production.
        //
        // Conversion demands a currency on every line. An Aramco materials list states
        // quantities and units and says nothing about money, so the lines carry none. The only
        // writer of a line's currency was the extraction-review path — which closes the moment
        // the figures are approved. Approving is also what makes a lead QUALIFIED, and only a
        // QUALIFIED lead may convert.
        //
        // Approve (mandatory) -> currency unreachable -> conversion refuses for ever. Lead 466
        // sat approved, qualified, and permanently unconvertible with 21 such lines.
        //
        // Quantity and unit always honoured a convert-time correction; currency read the stored
        // line only. Closing that asymmetry is the fix.
        var leadId = await QualifiedLeadAsync(LineWithoutCurrency("10", 176, "902017274"));

        long itemId;
        await using (var read = _database.ContextFor(null))
            itemId = await read.LeadItems.AsNoTracking()
                .Where(x => x.LeadId == leadId).Select(x => x.Id).SingleAsync();

        // The currency refusal comes FIRST, before the soft warning gate, so the operator is
        // told the one thing that actually stops the conversion.
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConvertAsync(leadId, new ConvertRequest { ActingUser = "sara@nexora.sa" }));
        Assert.Contains("currency", refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await RfqCountForAsync(leadId));

        // Stating it on the conversion is enough: no second approval, no reopened review.
        // The line still carries a soft "no catalog match" warning, acknowledged as any
        // operator would have to — this fix does not weaken that gate.
        var rfqId = await ConvertAsync(leadId, new ConvertRequest
        {
            ActingUser = "sara@nexora.sa",
            Currency = "usd",
            WarningAcknowledgementReason = "Checked against the source bid list.",
            Items = new() { new ConvertRequestItem { LeadItemId = itemId, AcknowledgeWarning = true } },
        });

        await using var owner = _database.ContextFor(null);
        var line = await owner.Rfqitems.AsNoTracking().SingleAsync(x => x.Rfqid == rfqId);
        // Stored in the three-letter form the review path validates, not as it was typed.
        Assert.Equal("USD", line.Currency);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_per_line_currency_beats_the_conversion_wide_one()
    {
        // Mixed-currency inquiries are real, so the per-line value has to win — the same
        // precedence quantity and unit of measure already follow.
        var leadId = await QualifiedLeadAsync(LineWithoutCurrency("10", 5, "902017276"));

        await using (var read = _database.ContextFor(null))
        {
            var itemId = await read.LeadItems.AsNoTracking()
                .Where(x => x.LeadId == leadId).Select(x => x.Id).SingleAsync();

            var rfqId = await ConvertAsync(leadId, new ConvertRequest
            {
                ActingUser = "sara@nexora.sa",
                Currency = "USD",
                WarningAcknowledgementReason = "Checked against the source bid list.",
                Items = new()
                {
                    new ConvertRequestItem
                    {
                        LeadItemId = itemId, Currency = "SAR", AcknowledgeWarning = true,
                    },
                },
            });

            await using var owner = _database.ContextFor(null);
            var line = await owner.Rfqitems.AsNoTracking().SingleAsync(x => x.Rfqid == rfqId);
            Assert.Equal("SAR", line.Currency);
        }
    }
}
