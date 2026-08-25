using System.Security.Cryptography;
using System.Text;
using ERP_RFQ_Automation.CommercialCases.Lifecycle;
using ERP_RFQ_Automation.CommercialCases.Participation;
using ERP_RFQ_Automation.CommercialCases.Promotion;
using ERP_RFQ_Automation.CommercialFinance;
using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.CustomerResolution;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.DTOs.Lead;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.Intelligence.Decision;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Reporting;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Sla;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// THE CLIENT-RESOLUTION SEAM: an enquiry arrives, a human names the client organisation it
/// came from, and that answer has to survive all the way onto an RFQ — because a lead cannot
/// be qualified or converted without a customer, so an enquiry with no client reaches no quote
/// by any route.
///
/// <para><b>Why this file exists.</b> Every gate suite that needs a lead with a customer stamps
/// one on by hand — <c>UpstreamSpine.EstablishLeadAsync</c> calls
/// <c>lead.ResolveCommercialIdentity(CustomerId, null, "CUSTOMER_CONFIRMED")</c> directly, and
/// <c>GoldenCommercialJourneySeeder</c> and <c>QuoteBackfillSpine</c> do the same. Every one of
/// them therefore proves something about what happens AFTER a lead has a client, and nothing at
/// all about whether a human can give it one. That is exactly the join that was cut: the only
/// human door onto <c>Lead.ResolveCommercialIdentity</c> was the extraction-review submit, whose
/// first gate refuses any lead whose extraction already succeeded. On the ordinary happy path
/// the door was shut before anyone saw the lead, and no test noticed because no test used it.</para>
///
/// <para>These tests go through the product's doors on both sides. The lead is established by
/// <c>LeadIdentityApplicationService.ReconcileAsync</c> — the same call the extraction worker
/// makes — with NO customer, exactly as ingestion leaves it. The client is attached by the
/// repository command a controller calls. Nothing in between is hand-stamped.</para>
///
/// <para><b>What this file deliberately does not cover</b>: HTTP, authorization and RLS (the
/// authenticated-HTTP and PostgreSQL lanes own those); the machine resolver's own matching
/// tiers (<c>CustomerIdentityResolverTests</c> owns those as pure unit tests); and the alias
/// learner's poisoning safeguards (<c>CustomerAliasLearnerTests</c>). What is asserted here is
/// only the carriage between those parts.</para>
/// </summary>
public sealed class LeadClientLinkSeamTests
{
    // ==========================================================================================
    // THE DEFECT, in executable form
    // ==========================================================================================

