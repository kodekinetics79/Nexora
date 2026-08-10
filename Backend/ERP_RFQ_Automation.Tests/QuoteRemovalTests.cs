using ERP_RFQ_Automation.Intelligence.Pricing;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Quote deletion — the ZATCA field-audit finding.
///
/// <para><c>QuoteRepository.DeleteAsync</c> removed a <c>Quote</c> with no status guard, no reason,
/// no audit event and no tombstone, reachable from <c>DELETE /api/Quote/{id}</c> with nothing but
/// the Quotations Delete permission. The row was not the serious part: the FKs cascaded, so the
/// same click destroyed the R5 <c>QuotePriceAttestations</c> and the R7
/// <c>QuoteValidityExtensions</c> — the evidence for two ratified controls. A control whose
/// evidence is deleted by the same button that deletes the record is not a control.</para>
///
/// <para>The edit path was already guarded at <c>QuoteService.UpdateQuoteAsync</c> (FIN-05).
/// These tests hold deletion to the same standard, at the repository layer the controller calls.</para>
/// </summary>
public sealed class QuoteRemovalTests
{
    private const long Tenant = 97_001;
    private const long OtherTenant = 97_002;
    private const long DraftStatusId = 97_010;
    private const long SentStatusId = 97_011;
    private const long QuoteId = 97_100;

    // ============================================================================
    // Past DRAFT: withdrawn, not destroyed
    // ============================================================================

    /// <summary>
    /// THE TEST THAT MATTERS. A quote the customer already holds is withdrawn with a reason; the
    /// row survives, the R5 attestation survives, the R7 extension survives, and the removal is on
    /// the record.
    /// </summary>
    [Fact]
    public async Task An_issued_quote_is_withdrawn_with_its_evidence_intact_not_deleted()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        Seed(context, SentStatusId);
        SeedAttestation(context);
        SeedValidityExtension(context);
        var repository = new QuoteRepository(context);

        var outcome = await repository.RemoveAsync(
            QuoteId, Tenant, "Customer cancelled the tender before award.", "rep@nexora.invalid");

        Assert.NotNull(outcome);
        Assert.Equal(QuoteRemovalModes.Withdrawn, outcome!.Mode);
        Assert.False(outcome.WasDeleted);

        context.ChangeTracker.Clear();
        var quote = await context.Quotes.AsNoTracking().SingleAsync(q => q.Id == QuoteId);
        Assert.NotNull(quote.RemovedOn);
        Assert.Equal("rep@nexora.invalid", quote.RemovedBy);
        Assert.Equal("Customer cancelled the tender before award.", quote.RemovalReason);

