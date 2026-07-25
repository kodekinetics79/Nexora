using ERP_RFQ_Automation.DTOs.Contact;
using ERP_RFQ_Automation.DTOs.CustomerDTOs;
using ERP_RFQ_Automation.DTOs.SupplierDTOs;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ERP_RFQ_Automation.Repositories
{
    public class ContactRepository : IContactRepository
    {
        private readonly ErpRfqAutomationContext _context;

        public ContactRepository(ErpRfqAutomationContext context)
        {
            _context = context;
        }

        public async Task<(IEnumerable<ContactResponseDTO>, int TotalCount)> GetAllAsync(int pageNumber, int pageSize, long? id, string? firstName, string? lastName, string? email, long? customerId, long? supplierId, bool? isPrimary, bool? isActive, long businessUnitId)
        {
            var query = _context.Contacts
                .AsNoTracking()
                .Where(contact => contact.BusinessUnitId == businessUnitId)
                .GroupJoin(_context.Customers.Where(c => c.Buid == businessUnitId),
                    contact => contact.CustomerId,
                    customer => customer.Id,
                    (contact, customers) => new { contact, customers })
                .SelectMany(
                    x => x.customers.DefaultIfEmpty(),
                    (contact, customer) => new { contact.contact, customer })
                .GroupJoin(_context.Suppliers.Where(s => s.Buid == businessUnitId),
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

            // Apply filters
            if (id.HasValue)
                query = query.Where(x => x.Contact.Id == id.Value);
            if (!string.IsNullOrWhiteSpace(firstName))
                query = query.Where(x => x.Contact.FirstName.ToLower().Contains(firstName.ToLower()));
            if (!string.IsNullOrWhiteSpace(lastName))
                query = query.Where(x => x.Contact.LastName.ToLower().Contains(lastName.ToLower()));
            if (!string.IsNullOrWhiteSpace(email))
                query = query.Where(x => x.Contact.Email != null && x.Contact.Email.ToLower().Contains(email.ToLower()));
            if (customerId.HasValue)
                query = query.Where(x => x.Contact.CustomerId == customerId.Value);
            if (supplierId.HasValue)
                query = query.Where(x => x.Contact.SupplierId == supplierId.Value);
            if (isPrimary.HasValue)
                query = query.Where(x => x.Contact.IsPrimary == isPrimary.Value);
            if (isActive.HasValue)
                query = query.Where(x => x.Contact.IsActive == isActive.Value);

            // Get total count before pagination
            var totalCount = await query.CountAsync();

            // Apply pagination
            query = query.Skip((pageNumber - 1) * pageSize).Take(pageSize);

            // Project to ContactResponseDTO
            var contacts = await query.Select(x => new ContactResponseDTO
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
                ModifiedOn = x.Contact.ModifiedOn
            }).ToListAsync();

            return (contacts, totalCount);
        }

        public async Task<Contact> GetByIdAsync(long id, long businessUnitId)
        {
            var contact = await _context.Contacts
                .AsNoTracking()
                .Include(c => c.Customer)
                .Include(c => c.Supplier)
                .FirstOrDefaultAsync(c => c.Id == id && c.BusinessUnitId == businessUnitId);

            return contact ?? throw new KeyNotFoundException($"Contact with ID {id} not found in Business Unit {businessUnitId}.");
        }

        public async Task AddAsync(Contact contact)
        {
            if (contact.CustomerId.HasValue == contact.SupplierId.HasValue)
                throw new ArgumentException("Contact must be associated with exactly one Customer or Supplier.");

            // Validate foreign keys
            long? buId = null;
            if (contact.CustomerId.HasValue)
            {
                var customer = await _context.Customers.FirstOrDefaultAsync(c => c.Id == contact.CustomerId.Value);
                if (customer == null)
                    throw new ArgumentException($"Customer with ID {contact.CustomerId.Value} does not exist.");
                buId = customer.Buid;
            }
            if (contact.SupplierId.HasValue)
            {
                var supplier = await _context.Suppliers.FirstOrDefaultAsync(s => s.Id == contact.SupplierId.Value);
                if (supplier == null)
                    throw new ArgumentException($"Supplier with ID {contact.SupplierId.Value} does not exist.");
                if (buId.HasValue && supplier.Buid != buId)
                    throw new ArgumentException("Customer and Supplier must belong to the same Business Unit.");
                buId = supplier.Buid;
            }

            if (!buId.HasValue || buId <= 0)
                throw new ArgumentException("Contact must resolve to a tenant-owned Customer or Supplier.");
            contact.BusinessUnitId = buId.Value;

            // Validate IsPrimary: Only one primary contact per parent
            if (contact.IsPrimary == true)
            {
                bool primaryExists = false;
                if (contact.CustomerId.HasValue)
                    primaryExists = await _context.Contacts.AnyAsync(c => c.CustomerId == contact.CustomerId.Value && c.IsPrimary == true);
                else if (contact.SupplierId.HasValue)
                    primaryExists = await _context.Contacts.AnyAsync(c => c.SupplierId == contact.SupplierId.Value && c.IsPrimary == true);

                if (primaryExists)
                    throw new ArgumentException("A primary contact already exists for this parent. Only one primary contact is allowed.");
            }

            _context.Contacts.Add(contact);
            await _context.SaveChangesAsync();
        }

        public async Task UpdateAsync(Contact contact)
        {
            if (contact.CustomerId.HasValue == contact.SupplierId.HasValue)
                throw new ArgumentException("Contact must be associated with exactly one Customer or Supplier.");

            // Resolve ownership from the selected parent records; the direct tenant column is authoritative.
            long? buId = null;
            if (contact.CustomerId.HasValue)
            {
                var customer = await _context.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == contact.CustomerId.Value);
                if (customer == null)
                    throw new ArgumentException($"Customer with ID {contact.CustomerId.Value} does not exist.");
                buId = customer.Buid;
            }
            if (contact.SupplierId.HasValue)
            {
                var supplier = await _context.Suppliers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == contact.SupplierId.Value);
                if (supplier == null)
                    throw new ArgumentException($"Supplier with ID {contact.SupplierId.Value} does not exist.");
                if (buId.HasValue && supplier.Buid != buId)
                    throw new ArgumentException("Customer and Supplier must belong to the same Business Unit.");
                buId = supplier.Buid;
            }

            if (!buId.HasValue || buId <= 0)
                throw new ArgumentException("Contact must resolve to a tenant-owned Customer or Supplier.");
            if (contact.BusinessUnitId != 0 && contact.BusinessUnitId != buId.Value)
                throw new ArgumentException("Contact cannot be reassigned across Business Units.");
            contact.BusinessUnitId = buId.Value;

            // Resolve the existing row through its direct tenant key before applying reassignment.
            var existing = await _context.Contacts.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == contact.Id && c.BusinessUnitId == buId);
            if (existing == null)
                throw new KeyNotFoundException($"Contact with ID {contact.Id} not found.");

            // Unique email per parent (excluding self)
            if (!string.IsNullOrEmpty(contact.Email))
            {
                bool emailExists = false;
                if (contact.CustomerId.HasValue)
                    emailExists = await _context.Contacts.AnyAsync(con => con.Email == contact.Email && con.CustomerId == contact.CustomerId.Value && con.Id != contact.Id);
                else if (contact.SupplierId.HasValue)
                    emailExists = await _context.Contacts.AnyAsync(con => con.Email == contact.Email && con.SupplierId == contact.SupplierId.Value && con.Id != contact.Id);
                if (emailExists)
                    throw new ArgumentException($"Email {contact.Email} already exists for this parent.");
            }

            // Validate IsPrimary: Only one primary contact per parent (excluding self)
            if (contact.IsPrimary == true)
            {
                bool primaryExists = false;
                if (contact.CustomerId.HasValue)
                    primaryExists = await _context.Contacts.AnyAsync(c => c.CustomerId == contact.CustomerId.Value && c.IsPrimary == true && c.Id != contact.Id);
                else if (contact.SupplierId.HasValue)
                    primaryExists = await _context.Contacts.AnyAsync(c => c.SupplierId == contact.SupplierId.Value && c.IsPrimary == true && c.Id != contact.Id);

                if (primaryExists)
                    throw new ArgumentException("A primary contact already exists for this parent. Only one primary contact is allowed.");
            }

            // Clear navigation properties to prevent EF tracking conflicts
            contact.Customer = null;
            contact.Supplier = null;

            _context.Contacts.Update(contact);
            await _context.SaveChangesAsync();
        }


        public async Task DeleteAsync(long id, long businessUnitId)
        {
            var contact = await GetByIdAsync(id, businessUnitId);
            // No dependency checks assumed (add if needed, e.g., for RFQs)

            _context.Contacts.Remove(contact);
            await _context.SaveChangesAsync();
        }

        public async Task<IEnumerable<CustomerDropdown>> GetCustomersAsync(long businessUnitId)
        {
            return await _context.Customers
                .AsNoTracking()
                .Where(c => c.Buid == businessUnitId)
                .Select(c => new CustomerDropdown
                {
                    Id = c.Id,
                    Name = c.Name
                })
                .ToListAsync();
        }

        public async Task<IEnumerable<SupplierDropDown>> GetSuppliersAsync(long businessUnitId)
        {
            return await _context.Suppliers
                .AsNoTracking()
                .Where(s => s.Buid == businessUnitId)
                .Select(s => new SupplierDropDown
                {
                    Id = s.Id,
                    Name = s.Name
                })
                .ToListAsync();
        }
    }
}
