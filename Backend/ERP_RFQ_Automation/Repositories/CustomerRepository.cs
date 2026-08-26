using System.ComponentModel.DataAnnotations;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.DTOs.CustomerDTOs;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.MasterData;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tax;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Repositories
{
    public class CustomerRepository : ICustomerRepository
    {
        private readonly ErpRfqAutomationContext _context;

        public CustomerRepository(ErpRfqAutomationContext context)
        {
            _context = context;
        }

        public async Task<(IEnumerable<CustomerResponseDTO>, int TotalCount)> GetAllAsync(
            int pageNumber, int pageSize, long? id, string? name, string? contactEmail,
            bool? isActive, string? docId, long businessUnitId, AccountTeamScope scope)
        {
            ArgumentNullException.ThrowIfNull(scope);

            // FR-CST-02. The account filter is applied to the base set, BEFORE the count and before
            // paging, so the total a caller is shown is the total they may read. Counting the whole
            // tenant and then paging a subset is how a scoped list still discloses how many records
            // exist outside the scope.
            var query = _context.Customers
                .AsNoTracking()
                .Where(c => c.Buid == businessUnitId)
                .InAccountScope(_context, businessUnitId, scope, DateTime.UtcNow)
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

            if (id.HasValue)
                query = query.Where(x => x.Customer.Id == id.Value);
            if (!string.IsNullOrWhiteSpace(name))
            {
                var normalizedName = name.Trim().ToLower();
                query = query.Where(x => x.Customer.Name.ToLower().Contains(normalizedName));
            }
            if (!string.IsNullOrWhiteSpace(contactEmail))
            {
                var normalizedEmail = contactEmail.Trim().ToLower();
                query = query.Where(x => x.Customer.ContactEmail != null && x.Customer.ContactEmail.ToLower().Contains(normalizedEmail));
            }
            if (isActive.HasValue)
                query = query.Where(x => x.Customer.IsActive == isActive.Value);
            if (!string.IsNullOrWhiteSpace(docId))
            {
                var normalizedDocId = docId.Trim().ToLower();
                query = query.Where(x => x.Customer.DocId != null && x.Customer.DocId.ToLower().Contains(normalizedDocId));
            }

            // Get total count before pagination
            var totalCount = await query.CountAsync();

            query = query
                .OrderBy(x => x.Customer.Name)
                .ThenBy(x => x.Customer.Id)
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize);

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
                ModifiedOn = x.Customer.ModifiedOn,
                ConcurrencyToken = x.Customer.ConcurrencyToken,
                // FR-CST-01 — the master fields travel with the list row, so the register columns
                // and the "no account team" gap render from data already on the page.
                CommercialRegistrationNumber = x.Customer.CommercialRegistrationNumber,
                TaxRegistrationNumber = x.Customer.TaxRegistrationNumber,
                Sector = x.Customer.Sector,
                RegionStateId = x.Customer.RegionStateId,
                RegionName = x.Customer.RegionState != null ? x.Customer.RegionState.StateName : null,
                AccountTeamId = x.Customer.AccountTeamId,
                AccountTeamName = x.Customer.AccountTeam != null ? x.Customer.AccountTeam.TeamName : null,
                // AA-01: tenant-defined custom field values travel with the list row so a
                // custom-field column renders from data already on the page.
                CustomFields = x.Customer.CustomFieldsJson
            }).ToListAsync();

            return (customers, totalCount);
        }

        public async Task<Customer> GetByIdAsync(long id, long businessUnitId, AccountTeamScope scope)
        {
            ArgumentNullException.ThrowIfNull(scope);

            var customer = await _context.Customers
                .AsNoTracking()
                .Where(c => c.Buid == businessUnitId)
                .InAccountScope(_context, businessUnitId, scope, DateTime.UtcNow)
                .Include(c => c.Bu)
                .Include(c => c.AccountTeam)
                .Include(c => c.RegionState)
                .FirstOrDefaultAsync(c => c.Id == id);

            // Same exception as "no such customer", deliberately: distinguishing "exists but not
            // yours" from "does not exist" confirms the record to a caller who may not read it.
            return customer ?? throw new KeyNotFoundException($"Customer with ID {id} not found in Business Unit {businessUnitId}.");
        }

        public Task AddAsync(Customer customer, long businessUnitId, string actor) =>
            AddInternalAsync(customer, businessUnitId, actor, initialOwnerUserId: null);

        public Task AddOwnedAsync(Customer customer, long businessUnitId, string actor, long ownerUserId)
        {
            if (ownerUserId <= 0)
                throw new ArgumentException("An initial customer owner is required.", nameof(ownerUserId));

            return AddInternalAsync(customer, businessUnitId, actor, ownerUserId);
        }

        private async Task AddInternalAsync(
            Customer customer,
            long businessUnitId,
            string actor,
            long? initialOwnerUserId)
        {
            ValidateTenantAndActor(businessUnitId, actor);
            NormalizeAndValidate(customer);
            var input = CloneCustomer(customer);
            var persisted = await ExecuteInTransactionAsync(async () =>
            {
                var normalizedName = input.Name.ToLower();
                if (await _context.Customers.AnyAsync(c =>
                        c.Buid == businessUnitId && c.Name.ToLower() == normalizedName))
                    throw new ArgumentException("A customer with this name already exists in the authenticated tenant.");
                if (!await _context.BusinessUnits.AnyAsync(bu => bu.Id == businessUnitId))
                    throw new ArgumentException("The authenticated Business Unit does not exist.");
                if (initialOwnerUserId.HasValue && !await _context.Users.AnyAsync(user =>
                        user.Id == initialOwnerUserId.Value
                        && user.Buid == businessUnitId
                        && user.IsActive == true))
                    throw new ArgumentException("The initial customer owner must be an active user in the authenticated tenant.");

                await ValidateTenantReferencesAsync(input, businessUnitId);

                await CustomerIdentityMaintenance.EnsureAuthoritativeValuesAvailableAsync(
                    _context, businessUnitId, null,
                    [(CustomerIdentifierType.Email, input.ContactEmail)]);

                var candidate = CloneCustomer(input);
                candidate.Buid = businessUnitId;
                candidate.DocId = null;
                candidate.IsActive ??= true;
                candidate.CreatedBy = actor;
                candidate.CreatedOn = DateTime.UtcNow;
                candidate.ModifiedBy = null;
                candidate.ModifiedOn = null;
                candidate.ConcurrencyToken = Guid.NewGuid();
                _context.Customers.Add(candidate);
                await _context.SaveChangesAsync();

                if (candidate.Id is <= 0 or > 99_999_999)
                    throw new InvalidOperationException("The customer identity cannot be represented by the configured document number.");

                candidate.DocId = $"CU{candidate.Id:D8}";
                await _context.SaveChangesAsync();
                await CustomerIdentityMaintenance.SynchronizeAsync(
                    _context, businessUnitId, candidate.Id, "CustomerProfile");

                if (initialOwnerUserId.HasValue)
                {
                    _context.Set<CustomerOwnership>().Add(new CustomerOwnership
                    {
                        BusinessUnitId = businessUnitId,
                        CustomerId = candidate.Id,
                        PrimaryUserId = initialOwnerUserId.Value,
                        Scope = OwnershipScope.GeneralCustomer,
                        Priority = 100,
                        EffectiveFrom = candidate.CreatedOn,
                        IsActive = true,
                        Source = "customer-profile-create",
                        Reason = "The scoped creator retains access to the account they created.",
                        Version = 1
                    });
                }

                await _context.SaveChangesAsync();
                return candidate;
            });
            CopyPersistedValues(persisted, customer);
        }

        public async Task UpdateAsync(Customer customer, long businessUnitId, string actor, Guid expectedConcurrencyToken)
        {
            ValidateTenantAndActor(businessUnitId, actor);
            ValidateExpectedToken(expectedConcurrencyToken);
            NormalizeAndValidate(customer);
            var input = CloneCustomer(customer);
            var persisted = await ExecuteInTransactionAsync(async () =>
            {
                var existing = await _context.Customers
                    .FirstOrDefaultAsync(c => c.Id == input.Id && c.Buid == businessUnitId);
                if (existing == null)
                    throw new KeyNotFoundException($"Customer with ID {input.Id} not found in Business Unit {businessUnitId}.");

                var normalizedName = input.Name.ToLower();
                if (await _context.Customers.AnyAsync(c =>
                        c.Buid == businessUnitId && c.Id != input.Id && c.Name.ToLower() == normalizedName))
                    throw new ArgumentException("A customer with this name already exists in the authenticated tenant.");

                await ValidateTenantReferencesAsync(input, businessUnitId);

                await CustomerIdentityMaintenance.EnsureAuthoritativeValuesAvailableAsync(
                    _context, businessUnitId, existing.Id,
                    [(CustomerIdentifierType.Email, input.ContactEmail)]);

                _context.Entry(existing).Property(x => x.ConcurrencyToken).OriginalValue = expectedConcurrencyToken;
                CopyEditableValues(input, existing);
                existing.ModifiedBy = actor;
                existing.ModifiedOn = DateTime.UtcNow;
                existing.ConcurrencyToken = Guid.NewGuid();
                await _context.SaveChangesAsync();
                await CustomerIdentityMaintenance.SynchronizeAsync(
                    _context, businessUnitId, existing.Id, "CustomerProfile");
                await _context.SaveChangesAsync();
                return existing;
            });
            CopyPersistedValues(persisted, customer);
        }

        public async Task DeleteAsync(long id, long businessUnitId, string actor, Guid expectedConcurrencyToken)
        {
            ValidateTenantAndActor(businessUnitId, actor);
            ValidateExpectedToken(expectedConcurrencyToken);
            await ExecuteInTransactionAsync(async () =>
            {
                var customer = await _context.Customers
                    .FirstOrDefaultAsync(c => c.Id == id && c.Buid == businessUnitId);
                if (customer == null)
                    throw new KeyNotFoundException($"Customer with ID {id} not found in Business Unit {businessUnitId}.");

                _context.Entry(customer).Property(x => x.ConcurrencyToken).OriginalValue = expectedConcurrencyToken;
                var changedOn = DateTime.UtcNow;
                customer.IsActive = false;
                customer.ModifiedBy = actor;
                customer.ModifiedOn = changedOn;
                customer.ConcurrencyToken = Guid.NewGuid();

                var contacts = await _context.Contacts
                    .Where(c => c.BusinessUnitId == businessUnitId && c.CustomerId == id && c.IsActive != false)
                    .ToListAsync();
                foreach (var contact in contacts)
                {
                    contact.IsActive = false;
                    contact.IsPrimary = false;
                    contact.ModifiedBy = actor;
                    contact.ModifiedOn = changedOn;
                    contact.ConcurrencyToken = Guid.NewGuid();
                }

                await _context.SaveChangesAsync();
                await CustomerIdentityMaintenance.SynchronizeAsync(
                    _context, businessUnitId, customer.Id, "CustomerProfile");
                await _context.SaveChangesAsync();
                return true;
            });
        }

        public async Task<Customer?> GetByEmailAsync(string email, long businessUnitId, AccountTeamScope scope)
        {
            ArgumentNullException.ThrowIfNull(scope);
            var lowerEmail = email.Trim().ToLowerInvariant();
            return await _context.Customers
                .AsNoTracking()
                .Where(c => c.Buid == businessUnitId)
                .InAccountScope(_context, businessUnitId, scope, DateTime.UtcNow)
                .Include(c => c.Bu)
                .Include(c => c.AccountTeam)
                .Include(c => c.RegionState)
                .FirstOrDefaultAsync(c => c.ContactEmail != null && c.ContactEmail.ToLower() == lowerEmail);
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

        private static void NormalizeAndValidate(Customer customer)
        {
            customer.Name = customer.Name?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(customer.Name) || customer.Name.Length > 255)
                throw new ArgumentException("Customer name is required and cannot exceed 255 characters.");

            customer.ContactEmail = NormalizeOptional(customer.ContactEmail)?.ToLowerInvariant();
            if (customer.ContactEmail?.Length > 320 ||
                customer.ContactEmail is not null && !new EmailAddressAttribute().IsValid(customer.ContactEmail))
                throw new ArgumentException("Customer email is invalid.");

            customer.ImageUrl = NormalizeOptional(customer.ImageUrl) ?? string.Empty;
            if (customer.ImageUrl.Length > 100)
                throw new ArgumentException("Customer image URL cannot exceed 100 characters.");

            // ── FR-CST-01 master fields ──────────────────────────────────────
            // Canonicalised here as well as at the API edge: the DTO attributes cover the single
            // record controller, and this covers every other write path (importers, seeders, the
            // identity-maintenance sync), which is checklist item "bulk and import paths write it
            // too". The validators are the SHARED definitions — TaxRegistrationNumbers is the same
            // type Supplier and BusinessUnit use, not a second copy of the KSA rule.
            if (!CommercialRegistrationNumbers.TryCanonicalize(
                    customer.CommercialRegistrationNumber, "Commercial registration number",
                    out var commercialRegistration, out var commercialRegistrationError))
                throw new ArgumentException(commercialRegistrationError);
            customer.CommercialRegistrationNumber = commercialRegistration;

            if (!TaxRegistrationNumbers.TryCanonicalize(
                    customer.TaxRegistrationNumber, "VAT registration number",
                    out var taxRegistration, out var taxRegistrationError))
                throw new ArgumentException(taxRegistrationError);
            customer.TaxRegistrationNumber = taxRegistration;

            if (!CustomerSectors.TryCanonicalize(customer.Sector, out var sector, out var sectorError))
                throw new ArgumentException(sectorError);
            customer.Sector = sector;

            // Zero is not a key. Rejecting it rather than storing it is checklist item
            // "validation rejects the wrong values, not merely the impossible": a 0 would satisfy
            // "not null" and then match no team and no region for the rest of the record's life.
            if (customer.AccountTeamId is <= 0)
                throw new ArgumentException("The account team is not a valid team.");
            if (customer.RegionStateId is <= 0)
                throw new ArgumentException("The region is not a valid region.");
        }

        /// <summary>
        /// The two foreign keys must point inside the caller's own tenant. Neither database
        /// constraint can say so — both are single-column keys onto tables whose own tenant column
        /// the constraint cannot see — so this is the only place the boundary is enforced, and it
        /// runs on every write path because it sits inside the repository rather than the controller.
        /// </summary>
        private async Task ValidateTenantReferencesAsync(Customer customer, long businessUnitId)
        {
            if (customer.AccountTeamId is long accountTeamId &&
                !await _context.Teams.AnyAsync(t => t.Id == accountTeamId && t.BusinessUnitId == businessUnitId))
                throw new ArgumentException("The account team does not exist in the authenticated tenant.");

            if (customer.RegionStateId is int regionStateId &&
                !await _context.SetStates.AnyAsync(s => s.StateId == regionStateId && s.Buid == businessUnitId && s.IsActive))
                throw new ArgumentException(
                    "The region does not exist in this tenant's region master, or is inactive. " +
                    "Regions are maintained under Setup so that routing, delivery and reporting " +
                    "all read the same list.");
        }

        private static string? NormalizeOptional(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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

        private static Customer CloneCustomer(Customer source) => new()
        {
            Id = source.Id,
            Name = source.Name,
            ContactEmail = source.ContactEmail,
            ImageUrl = source.ImageUrl,
            BillingAddressLine1 = source.BillingAddressLine1,
            BillingAddressLine2 = source.BillingAddressLine2,
            BillingCity = source.BillingCity,
            BillingState = source.BillingState,
            BillingCountry = source.BillingCountry,
            BillingPostalCode = source.BillingPostalCode,
            ShippingAddressLine1 = source.ShippingAddressLine1,
            ShippingAddressLine2 = source.ShippingAddressLine2,
            ShippingCity = source.ShippingCity,
            ShippingState = source.ShippingState,
            ShippingCountry = source.ShippingCountry,
            ShippingPostalCode = source.ShippingPostalCode,
            IsActive = source.IsActive,
            CommercialRegistrationNumber = source.CommercialRegistrationNumber,
            TaxRegistrationNumber = source.TaxRegistrationNumber,
            Sector = source.Sector,
            RegionStateId = source.RegionStateId,
            AccountTeamId = source.AccountTeamId
        };

        private static void CopyEditableValues(Customer source, Customer target)
        {
            target.CommercialRegistrationNumber = source.CommercialRegistrationNumber;
            target.TaxRegistrationNumber = source.TaxRegistrationNumber;
            target.Sector = source.Sector;
            target.RegionStateId = source.RegionStateId;
            target.AccountTeamId = source.AccountTeamId;
            target.Name = source.Name;
            target.ContactEmail = source.ContactEmail;
            target.ImageUrl = source.ImageUrl;
            target.BillingAddressLine1 = source.BillingAddressLine1;
            target.BillingAddressLine2 = source.BillingAddressLine2;
            target.BillingCity = source.BillingCity;
            target.BillingState = source.BillingState;
            target.BillingCountry = source.BillingCountry;
            target.BillingPostalCode = source.BillingPostalCode;
            target.ShippingAddressLine1 = source.ShippingAddressLine1;
            target.ShippingAddressLine2 = source.ShippingAddressLine2;
            target.ShippingCity = source.ShippingCity;
            target.ShippingState = source.ShippingState;
            target.ShippingCountry = source.ShippingCountry;
            target.ShippingPostalCode = source.ShippingPostalCode;
        }

        private static void CopyPersistedValues(Customer source, Customer target)
        {
            target.Id = source.Id;
            target.Buid = source.Buid;
            target.DocId = source.DocId;
            target.IsActive = source.IsActive;
            target.CreatedBy = source.CreatedBy;
            target.CreatedOn = source.CreatedOn;
            target.ModifiedBy = source.ModifiedBy;
            target.ModifiedOn = source.ModifiedOn;
            target.ConcurrencyToken = source.ConcurrencyToken;
            target.CommercialRegistrationNumber = source.CommercialRegistrationNumber;
            target.TaxRegistrationNumber = source.TaxRegistrationNumber;
            target.Sector = source.Sector;
            target.RegionStateId = source.RegionStateId;
            target.AccountTeamId = source.AccountTeamId;
        }

    }
}
