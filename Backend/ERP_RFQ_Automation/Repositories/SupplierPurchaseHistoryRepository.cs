using ERP_RFQ_Automation.DTOs.SupplierPurchaseHistory;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ERP_RFQ_Automation.Repositories
{
    public class SupplierPurchaseHistoryRepository : ISupplierPurchaseHistoryRepository
    {
        private readonly ErpRfqAutomationContext _context;

        public SupplierPurchaseHistoryRepository(ErpRfqAutomationContext context)
        {
            _context = context;
        }

        public async Task<IEnumerable<SupplierPurchaseHistoryResponseDTO>> GetAllAsync(long businessUnitId)
        {
            return await _context.SupplierPurchaseHistories
                .Include(h => h.Product)
                .Include(h => h.Supplier)
                .Where(h => h.Product.Buid == businessUnitId)
                .Select(h => MapToDTO(h))
                .ToListAsync();
        }

        public async Task<IEnumerable<SupplierPurchaseHistoryResponseDTO>> GetByProductIdAsync(long productId, long businessUnitId)
        {
            return await _context.SupplierPurchaseHistories
                .Include(h => h.Product)
                .Include(h => h.Supplier)
                .Where(h => h.ProductId == productId && h.Product.Buid == businessUnitId)
                .Select(h => MapToDTO(h))
                .ToListAsync();
        }

        public async Task<SupplierPurchaseHistoryResponseDTO?> GetByIdAsync(long id, long businessUnitId)
        {
            var history = await _context.SupplierPurchaseHistories
                .Include(h => h.Product)
                .Include(h => h.Supplier)
                .FirstOrDefaultAsync(h => h.Id == id && h.Product.Buid == businessUnitId);

            return history != null ? MapToDTO(history) : null;
        }

        public async Task AddAsync(SupplierPurchaseHistory history)
        {
            history.PoDocId = await GenerateNextPoDocId();
            _context.SupplierPurchaseHistories.Add(history);

            // Update Product stock and price
            var product = await _context.Products.FindAsync(history.ProductId);
            if (product != null)
            {
                product.QtyOnHand += history.Quantity;
                product.FinalLandedCost = history.UnitPrice;
                _context.Products.Update(product);
            }

            await _context.SaveChangesAsync();
        }

        public async Task<string> AddBatchAsync(IEnumerable<SupplierPurchaseHistory> histories)
        {
            string poId = await GenerateNextPoDocId();
            var productUpdates = new Dictionary<long, (decimal qty, decimal price)>();

            foreach (var h in histories)
            {
                h.PoDocId = poId;
                _context.SupplierPurchaseHistories.Add(h);

                // Track updates by product
                if (productUpdates.ContainsKey(h.ProductId))
                {
                    var current = productUpdates[h.ProductId];
                    productUpdates[h.ProductId] = (current.qty + h.Quantity, h.UnitPrice);
                }
                else
                {
                    productUpdates[h.ProductId] = (h.Quantity, h.UnitPrice);
                }
            }

            // Apply product updates
            foreach (var update in productUpdates)
            {
                var product = await _context.Products.FindAsync(update.Key);
                if (product != null)
                {
                    product.QtyOnHand += update.Value.qty;
                    product.FinalLandedCost = update.Value.price;
                    _context.Products.Update(product);
                }
            }

            await _context.SaveChangesAsync();
            return poId;
        }


        private async Task<string> GenerateNextPoDocId()
        {
            var lastPo = await _context.SupplierPurchaseHistories
                .Where(h => h.PoDocId != null && h.PoDocId.StartsWith("PO"))
                .OrderByDescending(h => h.PoDocId)
                .Select(h => h.PoDocId)
                .FirstOrDefaultAsync();

            int nextNum = 1;
            if (lastPo != null && lastPo.Length > 2)
            {
                if (int.TryParse(lastPo.Substring(2), out int lastNum))
                {
                    nextNum = lastNum + 1;
                }
            }

            return $"PO{nextNum:D8}";
        }



        public async Task UpdateAsync(SupplierPurchaseHistory history)
        {
            _context.SupplierPurchaseHistories.Update(history);
            await _context.SaveChangesAsync();
        }

        public async Task DeleteAsync(long id, long businessUnitId)
        {
            var history = await _context.SupplierPurchaseHistories
                .Include(h => h.Product)
                .FirstOrDefaultAsync(h => h.Id == id && h.Product.Buid == businessUnitId);

            if (history != null)
            {
                // Revert stock
                var product = await _context.Products.FindAsync(history.ProductId);
                if (product != null)
                {
                    product.QtyOnHand -= history.Quantity;
                    _context.Products.Update(product);
                }

                _context.SupplierPurchaseHistories.Remove(history);
                await _context.SaveChangesAsync();
            }
        }

        public async Task DeleteByPoDocIdAsync(string poDocId, long businessUnitId)
        {
            var items = await _context.SupplierPurchaseHistories
                .Include(h => h.Product)
                .Where(h => h.PoDocId == poDocId && h.Product.Buid == businessUnitId)
                .ToListAsync();

            if (items.Any())
            {
                foreach (var item in items)
                {
                    // Revert stock
                    var product = await _context.Products.FindAsync(item.ProductId);
                    if (product != null)
                    {
                        product.QtyOnHand -= item.Quantity;
                        _context.Products.Update(product);
                    }
                }
                _context.SupplierPurchaseHistories.RemoveRange(items);
                await _context.SaveChangesAsync();
            }
        }


        private static SupplierPurchaseHistoryResponseDTO MapToDTO(SupplierPurchaseHistory history)
        {
            return new SupplierPurchaseHistoryResponseDTO
            {
                Id = history.Id,
                ProductId = history.ProductId,
                ProductName = history.Product?.ProductName,
                PartNo = history.Product?.PartNo,
                SupplierId = history.SupplierId,
                SupplierName = history.Supplier?.Name,
                PurchaseDate = history.PurchaseDate,
                Quantity = history.Quantity,
                UnitPrice = history.UnitPrice,
                Currency = history.Currency,
                BatchNo = history.BatchNo,
                ExpiryDate = history.ExpiryDate,
                PoDocId = history.PoDocId,
                CreatedBy = history.CreatedBy,
                CreatedOn = history.CreatedOn
            };
        }

    }
}