    /// <summary>
    /// The gate that stranded 31 live enquiries, pinned so it cannot be quietly relaxed.
    ///
    /// <para>A document that extracted cleanly leaves <c>ParseStatus = "Success"</c>
    /// (<c>ExtractionWorker.cs:1452</c>) and is never offered for review. Asking the review
    /// submit to attach a client to such a lead comes back
    /// <c>"This lead is no longer awaiting extraction review."</c> — and until
    /// <c>LinkClientAsync</c> existed that was the ONLY human path to a customer, so the answer
    /// was final.</para>
    ///
    /// <para>The gate is RIGHT and stays: the method it guards rewrites the whole line-item set
    /// from a client-held snapshot, and letting a stale caller in would silently discard another
    /// reviewer's edits. This test exists so that a future reader who finds the same symptom
    /// fixes it the way it was fixed here — a second door — instead of widening this one.</para>
    /// </summary>
    [Fact]
    public async Task Extraction_review_still_refuses_a_lead_whose_extraction_already_succeeded()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.Lead(seed, 700, Tenant, parseStatus: "Success",
                items: new[] { Seed.LeadItem(1, "00010", 5) });
            Seed.Customer(seed, 7_900, Tenant, "Fulton County Government");
            seed.SaveChanges();
        }

        using var context = db.ContextFor(Tenant);
        var repository = new LeadRepository(context);

        var refusal = await Assert.ThrowsAsync<LeadReviewConflictException>(() =>
            repository.SubmitLeadReviewAsync(700, Tenant, new LeadReviewSubmitDTO
            {
                ExpectedVersion = 1,
                Action = "save",
                Header = new LeadReviewHeaderDTO { CustomerId = 7_900 },
                Items = new() { new LeadItemReviewDTO { Id = 1, LineItemNo = "00010", Quantity = 5 } }
            }));

        Assert.Contains("no longer awaiting extraction review", refusal.Message);

        // And the lead is exactly as it was: unresolved, and honest about it.
        using var verify = db.ContextFor(Tenant);
        var lead = await verify.Leads.SingleAsync(x => x.Id == 700);
        Assert.Null(lead.CustomerId);
        Assert.Equal(LeadCustomerMatchStatuses.Unresolved, lead.CustomerMatchStatus);
    }

    // ==========================================================================================
    // SEAM. Ingestion -> human names the client -> qualification -> RFQ
    // ==========================================================================================

    /// <summary>
    /// The whole point of the module, walked end to end: an enquiry that ingestion could not
    /// match to any client record is named by a person and then reaches an RFQ that carries that
    /// client.
    ///
    /// <para>The assertion that matters is the LAST one. The RFQ's customer is never written by
    /// this test — it arrives only because <c>LinkClientAsync</c> put it on the lead and
    /// <c>ConvertLeadToRfqAsync</c> carried it across. Cut either half and the RFQ has no
    /// customer, or conversion refuses outright.</para>
    /// </summary>
    [Fact]
    public async Task An_unresolved_enquiry_named_by_a_person_reaches_an_RFQ_carrying_that_client()
    {
        using var spine = new ClientLinkSpine();
        var leadId = await spine.IngestUnresolvedLeadAsync("CRM-SEAM-001", "bids@fultoncountyga.gov");

        // Ingestion's honest starting state: no client, and a status that says so. The DB CHECK
        // CK_Leads_CustomerIdentityStatus makes the pair inseparable.
        await using (var read = spine.Context())
        {
            var ingested = await read.Leads.SingleAsync(x => x.Id == leadId);
            Assert.Null(ingested.CustomerId);
            Assert.Equal(LeadCustomerMatchStatuses.Unresolved, ingested.CustomerMatchStatus);
        }

        // A rep opens the enquiry and names the buyer. This is the door that did not exist.
        await using (var context = spine.Context())
        {
            var linked = await new LeadRepository(context).LinkClientAsync(
                leadId, Tenant,
                new LeadClientLinkRequestDTO { CustomerId = ClientLinkSpine.CustomerId, Reason = "Named from the bid header." },
                "rep@tenant.test");

            Assert.NotNull(linked);
            Assert.Equal(ClientLinkSpine.CustomerId, linked!.CustomerId);
        }

        // Approving the extracted figures is a SEPARATE decision and still has to happen;
        // linking a client must never have quietly granted it.
        await using (var context = spine.Context())
        {
            var lead = await context.Leads.SingleAsync(x => x.Id == leadId);
            Assert.False(lead.CommercialFactsVerified);
            lead.CommercialFactsVerified = true;
            await context.SaveChangesAsync();
        }

        await spine.QualifyAsync(leadId);
        var (rfqId, _) = await spine.ConvertAsync(leadId);

        await using var verify = spine.Context();
        var rfq = await verify.Rfqs.SingleAsync(x => x.Id == rfqId);
        Assert.Equal(ClientLinkSpine.CustomerId, rfq.CustomerId);
    }

    [Fact]
    public async Task Corrupt_source_evidence_fails_closed_without_an_RFQ_or_promotion_receipt()
    {
        using var spine = new ClientLinkSpine();
        var leadId = await spine.IngestUnresolvedLeadAsync("CRM-SEAM-CORRUPT", "bids@fultoncountyga.gov");
        await using (var context = spine.Context())
        {
            await new LeadRepository(context).LinkClientAsync(
                leadId, Tenant, new LeadClientLinkRequestDTO { CustomerId = ClientLinkSpine.CustomerId }, "rep@tenant.test");
            var lead = await context.Leads.SingleAsync(x => x.Id == leadId);
            lead.CommercialFactsVerified = true;
            await context.SaveChangesAsync();
        }
        await spine.QualifyAsync(leadId);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            spine.ConvertAsync(leadId, new CorruptEvidenceStorage()));
        Assert.Contains("digest", error.Message, StringComparison.OrdinalIgnoreCase);

        await using var verify = spine.Context();
        Assert.False(await verify.Rfqs.AnyAsync(x => x.LeadId == leadId));
        Assert.False(await verify.Set<RfqPromotion>().AnyAsync(x => x.LeadId == leadId));
        var leadAfter = await verify.Leads.Include(x => x.LeadStatus).SingleAsync(x => x.Id == leadId);
        Assert.Equal("QUALIFIED", leadAfter.LeadStatus!.SetupCode);
    }

    [Fact]
    public async Task Direct_API_cannot_commit_a_Bid_against_a_non_actionable_fit()
    {
        using var spine = new ClientLinkSpine();
        var leadId = await spine.IngestUnresolvedLeadAsync("CRM-SEAM-NOT-FIT", "bids@fultoncountyga.gov");
        await using (var context = spine.Context())
        {
            await new LeadRepository(context).LinkClientAsync(
                leadId, Tenant, new LeadClientLinkRequestDTO { CustomerId = ClientLinkSpine.CustomerId }, "rep@tenant.test");
            var lead = await context.Leads.SingleAsync(x => x.Id == leadId);
            lead.CommercialFactsVerified = true;
            await context.SaveChangesAsync();
        }
        await spine.QualifyAsync(leadId);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            spine.AttemptNonActionableBidCommitAsync(leadId));
        Assert.Contains("actionable", error.Message, StringComparison.OrdinalIgnoreCase);

        await using var verify = spine.Context();
        Assert.False(await verify.Set<LeadParticipationDecision>().AnyAsync(x => x.LeadId == leadId));
        Assert.False(await verify.Rfqs.AnyAsync(x => x.LeadId == leadId));
    }

    private sealed class CorruptEvidenceStorage : IEvidenceObjectStorage
    {
        public bool IsDurable => true;
        public Task ProbeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<EvidenceObject> WriteImmutableAsync(long businessUnitId, string zone, string sha256,
            string extension, ReadOnlyMemory<byte> content, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<Stream> OpenVerifiedReadAsync(string storageUri, string expectedSha256,
            CancellationToken ct = default) =>
            throw new InvalidDataException("Source evidence digest verification failed.");
    }

    // ==========================================================================================
    // SEAM. The human answer versus the machine resolver
    // ==========================================================================================

    /// <summary>
    /// A person's answer outranks the machine's, permanently.
    ///
    /// <para><c>LeadCustomerResolutionService</c> re-runs over every lead that is not
    /// human-decided — at ingestion, and again on every <c>POST /api/Lead/resolve-clients</c>.
    /// It decides what "human-decided" means by reading <c>CustomerMatchStatus</c>. If the link
    /// command wrote a machine-grade status, the next backfill would silently re-open a
    /// question a person had already answered.</para>
    /// </summary>
    [Fact]
    public async Task A_client_named_by_a_person_survives_a_full_machine_re_resolution()
    {
        using var spine = new ClientLinkSpine();
        var leadId = await spine.IngestUnresolvedLeadAsync("CRM-SEAM-002", "buyer@arcenesupply.example");

        // A DIFFERENT client is the one the person picks, so a machine that ignored the human
        // answer would have something else to land on and the assertion could not pass by luck.
        long decoyId;
        await using (var context = spine.Context())
        {
            decoyId = 7_931;
            Seed.Customer(context, decoyId, Tenant, "Arcene Supply Services LLP");
            context.Set<CustomerIdentifier>().Add(new CustomerIdentifier
            {
                BusinessUnitId = Tenant,
                CustomerId = decoyId,
                IdentifierType = CustomerIdentifierType.Domain,
                NormalizedValue = "arcenesupply.example",
                DisplayValue = "arcenesupply.example",
                IsVerified = true,
                Confidence = 0.95m,
                Source = "CustomerProfile",
                EffectiveFrom = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }

        await using (var context = spine.Context())
            await new LeadRepository(context).LinkClientAsync(
                leadId, Tenant, new LeadClientLinkRequestDTO { CustomerId = ClientLinkSpine.CustomerId }, "rep@tenant.test");

        // The backfill a manager runs from the UI, over exactly this lead.
        await using (var context = spine.Context())
        {
            var outcome = await new LeadCustomerResolutionService(context)
                .ResolveAsync(Tenant, leadId, CancellationToken.None);
            Assert.Equal(CustomerMatchReasonCodes.HumanResolved, outcome.ReasonCode);
        }

        await using var verify = spine.Context();
        var lead = await verify.Leads.SingleAsync(x => x.Id == leadId);
        Assert.Equal(ClientLinkSpine.CustomerId, lead.CustomerId);
        Assert.NotEqual(decoyId, lead.CustomerId);
        Assert.True(LeadCustomerMatchStatuses.IsHumanDecided(lead.CustomerMatchStatus));
    }

    // ==========================================================================================
    // SEAM. The learning loop
    // ==========================================================================================

    /// <summary>
    /// Naming a client once teaches Nexora to recognise the NEXT enquiry from that client by
    /// itself. This is the only mechanism by which the unresolved pile shrinks instead of
    /// growing, and it is a seam between two modules that never call each other: the CRM link
    /// writes identifiers, the resolver reads them.
    ///
    /// <para>Neither side is hand-built. No <c>CustomerIdentifier</c> row is seeded — the only
    /// one that can exist is the one the link command taught — and the second lead is resolved
    /// by the real resolver, not by an assertion about what it should have found.</para>
    /// </summary>
    [Fact]
    public async Task Naming_a_client_once_lets_the_machine_resolve_the_next_enquiry_by_itself()
    {
        using var spine = new ClientLinkSpine();
        var taught = await spine.IngestUnresolvedLeadAsync("CRM-SEAM-003", "tenders@fultoncountyga.gov");

        // Before anything is taught the tenant knows nothing about this domain.
        await using (var read = spine.Context())
            Assert.Empty(await read.Set<CustomerIdentifier>().IgnoreQueryFilters()
                .Where(i => i.BusinessUnitId == Tenant).ToListAsync());

        await using (var context = spine.Context())
            await new LeadRepository(context, aliasLearner: new CustomerAliasLearner(context))
                .LinkClientAsync(taught, Tenant,
                    new LeadClientLinkRequestDTO { CustomerId = ClientLinkSpine.CustomerId }, "rep@tenant.test");

        // A second enquiry from the same buyer, arriving later. Nobody touches it.
        var second = await spine.IngestUnresolvedLeadAsync("CRM-SEAM-004", "procurement@fultoncountyga.gov");

        await using (var context = spine.Context())
        {
            var outcome = await new LeadCustomerResolutionService(context)
                .ResolveAsync(Tenant, second, CancellationToken.None);
            Assert.Equal(CustomerMatchReasonCodes.SenderDomain, outcome.ReasonCode);
        }

        await using var verify = spine.Context();
        var resolved = await verify.Leads.SingleAsync(x => x.Id == second);
        Assert.Equal(ClientLinkSpine.CustomerId, resolved.CustomerId);

        // Machine-grade, not human-grade: the machine matched it, and the audit trail must not
        // claim a person did.
        Assert.False(LeadCustomerMatchStatuses.IsHumanDecided(resolved.CustomerMatchStatus));
    }

    // ==========================================================================================
    // Guards
    // ==========================================================================================

    /// <summary>
    /// Once an RFQ has inherited a lead's client, the lead may not be moved underneath it.
    ///
    /// <para><c>Rfq.InheritCommercialIdentity</c> refuses a lead whose customer differs from the
    /// one the RFQ already carries, and every downstream document is addressed from the RFQ. Two
    /// records disagreeing about who the client is, with no way back, is worse than a refusal
    /// that says so.</para>
    /// </summary>
    [Fact]
    public async Task A_lead_that_has_become_an_RFQ_will_not_have_its_client_swapped()
    {
        using var spine = new ClientLinkSpine();
        var leadId = await spine.IngestUnresolvedLeadAsync("CRM-SEAM-005", "bids@fultoncountyga.gov");

        await using (var context = spine.Context())
            await new LeadRepository(context).LinkClientAsync(
                leadId, Tenant, new LeadClientLinkRequestDTO { CustomerId = ClientLinkSpine.CustomerId }, "rep@tenant.test");

        await using (var context = spine.Context())
        {
            var lead = await context.Leads.SingleAsync(x => x.Id == leadId);
            lead.CommercialFactsVerified = true;
            await context.SaveChangesAsync();
        }
        await spine.QualifyAsync(leadId);
        await spine.ConvertAsync(leadId);

        long otherClient;
        await using (var context = spine.Context())
        {
            otherClient = 7_941;
            Seed.Customer(context, otherClient, Tenant, "Somebody Else Entirely");
            await context.SaveChangesAsync();
        }

        await using (var context = spine.Context())
        {
            var refusal = await Assert.ThrowsAsync<LeadReviewConflictException>(() =>
                new LeadRepository(context).LinkClientAsync(
                    leadId, Tenant, new LeadClientLinkRequestDTO { CustomerId = otherClient }, "rep@tenant.test"));
            Assert.Contains("already been converted", refusal.Message);
        }

        await using var verify = spine.Context();
        var lead2 = await verify.Leads.SingleAsync(x => x.Id == leadId);
        Assert.Equal(ClientLinkSpine.CustomerId, lead2.CustomerId);
    }

    /// <summary>
    /// A contact belonging to a different client is refused rather than silently dropped.
    /// A quote addressed to the right company and the wrong person is still a wrong quote.
    /// </summary>
    [Fact]
    public async Task A_contact_at_another_client_is_refused()
    {
        using var spine = new ClientLinkSpine();
        var leadId = await spine.IngestUnresolvedLeadAsync("CRM-SEAM-006", "bids@fultoncountyga.gov");

        await using (var context = spine.Context())
        {
            Seed.Customer(context, 7_951, Tenant, "Unrelated Client");
            Seed.Contact(context, 7_952, Tenant, 7_951, "someone@unrelated.example");
            await context.SaveChangesAsync();
        }

        await using (var context = spine.Context())
            await Assert.ThrowsAsync<LeadReviewValidationException>(() =>
                new LeadRepository(context).LinkClientAsync(leadId, Tenant,
                    new LeadClientLinkRequestDTO { CustomerId = ClientLinkSpine.CustomerId, ContactId = 7_952 },
                    "rep@tenant.test"));

        await using var verify = spine.Context();
        var lead = await verify.Leads.SingleAsync(x => x.Id == leadId);
        Assert.Null(lead.CustomerId);
    }

    /// <summary>
    /// A customer belonging to another tenant is not a customer. The lead stays unresolved,
    /// which is the honest outcome; RLS is a second line, not the only one.
    /// </summary>
    [Fact]
    public async Task A_customer_from_another_tenant_is_refused()
    {
        using var spine = new ClientLinkSpine();
        var leadId = await spine.IngestUnresolvedLeadAsync("CRM-SEAM-007", "bids@fultoncountyga.gov");

        const long otherTenant = 97_999;
        await using (var context = spine.RootContext())
        {
            Seed.EnsureBusinessUnit(context, otherTenant);
            Seed.Customer(context, 7_961, otherTenant, "Another Tenant's Client");
            await context.SaveChangesAsync();
        }

        await using (var context = spine.Context())
            await Assert.ThrowsAsync<LeadReviewValidationException>(() =>
                new LeadRepository(context).LinkClientAsync(leadId, Tenant,
                    new LeadClientLinkRequestDTO { CustomerId = 7_961 }, "rep@tenant.test"));

        await using var verify = spine.Context();
        var lead = await verify.Leads.SingleAsync(x => x.Id == leadId);
        Assert.Null(lead.CustomerId);
    }

    /// <summary>
    /// Every link leaves an immutable audit row naming who did it, and advances the lead's
    /// review version so a workbench holding a stale copy finds out.
    /// </summary>
    [Fact]
    public async Task Linking_a_client_is_recorded_against_the_person_who_did_it()
    {
        using var spine = new ClientLinkSpine();
        var leadId = await spine.IngestUnresolvedLeadAsync("CRM-SEAM-008", "bids@fultoncountyga.gov");

        long versionBefore;
        await using (var read = spine.Context())
            versionBefore = (await read.Leads.SingleAsync(x => x.Id == leadId)).ReviewVersion;

        await using (var context = spine.Context())
            await new LeadRepository(context).LinkClientAsync(leadId, Tenant,
                new LeadClientLinkRequestDTO { CustomerId = ClientLinkSpine.CustomerId, Reason = "Confirmed by phone." },
                "rep@tenant.test");

        await using var verify = spine.Context();
        var audit = await verify.Set<LeadReviewAudit>().SingleAsync(a => a.LeadId == leadId);
        Assert.Equal("link-client", audit.Action);
        Assert.Equal("rep@tenant.test", audit.ReviewedBy);
        Assert.Equal("Confirmed by phone.", audit.Reason);
        Assert.Equal(versionBefore, audit.FromVersion);

        var lead = await verify.Leads.SingleAsync(x => x.Id == leadId);
        Assert.Equal(versionBefore + 1, lead.ReviewVersion);
        Assert.Equal(lead.ReviewVersion, audit.ToVersion);
    }

    /// <summary>
    /// A caller that DOES hold a review version — the extraction workbench — still gets its
    /// optimistic-concurrency guarantee. The version is optional on the wire, not ignored.
    /// </summary>
    [Fact]
    public async Task A_supplied_review_version_is_enforced()
    {
        using var spine = new ClientLinkSpine();
        var leadId = await spine.IngestUnresolvedLeadAsync("CRM-SEAM-009", "bids@fultoncountyga.gov");

        await using var context = spine.Context();
        await Assert.ThrowsAsync<LeadReviewConflictException>(() =>
            new LeadRepository(context).LinkClientAsync(leadId, Tenant,
                new LeadClientLinkRequestDTO { CustomerId = ClientLinkSpine.CustomerId, ExpectedVersion = 999 },
                "rep@tenant.test"));
    }

    private const long Tenant = ClientLinkSpine.Tenant;
}

/// <summary>
/// A tenant plus the two real doors this file needs: the identity service the extraction worker
/// calls, and the governed lifecycle. Deliberately does NOT stamp a customer on anything — that
/// shortcut is what let the defect survive in every other fixture.
/// </summary>
internal sealed class ClientLinkSpine : IDisposable
{
    public const long Tenant = 97_501;
    public const long CustomerId = 97_510;

    private static DateTime Now => DateTime.UtcNow;
    private const int UomId = 7_942;
    private const long CurrencyId = 7_943;
    private static readonly byte[] EvidenceBytes = Encoding.UTF8.GetBytes(
        "CRM-SEAM|00010|Ball valve 2IN class 300|40|EA|SAR");
    private static readonly string EvidenceHash = Convert.ToHexString(SHA256.HashData(EvidenceBytes))
        .ToLowerInvariant();
    private const string EvidenceStorageUri = "memory://client-link-seam/inquiry.xlsx";
    private readonly TestDb _database = new();
    private int _sequence;

    public ClientLinkSpine()
    {
        using var seed = _database.ContextFor(null);
        var businessUnit = Seed.EnsureBusinessUnit(seed, Tenant);
        seed.SaveChanges();

        // The governed lifecycle picklist: without it a lead cannot be qualified and an RFQ
        // cannot be drafted, because both resolve their status through this catalog.
        seed.SetupMasters.AddRange(LifecycleStatusCatalog.CreateFor(businessUnit, "qa", Now));
        Seed.Customer(seed, CustomerId, Tenant, "Fulton County Government");
        seed.SetUoms.Add(new SetUom
        {
            UomId = UomId, BusinessUnitId = Tenant, UomCode = "EA", UomName = "Each",
            IsActive = true, CreatedBy = "qa", CreatedDate = Now
        });
        seed.Currencies.Add(new Currency
        {
            Id = CurrencyId, BusinessUnitId = Tenant, Code = "SAR", CurrencyName = "Saudi Riyal",
            ExchangeRate = 1m, IsBaseCurrency = true, IsActive = true, CreatedBy = "qa", CreatedOn = Now
        });
        seed.SaveChanges();
    }

    public ErpRfqAutomationContext Context() => _database.ContextFor(Tenant);

    /// <summary>An unfiltered context, for seeding a SECOND tenant's rows.</summary>
    public ErpRfqAutomationContext RootContext() => _database.ContextFor(null);

    /// <summary>
    /// Puts an enquiry into the tenant the way ingestion does — through
    /// <c>LeadIdentityApplicationService.ReconcileAsync</c>, the same call
    /// <c>ExtractionWorker</c> makes — and leaves it with NO customer, which is exactly the
    /// state the live tenant's 31 stranded enquiries are in. A directly-constructed Lead has no
    /// current revision and conversion rightly refuses it, so building one here would prove
    /// nothing about the product.
    /// </summary>
    public async Task<long> IngestUnresolvedLeadAsync(string reference, string senderEmail)
    {
        var ordinal = ++_sequence;
        var candidate = new Lead
        {
            Rfqno = reference,
            BuyersName = "County Bid Desk",
            Clientemail = senderEmail,
            RecDate = Now,
            BidClosingDate = Now.Date.AddDays(21),
            LeadSource = "ClientLinkSeamTests",
            CreatedBy = "qa",
            CreatedDate = Now,
            BusinessUnitId = Tenant,
            NoOfLineItems = 1,
            CustomerCompanyNameExtracted = "Fulton County Government"
        };
        candidate.LeadItems.Add(new LeadItem
        {
            LineItemNo = "00010",
            ItemMaterialCode = $"CRM-PART-{ordinal:0000}",
            ManufacturerPartNumber = $"CRM-PART-{ordinal:0000}",
            ProductShortDescription = "Ball valve 2IN class 300",
            Quantity = 40,
            UnitOfMeasure = "EA",
            Currency = "SAR",
            BidClosingDateLine = Now.Date.AddDays(21)
        });

        var key = $"crm:{Tenant}:{reference}";
        await using var context = Context();
        var result = await new LeadIdentityApplicationService(context).ReconcileAsync(candidate,
            new LeadIntakeDescriptor(
                BatchId: Guid.NewGuid(),
                SourceChannel: "ManualUpload", IdempotencyKey: key, ExternalSourceId: key,
                EmailThreadId: null, SourceSystem: "ClientLinkSeamTests", Sender: senderEmail,
                Subject: $"RFQ {reference}", OriginalFileName: $"{reference}.xlsx",
                MimeType: "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                FileSize: 20480, ContentHash: new string((char)('a' + (ordinal % 26)), 64),
                SourceDocumentId: null, ExtractionJobId: null, SourceReceivedAtUtc: Now,
                IngestedAtUtc: Now, ProcessingPath: LeadProcessingPath.Deterministic,
                ExternalAiUsed: false, ExternalCost: null, ActorType: "Service", ActorId: "qa",
                CorrelationId: key),
            CancellationToken.None);

        return result.LeadId;
    }

    /// <summary>
    /// Walks the real lifecycle to QUALIFIED. The policy graph refuses a shortcut, so every rung
    /// is climbed exactly as an operator would.
    /// </summary>
    public async Task QualifyAsync(long leadId)
    {
        foreach (var target in new[] { "PENDING_IDENTIFICATION", "ASSIGNED", "UNDER_REVIEW", "QUALIFIED" })
        {
            await using var context = Context();
            var current = await context.Leads.SingleAsync(x => x.Id == leadId);
            await new LifecycleApplicationService(context).TransitionLeadAsync(
                Tenant, leadId, new LifecycleActor("qa", "ClientLinkSeamTests"),
                new LifecycleTransitionCommand(target, current.LifecycleVersion, null, null,
                    "Seed", $"crm-{leadId}-{target}", $"lead-{leadId}",
                    $"crm-{target.ToLowerInvariant()}:{Tenant}:{leadId}"),
                false, CancellationToken.None);
        }
    }

    public async Task<(long RfqId, string Rfqno)> ConvertAsync(
        long leadId, IEvidenceObjectStorage? evidenceStorage = null)
    {
        await EnsurePromotionEvidenceAsync(leadId);
        await using var context = Context();
        var lead = await context.Leads.AsNoTracking().SingleAsync(x => x.Id == leadId);
        var lines = await context.Set<LeadItemRevision>().AsNoTracking()
            .Where(x => x.BusinessUnitId == Tenant && x.LeadRevisionId == lead.CurrentRevisionId)
            .OrderBy(x => x.LineNumber).ToListAsync();
        var participation = new LeadParticipationService(
            context, new LeadDecisionService(context, new GrossMarginService(context)),
            new LeadOutcomeReasons(context));
        var fit = await participation.RecordFitAssessmentAsync(Tenant, leadId,
            new RecordLeadFitAssessmentCommand(
                lead.CurrentRevisionId!.Value, lead.CurrentRevisionNumber, null, "FIT",
                "A human reviewer confirmed the client, eligibility, capability, delivery, compliance and commercials.",
                LeadParticipationService.GovernedFitCriterionCodes
                    .Select(code => new LeadFitCriterionCommand(code, "PASS", "Confirmed by the client-link seam reviewer."))
                    .ToArray(),
                $"client-link-fit:{Tenant}:{leadId}:{lead.CurrentRevisionId}", "qa"));
        var decision = await participation.CommitDecisionAsync(Tenant, leadId,
            new CommitLeadParticipationCommand(
                lead.CurrentRevisionId.Value, lead.CurrentRevisionNumber, null, true, fit.Id,
                lines.Select(line => new LeadLineParticipationCommand(
                    line.Id, LeadLineParticipationChoice.Bid,
                    ReasonNotes: "Reviewer confirmed the extracted line and normalized commercial values.",
                    Quantity: 40, UnitOfMeasure: "EA", Currency: "SAR")).ToArray(),
                $"client-link-participation:{Tenant}:{leadId}:{lead.CurrentRevisionId}", "qa"));
        var promoted = await new RfqPromotionService(
                context, evidenceStorage ?? new MemoryEvidenceStorage(EvidenceStorageUri, EvidenceHash, EvidenceBytes))
            .PromoteAsync(Tenant, leadId, new PromoteLeadToRfqCommand(
                lead.CurrentRevisionId.Value, lead.CurrentRevisionNumber, decision.Sequence, decision.Id,
                $"client-link-promotion:{Tenant}:{leadId}:{lead.CurrentRevisionId}", "qa"));
        return (promoted.RfqId, promoted.RfqNumber);
    }

    public async Task AttemptNonActionableBidCommitAsync(long leadId)
    {
        await EnsurePromotionEvidenceAsync(leadId);
        await using var context = Context();
        var lead = await context.Leads.AsNoTracking().SingleAsync(x => x.Id == leadId);
        var line = await context.Set<LeadItemRevision>().AsNoTracking()
            .SingleAsync(x => x.BusinessUnitId == Tenant && x.LeadRevisionId == lead.CurrentRevisionId);
        var participation = new LeadParticipationService(
            context, new LeadDecisionService(context, new GrossMarginService(context)),
            new LeadOutcomeReasons(context));
        var fit = await participation.RecordFitAssessmentAsync(Tenant, leadId,
            new RecordLeadFitAssessmentCommand(
                lead.CurrentRevisionId!.Value, lead.CurrentRevisionNumber, null, "NOT_FIT",
                "The human reviewer determined this request is not suitable for bidding.",
                LeadParticipationService.GovernedFitCriterionCodes
                    .Select(code => new LeadFitCriterionCommand(code, "PASS", "Reviewed."))
                    .ToArray(),
                $"client-link-not-fit:{Tenant}:{leadId}", "qa"));
        await participation.CommitDecisionAsync(Tenant, leadId,
            new CommitLeadParticipationCommand(
                lead.CurrentRevisionId.Value, lead.CurrentRevisionNumber, null, true, fit.Id,
                [new LeadLineParticipationCommand(line.Id, LeadLineParticipationChoice.Bid,
                    ReasonNotes: "Reviewer acknowledged the catalog warning.", Quantity: 40,
                    UnitOfMeasure: "EA", Currency: "SAR")],
                $"client-link-illegal-bid:{Tenant}:{leadId}", "qa"));
    }

    private async Task EnsurePromotionEvidenceAsync(long leadId)
    {
        await using var context = Context();
        var lead = await context.Leads.Include(x => x.LeadItems).SingleAsync(x => x.Id == leadId);
        var revision = await context.Set<LeadRevision>().AsNoTracking()
            .SingleAsync(x => x.BusinessUnitId == Tenant && x.Id == lead.CurrentRevisionId);
        if (await context.Set<LeadOccurrenceDocument>().AnyAsync(x =>
                x.BusinessUnitId == Tenant && x.OccurrenceId == revision.EstablishedByOccurrenceId))
            return;
        var occurrence = await context.Set<LeadIngestionOccurrence>().AsNoTracking()
            .SingleAsync(x => x.BusinessUnitId == Tenant && x.Id == revision.EstablishedByOccurrenceId);
        var corpus = DocumentCorpus.Create(Tenant, occurrence.BatchId, CorpusSourceType.ManualUpload);
        context.Add(corpus);
        await context.SaveChangesAsync();
        var job = new ExtractionJob
        {
            BatchId = occurrence.BatchId, BusinessUnitId = Tenant,
            SourceType = ExtractionSourceType.ManualUpload, ContentHash = EvidenceHash,
            StoragePath = EvidenceStorageUri, FileName = "inquiry.xlsx", FileType = "xlsx",
            Status = ExtractionStatus.Succeeded, Priority = 0, SchedulerTag = 0, Attempts = 1,
            MaxAttempts = 5, NextAttemptAt = Now, ResultLeadId = leadId, CreatedOn = Now, UpdatedOn = Now
        };
        context.Add(job);
        await context.SaveChangesAsync();
        var document = SourceDocument.Create(Tenant, corpus.Id, EvidenceHash, "inquiry.xlsx",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "memory", "client-link-seam/inquiry.xlsx", EvidenceHash, EvidenceBytes.Length);
        document.ReleaseFromQuarantine("memory", "client-link-seam/inquiry.xlsx", EvidenceHash);
        document.BindExtractionJob(job.Id);
        context.Add(document);
        await context.SaveChangesAsync();
        context.Add(new LeadOccurrenceDocument
        {
            BusinessUnitId = Tenant, OccurrenceId = revision.EstablishedByOccurrenceId,
            SourceDocumentId = document.Id, Role = "Primary", Ordinal = 1,
            LinkedAtUtc = DateTimeOffset.UtcNow
        });
        var runId = Guid.NewGuid();
        var run = ExtractionRun.Create(Tenant, document.Id, runId, job.Id, 1,
            "native-spreadsheet/client-link-test", "lead-evidence/v1");
        var page = DocumentPage.Create(Tenant, document.Id, 1, 100, 100);
        var inquiry = CanonicalInquiry.Create(Tenant, corpus.Id, 1);
        inquiry.PopulateHeader(lead.Rfqno, lead.BuyersName, lead.RecDate, lead.BidClosingDate);
        inquiry.BindLead(leadId);
        context.AddRange(run, page, inquiry);
        await context.SaveChangesAsync();
        var region = DocumentRegion.Create(Tenant, page.Id, DocumentRegionType.Table,
            0, 0, 100, 100, Encoding.UTF8.GetString(EvidenceBytes), 1m);
        context.Add(region);
        await context.SaveChangesAsync();
        var canonicalLines = lead.LeadItems.OrderBy(x => x.LineItemNo).Select((item, index) =>
        {
            var canonical = CanonicalLineItem.Create(Tenant, inquiry.Id, index + 1,
                item.ProductShortDescription ?? item.ItemText ?? item.ItemMaterialCode ?? "Requested line",
                item.Quantity, item.UnitOfMeasure);
            canonical.Enrich(null, item.ManufacturerPartNumber, item.Currency, null, null, "{}",
                CanonicalValidationStatus.Valid);
            canonical.BindLeadItem(item.Id);
            return (item, canonical);
        }).ToArray();
        context.AddRange(canonicalLines.Select(x => x.canonical));
        await context.SaveChangesAsync();
        foreach (var (item, canonical) in canonicalLines)
        {
            var evidence = FieldEvidence.ForLineItem(Tenant, region.Id, canonical.Id, "requestedLine",
                item.ProductShortDescription ?? item.ItemText,
                item.ManufacturerPartNumber ?? item.ItemMaterialCode, 1m, "client-link-test", runId,
                validationStatus: FieldValidationStatus.Valid);
            context.Add(evidence);
            context.Entry(evidence).Property("ExtractionRunId").CurrentValue = run.Id;
        }
        await context.SaveChangesAsync();
    }

    private sealed class MemoryEvidenceStorage(string storageUri, string expectedHash, byte[] content)
        : IEvidenceObjectStorage
    {
        public bool IsDurable => true;

        public Task ProbeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<EvidenceObject> WriteImmutableAsync(long businessUnitId, string zone, string sha256,
            string extension, ReadOnlyMemory<byte> bytes, CancellationToken ct = default) =>
            Task.FromResult(new EvidenceObject(storageUri, "memory", storageUri, expectedHash, null, bytes.Length));

        public Task<Stream> OpenVerifiedReadAsync(string requestedUri, string requestedHash,
            CancellationToken ct = default)
        {
            Assert.Equal(storageUri, requestedUri);
            Assert.Equal(expectedHash, requestedHash);
            Assert.Equal(expectedHash,
                Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant());
            return Task.FromResult<Stream>(new MemoryStream(content, writable: false));
        }
    }

    public void Dispose() => _database.Dispose();
}
