using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class Module03TenantSafeSalesRouting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $tenant_routing_preflight$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM public.customer_ownerships value
                        LEFT JOIN public."Customers" parent
                          ON parent."BUID" = value."BusinessUnitId" AND parent."ID" = value."CustomerId"
                        WHERE parent."ID" IS NULL
                    ) OR EXISTS (
                        SELECT 1 FROM public.lead_routing_decisions value
                        LEFT JOIN public."Leads" parent
                          ON parent."BusinessUnitID" = value."BusinessUnitId" AND parent."ID" = value."LeadId"
                        WHERE parent."ID" IS NULL
                    ) OR EXISTS (
                        SELECT 1 FROM public.lead_routing_decisions value
                        LEFT JOIN public."Customers" parent
                          ON parent."BUID" = value."BusinessUnitId" AND parent."ID" = value."CustomerId"
                        WHERE value."CustomerId" IS NOT NULL AND parent."ID" IS NULL
                    ) OR EXISTS (
                        SELECT 1 FROM public.lead_routing_decisions value
                        LEFT JOIN public.customer_identifiers parent
                          ON parent."BusinessUnitId" = value."BusinessUnitId" AND parent."Id" = value."MatchedIdentifierId"
                        WHERE value."MatchedIdentifierId" IS NOT NULL AND parent."Id" IS NULL
                    ) OR EXISTS (
                        SELECT 1 FROM public.lead_routing_decisions value
                        LEFT JOIN public.customer_ownerships parent
                          ON parent."BusinessUnitId" = value."BusinessUnitId" AND parent."Id" = value."OwnershipId"
                        WHERE value."OwnershipId" IS NOT NULL AND parent."Id" IS NULL
                    ) OR EXISTS (
                        SELECT 1 FROM public.lead_assignments value
                        LEFT JOIN public."Leads" parent
                          ON parent."BusinessUnitID" = value."BusinessUnitId" AND parent."ID" = value."LeadId"
                        WHERE parent."ID" IS NULL
                    ) OR EXISTS (
                        SELECT 1 FROM public.lead_assignments value
                        LEFT JOIN public.lead_routing_decisions parent
                          ON parent."BusinessUnitId" = value."BusinessUnitId" AND parent."Id" = value."RoutingDecisionId"
                        WHERE parent."Id" IS NULL
                    ) OR EXISTS (
                        SELECT 1 FROM public.lead_assignments value
                        LEFT JOIN public.customer_ownerships parent
                          ON parent."BusinessUnitId" = value."BusinessUnitId" AND parent."Id" = value."OwnershipId"
                        WHERE value."OwnershipId" IS NOT NULL AND parent."Id" IS NULL
                    ) OR EXISTS (
                        SELECT 1 FROM public.unassigned_work_items value
                        LEFT JOIN public."Leads" parent
                          ON parent."BusinessUnitID" = value."BusinessUnitId" AND parent."ID" = value."LeadId"
                        WHERE parent."ID" IS NULL
                    ) OR EXISTS (
                        SELECT 1 FROM public.unassigned_work_items value
                        LEFT JOIN public.lead_routing_decisions parent
                          ON parent."BusinessUnitId" = value."BusinessUnitId" AND parent."Id" = value."RoutingDecisionId"
                        WHERE parent."Id" IS NULL
                    ) THEN
                        RAISE EXCEPTION 'cross-tenant or orphan routing references must be repaired before Module 03 upgrade';
                    END IF;
                END $tenant_routing_preflight$;

                ALTER TABLE public.customer_ownerships
                    DROP CONSTRAINT IF EXISTS "FK_customer_ownerships_Customers_CustomerId",
                    DROP CONSTRAINT IF EXISTS "FK_customer_owner_tenant_customer";
                ALTER TABLE public.lead_assignments
                    DROP CONSTRAINT IF EXISTS "FK_lead_assignments_Leads_LeadId",
                    DROP CONSTRAINT IF EXISTS "FK_lead_assignments_customer_ownerships_OwnershipId",
                    DROP CONSTRAINT IF EXISTS "FK_lead_assignments_lead_routing_decisions_RoutingDecisionId";
                ALTER TABLE public.lead_routing_decisions
                    DROP CONSTRAINT IF EXISTS "FK_lead_routing_decisions_Customers_CustomerId",
                    DROP CONSTRAINT IF EXISTS "FK_lead_routing_decisions_Leads_LeadId",
                    DROP CONSTRAINT IF EXISTS "FK_lead_routing_decisions_customer_identifiers_MatchedIdentifierId",
                    DROP CONSTRAINT IF EXISTS "FK_lead_routing_decisions_customer_identifiers_MatchedIdentifi~",
                    DROP CONSTRAINT IF EXISTS "FK_lead_routing_decisions_customer_ownerships_OwnershipId";
                ALTER TABLE public.unassigned_work_items
                    DROP CONSTRAINT IF EXISTS "FK_unassigned_work_items_Leads_LeadId",
                    DROP CONSTRAINT IF EXISTS "FK_unassigned_work_items_lead_routing_decisions_RoutingDecisionId",
                    DROP CONSTRAINT IF EXISTS "FK_unassigned_work_items_lead_routing_decisions_RoutingDecisio~";
                """);

            migrationBuilder.DropIndex(
                name: "IX_unassigned_work_items_LeadId",
                table: "unassigned_work_items");

            migrationBuilder.DropIndex(
                name: "IX_unassigned_work_items_RoutingDecisionId",
                table: "unassigned_work_items");

            migrationBuilder.DropIndex(
                name: "IX_lead_routing_decisions_CustomerId",
                table: "lead_routing_decisions");

            migrationBuilder.DropIndex(
                name: "IX_lead_routing_decisions_LeadId",
                table: "lead_routing_decisions");

            migrationBuilder.DropIndex(
                name: "IX_lead_routing_decisions_MatchedIdentifierId",
                table: "lead_routing_decisions");

            migrationBuilder.DropIndex(
                name: "IX_lead_routing_decisions_OwnershipId",
                table: "lead_routing_decisions");

            migrationBuilder.DropIndex(
                name: "IX_lead_assignments_LeadId",
                table: "lead_assignments");

            migrationBuilder.DropIndex(
                name: "IX_lead_assignments_OwnershipId",
                table: "lead_assignments");

            migrationBuilder.DropIndex(
                name: "IX_lead_assignments_RoutingDecisionId",
                table: "lead_assignments");

            migrationBuilder.DropIndex(
                name: "IX_customer_ownerships_CustomerId",
                table: "customer_ownerships");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_lead_routing_decisions_BusinessUnitId_Id",
                table: "lead_routing_decisions",
                columns: new[] { "BusinessUnitId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_customer_ownerships_BusinessUnitId_Id",
                table: "customer_ownerships",
                columns: new[] { "BusinessUnitId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_customer_identifiers_BusinessUnitId_Id",
                table: "customer_identifiers",
                columns: new[] { "BusinessUnitId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_unassigned_work_items_BusinessUnitId_RoutingDecisionId",
                table: "unassigned_work_items",
                columns: new[] { "BusinessUnitId", "RoutingDecisionId" });

            migrationBuilder.CreateIndex(
                name: "IX_lead_routing_decisions_BusinessUnitId_CustomerId",
                table: "lead_routing_decisions",
                columns: new[] { "BusinessUnitId", "CustomerId" });

            migrationBuilder.CreateIndex(
                name: "IX_lead_routing_decisions_BusinessUnitId_MatchedIdentifierId",
                table: "lead_routing_decisions",
                columns: new[] { "BusinessUnitId", "MatchedIdentifierId" });

            migrationBuilder.CreateIndex(
                name: "IX_lead_routing_decisions_BusinessUnitId_OwnershipId",
                table: "lead_routing_decisions",
                columns: new[] { "BusinessUnitId", "OwnershipId" });

            migrationBuilder.CreateIndex(
                name: "IX_lead_assignments_BusinessUnitId_OwnershipId",
                table: "lead_assignments",
                columns: new[] { "BusinessUnitId", "OwnershipId" });

            migrationBuilder.CreateIndex(
                name: "IX_lead_assignments_BusinessUnitId_RoutingDecisionId",
                table: "lead_assignments",
                columns: new[] { "BusinessUnitId", "RoutingDecisionId" });

            migrationBuilder.AddForeignKey(
                name: "FK_customer_ownerships_Customers_BusinessUnitId_CustomerId",
                table: "customer_ownerships",
                columns: new[] { "BusinessUnitId", "CustomerId" },
                principalTable: "Customers",
                principalColumns: new[] { "BUID", "ID" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_lead_assignments_Leads_BusinessUnitId_LeadId",
                table: "lead_assignments",
                columns: new[] { "BusinessUnitId", "LeadId" },
                principalTable: "Leads",
                principalColumns: new[] { "BusinessUnitID", "ID" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_lead_assignments_customer_ownerships_BusinessUnitId_Ownersh~",
                table: "lead_assignments",
                columns: new[] { "BusinessUnitId", "OwnershipId" },
                principalTable: "customer_ownerships",
                principalColumns: new[] { "BusinessUnitId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_lead_assignments_lead_routing_decisions_BusinessUnitId_Rout~",
                table: "lead_assignments",
                columns: new[] { "BusinessUnitId", "RoutingDecisionId" },
                principalTable: "lead_routing_decisions",
                principalColumns: new[] { "BusinessUnitId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_lead_routing_decisions_Customers_BusinessUnitId_CustomerId",
                table: "lead_routing_decisions",
                columns: new[] { "BusinessUnitId", "CustomerId" },
                principalTable: "Customers",
                principalColumns: new[] { "BUID", "ID" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_lead_routing_decisions_Leads_BusinessUnitId_LeadId",
                table: "lead_routing_decisions",
                columns: new[] { "BusinessUnitId", "LeadId" },
                principalTable: "Leads",
                principalColumns: new[] { "BusinessUnitID", "ID" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_lead_routing_decisions_customer_identifiers_BusinessUnitId_~",
                table: "lead_routing_decisions",
                columns: new[] { "BusinessUnitId", "MatchedIdentifierId" },
                principalTable: "customer_identifiers",
                principalColumns: new[] { "BusinessUnitId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_lead_routing_decisions_customer_ownerships_BusinessUnitId_O~",
                table: "lead_routing_decisions",
                columns: new[] { "BusinessUnitId", "OwnershipId" },
                principalTable: "customer_ownerships",
                principalColumns: new[] { "BusinessUnitId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_unassigned_work_items_Leads_BusinessUnitId_LeadId",
                table: "unassigned_work_items",
                columns: new[] { "BusinessUnitId", "LeadId" },
                principalTable: "Leads",
                principalColumns: new[] { "BusinessUnitID", "ID" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_unassigned_work_items_lead_routing_decisions_BusinessUnitId~",
                table: "unassigned_work_items",
                columns: new[] { "BusinessUnitId", "RoutingDecisionId" },
                principalTable: "lead_routing_decisions",
                principalColumns: new[] { "BusinessUnitId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("""
                DO $tenant_routing_user_preflight$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM public.lead_routing_decisions value
                        CROSS JOIN LATERAL (VALUES (value."SuggestedUserId"), (value."SelectedUserId")) refs("UserId")
                        LEFT JOIN public."Users" parent
                          ON parent."BUID" = value."BusinessUnitId" AND parent."ID" = refs."UserId"
                        WHERE refs."UserId" IS NOT NULL AND parent."ID" IS NULL
                    ) OR EXISTS (
                        SELECT 1 FROM public.lead_assignments value
                        CROSS JOIN LATERAL (VALUES (value."FromUserId"), (value."ToUserId"), (value."AssignedByUserId")) refs("UserId")
                        LEFT JOIN public."Users" parent
                          ON parent."BUID" = value."BusinessUnitId" AND parent."ID" = refs."UserId"
                        WHERE refs."UserId" IS NOT NULL AND parent."ID" IS NULL
                    ) OR EXISTS (
                        SELECT 1 FROM public.unassigned_work_items value
                        CROSS JOIN LATERAL (VALUES (value."SuggestedUserId"), (value."ClaimedByUserId")) refs("UserId")
                        LEFT JOIN public."Users" parent
                          ON parent."BUID" = value."BusinessUnitId" AND parent."ID" = refs."UserId"
                        WHERE refs."UserId" IS NOT NULL AND parent."ID" IS NULL
                    ) OR EXISTS (
                        SELECT 1 FROM public.unassigned_work_items value
                        LEFT JOIN public."Customers" parent
                          ON parent."BUID" = value."BusinessUnitId" AND parent."ID" = value."SuggestedCustomerId"
                        WHERE value."SuggestedCustomerId" IS NOT NULL AND parent."ID" IS NULL
                    ) THEN
                        RAISE EXCEPTION 'cross-tenant routing user or suggested-customer references must be repaired before Module 03 upgrade';
                    END IF;
                END $tenant_routing_user_preflight$;

                ALTER TABLE public.lead_assignments
                    DROP CONSTRAINT IF EXISTS "FK_lead_assignments_Users_ToUserId";

                ALTER TABLE public.lead_routing_decisions
                    ADD CONSTRAINT "FK_lead_decision_tenant_suggested_user"
                        FOREIGN KEY ("BusinessUnitId", "SuggestedUserId") REFERENCES public."Users" ("BUID", "ID") ON DELETE RESTRICT,
                    ADD CONSTRAINT "FK_lead_decision_tenant_selected_user"
                        FOREIGN KEY ("BusinessUnitId", "SelectedUserId") REFERENCES public."Users" ("BUID", "ID") ON DELETE RESTRICT;
                ALTER TABLE public.lead_assignments
                    ADD CONSTRAINT "FK_lead_assignment_tenant_from_user"
                        FOREIGN KEY ("BusinessUnitId", "FromUserId") REFERENCES public."Users" ("BUID", "ID") ON DELETE RESTRICT,
                    ADD CONSTRAINT "FK_lead_assignment_tenant_to_user"
                        FOREIGN KEY ("BusinessUnitId", "ToUserId") REFERENCES public."Users" ("BUID", "ID") ON DELETE RESTRICT,
                    ADD CONSTRAINT "FK_lead_assignment_tenant_actor"
                        FOREIGN KEY ("BusinessUnitId", "AssignedByUserId") REFERENCES public."Users" ("BUID", "ID") ON DELETE RESTRICT;
                ALTER TABLE public.unassigned_work_items
                    ADD CONSTRAINT "FK_unassigned_tenant_suggested_customer"
                        FOREIGN KEY ("BusinessUnitId", "SuggestedCustomerId") REFERENCES public."Customers" ("BUID", "ID") ON DELETE RESTRICT,
                    ADD CONSTRAINT "FK_unassigned_tenant_suggested_user"
                        FOREIGN KEY ("BusinessUnitId", "SuggestedUserId") REFERENCES public."Users" ("BUID", "ID") ON DELETE RESTRICT,
                    ADD CONSTRAINT "FK_unassigned_tenant_claimed_user"
                        FOREIGN KEY ("BusinessUnitId", "ClaimedByUserId") REFERENCES public."Users" ("BUID", "ID") ON DELETE RESTRICT;

                CREATE OR REPLACE FUNCTION public.nexora_reject_routing_decision_mutation()
                RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                    RAISE EXCEPTION 'routing decision history is append-only';
                END $fn$;
                DROP TRIGGER IF EXISTS lead_routing_decisions_immutable ON public.lead_routing_decisions;
                CREATE TRIGGER lead_routing_decisions_immutable
                    BEFORE UPDATE OR DELETE ON public.lead_routing_decisions
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_routing_decision_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS lead_routing_decisions_immutable ON public.lead_routing_decisions;
                DROP FUNCTION IF EXISTS public.nexora_reject_routing_decision_mutation();
                ALTER TABLE public.lead_routing_decisions
                    DROP CONSTRAINT IF EXISTS "FK_lead_decision_tenant_suggested_user",
                    DROP CONSTRAINT IF EXISTS "FK_lead_decision_tenant_selected_user";
                ALTER TABLE public.lead_assignments
                    DROP CONSTRAINT IF EXISTS "FK_lead_assignment_tenant_from_user",
                    DROP CONSTRAINT IF EXISTS "FK_lead_assignment_tenant_to_user",
                    DROP CONSTRAINT IF EXISTS "FK_lead_assignment_tenant_actor";
                ALTER TABLE public.unassigned_work_items
                    DROP CONSTRAINT IF EXISTS "FK_unassigned_tenant_suggested_customer",
                    DROP CONSTRAINT IF EXISTS "FK_unassigned_tenant_suggested_user",
                    DROP CONSTRAINT IF EXISTS "FK_unassigned_tenant_claimed_user";
                ALTER TABLE public.lead_assignments
                    ADD CONSTRAINT "FK_lead_assignments_Users_ToUserId"
                    FOREIGN KEY ("ToUserId") REFERENCES public."Users" ("ID") ON DELETE RESTRICT;
                """);
            migrationBuilder.DropForeignKey(
                name: "FK_customer_ownerships_Customers_BusinessUnitId_CustomerId",
                table: "customer_ownerships");

            migrationBuilder.DropForeignKey(
                name: "FK_lead_assignments_Leads_BusinessUnitId_LeadId",
                table: "lead_assignments");

            migrationBuilder.DropForeignKey(
                name: "FK_lead_assignments_customer_ownerships_BusinessUnitId_Ownersh~",
                table: "lead_assignments");

            migrationBuilder.DropForeignKey(
                name: "FK_lead_assignments_lead_routing_decisions_BusinessUnitId_Rout~",
                table: "lead_assignments");

            migrationBuilder.DropForeignKey(
                name: "FK_lead_routing_decisions_Customers_BusinessUnitId_CustomerId",
                table: "lead_routing_decisions");

            migrationBuilder.DropForeignKey(
                name: "FK_lead_routing_decisions_Leads_BusinessUnitId_LeadId",
                table: "lead_routing_decisions");

            migrationBuilder.DropForeignKey(
                name: "FK_lead_routing_decisions_customer_identifiers_BusinessUnitId_~",
                table: "lead_routing_decisions");

            migrationBuilder.DropForeignKey(
                name: "FK_lead_routing_decisions_customer_ownerships_BusinessUnitId_O~",
                table: "lead_routing_decisions");

            migrationBuilder.DropForeignKey(
                name: "FK_unassigned_work_items_Leads_BusinessUnitId_LeadId",
                table: "unassigned_work_items");

            migrationBuilder.DropForeignKey(
                name: "FK_unassigned_work_items_lead_routing_decisions_BusinessUnitId~",
                table: "unassigned_work_items");

            migrationBuilder.DropIndex(
                name: "IX_unassigned_work_items_BusinessUnitId_RoutingDecisionId",
                table: "unassigned_work_items");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_lead_routing_decisions_BusinessUnitId_Id",
                table: "lead_routing_decisions");

            migrationBuilder.DropIndex(
                name: "IX_lead_routing_decisions_BusinessUnitId_CustomerId",
                table: "lead_routing_decisions");

            migrationBuilder.DropIndex(
                name: "IX_lead_routing_decisions_BusinessUnitId_MatchedIdentifierId",
                table: "lead_routing_decisions");

            migrationBuilder.DropIndex(
                name: "IX_lead_routing_decisions_BusinessUnitId_OwnershipId",
                table: "lead_routing_decisions");

            migrationBuilder.DropIndex(
                name: "IX_lead_assignments_BusinessUnitId_OwnershipId",
                table: "lead_assignments");

            migrationBuilder.DropIndex(
                name: "IX_lead_assignments_BusinessUnitId_RoutingDecisionId",
                table: "lead_assignments");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_customer_ownerships_BusinessUnitId_Id",
                table: "customer_ownerships");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_customer_identifiers_BusinessUnitId_Id",
                table: "customer_identifiers");

            migrationBuilder.CreateIndex(
                name: "IX_unassigned_work_items_LeadId",
                table: "unassigned_work_items",
                column: "LeadId");

            migrationBuilder.CreateIndex(
                name: "IX_unassigned_work_items_RoutingDecisionId",
                table: "unassigned_work_items",
                column: "RoutingDecisionId");

            migrationBuilder.CreateIndex(
                name: "IX_lead_routing_decisions_CustomerId",
                table: "lead_routing_decisions",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_lead_routing_decisions_LeadId",
                table: "lead_routing_decisions",
                column: "LeadId");

            migrationBuilder.CreateIndex(
                name: "IX_lead_routing_decisions_MatchedIdentifierId",
                table: "lead_routing_decisions",
                column: "MatchedIdentifierId");

            migrationBuilder.CreateIndex(
                name: "IX_lead_routing_decisions_OwnershipId",
                table: "lead_routing_decisions",
                column: "OwnershipId");

            migrationBuilder.CreateIndex(
                name: "IX_lead_assignments_LeadId",
                table: "lead_assignments",
                column: "LeadId");

            migrationBuilder.CreateIndex(
                name: "IX_lead_assignments_OwnershipId",
                table: "lead_assignments",
                column: "OwnershipId");

            migrationBuilder.CreateIndex(
                name: "IX_lead_assignments_RoutingDecisionId",
                table: "lead_assignments",
                column: "RoutingDecisionId");

            migrationBuilder.CreateIndex(
                name: "IX_customer_ownerships_CustomerId",
                table: "customer_ownerships",
                column: "CustomerId");

            migrationBuilder.Sql("""
                ALTER TABLE public.customer_ownerships
                    ADD CONSTRAINT "FK_customer_owner_tenant_customer"
                    FOREIGN KEY ("BusinessUnitId", "CustomerId")
                    REFERENCES public."Customers" ("BUID", "ID") ON DELETE RESTRICT;
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_lead_assignments_Leads_LeadId",
                table: "lead_assignments",
                column: "LeadId",
                principalTable: "Leads",
                principalColumn: "ID",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_lead_assignments_customer_ownerships_OwnershipId",
                table: "lead_assignments",
                column: "OwnershipId",
                principalTable: "customer_ownerships",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_lead_assignments_lead_routing_decisions_RoutingDecisionId",
                table: "lead_assignments",
                column: "RoutingDecisionId",
                principalTable: "lead_routing_decisions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_lead_routing_decisions_Customers_CustomerId",
                table: "lead_routing_decisions",
                column: "CustomerId",
                principalTable: "Customers",
                principalColumn: "ID",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_lead_routing_decisions_Leads_LeadId",
                table: "lead_routing_decisions",
                column: "LeadId",
                principalTable: "Leads",
                principalColumn: "ID",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_lead_routing_decisions_customer_identifiers_MatchedIdentifi~",
                table: "lead_routing_decisions",
                column: "MatchedIdentifierId",
                principalTable: "customer_identifiers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_lead_routing_decisions_customer_ownerships_OwnershipId",
                table: "lead_routing_decisions",
                column: "OwnershipId",
                principalTable: "customer_ownerships",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_unassigned_work_items_Leads_LeadId",
                table: "unassigned_work_items",
                column: "LeadId",
                principalTable: "Leads",
                principalColumn: "ID",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_unassigned_work_items_lead_routing_decisions_RoutingDecisio~",
                table: "unassigned_work_items",
                column: "RoutingDecisionId",
                principalTable: "lead_routing_decisions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
