using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class BackfillCommercialRoutingLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO lead_routing_decisions
                    ("BusinessUnitId", "LeadId", "CustomerId", "MatchedIdentifierId", "OwnershipId",
                     "SuggestedUserId", "SelectedUserId", "MatchStatus", "Outcome", "MatchConfidence",
                     "DecisionCode", "Explanation", "PolicyVersion", "CorrelationId", "IdempotencyKey", "CreatedOn")
                SELECT l."BusinessUnitID", l."ID", NULL, NULL, NULL,
                       l."AssignTo", l."AssignTo", 'NoEvidence', 'AssignedPrimary', 0,
                       'MIGRATED_ASSIGNMENT', '{"source":"migration","reason":"existing Lead.AssignTo"}'::jsonb,
                       '1', 'commercial-routing-backfill',
                       'migration:lead:' || l."ID"::text || ':assignment:v1',
                       COALESCE(l."AssignOn", l."ModifiedDate", l."CreatedDate", now())
                FROM "Leads" l
                JOIN "Users" u ON u."ID" = l."AssignTo" AND u."BUID" = l."BusinessUnitID"
                WHERE l."AssignTo" IS NOT NULL
                  AND NOT EXISTS (
                      SELECT 1 FROM lead_routing_decisions d
                      WHERE d."BusinessUnitId" = l."BusinessUnitID"
                        AND d."IdempotencyKey" = 'migration:lead:' || l."ID"::text || ':assignment:v1');

                INSERT INTO lead_assignments
                    ("BusinessUnitId", "LeadId", "FromUserId", "ToUserId", "AssignmentScope",
                     "OwnershipId", "RoutingDecisionId", "ReasonCode", "Comment", "EffectiveFrom",
                     "EffectiveTo", "AssignedByUserId", "CorrelationId", "IdempotencyKey")
                SELECT l."BusinessUnitID", l."ID", NULL, l."AssignTo", 'LeadOnly',
                       NULL, d."Id", 'MIGRATED_ASSIGNMENT', l."AssignComment",
                       COALESCE(l."AssignOn", l."ModifiedDate", l."CreatedDate", now()),
                       NULL, NULL, 'commercial-routing-backfill',
                       'migration:lead:' || l."ID"::text || ':assignment:v1'
                FROM "Leads" l
                JOIN lead_routing_decisions d
                  ON d."BusinessUnitId" = l."BusinessUnitID"
                 AND d."IdempotencyKey" = 'migration:lead:' || l."ID"::text || ':assignment:v1'
                WHERE l."AssignTo" IS NOT NULL
                  AND NOT EXISTS (
                      SELECT 1 FROM lead_assignments a
                      WHERE a."BusinessUnitId" = l."BusinessUnitID"
                        AND a."LeadId" = l."ID" AND a."EffectiveTo" IS NULL);

                INSERT INTO lead_routing_decisions
                    ("BusinessUnitId", "LeadId", "CustomerId", "MatchedIdentifierId", "OwnershipId",
                     "SuggestedUserId", "SelectedUserId", "MatchStatus", "Outcome", "MatchConfidence",
                     "DecisionCode", "Explanation", "PolicyVersion", "CorrelationId", "IdempotencyKey", "CreatedOn")
                SELECT l."BusinessUnitID", l."ID", NULL, NULL, NULL,
                       NULL, NULL, 'NoEvidence', 'Unassigned', 0,
                       'MIGRATED_UNASSIGNED', '{"source":"migration","reason":"accepted lead without owner"}'::jsonb,
                       '1', 'commercial-routing-backfill',
                       'migration:lead:' || l."ID"::text || ':unassigned:v1',
                       COALESCE(l."ModifiedDate", l."CreatedDate", now())
                FROM "Leads" l
                JOIN "Setup_Master" status ON status."SetupID" = l."LeadStatusId"
                WHERE l."AssignTo" IS NULL
                  AND lower(status."SetupValue") LIKE '%accept%'
                  AND NOT EXISTS (
                      SELECT 1 FROM lead_routing_decisions d
                      WHERE d."BusinessUnitId" = l."BusinessUnitID"
                        AND d."IdempotencyKey" = 'migration:lead:' || l."ID"::text || ':unassigned:v1');

                INSERT INTO unassigned_work_items
                    ("BusinessUnitId", "LeadId", "RoutingDecisionId", "QueueType", "ReasonCode", "Status",
                     "Priority", "EnteredOn", "SlaDueOn", "SuggestedCustomerId", "SuggestedUserId",
                     "MatchConfidence", "RequiredAction", "ClaimedByUserId", "ClaimedUntil", "ResolvedOn",
                     "ResolutionCode", "IdempotencyKey", "Version")
                SELECT l."BusinessUnitID", l."ID", d."Id", 'Unassigned', 'MIGRATED_UNASSIGNED', 'Open',
                       0, d."CreatedOn", d."CreatedOn" + interval '4 hours', NULL, NULL,
                       0, 'Confirm customer and assign an eligible owner', NULL, NULL, NULL,
                       NULL, 'migration:lead:' || l."ID"::text || ':unassigned:v1', 1
                FROM "Leads" l
                JOIN lead_routing_decisions d
                  ON d."BusinessUnitId" = l."BusinessUnitID"
                 AND d."IdempotencyKey" = 'migration:lead:' || l."ID"::text || ':unassigned:v1'
                WHERE NOT EXISTS (
                    SELECT 1 FROM unassigned_work_items w
                    WHERE w."BusinessUnitId" = l."BusinessUnitID" AND w."LeadId" = l."ID"
                      AND w."Status" IN ('Open', 'Claimed'));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM unassigned_work_items
                WHERE "IdempotencyKey" LIKE 'migration:lead:%:unassigned:v1';
                DELETE FROM lead_assignments
                WHERE "IdempotencyKey" LIKE 'migration:lead:%:assignment:v1';
                DELETE FROM lead_routing_decisions
                WHERE "IdempotencyKey" LIKE 'migration:lead:%:assignment:v1'
                   OR "IdempotencyKey" LIKE 'migration:lead:%:unassigned:v1';
                """);
        }
    }
}
