using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class InitialBaseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Attachments",
                columns: table => new
                {
                    ID = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ParentType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ParentID = table.Column<long>(type: "bigint", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    FilePath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    MimeType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    FileSize = table.Column<long>(type: "bigint", nullable: true),
                    ContentType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedOn = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "(sysdatetime())"),
                    UploadedDate = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__Attachme__3214EC2740D763DA", x => x.ID);
                });

            migrationBuilder.CreateTable(
                name: "BusinessUnits",
                columns: table => new
                {
                    ID = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BusinessUnitCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    BusinessUnitName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: true, defaultValue: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "(getdate())"),
                    ModifiedBy = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    ModifiedOn = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__Business__3214EC27B5E4A97A", x => x.ID);
                });

            migrationBuilder.CreateTable(
                name: "Images",
                columns: table => new
                {
                    ID = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ResourceType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ResourceID = table.Column<long>(type: "bigint", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    FilePath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    MimeType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    UploadDate = table.Column<DateTime>(type: "datetime2", nullable: true, defaultValueSql: "(getdate())"),
                    UploadedBy = table.Column<long>(type: "bigint", nullable: true),
                    IsPrimary = table.Column<bool>(type: "bit", nullable: true, defaultValue: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    ModifiedOn = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__Images__3214EC27B2D5CCF9", x => x.ID);
                });

            migrationBuilder.CreateTable(
                name: "Module",
                columns: table => new
                {
                    ID = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ModuleName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: true, defaultValue: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    ModifiedOn = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__Module__3214EC276837F46D", x => x.ID);
                });

            migrationBuilder.CreateTable(
                name: "Currency",
                columns: table => new
                {
                    ID = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    CurrencyName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Symbol = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    ExchangeRate = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    IsBaseCurrency = table.Column<bool>(type: "bit", nullable: true, defaultValue: false),
                    BusinessUnitID = table.Column<long>(type: "bigint", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: true, defaultValue: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    ModifiedOn = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__Currency__3214EC2734927EB0", x => x.ID);
                    table.ForeignKey(
                        name: "FK__Currency__Busine__4E88ABD4",
                        column: x => x.BusinessUnitID,
                        principalTable: "BusinessUnits",
                        principalColumn: "ID");
                });

            migrationBuilder.CreateTable(
                name: "Customers",
                columns: table => new
                {
                    ID = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DocId = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: true),
                    Name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    ContactEmail = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    ImageURL = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    BillingAddressLine1 = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    BillingAddressLine2 = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    BillingCity = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    BillingState = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    BillingCountry = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    BillingPostalCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    ShippingAddressLine1 = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    ShippingAddressLine2 = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    ShippingCity = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ShippingState = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ShippingCountry = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ShippingPostalCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    BUID = table.Column<long>(type: "bigint", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: true, defaultValue: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    ModifiedOn = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__Customer__3214EC27D6DB6FD1", x => x.ID);
                    table.ForeignKey(
                        name: "FK__Customers__BUID__0D7A0286",
                        column: x => x.BUID,
                        principalTable: "BusinessUnits",
                        principalColumn: "ID");
                });

            migrationBuilder.CreateTable(
                name: "Email_Configurations",
                columns: table => new
                {
                    ID = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BusinessUnitID = table.Column<long>(type: "bigint", nullable: false),
                    ConfigurationName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    EmailAddress = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Protocol = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Host = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Port = table.Column<int>(type: "int", nullable: false),
                    Username = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Password = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    UseSSL = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    PollingInterval = table.Column<int>(type: "int", nullable: false, defaultValue: 300),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    CreatedOn = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "(sysdatetime())")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__Email_Co__3214EC278A1BB987", x => x.ID);
                    table.ForeignKey(
                        name: "FK__Email_Con__Busin__489AC854",
                        column: x => x.BusinessUnitID,
                        principalTable: "BusinessUnits",
                        principalColumn: "ID");
                });

            migrationBuilder.CreateTable(
                name: "ProductCategories",
                columns: table => new
                {
                    ID = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CategoryName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    ParentCategoryID = table.Column<long>(type: "bigint", nullable: true),
                    BusinessUnitID = table.Column<long>(type: "bigint", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: true, defaultValue: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    ModifiedOn = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__Inventor__3214EC27EA9C64B5", x => x.ID);
                    table.ForeignKey(
                        name: "FK__Inventory__Busin__534D60F1",
                        column: x => x.BusinessUnitID,
                        principalTable: "BusinessUnits",
                        principalColumn: "ID");
                    table.ForeignKey(
                        name: "FK__Inventory__Paren__52593CB8",
                        column: x => x.ParentCategoryID,
                        principalTable: "ProductCategories",
                        principalColumn: "ID");
                });

            migrationBuilder.CreateTable(
                name: "ProductSubCategories",
                columns: table => new
                {
                    ID = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SubCategoryName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    BusinessUnitID = table.Column<long>(type: "bigint", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: true, defaultValue: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedOn = table.Column<DateTime>(type: "datetime", nullable: true, defaultValueSql: "(getdate())"),
                    ModifiedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ModifiedOn = table.Column<DateTime>(type: "datetime", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__ProductS__3214EC2758B5F2D2", x => x.ID);
                    table.ForeignKey(
                        name: "FK_ProductSubCategories_BusinessUnits",
                        column: x => x.BusinessUnitID,
                        principalTable: "BusinessUnits",
                        principalColumn: "ID");
                });

            migrationBuilder.CreateTable(
                name: "QuoteConfiguration",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    Logo = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PrimaryColor = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    TermsAndConditions = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CompanyAddress = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CompanyPhone = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CompanyEmail = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    FooterText = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ModifiedOn = table.Column<DateTime>(type: "datetime2", nullable: true, defaultValueSql: "(sysdatetime())")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuoteConfiguration", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QuoteConfiguration_BusinessUnit",
                        column: x => x.BusinessUnitId,
                        principalTable: "BusinessUnits",
                        principalColumn: "ID");
                });

            migrationBuilder.CreateTable(
                name: "SetCountry",
                columns: table => new
                {
                    CountryID = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CountryCode = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    CountryName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    BUID = table.Column<long>(type: "bigint", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime", nullable: false, defaultValueSql: "(getdate())"),
                    ModifiedBy = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ModifiedDate = table.Column<DateTime>(type: "datetime", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__SetCount__10D160BF33E5BD3A", x => x.CountryID);
                    table.ForeignKey(
                        name: "FK_Country_BusinessUnit",
                        column: x => x.BUID,
                        principalTable: "BusinessUnits",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "setUOM",
                columns: table => new
                {
                    UomID = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BusinessUnitID = table.Column<long>(type: "bigint", nullable: false),
                    UomCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    UomName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime", nullable: false, defaultValueSql: "(getdate())"),
                    ModifiedBy = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ModifiedDate = table.Column<DateTime>(type: "datetime", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__setUOM__F6F8D59E4737F405", x => x.UomID);
                    table.ForeignKey(
                        name: "FK_RFQ_BusinessUnit",
                        column: x => x.BusinessUnitID,
                        principalTable: "BusinessUnits",
                        principalColumn: "ID");
                });

            migrationBuilder.CreateTable(
                name: "Setup_Master",
                columns: table => new
                {
                    SetupID = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SetupType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SetupCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    SetupValue = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ParentSetupID = table.Column<long>(type: "bigint", nullable: true),
                    BusinessUnitID = table.Column<long>(type: "bigint", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: true, defaultValue: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ModifiedOn = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__Setup_Ma__C9C734B31BDDC1E2", x => x.SetupID);
                    table.ForeignKey(
                        name: "FK__Setup_Mas__Busin__68487DD7",
                        column: x => x.BusinessUnitID,
                        principalTable: "BusinessUnits",
                        principalColumn: "ID");
                    table.ForeignKey(
                        name: "FK__Setup_Mas__Paren__6754599E",
                        column: x => x.ParentSetupID,
                        principalTable: "Setup_Master",
                        principalColumn: "SetupID");
                });

            migrationBuilder.CreateTable(
                name: "UserGroups",
                columns: table => new
                {
                    ID = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserGroupsName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    BusinessUnitID = table.Column<long>(type: "bigint", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    ModifiedOn = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__UserGrou__3214EC277F8DF4F8", x => x.ID);
                    table.ForeignKey(
                        name: "FK__UserGroup__Busin__73BA3083",
                        column: x => x.BusinessUnitID,
                        principalTable: "BusinessUnits",
                        principalColumn: "ID");
                });

            migrationBuilder.CreateTable(
                name: "Warehouses",
                columns: table => new
                {
                    ID = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    WarehouseCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    WarehouseName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Location = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    AddressLine1 = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    AddressLine2 = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    City = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    State = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Country = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    PostalCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    Capacity = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    ManagerName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    ContactPhone = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ContactEmail = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    BusinessUnitID = table.Column<long>(type: "bigint", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: true, defaultValue: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "(getdate())"),
                    ModifiedBy = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    ModifiedOn = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__Warehous__3214EC27E9A0A7EE", x => x.ID);
                    table.ForeignKey(
                        name: "FK_Warehouses_BusinessUnits",
                        column: x => x.BusinessUnitID,
                        principalTable: "BusinessUnits",
                        principalColumn: "ID");
                });

            migrationBuilder.CreateTable(
                name: "EmailIngests",
                columns: table => new
                {
                    ID = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MessageID = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    EmailSubject = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    FromEmail = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    ToEmail = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    RawEmailPath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ParsedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    EmailConfigurationID = table.Column<long>(type: "bigint", nullable: false),
                    ParseStatus = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    CreatedOn = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "(sysdatetime())")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__EmailIng__3214EC2728D6F6B3", x => x.ID);
                    table.ForeignKey(
                        name: "FK__EmailInge__Email__503BEA1C",
                        column: x => x.EmailConfigurationID,
                        principalTable: "Email_Configurations",
                        principalColumn: "ID");
                });

            migrationBuilder.CreateTable(
                name: "SetState",
                columns: table => new
                {
                    StateID = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    StateCode = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    StateName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CountryID = table.Column<int>(type: "int", nullable: false),
                    BUID = table.Column<long>(type: "bigint", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime", nullable: false, defaultValueSql: "(getdate())"),
                    ModifiedBy = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ModifiedDate = table.Column<DateTime>(type: "datetime", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__SetState__C3BA3B5A26295488", x => x.StateID);
                    table.ForeignKey(
                        name: "FK_State_BusinessUnit",
                        column: x => x.BUID,
                        principalTable: "BusinessUnits",
                        principalColumn: "ID");
                    table.ForeignKey(
                        name: "FK_State_Country",
                        column: x => x.CountryID,
                        principalTable: "SetCountry",
                        principalColumn: "CountryID");
                });

            migrationBuilder.CreateTable(
                name: "RolePermissions",
                columns: table => new
                {
                    ID = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RoleID = table.Column<long>(type: "bigint", nullable: true),
                    ModuleID = table.Column<long>(type: "bigint", nullable: false),
                    BusinessUnitID = table.Column<long>(type: "bigint", nullable: false),
                    CanCreate = table.Column<bool>(type: "bit", nullable: true, defaultValue: false),
                    CanEdit = table.Column<bool>(type: "bit", nullable: true, defaultValue: false),
                    CanDelete = table.Column<bool>(type: "bit", nullable: true, defaultValue: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    ModifiedOn = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__RolePerm__3214EC27212832A0", x => x.ID);
                    table.ForeignKey(
                        name: "FK__RolePermi__Busin__05D8E0BE",
                        column: x => x.BusinessUnitID,
                        principalTable: "BusinessUnits",
                        principalColumn: "ID");
                    table.ForeignKey(
                        name: "FK__RolePermi__Modul__04E4BC85",
                        column: x => x.ModuleID,
                        principalTable: "Module",
                        principalColumn: "ID");
                    table.ForeignKey(
                        name: "FK__RolePermi__RoleI__03F0984C",
                        column: x => x.RoleID,
                        principalTable: "Setup_Master",
                        principalColumn: "SetupID");
                });

            migrationBuilder.CreateTable(
                name: "SetCity",
                columns: table => new
                {
                    CityID = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CityName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    StateID = table.Column<int>(type: "int", nullable: false),
                    CountryID = table.Column<int>(type: "int", nullable: false),
                    BUID = table.Column<long>(type: "bigint", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime", nullable: false, defaultValueSql: "(getdate())"),
                    ModifiedBy = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ModifiedDate = table.Column<DateTime>(type: "datetime", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__SetCity__F2D21A961487DC00", x => x.CityID);
                    table.ForeignKey(
                        name: "FK_City_BusinessUnit",
                        column: x => x.BUID,
                        principalTable: "BusinessUnits",
                        principalColumn: "ID");
                    table.ForeignKey(
                        name: "FK_City_Country",
                        column: x => x.CountryID,
                        principalTable: "SetCountry",
                        principalColumn: "CountryID");
                    table.ForeignKey(
                        name: "FK_City_State",
                        column: x => x.StateID,
                        principalTable: "SetState",
                        principalColumn: "StateID",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Suppliers",
                columns: table => new
                {
                    ID = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DocId = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: true),
                    Name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    ContactEmail = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    ImageURL = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    PaymentTerms = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    AddressLine1 = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    AddressLine2 = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    PostalCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    SuccessRate = table.Column<decimal>(type: "decimal(5,2)", nullable: true),
                    AvgResponseTime = table.Column<int>(type: "int", nullable: true),
                    Tags = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Comments = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CurrencyID = table.Column<long>(type: "bigint", nullable: true),
                    BUID = table.Column<long>(type: "bigint", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: true, defaultValue: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    ModifiedOn = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CityID = table.Column<int>(type: "int", nullable: true),
                    CountryID = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__Supplier__3214EC2782495266", x => x.ID);
                    table.ForeignKey(
                        name: "FK_Suppliers_City",
                        column: x => x.CityID,
                        principalTable: "SetCity",
                        principalColumn: "CityID");
                    table.ForeignKey(
                        name: "FK_Suppliers_Country",
                        column: x => x.CountryID,
                        principalTable: "SetCountry",
                        principalColumn: "CountryID");
                    table.ForeignKey(
                        name: "FK__Suppliers__BUID__1332DBDC",
                        column: x => x.BUID,
                        principalTable: "BusinessUnits",
                        principalColumn: "ID");
                    table.ForeignKey(
                        name: "FK__Suppliers__Curre__123EB7A3",
                        column: x => x.CurrencyID,
                        principalTable: "Currency",
                        principalColumn: "ID");
                });

            migrationBuilder.CreateTable(
                name: "Contacts",
                columns: table => new
                {
                    ID = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CustomerID = table.Column<long>(type: "bigint", nullable: true),
                    SupplierID = table.Column<long>(type: "bigint", nullable: true),
                    FirstName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    MiddleName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    LastName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    PhoneNo = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    MobileNo = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Position = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    IsPrimary = table.Column<bool>(type: "bit", nullable: true, defaultValue: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: true, defaultValue: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    ModifiedOn = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__Contacts__3214EC274B89BAF3", x => x.ID);
                    table.ForeignKey(
                        name: "FK__Contacts__Custom__17F790F9",
                        column: x => x.CustomerID,
                        principalTable: "Customers",
                        principalColumn: "ID");
                    table.ForeignKey(
                        name: "FK__Contacts__Suppli__18EBB532",
                        column: x => x.SupplierID,
                        principalTable: "Suppliers",
                        principalColumn: "ID");
                });

            migrationBuilder.CreateTable(
                name: "Products",
                columns: table => new
                {
                    ID = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DocId = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: true),
                    ProductName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    PartNo = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ModelNo = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CategoryID = table.Column<long>(type: "bigint", nullable: true),
                    QtyOnHand = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    ReorderPoint = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    UomID = table.Column<int>(type: "int", nullable: true),
                    UnitCost = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    SellingPrice = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    WarehouseID = table.Column<long>(type: "bigint", nullable: true),
                    PreferredSupplierID = table.Column<long>(type: "bigint", nullable: true),
                    BatchTracking = table.Column<bool>(type: "bit", nullable: true, defaultValue: false),
                    SerialTracking = table.Column<bool>(type: "bit", nullable: true, defaultValue: false),
                    ExpirationDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Height = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    Width = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    Depth = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    Weight = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    Dimensions = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Barcode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    QRCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    LeadTime = table.Column<int>(type: "int", nullable: true),
                    HSCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    CountryOfOrigin = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    ModifiedOn = table.Column<DateTime>(type: "datetime2", nullable: true),
                    BUID = table.Column<long>(type: "bigint", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: true, defaultValue: true),
                    IsCatalogItem = table.Column<bool>(type: "bit", nullable: true, defaultValue: true),
                    SubCategoryID = table.Column<int>(type: "int", nullable: true),
                    FinalLandedCost = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    FinalSalesPrice = table.Column<decimal>(type: "decimal(18,2)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__Inventor__3214EC27426EF885", x => x.ID);
                    table.ForeignKey(
                        name: "FK_Products_ProductSubCategories",
                        column: x => x.SubCategoryID,
                        principalTable: "ProductSubCategories",
                        principalColumn: "ID");
                    table.ForeignKey(
                        name: "FK_Products_setUOM",
                        column: x => x.UomID,
                        principalTable: "setUOM",
                        principalColumn: "UomID");
                    table.ForeignKey(
                        name: "FK__Products__BUID",
                        column: x => x.BUID,
                        principalTable: "BusinessUnits",
                        principalColumn: "ID");
                    table.ForeignKey(
                        name: "FK__Products__Categ",
                        column: x => x.CategoryID,
                        principalTable: "ProductCategories",
                        principalColumn: "ID");
                    table.ForeignKey(
                        name: "FK__Products__Prefe",
                        column: x => x.PreferredSupplierID,
                        principalTable: "Suppliers",
                        principalColumn: "ID");
                    table.ForeignKey(
                        name: "FK__Products__Wareh",
                        column: x => x.WarehouseID,
                        principalTable: "Warehouses",
                        principalColumn: "ID");
                });

            migrationBuilder.CreateTable(
                name: "SupplierQuotedItems",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SupplierId = table.Column<long>(type: "bigint", nullable: false),
                    ItemName = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UomId = table.Column<int>(type: "int", nullable: true),
                    Quantity = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    UnitPrice = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    CurrencyId = table.Column<long>(type: "bigint", nullable: true),
                    QuoteReference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    QuoteDate = table.Column<DateTime>(type: "datetime", nullable: true),
                    ValidUntil = table.Column<DateTime>(type: "datetime", nullable: true),
                    TaxAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    DiscountAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime", nullable: false, defaultValueSql: "(getdate())"),
                    ModifiedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ModifiedDate = table.Column<DateTime>(type: "datetime", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierQuotedItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SupplierQuotedItems_BusinessUnits",
                        column: x => x.BusinessUnitId,
                        principalTable: "BusinessUnits",
                        principalColumn: "ID");
                    table.ForeignKey(
                        name: "FK_SupplierQuotedItems_Currency",
                        column: x => x.CurrencyId,
                        principalTable: "Currency",
                        principalColumn: "ID");
                    table.ForeignKey(
                        name: "FK_SupplierQuotedItems_SetUoms",
                        column: x => x.UomId,
                        principalTable: "setUOM",
                        principalColumn: "UomID");
                    table.ForeignKey(
                        name: "FK_SupplierQuotedItems_Suppliers",
                        column: x => x.SupplierId,
                        principalTable: "Suppliers",
                        principalColumn: "ID");
                });

            migrationBuilder.CreateTable(
                name: "SupplierPurchaseHistory",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProductId = table.Column<long>(type: "bigint", nullable: false),
                    SupplierId = table.Column<long>(type: "bigint", nullable: false),
                    PurchaseDate = table.Column<DateTime>(type: "datetime", nullable: false, defaultValueSql: "(getdate())"),
                    Quantity = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    UnitPrice = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    BatchNo = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ExpiryDate = table.Column<DateOnly>(type: "date", nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "datetime", nullable: false, defaultValueSql: "(getdate())"),
                    PoDocId = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierPurchaseHistory", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SupplierPurchaseHistory_Products",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "ID");
                    table.ForeignKey(
                        name: "FK_SupplierPurchaseHistory_Suppliers",
                        column: x => x.SupplierId,
                        principalTable: "Suppliers",
                        principalColumn: "ID");
                });

            migrationBuilder.CreateTable(
                name: "LeadItems",
                columns: table => new
                {
                    ID = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LeadID = table.Column<long>(type: "bigint", nullable: false),
                    CompanyRef = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CustomerAccountPortalID = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CustomerRFQNo = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ItemMaterialCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CommodityProduct = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    BuyerName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    LineItemNo = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ProductShortName = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Alternative = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ProductShortDescription = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Currency = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    UnitOfMeasure = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    UnitPrice = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    StorageLocation = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ManufacturerName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ManufacturerPartNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    AlternateProductName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    AlternatePartNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ItemText = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    MaterialPOText = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    LeadTime = table.Column<int>(type: "int", nullable: true),
                    ReceivedDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    BidClosingDateLine = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AIConfidence = table.Column<decimal>(type: "decimal(5,4)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__LeadItem__3214EC2776894FBF", x => x.ID);
                });

            migrationBuilder.CreateTable(
                name: "Leads",
                columns: table => new
                {
                    ID = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RFQNo = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    BuyersName = table.Column<string>(type: "nvarchar(510)", maxLength: 510, nullable: true),
                    RecDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    BidClosingDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    BiddingDecision = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    AcknowledgmentDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SubDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    HeaderRemarks = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OpportunityNo = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    NoOfLineItems = table.Column<int>(type: "int", nullable: true),
                    RFQType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    DurationAgreement = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    LeadSource = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    AIConfidence = table.Column<decimal>(type: "decimal(5,4)", nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "(sysdatetime())"),
                    BusinessUnitID = table.Column<long>(type: "bigint", nullable: false),
                    EmailIngestsID = table.Column<long>(type: "bigint", nullable: false),
                    ModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    EmailSource = table.Column<string>(type: "varchar(255)", unicode: false, maxLength: 255, nullable: true),
                    Clientemail = table.Column<string>(type: "varchar(255)", unicode: false, maxLength: 255, nullable: true),
                    LeadStatusId = table.Column<long>(type: "bigint", nullable: true),
                    AssignTo = table.Column<long>(type: "bigint", nullable: true),
                    AssignOn = table.Column<DateTime>(type: "datetime", nullable: true),
                    AssignComment = table.Column<string>(type: "varchar(500)", unicode: false, maxLength: 500, nullable: true),
                    LeadRejectedReasonID = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__Leads__3214EC2705035004", x => x.ID);
                    table.ForeignKey(
                        name: "FK_Leads_LeadRejectedReason",
                        column: x => x.LeadRejectedReasonID,
                        principalTable: "Setup_Master",
                        principalColumn: "SetupID");
                    table.ForeignKey(
                        name: "FK_Leads_Setup_Master",
                        column: x => x.LeadStatusId,
                        principalTable: "Setup_Master",
                        principalColumn: "SetupID");
                    table.ForeignKey(
                        name: "FK__Leads__BusinessU__55009F39",
                        column: x => x.BusinessUnitID,
                        principalTable: "BusinessUnits",
                        principalColumn: "ID");
                    table.ForeignKey(
                        name: "FK__Leads__EmailInge__55F4C372",
                        column: x => x.EmailIngestsID,
                        principalTable: "EmailIngests",
                        principalColumn: "ID");
                });

            migrationBuilder.CreateTable(
                name: "RFQ",
                columns: table => new
                {
                    ID = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RFQNo = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    BuyersName = table.Column<string>(type: "nvarchar(1020)", maxLength: 1020, nullable: true),
                    RecDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    BidClosingDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    BiddingDecision = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    AcknowledgmentDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SubDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    HeaderRemarks = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OpportunityNo = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    NoOfLineItems = table.Column<int>(type: "int", nullable: true),
                    RFQType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    RFQTypeID = table.Column<long>(type: "bigint", nullable: true),
                    DurationAgreement = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    LeadID = table.Column<long>(type: "bigint", nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "(getdate())"),
                    ModifiedBy = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    ModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    BusinessUnitID = table.Column<long>(type: "bigint", nullable: false),
                    RFQStatusID = table.Column<long>(type: "bigint", nullable: true),
                    CustomerID = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__RFQ__3214EC27E71B0249", x => x.ID);
                    table.ForeignKey(
                        name: "FK_RFQ_BusinessUnitID",
                        column: x => x.BusinessUnitID,
                        principalTable: "BusinessUnits",
                        principalColumn: "ID");
                    table.ForeignKey(
                        name: "FK_RFQ_Customers_CustomerID",
                        column: x => x.CustomerID,
                        principalTable: "Customers",
                        principalColumn: "ID");
                    table.ForeignKey(
                        name: "FK_RFQ_LeadID",
                        column: x => x.LeadID,
                        principalTable: "Leads",
                        principalColumn: "ID");
                    table.ForeignKey(
                        name: "FK_RFQ_StatusID",
                        column: x => x.RFQStatusID,
                        principalTable: "Setup_Master",
                        principalColumn: "SetupID");
                    table.ForeignKey(
                        name: "FK_RFQ_TypeID",
                        column: x => x.RFQTypeID,
                        principalTable: "Setup_Master",
                        principalColumn: "SetupID");
                });

            migrationBuilder.CreateTable(
                name: "Quotes",
                columns: table => new
                {
                    ID = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    QuoteNo = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    RFQID = table.Column<long>(type: "bigint", nullable: true),
                    CustomerID = table.Column<long>(type: "bigint", nullable: true),
                    BusinessUnitID = table.Column<long>(type: "bigint", nullable: false),
                    QuoteDate = table.Column<DateTime>(type: "datetime2", nullable: true, defaultValueSql: "(sysdatetime())"),
                    ValidUntil = table.Column<DateTime>(type: "datetime2", nullable: true),
                    StatusID = table.Column<long>(type: "bigint", nullable: true),
                    CurrencyID = table.Column<long>(type: "bigint", nullable: true),
                    TotalAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    HeaderRemarks = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: true, defaultValueSql: "(sysdatetime())"),
                    ModifiedBy = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    ModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DiscountTypeId = table.Column<long>(type: "bigint", nullable: true),
                    DiscountValue = table.Column<decimal>(type: "decimal(18,2)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__Quotes__3214EC27B0FC1337", x => x.ID);
                    table.ForeignKey(
                        name: "FK_Quote_DiscountType",
                        column: x => x.DiscountTypeId,
                        principalTable: "Setup_Master",
                        principalColumn: "SetupID");
                    table.ForeignKey(
                        name: "FK_Quotes_BusinessUnits",
                        column: x => x.BusinessUnitID,
                        principalTable: "BusinessUnits",
                        principalColumn: "ID");
                    table.ForeignKey(
                        name: "FK_Quotes_Currency",
                        column: x => x.CurrencyID,
                        principalTable: "Currency",
                        principalColumn: "ID");
                    table.ForeignKey(
                        name: "FK_Quotes_Customers",
                        column: x => x.CustomerID,
                        principalTable: "Customers",
                        principalColumn: "ID");
                    table.ForeignKey(
                        name: "FK_Quotes_RFQ",
                        column: x => x.RFQID,
                        principalTable: "RFQ",
                        principalColumn: "ID");
                    table.ForeignKey(
                        name: "FK_Quotes_Status",
                        column: x => x.StatusID,
                        principalTable: "Setup_Master",
                        principalColumn: "SetupID");
                });

            migrationBuilder.CreateTable(
                name: "RFQItems",
                columns: table => new
                {
                    ID = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RFQID = table.Column<long>(type: "bigint", nullable: false),
                    CompanyRef = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CustomerAccountPortalID = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CustomerRFQNo = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ItemMaterialCode = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    LineItemNo = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ProductID = table.Column<long>(type: "bigint", nullable: true),
                    CommodityProduct = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    ProductShortName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ProductShortDescription = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Alternative = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    BuyerName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Currency = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    CurrencyID = table.Column<long>(type: "bigint", nullable: true),
                    UnitOfMeasure = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    UomId = table.Column<int>(type: "int", nullable: true),
                    UnitPrice = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    StorageLocation = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    WarehouseID = table.Column<long>(type: "bigint", nullable: true),
                    ManufacturerName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    ManufacturerPartNumber = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SupplierID = table.Column<long>(type: "bigint", nullable: true),
                    AlternateProductName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    AlternatePartNumber = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ItemText = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    MaterialPOText = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    LeadTime = table.Column<int>(type: "int", nullable: true),
                    RequiredDesiredDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReceivedDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    BidClosingDateLine = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "(getdate())"),
                    ModifiedBy = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    ModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AIConfidence = table.Column<decimal>(type: "decimal(5,4)", nullable: true),
                    SupplierQuotedItemId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__RFQItems__3214EC2712F05C03", x => x.ID);
                    table.ForeignKey(
                        name: "FK_RFQItems_Currency",
                        column: x => x.CurrencyID,
                        principalTable: "Currency",
                        principalColumn: "ID");
                    table.ForeignKey(
                        name: "FK_RFQItems_Product",
                        column: x => x.ProductID,
                        principalTable: "Products",
                        principalColumn: "ID");
                    table.ForeignKey(
                        name: "FK_RFQItems_RFQ",
                        column: x => x.RFQID,
                        principalTable: "RFQ",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RFQItems_Supplier",
                        column: x => x.SupplierID,
                        principalTable: "Suppliers",
                        principalColumn: "ID");
                    table.ForeignKey(
                        name: "FK_RFQItems_UOM",
                        column: x => x.UomId,
                        principalTable: "setUOM",
                        principalColumn: "UomID");
                    table.ForeignKey(
                        name: "FK_RFQItems_Warehouse",
                        column: x => x.WarehouseID,
                        principalTable: "Warehouses",
                        principalColumn: "ID");
                    table.ForeignKey(
                        name: "FK_Rfqitems_SupplierQuotedItems",
                        column: x => x.SupplierQuotedItemId,
                        principalTable: "SupplierQuotedItems",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Orders",
                columns: table => new
                {
                    ID = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrderNo = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: false),
                    QuoteID = table.Column<long>(type: "bigint", nullable: true),
                    LeadID = table.Column<long>(type: "bigint", nullable: true),
                    RFQID = table.Column<long>(type: "bigint", nullable: true),
                    CustomerID = table.Column<long>(type: "bigint", nullable: false),
                    BusinessUnitID = table.Column<long>(type: "bigint", nullable: false),
                    StatusID = table.Column<long>(type: "bigint", nullable: false),
                    CurrencyID = table.Column<long>(type: "bigint", nullable: true),
                    PaymentMethodID = table.Column<long>(type: "bigint", nullable: true),
                    PaymentStatusID = table.Column<long>(type: "bigint", nullable: true),
                    PaymentDate = table.Column<DateTime>(type: "datetime", nullable: true),
                    PaidAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    BalanceAmount = table.Column<decimal>(type: "decimal(19,2)", nullable: true, computedColumnSql: "([TotalAmount]-[PaidAmount])", stored: false),
                    PaymentReference = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: true),
                    OrderDate = table.Column<DateTime>(type: "datetime", nullable: false, defaultValueSql: "(getdate())"),
                    DeliveryDate = table.Column<DateTime>(type: "datetime", nullable: true),
                    TotalAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    SubTotal = table.Column<decimal>(type: "decimal(18,2)", nullable: true, defaultValue: 0m),
                    TaxAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: true, defaultValue: 0m),
                    DiscountAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: true, defaultValue: 0m),
                    TermsAndConditions = table.Column<string>(type: "varchar(max)", unicode: false, nullable: true),
                    Notes = table.Column<string>(type: "varchar(max)", unicode: false, nullable: true),
                    CreatedBy = table.Column<string>(type: "varchar(255)", unicode: false, maxLength: 255, nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "datetime", nullable: false, defaultValueSql: "(getdate())"),
                    ModifiedBy = table.Column<string>(type: "varchar(255)", unicode: false, maxLength: 255, nullable: true),
                    ModifiedOn = table.Column<DateTime>(type: "datetime", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__Orders__3214EC27F30500C1", x => x.ID);
                    table.ForeignKey(
                        name: "FK__Orders__Business__3F9B6DFF",
                        column: x => x.BusinessUnitID,
                        principalTable: "BusinessUnits",
                        principalColumn: "ID");
                    table.ForeignKey(
                        name: "FK__Orders__Currency__436BFEE3",
                        column: x => x.CurrencyID,
                        principalTable: "Currency",
                        principalColumn: "ID");
                    table.ForeignKey(
                        name: "FK__Orders__Customer__3EA749C6",
                        column: x => x.CustomerID,
                        principalTable: "Customers",
                        principalColumn: "ID");
                    table.ForeignKey(
                        name: "FK__Orders__LeadID__3CBF0154",
                        column: x => x.LeadID,
                        principalTable: "Leads",
                        principalColumn: "ID");
                    table.ForeignKey(
                        name: "FK__Orders__PaymentM__4183B671",
                        column: x => x.PaymentMethodID,
                        principalTable: "Setup_Master",
                        principalColumn: "SetupID");
                    table.ForeignKey(
                        name: "FK__Orders__PaymentS__4277DAAA",
                        column: x => x.PaymentStatusID,
                        principalTable: "Setup_Master",
                        principalColumn: "SetupID");
                    table.ForeignKey(
                        name: "FK__Orders__QuoteID__3BCADD1B",
                        column: x => x.QuoteID,
                        principalTable: "Quotes",
                        principalColumn: "ID");
                    table.ForeignKey(
                        name: "FK__Orders__RFQID__3DB3258D",
                        column: x => x.RFQID,
                        principalTable: "RFQ",
                        principalColumn: "ID");
                    table.ForeignKey(
                        name: "FK__Orders__StatusID__408F9238",
                        column: x => x.StatusID,
                        principalTable: "Setup_Master",
                        principalColumn: "SetupID");
                });

            migrationBuilder.CreateTable(
                name: "QuoteItems",
                columns: table => new
                {
                    ID = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    QuoteID = table.Column<long>(type: "bigint", nullable: false),
                    RFQItemID = table.Column<long>(type: "bigint", nullable: true),
                    ProductID = table.Column<long>(type: "bigint", nullable: true),
                    ItemDescription = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Quantity = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    UnitPrice = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    TotalAmount = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    Discount = table.Column<decimal>(type: "decimal(18,6)", nullable: true, defaultValue: 0m),
                    TaxAmount = table.Column<decimal>(type: "decimal(18,6)", nullable: true, defaultValue: 0m),
                    DeliveryLeadTime = table.Column<int>(type: "int", nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: true, defaultValueSql: "(sysdatetime())"),
                    ModifiedBy = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    ModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DiscountTypeId = table.Column<long>(type: "bigint", nullable: true),
                    DiscountValue = table.Column<decimal>(type: "decimal(18,2)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__QuoteIte__3214EC27B021232E", x => x.ID);
                    table.ForeignKey(
                        name: "FK_QuoteItem_DiscountType",
                        column: x => x.DiscountTypeId,
                        principalTable: "Setup_Master",
                        principalColumn: "SetupID");
                    table.ForeignKey(
                        name: "FK_QuoteItems_Products",
                        column: x => x.ProductID,
                        principalTable: "Products",
                        principalColumn: "ID");
                    table.ForeignKey(
                        name: "FK_QuoteItems_Quotes",
                        column: x => x.QuoteID,
                        principalTable: "Quotes",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_QuoteItems_RFQItems",
                        column: x => x.RFQItemID,
                        principalTable: "RFQItems",
                        principalColumn: "ID");
                });

            migrationBuilder.CreateTable(
                name: "OrderItems",
                columns: table => new
                {
                    ID = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrderID = table.Column<long>(type: "bigint", nullable: false),
                    ProductID = table.Column<long>(type: "bigint", nullable: false),
                    Description = table.Column<string>(type: "varchar(500)", unicode: false, maxLength: 500, nullable: true),
                    Quantity = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    UnitPrice = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    Discount = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    TaxAmount = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    TotalAmount = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    UomID = table.Column<int>(type: "int", nullable: true),
                    WarehouseID = table.Column<long>(type: "bigint", nullable: true),
                    CreatedBy = table.Column<string>(type: "varchar(255)", unicode: false, maxLength: 255, nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime", nullable: false, defaultValueSql: "(getdate())"),
                    ModifiedBy = table.Column<string>(type: "varchar(255)", unicode: false, maxLength: 255, nullable: true),
                    ModifiedDate = table.Column<DateTime>(type: "datetime", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__OrderIte__3214EC27F54B0F5F", x => x.ID);
                    table.ForeignKey(
                        name: "FK__OrderItem__Order__4A18FC72",
                        column: x => x.OrderID,
                        principalTable: "Orders",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK__OrderItem__Produ__4B0D20AB",
                        column: x => x.ProductID,
                        principalTable: "Products",
                        principalColumn: "ID");
                    table.ForeignKey(
                        name: "FK__OrderItem__UomID__4C0144E4",
                        column: x => x.UomID,
                        principalTable: "setUOM",
                        principalColumn: "UomID");
                    table.ForeignKey(
                        name: "FK__OrderItem__Wareh__4CF5691D",
                        column: x => x.WarehouseID,
                        principalTable: "Warehouses",
                        principalColumn: "ID");
                });

            migrationBuilder.CreateTable(
                name: "Shipments",
                columns: table => new
                {
                    ID = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ShipmentNo = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    OrderID = table.Column<long>(type: "bigint", nullable: false),
                    BusinessUnitID = table.Column<long>(type: "bigint", nullable: false),
                    StatusID = table.Column<long>(type: "bigint", nullable: false),
                    ShipmentDate = table.Column<DateTime>(type: "datetime", nullable: false),
                    EstimatedDeliveryDate = table.Column<DateTime>(type: "datetime", nullable: true),
                    ActualDeliveryDate = table.Column<DateTime>(type: "datetime", nullable: true),
                    Carrier = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ServiceLevel = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    TrackingNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ExternalID = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    ShippingCost = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    LabelUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RawResponse = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ShippingAddress = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "datetime", nullable: false, defaultValueSql: "(getdate())"),
                    ModifiedBy = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    ModifiedOn = table.Column<DateTime>(type: "datetime", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__Shipment__3214EC2732EE97FF", x => x.ID);
                    table.ForeignKey(
                        name: "FK_Shipments_BusinessUnits",
                        column: x => x.BusinessUnitID,
                        principalTable: "BusinessUnits",
                        principalColumn: "ID");
                    table.ForeignKey(
                        name: "FK_Shipments_Orders",
                        column: x => x.OrderID,
                        principalTable: "Orders",
                        principalColumn: "ID");
                    table.ForeignKey(
                        name: "FK_Shipments_Status",
                        column: x => x.StatusID,
                        principalTable: "Setup_Master",
                        principalColumn: "SetupID");
                });

            migrationBuilder.CreateTable(
                name: "ShipmentItems",
                columns: table => new
                {
                    ID = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ShipmentID = table.Column<long>(type: "bigint", nullable: false),
                    OrderItemID = table.Column<long>(type: "bigint", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "datetime", nullable: false, defaultValueSql: "(getdate())"),
                    ModifiedBy = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    ModifiedOn = table.Column<DateTime>(type: "datetime", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__Shipment__3214EC27B4DD8C7A", x => x.ID);
                    table.ForeignKey(
                        name: "FK_ShipmentItems_OrderItems",
                        column: x => x.OrderItemID,
                        principalTable: "OrderItems",
                        principalColumn: "ID");
                    table.ForeignKey(
                        name: "FK_ShipmentItems_Shipments",
                        column: x => x.ShipmentID,
                        principalTable: "Shipments",
                        principalColumn: "ID");
                });

            migrationBuilder.CreateTable(
                name: "ShipmentStatusHistory",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ShipmentId = table.Column<long>(type: "bigint", nullable: false),
                    PreviousStatusId = table.Column<long>(type: "bigint", nullable: true),
                    NewStatusId = table.Column<long>(type: "bigint", nullable: false),
                    ChangedBy = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    ChangedOn = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(600)", maxLength: 600, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__Shipment__3214EC0749B79ADB", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ShipmentStatusHistory_NewStatus",
                        column: x => x.NewStatusId,
                        principalTable: "Setup_Master",
                        principalColumn: "SetupID");
                    table.ForeignKey(
                        name: "FK_ShipmentStatusHistory_PreviousStatus",
                        column: x => x.PreviousStatusId,
                        principalTable: "Setup_Master",
                        principalColumn: "SetupID");
                    table.ForeignKey(
                        name: "FK_ShipmentStatusHistory_Shipments",
                        column: x => x.ShipmentId,
                        principalTable: "Shipments",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProductAttachments",
                columns: table => new
                {
                    AttachmentID = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    InventoryID = table.Column<long>(type: "bigint", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Locations = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    UploadDate = table.Column<DateTime>(type: "datetime2", nullable: true, defaultValueSql: "(getdate())"),
                    UploadedBy = table.Column<long>(type: "bigint", nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    ModifiedOn = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__Inventor__442C64DEB528BA1B", x => x.AttachmentID);
                    table.ForeignKey(
                        name: "FK__Inventory__Inven__42E1EEFE",
                        column: x => x.InventoryID,
                        principalTable: "Products",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Teams",
                columns: table => new
                {
                    ID = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TeamName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    SubTeamID = table.Column<long>(type: "bigint", nullable: true),
                    ManagerID = table.Column<long>(type: "bigint", nullable: true),
                    BusinessUnitID = table.Column<long>(type: "bigint", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    ModifiedOn = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__Teams__3214EC27A735D5D4", x => x.ID);
                    table.ForeignKey(
                        name: "FK__Teams__BusinessU__70DDC3D8",
                        column: x => x.BusinessUnitID,
                        principalTable: "BusinessUnits",
                        principalColumn: "ID");
                    table.ForeignKey(
                        name: "FK__Teams__SubTeamID__6FE99F9F",
                        column: x => x.SubTeamID,
                        principalTable: "Teams",
                        principalColumn: "ID");
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    ID = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FirstName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    MiddleName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    LastName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Password_Hash = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    ImageURL = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RoleID = table.Column<long>(type: "bigint", nullable: true),
                    TeamID = table.Column<long>(type: "bigint", nullable: true),
                    Timezone = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    LastLogin = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Region = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ManagerID = table.Column<long>(type: "bigint", nullable: true),
                    BUID = table.Column<long>(type: "bigint", nullable: true),
                    UserGroupID = table.Column<long>(type: "bigint", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: true, defaultValue: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    ModifiedOn = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__Users__3214EC279AB429D5", x => x.ID);
                    table.ForeignKey(
                        name: "FK_Users_Manager",
                        column: x => x.ManagerID,
                        principalTable: "Users",
                        principalColumn: "ID");
                    table.ForeignKey(
                        name: "FK__Users__BUID__7D439ABD",
                        column: x => x.BUID,
                        principalTable: "BusinessUnits",
                        principalColumn: "ID");
                    table.ForeignKey(
                        name: "FK__Users__RoleID__7B5B524B",
                        column: x => x.RoleID,
                        principalTable: "Setup_Master",
                        principalColumn: "SetupID");
                    table.ForeignKey(
                        name: "FK__Users__TeamID__7C4F7684",
                        column: x => x.TeamID,
                        principalTable: "Teams",
                        principalColumn: "ID");
                    table.ForeignKey(
                        name: "FK__Users__UserGroup__7E37BEF6",
                        column: x => x.UserGroupID,
                        principalTable: "UserGroups",
                        principalColumn: "ID");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Attachments_ParentTypeID",
                table: "Attachments",
                columns: new[] { "ParentType", "ParentID" });

            migrationBuilder.CreateIndex(
                name: "IX_Contacts_CustomerID",
                table: "Contacts",
                column: "CustomerID");

            migrationBuilder.CreateIndex(
                name: "IX_Contacts_Email",
                table: "Contacts",
                column: "Email");

            migrationBuilder.CreateIndex(
                name: "IX_Contacts_SupplierID",
                table: "Contacts",
                column: "SupplierID");

            migrationBuilder.CreateIndex(
                name: "UQ__Contacts__A9D10534C4FF61F8",
                table: "Contacts",
                column: "Email",
                unique: true,
                filter: "[Email] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Currency_BusinessUnitID",
                table: "Currency",
                column: "BusinessUnitID");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_BUID",
                table: "Customers",
                column: "BUID");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_Name",
                table: "Customers",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "UQ__Customer__FFA796CD4707A72F",
                table: "Customers",
                column: "ContactEmail",
                unique: true,
                filter: "[ContactEmail] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Email_Configurations_BusinessUnitID",
                table: "Email_Configurations",
                column: "BusinessUnitID");

            migrationBuilder.CreateIndex(
                name: "IX_EmailIngests_EmailConfigurationID",
                table: "EmailIngests",
                column: "EmailConfigurationID");

            migrationBuilder.CreateIndex(
                name: "UQ__EmailIng__C87C037D5950F99E",
                table: "EmailIngests",
                column: "MessageID",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Images_Resource",
                table: "Images",
                columns: new[] { "ResourceType", "ResourceID" });

            migrationBuilder.CreateIndex(
                name: "IX_LeadItems_BidClosingDateLine",
                table: "LeadItems",
                column: "BidClosingDateLine");

            migrationBuilder.CreateIndex(
                name: "IX_LeadItems_BuyerName",
                table: "LeadItems",
                column: "BuyerName");

            migrationBuilder.CreateIndex(
                name: "IX_LeadItems_CustomerRFQNo",
                table: "LeadItems",
                column: "CustomerRFQNo");

            migrationBuilder.CreateIndex(
                name: "IX_LeadItems_LeadID",
                table: "LeadItems",
                column: "LeadID");

            migrationBuilder.CreateIndex(
                name: "IX_LeadItems_ReceivedDate",
                table: "LeadItems",
                column: "ReceivedDate");

            migrationBuilder.CreateIndex(
                name: "IX_LeadItems_RFQ_Include",
                table: "LeadItems",
                column: "CustomerRFQNo");

            migrationBuilder.CreateIndex(
                name: "IX_Leads_AssignTo",
                table: "Leads",
                column: "AssignTo");

            migrationBuilder.CreateIndex(
                name: "IX_Leads_BusinessUnitID",
                table: "Leads",
                column: "BusinessUnitID");

            migrationBuilder.CreateIndex(
                name: "IX_Leads_EmailIngestsID",
                table: "Leads",
                column: "EmailIngestsID");

            migrationBuilder.CreateIndex(
                name: "IX_Leads_LeadRejectedReasonID",
                table: "Leads",
                column: "LeadRejectedReasonID");

            migrationBuilder.CreateIndex(
                name: "IX_Leads_LeadStatusId",
                table: "Leads",
                column: "LeadStatusId");

            migrationBuilder.CreateIndex(
                name: "IX_Leads_RecDate",
                table: "Leads",
                column: "RecDate");

            migrationBuilder.CreateIndex(
                name: "IX_Leads_RFQNo",
                table: "Leads",
                column: "RFQNo");

            migrationBuilder.CreateIndex(
                name: "IX_Module_ModuleName",
                table: "Module",
                column: "ModuleName");

            migrationBuilder.CreateIndex(
                name: "UQ__Module__EAC9AEC357051E1B",
                table: "Module",
                column: "ModuleName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrderItems_OrderID",
                table: "OrderItems",
                column: "OrderID");

            migrationBuilder.CreateIndex(
                name: "IX_OrderItems_ProductID",
                table: "OrderItems",
                column: "ProductID");

            migrationBuilder.CreateIndex(
                name: "IX_OrderItems_UomID",
                table: "OrderItems",
                column: "UomID");

            migrationBuilder.CreateIndex(
                name: "IX_OrderItems_WarehouseID",
                table: "OrderItems",
                column: "WarehouseID");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_BusinessUnitID",
                table: "Orders",
                column: "BusinessUnitID");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_CurrencyID",
                table: "Orders",
                column: "CurrencyID");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_CustomerID",
                table: "Orders",
                column: "CustomerID");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_LeadID",
                table: "Orders",
                column: "LeadID");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_OrderNo",
                table: "Orders",
                column: "OrderNo");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_PaymentMethodID",
                table: "Orders",
                column: "PaymentMethodID");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_PaymentStatusID",
                table: "Orders",
                column: "PaymentStatusID");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_QuoteID",
                table: "Orders",
                column: "QuoteID");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_RFQID",
                table: "Orders",
                column: "RFQID");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_StatusID",
                table: "Orders",
                column: "StatusID");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryAttachments_InventoryID",
                table: "ProductAttachments",
                column: "InventoryID");

            migrationBuilder.CreateIndex(
                name: "IX_ProductAttachments_UploadedBy",
                table: "ProductAttachments",
                column: "UploadedBy");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryCategories_BusinessUnitID",
                table: "ProductCategories",
                column: "BusinessUnitID");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryCategories_CategoryName",
                table: "ProductCategories",
                column: "CategoryName");

            migrationBuilder.CreateIndex(
                name: "IX_ProductCategories_ParentCategoryID",
                table: "ProductCategories",
                column: "ParentCategoryID");

            migrationBuilder.CreateIndex(
                name: "IX_Inventory_CategoryID",
                table: "Products",
                column: "CategoryID");

            migrationBuilder.CreateIndex(
                name: "IX_Inventory_PartNo",
                table: "Products",
                column: "PartNo");

            migrationBuilder.CreateIndex(
                name: "IX_Inventory_PreferredSupplierID",
                table: "Products",
                column: "PreferredSupplierID");

            migrationBuilder.CreateIndex(
                name: "IX_Inventory_WarehouseID",
                table: "Products",
                column: "WarehouseID");

            migrationBuilder.CreateIndex(
                name: "IX_Products_BUID",
                table: "Products",
                column: "BUID");

            migrationBuilder.CreateIndex(
                name: "IX_Products_SubCategoryID",
                table: "Products",
                column: "SubCategoryID");

            migrationBuilder.CreateIndex(
                name: "IX_Products_UomID",
                table: "Products",
                column: "UomID");

            migrationBuilder.CreateIndex(
                name: "UQ__Inventor__7C3FF6B67DFB4EBD",
                table: "Products",
                column: "PartNo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductSubCategories_BusinessUnitID",
                table: "ProductSubCategories",
                column: "BusinessUnitID");

            migrationBuilder.CreateIndex(
                name: "IX_ProductSubCategories_IsActive",
                table: "ProductSubCategories",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "UQ_QuoteConfiguration_BusinessUnitId",
                table: "QuoteConfiguration",
                column: "BusinessUnitId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QuoteItems_DiscountTypeId",
                table: "QuoteItems",
                column: "DiscountTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_QuoteItems_ProductID",
                table: "QuoteItems",
                column: "ProductID");

            migrationBuilder.CreateIndex(
                name: "IX_QuoteItems_QuoteID",
                table: "QuoteItems",
                column: "QuoteID");

            migrationBuilder.CreateIndex(
                name: "IX_QuoteItems_RFQItemID",
                table: "QuoteItems",
                column: "RFQItemID");

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_BusinessUnitID",
                table: "Quotes",
                column: "BusinessUnitID");

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_CurrencyID",
                table: "Quotes",
                column: "CurrencyID");

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_CustomerID",
                table: "Quotes",
                column: "CustomerID");

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_DiscountTypeId",
                table: "Quotes",
                column: "DiscountTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_Helper",
                table: "Quotes",
                columns: new[] { "RFQID", "CustomerID", "StatusID" });

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_QuoteNo",
                table: "Quotes",
                column: "QuoteNo");

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_StatusID",
                table: "Quotes",
                column: "StatusID");

            migrationBuilder.CreateIndex(
                name: "IX_RFQ_BusinessUnitID",
                table: "RFQ",
                column: "BusinessUnitID");

            migrationBuilder.CreateIndex(
                name: "IX_RFQ_CustomerID",
                table: "RFQ",
                column: "CustomerID");

            migrationBuilder.CreateIndex(
                name: "IX_RFQ_LeadID",
                table: "RFQ",
                column: "LeadID");

            migrationBuilder.CreateIndex(
                name: "IX_RFQ_RFQStatusID",
                table: "RFQ",
                column: "RFQStatusID");

            migrationBuilder.CreateIndex(
                name: "IX_RFQ_RFQTypeID",
                table: "RFQ",
                column: "RFQTypeID");

            migrationBuilder.CreateIndex(
                name: "IX_RFQItems_CurrencyID",
                table: "RFQItems",
                column: "CurrencyID");

            migrationBuilder.CreateIndex(
                name: "IX_RFQItems_ProductID",
                table: "RFQItems",
                column: "ProductID");

            migrationBuilder.CreateIndex(
                name: "IX_RFQItems_RFQID",
                table: "RFQItems",
                column: "RFQID");

            migrationBuilder.CreateIndex(
                name: "IX_RFQItems_SupplierID",
                table: "RFQItems",
                column: "SupplierID");

            migrationBuilder.CreateIndex(
                name: "IX_RFQItems_SupplierQuotedItemId",
                table: "RFQItems",
                column: "SupplierQuotedItemId");

            migrationBuilder.CreateIndex(
                name: "IX_RFQItems_UomId",
                table: "RFQItems",
                column: "UomId");

            migrationBuilder.CreateIndex(
                name: "IX_RFQItems_WarehouseID",
                table: "RFQItems",
                column: "WarehouseID");

            migrationBuilder.CreateIndex(
                name: "IX_RolePermissions_RoleID",
                table: "RolePermissions",
                column: "RoleID");

            migrationBuilder.CreateIndex(
                name: "IX_UserPermissions_BusinessUnitID",
                table: "RolePermissions",
                column: "BusinessUnitID");

            migrationBuilder.CreateIndex(
                name: "IX_UserPermissions_ModuleID",
                table: "RolePermissions",
                column: "ModuleID");

            migrationBuilder.CreateIndex(
                name: "IX_SetCity_BUID",
                table: "SetCity",
                column: "BUID");

            migrationBuilder.CreateIndex(
                name: "IX_SetCity_CountryID",
                table: "SetCity",
                column: "CountryID");

            migrationBuilder.CreateIndex(
                name: "IX_SetCity_StateID",
                table: "SetCity",
                column: "StateID");

            migrationBuilder.CreateIndex(
                name: "IX_SetCountry_BUID",
                table: "SetCountry",
                column: "BUID");

            migrationBuilder.CreateIndex(
                name: "IX_SetState_BUID",
                table: "SetState",
                column: "BUID");

            migrationBuilder.CreateIndex(
                name: "IX_SetState_CountryID",
                table: "SetState",
                column: "CountryID");

            migrationBuilder.CreateIndex(
                name: "IX_setUOM_BusinessUnitID",
                table: "setUOM",
                column: "BusinessUnitID");

            migrationBuilder.CreateIndex(
                name: "IX_Setup_Master_BusinessUnitID",
                table: "Setup_Master",
                column: "BusinessUnitID");

            migrationBuilder.CreateIndex(
                name: "IX_Setup_Master_ParentSetupID",
                table: "Setup_Master",
                column: "ParentSetupID");

            migrationBuilder.CreateIndex(
                name: "IX_ShipmentItems_OrderItemID",
                table: "ShipmentItems",
                column: "OrderItemID");

            migrationBuilder.CreateIndex(
                name: "IX_ShipmentItems_ShipmentID",
                table: "ShipmentItems",
                column: "ShipmentID");

            migrationBuilder.CreateIndex(
                name: "IX_Shipments_BusinessUnitID",
                table: "Shipments",
                column: "BusinessUnitID");

            migrationBuilder.CreateIndex(
                name: "IX_Shipments_OrderID",
                table: "Shipments",
                column: "OrderID");

            migrationBuilder.CreateIndex(
                name: "IX_Shipments_StatusID",
                table: "Shipments",
                column: "StatusID");

            migrationBuilder.CreateIndex(
                name: "IX_ShipmentStatusHistory_NewStatusId",
                table: "ShipmentStatusHistory",
                column: "NewStatusId");

            migrationBuilder.CreateIndex(
                name: "IX_ShipmentStatusHistory_PreviousStatusId",
                table: "ShipmentStatusHistory",
                column: "PreviousStatusId");

            migrationBuilder.CreateIndex(
                name: "IX_ShipmentStatusHistory_ShipmentId",
                table: "ShipmentStatusHistory",
                column: "ShipmentId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierPurchaseHistory_ProductId",
                table: "SupplierPurchaseHistory",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierPurchaseHistory_SupplierId",
                table: "SupplierPurchaseHistory",
                column: "SupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierQuotedItems_BusinessUnitId",
                table: "SupplierQuotedItems",
                column: "BusinessUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierQuotedItems_CurrencyId",
                table: "SupplierQuotedItems",
                column: "CurrencyId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierQuotedItems_SupplierId",
                table: "SupplierQuotedItems",
                column: "SupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierQuotedItems_UomId",
                table: "SupplierQuotedItems",
                column: "UomId");

            migrationBuilder.CreateIndex(
                name: "IX_Suppliers_BUID",
                table: "Suppliers",
                column: "BUID");

            migrationBuilder.CreateIndex(
                name: "IX_Suppliers_CityID",
                table: "Suppliers",
                column: "CityID");

            migrationBuilder.CreateIndex(
                name: "IX_Suppliers_ContactEmail",
                table: "Suppliers",
                column: "ContactEmail");

            migrationBuilder.CreateIndex(
                name: "IX_Suppliers_CountryID",
                table: "Suppliers",
                column: "CountryID");

            migrationBuilder.CreateIndex(
                name: "IX_Suppliers_CurrencyID",
                table: "Suppliers",
                column: "CurrencyID");

            migrationBuilder.CreateIndex(
                name: "IX_Suppliers_Name",
                table: "Suppliers",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "UQ__Supplier__FFA796CDFB352BC7",
                table: "Suppliers",
                column: "ContactEmail",
                unique: true,
                filter: "[ContactEmail] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Teams_BusinessUnitID",
                table: "Teams",
                column: "BusinessUnitID");

            migrationBuilder.CreateIndex(
                name: "IX_Teams_ManagerID",
                table: "Teams",
                column: "ManagerID");

            migrationBuilder.CreateIndex(
                name: "IX_Teams_SubTeamID",
                table: "Teams",
                column: "SubTeamID");

            migrationBuilder.CreateIndex(
                name: "IX_UserGroups_BusinessUnitID",
                table: "UserGroups",
                column: "BusinessUnitID");

            migrationBuilder.CreateIndex(
                name: "IX_Users_BUID",
                table: "Users",
                column: "BUID");

            migrationBuilder.CreateIndex(
                name: "IX_Users_Email",
                table: "Users",
                column: "Email");

            migrationBuilder.CreateIndex(
                name: "IX_Users_IsActive",
                table: "Users",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_Users_ManagerID",
                table: "Users",
                column: "ManagerID");

            migrationBuilder.CreateIndex(
                name: "IX_Users_RoleID",
                table: "Users",
                column: "RoleID");

            migrationBuilder.CreateIndex(
                name: "IX_Users_TeamID",
                table: "Users",
                column: "TeamID");

            migrationBuilder.CreateIndex(
                name: "IX_Users_UserGroupID",
                table: "Users",
                column: "UserGroupID");

            migrationBuilder.CreateIndex(
                name: "UQ__Users__A9D10534A3A2A11E",
                table: "Users",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Warehouses_BusinessUnitID",
                table: "Warehouses",
                column: "BusinessUnitID");

            migrationBuilder.CreateIndex(
                name: "UQ_Warehouses_Code_BU",
                table: "Warehouses",
                columns: new[] { "WarehouseCode", "BusinessUnitID" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_LeadItems_Leads",
                table: "LeadItems",
                column: "LeadID",
                principalTable: "Leads",
                principalColumn: "ID",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Leads_Users",
                table: "Leads",
                column: "AssignTo",
                principalTable: "Users",
                principalColumn: "ID");

            migrationBuilder.AddForeignKey(
                name: "FK__Inventory__Uploa__44CA3770",
                table: "ProductAttachments",
                column: "UploadedBy",
                principalTable: "Users",
                principalColumn: "ID");

            migrationBuilder.AddForeignKey(
                name: "FK_Teams_Users",
                table: "Teams",
                column: "ManagerID",
                principalTable: "Users",
                principalColumn: "ID");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK__Setup_Mas__Busin__68487DD7",
                table: "Setup_Master");

            migrationBuilder.DropForeignKey(
                name: "FK__Teams__BusinessU__70DDC3D8",
                table: "Teams");

            migrationBuilder.DropForeignKey(
                name: "FK__UserGroup__Busin__73BA3083",
                table: "UserGroups");

            migrationBuilder.DropForeignKey(
                name: "FK__Users__BUID__7D439ABD",
                table: "Users");

            migrationBuilder.DropForeignKey(
                name: "FK__Users__RoleID__7B5B524B",
                table: "Users");

            migrationBuilder.DropForeignKey(
                name: "FK_Teams_Users",
                table: "Teams");

            migrationBuilder.DropTable(
                name: "Attachments");

            migrationBuilder.DropTable(
                name: "Contacts");

            migrationBuilder.DropTable(
                name: "Images");

            migrationBuilder.DropTable(
                name: "LeadItems");

            migrationBuilder.DropTable(
                name: "ProductAttachments");

            migrationBuilder.DropTable(
                name: "QuoteConfiguration");

            migrationBuilder.DropTable(
                name: "QuoteItems");

            migrationBuilder.DropTable(
                name: "RolePermissions");

            migrationBuilder.DropTable(
                name: "ShipmentItems");

            migrationBuilder.DropTable(
                name: "ShipmentStatusHistory");

            migrationBuilder.DropTable(
                name: "SupplierPurchaseHistory");

            migrationBuilder.DropTable(
                name: "RFQItems");

            migrationBuilder.DropTable(
                name: "Module");

            migrationBuilder.DropTable(
                name: "OrderItems");

            migrationBuilder.DropTable(
                name: "Shipments");

            migrationBuilder.DropTable(
                name: "SupplierQuotedItems");

            migrationBuilder.DropTable(
                name: "Products");

            migrationBuilder.DropTable(
                name: "Orders");

            migrationBuilder.DropTable(
                name: "ProductSubCategories");

            migrationBuilder.DropTable(
                name: "setUOM");

            migrationBuilder.DropTable(
                name: "ProductCategories");

            migrationBuilder.DropTable(
                name: "Suppliers");

            migrationBuilder.DropTable(
                name: "Warehouses");

            migrationBuilder.DropTable(
                name: "Quotes");

            migrationBuilder.DropTable(
                name: "SetCity");

            migrationBuilder.DropTable(
                name: "Currency");

            migrationBuilder.DropTable(
                name: "RFQ");

            migrationBuilder.DropTable(
                name: "SetState");

            migrationBuilder.DropTable(
                name: "Customers");

            migrationBuilder.DropTable(
                name: "Leads");

            migrationBuilder.DropTable(
                name: "SetCountry");

            migrationBuilder.DropTable(
                name: "EmailIngests");

            migrationBuilder.DropTable(
                name: "Email_Configurations");

            migrationBuilder.DropTable(
                name: "BusinessUnits");

            migrationBuilder.DropTable(
                name: "Setup_Master");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "Teams");

            migrationBuilder.DropTable(
                name: "UserGroups");
        }
    }
}
