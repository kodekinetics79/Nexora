using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using Models = ERP_RFQ_Automation.Models;

namespace ERP_RFQ_Automation.Tests.Support;

/// <summary>
/// Minimal, FK-satisfying seed helpers. Commercial documents (Lead) require a
/// BusinessUnit -> EmailConfiguration -> EmailIngest parent chain, which SQLite's
/// foreign-key enforcement demands; these helpers create exactly that graph.
/// </summary>
public static class Seed
{
    private static readonly DateTime Now = new(2026, 7, 14, 0, 0, 0, DateTimeKind.Utc);

    public static BusinessUnit EnsureBusinessUnit(ErpRfqAutomationContext ctx, long id)
        => ctx.BusinessUnits.Find(id) ?? BusinessUnit(ctx, id);

    /// <summary>
    /// A business unit for a database pinned to an EARLIER migration, seeded by raw SQL naming only
    /// the columns that existed then — the same reason <see cref="HistoricalLead"/> exists.
    ///
    /// <para>The populated-upgrade rehearsals migrate down to a fixed target, seed, then upgrade.
    /// Seeding through the current model makes EF name every column the model knows about, so each
    /// new column added upstream — <c>TaxRegistrationNumber</c> was the one that broke it — turns
    /// into <c>42703</c> against the older schema. That failure looks like a migration defect and
    /// is not one, which is the expensive kind of red.</para>
    /// </summary>
    public static void HistoricalBusinessUnit(ErpRfqAutomationContext ctx, long id)
    {
        if (ctx.ChangeTracker.HasChanges())
            ctx.SaveChanges();

        ctx.Database.ExecuteSql($"""
            INSERT INTO "BusinessUnits" ("ID", "BusinessUnitCode", "BusinessUnitName", "IsActive", "CreatedBy", "CreatedOn")
            VALUES ({id}, {$"BU{id}"}, {$"Business Unit {id}"}, true, 'seed', now())
            ON CONFLICT ("ID") DO NOTHING;
            """);
    }

    public static BusinessUnit BusinessUnit(ErpRfqAutomationContext ctx, long id)
    {
        var bu = new BusinessUnit
        {
            Id = id,
            BusinessUnitCode = $"BU{id}",
            BusinessUnitName = $"Business Unit {id}",
            IsActive = true,
            CreatedBy = "seed",
            CreatedOn = Now
        };
        ctx.BusinessUnits.Add(bu);
        return bu;
    }

    public static EmailConfiguration EmailConfig(ErpRfqAutomationContext ctx, long id, long businessUnitId)
    {
        var cfg = new EmailConfiguration
        {
            Id = id,
            BusinessUnitId = businessUnitId,
            ConfigurationName = $"cfg{id}",
            EmailAddress = $"inbox{id}@example.com",
            Protocol = "IMAP",
            Host = "imap.example.com",
            Port = 993,
            Username = "user",
            Password = "secret",
            UseSsl = true,
            PollingInterval = 60,
            IsActive = true,
            CreatedOn = Now
        };
        ctx.EmailConfigurations.Add(cfg);
        return cfg;
    }

    public static EmailIngest EmailIngest(ErpRfqAutomationContext ctx, long id, long emailConfigId, string parseStatus)
    {
        var ingest = new EmailIngest
        {
            Id = id,
            MessageId = $"msg-{id}",
            FromEmail = "buyer@customer.com",
            EmailConfigurationId = emailConfigId,
            ParseStatus = parseStatus,
            CreatedOn = Now
        };
        ctx.EmailIngests.Add(ingest);
        return ingest;
    }

