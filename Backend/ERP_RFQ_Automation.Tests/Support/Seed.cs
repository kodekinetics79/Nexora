using ERP_RFQ_Automation.Models;

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
