using ERP_RFQ_Automation.DTOs.BusinessUnit;
using ERP_RFQ_Automation.DTOs.CurrencyDTOs;
using ERP_RFQ_Automation.DTOs.SupplierDTOs;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Repositories
{
    public class SupplierRepository : ISupplierRepository
    {
        private readonly ErpRfqAutomationContext _context;
        private readonly ISupplierNumberGenerator _numberGenerator;

        public SupplierRepository(ErpRfqAutomationContext context)
            : this(context, new DeterministicSupplierNumberGenerator())
        {
        }

        public SupplierRepository(
            ErpRfqAutomationContext context,
            ISupplierNumberGenerator numberGenerator)
        {
            _context = context;
            _numberGenerator = numberGenerator;
        }

        // Update to SupplierRepository - GetAllAsync signature and implementation
        public async Task<(IEnumerable<SupplierResponseDTO>, int TotalCount)> GetAllAsync(int pageNumber, int pageSize, long? id, string? name, string? contactEmail, long? currencyId, bool? isActive, string? docId, long businessUnitId)
        {
            var query = _context.Suppliers
                .AsNoTracking()
                .Where(s => s.Buid == businessUnitId)
                .GroupJoin(_context.SetCities,
                    s => s.CityId,
                    c => c.CityId,
                    (s, cities) => new { s, cities })
                .SelectMany(
                    x => x.cities.DefaultIfEmpty(),
                    (x, city) => new { x.s, city })
                .GroupJoin(_context.SetCountries,
                    x => x.s.CountryId,
                    ct => ct.CountryId,
                    (x, countries) => new { x.s, x.city, countries })
                .SelectMany(
                    x => x.countries.DefaultIfEmpty(),
                    (x, country) => new { x.s, x.city, country })
                .GroupJoin(_context.Currencies,
                    x => x.s.CurrencyId,
                    currency => currency.Id,
                    (x, currencies) => new { x.s, x.city, x.country, currencies })
                .SelectMany(
                    x => x.currencies.DefaultIfEmpty(),
                    (x, currency) => new { x.s, x.city, x.country, currency })
                .GroupJoin(_context.BusinessUnits,
                    x => x.s.Buid,
                    bu => bu.Id,
                    (x, bus) => new { x.s, x.city, x.country, x.currency, bus })
                .SelectMany(
                    x => x.bus.DefaultIfEmpty(),
                    (x, bu) => new
                    {
                        Supplier = x.s,
                        CityName = x.city != null ? x.city.CityName : null,
                        CountryName = x.country != null ? x.country.CountryName : null,
                        CurrencyName = x.currency != null ? x.currency.CurrencyName : null,
                        BusinessUnitName = bu != null ? bu.BusinessUnitName : null
                    });
            // Apply filters
            if (id.HasValue)
                query = query.Where(x => x.Supplier.Id == id.Value);
            if (!string.IsNullOrWhiteSpace(name))
                query = query.Where(x => x.Supplier.Name.ToLower().Contains(name.ToLower()));
            if (!string.IsNullOrWhiteSpace(contactEmail))
                query = query.Where(x => x.Supplier.ContactEmail != null && x.Supplier.ContactEmail.ToLower().Contains(contactEmail.ToLower()));
            if (currencyId.HasValue)
                query = query.Where(x => x.Supplier.CurrencyId == currencyId.Value);
            if (isActive.HasValue)
                query = query.Where(x => x.Supplier.IsActive == isActive.Value);
            if (!string.IsNullOrWhiteSpace(docId))
                query = query.Where(x => x.Supplier.DocId != null && x.Supplier.DocId.ToLower().Contains(docId.ToLower()));
            // Get total count before pagination
            var totalCount = await query.CountAsync();
            // Apply pagination
            query = query.OrderBy(x => x.Supplier.Name).ThenBy(x => x.Supplier.Id)
                .Skip((pageNumber - 1) * pageSize).Take(pageSize);
            // Project to SupplierResponseDTO
            var suppliers = await query.Select(x => new SupplierResponseDTO
            {
                Id = x.Supplier.Id,
                DocId = x.Supplier.DocId,
                Name = x.Supplier.Name,
                ContactEmail = x.Supplier.ContactEmail,
                ImageUrl = x.Supplier.ImageUrl,
                PaymentTerms = x.Supplier.PaymentTerms,
                AddressLine1 = x.Supplier.AddressLine1,
                AddressLine2 = x.Supplier.AddressLine2,
                CityId = x.Supplier.CityId,
                CityName = x.CityName,
                CountryId = x.Supplier.CountryId,
                CountryName = x.CountryName,
                PostalCode = x.Supplier.PostalCode,
                SuccessRate = x.Supplier.SuccessRate,
                AvgResponseTime = x.Supplier.AvgResponseTime,
                Tags = x.Supplier.Tags,
                Comments = x.Supplier.Comments,
                CurrencyId = x.Supplier.CurrencyId,
                CurrencyName = x.CurrencyName,
                Buid = x.Supplier.Buid,
                BusinessUnitName = x.BusinessUnitName,
                IsActive = x.Supplier.IsActive,
                GovernanceStatus = x.Supplier.GovernanceStatus,
                VerificationStatus = x.Supplier.VerificationStatus,
                ComplianceStatus = x.Supplier.ComplianceStatus,
                RiskStatus = x.Supplier.RiskStatus,
                ReadinessStatus = x.Supplier.ReadinessStatus,
                EffectiveFrom = x.Supplier.EffectiveFrom,
                GovernanceReviewedBy = x.Supplier.GovernanceReviewedBy,
                GovernanceReviewedOn = x.Supplier.GovernanceReviewedOn,
                ConcurrencyToken = x.Supplier.ConcurrencyToken,
                CreatedBy = x.Supplier.CreatedBy,
                CreatedOn = x.Supplier.CreatedOn,
                ModifiedBy = x.Supplier.ModifiedBy,
                ModifiedOn = x.Supplier.ModifiedOn
            }).ToListAsync();
            return (suppliers, totalCount);
        }

        public async Task<Supplier> GetByIdAsync(long id, long businessUnitId)
        {
            var supplier = await _context.Suppliers
                .AsNoTracking()
                .Include(s => s.Currency)
                .Include(s => s.Bu)
                .Include(s => s.City)
                .Include(s => s.Country)
                .FirstOrDefaultAsync(s => s.Id == id && s.Buid == businessUnitId);

            return supplier ?? throw new KeyNotFoundException($"Supplier with ID {id} not found in Business Unit {businessUnitId}.");
        }

        public async Task AddAsync(Supplier supplier)
        {
            if (supplier.Buid is null or <= 0)
                throw new ArgumentException("Supplier must belong to an authenticated Business Unit.");

            await using var transaction = _context.Database.IsRelational()
                && _context.Database.CurrentTransaction is null
                ? await _context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable)
                : null;

            // Validate unique name within same BusinessUnit
            var normalizedName = supplier.Name.Trim();
            var nameExists = await _context.Suppliers.AnyAsync(s => s.Name.ToLower() == normalizedName.ToLower() && s.Buid == supplier.Buid);
            if (nameExists)
                throw new ArgumentException($"Name {normalizedName} already exists in this Business Unit.");

            supplier.Name = normalizedName;

            // Validate foreign keys
            if (supplier.CurrencyId.HasValue)
            {
                var currencyExists = await _context.Currencies.AnyAsync(cur =>
                    cur.Id == supplier.CurrencyId.Value && cur.BusinessUnitId == supplier.Buid.Value);
                if (!currencyExists)
                    throw new ArgumentException($"Currency with ID {supplier.CurrencyId.Value} does not exist.");
            }
            if (supplier.Buid.HasValue)
            {
                var buExists = await _context.BusinessUnits.AnyAsync(bu => bu.Id == supplier.Buid.Value);
                if (!buExists)
                    throw new ArgumentException($"Business Unit with ID {supplier.Buid.Value} does not exist.");
            }

            supplier.DocId = null;
            supplier.GovernanceStatus = SupplierGovernanceStatuses.Unverified;
            supplier.VerificationStatus = SupplierGovernanceUnknown.Unknown;
            supplier.ComplianceStatus = SupplierGovernanceUnknown.Unknown;
            supplier.RiskStatus = SupplierGovernanceUnknown.Unknown;
            supplier.ReadinessStatus = SupplierReadinessStatuses.ReviewRequired;
            supplier.EffectiveFrom = supplier.CreatedOn;
            supplier.ConcurrencyToken = Guid.NewGuid();

            try
            {
                _context.Suppliers.Add(supplier);
                await _context.SaveChangesAsync();
                supplier.DocId = _numberGenerator.Generate(supplier.Id, supplier.Buid.Value);
                await _context.SaveChangesAsync();

                if (transaction is not null)
                    await transaction.CommitAsync();
            }
            catch
            {
                if (transaction is not null)
                    await transaction.RollbackAsync();
                throw;
            }
        }

        public async Task UpdateAsync(Supplier supplier, long businessUnitId)
        {
            await using var transaction = _context.Database.IsRelational()
                && _context.Database.CurrentTransaction is null
                ? await _context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable)
                : null;
            var existing = await _context.Suppliers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == supplier.Id && s.Buid == businessUnitId);
            if (existing == null)
                throw new KeyNotFoundException($"Supplier with ID {supplier.Id} not found in Business Unit {businessUnitId}.");

            if (supplier.Buid != businessUnitId)
                throw new ArgumentException("Provided Supplier Business Unit does not match context.");

            // Validate unique name within same BusinessUnit (excluding current supplier)
            var normalizedName = supplier.Name.Trim();
            var nameExists = await _context.Suppliers.AnyAsync(s => s.Name.ToLower() == normalizedName.ToLower() && s.Buid == businessUnitId && s.Id != supplier.Id);
            if (nameExists)
                throw new ArgumentException($"Name {normalizedName} already exists in this Business Unit.");

            supplier.Name = normalizedName;

            // Validate foreign keys
            if (supplier.CurrencyId.HasValue)
            {
                var currencyExists = await _context.Currencies.AnyAsync(cur =>
                    cur.Id == supplier.CurrencyId.Value && cur.BusinessUnitId == businessUnitId);
                if (!currencyExists)
                    throw new ArgumentException($"Currency with ID {supplier.CurrencyId.Value} does not exist.");
            }
            if (supplier.Buid.HasValue)
            {
                var buExists = await _context.BusinessUnits.AnyAsync(bu => bu.Id == supplier.Buid.Value);
                if (!buExists)
                    throw new ArgumentException($"Business Unit with ID {supplier.Buid.Value} does not exist.");
            }
            // Preserve existing DocId (do not allow change)
            var existingDocId = await _context.Suppliers
                .Where(s => s.Id == supplier.Id)
                .Select(s => s.DocId)
                .FirstOrDefaultAsync();
            supplier.DocId = existingDocId;

            var expectedConcurrencyToken = supplier.ConcurrencyToken;
            var entry = _context.Suppliers.Attach(supplier);
            if (expectedConcurrencyToken.HasValue)
                entry.Property(s => s.ConcurrencyToken).OriginalValue = expectedConcurrencyToken;
            supplier.ConcurrencyToken = Guid.NewGuid();
            entry.State = EntityState.Modified;
            await _context.SaveChangesAsync();
            if (transaction is not null)
                await transaction.CommitAsync();
        }

        public async Task DeleteAsync(long id, long businessUnitId)
        {
            _ = await GetByIdAsync(id, businessUnitId);
            throw new InvalidOperationException(
                "Supplier records preserve commercial lineage and cannot be deleted. Use the governed inactive decision instead.");
        }

        public async Task<List<SupplierSearchResultDTO>> SearchSuppliersAsync(string? searchTerm, string? productCategory, long businessUnitId)
        {
            var query = _context.Suppliers
                .AsNoTracking()
                .Where(s => s.Buid == businessUnitId && s.IsActive == true
                    && s.VerificationStatus == SupplierVerificationStatuses.Verified
                    && s.ComplianceStatus == SupplierComplianceStatuses.Cleared
                    && (s.RiskStatus == SupplierRiskStatuses.Low || s.RiskStatus == SupplierRiskStatuses.Medium)
                    && s.ReadinessStatus == SupplierReadinessStatuses.Ready
                    && (s.GovernanceStatus == SupplierGovernanceStatuses.Approved
                        || s.GovernanceStatus == SupplierGovernanceStatuses.Preferred
                        || s.GovernanceStatus == SupplierGovernanceStatuses.Provisional))
                .Include(s => s.City)
                .Include(s => s.Country)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(searchTerm))
            {
                searchTerm = searchTerm.ToLower();
                query = query.Where(s =>
                    s.Name.ToLower().Contains(searchTerm) ||
                    (s.ContactEmail != null && s.ContactEmail.ToLower().Contains(searchTerm)) ||
                    (s.Tags != null && s.Tags.ToLower().Contains(searchTerm))
                );
            }

            if (!string.IsNullOrWhiteSpace(productCategory))
            {
                // Filter by product category tags
                query = query.Where(s => s.Tags != null && s.Tags.ToLower().Contains(productCategory.ToLower()));
            }

            var suppliers = await query.OrderBy(s => s.Name).ThenBy(s => s.Id)
                .Take(20)
                .Select(s => new SupplierSearchResultDTO
                {
                    Id = s.Id,
                    Name = s.Name,
                    ContactEmail = s.ContactEmail,
                    AddressLine1 = s.AddressLine1,
                    AddressLine2 = s.AddressLine2,
                    City = s.City != null ? s.City.CityName : null,
                    Country = s.Country != null ? s.Country.CountryName : null,
                    SuccessRate = s.SuccessRate,
                    AvgResponseTime = s.AvgResponseTime,
                    Tags = s.Tags,
                    GovernanceStatus = s.GovernanceStatus,
                    VerificationStatus = s.VerificationStatus,
                    ComplianceStatus = s.ComplianceStatus,
                    RiskStatus = s.RiskStatus,
                    ReadinessStatus = s.ReadinessStatus
                })
                .ToListAsync();

            return suppliers;
        }

    }
}