    /// <summary>Seeds a full BU + email-config + ingest + lead chain and returns the lead.
    /// Ids are derived from <paramref name="leadId"/> so multiple leads don't collide.</summary>
    public static Lead Lead(
        ErpRfqAutomationContext ctx,
        long leadId,
        long businessUnitId,
        long? leadStatusId = null,
        string parseStatus = "Success",
        string? headerRemarks = null,
        string? buyersName = "Acme Buyer",
        IEnumerable<LeadItem>? items = null)
    {
        // One BU per business-unit id (reused across leads in the same BU).
        var bu = ctx.BusinessUnits.Find(businessUnitId) ?? BusinessUnit(ctx, businessUnitId);

        var cfgId = 10_000 + leadId;
        var ingestId = 20_000 + leadId;
        EmailConfig(ctx, cfgId, businessUnitId);
        EmailIngest(ctx, ingestId, cfgId, parseStatus);

        var lead = new Lead
        {
            Id = leadId,
            Rfqno = $"RFQ-{leadId}",
            BuyersName = buyersName,
            RecDate = Now,
            HeaderRemarks = headerRemarks,
            LeadSource = "Email",
            CreatedBy = "seed",
            CreatedDate = Now,
            BusinessUnitId = businessUnitId,
            EmailIngestsId = ingestId,
            LeadStatusId = leadStatusId
        };
        if (items != null)
            foreach (var it in items) lead.LeadItems.Add(it);

        ctx.Leads.Add(lead);
        return lead;
    }

    /// <summary>
    /// A Lead row for MIGRATION REHEARSALS — tests that migrate down to a historical
    /// migration, seed data, and migrate forward again.
    ///
    /// Those tests run the CURRENT EF model against an OLD schema, so seeding through
    /// <see cref="Lead"/> (or <see cref="EmailIngest"/>) breaks the moment ANY later
    /// migration adds a column: EF names every column the model knows and PostgreSQL answers
    /// 42703. That is not a defect in the migration under test, and pinning each rehearsal to
    /// its own era is the fix already used by
    /// <c>Module03SalesRoutingMigrationPostgreSqlTests.SeedOwnersAsync</c>.
    ///
    /// So: raw SQL naming ONLY columns that predate every rehearsal target (everything else
    /// is nullable or database-defaulted, and CommercialCaseId/CommercialCaseReference come
    /// from the reference trigger), no EmailIngest at all, and the returned entity is
    /// ATTACHED as Unchanged so navigation properties still work without EF ever selecting or
    /// writing the lead itself.
    /// </summary>
    public static Lead HistoricalLead(
        ErpRfqAutomationContext ctx, long leadId, long businessUnitId, string? buyersName = "Acme Buyer")
    {
        // Raw SQL, not Find: on an untracked id Find issues a SELECT naming every column the
        // CURRENT model maps, so against a database pinned to an earlier migration it raises 42703
        // for whichever column was added upstream most recently. That reads as a migration defect
        // and is not one.
        HistoricalBusinessUnit(ctx, businessUnitId);
        // Flush whatever the caller staged (its own business units and their reference
        // configuration) so the raw INSERT below has its foreign-key targets.
        if (ctx.ChangeTracker.HasChanges())
            ctx.SaveChanges();

        ctx.Database.ExecuteSql($"""
            INSERT INTO "Leads" ("ID", "BusinessUnitID", "RFQNo", "BuyersName", "RecDate", "LeadSource", "CreatedBy")
            VALUES ({leadId}, {businessUnitId}, {$"RFQ-{leadId}"}, {buyersName}, now(), 'Email', 'seed')
            """);

        // The Nexora Serial is allocated by the reference trigger, and downstream commercial
        // records refuse a lead without one. Read it back with a two-column PROJECTION —
        // loading the entity would select every column the current model knows and defeat
        // the whole point of this helper.
        var identity = ctx.Leads.IgnoreQueryFilters()
            .Where(x => x.Id == leadId)
            .Select(x => new { x.CommercialCaseId, x.CommercialCaseReference })
            .Single();

        var lead = new Lead
        {
            Id = leadId,
            Rfqno = $"RFQ-{leadId}",
            BuyersName = buyersName,
            RecDate = Now,
            LeadSource = "Email",
            CreatedBy = "seed",
            CreatedDate = Now,
            BusinessUnitId = businessUnitId
        };
        // Private setters, deliberately: the commercial identity is assigned by the domain,
        // never by a caller. Mirroring the database values onto the stub keeps that rule.
        typeof(Models.Lead).GetProperty(nameof(Models.Lead.CommercialCaseId))!.SetValue(lead, identity.CommercialCaseId);
        typeof(Models.Lead).GetProperty(nameof(Models.Lead.CommercialCaseReference))!.SetValue(lead, identity.CommercialCaseReference);

        // Unchanged, so EF never re-inserts it and never selects its columns.
        ctx.Attach(lead);
        return lead;
    }

