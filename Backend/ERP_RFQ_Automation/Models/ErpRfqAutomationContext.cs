using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.CustomFields;
using ERP_RFQ_Automation.CommercialCases.Lifecycle;

namespace ERP_RFQ_Automation.Models;

public partial class ErpRfqAutomationContext : DbContext
{
    public ErpRfqAutomationContext()
    {
    }

    public ErpRfqAutomationContext(DbContextOptions<ErpRfqAutomationContext> options)
        : base(options)
    {
    }

    // Tenant-scoping constructor. ITenantContext is optional so EF design-time
    // (migrations) and the parameterless path still work; DI injects the real
    // per-request scope at runtime. Backs the global query filters in
    // ErpRfqAutomationContext.Tenancy.cs. (ADR-0005)
    public ErpRfqAutomationContext(
        DbContextOptions<ErpRfqAutomationContext> options,
        ERP_RFQ_Automation.MultiTenancy.ITenantContext tenant)
        : base(options)
    {
        _tenant = tenant;
    }

    public virtual DbSet<Attachment> Attachments { get; set; }

    public virtual DbSet<BusinessUnit> BusinessUnits { get; set; }

    public virtual DbSet<CommercialCase> CommercialCases { get; set; }

    public virtual DbSet<CommercialLifecycleEvent> CommercialLifecycleEvents { get; set; }

    public virtual DbSet<LifecycleOutboxMessage> LifecycleOutboxMessages { get; set; }

    public virtual DbSet<Contact> Contacts { get; set; }

    public virtual DbSet<Currency> Currencies { get; set; }

    public virtual DbSet<Customer> Customers { get; set; }

    public virtual DbSet<EmailConfiguration> EmailConfigurations { get; set; }

    public virtual DbSet<EmailIngest> EmailIngests { get; set; }

    public virtual DbSet<Image> Images { get; set; }

    public virtual DbSet<Lead> Leads { get; set; }

    public virtual DbSet<LeadItem> LeadItems { get; set; }

    public virtual DbSet<LeadReferenceConfiguration> LeadReferenceConfigurations { get; set; }

    public virtual DbSet<LeadStatusHistory> LeadStatusHistories { get; set; }

    public virtual DbSet<Module> Modules { get; set; }

    public virtual DbSet<Order> Orders { get; set; }

    public virtual DbSet<OrderItem> OrderItems { get; set; }

    public virtual DbSet<Product> Products { get; set; }

    public virtual DbSet<ProductAttachment> ProductAttachments { get; set; }

    public virtual DbSet<ProductCategory> ProductCategories { get; set; }

    public virtual DbSet<ProductSubCategory> ProductSubCategories { get; set; }

    public virtual DbSet<Quote> Quotes { get; set; }

    public virtual DbSet<QuoteConfiguration> QuoteConfigurations { get; set; }

    public virtual DbSet<QuoteItem> QuoteItems { get; set; }

    public virtual DbSet<Rfq> Rfqs { get; set; }

    public virtual DbSet<Rfqitem> Rfqitems { get; set; }

    public virtual DbSet<RolePermission> RolePermissions { get; set; }

    public virtual DbSet<SetCity> SetCities { get; set; }

    public virtual DbSet<SetCountry> SetCountries { get; set; }

    public virtual DbSet<SetState> SetStates { get; set; }

    public virtual DbSet<SetUom> SetUoms { get; set; }

    public virtual DbSet<SetupMaster> SetupMasters { get; set; }

    public virtual DbSet<Shipment> Shipments { get; set; }

    public virtual DbSet<ShipmentItem> ShipmentItems { get; set; }

    public virtual DbSet<ShipmentStatusHistory> ShipmentStatusHistories { get; set; }

    public virtual DbSet<Supplier> Suppliers { get; set; }

    public virtual DbSet<SupplierPurchaseHistory> SupplierPurchaseHistories { get; set; }

    public virtual DbSet<SupplierQuotedItem> SupplierQuotedItems { get; set; }

    public virtual DbSet<Team> Teams { get; set; }

    public virtual DbSet<User> Users { get; set; }

    public virtual DbSet<UserGroup> UserGroups { get; set; }

    public virtual DbSet<ViewSupplierPriceList> ViewSupplierPriceLists { get; set; }

    public virtual DbSet<Warehouse> Warehouses { get; set; }

    public virtual DbSet<ERP_RFQ_Automation.Inventory.StockReservation> StockReservations { get; set; }

    public virtual DbSet<Taxis> Taxes { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) { }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        LeadPersistenceRules.Prepare(this);
        CustomFieldGovernanceInterceptor.Validate(ChangeTracker);
        LifecycleGovernanceInterceptor.Validate(ChangeTracker);
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        LeadPersistenceRules.Prepare(this);
        CustomFieldGovernanceInterceptor.Validate(ChangeTracker);
        LifecycleGovernanceInterceptor.Validate(ChangeTracker);
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // SQL Server is case-INSENSITIVE. Postgres is case-sensitive, so email
        // uniqueness / lookups would silently change behavior. `citext` restores
        // case-insensitive comparison for the email columns that carry unique
        // indexes (Customer/Supplier ContactEmail, Users/Contact Email).
        modelBuilder.HasPostgresExtension("citext");

        modelBuilder.Entity<Attachment>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK__Attachme__3214EC2740D763DA");

            entity.HasIndex(e => new { e.ParentType, e.ParentId }, "IX_Attachments_ParentTypeID");

