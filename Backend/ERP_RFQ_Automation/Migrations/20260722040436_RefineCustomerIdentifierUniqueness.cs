using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class RefineCustomerIdentifierUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_customer_identifiers_BusinessUnitId_IdentifierType_Normaliz~",
                table: "customer_identifiers");

            migrationBuilder.CreateIndex(
                name: "IX_customer_identifiers_BusinessUnitId_IdentifierType_Normaliz~",
                table: "customer_identifiers",
                columns: new[] { "BusinessUnitId", "IdentifierType", "NormalizedValue", "CustomerId" },
                unique: true,
                filter: "\"EffectiveTo\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "UX_customer_identifiers_authoritative",
                table: "customer_identifiers",
                columns: new[] { "BusinessUnitId", "IdentifierType", "NormalizedValue" },
                unique: true,
                filter: "\"EffectiveTo\" IS NULL AND \"IdentifierType\" IN ('ErpAccount', 'TaxRegistration', 'Email', 'Phone')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_customer_identifiers_BusinessUnitId_IdentifierType_Normaliz~",
                table: "customer_identifiers");

            migrationBuilder.DropIndex(
                name: "UX_customer_identifiers_authoritative",
                table: "customer_identifiers");

            migrationBuilder.CreateIndex(
                name: "IX_customer_identifiers_BusinessUnitId_IdentifierType_Normaliz~",
                table: "customer_identifiers",
                columns: new[] { "BusinessUnitId", "IdentifierType", "NormalizedValue" },
                unique: true,
                filter: "\"EffectiveTo\" IS NULL");
        }
    }
}
