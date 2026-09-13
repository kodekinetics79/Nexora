using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

public sealed class CustomerIdentityMaintenanceTests
{
    [Fact]
    public async Task Synchronize_builds_current_profile_and_contact_identity_without_cross_tenant_leakage()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(null);
        Seed.EnsureBusinessUnit(db, 41);
        Seed.EnsureBusinessUnit(db, 42);
        var customer = Customer(41, "CU00000041", "Acme Controls", "buyer@acme.test");
        var other = Customer(42, "CU00000042", "Other Acme", "other@acme.test");
        db.Customers.AddRange(customer, other);
        await db.SaveChangesAsync();
        db.Contacts.Add(new Contact
        {
            BusinessUnitId = 41,
            CustomerId = customer.Id,
            FirstName = "Robert",
            LastName = "Buyer",
            Email = "robert@acme.test",
            MobileNo = "+1 (212) 555-0100",
            IsActive = true,
            CreatedBy = "seed",
            CreatedOn = DateTime.UtcNow,
            ConcurrencyToken = Guid.NewGuid()
        });
        await db.SaveChangesAsync();

        await CustomerIdentityMaintenance.SynchronizeAsync(db, 41, customer.Id, "CustomerProfile");
        await db.SaveChangesAsync();

        var identifiers = await db.Set<CustomerIdentifier>().AsNoTracking()
            .Where(i => i.BusinessUnitId == 41 && i.CustomerId == customer.Id && i.EffectiveTo == null)
            .ToListAsync();
        Assert.Contains(identifiers, i => i.IdentifierType == CustomerIdentifierType.ErpAccount && i.NormalizedValue == "CU00000041");
        Assert.Contains(identifiers, i => i.IdentifierType == CustomerIdentifierType.CustomerName && i.NormalizedValue == "ACME CONTROLS");
        Assert.Contains(identifiers, i => i.IdentifierType == CustomerIdentifierType.Email && i.NormalizedValue == "buyer@acme.test");
        Assert.Contains(identifiers, i => i.IdentifierType == CustomerIdentifierType.Email && i.NormalizedValue == "robert@acme.test");
        Assert.Contains(identifiers, i => i.IdentifierType == CustomerIdentifierType.Domain && i.NormalizedValue == "acme.test");
        Assert.Contains(identifiers, i => i.IdentifierType == CustomerIdentifierType.Phone && i.NormalizedValue == "12125550100");
        Assert.DoesNotContain(identifiers, i => i.CustomerId == other.Id);
    }

    [Fact]
    public async Task Synchronize_expires_stale_managed_values_and_preserves_governed_manual_aliases()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(null);
        Seed.EnsureBusinessUnit(db, 41);
        var customer = Customer(41, "CU00000041", "Original Name", "old@example.test");
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        await CustomerIdentityMaintenance.SynchronizeAsync(db, 41, customer.Id, "CustomerProfile");
        db.Add(new CustomerIdentifier
        {
            BusinessUnitId = 41,
            CustomerId = customer.Id,
            IdentifierType = CustomerIdentifierType.Alias,
            NormalizedValue = "SPECIAL ACCOUNT",
            DisplayValue = "Special Account",
            IsVerified = true,
            Confidence = 1m,
            Source = "HumanReview",
            EffectiveFrom = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        customer.Name = "Current Name";
        customer.ContactEmail = "new@example.test";
        await db.SaveChangesAsync();
        await CustomerIdentityMaintenance.SynchronizeAsync(db, 41, customer.Id, "CustomerProfile");
        await db.SaveChangesAsync();

        var all = await db.Set<CustomerIdentifier>().AsNoTracking()
            .Where(i => i.BusinessUnitId == 41 && i.CustomerId == customer.Id)
            .ToListAsync();
        Assert.Contains(all, i => i.NormalizedValue == "ORIGINAL NAME" && i.EffectiveTo != null);
        Assert.Contains(all, i => i.NormalizedValue == "old@example.test" && i.EffectiveTo != null);
        Assert.Contains(all, i => i.NormalizedValue == "CURRENT NAME" && i.EffectiveTo == null);
        Assert.Contains(all, i => i.NormalizedValue == "new@example.test" && i.EffectiveTo == null);
        Assert.Contains(all, i => i.IdentifierType == CustomerIdentifierType.Alias && i.EffectiveTo == null);

        customer.IsActive = false;
        await db.SaveChangesAsync();
        await CustomerIdentityMaintenance.SynchronizeAsync(db, 41, customer.Id, "CustomerProfile");
        await db.SaveChangesAsync();

        var active = await db.Set<CustomerIdentifier>().AsNoTracking()
            .Where(i => i.BusinessUnitId == 41 && i.CustomerId == customer.Id && i.EffectiveTo == null)
            .ToListAsync();
        Assert.Empty(active);
    }

    [Fact]
    public async Task Synchronize_never_expires_what_a_reviewer_taught()
    {
        // The learning loop writes Source = "LeadReviewLearned", which is deliberately
        // ABSENT from ManagedSources. If a customer-profile sync could expire it, every
        // client correction a rep made would quietly evaporate the next time someone edited
        // the customer record — and the same document would come back unresolved.
        using var database = new TestDb();
        await using var db = database.ContextFor(null);
        Seed.EnsureBusinessUnit(db, 41);
        var customer = Customer(41, "CU00000041", "Saudi Electricity Company", "profile@se.com.sa");
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        await CustomerIdentityMaintenance.SynchronizeAsync(db, 41, customer.Id, "CustomerProfile");
        await db.SaveChangesAsync();

        db.AddRange(
            Learned(customer.Id, CustomerIdentifierType.Alias, "SEC", "SEC", true, 0.90m),
            Learned(customer.Id, CustomerIdentifierType.PortalAccount,
                "MATERIALS E BIDDING SYSTEM|2004414", "MATERIALS E-BIDDING SYSTEM / 2004414", true, 0.92m),
            Learned(customer.Id, CustomerIdentifierType.RfqNumberPattern,
                "^C\\d{9}$", "C001046556", false, 0.50m));
        await db.SaveChangesAsync();

        // The customer record is renamed and re-synced: everything MANAGED is rebuilt.
        customer.Name = "Saudi Electricity Co.";
        customer.ContactEmail = "newprofile@se.com.sa";
        await db.SaveChangesAsync();
        await CustomerIdentityMaintenance.SynchronizeAsync(db, 41, customer.Id, "CustomerProfile");
        await db.SaveChangesAsync();

        var active = await db.Set<CustomerIdentifier>().AsNoTracking()
            .Where(i => i.BusinessUnitId == 41 && i.CustomerId == customer.Id && i.EffectiveTo == null)
            .ToListAsync();
        var learned = active.Where(i => i.Source == "LeadReviewLearned").ToList();
        Assert.Equal(3, learned.Count);
        Assert.Contains(learned, i => i.IdentifierType == CustomerIdentifierType.Alias && i.NormalizedValue == "SEC");
        Assert.Contains(learned, i => i.IdentifierType == CustomerIdentifierType.PortalAccount);
        Assert.Contains(learned, i => i.IdentifierType == CustomerIdentifierType.RfqNumberPattern && !i.IsVerified);
        // The managed side still did its job.
        Assert.Contains(active, i => i.NormalizedValue == "newprofile@se.com.sa");

        static CustomerIdentifier Learned(
            long customerId, CustomerIdentifierType type, string normalized, string display,
            bool verified, decimal confidence) => new()
            {
                BusinessUnitId = 41,
                CustomerId = customerId,
                IdentifierType = type,
                NormalizedValue = normalized,
                DisplayValue = display,
                IsVerified = verified,
                Confidence = confidence,
                Source = "LeadReviewLearned",
                EffectiveFrom = DateTime.UtcNow,
                ObservationCount = 1,
                LastObservedOn = DateTime.UtcNow
            };
    }

    [Theory]
    // A Saudi consumer ISP, a national one, a free-mail provider, and a procurement relay.
    [InlineData("agent@sahara.com")]
    [InlineData("agent@awalnet.net.sa")]
    [InlineData("buyer.person@gmail.com")]
    [InlineData("sec-tenders@bidnet.com")]
    public async Task Synchronize_writes_only_the_exact_address_for_a_contact_on_a_shared_mail_domain(string email)
    {
        // A contact saved at agent@sahara.com wrote sahara.com as SEC's verified 0.95 Domain, and the
        // next mail from any other Sahara subscriber, about any buyer, linked to SEC and routed there.
        using var database = new TestDb();
        await using var db = database.ContextFor(null);
        Seed.EnsureBusinessUnit(db, 41);
        var customer = Customer(41, "CU00000041", "Saudi Electricity Company", "profile@se.com.sa");
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        db.Contacts.Add(new Contact
        {
            BusinessUnitId = 41,
            CustomerId = customer.Id,
            FirstName = "Freight",
            LastName = "Agent",
            Email = email,
            IsActive = true,
            CreatedBy = "seed",
            CreatedOn = DateTime.UtcNow,
            ConcurrencyToken = Guid.NewGuid()
        });
        // The row an earlier sync wrote for that domain, which this sync must now expire.
        var sharedDomain = RoutingValueNormalizer.DomainFromEmail(email)!;
        db.Add(new CustomerIdentifier
        {
            BusinessUnitId = 41,
            CustomerId = customer.Id,
            IdentifierType = CustomerIdentifierType.Domain,
            NormalizedValue = sharedDomain,
            DisplayValue = sharedDomain,
            IsVerified = true,
            Confidence = 0.95m,
            Source = "CustomerContact",
            EffectiveFrom = DateTime.UtcNow.AddDays(-30)
        });
        await db.SaveChangesAsync();

        await CustomerIdentityMaintenance.SynchronizeAsync(db, 41, customer.Id, "CustomerProfile");
        await db.SaveChangesAsync();

        var active = await db.Set<CustomerIdentifier>().AsNoTracking()
            .Where(i => i.BusinessUnitId == 41 && i.CustomerId == customer.Id && i.EffectiveTo == null)
            .ToListAsync();
        Assert.Contains(active, i => i.IdentifierType == CustomerIdentifierType.Email && i.NormalizedValue == email);
        Assert.DoesNotContain(active, i => i.IdentifierType == CustomerIdentifierType.Domain && i.NormalizedValue == sharedDomain);
        // The customer's own organisation domain, from its profile address, is still written.
        Assert.Contains(active, i => i.IdentifierType == CustomerIdentifierType.Domain && i.NormalizedValue == "se.com.sa");
    }

    [Theory]
    // What a confirmation left on the customer before policy A: a verified learned row, a demoted or
    // unverified filing, and a row some other process wrote. None of them links or routes a domain now.
    [InlineData(CustomerIdentifierType.Domain, "LeadReviewLearned", true)]
    [InlineData(CustomerIdentifierType.Domain, "LeadReviewUnverified", false)]
    [InlineData(CustomerIdentifierType.Domain, "SomeOtherProcess", true)]
    [InlineData(CustomerIdentifierType.Email, "LeadReviewLearned", true)]
    [InlineData(CustomerIdentifierType.Email, "LeadReviewUnverified", false)]
    public async Task PolicyA_a_contact_a_person_saves_takes_over_the_row_a_confirmation_left_for_its_address(
        CustomerIdentifierType type, string source, bool verified)
    {
        // OWNER DECISION 2026-09-13, policy A: a whole email domain is never learned from confirmations; it
        // comes only from a customer contact or an admin entry, and the resolver's S2 and routing read a Domain
        // row only where a person entered it. One active row may hold a value per customer, so the row a
        // confirmation left on se.com.sa swallowed the contact a person then saved there: the sync left it
        // LeadReviewLearned and SEC's contact domain never linked or routed. The exact address the same.
        using var database = new TestDb();
        await using var db = database.ContextFor(null);
        Seed.EnsureBusinessUnit(db, 41);
        var customer = Customer(41, "CU00000041", "Saudi Electricity Company", "unused@example.test");
        customer.ContactEmail = null;
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        var value = type == CustomerIdentifierType.Domain ? "se.com.sa" : "procurement@se.com.sa";
        db.Add(new CustomerIdentifier
        {
            BusinessUnitId = 41,
            CustomerId = customer.Id,
            IdentifierType = type,
            NormalizedValue = value,
            DisplayValue = value,
            IsVerified = verified,
            Confidence = verified ? 0.95m : 0.50m,
            Source = source,
            EffectiveFrom = DateTime.UtcNow.AddDays(-30),
            ObservationCount = 2,
            LastObservedOn = DateTime.UtcNow.AddDays(-1),
            LearnedFromLeadId = 9001,
            LearnedFromReviewAuditId = 9002
        });
        var contact = ContactAt(customer.Id, "procurement@se.com.sa");
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();

        await CustomerIdentityMaintenance.SynchronizeAsync(db, 41, customer.Id, "CustomerContact");
        await db.SaveChangesAsync();

        var row = Assert.Single(await ActiveAsync(db, customer.Id, type, value));
        Assert.Equal("CustomerContact", row.Source);
        Assert.True(row.IsVerified);
        Assert.Equal(type == CustomerIdentifierType.Domain ? 0.95m : 1m, row.Confidence);
        // No longer the learner's: a relink of lead 9001 must not expire or demote the contact's row.
        Assert.Null(row.LearnedFromLeadId);
        Assert.Null(row.LearnedFromReviewAuditId);

        // The contact leaves. Its row goes with it, and the confirmation's filing does not come back.
        contact.IsActive = false;
        await db.SaveChangesAsync();
        await CustomerIdentityMaintenance.SynchronizeAsync(db, 41, customer.Id, "CustomerContact");
        await db.SaveChangesAsync();
        Assert.Empty(await ActiveAsync(db, customer.Id, type, value));
    }

    [Fact]
    public async Task PolicyA_a_contact_sync_never_rewrites_a_domain_row_an_administrator_entered()
    {
        // Policy A: a domain comes from a customer contact OR an admin entry. An administrator who put
        // se.com.sa on SEC and left it unverified said "not yet". A contact saved at that domain does not
        // overrule the administrator, and the row is not the sync's to expire when the contact goes.
        using var database = new TestDb();
        await using var db = database.ContextFor(null);
        Seed.EnsureBusinessUnit(db, 41);
        var customer = Customer(41, "CU00000041", "Saudi Electricity Company", "unused@example.test");
        customer.ContactEmail = null;
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        db.Add(new CustomerIdentifier
        {
            BusinessUnitId = 41,
            CustomerId = customer.Id,
            IdentifierType = CustomerIdentifierType.Domain,
            NormalizedValue = "se.com.sa",
            DisplayValue = "se.com.sa",
            IsVerified = false,
            Confidence = 0.50m,
            Source = "MasterData",
            EffectiveFrom = DateTime.UtcNow.AddDays(-30)
        });
        var contact = ContactAt(customer.Id, "procurement@se.com.sa");
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();

        await CustomerIdentityMaintenance.SynchronizeAsync(db, 41, customer.Id, "CustomerContact");
        await db.SaveChangesAsync();

        var row = Assert.Single(await ActiveAsync(db, customer.Id, CustomerIdentifierType.Domain, "se.com.sa"));
        Assert.Equal("MasterData", row.Source);
        Assert.False(row.IsVerified);
        Assert.Equal(0.50m, row.Confidence);

        contact.IsActive = false;
        await db.SaveChangesAsync();
        await CustomerIdentityMaintenance.SynchronizeAsync(db, 41, customer.Id, "CustomerContact");
        await db.SaveChangesAsync();
        row = Assert.Single(await ActiveAsync(db, customer.Id, CustomerIdentifierType.Domain, "se.com.sa"));
        Assert.Equal("MasterData", row.Source);
    }

    [Fact]
    public async Task PolicyA_a_contact_on_a_shared_mail_domain_never_turns_a_learned_domain_row_into_a_contacts_row()
    {
        // The organisation-domain guard stands in front of the take-over. gmail.com names nobody, so a contact
        // at buyer.person@gmail.com writes its exact address and nothing for gmail.com, and the learned gmail.com
        // row a confirmation left stays a learned row, which links and routes nothing under policy A.
        using var database = new TestDb();
        await using var db = database.ContextFor(null);
        Seed.EnsureBusinessUnit(db, 41);
        var customer = Customer(41, "CU00000041", "Saudi Electricity Company", "unused@example.test");
        customer.ContactEmail = null;
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        db.Add(new CustomerIdentifier
        {
            BusinessUnitId = 41,
            CustomerId = customer.Id,
            IdentifierType = CustomerIdentifierType.Domain,
            NormalizedValue = "gmail.com",
            DisplayValue = "gmail.com",
            IsVerified = true,
            Confidence = 0.95m,
            Source = "LeadReviewLearned",
            EffectiveFrom = DateTime.UtcNow.AddDays(-30)
        });
        db.Contacts.Add(ContactAt(customer.Id, "buyer.person@gmail.com"));
        await db.SaveChangesAsync();

        await CustomerIdentityMaintenance.SynchronizeAsync(db, 41, customer.Id, "CustomerContact");
        await db.SaveChangesAsync();

        var row = Assert.Single(await ActiveAsync(db, customer.Id, CustomerIdentifierType.Domain, "gmail.com"));
        Assert.Equal("LeadReviewLearned", row.Source);
        var address = Assert.Single(await ActiveAsync(db, customer.Id, CustomerIdentifierType.Email, "buyer.person@gmail.com"));
        Assert.Equal("CustomerContact", address.Source);
    }

    private static async Task<List<CustomerIdentifier>> ActiveAsync(
        ErpRfqAutomationContext db, long customerId, CustomerIdentifierType type, string value) =>
        await db.Set<CustomerIdentifier>().AsNoTracking()
            .Where(i => i.BusinessUnitId == 41 && i.CustomerId == customerId && i.IdentifierType == type
                        && i.NormalizedValue == value && i.EffectiveTo == null)
            .ToListAsync();

    private static Contact ContactAt(long customerId, string email) => new()
    {
        BusinessUnitId = 41,
        CustomerId = customerId,
        FirstName = "Procurement",
        LastName = "Desk",
        Email = email,
        IsActive = true,
        CreatedBy = "seed",
        CreatedOn = DateTime.UtcNow,
        ConcurrencyToken = Guid.NewGuid()
    };

    private static Customer Customer(long tenantId, string docId, string name, string email) => new()
    {
        Buid = tenantId,
        DocId = docId,
        Name = name,
        ContactEmail = email,
        ImageUrl = string.Empty,
        IsActive = true,
        CreatedBy = "seed",
        CreatedOn = DateTime.UtcNow,
        ConcurrencyToken = Guid.NewGuid()
    };
}
