using ERP_RFQ_Automation.DTOs.GeneralDropdown;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
namespace ERP_RFQ_Automation.Repositories
{
    public class GeneralDropdownRepository : IGeneralDropdownRepository
    {
        private readonly ErpRfqAutomationContext _context;
        public GeneralDropdownRepository(ErpRfqAutomationContext context)
        {
            _context = context;
        }
        public async Task<IEnumerable<GeneralDropdownDto>> GetCountriesAsync(long businessUnitId)
        {
            return await _context.SetCountries
                .Where(c => c.Buid == businessUnitId && c.IsActive)
                .OrderBy(c => c.CountryName)
                .Select(c => new GeneralDropdownDto
                {
                    Id = c.CountryId,
                    Code = c.CountryCode,
                    Value = c.CountryName
                })
                .AsNoTracking()
                .ToListAsync();
        }
        public async Task<IEnumerable<GeneralDropdownDto>> GetStatesAsync(long businessUnitId, int? countryId = null)
        {
            var query = _context.SetStates.Where(s => s.Buid == businessUnitId && s.IsActive);
            
            if (countryId.HasValue)
                query = query.Where(s => s.CountryId == countryId.Value);

            return await query
                .OrderBy(s => s.StateName)
                .Select(s => new GeneralDropdownDto
                {
                    Id = s.StateId,
                    Code = s.StateCode,
                    Value = s.StateName
                })
                .AsNoTracking()
                .ToListAsync();
        }
        public async Task<IEnumerable<GeneralDropdownDto>> GetCitiesAsync(long businessUnitId, int? stateId = null, int? countryId = null)
        {
            var query = _context.SetCities.Where(c => c.Buid == businessUnitId && c.IsActive);

            if (stateId.HasValue)
                query = query.Where(c => c.StateId == stateId.Value);
            
            if (countryId.HasValue)
                query = query.Where(c => c.CountryId == countryId.Value);

            return await query
                .OrderBy(c => c.CityName)
                .Select(c => new GeneralDropdownDto
                {
                    Id = c.CityId,
                    Code = string.Empty,
                    Value = c.CityName
                })
                .AsNoTracking()
                .ToListAsync();
        }
        public async Task<IEnumerable<GeneralDropdownDto>> GetCategoriesAsync(long businessUnitId)
        {
            return await _context.ProductCategories
                .Where(c => c.BusinessUnitId == businessUnitId
                         && c.IsActive == true)
                .OrderBy(c => c.CategoryName)
                .Select(c => new GeneralDropdownDto
                {
                    Id = c.Id,
                    Code = string.Empty,
                    Value = c.CategoryName
                })
                .AsNoTracking()
                .ToListAsync();
        }
        public async Task<IEnumerable<GeneralDropdownDto>> GetWarehousesAsync(long businessUnitId)
        {
            return await _context.Warehouses
                .Where(w => w.BusinessUnitId == businessUnitId
                         && w.IsActive == true)
                .OrderBy(w => w.WarehouseName)
                .Select(w => new GeneralDropdownDto
                {
                    Id = w.Id,
                    Code = w.WarehouseCode ?? string.Empty,
                    Value = w.WarehouseName
                })
                .AsNoTracking()
                .ToListAsync();
        }
        public async Task<IEnumerable<GeneralDropdownDto>> GetSuppliersAsync(long businessUnitId)
        {
            return await _context.Suppliers
                .Where(s => s.Buid == businessUnitId
                         && s.IsActive == true)
                .OrderBy(s => s.Name)
                .Select(s => new GeneralDropdownDto
                {
                    Id = s.Id,
                    Code = string.Empty,
                    Value = s.Name
                })
                .AsNoTracking()
                .ToListAsync();
        }
        public async Task<IEnumerable<GeneralDropdownDto>> GetStatusesAsync(long businessUnitId)
        {
            return await _context.SetupMasters
                .Where(s => s.SetupType.Contains("Status")
                         && s.IsActive == true)
                .OrderBy(s => s.SetupValue)
                .Select(s => new GeneralDropdownDto
                {
                    Id = s.SetupId,
                    Code = string.Empty,
                    Value = s.SetupValue
                })
                .AsNoTracking()
                .ToListAsync();
        }
       
    }
}