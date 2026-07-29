using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class V2Gate02ValidateOpportunityCommercialComponents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE public.commercial_opportunity_recommendations
                    VALIDATE CONSTRAINT "CK_opportunity_recommendations_ComponentsObject";
                ALTER TABLE public.commercial_opportunity_recommendations
                    VALIDATE CONSTRAINT "CK_opportunity_recommendations_EcvCurrency";
                ALTER TABLE public.commercial_opportunity_recommendations
                    VALIDATE CONSTRAINT "CK_opportunity_recommendations_EcvNonNegative";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
