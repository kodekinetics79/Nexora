// Repositories/ProductSubCategoryRepository.cs
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ERP_RFQ_Automation.Repositories
{
    public class ProductSubCategoryRepository : IProductSubCategoryRepository
    {
        private readonly ErpRfqAutomationContext _context;

        public ProductSubCategoryRepository(ErpRfqAutomationContext context)
        {
            _context = context;
        }

        public async Task<IEnumerable<ProductSubCategory>> GetAllAsync(long businessUnitId)
        {
            return await _context.ProductSubCategories
                .AsNoTracking()
                .Where(s => s.BusinessUnitId == businessUnitId)
                .ToListAsync();
        }

        public async Task<ProductSubCategory> GetByIdAsync(int id, long businessUnitId)
        {
            var sub = await _context.ProductSubCategories
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == id && s.BusinessUnitId == businessUnitId);

            return sub ?? throw new KeyNotFoundException($"SubCategory {id} not found in BU {businessUnitId}.");
        }

        public async Task AddAsync(ProductSubCategory subCategory)
        {
            // Unique name per BusinessUnit
            bool nameExists = await _context.ProductSubCategories
                .AnyAsync(s => s.SubCategoryName == subCategory.SubCategoryName &&
                               s.BusinessUnitId == subCategory.BusinessUnitId);

            if (nameExists)
                throw new ArgumentException($"SubCategory name '{subCategory.SubCategoryName}' already exists in this Business Unit.");

            // Optional: check BU exists
            if (!await _context.BusinessUnits.AnyAsync(b => b.Id == subCategory.BusinessUnitId))
                throw new ArgumentException($"Business Unit {subCategory.BusinessUnitId} does not exist.");

            _context.ProductSubCategories.Add(subCategory);
            await _context.SaveChangesAsync();
        }

        public async Task UpdateAsync(ProductSubCategory subCategory)
        {
            var existing = await _context.ProductSubCategories
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == subCategory.Id);

            if (existing == null)
                throw new KeyNotFoundException($"SubCategory {subCategory.Id} not found.");

            if (existing.BusinessUnitId != subCategory.BusinessUnitId)
                throw new ArgumentException("Cannot change Business Unit of a subcategory.");

            // Unique name (exclude self)
            bool nameExists = await _context.ProductSubCategories
                .AnyAsync(s => s.SubCategoryName == subCategory.SubCategoryName &&
                               s.BusinessUnitId == subCategory.BusinessUnitId &&
                               s.Id != subCategory.Id);

            if (nameExists)
                throw new ArgumentException($"SubCategory name '{subCategory.SubCategoryName}' already exists.");

            _context.ProductSubCategories.Update(subCategory);
            await _context.SaveChangesAsync();
        }

        public async Task DeleteAsync(int id, long businessUnitId)
        {
            var subCategory = await GetByIdAsync(id, businessUnitId);

            // Prevent delete if linked to products
            bool hasProducts = await _context.Products
                .AnyAsync(p => p.SubCategoryId == id);   // assuming you add SubCategoryId to Product model later

            if (hasProducts)
                throw new InvalidOperationException("Cannot delete subcategory that has linked products.");

            _context.ProductSubCategories.Remove(subCategory);
            await _context.SaveChangesAsync();
        }
    }
}