            entity.Property(e => e.Id).HasColumnName("ID");
            entity.Property(e => e.ContentType).HasMaxLength(200);
            entity.Property(e => e.CreatedOn).HasDefaultValueSql("now()");
            entity.Property(e => e.FileName).HasMaxLength(255);
            entity.Property(e => e.FilePath).HasMaxLength(500);
            entity.Property(e => e.MimeType).HasMaxLength(100);
            entity.Property(e => e.ParentId).HasColumnName("ParentID");
            entity.Property(e => e.ParentType).HasMaxLength(50);
        });

        modelBuilder.Entity<BusinessUnit>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK__Business__3214EC27B5E4A97A");

            entity.Property(e => e.Id).HasColumnName("ID");
            entity.Property(e => e.BusinessUnitCode).HasMaxLength(50);
            entity.Property(e => e.BusinessUnitName).HasMaxLength(255);
            entity.Property(e => e.CreatedBy).HasMaxLength(255);
            entity.Property(e => e.CreatedOn).HasDefaultValueSql("now()");
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.ModifiedBy).HasMaxLength(255);
        });

        modelBuilder.Entity<Contact>(entity =>
        {
            entity.ToTable("Contacts", table => table.HasCheckConstraint(
                "CK_Contacts_ExactlyOneParent",
                "(\"CustomerID\" IS NULL) <> (\"SupplierID\" IS NULL)"));
            entity.HasKey(e => e.Id).HasName("PK__Contacts__3214EC274B89BAF3");
            entity.HasAlternateKey(e => new { e.BusinessUnitId, e.Id }).HasName("AK_Contacts_BusinessUnitID_ID");

            entity.HasIndex(e => e.CustomerId, "IX_Contacts_CustomerID");

            entity.HasIndex(e => new { e.BusinessUnitId, e.Email }, "IX_Contacts_BusinessUnitID_Email");

            entity.HasIndex(e => e.SupplierId, "IX_Contacts_SupplierID");

            entity.HasIndex(e => new { e.BusinessUnitId, e.Email }, "UQ_Contacts_BusinessUnitID_Email")
                .IsUnique()
                .HasFilter("\"Email\" IS NOT NULL");

            entity.Property(e => e.Id).HasColumnName("ID");
            entity.Property(e => e.BusinessUnitId).HasColumnName("BusinessUnitID");
            entity.Property(e => e.CreatedBy).HasMaxLength(255);
            entity.Property(e => e.CustomerId).HasColumnName("CustomerID");
            entity.Property(e => e.Email).HasColumnType("citext"); // case-insensitive unique email
            entity.Property(e => e.FirstName).HasMaxLength(100);
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.IsPrimary).HasDefaultValue(false);
            entity.Property(e => e.LastName).HasMaxLength(100);
            entity.Property(e => e.MiddleName).HasMaxLength(100);
            entity.Property(e => e.MobileNo).HasMaxLength(50);
            entity.Property(e => e.ModifiedBy).HasMaxLength(255);
            entity.Property(e => e.PhoneNo).HasMaxLength(50);
            entity.Property(e => e.Position).HasMaxLength(100);
            entity.Property(e => e.SupplierId).HasColumnName("SupplierID");

            entity.HasOne(d => d.Customer).WithMany(p => p.Contacts)
                .HasForeignKey(d => new { d.BusinessUnitId, d.CustomerId })
                .HasPrincipalKey(d => new { BusinessUnitId = d.Buid, d.Id })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("FK__Contacts__Custom__17F790F9");

            entity.HasOne(d => d.Supplier).WithMany(p => p.Contacts)
                .HasForeignKey(d => new { d.SupplierId, d.BusinessUnitId })
                .HasPrincipalKey(d => new { d.Id, BusinessUnitId = d.Buid })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("FK_Contacts_Suppliers_SupplierID_BusinessUnitID");
        });

        modelBuilder.Entity<Currency>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK__Currency__3214EC2734927EB0");

            entity.ToTable("Currency");

            entity.HasIndex(e => e.BusinessUnitId, "IX_Currency_BusinessUnitID");

            entity.Property(e => e.Id).HasColumnName("ID");
            entity.Property(e => e.BusinessUnitId).HasColumnName("BusinessUnitID");
            entity.Property(e => e.Code).HasMaxLength(10);
            entity.Property(e => e.CreatedBy).HasMaxLength(255);
            entity.Property(e => e.CurrencyName).HasMaxLength(100);
            entity.Property(e => e.ExchangeRate).HasColumnType("decimal(18, 6)");
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.IsBaseCurrency).HasDefaultValue(false);
            entity.Property(e => e.ModifiedBy).HasMaxLength(255);
            entity.Property(e => e.Symbol).HasMaxLength(10);

            entity.HasOne(d => d.BusinessUnit).WithMany(p => p.Currencies)
                .HasForeignKey(d => d.BusinessUnitId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK__Currency__Busine__4E88ABD4");
        });

        modelBuilder.Entity<Customer>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK__Customer__3214EC27D6DB6FD1");
            entity.Property(e => e.Buid).IsRequired();
            entity.HasAlternateKey(e => new { e.Buid, e.Id }).HasName("AK_Customers_BUID_ID");

            entity.HasIndex(e => e.Name, "IX_Customers_Name");

            entity.HasIndex(e => new { e.Buid, e.ContactEmail }, "UQ_Customers_BUID_ContactEmail")
                .IsUnique()
                .HasFilter("\"ContactEmail\" IS NOT NULL");

            entity.Property(e => e.Id).HasColumnName("ID");
            entity.Property(e => e.BillingAddressLine1).HasMaxLength(255);
            entity.Property(e => e.BillingAddressLine2).HasMaxLength(255);
            entity.Property(e => e.BillingCity).HasMaxLength(100);
            entity.Property(e => e.BillingCountry).HasMaxLength(100);
            entity.Property(e => e.BillingPostalCode).HasMaxLength(20);
            entity.Property(e => e.BillingState).HasMaxLength(100);
            entity.Property(e => e.Buid).HasColumnName("BUID");
            entity.Property(e => e.ContactEmail).HasColumnType("citext"); // case-insensitive unique email (was nvarchar(255) CI in SQL Server)
            entity.Property(e => e.CreatedBy).HasMaxLength(255);
            entity.Property(e => e.DocId)
                .HasMaxLength(10)
                .IsUnicode(false);
            entity.Property(e => e.ImageUrl)
                .HasMaxLength(100)
                .HasColumnName("ImageURL");
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.ModifiedBy).HasMaxLength(255);
            entity.Property(e => e.Name).HasMaxLength(255);
            entity.Property(e => e.ShippingAddressLine1).HasMaxLength(255);
            entity.Property(e => e.ShippingAddressLine2).HasMaxLength(255);
            entity.Property(e => e.ShippingCity).HasMaxLength(100);
            entity.Property(e => e.ShippingCountry).HasMaxLength(100);
            entity.Property(e => e.ShippingPostalCode).HasMaxLength(20);
            entity.Property(e => e.ShippingState).HasMaxLength(100);

            entity.HasOne(d => d.Bu).WithMany(p => p.Customers)
                .HasForeignKey(d => d.Buid)
                .HasConstraintName("FK__Customers__BUID__0D7A0286");
        });

        modelBuilder.Entity<EmailConfiguration>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK__Email_Co__3214EC278A1BB987");

            entity.ToTable("Email_Configurations");

            entity.Property(e => e.Id).HasColumnName("ID");
            entity.Property(e => e.BusinessUnitId).HasColumnName("BusinessUnitID");
            entity.Property(e => e.ConfigurationName).HasMaxLength(255);
            entity.Property(e => e.CreatedOn).HasDefaultValueSql("now()");
            entity.Property(e => e.EmailAddress).HasMaxLength(255);
            entity.Property(e => e.Host).HasMaxLength(255);
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.Password).HasMaxLength(255);
            entity.Property(e => e.PollingInterval).HasDefaultValue(300);
            entity.Property(e => e.Protocol).HasMaxLength(50);
            entity.Property(e => e.UseSsl)
                .HasDefaultValue(true)
                .HasColumnName("UseSSL");
            entity.Property(e => e.Username).HasMaxLength(255);

            entity.HasOne(d => d.BusinessUnit).WithMany(p => p.EmailConfigurations)
                .HasForeignKey(d => d.BusinessUnitId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK__Email_Con__Busin__489AC854");
        });

        modelBuilder.Entity<EmailIngest>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK__EmailIng__3214EC2728D6F6B3");

            entity.HasIndex(e => e.MessageId, "UQ__EmailIng__C87C037D5950F99E").IsUnique();

            entity.Property(e => e.Id).HasColumnName("ID");
            entity.Property(e => e.CreatedOn).HasDefaultValueSql("now()");
            entity.Property(e => e.EmailConfigurationId).HasColumnName("EmailConfigurationID");
            entity.Property(e => e.EmailSubject).HasMaxLength(500);
            entity.Property(e => e.FromEmail).HasMaxLength(255);
            entity.Property(e => e.MessageId)
                .HasMaxLength(255)
                .HasColumnName("MessageID");
            entity.Property(e => e.ParseStatus).HasMaxLength(50);
            entity.Property(e => e.RawEmailPath).HasMaxLength(500);
            entity.Property(e => e.ToEmail).HasMaxLength(255);

            entity.HasOne(d => d.EmailConfiguration).WithMany(p => p.EmailIngests)
                .HasForeignKey(d => d.EmailConfigurationId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK__EmailInge__Email__503BEA1C");
        });

        modelBuilder.Entity<Image>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK__Images__3214EC27B2D5CCF9");

            entity.HasIndex(e => new { e.ResourceType, e.ResourceId }, "IX_Images_Resource");

            entity.Property(e => e.Id).HasColumnName("ID");
            entity.Property(e => e.CreatedBy).HasMaxLength(255);
            entity.Property(e => e.Description).HasMaxLength(255);
            entity.Property(e => e.FileName).HasMaxLength(255);
            entity.Property(e => e.FilePath).HasMaxLength(500);
            entity.Property(e => e.IsPrimary).HasDefaultValue(false);
            entity.Property(e => e.MimeType).HasMaxLength(100);
            entity.Property(e => e.ModifiedBy).HasMaxLength(255);
            entity.Property(e => e.ResourceId).HasColumnName("ResourceID");
            entity.Property(e => e.ResourceType).HasMaxLength(100);
            entity.Property(e => e.UploadDate).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<Lead>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK__Leads__3214EC2705035004");

            entity.HasIndex(e => e.LeadStatusId, "IX_Leads_LeadStatusId");

            entity.HasIndex(e => e.Rfqno, "IX_Leads_RFQNo");

            entity.HasIndex(e => e.RecDate, "IX_Leads_RecDate");

            entity.HasIndex(e => new { e.BusinessUnitId, e.CustomerId }, "IX_Leads_BusinessUnitID_CustomerID");

            entity.HasIndex(e => new { e.BusinessUnitId, e.ContactId }, "IX_Leads_BusinessUnitID_ContactID");

            entity.Property(e => e.Id).HasColumnName("ID");
            entity.Property(e => e.Aiconfidence)
                .HasColumnType("decimal(5, 4)")
                .HasColumnName("AIConfidence");
            entity.Property(e => e.AssignComment)
                .HasMaxLength(500)
                .IsUnicode(false);
            entity.Property(e => e.AssignOn);
            entity.Property(e => e.BiddingDecision).HasMaxLength(100);
            entity.Property(e => e.BusinessUnitId).HasColumnName("BusinessUnitID");
            entity.Property(e => e.BuyersName).HasMaxLength(510);
            entity.Property(e => e.Clientemail)
                .HasMaxLength(255)
                .IsUnicode(false);
            entity.Property(e => e.CreatedBy).HasMaxLength(20);
            entity.Property(e => e.CreatedDate).HasDefaultValueSql("now()");
            entity.Property(e => e.CustomerId).HasColumnName("CustomerID");
            entity.Property(e => e.ContactId).HasColumnName("ContactID");
            entity.Property(e => e.CustomerMatchStatus).HasMaxLength(32).HasDefaultValue("UNRESOLVED");
            entity.Property(e => e.DurationAgreement).HasMaxLength(100);
            entity.Property(e => e.EmailIngestsId).HasColumnName("EmailIngestsID");
            entity.Property(e => e.EmailSource)
                .HasMaxLength(255)
                .IsUnicode(false);
            entity.Property(e => e.LeadRejectedReasonId).HasColumnName("LeadRejectedReasonID");
            entity.Property(e => e.LeadSource).HasMaxLength(100);
            entity.Property(e => e.OpportunityNo).HasMaxLength(100);
            entity.Property(e => e.Rfqno)
                .HasMaxLength(100)
                .HasColumnName("RFQNo");
            entity.Property(e => e.Rfqtype)
                .HasMaxLength(50)
                .HasColumnName("RFQType");

            entity.HasOne(d => d.AssignToNavigation).WithMany(p => p.Leads)
                .HasForeignKey(d => d.AssignTo)
                .HasConstraintName("FK_Leads_Users");

            entity.HasOne(d => d.BusinessUnit).WithMany(p => p.Leads)
                .HasForeignKey(d => d.BusinessUnitId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK__Leads__BusinessU__55009F39");

            entity.HasOne(d => d.EmailIngests).WithMany(p => p.Leads)
                .HasForeignKey(d => d.EmailIngestsId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK__Leads__EmailInge__55F4C372");

            entity.HasOne(d => d.LeadRejectedReason).WithMany(p => p.LeadLeadRejectedReasons)
                .HasForeignKey(d => d.LeadRejectedReasonId)
                .HasConstraintName("FK_Leads_LeadRejectedReason");

            entity.HasOne(d => d.LeadStatus).WithMany(p => p.LeadLeadStatuses)
                .HasForeignKey(d => d.LeadStatusId)
                .HasConstraintName("FK_Leads_Setup_Master");
        });

        modelBuilder.Entity<LeadItem>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK__LeadItem__3214EC2776894FBF");

            entity.HasIndex(e => e.BidClosingDateLine, "IX_LeadItems_BidClosingDateLine");

            entity.HasIndex(e => e.BuyerName, "IX_LeadItems_BuyerName");

            entity.HasIndex(e => e.CustomerRfqno, "IX_LeadItems_CustomerRFQNo");

            entity.HasIndex(e => e.LeadId, "IX_LeadItems_LeadID");

            entity.HasIndex(e => e.CustomerRfqno, "IX_LeadItems_RFQ_Include");

            entity.HasIndex(e => e.ReceivedDate, "IX_LeadItems_ReceivedDate");

            entity.Property(e => e.Id).HasColumnName("ID");
            entity.Property(e => e.Aiconfidence)
                .HasColumnType("decimal(5, 4)")
                .HasColumnName("AIConfidence");
            entity.Property(e => e.AlternatePartNumber).HasMaxLength(100);
            entity.Property(e => e.AlternateProductName).HasMaxLength(200);
            entity.Property(e => e.Alternative).HasMaxLength(100);
            entity.Property(e => e.BuyerName).HasMaxLength(200);
            entity.Property(e => e.CommodityProduct).HasMaxLength(200);
            entity.Property(e => e.CompanyRef).HasMaxLength(100);
            entity.Property(e => e.Currency).HasMaxLength(10);
            entity.Property(e => e.CustomerAccountPortalId)
                .HasMaxLength(100)
                .HasColumnName("CustomerAccountPortalID");
            entity.Property(e => e.CustomerRfqno)
                .HasMaxLength(100)
                .HasColumnName("CustomerRFQNo");
            entity.Property(e => e.ItemMaterialCode).HasMaxLength(100);
            entity.Property(e => e.ItemText).HasMaxLength(2000);
            entity.Property(e => e.LeadId).HasColumnName("LeadID");
            entity.Property(e => e.LineItemNo).HasMaxLength(50);
            entity.Property(e => e.ManufacturerName).HasMaxLength(200);
            entity.Property(e => e.ManufacturerPartNumber).HasMaxLength(100);
            entity.Property(e => e.MaterialPotext)
                .HasMaxLength(2000)
                .HasColumnName("MaterialPOText");
            entity.Property(e => e.ProductShortDescription).HasMaxLength(1000);
            entity.Property(e => e.ProductShortName).HasMaxLength(1000);
            entity.Property(e => e.StorageLocation).HasMaxLength(100);
            entity.Property(e => e.UnitOfMeasure).HasMaxLength(100);
            entity.Property(e => e.UnitPrice).HasColumnType("decimal(18, 6)");

            entity.HasOne(d => d.Lead).WithMany(p => p.LeadItems)
                .HasForeignKey(d => d.LeadId)
                .HasConstraintName("FK_LeadItems_Leads");
        });

        modelBuilder.Entity<Module>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK__Module__3214EC276837F46D");

            entity.ToTable("Module");

            entity.HasIndex(e => e.ModuleName, "IX_Module_ModuleName");

            entity.HasIndex(e => e.ModuleName, "UQ__Module__EAC9AEC357051E1B").IsUnique();

            entity.Property(e => e.Id).HasColumnName("ID");
            entity.Property(e => e.CreatedBy).HasMaxLength(255);
            entity.Property(e => e.Description).HasMaxLength(255);
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.ModifiedBy).HasMaxLength(255);
            entity.Property(e => e.ModuleName).HasMaxLength(100);
        });

        modelBuilder.Entity<Order>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK__Orders__3214EC27F30500C1");

            entity.HasIndex(e => e.CustomerId, "IX_Orders_CustomerID");

            entity.HasIndex(e => e.OrderNo, "IX_Orders_OrderNo");

            entity.HasIndex(e => e.PaymentStatusId, "IX_Orders_PaymentStatusID");

            entity.Property(e => e.Id).HasColumnName("ID");
            // Postgres supports only STORED generated columns (not the virtual /
            // non-persisted computed column SQL Server used here). Translate the
            // expression to Postgres-quoted identifiers and persist it. Behaviour
            // is unchanged for the app, which computes the balance in code
            // (OrderService) and never reads this column.
            entity.Property(e => e.BalanceAmount)
                .HasComputedColumnSql("\"TotalAmount\" - \"PaidAmount\"", stored: true)
                .HasColumnType("decimal(19, 2)");
            entity.Property(e => e.BusinessUnitId).HasColumnName("BusinessUnitID");
            entity.Property(e => e.CommercialCaseId).HasColumnName("CommercialCaseID");
            entity.Property(e => e.ContactId).HasColumnName("ContactID");
            entity.Property(e => e.NexoraSerial).HasMaxLength(100);
            entity.Property(e => e.CreatedBy)
                .HasMaxLength(255)
                .IsUnicode(false);
            entity.Property(e => e.CreatedOn)
                .HasDefaultValueSql("now()");
            entity.Property(e => e.CurrencyId).HasColumnName("CurrencyID");
            entity.Property(e => e.CustomerId).HasColumnName("CustomerID");
            entity.Property(e => e.DeliveryDate);
            entity.Property(e => e.DiscountAmount)
                .HasDefaultValue(0m)
                .HasColumnType("decimal(18, 2)");
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.LeadId).HasColumnName("LeadID");
            entity.Property(e => e.ModifiedBy)
                .HasMaxLength(255)
                .IsUnicode(false);
            entity.Property(e => e.ModifiedOn);
            entity.Property(e => e.Notes).IsUnicode(false);
            entity.Property(e => e.OrderDate)
                .HasDefaultValueSql("now()");
            entity.Property(e => e.OrderNo)
                .HasMaxLength(50)
                .IsUnicode(false);
            entity.Property(e => e.PaidAmount).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.PaymentDate);
            entity.Property(e => e.PaymentMethodId).HasColumnName("PaymentMethodID");
            entity.Property(e => e.PaymentReference)
                .HasMaxLength(100)
                .IsUnicode(false);
            entity.Property(e => e.PaymentStatusId).HasColumnName("PaymentStatusID");
            entity.Property(e => e.QuoteId).HasColumnName("QuoteID");
            entity.Property(e => e.Rfqid).HasColumnName("RFQID");
            entity.Property(e => e.StatusId).HasColumnName("StatusID");
            entity.Property(e => e.SubTotal)
                .HasDefaultValue(0m)
                .HasColumnType("decimal(18, 2)");
            entity.Property(e => e.TaxAmount)
                .HasDefaultValue(0m)
                .HasColumnType("decimal(18, 2)");
            entity.Property(e => e.TermsAndConditions).IsUnicode(false);
            entity.Property(e => e.TotalAmount).HasColumnType("decimal(18, 2)");

            entity.HasOne(d => d.BusinessUnit).WithMany(p => p.Orders)
                .HasForeignKey(d => d.BusinessUnitId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK__Orders__Business__3F9B6DFF");

            entity.HasOne<CommercialCase>()
                .WithMany()
                .HasForeignKey(e => new { e.BusinessUnitId, e.CommercialCaseId })
                .HasPrincipalKey(e => new { e.BusinessUnitId, e.Id })
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(e => new { e.BusinessUnitId, e.CommercialCaseId });
            entity.HasIndex(e => new { e.BusinessUnitId, e.NexoraSerial });

            entity.HasOne(d => d.Currency).WithMany(p => p.Orders)
                .HasForeignKey(d => d.CurrencyId)
                .HasConstraintName("FK__Orders__Currency__436BFEE3");

            entity.HasOne(d => d.Customer).WithMany(p => p.Orders)
                .HasForeignKey(d => d.CustomerId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK__Orders__Customer__3EA749C6");

            entity.HasOne(d => d.Lead).WithMany(p => p.Orders)
                .HasForeignKey(d => d.LeadId)
                .HasConstraintName("FK__Orders__LeadID__3CBF0154");

            entity.HasOne(d => d.PaymentMethod).WithMany(p => p.OrderPaymentMethods)
                .HasForeignKey(d => d.PaymentMethodId)
                .HasConstraintName("FK__Orders__PaymentM__4183B671");

            entity.HasOne(d => d.PaymentStatus).WithMany(p => p.OrderPaymentStatuses)
                .HasForeignKey(d => d.PaymentStatusId)
                .HasConstraintName("FK__Orders__PaymentS__4277DAAA");

            entity.HasOne(d => d.Quote).WithMany(p => p.Orders)
                .HasForeignKey(d => d.QuoteId)
                .HasConstraintName("FK__Orders__QuoteID__3BCADD1B");

            entity.HasOne(d => d.Rfq).WithMany(p => p.Orders)
                .HasForeignKey(d => d.Rfqid)
                .HasConstraintName("FK__Orders__RFQID__3DB3258D");

            entity.HasOne(d => d.Status).WithMany(p => p.OrderStatuses)
                .HasForeignKey(d => d.StatusId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK__Orders__StatusID__408F9238");
        });

        modelBuilder.Entity<OrderItem>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK__OrderIte__3214EC27F54B0F5F");

            entity.HasIndex(e => e.OrderId, "IX_OrderItems_OrderID");

            entity.HasIndex(e => e.ProductId, "IX_OrderItems_ProductID");

            entity.Property(e => e.Id).HasColumnName("ID");
            entity.Property(e => e.CreatedBy)
                .HasMaxLength(255)
                .IsUnicode(false);
            entity.Property(e => e.CreatedDate)
                .HasDefaultValueSql("now()");
            entity.Property(e => e.Description)
                .HasMaxLength(500)
                .IsUnicode(false);
            entity.Property(e => e.Discount).HasColumnType("decimal(18, 6)");
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.ModifiedBy)
                .HasMaxLength(255)
                .IsUnicode(false);
            entity.Property(e => e.ModifiedDate);
            entity.Property(e => e.OrderId).HasColumnName("OrderID");
            entity.Property(e => e.ProductId).HasColumnName("ProductID");
            entity.Property(e => e.Quantity).HasColumnType("decimal(18, 6)");
            entity.Property(e => e.TaxAmount).HasColumnType("decimal(18, 6)");
            entity.Property(e => e.TotalAmount).HasColumnType("decimal(18, 6)");
            entity.Property(e => e.UnitPrice).HasColumnType("decimal(18, 6)");
            entity.Property(e => e.UomId).HasColumnName("UomID");
            entity.Property(e => e.WarehouseId).HasColumnName("WarehouseID");

            entity.HasOne(d => d.Order).WithMany(p => p.OrderItems)
                .HasForeignKey(d => d.OrderId)
                .HasConstraintName("FK__OrderItem__Order__4A18FC72");

            entity.HasOne(d => d.Product).WithMany(p => p.OrderItems)
                .HasForeignKey(d => d.ProductId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK__OrderItem__Produ__4B0D20AB");

            entity.HasOne(d => d.Uom).WithMany(p => p.OrderItems)
                .HasForeignKey(d => d.UomId)
                .HasConstraintName("FK__OrderItem__UomID__4C0144E4");

            entity.HasOne(d => d.Warehouse).WithMany(p => p.OrderItems)
                .HasForeignKey(d => d.WarehouseId)
                .HasConstraintName("FK__OrderItem__Wareh__4CF5691D");
        });

        modelBuilder.Entity<Product>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK__Inventor__3214EC27426EF885");

            entity.HasIndex(e => e.CategoryId, "IX_Inventory_CategoryID");

            entity.HasIndex(e => e.PartNo, "IX_Inventory_PartNo");

            entity.HasIndex(e => e.PreferredSupplierId, "IX_Inventory_PreferredSupplierID");

            entity.HasIndex(e => e.WarehouseId, "IX_Inventory_WarehouseID");

            entity.HasIndex(e => e.SubCategoryId, "IX_Products_SubCategoryID");

            entity.HasIndex(e => e.PartNo, "UQ__Inventor__7C3FF6B67DFB4EBD").IsUnique();

            entity.Property(e => e.Id).HasColumnName("ID");
            entity.Property(e => e.Barcode).HasMaxLength(100);
            entity.Property(e => e.BatchTracking).HasDefaultValue(false);
            entity.Property(e => e.Buid).HasColumnName("BUID");
            entity.Property(e => e.CategoryId).HasColumnName("CategoryID");
            entity.Property(e => e.CountryOfOrigin).HasMaxLength(100);
            entity.Property(e => e.CreatedBy).HasMaxLength(255);
            entity.Property(e => e.Depth).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.Dimensions).HasMaxLength(100);
            entity.Property(e => e.DocId)
                .HasMaxLength(10)
                .IsUnicode(false);
            entity.Property(e => e.FinalLandedCost).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.FinalSalesPrice).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.Height).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.Hscode)
                .HasMaxLength(50)
                .HasColumnName("HSCode");
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.IsCatalogItem).HasDefaultValue(true);
            entity.Property(e => e.ModelNo).HasMaxLength(100);
            entity.Property(e => e.ModifiedBy).HasMaxLength(255);
            entity.Property(e => e.PartNo).HasMaxLength(100);
            entity.Property(e => e.PreferredSupplierId).HasColumnName("PreferredSupplierID");
            entity.Property(e => e.ProductName).HasMaxLength(100);
            entity.Property(e => e.Qrcode)
                .HasMaxLength(100)
                .HasColumnName("QRCode");
            entity.Property(e => e.QtyOnHand).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.ReorderPoint).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.SellingPrice).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.SerialTracking).HasDefaultValue(false);
            entity.Property(e => e.SubCategoryId).HasColumnName("SubCategoryID");
            entity.Property(e => e.UnitCost).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.UomId).HasColumnName("UomID");
            entity.Property(e => e.WarehouseId).HasColumnName("WarehouseID");
            entity.Property(e => e.Weight).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.Width).HasColumnType("decimal(18, 2)");

            entity.HasOne(d => d.Bu).WithMany(p => p.Products)
                .HasForeignKey(d => d.Buid)
                .HasConstraintName("FK__Products__BUID");

            entity.HasOne(d => d.Category).WithMany(p => p.Products)
                .HasForeignKey(d => d.CategoryId)
                .HasConstraintName("FK__Products__Categ");

            entity.HasOne(d => d.PreferredSupplier).WithMany(p => p.Products)
                .HasForeignKey(d => d.PreferredSupplierId)
                .HasConstraintName("FK__Products__Prefe");

            entity.HasOne(d => d.SubCategory).WithMany(p => p.Products)
                .HasForeignKey(d => d.SubCategoryId)
                .HasConstraintName("FK_Products_ProductSubCategories");

            entity.HasOne(d => d.Uom).WithMany(p => p.Products)
                .HasForeignKey(d => d.UomId)
                .HasConstraintName("FK_Products_setUOM");

            entity.HasOne(d => d.Warehouse).WithMany(p => p.Products)
                .HasForeignKey(d => d.WarehouseId)
                .HasConstraintName("FK__Products__Wareh");
        });

        modelBuilder.Entity<ProductAttachment>(entity =>
        {
            entity.HasKey(e => e.AttachmentId).HasName("PK__Inventor__442C64DEB528BA1B");

            entity.HasIndex(e => e.InventoryId, "IX_InventoryAttachments_InventoryID");

            entity.Property(e => e.AttachmentId).HasColumnName("AttachmentID");
            entity.Property(e => e.CreatedBy).HasMaxLength(255);
            entity.Property(e => e.Description).HasMaxLength(255);
            entity.Property(e => e.FileName).HasMaxLength(255);
            entity.Property(e => e.InventoryId).HasColumnName("InventoryID");
            entity.Property(e => e.Locations).HasMaxLength(500);
            entity.Property(e => e.ModifiedBy).HasMaxLength(255);
            entity.Property(e => e.UploadDate).HasDefaultValueSql("now()");

            entity.HasOne(d => d.Inventory).WithMany(p => p.ProductAttachments)
                .HasForeignKey(d => d.InventoryId)
                .HasConstraintName("FK__Inventory__Inven__42E1EEFE");

            entity.HasOne(d => d.UploadedByNavigation).WithMany(p => p.ProductAttachments)
                .HasForeignKey(d => d.UploadedBy)
                .HasConstraintName("FK__Inventory__Uploa__44CA3770");
        });

        modelBuilder.Entity<ProductCategory>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK__Inventor__3214EC27EA9C64B5");

            entity.HasIndex(e => e.BusinessUnitId, "IX_InventoryCategories_BusinessUnitID");

            entity.HasIndex(e => e.CategoryName, "IX_InventoryCategories_CategoryName");

            entity.Property(e => e.Id).HasColumnName("ID");
            entity.Property(e => e.BusinessUnitId).HasColumnName("BusinessUnitID");
            entity.Property(e => e.CategoryName).HasMaxLength(100);
            entity.Property(e => e.CreatedBy).HasMaxLength(255);
            entity.Property(e => e.Description).HasMaxLength(255);
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.ModifiedBy).HasMaxLength(255);
            entity.Property(e => e.ParentCategoryId).HasColumnName("ParentCategoryID");

            entity.HasOne(d => d.BusinessUnit).WithMany(p => p.ProductCategories)
                .HasForeignKey(d => d.BusinessUnitId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK__Inventory__Busin__534D60F1");

            entity.HasOne(d => d.ParentCategory).WithMany(p => p.InverseParentCategory)
                .HasForeignKey(d => d.ParentCategoryId)
                .HasConstraintName("FK__Inventory__Paren__52593CB8");
        });

        modelBuilder.Entity<ProductSubCategory>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK__ProductS__3214EC2758B5F2D2");

            entity.HasIndex(e => e.BusinessUnitId, "IX_ProductSubCategories_BusinessUnitID");

            entity.HasIndex(e => e.IsActive, "IX_ProductSubCategories_IsActive");

            entity.Property(e => e.Id).HasColumnName("ID");
            entity.Property(e => e.BusinessUnitId).HasColumnName("BusinessUnitID");
            entity.Property(e => e.CreatedBy).HasMaxLength(100);
            entity.Property(e => e.CreatedOn)
                .HasDefaultValueSql("now()");
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.ModifiedBy).HasMaxLength(100);
            entity.Property(e => e.ModifiedOn);
            entity.Property(e => e.SubCategoryName).HasMaxLength(200);

            entity.HasOne(d => d.BusinessUnit).WithMany(p => p.ProductSubCategories)
                .HasForeignKey(d => d.BusinessUnitId)
                .HasConstraintName("FK_ProductSubCategories_BusinessUnits");
        });

        modelBuilder.Entity<Quote>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK__Quotes__3214EC27B0FC1337");

            entity.HasIndex(e => new { e.Rfqid, e.CustomerId, e.StatusId }, "IX_Quotes_Helper");

            entity.HasIndex(e => e.QuoteNo, "IX_Quotes_QuoteNo");

            entity.HasIndex(e => new { e.BusinessUnitId, e.CommercialCaseId }, "IX_Quotes_BusinessUnitID_CommercialCaseID");

            entity.HasIndex(e => new { e.BusinessUnitId, e.NexoraSerial }, "IX_Quotes_BusinessUnitID_NexoraSerial");

            entity.Property(e => e.Id).HasColumnName("ID");
            entity.Property(e => e.BusinessUnitId).HasColumnName("BusinessUnitID");
            entity.Property(e => e.CreatedBy).HasMaxLength(255);
            entity.Property(e => e.CreatedDate).HasDefaultValueSql("now()");
            entity.Property(e => e.CurrencyId).HasColumnName("CurrencyID");
            entity.Property(e => e.CustomerId).HasColumnName("CustomerID");
            entity.Property(e => e.ContactId).HasColumnName("ContactID");
            entity.Property(e => e.CommercialCaseId).HasColumnName("CommercialCaseID");
            entity.Property(e => e.NexoraSerial).HasMaxLength(100);
            entity.Property(e => e.DiscountValue).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.ModifiedBy).HasMaxLength(255);
            entity.Property(e => e.QuoteDate).HasDefaultValueSql("now()");
            entity.Property(e => e.QuoteNo).HasMaxLength(50);
            entity.Property(e => e.Rfqid).HasColumnName("RFQID");
            entity.Property(e => e.StatusId).HasColumnName("StatusID");
            entity.Property(e => e.TotalAmount).HasColumnType("decimal(18, 2)");

            entity.HasOne(d => d.BusinessUnit).WithMany(p => p.Quotes)
                .HasForeignKey(d => d.BusinessUnitId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Quotes_BusinessUnits");

            entity.HasOne(d => d.Currency).WithMany(p => p.Quotes)
                .HasForeignKey(d => d.CurrencyId)
                .HasConstraintName("FK_Quotes_Currency");

            entity.HasOne(d => d.Customer).WithMany(p => p.Quotes)
                .HasForeignKey(d => d.CustomerId)
                .HasConstraintName("FK_Quotes_Customers");

            entity.HasOne(d => d.DiscountType).WithMany(p => p.QuoteDiscountTypes)
                .HasForeignKey(d => d.DiscountTypeId)
                .HasConstraintName("FK_Quote_DiscountType");

            entity.HasOne(d => d.Rfq).WithMany(p => p.Quotes)
                .HasForeignKey(d => d.Rfqid)
                .HasConstraintName("FK_Quotes_RFQ");

            entity.HasOne(d => d.Status).WithMany(p => p.QuoteStatuses)
                .HasForeignKey(d => d.StatusId)
                .HasConstraintName("FK_Quotes_Status");

            entity.HasOne<CommercialCase>().WithMany()
                .HasForeignKey(e => new { e.BusinessUnitId, e.CommercialCaseId })
                .HasPrincipalKey(e => new { e.BusinessUnitId, e.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<QuoteConfiguration>(entity =>
        {
            entity.ToTable("QuoteConfiguration");

            entity.HasIndex(e => e.BusinessUnitId, "UQ_QuoteConfiguration_BusinessUnitId").IsUnique();

            entity.Property(e => e.CompanyAddress).HasMaxLength(500);
            entity.Property(e => e.CompanyEmail).HasMaxLength(255);
            entity.Property(e => e.CompanyPhone).HasMaxLength(100);
            entity.Property(e => e.FooterText).HasMaxLength(500);
            entity.Property(e => e.ModifiedBy).HasMaxLength(100);
            entity.Property(e => e.ModifiedOn).HasDefaultValueSql("now()");
            entity.Property(e => e.PrimaryColor).HasMaxLength(20);

            entity.HasOne(d => d.BusinessUnit).WithOne(p => p.QuoteConfiguration)
                .HasForeignKey<QuoteConfiguration>(d => d.BusinessUnitId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_QuoteConfiguration_BusinessUnit");
        });

        modelBuilder.Entity<QuoteItem>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK__QuoteIte__3214EC27B021232E");

            entity.Property(e => e.Id).HasColumnName("ID");
            entity.Property(e => e.CreatedBy).HasMaxLength(255);
            entity.Property(e => e.CreatedDate).HasDefaultValueSql("now()");
            entity.Property(e => e.Discount)
                .HasDefaultValue(0m)
                .HasColumnType("decimal(18, 6)");
            entity.Property(e => e.DiscountValue).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.ModifiedBy).HasMaxLength(255);
            entity.Property(e => e.ProductId).HasColumnName("ProductID");
            entity.Property(e => e.Quantity).HasColumnType("decimal(18, 6)");
            entity.Property(e => e.QuoteId).HasColumnName("QuoteID");
            entity.Property(e => e.RfqitemId).HasColumnName("RFQItemID");
            entity.Property(e => e.TaxAmount)
                .HasDefaultValue(0m)
                .HasColumnType("decimal(18, 6)");
            entity.Property(e => e.TotalAmount).HasColumnType("decimal(18, 6)");
            entity.Property(e => e.UnitPrice).HasColumnType("decimal(18, 6)");

            entity.HasOne(d => d.DiscountType).WithMany(p => p.QuoteItems)
                .HasForeignKey(d => d.DiscountTypeId)
                .HasConstraintName("FK_QuoteItem_DiscountType");

            entity.HasOne(d => d.Product).WithMany(p => p.QuoteItems)
                .HasForeignKey(d => d.ProductId)
                .HasConstraintName("FK_QuoteItems_Products");

            entity.HasOne(d => d.Quote).WithMany(p => p.QuoteItems)
                .HasForeignKey(d => d.QuoteId)
                .HasConstraintName("FK_QuoteItems_Quotes");

            entity.HasOne(d => d.Rfqitem).WithMany(p => p.QuoteItems)
                .HasForeignKey(d => d.RfqitemId)
                .HasConstraintName("FK_QuoteItems_RFQItems");
        });

        modelBuilder.Entity<Rfq>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK__RFQ__3214EC27E71B0249");

            entity.ToTable("RFQ");

            entity.HasIndex(e => new { e.BusinessUnitId, e.CommercialCaseId }, "IX_RFQ_BusinessUnitID_CommercialCaseID");

            entity.HasIndex(e => new { e.BusinessUnitId, e.NexoraSerial }, "IX_RFQ_BusinessUnitID_NexoraSerial");

            entity.Property(e => e.Id).HasColumnName("ID");
            entity.Property(e => e.BiddingDecision).HasMaxLength(200);
            entity.Property(e => e.BusinessUnitId).HasColumnName("BusinessUnitID");
            entity.Property(e => e.BuyersName).HasMaxLength(1020);
            entity.Property(e => e.CreatedBy).HasMaxLength(40);
            entity.Property(e => e.CreatedDate).HasDefaultValueSql("now()");
            entity.Property(e => e.CustomerId).HasColumnName("CustomerID");
            entity.Property(e => e.ContactId).HasColumnName("ContactID");
            entity.Property(e => e.CommercialCaseId).HasColumnName("CommercialCaseID");
            entity.Property(e => e.NexoraSerial).HasMaxLength(100);
            entity.Property(e => e.DurationAgreement).HasMaxLength(200);
            entity.Property(e => e.LeadId).HasColumnName("LeadID");
            entity.Property(e => e.ModifiedBy).HasMaxLength(40);
            entity.Property(e => e.OpportunityNo).HasMaxLength(200);
            entity.Property(e => e.Rfqno)
                .HasMaxLength(200)
                .HasColumnName("RFQNo");
            entity.Property(e => e.RfqstatusId).HasColumnName("RFQStatusID");
            entity.Property(e => e.Rfqtype)
                .HasMaxLength(100)
                .HasColumnName("RFQType");
            entity.Property(e => e.RfqtypeId).HasColumnName("RFQTypeID");

            entity.HasOne(d => d.BusinessUnit).WithMany(p => p.Rfqs)
                .HasForeignKey(d => d.BusinessUnitId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_RFQ_BusinessUnitID");

            entity.HasOne(d => d.Customer).WithMany(p => p.Rfqs).HasForeignKey(d => d.CustomerId);

            entity.HasOne(d => d.Lead).WithMany(p => p.Rfqs)
                .HasForeignKey(d => d.LeadId)
                .HasConstraintName("FK_RFQ_LeadID");

            entity.HasOne(d => d.Rfqstatus).WithMany(p => p.RfqRfqstatuses)
                .HasForeignKey(d => d.RfqstatusId)
                .HasConstraintName("FK_RFQ_StatusID");

            entity.HasOne(d => d.RfqtypeNavigation).WithMany(p => p.RfqRfqtypeNavigations)
                .HasForeignKey(d => d.RfqtypeId)
                .HasConstraintName("FK_RFQ_TypeID");

            entity.HasOne<CommercialCase>().WithMany()
                .HasForeignKey(e => new { e.BusinessUnitId, e.CommercialCaseId })
                .HasPrincipalKey(e => new { e.BusinessUnitId, e.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Rfqitem>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK__RFQItems__3214EC2712F05C03");

            entity.ToTable("RFQItems");

            entity.Property(e => e.Id).HasColumnName("ID");
            entity.Property(e => e.Aiconfidence)
                .HasColumnType("decimal(5, 4)")
                .HasColumnName("AIConfidence");
            entity.Property(e => e.AlternatePartNumber).HasMaxLength(200);
            entity.Property(e => e.AlternateProductName).HasMaxLength(400);
            entity.Property(e => e.Alternative).HasMaxLength(200);
            entity.Property(e => e.BuyerName).HasMaxLength(400);
            entity.Property(e => e.CommodityProduct).HasMaxLength(400);
            entity.Property(e => e.CompanyRef).HasMaxLength(200);
            entity.Property(e => e.CreatedBy).HasMaxLength(40);
            entity.Property(e => e.CreatedDate).HasDefaultValueSql("now()");
            entity.Property(e => e.Currency).HasMaxLength(20);
            entity.Property(e => e.CurrencyId).HasColumnName("CurrencyID");
            entity.Property(e => e.CustomerAccountPortalId)
                .HasMaxLength(200)
                .HasColumnName("CustomerAccountPortalID");
            entity.Property(e => e.CustomerRfqno)
                .HasMaxLength(200)
                .HasColumnName("CustomerRFQNo");
            entity.Property(e => e.ItemMaterialCode).HasMaxLength(200);
            entity.Property(e => e.ItemText).HasMaxLength(4000);
            entity.Property(e => e.LineItemNo).HasMaxLength(100);
            entity.Property(e => e.ManufacturerName).HasMaxLength(400);
            entity.Property(e => e.ManufacturerPartNumber).HasMaxLength(200);
            entity.Property(e => e.MaterialPotext)
                .HasMaxLength(4000)
                .HasColumnName("MaterialPOText");
            entity.Property(e => e.ModifiedBy).HasMaxLength(40);
            entity.Property(e => e.ProductId).HasColumnName("ProductID");
            entity.Property(e => e.Rfqid).HasColumnName("RFQID");
            entity.Property(e => e.StorageLocation).HasMaxLength(200);
            entity.Property(e => e.SupplierId).HasColumnName("SupplierID");
            entity.Property(e => e.UnitOfMeasure).HasMaxLength(200);
            entity.Property(e => e.UnitPrice).HasColumnType("decimal(18, 6)");
            entity.Property(e => e.WarehouseId).HasColumnName("WarehouseID");

            entity.HasOne(d => d.CurrencyNavigation).WithMany(p => p.Rfqitems)
                .HasForeignKey(d => d.CurrencyId)
                .HasConstraintName("FK_RFQItems_Currency");

            entity.HasOne(d => d.Product).WithMany(p => p.Rfqitems)
                .HasForeignKey(d => d.ProductId)
                .HasConstraintName("FK_RFQItems_Product");

            entity.HasOne(d => d.Rfq).WithMany(p => p.Rfqitems)
                .HasForeignKey(d => d.Rfqid)
                .HasConstraintName("FK_RFQItems_RFQ");

            entity.HasOne(d => d.Supplier).WithMany(p => p.Rfqitems)
                .HasForeignKey(d => d.SupplierId)
                .HasConstraintName("FK_RFQItems_Supplier");

            entity.HasOne(d => d.SupplierQuotedItem).WithMany(p => p.Rfqitems)
                .HasForeignKey(d => d.SupplierQuotedItemId)
                .HasConstraintName("FK_Rfqitems_SupplierQuotedItems");

            entity.HasOne(d => d.Uom).WithMany(p => p.Rfqitems)
                .HasForeignKey(d => d.UomId)
                .HasConstraintName("FK_RFQItems_UOM");

            entity.HasOne(d => d.Warehouse).WithMany(p => p.Rfqitems)
                .HasForeignKey(d => d.WarehouseId)
                .HasConstraintName("FK_RFQItems_Warehouse");
        });

        modelBuilder.Entity<RolePermission>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK__RolePerm__3214EC27212832A0");

            entity.HasIndex(e => e.BusinessUnitId, "IX_UserPermissions_BusinessUnitID");

            entity.HasIndex(e => e.ModuleId, "IX_UserPermissions_ModuleID");

            entity.Property(e => e.Id).HasColumnName("ID");
            entity.Property(e => e.BusinessUnitId).HasColumnName("BusinessUnitID");
            entity.Property(e => e.CanCreate).HasDefaultValue(false);
            entity.Property(e => e.CanDelete).HasDefaultValue(false);
            entity.Property(e => e.CanEdit).HasDefaultValue(false);
            entity.Property(e => e.CreatedBy).HasMaxLength(255);
            entity.Property(e => e.ModifiedBy).HasMaxLength(255);
            entity.Property(e => e.ModuleId).HasColumnName("ModuleID");
            entity.Property(e => e.RoleId).HasColumnName("RoleID");

            entity.HasOne(d => d.BusinessUnit).WithMany(p => p.RolePermissions)
                .HasForeignKey(d => d.BusinessUnitId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK__RolePermi__Busin__05D8E0BE");

            entity.HasOne(d => d.Module).WithMany(p => p.RolePermissions)
                .HasForeignKey(d => d.ModuleId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK__RolePermi__Modul__04E4BC85");

            entity.HasOne(d => d.Role).WithMany(p => p.RolePermissions)
                .HasForeignKey(d => new { d.BusinessUnitId, d.RoleId })
                .HasPrincipalKey(p => new { p.BusinessUnitId, p.SetupId })
                .HasConstraintName("FK__RolePermi__RoleI__03F0984C");
        });

        modelBuilder.Entity<SetCity>(entity =>
        {
            entity.HasKey(e => e.CityId).HasName("PK__SetCity__F2D21A961487DC00");

            entity.ToTable("SetCity");

            entity.Property(e => e.CityId).HasColumnName("CityID");
            entity.Property(e => e.Buid).HasColumnName("BUID");
            entity.Property(e => e.CityName).HasMaxLength(100);
            entity.Property(e => e.CountryId).HasColumnName("CountryID");
            entity.Property(e => e.CreatedBy).HasMaxLength(50);
            entity.Property(e => e.CreatedDate)
                .HasDefaultValueSql("now()");
            entity.Property(e => e.Description).HasMaxLength(255);
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.ModifiedBy).HasMaxLength(50);
            entity.Property(e => e.ModifiedDate);
            entity.Property(e => e.StateId).HasColumnName("StateID");

            entity.HasOne(d => d.Bu).WithMany(p => p.SetCities)
                .HasForeignKey(d => d.Buid)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_City_BusinessUnit");

            entity.HasOne(d => d.Country).WithMany(p => p.SetCities)
                .HasForeignKey(d => d.CountryId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_City_Country");

            entity.HasOne(d => d.State).WithMany(p => p.SetCities)
                .HasForeignKey(d => d.StateId)
                .HasConstraintName("FK_City_State");
        });

        modelBuilder.Entity<SetCountry>(entity =>
        {
            entity.HasKey(e => e.CountryId).HasName("PK__SetCount__10D160BF33E5BD3A");

            entity.ToTable("SetCountry");

            entity.Property(e => e.CountryId).HasColumnName("CountryID");
            entity.Property(e => e.Buid).HasColumnName("BUID");
            entity.Property(e => e.CountryCode).HasMaxLength(10);
            entity.Property(e => e.CountryName).HasMaxLength(100);
            entity.Property(e => e.CreatedBy).HasMaxLength(50);
            entity.Property(e => e.CreatedDate)
                .HasDefaultValueSql("now()");
            entity.Property(e => e.Description).HasMaxLength(255);
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.ModifiedBy).HasMaxLength(50);
            entity.Property(e => e.ModifiedDate);

            entity.HasOne(d => d.Bu).WithMany(p => p.SetCountries)
                .HasForeignKey(d => d.Buid)
                .HasConstraintName("FK_Country_BusinessUnit");
        });

        modelBuilder.Entity<SetState>(entity =>
        {
            entity.HasKey(e => e.StateId).HasName("PK__SetState__C3BA3B5A26295488");

            entity.ToTable("SetState");

            entity.Property(e => e.StateId).HasColumnName("StateID");
            entity.Property(e => e.Buid).HasColumnName("BUID");
            entity.Property(e => e.CountryId).HasColumnName("CountryID");
            entity.Property(e => e.CreatedBy).HasMaxLength(50);
            entity.Property(e => e.CreatedDate)
                .HasDefaultValueSql("now()");
            entity.Property(e => e.Description).HasMaxLength(255);
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.ModifiedBy).HasMaxLength(50);
            entity.Property(e => e.ModifiedDate);
            entity.Property(e => e.StateCode).HasMaxLength(10);
            entity.Property(e => e.StateName).HasMaxLength(100);

            entity.HasOne(d => d.Bu).WithMany(p => p.SetStates)
                .HasForeignKey(d => d.Buid)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_State_BusinessUnit");

            entity.HasOne(d => d.Country).WithMany(p => p.SetStates)
                .HasForeignKey(d => d.CountryId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_State_Country");
        });

        modelBuilder.Entity<SetUom>(entity =>
        {
            entity.HasKey(e => e.UomId).HasName("PK__setUOM__F6F8D59E4737F405");

            entity.ToTable("setUOM");

            entity.Property(e => e.UomId).HasColumnName("UomID");
            entity.Property(e => e.BusinessUnitId).HasColumnName("BusinessUnitID");
            entity.Property(e => e.CreatedBy).HasMaxLength(50);
            entity.Property(e => e.CreatedDate)
                .HasDefaultValueSql("now()");
            entity.Property(e => e.Description).HasMaxLength(255);
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.ModifiedBy).HasMaxLength(50);
            entity.Property(e => e.ModifiedDate);
            entity.Property(e => e.UomCode).HasMaxLength(50);
            entity.Property(e => e.UomName).HasMaxLength(100);

            entity.HasOne(d => d.BusinessUnit).WithMany(p => p.SetUoms)
                .HasForeignKey(d => d.BusinessUnitId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_RFQ_BusinessUnit");
        });

        modelBuilder.Entity<SetupMaster>(entity =>
        {
            entity.HasKey(e => e.SetupId).HasName("PK__Setup_Ma__C9C734B31BDDC1E2");

            entity.ToTable("Setup_Master");

            entity.HasIndex(e => e.BusinessUnitId, "IX_Setup_Master_BusinessUnitID");

            entity.Property(e => e.SetupId).HasColumnName("SetupID");
            entity.Property(e => e.BusinessUnitId).HasColumnName("BusinessUnitID");
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.ParentSetupId).HasColumnName("ParentSetupID");
            entity.Property(e => e.SetupCode).HasMaxLength(100);
            entity.Property(e => e.SetupType).HasMaxLength(100);

            entity.HasOne(d => d.BusinessUnit).WithMany(p => p.SetupMasters)
                .HasForeignKey(d => d.BusinessUnitId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK__Setup_Mas__Busin__68487DD7");

            entity.HasOne(d => d.ParentSetup).WithMany(p => p.InverseParentSetup)
                .HasForeignKey(d => d.ParentSetupId)
                .HasConstraintName("FK__Setup_Mas__Paren__6754599E");
        });

        modelBuilder.Entity<Shipment>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK__Shipment__3214EC2732EE97FF");

            entity.Property(e => e.Id).HasColumnName("ID");
            entity.Property(e => e.ActualDeliveryDate);
            entity.Property(e => e.BusinessUnitId).HasColumnName("BusinessUnitID");
            entity.Property(e => e.Carrier).HasMaxLength(100);
            entity.Property(e => e.CreatedBy).HasMaxLength(255);
            entity.Property(e => e.CreatedOn)
                .HasDefaultValueSql("now()");
            entity.Property(e => e.EstimatedDeliveryDate);
            entity.Property(e => e.ExternalId)
                .HasMaxLength(255)
                .HasColumnName("ExternalID");
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.LabelUrl).HasMaxLength(500);
            entity.Property(e => e.ModifiedBy).HasMaxLength(255);
            entity.Property(e => e.ModifiedOn);
            entity.Property(e => e.Notes).HasMaxLength(500);
            entity.Property(e => e.OrderId).HasColumnName("OrderID");
            entity.Property(e => e.RawResponse).HasMaxLength(500);
            entity.Property(e => e.ServiceLevel).HasMaxLength(100);
            entity.Property(e => e.ShipmentDate);
            entity.Property(e => e.ShipmentNo).HasMaxLength(50);
            entity.Property(e => e.ShippingAddress).HasMaxLength(500);
            entity.Property(e => e.ShippingCost).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.StatusId).HasColumnName("StatusID");
            entity.Property(e => e.TrackingNumber).HasMaxLength(100);

            entity.HasOne(d => d.BusinessUnit).WithMany(p => p.Shipments)
                .HasForeignKey(d => d.BusinessUnitId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Shipments_BusinessUnits");

            entity.HasOne(d => d.Order).WithMany(p => p.Shipments)
                .HasForeignKey(d => d.OrderId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Shipments_Orders");

            entity.HasOne(d => d.Status).WithMany(p => p.Shipments)
                .HasForeignKey(d => d.StatusId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Shipments_Status");
        });

        modelBuilder.Entity<ShipmentItem>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK__Shipment__3214EC27B4DD8C7A");

            entity.Property(e => e.Id).HasColumnName("ID");
            entity.Property(e => e.CreatedBy).HasMaxLength(255);
            entity.Property(e => e.CreatedOn)
                .HasDefaultValueSql("now()");
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.ModifiedBy).HasMaxLength(255);
            entity.Property(e => e.ModifiedOn);
            entity.Property(e => e.Notes).HasMaxLength(500);
            entity.Property(e => e.OrderItemId).HasColumnName("OrderItemID");
            entity.Property(e => e.Quantity).HasColumnType("decimal(18, 6)");
            entity.Property(e => e.ShipmentId).HasColumnName("ShipmentID");

            entity.HasOne(d => d.OrderItem).WithMany(p => p.ShipmentItems)
                .HasForeignKey(d => d.OrderItemId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_ShipmentItems_OrderItems");

            entity.HasOne(d => d.Shipment).WithMany(p => p.ShipmentItems)
                .HasForeignKey(d => d.ShipmentId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_ShipmentItems_Shipments");
        });

        modelBuilder.Entity<ShipmentStatusHistory>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK__Shipment__3214EC0749B79ADB");

            entity.ToTable("ShipmentStatusHistory");

            entity.HasIndex(e => e.ShipmentId, "IX_ShipmentStatusHistory_ShipmentId");

            entity.Property(e => e.ChangedBy).HasMaxLength(255);
            entity.Property(e => e.Notes).HasMaxLength(600);

            entity.HasOne(d => d.NewStatus).WithMany(p => p.ShipmentStatusHistoryNewStatuses)
                .HasForeignKey(d => d.NewStatusId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_ShipmentStatusHistory_NewStatus");

            entity.HasOne(d => d.PreviousStatus).WithMany(p => p.ShipmentStatusHistoryPreviousStatuses)
                .HasForeignKey(d => d.PreviousStatusId)
                .HasConstraintName("FK_ShipmentStatusHistory_PreviousStatus");

            entity.HasOne(d => d.Shipment).WithMany(p => p.ShipmentStatusHistories)
                .HasForeignKey(d => d.ShipmentId)
                .HasConstraintName("FK_ShipmentStatusHistory_Shipments");
        });

        modelBuilder.Entity<Supplier>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK__Supplier__3214EC2782495266");
            entity.HasAlternateKey(e => new { e.Id, e.Buid }).HasName("AK_Suppliers_ID_BUID");

            entity.HasIndex(e => e.ContactEmail, "IX_Suppliers_ContactEmail");

            entity.HasIndex(e => e.Name, "IX_Suppliers_Name");

            entity.HasIndex(e => e.ContactEmail, "UQ__Supplier__FFA796CDFB352BC7").IsUnique();

            entity.Property(e => e.Id).HasColumnName("ID");
            entity.Property(e => e.AddressLine1).HasMaxLength(255);
            entity.Property(e => e.AddressLine2).HasMaxLength(255);
            entity.Property(e => e.Buid).HasColumnName("BUID");
            entity.Property(e => e.CityId).HasColumnName("CityID");
            entity.Property(e => e.ContactEmail).HasColumnType("citext"); // case-insensitive unique email
            entity.Property(e => e.CountryId).HasColumnName("CountryID");
            entity.Property(e => e.CreatedBy).HasMaxLength(255);
            entity.Property(e => e.CurrencyId).HasColumnName("CurrencyID");
            entity.Property(e => e.DocId)
                .HasMaxLength(10)
                .IsUnicode(false);
            entity.Property(e => e.ImageUrl)
                .HasMaxLength(100)
                .HasColumnName("ImageURL");
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.ModifiedBy).HasMaxLength(255);
            entity.Property(e => e.Name).HasMaxLength(255);
            entity.Property(e => e.PaymentTerms).HasMaxLength(255);
            entity.Property(e => e.PostalCode).HasMaxLength(20);
            entity.Property(e => e.SuccessRate).HasColumnType("decimal(5, 2)");

            entity.HasOne(d => d.Bu).WithMany(p => p.Suppliers)
                .HasForeignKey(d => d.Buid)
                .HasConstraintName("FK__Suppliers__BUID__1332DBDC");

            entity.HasOne(d => d.City).WithMany(p => p.Suppliers)
                .HasForeignKey(d => d.CityId)
                .HasConstraintName("FK_Suppliers_City");

            entity.HasOne(d => d.Country).WithMany(p => p.Suppliers)
                .HasForeignKey(d => d.CountryId)
                .HasConstraintName("FK_Suppliers_Country");

            entity.HasOne(d => d.Currency).WithMany(p => p.Suppliers)
                .HasForeignKey(d => d.CurrencyId)
                .HasConstraintName("FK__Suppliers__Curre__123EB7A3");
        });

        modelBuilder.Entity<SupplierPurchaseHistory>(entity =>
        {
            entity.ToTable("SupplierPurchaseHistory");

            entity.Property(e => e.BatchNo).HasMaxLength(100);
            entity.Property(e => e.CreatedBy).HasMaxLength(255);
            entity.Property(e => e.CreatedOn)
                .HasDefaultValueSql("now()");
            entity.Property(e => e.Currency).HasMaxLength(10);
            entity.Property(e => e.PoDocId).HasMaxLength(10);
            entity.Property(e => e.PurchaseDate)
                .HasDefaultValueSql("now()");
            entity.Property(e => e.Quantity).HasColumnType("decimal(18, 6)");
            entity.Property(e => e.UnitPrice).HasColumnType("decimal(18, 6)");

            entity.HasOne(d => d.Product).WithMany(p => p.SupplierPurchaseHistories)
                .HasForeignKey(d => d.ProductId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_SupplierPurchaseHistory_Products");

            entity.HasOne(d => d.Supplier).WithMany(p => p.SupplierPurchaseHistories)
                .HasForeignKey(d => d.SupplierId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_SupplierPurchaseHistory_Suppliers");
        });

        modelBuilder.Entity<SupplierQuotedItem>(entity =>
        {
            entity.Property(e => e.CreatedBy).HasMaxLength(256);
            entity.Property(e => e.CreatedDate)
                .HasDefaultValueSql("now()");
            entity.Property(e => e.DiscountAmount).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.ItemName).HasMaxLength(500);
            entity.Property(e => e.ModifiedBy).HasMaxLength(256);
            entity.Property(e => e.ModifiedDate);
            entity.Property(e => e.Quantity).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.QuoteDate);
            entity.Property(e => e.QuoteReference).HasMaxLength(100);
            entity.Property(e => e.TaxAmount).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.UnitPrice).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.ValidUntil);

            entity.HasOne(d => d.BusinessUnit).WithMany(p => p.SupplierQuotedItems)
                .HasForeignKey(d => d.BusinessUnitId)
                .HasConstraintName("FK_SupplierQuotedItems_BusinessUnits");

            entity.HasOne(d => d.Currency).WithMany(p => p.SupplierQuotedItems)
                .HasForeignKey(d => d.CurrencyId)
                .HasConstraintName("FK_SupplierQuotedItems_Currency");

            entity.HasOne(d => d.Supplier).WithMany(p => p.SupplierQuotedItems)
                .HasForeignKey(d => d.SupplierId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_SupplierQuotedItems_Suppliers");

            entity.HasOne(d => d.Uom).WithMany(p => p.SupplierQuotedItems)
                .HasForeignKey(d => d.UomId)
                .HasConstraintName("FK_SupplierQuotedItems_SetUoms");
        });

        modelBuilder.Entity<Team>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK__Teams__3214EC27A735D5D4");

            entity.HasIndex(e => e.BusinessUnitId, "IX_Teams_BusinessUnitID");

            entity.Property(e => e.Id).HasColumnName("ID");
            entity.Property(e => e.BusinessUnitId).HasColumnName("BusinessUnitID");
            entity.Property(e => e.CreatedBy).HasMaxLength(255);
            entity.Property(e => e.ManagerId).HasColumnName("ManagerID");
            entity.Property(e => e.ModifiedBy).HasMaxLength(255);
            entity.Property(e => e.SubTeamId).HasColumnName("SubTeamID");
            entity.Property(e => e.TeamName).HasMaxLength(255);

            entity.HasOne(d => d.BusinessUnit).WithMany(p => p.Teams)
                .HasForeignKey(d => d.BusinessUnitId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK__Teams__BusinessU__70DDC3D8");

            entity.HasOne(d => d.Manager).WithMany(p => p.Teams)
                .HasForeignKey(d => d.ManagerId)
                .HasConstraintName("FK_Teams_Users");

            entity.HasOne(d => d.SubTeam).WithMany(p => p.InverseSubTeam)
                .HasForeignKey(d => d.SubTeamId)
                .HasConstraintName("FK__Teams__SubTeamID__6FE99F9F");
        });

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK__Users__3214EC279AB429D5");

            entity.HasIndex(e => e.Buid, "IX_Users_BUID");

            entity.HasIndex(e => e.Email, "IX_Users_Email");

            entity.HasIndex(e => e.IsActive, "IX_Users_IsActive");

            entity.HasIndex(e => e.Email, "UQ__Users__A9D10534A3A2A11E").IsUnique();

            entity.Property(e => e.Id).HasColumnName("ID");
            entity.Property(e => e.Buid).HasColumnName("BUID");
            entity.Property(e => e.CreatedBy).HasMaxLength(255);
            entity.Property(e => e.Email).HasColumnType("citext"); // case-insensitive unique email
            entity.Property(e => e.FirstName).HasMaxLength(100);
            entity.Property(e => e.ImageUrl)
                .HasMaxLength(100)
                .HasColumnName("ImageURL");
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.LastName).HasMaxLength(100);
            entity.Property(e => e.ManagerId).HasColumnName("ManagerID");
            entity.Property(e => e.MiddleName).HasMaxLength(100);
            entity.Property(e => e.ModifiedBy).HasMaxLength(255);
            entity.Property(e => e.PasswordHash)
                .HasMaxLength(255)
                .HasColumnName("Password_Hash");
            entity.Property(e => e.Region).HasMaxLength(100);
            entity.Property(e => e.RoleId).HasColumnName("RoleID");
            entity.Property(e => e.TeamId).HasColumnName("TeamID");
            entity.Property(e => e.Timezone).HasMaxLength(50);
            entity.Property(e => e.UserGroupId).HasColumnName("UserGroupID");

            entity.HasOne(d => d.Bu).WithMany(p => p.Users)
                .HasForeignKey(d => d.Buid)
                .HasConstraintName("FK__Users__BUID__7D439ABD");

            entity.HasOne(d => d.Manager).WithMany(p => p.InverseManager)
                .HasForeignKey(d => d.ManagerId)
                .HasConstraintName("FK_Users_Manager");

            entity.HasOne(d => d.Role).WithMany(p => p.Users)
                .HasForeignKey(d => new { d.Buid, d.RoleId })
                .HasPrincipalKey(p => new { p.BusinessUnitId, p.SetupId })
                .HasConstraintName("FK__Users__RoleID__7B5B524B");

            entity.HasOne(d => d.Team).WithMany(p => p.Users)
                .HasForeignKey(d => d.TeamId)
                .HasConstraintName("FK__Users__TeamID__7C4F7684");

            entity.HasOne(d => d.UserGroup).WithMany(p => p.Users)
                .HasForeignKey(d => d.UserGroupId)
                .HasConstraintName("FK__Users__UserGroup__7E37BEF6");
        });

        modelBuilder.Entity<UserGroup>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK__UserGrou__3214EC277F8DF4F8");

            entity.HasIndex(e => e.BusinessUnitId, "IX_UserGroups_BusinessUnitID");

            entity.Property(e => e.Id).HasColumnName("ID");
            entity.Property(e => e.BusinessUnitId).HasColumnName("BusinessUnitID");
            entity.Property(e => e.CreatedBy).HasMaxLength(255);
            entity.Property(e => e.ModifiedBy).HasMaxLength(255);
            entity.Property(e => e.UserGroupsName).HasMaxLength(255);

            entity.HasOne(d => d.BusinessUnit).WithMany(p => p.UserGroups)
                .HasForeignKey(d => d.BusinessUnitId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK__UserGroup__Busin__73BA3083");
        });

        modelBuilder.Entity<ViewSupplierPriceList>(entity =>
        {
            entity
                .HasNoKey()
                .ToView("View_SupplierPriceList");

            entity.Property(e => e.Currency).HasMaxLength(10);
            entity.Property(e => e.LastPurchasedDate);
            entity.Property(e => e.LatestPrice).HasColumnType("decimal(18, 6)");
            entity.Property(e => e.PartNo).HasMaxLength(100);
            entity.Property(e => e.ProductName).HasMaxLength(100);
            entity.Property(e => e.SupplierName).HasMaxLength(255);
        });

        modelBuilder.Entity<Warehouse>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK__Warehous__3214EC27E9A0A7EE");

            entity.HasIndex(e => new { e.WarehouseCode, e.BusinessUnitId }, "UQ_Warehouses_Code_BU").IsUnique();

            entity.Property(e => e.Id).HasColumnName("ID");
            entity.Property(e => e.AddressLine1).HasMaxLength(255);
            entity.Property(e => e.AddressLine2).HasMaxLength(255);
            entity.Property(e => e.BusinessUnitId).HasColumnName("BusinessUnitID");
            entity.Property(e => e.Capacity).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.City).HasMaxLength(100);
            entity.Property(e => e.ContactEmail).HasMaxLength(255);
            entity.Property(e => e.ContactPhone).HasMaxLength(50);
            entity.Property(e => e.Country).HasMaxLength(100);
            entity.Property(e => e.CreatedBy).HasMaxLength(255);
            entity.Property(e => e.CreatedOn).HasDefaultValueSql("now()");
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.Location).HasMaxLength(255);
            entity.Property(e => e.ManagerName).HasMaxLength(255);
            entity.Property(e => e.ModifiedBy).HasMaxLength(255);
            entity.Property(e => e.PostalCode).HasMaxLength(20);
            entity.Property(e => e.State).HasMaxLength(100);
            entity.Property(e => e.WarehouseCode).HasMaxLength(50);
            entity.Property(e => e.WarehouseName).HasMaxLength(100);

            entity.HasOne(d => d.BusinessUnit).WithMany(p => p.Warehouses)
                .HasForeignKey(d => d.BusinessUnitId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Warehouses_BusinessUnits");
        });

        // Taxis was present in the model layer but never mapped in the DbContext.
        // Map it so EF manages the Taxes table (unblocks server-side tax). The
        // source database has no rows for it, so this creates a fresh table.
        modelBuilder.Entity<Taxis>(entity =>
        {
            entity.ToTable("Taxes");

            entity.Property(e => e.Id).HasColumnName("ID");
            entity.Property(e => e.BusinessUnitId).HasColumnName("BusinessUnitID");
            entity.Property(e => e.TaxCode).HasMaxLength(50);
            entity.Property(e => e.TaxName).HasMaxLength(255);
            entity.Property(e => e.TaxType).HasMaxLength(50);
            entity.Property(e => e.TaxRate).HasColumnType("decimal(9, 4)");
            entity.Property(e => e.Country).HasMaxLength(100);
            entity.Property(e => e.State).HasMaxLength(100);
            entity.Property(e => e.IsInclusive).HasDefaultValue(false);
            entity.Property(e => e.IsCompound).HasDefaultValue(false);
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.CreatedBy).HasMaxLength(255);
            entity.Property(e => e.CreatedOn).HasDefaultValueSql("now()");
            entity.Property(e => e.ModifiedBy).HasMaxLength(255);

            // The orphan Inventory* classes are intentionally not mapped in this
            // context; ignore the reverse navigation so EF model discovery does
            // not pull them (and their whole graph) into the model.
            entity.Ignore(e => e.Inventories);

            entity.HasOne(d => d.BusinessUnit).WithMany()
                .HasForeignKey(d => d.BusinessUnitId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Taxes_BusinessUnits");
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
