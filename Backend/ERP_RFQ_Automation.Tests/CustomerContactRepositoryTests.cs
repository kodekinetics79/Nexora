using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

public sealed class CustomerContactRepositoryTests
{
    [Fact]
    public async Task Customer_mutations_are_server_authoritative_versioned_and_soft_delete()
    {
        const long tenant = 41;
        using var database = new TestDb();
        await SeedTenantAsync(database, tenant);
        await using var context = database.ContextFor(tenant);
        var repository = new CustomerRepository(context);
        var customer = new Customer
        {
            Name = "  Acme Industries  ",
            ContactEmail = " BUYER@EXAMPLE.COM ",
            ImageUrl = string.Empty,
            Buid = 999,
            CreatedBy = "forged"
        };

        await repository.AddAsync(customer, tenant, "creator-7");

        Assert.Equal(tenant, customer.Buid);
        Assert.Equal("creator-7", customer.CreatedBy);
        Assert.Equal("Acme Industries", customer.Name);
        Assert.Equal("buyer@example.com", customer.ContactEmail);
        Assert.Equal($"CU{customer.Id:D8}", customer.DocId);
        Assert.NotEqual(Guid.Empty, customer.ConcurrencyToken);
        Assert.Contains(await context.Set<CustomerIdentifier>().ToListAsync(), identity =>
            identity.CustomerId == customer.Id && identity.IdentifierType == CustomerIdentifierType.Email &&
            identity.EffectiveTo == null);

        var contactRepository = new ContactRepository(context);
        var activeContact = new Contact
        {
            CustomerId = customer.Id,
            FirstName = "Account",
            LastName = "Owner",
            IsPrimary = true,
            IsActive = true
        };
        await contactRepository.AddAsync(activeContact, tenant, "creator-7");

        var firstToken = customer.ConcurrencyToken;
        var update = new Customer
        {
            Id = customer.Id,
            Name = "Acme Global",
            ContactEmail = "sales@acme.example",
            ImageUrl = string.Empty,
            IsActive = false,
            Buid = 999,
            CreatedBy = "forged"
        };
        await repository.UpdateAsync(update, tenant, "editor-8", firstToken);

        Assert.NotEqual(firstToken, update.ConcurrencyToken);
        var persisted = await context.Customers.AsNoTracking().SingleAsync(x => x.Id == customer.Id);
        Assert.Equal("creator-7", persisted.CreatedBy);
        Assert.Equal("editor-8", persisted.ModifiedBy);
        Assert.Equal(customer.DocId, persisted.DocId);
        Assert.True(persisted.IsActive);

        await repository.DeleteAsync(customer.Id, tenant, "deactivator-9", update.ConcurrencyToken);

        persisted = await context.Customers.AsNoTracking().SingleAsync(x => x.Id == customer.Id);
        Assert.False(persisted.IsActive);
        Assert.Equal("deactivator-9", persisted.ModifiedBy);
        var deactivatedContact = await context.Contacts.AsNoTracking().SingleAsync(x => x.Id == activeContact.Id);
        Assert.False(deactivatedContact.IsActive);
        Assert.False(deactivatedContact.IsPrimary);
        Assert.Equal("deactivator-9", deactivatedContact.ModifiedBy);
        Assert.All(
            await context.Set<CustomerIdentifier>()
                .Where(x => x.CustomerId == customer.Id)
                .ToListAsync(),
            identity => Assert.NotNull(identity.EffectiveTo));
    }

