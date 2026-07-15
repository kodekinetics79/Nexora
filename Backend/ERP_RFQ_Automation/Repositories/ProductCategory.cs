// Repositories/ProductCategoryRepository.cs
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ERP_RFQ_Automation.Repositories
{
    public class ProductCategoryRepository : IProductCategoryRepository
    {
        private readonly ErpRfqAutomationContext _context;

        public ProductCategoryRepository(ErpRfqAutomationContext context)
        {
            _context = context;
        }

        public async Task<IEnumerable<ProductCategory>> GetAllAsync(long businessUnitId)
        {
            return await _context.ProductCategories
                .AsNoTracking()
                .Where(c => c.BusinessUnitId == businessUnitId)
                .Include(c => c.ParentCategory)           
                .ToListAsync();
        }

        public async Task<ProductCategory> GetByIdAsync(long id, long businessUnitId)
        {
            var category = await _context.ProductCategories
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == id && c.BusinessUnitId == businessUnitId);

            return category ?? throw new KeyNotFoundException($"Category {id} not found in BU {businessUnitId}.");
        }

        public async Task<ProductCategory?> GetByIdWithParentAsync(long id, long businessUnitId)
        {
            return await _context.ProductCategories
                .AsNoTracking()
                .Include(c => c.ParentCategory)
                .FirstOrDefaultAsync(c => c.Id == id && c.BusinessUnitId == businessUnitId);
        }

        public async Task AddAsync(ProductCategory category)
        {
            // Prevent self-reference loop
            if (category.ParentCategoryId == category.Id)
                throw new ArgumentException("Cannot set a category as its own parent.");

            // Unique name check per BusinessUnit
            bool nameExists = await _context.ProductCategories
                .AnyAsync(c => c.CategoryName == category.CategoryName &&
                               c.BusinessUnitId == category.BusinessUnitId);

            if (nameExists)
                throw new ArgumentException($"Category name '{category.CategoryName}' already exists in this Business Unit.");

            // Parent must exist and belong to same BU (if provided)
            if (category.ParentCategoryId.HasValue)
            {
                bool parentExists = await _context.ProductCategories
                    .AnyAsync(p => p.Id == category.ParentCategoryId &&
                                   p.BusinessUnitId == category.BusinessUnitId);

                if (!parentExists)
                    throw new ArgumentException("Parent category does not exist or belongs to different Business Unit.");
            }

            _context.ProductCategories.Add(category);
            await _context.SaveChangesAsync();
        }

        public async Task UpdateAsync(ProductCategory category)
        {
            var existing = await _context.ProductCategories
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == category.Id);

            if (existing == null)
                throw new KeyNotFoundException($"Category {category.Id} not found.");

            if (existing.BusinessUnitId != category.BusinessUnitId)
                throw new ArgumentException("Cannot change Business Unit of a category.");

            // Prevent self-reference
            if (category.ParentCategoryId == category.Id)
                throw new ArgumentException("Cannot set a category as its own parent.");

            // Unique name (exclude self)
            bool nameExists = await _context.ProductCategories
                .AnyAsync(c => c.CategoryName == category.CategoryName &&
                               c.BusinessUnitId == category.BusinessUnitId &&
                               c.Id != category.Id);

            if (nameExists)
                throw new ArgumentException($"Category name '{category.CategoryName}' already exists.");

            // Parent validation
            if (category.ParentCategoryId.HasValue)
            {
                bool parentExists = await _context.ProductCategories
                    .AnyAsync(p => p.Id == category.ParentCategoryId &&
                                   p.BusinessUnitId == category.BusinessUnitId);

                if (!parentExists)
                    throw new ArgumentException("Invalid parent category.");
            }

            _context.ProductCategories.Update(category);
            await _context.SaveChangesAsync();
        }

        public async Task DeleteAsync(long id, long businessUnitId)
        {
            var category = await GetByIdAsync(id, businessUnitId);

            // Optional: Prevent delete if has children or products
            bool hasChildren = await _context.ProductCategories
                .AnyAsync(c => c.ParentCategoryId == id);

            bool hasProducts = await _context.Products
                .AnyAsync(p => p.CategoryId == id);

            if (hasChildren || hasProducts)
                throw new InvalidOperationException("Cannot delete category that has sub-categories or linked products.");

            _context.ProductCategories.Remove(category);
            await _context.SaveChangesAsync();
        }
    }
}