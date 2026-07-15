using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.DTOs.SupplierQuotedItem;

namespace ERP_RFQ_Automation.Repositories
{
    public class SupplierQuotedItemRepository : ISupplierQuotedItemRepository
    {
        private readonly ErpRfqAutomationContext _context;

        public SupplierQuotedItemRepository(ErpRfqAutomationContext context)
        {
            _context = context;
        }

        public async Task<IEnumerable<SupplierQuotedItemResponseDTO>> GetAllAsync(long businessUnitId)
        {
            return await _context.SupplierQuotedItems
                .Include(s => s.Supplier)
                .Include(s => s.Currency)
                .Include(s => s.Uom)
                .Where(x => x.IsActive && x.BusinessUnitId == businessUnitId)
                .OrderByDescending(x => x.CreatedDate)
                .Select(s => new SupplierQuotedItemResponseDTO
                {
                    Id = s.Id,
                    SupplierId = s.SupplierId,
                    SupplierName = s.Supplier.Name,
                    ItemName = s.ItemName,
                    Description = s.Description,
                    UomId = s.UomId,
                    UomName = s.Uom.UomName,
                    Quantity = s.Quantity,
                    UnitPrice = s.UnitPrice,
                    CurrencyId = s.CurrencyId,
                    CurrencyName = s.Currency.Code,
                    QuoteReference = s.QuoteReference,
                    QuoteDate = s.QuoteDate,
                    ValidUntil = s.ValidUntil,
                    TaxAmount = s.TaxAmount,
                    DiscountAmount = s.DiscountAmount,
                    CreatedBy = s.CreatedBy,
                    CreatedDate = s.CreatedDate,
                    IsActive = s.IsActive,
                    BusinessUnitId = s.BusinessUnitId
                })
                .ToListAsync();
        }

        public async Task<SupplierQuotedItemResponseDTO?> GetByIdAsync(long id, long businessUnitId)
        {
            return await _context.SupplierQuotedItems
                .Include(s => s.Supplier)
                .Include(s => s.Currency)
                .Include(s => s.Uom)
                .Where(x => x.Id == id && x.IsActive && x.BusinessUnitId == businessUnitId)
                .Select(s => new SupplierQuotedItemResponseDTO
                {
                    Id = s.Id,
                    SupplierId = s.SupplierId,
                    SupplierName = s.Supplier.Name,
                    ItemName = s.ItemName,
                    Description = s.Description,
                    UomId = s.UomId,
                    UomName = s.Uom.UomName,
                    Quantity = s.Quantity,
                    UnitPrice = s.UnitPrice,
                    CurrencyId = s.CurrencyId,
                    CurrencyName = s.Currency.Code,
                    QuoteReference = s.QuoteReference,
                    QuoteDate = s.QuoteDate,
                    ValidUntil = s.ValidUntil,
                    TaxAmount = s.TaxAmount,
                    DiscountAmount = s.DiscountAmount,
                    CreatedBy = s.CreatedBy,
                    CreatedDate = s.CreatedDate,
                    IsActive = s.IsActive,
                    BusinessUnitId = s.BusinessUnitId
                })
                .FirstOrDefaultAsync();
        }

        public async Task<SupplierQuotedItem> AddAsync(SupplierQuotedItem entity)
        {
            _context.SupplierQuotedItems.Add(entity);
            await _context.SaveChangesAsync();
            return entity;
        }

        public async Task UpdateAsync(SupplierQuotedItem entity, long businessUnitId)
        {
            var existing = await GetByIdAsync(entity.Id, businessUnitId);
            if (existing == null) throw new KeyNotFoundException("Item not found or access denied.");
            
            _context.Entry(existing).CurrentValues.SetValues(entity);
            await _context.SaveChangesAsync();
        }

        public async Task DeleteAsync(long id, long businessUnitId)
        {
            var entity = await _context.SupplierQuotedItems.FirstOrDefaultAsync(x => x.Id == id && x.BusinessUnitId == businessUnitId);
            if (entity != null)
            {
                entity.IsActive = false;
                await _context.SaveChangesAsync();
            }
        }

        public async Task<IEnumerable<SupplierQuotedItemResponseDTO>> GetBySupplierIdAsync(long supplierId, long businessUnitId)
        {
            return await _context.SupplierQuotedItems
                .Include(s => s.Supplier)
                .Include(s => s.Currency)
                .Include(s => s.Uom)
                .Where(x => x.SupplierId == supplierId && x.IsActive && x.BusinessUnitId == businessUnitId)
                .OrderByDescending(x => x.CreatedDate)
                .Select(s => new SupplierQuotedItemResponseDTO
                {
                    Id = s.Id,
                    SupplierId = s.SupplierId,
                    SupplierName = s.Supplier.Name,
                    ItemName = s.ItemName,
                    Description = s.Description,
                    UomId = s.UomId,
                    UomName = s.Uom.UomName,
                    Quantity = s.Quantity,
                    UnitPrice = s.UnitPrice,
                    CurrencyId = s.CurrencyId,
                    CurrencyName = s.Currency.Code,
                    QuoteReference = s.QuoteReference,
                    QuoteDate = s.QuoteDate,
                    ValidUntil = s.ValidUntil,
                    TaxAmount = s.TaxAmount,
                    DiscountAmount = s.DiscountAmount,
                    CreatedBy = s.CreatedBy,
                    CreatedDate = s.CreatedDate,
                    IsActive = s.IsActive,
                    BusinessUnitId = s.BusinessUnitId
                })
                .ToListAsync();
        }
    }
}
