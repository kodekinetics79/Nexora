using ERP_RFQ_Automation.DTOs.BusinessUnit;
using ERP_RFQ_Automation.DTOs.CurrencyDTOs;
using ERP_RFQ_Automation.DTOs.CustomerDTOs;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ERP_RFQ_Automation.Repositories
{
    public class CustomerRepository : ICustomerRepository
    {
        private readonly ErpRfqAutomationContext _context;

        public CustomerRepository(ErpRfqAutomationContext context)
        {
            _context = context;
        }

        public async Task<(IEnumerable<CustomerResponseDTO>, int TotalCount)> GetAllAsync(int pageNumber, int pageSize, long? id, string? name, string? contactEmail, bool? isActive, string? docId, long businessUnitId)
        {
            var query = _context.Customers
                .AsNoTracking()
                .Where(c => c.Buid == businessUnitId)
                .GroupJoin(
                    _context.BusinessUnits,
                    customer => customer.Buid,
                    bu => bu.Id,
                    (customer, bus) => new { customer, bus }
                )
                .SelectMany(
                    x => x.bus.DefaultIfEmpty(),
                    (x, bu) => new
                    {
                        Customer = x.customer,
                        BusinessUnitName = bu != null ? bu.BusinessUnitName : null
                    });

            // Apply filters
            if (id.HasValue)
                query = query.Where(x => x.Customer.Id == id.Value);
            if (!string.IsNullOrWhiteSpace(name))
                query = query.Where(x => x.Customer.Name.ToLower().Contains(name.ToLower()));
            if (!string.IsNullOrWhiteSpace(contactEmail))
                query = query.Where(x => x.Customer.ContactEmail != null && x.Customer.ContactEmail.ToLower().Contains(contactEmail.ToLower()));
            
            if (isActive.HasValue)
                query = query.Where(x => x.Customer.IsActive == isActive.Value);
            if (!string.IsNullOrWhiteSpace(docId))
                query = query.Where(x => x.Customer.DocId != null && x.Customer.DocId.ToLower().Contains(docId.ToLower()));

            // Get total count before pagination
            var totalCount = await query.CountAsync();

            // Apply pagination
            query = query.Skip((pageNumber - 1) * pageSize).Take(pageSize);

            // Project to CustomerResponseDTO
            var customers = await query.Select(x => new CustomerResponseDTO
            {
                Id = x.Customer.Id,
                Name = x.Customer.Name,
                ContactEmail = x.Customer.ContactEmail,
                ImageUrl = x.Customer.ImageUrl,
                BillingAddressLine1 = x.Customer.BillingAddressLine1,
                BillingAddressLine2 = x.Customer.BillingAddressLine2,
                BillingCity = x.Customer.BillingCity,
                BillingState = x.Customer.BillingState,
                BillingCountry = x.Customer.BillingCountry,
                BillingPostalCode = x.Customer.BillingPostalCode,
                ShippingAddressLine1 = x.Customer.ShippingAddressLine1,
                ShippingAddressLine2 = x.Customer.ShippingAddressLine2,
                ShippingCity = x.Customer.ShippingCity,
                ShippingState = x.Customer.ShippingState,
                ShippingCountry = x.Customer.ShippingCountry,
                ShippingPostalCode = x.Customer.ShippingPostalCode,
                Buid = x.Customer.Buid,
                DocId = x.Customer.DocId,
                BusinessUnitName = x.BusinessUnitName,
                IsActive = x.Customer.IsActive,
                CreatedBy = x.Customer.CreatedBy,
                CreatedOn = x.Customer.CreatedOn,
                ModifiedBy = x.Customer.ModifiedBy,
                ModifiedOn = x.Customer.ModifiedOn
            }).ToListAsync();

            return (customers, totalCount);
        }
        public async Task<Customer> GetByIdAsync(long id, long businessUnitId)
        {
            var customer = await _context.Customers
                .AsNoTracking()
                .Include(c => c.Bu)
                .FirstOrDefaultAsync(c => c.Id == id && c.Buid == businessUnitId);

            return customer ?? throw new KeyNotFoundException($"Customer with ID {id} not found in Business Unit {businessUnitId}.");
        }

        public async Task AddAsync(Customer customer)
        {
            // Validate unique name within same BusinessUnit
            var nameExists = await _context.Customers.AnyAsync(c => c.Name == customer.Name && c.Buid == customer.Buid);
            if (nameExists)
                throw new ArgumentException($"Name {customer.Name} already exists in this Business Unit.");


            if (customer.Buid.HasValue)
            {
                var buExists = await _context.BusinessUnits.AnyAsync(bu => bu.Id == customer.Buid.Value);
                if (!buExists)
                    throw new ArgumentException($"Business Unit with ID {customer.Buid.Value} does not exist.");
            }
            var maxDoc = await _context.Customers
        .Where(c => c.DocId != null && c.DocId.StartsWith("CU"))
        .Select(c => c.DocId)
        .MaxAsync();

            long nextNum = 1;
            if (maxDoc != null)
            {
                nextNum = long.Parse(maxDoc.Substring(2)) + 1;
            }
            customer.DocId = "CU" + nextNum.ToString("D8");

            _context.Customers.Add(customer);
            await _context.SaveChangesAsync();
        }

        public async Task UpdateAsync(Customer customer, long businessUnitId)
        {
            var existing = await _context.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == customer.Id && c.Buid == businessUnitId);
            if (existing == null)
                throw new KeyNotFoundException($"Customer with ID {customer.Id} not found in Business Unit {businessUnitId}.");

            if (customer.Buid != businessUnitId)
                throw new ArgumentException("Provided Customer Business Unit does not match context.");

            // Validate unique name within same BusinessUnit (excluding current customer)
            var nameExists = await _context.Customers.AnyAsync(c => c.Name == customer.Name && c.Buid == businessUnitId && c.Id != customer.Id);
            if (nameExists)
                throw new ArgumentException($"Name {customer.Name} already exists in this Business Unit.");

           
            if (customer.Buid.HasValue)
            {
                var buExists = await _context.BusinessUnits.AnyAsync(bu => bu.Id == customer.Buid.Value);
                if (!buExists)
                    throw new ArgumentException($"Business Unit with ID {customer.Buid.Value} does not exist.");
            }
            // Preserve existing DocId (do not allow change)
            var existingDocId = await _context.Customers
                .Where(c => c.Id == customer.Id)
                .Select(c => c.DocId)
                .FirstOrDefaultAsync();
            customer.DocId = existingDocId;

            _context.Customers.Update(customer);
            await _context.SaveChangesAsync();
        }

        public async Task DeleteAsync(long id, long businessUnitId)
        {
            var customer = await GetByIdAsync(id, businessUnitId);

            // Check for dependent contacts
            var hasContacts = await _context.Contacts.AnyAsync(con => con.CustomerId == id);  // Assume Contact has CustomerId
            if (hasContacts)
                throw new InvalidOperationException($"Cannot delete Customer with ID {id} because they have associated contacts.");

            _context.Customers.Remove(customer);
            await _context.SaveChangesAsync();
        }

        public async Task<Customer?> GetByEmailAsync(string email, long businessUnitId)
        {
            var lowerEmail = email.Trim().ToLowerInvariant();
            return await _context.Customers
                .AsNoTracking()
                .Include(c => c.Bu)
                .FirstOrDefaultAsync(c => c.ContactEmail != null && c.ContactEmail.ToLower() == lowerEmail && c.Buid == businessUnitId);
        }

    }
}