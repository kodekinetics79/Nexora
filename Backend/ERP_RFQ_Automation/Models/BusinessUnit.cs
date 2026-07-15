using System;
using System.Collections.Generic;

namespace ERP_RFQ_Automation.Models;

public partial class BusinessUnit
{
    public long Id { get; set; }

    public string BusinessUnitCode { get; set; } = null!;

    public string BusinessUnitName { get; set; } = null!;

    public string? Description { get; set; }

    public bool? IsActive { get; set; }

    public string CreatedBy { get; set; } = null!;

    public DateTime CreatedOn { get; set; }

    public string? ModifiedBy { get; set; }

    public DateTime? ModifiedOn { get; set; }

    public virtual ICollection<Currency> Currencies { get; set; } = new List<Currency>();

    public virtual ICollection<Customer> Customers { get; set; } = new List<Customer>();

    public virtual ICollection<EmailConfiguration> EmailConfigurations { get; set; } = new List<EmailConfiguration>();

    public virtual ICollection<Lead> Leads { get; set; } = new List<Lead>();

    public virtual ICollection<Order> Orders { get; set; } = new List<Order>();

    public virtual ICollection<ProductCategory> ProductCategories { get; set; } = new List<ProductCategory>();

    public virtual ICollection<ProductSubCategory> ProductSubCategories { get; set; } = new List<ProductSubCategory>();

    public virtual ICollection<Product> Products { get; set; } = new List<Product>();

    public virtual QuoteConfiguration? QuoteConfiguration { get; set; }

    public virtual ICollection<Quote> Quotes { get; set; } = new List<Quote>();

    public virtual ICollection<Rfq> Rfqs { get; set; } = new List<Rfq>();

    public virtual ICollection<RolePermission> RolePermissions { get; set; } = new List<RolePermission>();

    public virtual ICollection<SetCity> SetCities { get; set; } = new List<SetCity>();

    public virtual ICollection<SetCountry> SetCountries { get; set; } = new List<SetCountry>();

    public virtual ICollection<SetState> SetStates { get; set; } = new List<SetState>();

    public virtual ICollection<SetUom> SetUoms { get; set; } = new List<SetUom>();

    public virtual ICollection<SetupMaster> SetupMasters { get; set; } = new List<SetupMaster>();

    public virtual ICollection<Shipment> Shipments { get; set; } = new List<Shipment>();

    public virtual ICollection<SupplierQuotedItem> SupplierQuotedItems { get; set; } = new List<SupplierQuotedItem>();

    public virtual ICollection<Supplier> Suppliers { get; set; } = new List<Supplier>();

    public virtual ICollection<Team> Teams { get; set; } = new List<Team>();

    public virtual ICollection<UserGroup> UserGroups { get; set; } = new List<UserGroup>();

    public virtual ICollection<User> Users { get; set; } = new List<User>();

    public virtual ICollection<Warehouse> Warehouses { get; set; } = new List<Warehouse>();
}