    [Fact]
    public async Task Customer_update_rejects_a_stale_concurrency_token()
    {
        const long tenant = 42;
        using var database = new TestDb();
        await SeedTenantAsync(database, tenant);
        Guid staleToken;
        long customerId;
        await using (var context = database.ContextFor(tenant))
        {
            var repository = new CustomerRepository(context);
            var customer = new Customer { Name = "Versioned", ImageUrl = string.Empty };
            await repository.AddAsync(customer, tenant, "creator");
            staleToken = customer.ConcurrencyToken;
            customerId = customer.Id;
            await repository.UpdateAsync(
                new Customer { Id = customerId, Name = "Version two", ImageUrl = string.Empty },
                tenant,
                "editor",
                staleToken);
        }

        await using var staleContext = database.ContextFor(tenant);
        var staleRepository = new CustomerRepository(staleContext);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => staleRepository.UpdateAsync(
            new Customer { Id = customerId, Name = "Stale overwrite", ImageUrl = string.Empty },
            tenant,
            "stale-editor",
            staleToken));
    }

    [Fact]
    public async Task Customer_contact_mutations_refresh_identity_and_deactivate_without_deleting()
    {
        const long tenant = 43;
        using var database = new TestDb();
        await SeedTenantAsync(database, tenant);
        await using var context = database.ContextFor(tenant);
        var customerRepository = new CustomerRepository(context);
        var customer = new Customer { Name = "Contact Account", ImageUrl = string.Empty };
        await customerRepository.AddAsync(customer, tenant, "creator");
        var contactRepository = new ContactRepository(context);
        var contact = new Contact
        {
            CustomerId = customer.Id,
            FirstName = "  Jane ",
            LastName = " Buyer ",
            Email = " JANE@EXAMPLE.COM ",
            IsActive = true,
            IsPrimary = true,
            BusinessUnitId = 999,
            CreatedBy = "forged"
        };

        await contactRepository.AddAsync(contact, tenant, "contact-creator");

        Assert.Equal(tenant, contact.BusinessUnitId);
        Assert.Equal("contact-creator", contact.CreatedBy);
        Assert.Equal("jane@example.com", contact.Email);
        Assert.Contains(await context.Set<CustomerIdentifier>().ToListAsync(), identity =>
            identity.CustomerId == customer.Id && identity.DisplayValue == "jane@example.com" &&
            identity.EffectiveTo == null);

        await contactRepository.DeleteAsync(contact.Id, tenant, "contact-deactivator", contact.ConcurrencyToken);

        var persisted = await context.Contacts.AsNoTracking().SingleAsync(x => x.Id == contact.Id);
        Assert.False(persisted.IsActive);
        Assert.False(persisted.IsPrimary);
        Assert.DoesNotContain(await context.Set<CustomerIdentifier>().ToListAsync(), identity =>
            identity.CustomerId == customer.Id && identity.DisplayValue == "jane@example.com" &&
            identity.EffectiveTo == null);
    }

    [Fact]
    public async Task Supplier_contacts_do_not_create_customer_identity_records()
    {
        const long tenant = 44;
        using var database = new TestDb();
        await SeedTenantAsync(database, tenant);
        await using var context = database.ContextFor(tenant);
        var supplier = new Supplier
        {
            Name = "Supplier One",
            ImageUrl = string.Empty,
            Buid = tenant,
            IsActive = true,
            CreatedBy = "seed",
            CreatedOn = DateTime.UtcNow,
            ConcurrencyToken = Guid.NewGuid()
        };
        context.Suppliers.Add(supplier);
        await context.SaveChangesAsync();
        var repository = new ContactRepository(context);

        await repository.AddAsync(new Contact
        {
            SupplierId = supplier.Id,
            FirstName = "Supply",
            LastName = "Contact",
            Email = "supplier@example.com"
        }, tenant, "creator");

        Assert.Empty(await context.Set<CustomerIdentifier>().ToListAsync());
    }

    [Fact]
    public async Task Contact_update_preserves_parent_and_lifecycle_state()
    {
        const long tenant = 45;
        using var database = new TestDb();
        await SeedTenantAsync(database, tenant);
        await using var context = database.ContextFor(tenant);
        var customerRepository = new CustomerRepository(context);
        var first = new Customer { Name = "First Account", ImageUrl = string.Empty };
        var second = new Customer { Name = "Second Account", ImageUrl = string.Empty };
        await customerRepository.AddAsync(first, tenant, "creator");
        await customerRepository.AddAsync(second, tenant, "creator");
        var repository = new ContactRepository(context);
        var contact = new Contact
        {
            CustomerId = first.Id,
            FirstName = "Stable",
            LastName = "Owner",
            IsActive = true
        };
        await repository.AddAsync(contact, tenant, "creator");

        var attemptedReparent = new Contact
        {
            Id = contact.Id,
            CustomerId = second.Id,
            FirstName = "Stable",
            LastName = "Owner",
            IsActive = false
        };
        await Assert.ThrowsAsync<ArgumentException>(() => repository.UpdateAsync(
            attemptedReparent, tenant, "editor", contact.ConcurrencyToken));

        var ordinaryEdit = new Contact
        {
            Id = contact.Id,
            CustomerId = first.Id,
            FirstName = "Updated",
            LastName = "Owner",
            IsActive = false
        };
        await repository.UpdateAsync(ordinaryEdit, tenant, "editor", contact.ConcurrencyToken);

        var persisted = await context.Contacts.AsNoTracking().SingleAsync(x => x.Id == contact.Id);
        Assert.Equal(first.Id, persisted.CustomerId);
        Assert.True(persisted.IsActive);
        Assert.Equal("Updated", persisted.FirstName);
    }

    [Fact]
    public async Task Active_contact_requires_an_active_tenant_owned_parent()
    {
        const long tenant = 46;
        using var database = new TestDb();
        await SeedTenantAsync(database, tenant);
        await using var context = database.ContextFor(tenant);
        var customer = new Customer
        {
            Name = "Inactive Account",
            ImageUrl = string.Empty,
            Buid = tenant,
            IsActive = false,
            CreatedBy = "seed",
            CreatedOn = DateTime.UtcNow,
            ConcurrencyToken = Guid.NewGuid(),
            DocId = "CU00000046"
        };
        context.Customers.Add(customer);
        await context.SaveChangesAsync();
        var repository = new ContactRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(() => repository.AddAsync(new Contact
        {
            CustomerId = customer.Id,
            FirstName = "Cannot",
            LastName = "Route",
            IsActive = true
        }, tenant, "creator"));
    }

    [Fact]
    public async Task Contact_identity_cannot_claim_another_customers_authoritative_email()
    {
        const long tenant = 47;
        using var database = new TestDb();
        await SeedTenantAsync(database, tenant);
        await using var context = database.ContextFor(tenant);
        var customers = new CustomerRepository(context);
        var first = new Customer { Name = "Email Owner", ContactEmail = "owner@example.test", ImageUrl = string.Empty };
        var second = new Customer { Name = "Other Account", ImageUrl = string.Empty };
        await customers.AddAsync(first, tenant, "creator");
        await customers.AddAsync(second, tenant, "creator");

        var contacts = new ContactRepository(context);
        var error = await Assert.ThrowsAsync<CustomerIdentityConflictException>(() => contacts.AddAsync(new Contact
        {
            CustomerId = second.Id,
            FirstName = "Conflicting",
            LastName = "Buyer",
            Email = "OWNER@example.test"
        }, tenant, "creator"));

        Assert.Contains("another customer", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(await context.Contacts.AsNoTracking().ToListAsync(), contact =>
            contact.CustomerId == second.Id && contact.Email == "owner@example.test");
    }

    private static async Task SeedTenantAsync(TestDb database, long tenant)
    {
        await using var context = database.ContextFor(null);
        context.BusinessUnits.Add(new BusinessUnit
        {
            Id = tenant,
            BusinessUnitCode = $"BU-{tenant}",
            BusinessUnitName = $"Tenant {tenant}",
            IsActive = true,
            CreatedBy = "seed",
            CreatedOn = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
    }
}
