using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class CoreCommercialSalesFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "commercial_activities",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    SalesRepUserId = table.Column<long>(type: "bigint", nullable: false),
                    ActivityType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    AggregateType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    AggregateId = table.Column<long>(type: "bigint", nullable: false),
                    CustomerId = table.Column<long>(type: "bigint", nullable: true),
                    LeadAssignmentId = table.Column<long>(type: "bigint", nullable: true),
                    OccurredAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    OutcomeCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    EvidenceReference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ActorId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_commercial_activities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_commercial_activities_Users_SalesRepUserId",
                        column: x => x.SalesRepUserId,
                        principalTable: "Users",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "follow_up_tasks",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    AssignedToUserId = table.Column<long>(type: "bigint", nullable: false),
                    AggregateType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    AggregateId = table.Column<long>(type: "bigint", nullable: false),
                    CustomerId = table.Column<long>(type: "bigint", nullable: true),
                    DueAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    PurposeCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreationIdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_follow_up_tasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_follow_up_tasks_Users_AssignedToUserId",
                        column: x => x.AssignedToUserId,
                        principalTable: "Users",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sales_contributions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    SalesRepUserId = table.Column<long>(type: "bigint", nullable: false),
                    ContributionType = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    AggregateType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    AggregateId = table.Column<long>(type: "bigint", nullable: false),
                    CustomerId = table.Column<long>(type: "bigint", nullable: true),
                    ContributionPercent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    RevenueAmount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    CurrencyCode = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    RecognizedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    EvidenceReference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ActorId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sales_contributions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sales_contributions_Users_SalesRepUserId",
                        column: x => x.SalesRepUserId,
                        principalTable: "Users",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sales_rep_profiles",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    IsRoutingEligible = table.Column<bool>(type: "boolean", nullable: false),
                    CapacityPercent = table.Column<int>(type: "integer", nullable: false),
                    DistributionWeight = table.Column<decimal>(type: "numeric(10,4)", precision: 10, scale: 4, nullable: false),
                    TerritoryKeys = table.Column<string[]>(type: "text[]", nullable: false),
                    ProductCategoryKeys = table.Column<string[]>(type: "text[]", nullable: false),
                    EffectiveFromUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    EffectiveToUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    LastMutationIdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sales_rep_profiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sales_rep_profiles_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sales_team_memberships",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    TeamId = table.Column<long>(type: "bigint", nullable: false),
                    IsPrimary = table.Column<bool>(type: "boolean", nullable: false),
                    EffectiveFromUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    EffectiveToUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sales_team_memberships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sales_team_memberships_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "follow_up_transition_events",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    FollowUpTaskId = table.Column<long>(type: "bigint", nullable: false),
                    FromStatus = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    ToStatus = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    FromVersion = table.Column<long>(type: "bigint", nullable: false),
                    ToVersion = table.Column<long>(type: "bigint", nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ActorId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_follow_up_transition_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_follow_up_transition_events_follow_up_tasks_FollowUpTaskId",
                        column: x => x.FollowUpTaskId,
                        principalTable: "follow_up_tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_commercial_activities_BusinessUnitId_IdempotencyKey",
                table: "commercial_activities",
                columns: new[] { "BusinessUnitId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_commercial_activities_BusinessUnitId_SalesRepUserId_Occurre~",
                table: "commercial_activities",
                columns: new[] { "BusinessUnitId", "SalesRepUserId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_commercial_activities_SalesRepUserId",
                table: "commercial_activities",
                column: "SalesRepUserId");

            migrationBuilder.CreateIndex(
                name: "IX_follow_up_tasks_AssignedToUserId",
                table: "follow_up_tasks",
                column: "AssignedToUserId");

            migrationBuilder.CreateIndex(
                name: "IX_follow_up_tasks_BusinessUnitId_AssignedToUserId_DueAtUtc",
                table: "follow_up_tasks",
                columns: new[] { "BusinessUnitId", "AssignedToUserId", "DueAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_follow_up_tasks_BusinessUnitId_CreationIdempotencyKey",
                table: "follow_up_tasks",
                columns: new[] { "BusinessUnitId", "CreationIdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_follow_up_transition_events_BusinessUnitId_IdempotencyKey",
                table: "follow_up_transition_events",
                columns: new[] { "BusinessUnitId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_follow_up_transition_events_FollowUpTaskId",
                table: "follow_up_transition_events",
                column: "FollowUpTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_sales_contributions_BusinessUnitId_IdempotencyKey",
                table: "sales_contributions",
                columns: new[] { "BusinessUnitId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sales_contributions_BusinessUnitId_SalesRepUserId_Recognize~",
                table: "sales_contributions",
                columns: new[] { "BusinessUnitId", "SalesRepUserId", "RecognizedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_contributions_SalesRepUserId",
                table: "sales_contributions",
                column: "SalesRepUserId");

            migrationBuilder.CreateIndex(
                name: "IX_sales_rep_profiles_BusinessUnitId_LastMutationIdempotencyKey",
                table: "sales_rep_profiles",
                columns: new[] { "BusinessUnitId", "LastMutationIdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sales_rep_profiles_BusinessUnitId_UserId",
                table: "sales_rep_profiles",
                columns: new[] { "BusinessUnitId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sales_rep_profiles_UserId",
                table: "sales_rep_profiles",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_sales_team_memberships_BusinessUnitId_UserId_TeamId_Effecti~",
                table: "sales_team_memberships",
                columns: new[] { "BusinessUnitId", "UserId", "TeamId", "EffectiveToUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_team_memberships_UserId",
                table: "sales_team_memberships",
                column: "UserId");

            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX IF NOT EXISTS "UX_Users_BUID_ID"
                    ON public."Users" ("BUID", "ID");
                CREATE UNIQUE INDEX IF NOT EXISTS "UX_Teams_BusinessUnitID_ID"
                    ON public."Teams" ("BusinessUnitID", "ID");
                CREATE UNIQUE INDEX IF NOT EXISTS "UX_lead_assignments_BusinessUnitId_Id"
                    ON public.lead_assignments ("BusinessUnitId", "Id");
                CREATE UNIQUE INDEX IF NOT EXISTS "UX_follow_up_tasks_BusinessUnitId_Id"
                    ON public.follow_up_tasks ("BusinessUnitId", "Id");

                ALTER TABLE public.commercial_activities
                    DROP CONSTRAINT IF EXISTS "FK_commercial_activities_Users_SalesRepUserId";
                ALTER TABLE public.follow_up_tasks
                    DROP CONSTRAINT IF EXISTS "FK_follow_up_tasks_Users_AssignedToUserId";
                ALTER TABLE public.sales_contributions
                    DROP CONSTRAINT IF EXISTS "FK_sales_contributions_Users_SalesRepUserId";
                ALTER TABLE public.sales_rep_profiles
                    DROP CONSTRAINT IF EXISTS "FK_sales_rep_profiles_Users_UserId";
                ALTER TABLE public.sales_team_memberships
                    DROP CONSTRAINT IF EXISTS "FK_sales_team_memberships_Users_UserId";
                ALTER TABLE public.follow_up_transition_events
                    DROP CONSTRAINT IF EXISTS "FK_follow_up_transition_events_follow_up_tasks_FollowUpTaskId";

                ALTER TABLE public.commercial_activities
                    ADD CONSTRAINT "FK_sales_activity_tenant_user"
                    FOREIGN KEY ("BusinessUnitId", "SalesRepUserId")
                    REFERENCES public."Users" ("BUID", "ID") ON DELETE RESTRICT,
                    ADD CONSTRAINT "FK_sales_activity_tenant_customer"
                    FOREIGN KEY ("BusinessUnitId", "CustomerId")
                    REFERENCES public."Customers" ("BUID", "ID") ON DELETE RESTRICT,
                    ADD CONSTRAINT "FK_sales_activity_tenant_assignment"
                    FOREIGN KEY ("BusinessUnitId", "LeadAssignmentId")
                    REFERENCES public.lead_assignments ("BusinessUnitId", "Id") ON DELETE RESTRICT;

                ALTER TABLE public.follow_up_tasks
                    ADD CONSTRAINT "FK_follow_up_tenant_user"
                    FOREIGN KEY ("BusinessUnitId", "AssignedToUserId")
                    REFERENCES public."Users" ("BUID", "ID") ON DELETE RESTRICT,
                    ADD CONSTRAINT "FK_follow_up_tenant_customer"
                    FOREIGN KEY ("BusinessUnitId", "CustomerId")
                    REFERENCES public."Customers" ("BUID", "ID") ON DELETE RESTRICT;

                ALTER TABLE public.follow_up_transition_events
                    ADD CONSTRAINT "FK_follow_up_event_tenant_task"
                    FOREIGN KEY ("BusinessUnitId", "FollowUpTaskId")
                    REFERENCES public.follow_up_tasks ("BusinessUnitId", "Id") ON DELETE RESTRICT;

                ALTER TABLE public.sales_contributions
                    ADD CONSTRAINT "FK_sales_contribution_tenant_user"
                    FOREIGN KEY ("BusinessUnitId", "SalesRepUserId")
                    REFERENCES public."Users" ("BUID", "ID") ON DELETE RESTRICT,
                    ADD CONSTRAINT "FK_sales_contribution_tenant_customer"
                    FOREIGN KEY ("BusinessUnitId", "CustomerId")
                    REFERENCES public."Customers" ("BUID", "ID") ON DELETE RESTRICT;

                ALTER TABLE public.sales_rep_profiles
                    ADD CONSTRAINT "FK_sales_profile_tenant_user"
                    FOREIGN KEY ("BusinessUnitId", "UserId")
                    REFERENCES public."Users" ("BUID", "ID") ON DELETE RESTRICT;

                ALTER TABLE public.sales_team_memberships
                    ADD CONSTRAINT "FK_sales_membership_tenant_user"
                    FOREIGN KEY ("BusinessUnitId", "UserId")
                    REFERENCES public."Users" ("BUID", "ID") ON DELETE RESTRICT,
                    ADD CONSTRAINT "FK_sales_membership_tenant_team"
                    FOREIGN KEY ("BusinessUnitId", "TeamId")
                    REFERENCES public."Teams" ("BusinessUnitID", "ID") ON DELETE RESTRICT;

                ALTER TABLE public.customer_ownerships
                    DROP CONSTRAINT IF EXISTS "FK_customer_ownerships_Customers_CustomerId",
                    DROP CONSTRAINT IF EXISTS "FK_customer_ownerships_Users_PrimaryUserId",
                    DROP CONSTRAINT IF EXISTS "FK_customer_ownerships_Users_BackupUserId";
                ALTER TABLE public.customer_ownerships
                    ADD CONSTRAINT "FK_customer_owner_tenant_customer"
                    FOREIGN KEY ("BusinessUnitId", "CustomerId")
                    REFERENCES public."Customers" ("BUID", "ID") ON DELETE RESTRICT,
                    ADD CONSTRAINT "FK_customer_owner_tenant_primary_user"
                    FOREIGN KEY ("BusinessUnitId", "PrimaryUserId")
                    REFERENCES public."Users" ("BUID", "ID") ON DELETE RESTRICT,
                    ADD CONSTRAINT "FK_customer_owner_tenant_backup_user"
                    FOREIGN KEY ("BusinessUnitId", "BackupUserId")
                    REFERENCES public."Users" ("BUID", "ID") ON DELETE RESTRICT;

                DO $ownership_guard$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM public.customer_ownerships
                        WHERE "IsActive" = TRUE AND "EffectiveTo" IS NULL
                        GROUP BY "BusinessUnitId", "CustomerId", "Scope", COALESCE("ScopeKey", '')
                        HAVING count(*) > 1
                    ) THEN
                        RAISE EXCEPTION 'duplicate active customer ownerships must be resolved before upgrade';
                    END IF;
                END $ownership_guard$;

                CREATE UNIQUE INDEX "UX_customer_ownerships_single_active"
                    ON public.customer_ownerships
                    ("BusinessUnitId", "CustomerId", "Scope", COALESCE("ScopeKey", ''))
                    WHERE "IsActive" = TRUE AND "EffectiveTo" IS NULL;
                """);

            migrationBuilder.Sql("""
                DO $govern$
                DECLARE governed_table text;
                        sales_sequence text;
                BEGIN
                    FOREACH governed_table IN ARRAY ARRAY[
                        'commercial_activities', 'follow_up_tasks', 'follow_up_transition_events',
                        'sales_contributions', 'sales_rep_profiles', 'sales_team_memberships'
                    ] LOOP
                        EXECUTE format('ALTER TABLE public.%I ENABLE ROW LEVEL SECURITY', governed_table);
                        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_tenant_app') THEN
                            EXECUTE format('DROP POLICY IF EXISTS nexora_tenant_isolation ON public.%I', governed_table);
                            EXECUTE format(
                                'CREATE POLICY nexora_tenant_isolation ON public.%I TO nexora_tenant_app USING ("BusinessUnitId" = NULLIF(current_setting(''nexora.business_unit_id'', true), '''')::bigint) WITH CHECK ("BusinessUnitId" = NULLIF(current_setting(''nexora.business_unit_id'', true), '''')::bigint)',
                                governed_table);
                            EXECUTE format('GRANT SELECT, INSERT, UPDATE ON public.%I TO nexora_tenant_app', governed_table);
                        END IF;
                    END LOOP;

                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_tenant_app') THEN
                        FOREACH governed_table IN ARRAY ARRAY[
                            'commercial_activities', 'follow_up_tasks', 'follow_up_transition_events',
                            'sales_contributions', 'sales_rep_profiles', 'sales_team_memberships'
                        ] LOOP
                            sales_sequence := pg_get_serial_sequence(format('public.%I', governed_table), 'Id');
                            IF sales_sequence IS NULL THEN
                                RAISE EXCEPTION 'sales identity sequence missing for %', governed_table;
                            END IF;
                            EXECUTE format('REVOKE ALL PRIVILEGES ON SEQUENCE %s FROM nexora_tenant_app', sales_sequence);
                            EXECUTE format('GRANT USAGE ON SEQUENCE %s TO nexora_tenant_app', sales_sequence);
                        END LOOP;
                    END IF;
                END $govern$;

                CREATE OR REPLACE FUNCTION public.nexora_reject_sales_event_mutation()
                RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                    RAISE EXCEPTION 'commercial event history is append-only';
                END $fn$;

                CREATE TRIGGER commercial_activities_immutable
                    BEFORE UPDATE OR DELETE ON public.commercial_activities
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_sales_event_mutation();
                CREATE TRIGGER follow_up_transition_events_immutable
                    BEFORE UPDATE OR DELETE ON public.follow_up_transition_events
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_sales_event_mutation();
                CREATE TRIGGER sales_contributions_immutable
                    BEFORE UPDATE OR DELETE ON public.sales_contributions
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_reject_sales_event_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS public."UX_customer_ownerships_single_active";
                ALTER TABLE public.customer_ownerships
                    DROP CONSTRAINT IF EXISTS "FK_customer_owner_tenant_customer",
                    DROP CONSTRAINT IF EXISTS "FK_customer_owner_tenant_primary_user",
                    DROP CONSTRAINT IF EXISTS "FK_customer_owner_tenant_backup_user";
                ALTER TABLE public.customer_ownerships
                    ADD CONSTRAINT "FK_customer_ownerships_Customers_CustomerId"
                    FOREIGN KEY ("CustomerId") REFERENCES public."Customers" ("ID") ON DELETE RESTRICT,
                    ADD CONSTRAINT "FK_customer_ownerships_Users_PrimaryUserId"
                    FOREIGN KEY ("PrimaryUserId") REFERENCES public."Users" ("ID") ON DELETE RESTRICT,
                    ADD CONSTRAINT "FK_customer_ownerships_Users_BackupUserId"
                    FOREIGN KEY ("BackupUserId") REFERENCES public."Users" ("ID") ON DELETE RESTRICT;

                DROP TRIGGER IF EXISTS commercial_activities_immutable ON public.commercial_activities;
                DROP TRIGGER IF EXISTS follow_up_transition_events_immutable ON public.follow_up_transition_events;
                DROP TRIGGER IF EXISTS sales_contributions_immutable ON public.sales_contributions;
                DROP FUNCTION IF EXISTS public.nexora_reject_sales_event_mutation();
                """);
            migrationBuilder.DropTable(
                name: "commercial_activities");

            migrationBuilder.DropTable(
                name: "follow_up_transition_events");

            migrationBuilder.DropTable(
                name: "sales_contributions");

            migrationBuilder.DropTable(
                name: "sales_rep_profiles");

            migrationBuilder.DropTable(
                name: "sales_team_memberships");

            migrationBuilder.DropTable(
                name: "follow_up_tasks");

            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS public."UX_Users_BUID_ID";
                DROP INDEX IF EXISTS public."UX_Teams_BusinessUnitID_ID";
                DROP INDEX IF EXISTS public."UX_lead_assignments_BusinessUnitId_Id";
                """);
        }
    }
}
