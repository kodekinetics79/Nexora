using System;

namespace ERP_RFQ_Automation.DTOs.SupplierPurchaseHistory
{
    public class SupplierPurchaseHistoryResponseDTO
    {
        public long Id { get; set; }
        public long ProductId { get; set; }
        public string? ProductName { get; set; }
        public string? PartNo { get; set; }
        public long SupplierId { get; set; }
        public string? SupplierName { get; set; }
        public DateTime PurchaseDate { get; set; }
        public decimal Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public string? Currency { get; set; }
        public string? BatchNo { get; set; }
        public DateOnly? ExpiryDate { get; set; }
        public string? PoDocId { get; set; }
        public string CreatedBy { get; set; } = null!;
        public DateTime CreatedOn { get; set; }

    }

    public class SupplierPurchaseHistoryCreateDTO
    {
        public long ProductId { get; set; }
        public long SupplierId { get; set; }
        public DateTime PurchaseDate { get; set; } = DateTime.UtcNow;
        public decimal Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public string? Currency { get; set; }
        public string? BatchNo { get; set; }
        public string? ExpiryDate { get; set; } // Changed to string for better handling from frontend
        public string CreatedBy { get; set; } = null!;
    }

    public class SupplierPurchaseHistoryBatchCreateDTO
    {
        public List<SupplierPurchaseHistoryCreateDTO> Items { get; set; } = new();
        public long BusinessUnitId { get; set; }
    }


    public class SupplierPurchaseHistoryUpdateDTO
    {
        public long Id { get; set; }
        public long ProductId { get; set; }
        public long SupplierId { get; set; }
        public DateTime PurchaseDate { get; set; }
        public decimal Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public string? Currency { get; set; }
        public string? BatchNo { get; set; }
        public DateOnly? ExpiryDate { get; set; }
    }
}
