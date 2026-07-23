using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class TrackRefundDisbursement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DisbursementFailureReason",
                table: "CustomerRefunds",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DisbursementUpdatedBy",
                table: "CustomerRefunds",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DisbursementUpdatedOn",
                table: "CustomerRefunds",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.Sql("""
                ALTER TABLE public."CustomerRefunds"
                    ADD CONSTRAINT "CK_CustomerRefunds_DestinationToken"
                    CHECK ("DestinationReference" ~ '^token:[A-Za-z0-9_-]{8,180}$');
                ALTER TABLE public."CustomerRefunds"
                    ADD CONSTRAINT "CK_CustomerRefunds_PostingStatus"
                    CHECK ("PostingStatus" IN ('NotReleased', 'Reserved', 'PendingDisbursement',
                        'Settled', 'Failed', 'Cancelled', 'ReversalPendingExport'));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $block$
                BEGIN
                    IF EXISTS (SELECT 1 FROM public."CustomerRefunds"
                        WHERE "DisbursementUpdatedOn" IS NOT NULL OR "PostingStatus" IN ('Settled', 'Failed')) THEN
                        RAISE EXCEPTION 'cannot remove refund disbursement evidence after provider results exist';
                    END IF;
                END
                $block$;
                ALTER TABLE public."CustomerRefunds" DROP CONSTRAINT IF EXISTS "CK_CustomerRefunds_DestinationToken";
                ALTER TABLE public."CustomerRefunds" DROP CONSTRAINT IF EXISTS "CK_CustomerRefunds_PostingStatus";
                """);
            migrationBuilder.DropColumn(
                name: "DisbursementFailureReason",
                table: "CustomerRefunds");

            migrationBuilder.DropColumn(
                name: "DisbursementUpdatedBy",
                table: "CustomerRefunds");

            migrationBuilder.DropColumn(
                name: "DisbursementUpdatedOn",
                table: "CustomerRefunds");
        }
    }
}
