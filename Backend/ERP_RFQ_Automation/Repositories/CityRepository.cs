using ERP_RFQ_Automation.DTOs.LocationDTOs;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Repositories
{
    public class CityRepository : ICityRepository
    {
        private readonly ErpRfqAutomationContext _context;

        public CityRepository(ErpRfqAutomationContext context)
        {
            _context = context;
        }

        public async Task<IEnumerable<CityResponseDTO>> GetAllAsync(long buid)
        {
            return await _context.SetCities
                .AsNoTracking()
                .Include(c => c.State)
                .Include(c => c.Country)
                .Where(c => c.Buid == buid)
                .Select(c => new CityResponseDTO
                {
                    CityId = c.CityId,
                    CityName = c.CityName,
                    StateId = c.StateId,
                    StateName = c.State.StateName,
                    CountryId = c.CountryId,
                    CountryName = c.Country.CountryName,
                    Buid = c.Buid,
                    Description = c.Description,
                    IsActive = c.IsActive,
                    CreatedBy = c.CreatedBy,
                    CreatedDate = c.CreatedDate,
                    ModifiedBy = c.ModifiedBy,
                    ModifiedDate = c.ModifiedDate
                })
                .ToListAsync();
        }

        public async Task<CityResponseDTO?> GetByIdAsync(int id)
        {
            var c = await _context.SetCities
                .AsNoTracking()
                .Include(c => c.State)
                .Include(c => c.Country)
                .FirstOrDefaultAsync(x => x.CityId == id);

            if (c == null) return null;

            return new CityResponseDTO
            {
                CityId = c.CityId,
                CityName = c.CityName,
                StateId = c.StateId,
                StateName = c.State.StateName,
                CountryId = c.CountryId,
                CountryName = c.Country.CountryName,
                Buid = c.Buid,
                Description = c.Description,
                IsActive = c.IsActive,
                CreatedBy = c.CreatedBy,
                CreatedDate = c.CreatedDate,
                ModifiedBy = c.ModifiedBy,
                ModifiedDate = c.ModifiedDate
            };
        }

        public async Task<CityResponseDTO> CreateAsync(CityCreateDTO dto, string userId)
        {
            var city = new SetCity
            {
                CityName = dto.CityName,
                StateId = dto.StateId,
                CountryId = dto.CountryId,
                Buid = dto.Buid,
                Description = dto.Description,
                IsActive = dto.IsActive,
                CreatedBy = userId,
                CreatedDate = DateTime.UtcNow
            };

            _context.SetCities.Add(city);
            await _context.SaveChangesAsync();

            return (await GetByIdAsync(city.CityId))!;
        }

        public async Task<CityResponseDTO> UpdateAsync(int id, CityUpdateDTO dto, string userId)
        {
            var city = await _context.SetCities.FindAsync(id);
            if (city == null) throw new Exception("City not found");

            city.CityName = dto.CityName;
            city.StateId = dto.StateId;
            city.CountryId = dto.CountryId;
            city.Buid = dto.Buid;
            city.Description = dto.Description;
            city.IsActive = dto.IsActive;
            city.ModifiedBy = userId;
            city.ModifiedDate = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            return (await GetByIdAsync(id))!;
        }

        public async Task<bool> DeleteAsync(int id)
        {
            var city = await _context.SetCities.FindAsync(id);
            if (city == null) return false;

            _context.SetCities.Remove(city);
            await _context.SaveChangesAsync();
            return true;
        }
    }
}
