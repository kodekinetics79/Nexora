using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <summary>
    /// Gate 3 added two governed customer-PO actions — cancellation, and explicit acceptance of a
    /// blocking difference — and `nexora_write_otc_audit` admits neither. The function carries a
    /// hand-written whitelist of `(command_type, previous_state, new_state)` triples and raises
    /// `23514` on anything outside it, so both new commands would have been refused by the database
    /// after the application had already decided to allow them.
    ///
    /// <para><b>This is invisible to the portable lane by construction.</b> `AddAuditAsync` only
    /// calls the function on Npgsql, so SQLite never executes the whitelist at all. The first real
    /// cancellation in production would have taken a `23514` and rolled back a transaction the user
    /// had every reason to believe succeeded. Deploy this ahead of the code, for the same reason
    /// `AI_NOT_AUTHORIZED` and the delivery-shortfall CHECK were deployed ahead of theirs.</para>
    ///
    /// <para><b>The two branches are wider than the ones originally proposed, deliberately.</b> The
    /// proposal was `previous_state IN ('DRAFT','CONFIRMED')` for the cancellation. That is not what
    /// the code does: `CancelPurchaseOrderAsync` stamps `previousState = purchaseOrder.Status` and
    /// refuses on exactly two conditions — the PO is already `CANCELLED`, or a non-cancelled award
    /// still exists against it. A PO sitting at `PARTIALLY_AWARDED` or `FULLY_AWARDED` whose awards
    /// have all since been cancelled passes both guards and is genuinely cancellable, so the narrow
    /// whitelist would have reproduced the very defect this migration exists to close — on
    /// PostgreSQL only, on a path no portable test can reach. The rule encoded here mirrors the
    /// application's own rule instead of enumerating a guess: anything but `CANCELLED` may become
    /// `CANCELLED`.</para>
    ///
    /// <para>The acceptance branch is a **state-preserving** audit row: `previous_state` equals
    /// `new_state`, because accepting a difference records a decision without moving the aggregate.
    /// It is written against the purchase order rather than the award on purpose —
    /// `nexora_otc_award_transition_guard` makes a CONFIRMED award immutable except toward ORDERED
    /// or CANCELLED, and punching a hole in that guard to record an acceptance would trade a real
    /// control for a bookkeeping convenience. `CANCELLED` is excluded because a cancelled PO can
    /// have no live award to accept a difference against.</para>
    ///
    /// <para>No column, index, CHECK, RLS policy or GRANT changes, and there is no data to backfill.
    /// `CK_CustomerPurchaseOrders_Status` already permits `CANCELLED` and
    /// `CK_CustomerPurchaseOrders_Cancellation` already requires the reason. The body below is the
    /// original from `20260723190000_AddCustomerAwards` reproduced verbatim, with only the two
    /// branches added — the outer checks that the audit's tenant, version and state match the
    /// committed aggregate are untouched, and still bind both new commands.</para>
    /// </summary>
    /// <inheritdoc />
    public partial class OrderToCashAuditAdmitsPoCancellationAndDifferenceAcceptance : Migration
    {
        private const string WhitelistWithNewCommands = """
            (command_type = 'CREATE_PURCHASE_ORDER' AND aggregate_type = 'CUSTOMER_PURCHASE_ORDER'
                AND aggregate_version = 1 AND previous_state IS NULL) OR
            (command_type = 'CREATE_AWARD' AND aggregate_type = 'CUSTOMER_AWARD'
                AND aggregate_version = 1 AND previous_state IS NULL AND new_state = 'DRAFT') OR
            (command_type = 'CONFIRM_AWARD' AND previous_state = 'DRAFT' AND new_state = 'CONFIRMED') OR
            (command_type = 'CANCEL_AWARD' AND previous_state IN ('DRAFT','CONFIRMED') AND new_state = 'CANCELLED') OR
            (command_type = 'CONVERT_AWARD_TO_ORDER' AND previous_state = 'CONFIRMED' AND new_state = 'ORDERED') OR
            (command_type = 'CANCEL_PURCHASE_ORDER' AND aggregate_type = 'CUSTOMER_PURCHASE_ORDER'
                AND previous_state IS NOT NULL AND previous_state <> 'CANCELLED' AND new_state = 'CANCELLED') OR
            (command_type = 'ACCEPT_PO_DIFFERENCES' AND aggregate_type = 'CUSTOMER_PURCHASE_ORDER'
                AND previous_state = new_state AND new_state <> 'CANCELLED')
            """;

        private const string WhitelistOriginal = """
            (command_type = 'CREATE_PURCHASE_ORDER' AND aggregate_type = 'CUSTOMER_PURCHASE_ORDER'
                AND aggregate_version = 1 AND previous_state IS NULL) OR
            (command_type = 'CREATE_AWARD' AND aggregate_type = 'CUSTOMER_AWARD'
                AND aggregate_version = 1 AND previous_state IS NULL AND new_state = 'DRAFT') OR
            (command_type = 'CONFIRM_AWARD' AND previous_state = 'DRAFT' AND new_state = 'CONFIRMED') OR
            (command_type = 'CANCEL_AWARD' AND previous_state IN ('DRAFT','CONFIRMED') AND new_state = 'CANCELLED') OR
            (command_type = 'CONVERT_AWARD_TO_ORDER' AND previous_state = 'CONFIRMED' AND new_state = 'ORDERED')
            """;

        private static string Function(string whitelist) => $$"""
            CREATE OR REPLACE FUNCTION public.nexora_write_otc_audit(
                business_unit_id bigint, aggregate_type text, aggregate_id bigint,
                aggregate_version bigint, command_type text, previous_state text, new_state text,
                actor text, reason text, request_hash text, idempotency_key text,
                result_json jsonb, correlation_id text, occurred_on timestamp without time zone)
            RETURNS void LANGUAGE plpgsql SECURITY DEFINER
            SET search_path = pg_catalog, public AS $function$
            DECLARE stored_version bigint;
            DECLARE stored_state text;
            BEGIN
                IF current_setting('role', true) = 'nexora_tenant_app' AND business_unit_id IS DISTINCT FROM
                   NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint THEN
                    RAISE EXCEPTION 'audit tenant does not match the active tenant' USING ERRCODE = '42501';
                END IF;
                IF aggregate_type = 'CUSTOMER_PURCHASE_ORDER' THEN
                    SELECT p."Version", p."Status" INTO stored_version, stored_state
                    FROM public."CustomerPurchaseOrders" p
                    WHERE p."BusinessUnitId" = business_unit_id AND p."Id" = aggregate_id;
                ELSIF aggregate_type = 'CUSTOMER_AWARD' THEN
                    SELECT a."Version", a."Status" INTO stored_version, stored_state
                    FROM public."CustomerAwards" a
                    WHERE a."BusinessUnitId" = business_unit_id AND a."Id" = aggregate_id;
                ELSE
                    RAISE EXCEPTION 'unsupported order-to-cash audit aggregate' USING ERRCODE = '23514';
                END IF;
                IF NOT FOUND OR stored_version <> aggregate_version OR stored_state <> new_state THEN
                    RAISE EXCEPTION 'audit does not match committed aggregate state' USING ERRCODE = '23514';
                END IF;
                IF NOT (
                    {{whitelist}}) THEN
                    RAISE EXCEPTION 'invalid order-to-cash audit transition' USING ERRCODE = '23514';
                END IF;
                IF request_hash !~ '^[0-9a-f]{64}$' OR btrim(idempotency_key) = ''
                   OR btrim(actor) = '' OR btrim(correlation_id) = ''
                   OR jsonb_typeof(result_json) <> 'object' THEN
                    RAISE EXCEPTION 'invalid order-to-cash audit evidence' USING ERRCODE = '23514';
                END IF;
                INSERT INTO public."OrderToCashAuditEvents"
                    ("BusinessUnitId", "AggregateType", "AggregateId", "AggregateVersion", "CommandType",
                     "PreviousState", "NewState", "Actor", "Reason", "RequestHash", "IdempotencyKey",
                     "ResultJson", "CorrelationId", "OccurredOn")
                VALUES (business_unit_id, aggregate_type, aggregate_id, aggregate_version, command_type,
                    previous_state, new_state, actor, reason, request_hash, idempotency_key,
                    result_json, correlation_id, occurred_on);
            END
            $function$;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
            => migrationBuilder.Sql(Function(WhitelistWithNewCommands));

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
            => migrationBuilder.Sql(Function(WhitelistOriginal));
    }
}
