using System.ComponentModel.DataAnnotations;
using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.DTOs.Contact;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Repositories;

public class ContactRepository : IContactRepository
{
    private readonly ErpRfqAutomationContext _context;

    public ContactRepository(ErpRfqAutomationContext context)
    {
        _context = context;
    }

    public async Task<(IEnumerable<ContactResponseDTO>, int TotalCount)> GetAllAsync(
        int pageNumber,
        int pageSize,
        long? id,
        string? firstName,
        string? lastName,
        string? email,
        long? customerId,
        long? supplierId,
        bool? isPrimary,
        bool? isActive,
        long businessUnitId)
    {
        var query = _context.Contacts
            .AsNoTracking()
            .Where(contact => contact.BusinessUnitId == businessUnitId)
            .GroupJoin(
                _context.Customers.Where(c => c.Buid == businessUnitId),
                contact => contact.CustomerId,
                customer => customer.Id,
                (contact, customers) => new { contact, customers })
            .SelectMany(
                x => x.customers.DefaultIfEmpty(),
                (contact, customer) => new { contact.contact, customer })
            .GroupJoin(
                _context.Suppliers.Where(s => s.Buid == businessUnitId),
                x => x.contact.SupplierId,
                supplier => supplier.Id,
                (x, suppliers) => new { x.contact, x.customer, suppliers })
            .SelectMany(
                x => x.suppliers.DefaultIfEmpty(),
                (x, supplier) => new
                {
                    Contact = x.contact,
                    CustomerName = x.customer != null ? x.customer.Name : null,
                    SupplierName = supplier != null ? supplier.Name : null
                })
            .Where(x => x.Contact.CustomerId.HasValue || x.Contact.SupplierId.HasValue);

        if (id.HasValue)
            query = query.Where(x => x.Contact.Id == id.Value);
        if (!string.IsNullOrWhiteSpace(firstName))
        {
            var value = firstName.Trim().ToLower();
            query = query.Where(x => x.Contact.FirstName.ToLower().Contains(value));
        }
        if (!string.IsNullOrWhiteSpace(lastName))
        {
            var value = lastName.Trim().ToLower();
            query = query.Where(x => x.Contact.LastName.ToLower().Contains(value));
        }
        if (!string.IsNullOrWhiteSpace(email))
        {
            var value = email.Trim().ToLower();
            query = query.Where(x => x.Contact.Email != null && x.Contact.Email.ToLower().Contains(value));
        }
        if (customerId.HasValue)
            query = query.Where(x => x.Contact.CustomerId == customerId.Value);
        if (supplierId.HasValue)
            query = query.Where(x => x.Contact.SupplierId == supplierId.Value);
        if (isPrimary.HasValue)
            query = query.Where(x => x.Contact.IsPrimary == isPrimary.Value);
        if (isActive.HasValue)
            query = query.Where(x => x.Contact.IsActive == isActive.Value);

        var totalCount = await query.CountAsync();
        var contacts = await query
            .OrderBy(x => x.Contact.LastName)
            .ThenBy(x => x.Contact.FirstName)
            .ThenBy(x => x.Contact.Id)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new ContactResponseDTO
            {
                Id = x.Contact.Id,
                CustomerId = x.Contact.CustomerId,
                CustomerName = x.CustomerName,
                SupplierId = x.Contact.SupplierId,
                SupplierName = x.SupplierName,
                FirstName = x.Contact.FirstName,
                MiddleName = x.Contact.MiddleName,
                LastName = x.Contact.LastName,
                Email = x.Contact.Email,
                PhoneNo = x.Contact.PhoneNo,
                MobileNo = x.Contact.MobileNo,
                Position = x.Contact.Position,
                IsPrimary = x.Contact.IsPrimary,
                IsActive = x.Contact.IsActive,
                CreatedBy = x.Contact.CreatedBy,
                CreatedOn = x.Contact.CreatedOn,
                ModifiedBy = x.Contact.ModifiedBy,
                ModifiedOn = x.Contact.ModifiedOn,
                ConcurrencyToken = x.Contact.ConcurrencyToken
            })
            .ToListAsync();

