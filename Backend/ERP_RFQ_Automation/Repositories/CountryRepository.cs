using ERP_RFQ_Automation.DTOs.LocationDTOs;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Repositories
{
    public class CountryRepository : ICountryRepository
    {
        private readonly ErpRfqAutomationContext _context;

        public CountryRepository(ErpRfqAutomationContext context)
        {
            _context = context;
        }

        public async Task<IEnumerable<CountryResponseDTO>> GetAllAsync(long buid)
        {
            return await _context.SetCountries
                .AsNoTracking()
                .Where(c => c.Buid == buid)
                .Select(c => new CountryResponseDTO
                {
                    CountryId = c.CountryId,
                    CountryCode = c.CountryCode,
                    CountryName = c.CountryName,
                    Description = c.Description,
                    Buid = c.Buid,
                    IsActive = c.IsActive,
                    CreatedBy = c.CreatedBy,
                    CreatedDate = c.CreatedDate,
                    ModifiedBy = c.ModifiedBy,
                    ModifiedDate = c.ModifiedDate
                })
                .ToListAsync();
        }

        public async Task<CountryResponseDTO?> GetByIdAsync(int id)
        {
            var c = await _context.SetCountries
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.CountryId == id);

            if (c == null) return null;

            return new CountryResponseDTO
            {
                CountryId = c.CountryId,
                CountryCode = c.CountryCode,
                CountryName = c.CountryName,
                Description = c.Description,
                Buid = c.Buid,
                IsActive = c.IsActive,
                CreatedBy = c.CreatedBy,
                CreatedDate = c.CreatedDate,
                ModifiedBy = c.ModifiedBy,
                ModifiedDate = c.ModifiedDate
            };
        }

        public async Task<CountryResponseDTO> CreateAsync(CountryCreateDTO dto, string userId)
        {
            var country = new SetCountry
            {
                CountryCode = dto.CountryCode,
                CountryName = dto.CountryName,
                Description = dto.Description,
                Buid = dto.Buid,
                IsActive = dto.IsActive,
                CreatedBy = userId,
                CreatedDate = DateTime.UtcNow
            };

            _context.SetCountries.Add(country);
            await _context.SaveChangesAsync();

            return (await GetByIdAsync(country.CountryId))!;
        }

        public async Task<CountryResponseDTO> UpdateAsync(int id, CountryUpdateDTO dto, string userId)
        {
            var country = await _context.SetCountries.FindAsync(id);
            if (country == null) throw new Exception("Country not found");

            country.CountryCode = dto.CountryCode;
            country.CountryName = dto.CountryName;
            country.Description = dto.Description;
            country.Buid = dto.Buid;
            country.IsActive = dto.IsActive;
            country.ModifiedBy = userId;
            country.ModifiedDate = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            return (await GetByIdAsync(id))!;
        }

        public async Task<bool> DeleteAsync(int id)
        {
            var country = await _context.SetCountries.FindAsync(id);
            if (country == null) return false;

            _context.SetCountries.Remove(country);
            await _context.SaveChangesAsync();
            return true;
        }
    }
}
