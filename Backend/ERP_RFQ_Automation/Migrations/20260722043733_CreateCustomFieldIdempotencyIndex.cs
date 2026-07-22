using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class CreateCustomFieldIdempotencyIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX CONCURRENTLY IF NOT EXISTS
                    "IX_custom_field_value_history_BusinessUnitId_IdempotencyKey"
                ON custom_field_value_history ("BusinessUnitId", "IdempotencyKey");
                """, suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX CONCURRENTLY IF EXISTS
                    "IX_custom_field_value_history_BusinessUnitId_IdempotencyKey";
                """, suppressTransaction: true);
        }
    }
}
