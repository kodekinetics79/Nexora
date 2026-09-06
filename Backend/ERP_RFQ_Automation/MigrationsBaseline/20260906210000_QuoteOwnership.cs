using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <summary>
    /// A quote gets a real owner.
    ///
    /// <para><b>Why.</b> Nothing on <c>Quotes</c> said whose quote it was. The only ownership
    /// signal the product had was <c>CreatedBy</c> — a free-text actor string matched against a
    /// user's email or "First Last" — and the scoped pipeline funnel needs to divide headline
    /// money by person. A rename, a shared mailbox, an import run under a service account or two
    /// people with the same display name all move revenue between reps under that rule, silently.
    /// A display field is not a foundation for a figure somebody's quota is read off.</para>
    ///
    /// <para><b>Why the backfill leaves rows NULL.</b> It sets an owner only where the existing
    /// free text resolves to EXACTLY ONE user in the same business unit, by the same email-or-full-
    /// name rule the workload board has always applied (<c>QuoteOwnerAttribution</c> is the C#
    /// half of this statement, and the write paths call it). Everything else — "System", an
    /// importer's address, a person who has left, a string two users answer to — stays NULL,
    /// because a wrong owner is worse than no owner: it puts one rep's revenue under another rep's
    /// heading, which is the failure the column exists to prevent. NULL is the honest answer and
    /// the read paths state how many rows are in it rather than absorbing them.</para>
    ///
    /// <para>Additive: one nullable column, one restricting foreign key, one composite index the
    /// scoped funnel reads (the tenant leads it, because every predicate on the owner arrives with
    /// the business unit already fixed). No existing column is touched and no row is deleted, so a
    /// deployment that never reads the column behaves exactly as it does today.</para>
    /// </summary>
    [DbContext(typeof(ErpRfqAutomationContext))]
    [Migration("20260906210000_QuoteOwnership")]
    public partial class QuoteOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "OwnerUserID",
                table: "Quotes",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_BusinessUnitID_OwnerUserID",
                table: "Quotes",
                columns: new[] { "BusinessUnitID", "OwnerUserID" });

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_OwnerUserID",
                table: "Quotes",
                column: "OwnerUserID");

            migrationBuilder.AddForeignKey(
                name: "FK_Quotes_OwnerUser",
                table: "Quotes",
                column: "OwnerUserID",
                principalTable: "Users",
                principalColumn: "ID");

            if (migrationBuilder.ActiveProvider != "Npgsql.EntityFrameworkCore.PostgreSQL") return;
            migrationBuilder.Sql(BackfillOwnerFromCreatedBySql);
        }

        /// <summary>
        /// The backfill itself, exposed so a test can execute the statement this migration runs
        /// rather than a paraphrase of it. The precedent is
        /// <c>AddSetupMasterRoleRank.LegacyRankBackfillSql</c>: a data statement nobody can re-run
        /// after deployment is only ever certified by running the same text.
        ///
        /// <para>The candidate set is computed once and joined back, so "exactly one user answers
        /// to this string" is decided over the same rows that supply the answer. Email is citext,
        /// hence the cast before lower(); the name form is compared the way the application builds
        /// it, "First Last" with the pieces trimmed. A blank actor and a user with no name at all
        /// both collapse to the empty string, so both sides are required to be non-empty or every
        /// nameless user would claim every nameless quote.</para>
        /// </summary>
        public const string BackfillOwnerFromCreatedBySql = """
WITH candidate AS (
    SELECT q."ID" AS quote_id, u."ID" AS user_id
    FROM public."Quotes" q
    JOIN public."Users" u ON u."BUID" = q."BusinessUnitID"
    WHERE q."OwnerUserID" IS NULL
      AND btrim(coalesce(q."CreatedBy", '')) <> ''
      AND (
            lower(btrim(q."CreatedBy")) = lower(btrim(coalesce(u."Email"::text, '')))
         OR (
                btrim(coalesce(u."FirstName", '') || ' ' || coalesce(u."LastName", '')) <> ''
            AND lower(btrim(q."CreatedBy"))
                = lower(btrim(coalesce(u."FirstName", '') || ' ' || coalesce(u."LastName", '')))
            )
          )
), unambiguous AS (
    SELECT quote_id, min(user_id) AS user_id
    FROM candidate
    GROUP BY quote_id
    HAVING count(DISTINCT user_id) = 1
)
UPDATE public."Quotes" q
SET "OwnerUserID" = unambiguous.user_id
FROM unambiguous
WHERE unambiguous.quote_id = q."ID";
""";

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Quotes_OwnerUser",
                table: "Quotes");

            migrationBuilder.DropIndex(
                name: "IX_Quotes_BusinessUnitID_OwnerUserID",
                table: "Quotes");

            migrationBuilder.DropIndex(
                name: "IX_Quotes_OwnerUserID",
                table: "Quotes");

            migrationBuilder.DropColumn(
                name: "OwnerUserID",
                table: "Quotes");
        }
    }
}
