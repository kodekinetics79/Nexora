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