        // The evidence for R5 and R7 is still there. Before the cascade was broken, both of these
        // were zero after a delete.
        Assert.Single(await context.QuotePriceAttestations.AsNoTracking().ToListAsync());
        Assert.Single(await context.QuoteValidityExtensions.AsNoTracking().ToListAsync());
    }

    /// <summary>Every removal leaves a tombstone that says what went, who, when and why.</summary>
    [Fact]
    public async Task Every_removal_writes_a_tombstone_carrying_the_reason_and_the_actor()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        Seed(context, SentStatusId);
        SeedAttestation(context);
        var repository = new QuoteRepository(context);

        await repository.RemoveAsync(QuoteId, Tenant, "Priced in error.", "rep@nexora.invalid");
        context.ChangeTracker.Clear();

        var record = await context.QuoteRemovalRecords.AsNoTracking().SingleAsync();
        Assert.Equal(QuoteId, record.QuoteId);
        Assert.Equal("Q-REMOVE-1", record.QuoteNo);
        Assert.Equal(QuoteRemovalModes.Withdrawn, record.Mode);
        Assert.Equal("Priced in error.", record.Reason);
        Assert.Equal("rep@nexora.invalid", record.RemovedBy);
        Assert.Equal(SentStatusId, record.StatusId);
        Assert.Equal("SENT", record.StatusCode);
        Assert.Equal(401m, record.TotalAmount);
        // The counts distinguish "no evidence existed" from "evidence went missing".
        Assert.Equal(1, record.PriceAttestationCount);
        Assert.Equal(0, record.ValidityExtensionCount);
    }

    /// <summary>A withdrawn quote leaves the working list and the pipeline statistics.</summary>
    [Fact]
    public async Task A_withdrawn_quote_leaves_the_list_and_the_pipeline_statistics()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        Seed(context, SentStatusId);
        var repository = new QuoteRepository(context);

        var (before, beforeCount) = await repository.GetAllAsync(Tenant, 1, 50);
        Assert.Equal(1, beforeCount);
        Assert.Single(before);

        await repository.RemoveAsync(QuoteId, Tenant, "Duplicate of Q-REMOVE-2.", "rep@nexora.invalid");
        context.ChangeTracker.Clear();

        var (after, afterCount) = await repository.GetAllAsync(Tenant, 1, 50);
        Assert.Equal(0, afterCount);
        Assert.Empty(after);

        var stats = await repository.GetQuoteStatsAsync(Tenant);
        Assert.Equal(0, stats.TotalQuotes);

        // ...but it is still in the database, which is the entire point.
        Assert.NotNull(await context.Quotes.AsNoTracking().SingleOrDefaultAsync(q => q.Id == QuoteId));
    }

    /// <summary>A quote is withdrawn once. A second attempt is refused, not silently repeated.</summary>
    [Fact]
    public async Task A_second_removal_is_refused()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        Seed(context, SentStatusId);
        var repository = new QuoteRepository(context);

        await repository.RemoveAsync(QuoteId, Tenant, "Withdrawn at customer request.", "rep@nexora.invalid");
        context.ChangeTracker.Clear();

        var refusal = await Assert.ThrowsAsync<QuoteRemovalRefusedException>(() =>
            repository.RemoveAsync(QuoteId, Tenant, "Again.", "rep@nexora.invalid"));
        Assert.Contains("already removed", refusal.Message);
        Assert.Single(await context.QuoteRemovalRecords.AsNoTracking().ToListAsync());
    }

    // ============================================================================
    // The reason is not optional
    // ============================================================================

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_removal_without_a_reason_is_refused_and_changes_nothing(string reason)
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        Seed(context, SentStatusId);
        var repository = new QuoteRepository(context);

        await Assert.ThrowsAsync<QuoteRemovalRefusedException>(() =>
            repository.RemoveAsync(QuoteId, Tenant, reason, "rep@nexora.invalid"));

        context.ChangeTracker.Clear();
        Assert.Null((await context.Quotes.AsNoTracking().SingleAsync(q => q.Id == QuoteId)).RemovedOn);
        Assert.Empty(await context.QuoteRemovalRecords.AsNoTracking().ToListAsync());
    }

    // ============================================================================
    // DRAFT
    // ============================================================================

    /// <summary>
    /// A genuine scratch draft — never attested, never extended, no order — is still deletable, so
    /// the everyday "created it by mistake" case is not turned into paperwork. The tombstone is
    /// written anyway, and it survives the row because it carries no FK to Quotes.
    /// </summary>
    [Fact]
    public async Task A_clean_draft_is_discarded_but_the_tombstone_outlives_it()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        Seed(context, DraftStatusId);
        var repository = new QuoteRepository(context);

        var outcome = await repository.RemoveAsync(
            QuoteId, Tenant, "Created against the wrong RFQ.", "rep@nexora.invalid");

        Assert.Equal(QuoteRemovalModes.DraftDiscarded, outcome!.Mode);
        Assert.True(outcome.WasDeleted);

        context.ChangeTracker.Clear();
        Assert.Null(await context.Quotes.AsNoTracking().SingleOrDefaultAsync(q => q.Id == QuoteId));
        var record = await context.QuoteRemovalRecords.AsNoTracking().SingleAsync();
        Assert.Equal(QuoteId, record.QuoteId);
        Assert.Equal("Created against the wrong RFQ.", record.Reason);
    }

    /// <summary>
    /// "Draft" does not mean "nothing was ever confirmed". R5 attestation happens BEFORE the send,
    /// while the quote is still DRAFT — so a draft carrying an attestation is withdrawn, not
    /// discarded, and the attestation survives.
    /// </summary>
    [Fact]
    public async Task A_draft_that_was_already_price_attested_is_withdrawn_rather_than_discarded()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        Seed(context, DraftStatusId);
        SeedAttestation(context);
        var repository = new QuoteRepository(context);

        var outcome = await repository.RemoveAsync(
            QuoteId, Tenant, "Prices confirmed then the enquiry was pulled.", "rep@nexora.invalid");

        Assert.Equal(QuoteRemovalModes.Withdrawn, outcome!.Mode);
        context.ChangeTracker.Clear();
        Assert.NotNull(await context.Quotes.AsNoTracking().SingleOrDefaultAsync(q => q.Id == QuoteId));
        Assert.Single(await context.QuotePriceAttestations.AsNoTracking().ToListAsync());
    }

    /// <summary>Same for a draft whose validity was deliberately held open under R7.</summary>
    [Fact]
    public async Task A_draft_with_a_reasoned_validity_extension_is_withdrawn_rather_than_discarded()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        Seed(context, DraftStatusId);
        SeedValidityExtension(context);
        var repository = new QuoteRepository(context);

        var outcome = await repository.RemoveAsync(
            QuoteId, Tenant, "Held open then abandoned.", "rep@nexora.invalid");

        Assert.Equal(QuoteRemovalModes.Withdrawn, outcome!.Mode);
        context.ChangeTracker.Clear();
        Assert.Single(await context.QuoteValidityExtensions.AsNoTracking().ToListAsync());
    }

    // ============================================================================
    // Tenancy
    // ============================================================================

    /// <summary>Another tenant cannot remove this quote, and gets nothing back that says it exists.</summary>
    [Fact]
    public async Task A_removal_never_crosses_a_business_unit()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        Seed(context, SentStatusId);
        var repository = new QuoteRepository(context);

        var outcome = await repository.RemoveAsync(
            QuoteId, OtherTenant, "Not mine to remove.", "intruder@nexora.invalid");

        Assert.Null(outcome);
        context.ChangeTracker.Clear();
        Assert.Null((await context.Quotes.AsNoTracking().SingleAsync(q => q.Id == QuoteId)).RemovedOn);
        Assert.Empty(await context.QuoteRemovalRecords.AsNoTracking().IgnoreQueryFilters().ToListAsync());
    }

    // ============================================================================
    // Seeding
    // ============================================================================

    private static void Seed(ErpRfqAutomationContext context, long statusId)
    {
        var lead = Support.Seed.Lead(context, 97_301, Tenant);
        Support.Seed.Customer(context, 97_401, Tenant, "Gulf Industrial");
        Support.Seed.Contact(context, 97_501, Tenant, 97_401);
        context.SaveChanges();
        lead.ResolveCommercialIdentity(97_401, 97_501, "CONFIRMED");

        context.SetupMasters.Add(new SetupMaster
        {
            SetupId = DraftStatusId, BusinessUnitId = Tenant, SetupType = "QuoteStatus",
            SetupCode = "DRAFT", SetupValue = "Draft", CreatedBy = "tests", CreatedOn = DateTime.UtcNow
        });
        context.SetupMasters.Add(new SetupMaster
        {
            SetupId = SentStatusId, BusinessUnitId = Tenant, SetupType = "QuoteStatus",
            SetupCode = "SENT", SetupValue = "Sent", CreatedBy = "tests", CreatedOn = DateTime.UtcNow
        });

        var rfq = new Rfq
        {
            Id = 97_601, Rfqno = "RFQ-97601", RecDate = DateTime.UtcNow, BusinessUnitId = Tenant,
            LeadId = lead.Id, CreatedBy = "tests", CreatedDate = DateTime.UtcNow
        };
        rfq.InheritCommercialIdentity(lead);
        context.Rfqs.Add(rfq);
        context.SaveChanges();

        var quote = new Quote
        {
            Id = QuoteId, QuoteNo = "Q-REMOVE-1", BusinessUnitId = Tenant, Rfqid = rfq.Id,
            StatusId = statusId,
            QuoteDate = DateTime.UtcNow, ValidUntil = DateTime.UtcNow.AddDays(30),
            TotalAmount = 401m, CreatedBy = "tests", CreatedDate = DateTime.UtcNow,
            QuoteItems =
            {
                new QuoteItem
                {
                    Id = 97_101, ItemDescription = "Ball valve, 2in",
                    Quantity = 2m, UnitPrice = 120.500000m, TotalAmount = 241m,
                    CreatedBy = "tests", CreatedDate = DateTime.UtcNow
                }
            }
        };
        quote.InheritCommercialIdentity(rfq);
        context.Quotes.Add(quote);
        context.SaveChanges();
        context.ChangeTracker.Clear();
    }

    private static void SeedAttestation(ErpRfqAutomationContext context)
    {
        context.QuotePriceAttestations.Add(new QuotePriceAttestation
        {
            BusinessUnitId = Tenant,
            QuoteId = QuoteId,
            Source = PriceAttestationSources.SalesManager,
            SourceReference = "Sales manager on duty",
            LineFingerprint = new string('a', 64),
            LineCount = 1,
            ConfirmedBy = "manager@nexora.invalid",
            ConfirmedOn = DateTime.UtcNow
        });
        context.SaveChanges();
        context.ChangeTracker.Clear();
    }

    private static void SeedValidityExtension(ErpRfqAutomationContext context)
    {
        context.QuoteValidityExtensions.Add(new QuoteValidityExtension
        {
            BusinessUnitId = Tenant,
            QuoteId = QuoteId,
            PreviousValidUntil = DateTime.UtcNow.AddDays(30),
            NewValidUntil = DateTime.UtcNow.AddDays(60),
            Reason = "Buyer asked us to hold the tender price to the board date.",
            ExtendedBy = "rep@nexora.invalid",
            ExtendedOn = DateTime.UtcNow,
            IdempotencyKey = "quote-removal-tests-extension-1"
        });
        context.SaveChanges();
        context.ChangeTracker.Clear();
    }
}
