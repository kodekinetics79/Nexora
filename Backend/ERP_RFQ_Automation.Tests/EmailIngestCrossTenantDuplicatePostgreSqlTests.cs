using ERP_RFQ_Automation.CommercialCases.Lifecycle;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Services.Interfaces;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using System.Text.Json;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// SEC-ING-01 — the externally triggerable cross-tenant read in the email door.
///
/// <para><b>The defect.</b> <c>EmailService.SaveLeadFromEmailAndAttachments</c> decided whether an
/// inbound message was a duplicate with
/// <c>context.Leads.AnyAsync(l =&gt; l.Rfqno == ai.Rfqno &amp;&amp; l.BuyersName == ai.BuyersName)</c>
/// — no business-unit predicate. Both inputs are extracted from the inbound email, so an outside
/// party who can send mail to a tenant's ingest mailbox chose them. Two consequences:</para>
/// <list type="number">
///   <item><description>A cross-tenant EXISTENCE ORACLE: send a message naming an RFQ number and a
///   buyer, and a silent drop tells you whether a named buyer is running a named tender through a
///   competitor on this platform. That is the most sensitive fact this product holds.</description></item>
///   <item><description>Cross-tenant DENIAL OF INGEST, on day one. One buyer issues one RFQ number
///   to many vendors — that is the normal case in competitive tendering — so two Nexora tenants
///   bidding the same tender suppressed each other's leads, first-come-first-served, with only a
///   LogWarning and no user-visible error.</description></item>
/// </list>
///
/// <para><b>Why the PostgreSQL lane.</b> The read was live because the poller ran with no tenant
/// scope, which does two things at once: the EF global query filter becomes a no-op
/// (<c>CurrentTenantId == null || …</c>) AND the connection is routed to a role that is not bound
/// by row-level security. Only PostgreSQL can carry that half of the story; SQLite has neither
/// roles nor RLS, so on the portable lane a test could not tell a working predicate apart from a
/// row the database was hiding anyway. These tests therefore run on a real PostgreSQL, through a
/// context with NO tenant — the poller's exact pre-fix condition — and the first assertion of the
/// first test is that the other tenant's lead really is visible from here. The row is reachable;
/// the predicate is the only thing that excludes it. Delete
/// <c>l.BusinessUnitId == businessUnitId</c> from either duplicate check and this file fails.</para>
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class EmailIngestCrossTenantDuplicatePostgreSqlTests
{
    // Two tenants bidding the same public tender — the ordinary case, not an exotic one.
    private const long TenantA = 970_301;
    private const long TenantB = 970_302;
    private const string SharedRfq = "SEC-MEB-2026-77410";
    private const string SharedBuyer = "Saudi Electricity Company";

    private readonly PostgreSqlTestDatabase _database;

    public EmailIngestCrossTenantDuplicatePostgreSqlTests(PostgreSqlTestDatabase database)
        => _database = database;

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Tenant_Bs_lead_is_ingested_when_tenant_A_already_holds_the_same_rfq_and_buyer()
    {
        await SeedTenantAsync(TenantA);
        await SeedTenantAsync(TenantB);

        // Tenant A got the tender first and has the lead.
        await SeedExistingLeadAsync(TenantA, SharedRfq, SharedBuyer);
        var (configB, ingestB) = await SeedMailboxAndIngestAsync(TenantB, "b-first-poll");

        await using var context = _database.ContextFor(null);

        // THE PRE-CONDITION THAT MAKES THIS A REAL TEST. This is the poller's own state: no tenant
        // scope, so the EF filter is a no-op and PostgreSQL is not hiding anything either. Tenant
        // A's lead is right there to be read. Everything that keeps it out of tenant B's duplicate
        // decision below is the explicit predicate.
        Assert.Null(context.ScopedTenantId);
        Assert.True(await context.Leads
            .AnyAsync(l => l.Rfqno == SharedRfq && l.BuyersName == SharedBuyer));

        var (service, root) = CreateService();
        try
        {
            var (leadId, _) = await service.SaveLeadFromEmailAndAttachments(
                InboundTender(SharedRfq, SharedBuyer),
                ingestB,
                configB,
                context,
                ScriptedExtraction(SharedRfq, SharedBuyer));

            Assert.True(leadId > 0,
                "Tenant B's lead was suppressed because ANOTHER tenant already held this RFQ "
                + "number and buyer. That is the cross-tenant duplicate check.");

            await using var verify = _database.ContextFor(null);
            var ingested = await verify.Leads.AsNoTracking().SingleAsync(l => l.Id == leadId);
            Assert.Equal(TenantB, ingested.BusinessUnitId);
            Assert.Equal(SharedRfq, ingested.Rfqno);
            Assert.Equal(SharedBuyer, ingested.BuyersName);
            Assert.Equal(new DateTime(2026, 10, 1), ingested.RequiredDeliveryDate);
            Assert.Equal("North Logistics Hub, Gate 4", ingested.DeliveryLocation);
            Assert.Equal("FRAME-2026-118", ingested.AgreementReference);

            // The legacy door establishes its immutable baseline immediately. The commercial
            // values must survive there too, not only on the mutable Lead projection.
            var revision = await verify.Set<ERP_RFQ_Automation.LeadIdentity.LeadRevision>()
                .AsNoTracking().SingleAsync(x => x.BusinessUnitId == TenantB && x.LeadId == leadId);
            using var snapshot = JsonDocument.Parse(revision.SnapshotJson);
            var snapshotRoot = snapshot.RootElement;
            Assert.Equal(new DateTime(2026, 10, 1),
                snapshotRoot.GetProperty("requiredDeliveryDate").GetDateTime());
            Assert.Equal("North Logistics Hub, Gate 4",
                snapshotRoot.GetProperty("deliveryLocation").GetString());
            Assert.Equal("FRAME-2026-118",
                snapshotRoot.GetProperty("agreementReference").GetString());

            // Tenant A is untouched: two tenants, two leads, same tender.
            Assert.Equal(2, await verify.Leads.AsNoTracking()
                .CountAsync(l => l.Rfqno == SharedRfq && l.BuyersName == SharedBuyer));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task The_same_tenant_still_suppresses_its_own_duplicate()
    {
        // The other half, and the reason the fix is a predicate rather than a deletion. Duplicate
        // detection is a per-business-unit question; it still has to answer it.
        const string rfq = "SEC-MEB-2026-77411";
        await SeedTenantAsync(TenantB);
        await SeedExistingLeadAsync(TenantB, rfq, SharedBuyer);
        var (configB, ingestB) = await SeedMailboxAndIngestAsync(TenantB, "b-redelivery");

        await using var context = _database.ContextFor(null);
        var (service, root) = CreateService();
        try
        {
            var (leadId, _) = await service.SaveLeadFromEmailAndAttachments(
                InboundTender(rfq, SharedBuyer),
                ingestB,
                configB,
                context,
                ScriptedExtraction(rfq, SharedBuyer));

            Assert.Equal(0, leadId);

            await using var verify = _database.ContextFor(null);
            Assert.Equal(1, await verify.Leads.AsNoTracking()
                .CountAsync(l => l.BusinessUnitId == TenantB && l.Rfqno == rfq));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Tenant_Bs_no_rfq_number_lead_survives_tenant_As_matching_buyer_and_line()
    {
        // The second duplicate check — buyer + line count + first line's product and quantity —
        // had the identical omission and is reached whenever the document states no RFQ number.
        const string buyer = "Marafiq Procurement";
        await SeedTenantAsync(TenantA);
        await SeedTenantAsync(TenantB);
        await SeedExistingLeadAsync(TenantA, rfqno: null, buyer, withMatchingLine: true);
        var (configB, ingestB) = await SeedMailboxAndIngestAsync(TenantB, "b-no-rfq-number");

        await using var context = _database.ContextFor(null);
        var (service, root) = CreateService();
        try
        {
            var (leadId, _) = await service.SaveLeadFromEmailAndAttachments(
                InboundTender(rfqno: null, buyer),
                ingestB,
                configB,
                context,
                ScriptedExtraction(rfqno: null, buyer));

            Assert.True(leadId > 0,
                "Tenant B's no-RFQ-number lead was suppressed by ANOTHER tenant's matching buyer "
                + "and line. The fuzzy duplicate check is cross-tenant.");

            await using var verify = _database.ContextFor(null);
            Assert.Equal(TenantB,
                (await verify.Leads.AsNoTracking().SingleAsync(l => l.Id == leadId)).BusinessUnitId);
        }
        finally
        {
            Cleanup(root);
        }
    }

    // ------------------------------------------------------------------------------ helpers

    private async Task SeedTenantAsync(long businessUnitId)
    {
        await using var db = _database.ContextFor(null);
        if (await db.BusinessUnits.AnyAsync(b => b.Id == businessUnitId)) return;
        var businessUnit = Seed.BusinessUnit(db, businessUnitId);
        // The identity baseline the email door establishes for every lead reads the tenant's
        // lifecycle catalogue; seeding it keeps this test on the real write path.
        db.SetupMasters.AddRange(LifecycleStatusCatalog.CreateFor(businessUnit, "tests"));
        await db.SaveChangesAsync();
    }

    private async Task SeedExistingLeadAsync(
        long businessUnitId, string? rfqno, string buyersName, bool withMatchingLine = false)
    {
        await using var db = _database.ContextFor(null);
        var lead = new Lead
        {
            Rfqno = rfqno,
            BuyersName = buyersName,
            RecDate = DateTime.UtcNow,
            LeadSource = "Email",
            CreatedBy = "tests",
            CreatedDate = DateTime.UtcNow,
            BusinessUnitId = businessUnitId,
            NoOfLineItems = withMatchingLine ? 1 : 0
        };
        if (withMatchingLine)
            lead.LeadItems.Add(new LeadItem
            {
                LineItemNo = "00010",
                CommodityProduct = "33kV XLPE cable",
                ProductShortDescription = "33kV XLPE cable",
                Quantity = 250,
                UnitOfMeasure = "M",
                Currency = "SAR"
            });
        db.Leads.Add(lead);
        await db.SaveChangesAsync();
    }

    private async Task<(EmailConfiguration Config, EmailIngest Ingest)> SeedMailboxAndIngestAsync(
        long businessUnitId, string discriminator)
    {
        await using var db = _database.ContextFor(null);
        var config = new EmailConfiguration
        {
            BusinessUnitId = businessUnitId,
            ConfigurationName = $"intake-{businessUnitId}-{discriminator}",
            EmailAddress = $"intake-{businessUnitId}-{discriminator}@tenant.test",
            Protocol = "IMAP",
            Host = "127.0.0.1",
            Port = 1,
            Username = $"intake-{businessUnitId}",
            Password = "secret",
            UseSsl = false,
            PollingInterval = 300,
            IsActive = true,
            CreatedOn = DateTime.UtcNow
        };
        db.EmailConfigurations.Add(config);
        await db.SaveChangesAsync();

        var ingest = new EmailIngest
        {
            MessageId = $"<{discriminator}-{Guid.NewGuid():N}@tenant.test>",
            EmailSubject = "Request for quotation",
            FromEmail = "bids@se.com.sa",
            ToEmail = config.EmailAddress,
            EmailConfigurationId = config.Id,
            CreatedOn = DateTime.UtcNow,
            ParseStatus = "Pending"
        };
        db.EmailIngests.Add(ingest);
        await db.SaveChangesAsync();

        // Detached from the seeding context: the service is handed the context under test and
        // must not inherit tracked state from another one.
        db.ChangeTracker.Clear();
        return (config, ingest);
    }

    private static MimeMessage InboundTender(string? rfqno, string buyer)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Bid Desk", "bids@se.com.sa"));
        message.To.Add(new MailboxAddress("Intake", "intake@tenant.test"));
        message.Subject = rfqno is null
            ? $"Request for quotation - {buyer}"
            : $"RFQ {rfqno} - {buyer}";
        message.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId();
        message.Date = DateTimeOffset.UtcNow;
        message.Body = new BodyBuilder
        {
            TextBody = "Kindly send your best price for 250 m of 33kV XLPE cable, delivery Dammam."
        }.ToMessageBody();
        return message;
    }

    /// <summary>What the extractor returns for this message. Both fields come off the wire, which
    /// is the whole reason the duplicate check must not be a platform-wide question.</summary>
    private static StubLlm ScriptedExtraction(string? rfqno, string buyer)
    {
        var item = Ext.Item(0.9, "33kV XLPE cable") with
        {
            CommodityProduct = "33kV XLPE cable",
            Quantity = 250
        };
        var result = Ext.Result([item], 0.9) with
        {
            Rfqno = rfqno,
            BuyersName = buyer,
            RequiredDeliveryDate = "2026-10-01",
            DeliveryLocation = " North Logistics Hub, Gate 4 ",
            AgreementReference = " FRAME-2026-118 "
        };
        return new StubLlm(result);
    }

    private static (EmailService Service, string Root) CreateService()
    {
        var root = Path.Combine(Path.GetTempPath(), "nexora-email-cross-tenant", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return (new EmailService(
            context: null!, // this method is handed the context under test; _context is unused
            env: new TenantWorkGateEnvironment(root),
            logger: new NoopLogger<EmailService>(),
            llmService: null!, // the per-call llmService argument is the one used
            scopeFactory: new UnusedScopeFactory(),
            configuration: new ConfigurationBuilder().Build(),
            storage: new TenantWorkGateStorage(root)), root);
    }

    private static void Cleanup(string root)
    {
        try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
    }

    private sealed class UnusedScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() =>
            throw new InvalidOperationException(
                "SaveLeadFromEmailAndAttachments works on the context it is handed; it must not "
                + "open a scope of its own.");
    }
}