    /// <summary>
    /// Stamps a sales order's commercial identity for tests that seed a document graph directly
    /// instead of going through a creation service.
    ///
    /// <para>Private setters, deliberately, exactly as on <see cref="HistoricalLead"/>: an Order
    /// takes its case from the document it came from and nothing else may assign it. A test is not
    /// allowed to be the exception that reopens the setter, so the fixture reaches through
    /// reflection and the production surface stays closed.</para>
    /// </summary>
    public static Models.Order StampCommercialCase(
        Models.Order order, long commercialCaseId, string nexoraSerial, long? contactId = null)
    {
        typeof(Models.Order).GetProperty(nameof(Models.Order.CommercialCaseId))!
            .SetValue(order, (long?)commercialCaseId);
        typeof(Models.Order).GetProperty(nameof(Models.Order.NexoraSerial))!
            .SetValue(order, nexoraSerial);
        if (contactId.HasValue)
            typeof(Models.Order).GetProperty(nameof(Models.Order.ContactId))!.SetValue(order, contactId);
        return order;
    }

    /// <summary>Sets only the contact, for fixtures that predate the commercial case entirely.</summary>
    public static Models.Order StampContact(Models.Order order, long? contactId)
    {
        typeof(Models.Order).GetProperty(nameof(Models.Order.ContactId))!.SetValue(order, contactId);
        return order;
    }

    public static LeadItem LeadItem(long id, string? lineItemNo, int quantity, string? productName = "Widget")
        => new()
        {
            Id = id,
            LineItemNo = lineItemNo,
            ProductShortName = productName,
            Quantity = quantity
        };

    /// <summary>A LeadStatus (Setup_Master) row, e.g. SetupId 24 = "Lead Accepted",
    /// required as the FK target when a lead's LeadStatusId is set.</summary>
    public static SetupMaster LeadStatus(ErpRfqAutomationContext ctx, long setupId, long businessUnitId, string value)
    {
        EnsureBusinessUnit(ctx, businessUnitId);
        var s = new SetupMaster
        {
            SetupId = setupId,
            SetupType = "LeadStatus",
            SetupValue = value,
            BusinessUnitId = businessUnitId,
            IsActive = true,
            CreatedBy = "seed",
            CreatedOn = Now
        };
        ctx.SetupMasters.Add(s);
        return s;
    }

    public static Customer Customer(ErpRfqAutomationContext ctx, long id, long? buid, string name)
    {
        if (buid.HasValue) EnsureBusinessUnit(ctx, buid.Value);
        var c = new Customer
        {
            Id = id,
            Name = name,
            ImageUrl = "n/a",
            Buid = buid,
            IsActive = true,
            CreatedBy = "seed",
            CreatedOn = Now
        };
        ctx.Customers.Add(c);
        return c;
    }

    public static Contact Contact(ErpRfqAutomationContext ctx, long id, long businessUnitId, long customerId,
        string email = "buyer@customer.test")
    {
        var contact = new Contact
        {
            Id = id,
            BusinessUnitId = businessUnitId,
            CustomerId = customerId,
            FirstName = "Test",
            LastName = "Buyer",
            Email = email,
            IsActive = true,
            CreatedBy = "seed",
            CreatedOn = Now
        };
        ctx.Contacts.Add(contact);
        return contact;
    }
}