        return (contacts, totalCount);
    }

    public async Task<Contact> GetByIdAsync(long id, long businessUnitId)
    {
        var contact = await _context.Contacts
            .AsNoTracking()
            .Include(c => c.Customer)
            .Include(c => c.Supplier)
            .FirstOrDefaultAsync(c => c.Id == id && c.BusinessUnitId == businessUnitId);

        return contact ?? throw new KeyNotFoundException(
            $"Contact with ID {id} not found in Business Unit {businessUnitId}.");
    }

    public async Task AddAsync(Contact contact, long businessUnitId, string actor)
    {
        ValidateTenantAndActor(businessUnitId, actor);
        NormalizeAndValidate(contact);
        var input = CloneContact(contact);
        var persisted = await ExecuteInTransactionAsync(async () =>
        {
            input.IsActive ??= true;
            await ValidateParentAsync(input.CustomerId, input.SupplierId, businessUnitId, input.IsActive != false);
            await ValidateEmailAvailableAsync(input.Email, businessUnitId, null);
            if (input.IsActive == false)
                input.IsPrimary = false;
            else if (input.IsPrimary == true)
                await ValidatePrimaryAvailableAsync(input.CustomerId, input.SupplierId, businessUnitId, null);

            if (input.CustomerId.HasValue)
            {
                await CustomerIdentityMaintenance.EnsureAuthoritativeValuesAvailableAsync(
                    _context, businessUnitId, input.CustomerId,
                    [
                        (CustomerIdentifierType.Email, input.Email),
                        (CustomerIdentifierType.Phone, input.PhoneNo),
                        (CustomerIdentifierType.Phone, input.MobileNo)
                    ]);
            }

            var candidate = CloneContact(input);
            candidate.BusinessUnitId = businessUnitId;
            candidate.CreatedBy = actor;
            candidate.CreatedOn = DateTime.UtcNow;
            candidate.ModifiedBy = null;
            candidate.ModifiedOn = null;
            candidate.ConcurrencyToken = Guid.NewGuid();
            _context.Contacts.Add(candidate);
            await _context.SaveChangesAsync();
            if (candidate.CustomerId.HasValue)
            {
                await CustomerIdentityMaintenance.SynchronizeAsync(
                    _context, businessUnitId, candidate.CustomerId.Value, "CustomerContact");
                await _context.SaveChangesAsync();
            }
            return candidate;
        });
        CopyPersistedValues(persisted, contact);
    }

    public async Task UpdateAsync(
        Contact contact,
        long businessUnitId,
        string actor,
        Guid expectedConcurrencyToken)
    {
        ValidateTenantAndActor(businessUnitId, actor);
        ValidateExpectedToken(expectedConcurrencyToken);
        NormalizeAndValidate(contact);
        var input = CloneContact(contact);
        var persisted = await ExecuteInTransactionAsync(async () =>
        {
            var existing = await _context.Contacts
                .FirstOrDefaultAsync(c => c.Id == input.Id && c.BusinessUnitId == businessUnitId);
            if (existing == null)
                throw new KeyNotFoundException(
                    $"Contact with ID {input.Id} not found in Business Unit {businessUnitId}.");
            if (existing.CustomerId != input.CustomerId || existing.SupplierId != input.SupplierId)
                throw new ArgumentException("A contact cannot be reassigned to a different customer or supplier.");

            await ValidateParentAsync(existing.CustomerId, existing.SupplierId, businessUnitId, existing.IsActive != false);
            await ValidateEmailAvailableAsync(input.Email, businessUnitId, input.Id);
            if (existing.IsActive == false)
                input.IsPrimary = false;
            else if (input.IsPrimary == true)
                await ValidatePrimaryAvailableAsync(existing.CustomerId, existing.SupplierId, businessUnitId, input.Id);

            if (existing.CustomerId.HasValue)
            {
                await CustomerIdentityMaintenance.EnsureAuthoritativeValuesAvailableAsync(
                    _context, businessUnitId, existing.CustomerId,
                    [
                        (CustomerIdentifierType.Email, input.Email),
                        (CustomerIdentifierType.Phone, input.PhoneNo),
                        (CustomerIdentifierType.Phone, input.MobileNo)
                    ]);
            }

            _context.Entry(existing).Property(x => x.ConcurrencyToken).OriginalValue = expectedConcurrencyToken;
            CopyEditableValues(input, existing);
            existing.ModifiedBy = actor;
            existing.ModifiedOn = DateTime.UtcNow;
            existing.ConcurrencyToken = Guid.NewGuid();
            await _context.SaveChangesAsync();
            if (existing.CustomerId.HasValue)
            {
                await CustomerIdentityMaintenance.SynchronizeAsync(
                    _context, businessUnitId, existing.CustomerId.Value, "CustomerContact");
                await _context.SaveChangesAsync();
            }
            return existing;
        });
        CopyPersistedValues(persisted, contact);
    }

    public async Task DeleteAsync(
        long id,
        long businessUnitId,
        string actor,
        Guid expectedConcurrencyToken)
    {
        ValidateTenantAndActor(businessUnitId, actor);
        ValidateExpectedToken(expectedConcurrencyToken);
        await ExecuteInTransactionAsync(async () =>
        {
            var contact = await _context.Contacts
                .FirstOrDefaultAsync(c => c.Id == id && c.BusinessUnitId == businessUnitId);
            if (contact == null)
                throw new KeyNotFoundException(
                    $"Contact with ID {id} not found in Business Unit {businessUnitId}.");

            _context.Entry(contact).Property(x => x.ConcurrencyToken).OriginalValue = expectedConcurrencyToken;
            contact.IsActive = false;
            contact.IsPrimary = false;
            contact.ModifiedBy = actor;
            contact.ModifiedOn = DateTime.UtcNow;
            contact.ConcurrencyToken = Guid.NewGuid();
            await _context.SaveChangesAsync();
            if (contact.CustomerId.HasValue)
            {
                await CustomerIdentityMaintenance.SynchronizeAsync(
                    _context, businessUnitId, contact.CustomerId.Value, "CustomerContact");
                await _context.SaveChangesAsync();
            }
            return true;
        });
    }

    public async Task<IEnumerable<CustomerDropdown>> GetCustomersAsync(long businessUnitId) =>
        await _context.Customers
            .AsNoTracking()
            .Where(c => c.Buid == businessUnitId && c.IsActive != false)
            .OrderBy(c => c.Name)
            .ThenBy(c => c.Id)
            .Select(c => new CustomerDropdown { Id = c.Id, Name = c.Name })
            .ToListAsync();

    public async Task<IEnumerable<SupplierDropDown>> GetSuppliersAsync(long businessUnitId) =>
        await _context.Suppliers
            .AsNoTracking()
            .Where(s => s.Buid == businessUnitId && s.IsActive != false)
            .OrderBy(s => s.Name)
            .ThenBy(s => s.Id)
            .Select(s => new SupplierDropDown { Id = s.Id, Name = s.Name })
            .ToListAsync();

    private async Task ValidateParentAsync(
        long? customerId,
        long? supplierId,
        long businessUnitId,
        bool requireActive)
    {
        if (customerId.HasValue == supplierId.HasValue)
            throw new ArgumentException("Contact must be associated with exactly one Customer or Supplier.");

        var exists = customerId.HasValue
            ? await _context.Customers.AnyAsync(c => c.Id == customerId.Value && c.Buid == businessUnitId &&
                (!requireActive || c.IsActive != false))
            : await _context.Suppliers.AnyAsync(s => s.Id == supplierId!.Value && s.Buid == businessUnitId &&
                (!requireActive || s.IsActive != false));
        if (!exists)
            throw new ArgumentException("The contact parent does not exist or is inactive in the authenticated tenant.");
    }

    private async Task ValidateEmailAvailableAsync(string? email, long businessUnitId, long? excludedId)
    {
        if (email is null)
            return;

        var exists = await _context.Contacts.AnyAsync(c =>
            c.BusinessUnitId == businessUnitId && c.Email == email &&
            (!excludedId.HasValue || c.Id != excludedId.Value));
        if (exists)
            throw new ArgumentException("A contact with this email already exists in the authenticated tenant.");
    }

    private async Task ValidatePrimaryAvailableAsync(
        long? customerId,
        long? supplierId,
        long businessUnitId,
        long? excludedId)
    {
        var exists = customerId.HasValue
            ? await _context.Contacts.AnyAsync(c =>
                c.BusinessUnitId == businessUnitId && c.CustomerId == customerId.Value &&
                c.IsPrimary == true && c.IsActive != false && (!excludedId.HasValue || c.Id != excludedId.Value))
            : await _context.Contacts.AnyAsync(c =>
                c.BusinessUnitId == businessUnitId && c.SupplierId == supplierId!.Value &&
                c.IsPrimary == true && c.IsActive != false && (!excludedId.HasValue || c.Id != excludedId.Value));
        if (exists)
            throw new ArgumentException("A primary contact already exists for this parent.");
    }

    private static void ValidateTenantAndActor(long businessUnitId, string actor)
    {
        if (businessUnitId <= 0)
            throw new ArgumentException("An authenticated Business Unit is required.");
        if (string.IsNullOrWhiteSpace(actor))
            throw new ArgumentException("An authenticated actor is required.");
    }

    private static void ValidateExpectedToken(Guid token)
    {
        if (token == Guid.Empty)
            throw new ArgumentException("A concurrency token is required.");
    }

    private async Task<T> ExecuteInTransactionAsync<T>(Func<Task<T>> operation)
    {
        if (_context.Database.CurrentTransaction is not null)
            return await operation();

        var strategy = _context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _context.ChangeTracker.Clear();
            await using var transaction = await _context.Database.BeginTransactionAsync();
            var result = await operation();
            await transaction.CommitAsync();
            return result;
        });
    }

    private static Contact CloneContact(Contact source) => new()
    {
        Id = source.Id,
        CustomerId = source.CustomerId,
        SupplierId = source.SupplierId,
        FirstName = source.FirstName,
        MiddleName = source.MiddleName,
        LastName = source.LastName,
        Email = source.Email,
        PhoneNo = source.PhoneNo,
        MobileNo = source.MobileNo,
        Position = source.Position,
        IsPrimary = source.IsPrimary,
        IsActive = source.IsActive
    };

    private static void CopyEditableValues(Contact source, Contact target)
    {
        target.FirstName = source.FirstName;
        target.MiddleName = source.MiddleName;
        target.LastName = source.LastName;
        target.Email = source.Email;
        target.PhoneNo = source.PhoneNo;
        target.MobileNo = source.MobileNo;
        target.Position = source.Position;
        target.IsPrimary = source.IsPrimary;
    }

    private static void CopyPersistedValues(Contact source, Contact target)
    {
        target.Id = source.Id;
        target.BusinessUnitId = source.BusinessUnitId;
        target.CustomerId = source.CustomerId;
        target.SupplierId = source.SupplierId;
        target.IsPrimary = source.IsPrimary;
        target.IsActive = source.IsActive;
        target.CreatedBy = source.CreatedBy;
        target.CreatedOn = source.CreatedOn;
        target.ModifiedBy = source.ModifiedBy;
        target.ModifiedOn = source.ModifiedOn;
        target.ConcurrencyToken = source.ConcurrencyToken;
    }

    private static void NormalizeAndValidate(Contact contact)
    {
        contact.FirstName = contact.FirstName?.Trim() ?? string.Empty;
        contact.LastName = contact.LastName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(contact.FirstName) || contact.FirstName.Length > 100 ||
            string.IsNullOrWhiteSpace(contact.LastName) || contact.LastName.Length > 100)
        {
            throw new ArgumentException(
                "Contact first and last names are required and cannot exceed 100 characters.");
        }

        contact.MiddleName = NormalizeOptional(contact.MiddleName);
        contact.Email = NormalizeOptional(contact.Email)?.ToLowerInvariant();
        contact.PhoneNo = NormalizeOptional(contact.PhoneNo);
        contact.MobileNo = NormalizeOptional(contact.MobileNo);
        contact.Position = NormalizeOptional(contact.Position);
        if (contact.Email?.Length > 320 ||
            contact.Email is not null && !new EmailAddressAttribute().IsValid(contact.Email))
            throw new ArgumentException("Contact email is invalid.");
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
