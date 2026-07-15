using ERP_RFQ_Automation.DTOs.UomDTOs;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Repositories
{
    public class UomRepository : IUomRepository
    {
        private readonly ErpRfqAutomationContext _context;

        public UomRepository(ErpRfqAutomationContext context)
        {
            _context = context;
        }

        public async Task<IEnumerable<UomResponseDTO>> GetAllAsync(long businessUnitId)
        {
            return await _context.SetUoms
                .AsNoTracking()
                .Where(u => u.BusinessUnitId == businessUnitId)
                .Select(u => new UomResponseDTO
                {
                    UomId = u.UomId,
                    BusinessUnitId = u.BusinessUnitId,
                    UomCode = u.UomCode,
                    UomName = u.UomName,
                    Description = u.Description,
                    IsActive = u.IsActive,
                    CreatedBy = u.CreatedBy,
                    CreatedDate = u.CreatedDate,
                    ModifiedBy = u.ModifiedBy,
                    ModifiedDate = u.ModifiedDate
                })
                .ToListAsync();
        }

        public async Task<UomResponseDTO?> GetByIdAsync(int id)
        {
            var u = await _context.SetUoms
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.UomId == id);

            if (u == null) return null;

            return new UomResponseDTO
            {
                UomId = u.UomId,
                BusinessUnitId = u.BusinessUnitId,
                UomCode = u.UomCode,
                UomName = u.UomName,
                Description = u.Description,
                IsActive = u.IsActive,
                CreatedBy = u.CreatedBy,
                CreatedDate = u.CreatedDate,
                ModifiedBy = u.ModifiedBy,
                ModifiedDate = u.ModifiedDate
            };
        }

        public async Task<UomResponseDTO> CreateAsync(UomCreateDTO dto, string userId)
        {
            var uom = new SetUom
            {
                BusinessUnitId = dto.BusinessUnitId,
                UomCode = dto.UomCode,
                UomName = dto.UomName,
                Description = dto.Description,
                IsActive = dto.IsActive,
                CreatedBy = userId,
                CreatedDate = DateTime.UtcNow
            };

            _context.SetUoms.Add(uom);
            await _context.SaveChangesAsync();

            return (await GetByIdAsync(uom.UomId))!;
        }

        public async Task<UomResponseDTO> UpdateAsync(int id, UomUpdateDTO dto, string userId)
        {
            var uom = await _context.SetUoms.FindAsync(id);
            if (uom == null) throw new Exception("UOM not found");

            uom.BusinessUnitId = dto.BusinessUnitId;
            uom.UomCode = dto.UomCode;
            uom.UomName = dto.UomName;
            uom.Description = dto.Description;
            uom.IsActive = dto.IsActive;
            uom.ModifiedBy = userId;
            uom.ModifiedDate = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            return (await GetByIdAsync(id))!;
        }

        public async Task<bool> DeleteAsync(int id)
        {
            var uom = await _context.SetUoms.FindAsync(id);
            if (uom == null) return false;

            _context.SetUoms.Remove(uom);
            await _context.SaveChangesAsync();
            return true;
        }
    }
}
